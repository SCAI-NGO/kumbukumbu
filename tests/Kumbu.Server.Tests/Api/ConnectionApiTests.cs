// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>The connections REST API: for a signed-in session only, vaults by name, each with its connector URL.</summary>
[Collection("app")]
public sealed class ConnectionApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static Task<JsonNode> JsonAsync(HttpResponseMessage response) => RestClient.JsonAsync(response);

    private static Dictionary<string, string> Vaults(string vault, string right) => new() { [vault] = right };

    [Fact]
    public async Task Connections_are_created_listed_given_new_rights_and_revoked()
    {
        var user = await RestClient.NewAsync(Api);
        using var second = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var created = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "cor.energy", vaults = Vaults(user.Vault, "write"), expiresInDays = 90 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await JsonAsync(created);
        var id = json["id"]!.GetValue<string>();
        Assert.Matches("^[a-z2-7]{16}$", id);
        Assert.Equal($"/api/connections/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal("cor.energy", json["name"]!.GetValue<string>());
        Assert.Equal(KumbuAppFactory.PublicUrl + "/mcp/c/" + id, json["url"]!.GetValue<string>());
        Assert.Equal("write", json["vaults"]![user.Vault]!.GetValue<string>());
        Assert.Equal("waiting", json["state"]!.GetValue<string>());
        Assert.Null(json["reason"]?.GetValue<string>());
        Assert.Null(json["client"]);
        Assert.InRange(json["expiresAt"]!.GetValue<DateTime>().ToUniversalTime(), DateTime.UtcNow.AddDays(90).AddMinutes(-1), DateTime.UtcNow.AddDays(90));

        using var list = await user.GetAsync("/api/connections");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listed = Assert.Single((await JsonAsync(list)).AsArray())!;
        Assert.Equal(id, listed["id"]!.GetValue<string>());
        Assert.Equal(KumbuAppFactory.PublicUrl + "/mcp/c/" + id, listed["url"]!.GetValue<string>());

        using var rights = await user.SendAsync(
            HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = new Dictionary<string, string> { [user.Vault] = "read", ["Privat"] = "write" } });
        Assert.Equal(HttpStatusCode.OK, rights.StatusCode);
        var changed = await JsonAsync(rights);
        Assert.Equal("read", changed["vaults"]![user.Vault]!.GetValue<string>());
        Assert.Equal("write", changed["vaults"]!["Privat"]!.GetValue<string>());
        Assert.Equal("waiting", changed["state"]!.GetValue<string>());

        using var revoke = await user.SendAsync(HttpMethod.Delete, $"/api/connections/{id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var again = await user.SendAsync(HttpMethod.Delete, $"/api/connections/{id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        using var late = await user.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = Vaults(user.Vault, "read") });
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);

        using var after = await user.GetAsync("/api/connections");
        var revoked = Assert.Single((await JsonAsync(after)).AsArray())!;
        Assert.Equal("revoked", revoked["state"]!.GetValue<string>());
        Assert.Equal("revoked", revoked["reason"]!.GetValue<string>());
        Assert.NotNull(revoked["revokedAt"]?.GetValue<DateTime>());
    }

    [Fact]
    public async Task Vaults_are_given_by_name_and_refusals_are_400_or_409()
    {
        var user = await RestClient.NewAsync(Api);
        await user.KeyAsync("laptop", KeyRight.Read);

        using var unknown = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "phone", vaults = Vaults("Fremd", "read") });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("You have no vault named 'Fremd'.", await RestClient.ErrorAsync(unknown));
        using var badRight = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "phone", vaults = Vaults(user.Vault, "admin") });
        Assert.Equal(HttpStatusCode.BadRequest, badRight.StatusCode);
        using var none = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "phone", vaults = new Dictionary<string, string>() });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        using var badExpiry = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "phone", vaults = Vaults(user.Vault, "read"), expiresInDays = 7 });
        Assert.Equal(HttpStatusCode.BadRequest, badExpiry.StatusCode);
        using var reserved = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "dev", vaults = Vaults(user.Vault, "read") });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        using var taken = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "Laptop", vaults = Vaults(user.Vault, "read") });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("You already have an API key or connection named 'Laptop'.", await RestClient.ErrorAsync(taken));

        using var created = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "phone", vaults = Vaults(user.Vault, "read") });
        var id = (await JsonAsync(created))["id"]!.GetValue<string>();
        using var unknownOnChange = await user.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = Vaults("Fremd", "read") });
        Assert.Equal(HttpStatusCode.BadRequest, unknownOnChange.StatusCode);
        using var noSuch = await user.SendAsync(HttpMethod.Put, "/api/connections/aaaaaaaaaaaaaaaa/vaults", new { vaults = Vaults(user.Vault, "read") });
        Assert.Equal(HttpStatusCode.NotFound, noSuch.StatusCode);
    }

    [Fact]
    public async Task Another_users_connection_is_404()
    {
        var owner = await RestClient.NewAsync(Api);
        var other = await RestClient.NewAsync(Api);
        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "phone", vaults = Vaults(owner.Vault, "read") });
        var id = (await JsonAsync(created))["id"]!.GetValue<string>();

        using var revoke = await other.SendAsync(HttpMethod.Delete, $"/api/connections/{id}");
        using var rights = await other.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = Vaults(other.Vault, "write") });
        using var list = await other.GetAsync("/api/connections");

        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rights.StatusCode);
        Assert.Empty((await JsonAsync(list)).AsArray());
    }

    [Fact]
    public async Task An_api_key_gets_403_on_every_connection_endpoint()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("agent", KeyRight.Write);
        using var created = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "phone", vaults = Vaults(user.Vault, "read") });
        var id = (await JsonAsync(created))["id"]!.GetValue<string>();

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "/api/connections", null),
                     (HttpMethod.Post, "/api/connections", new { name = "stolen", vaults = Vaults(user.Vault, "write") }),
                     (HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = Vaults(user.Vault, "write") }),
                     (HttpMethod.Delete, $"/api/connections/{id}", null),
                 })
        {
            using var response = await user.SendAsync(method, path, body, bearer: key);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var list = await user.GetAsync("/api/connections");
        var listed = Assert.Single((await JsonAsync(list)).AsArray())!;
        Assert.Equal("waiting", listed["state"]!.GetValue<string>());
        Assert.Equal("read", listed["vaults"]![user.Vault]!.GetValue<string>());
    }

    [Fact]
    public async Task The_openapi_document_lists_the_connection_endpoints()
    {
        using var http = Api.CreateClient();
        using var response = await http.GetAsync("/api/openapi.json", Ct);

        var paths = (await JsonAsync(response))["paths"]!.AsObject();
        Assert.NotNull(paths["/api/connections"]?["get"]);
        Assert.NotNull(paths["/api/connections"]?["post"]);
        Assert.NotNull(paths["/api/connections/{id}/vaults"]?["put"]);
        Assert.NotNull(paths["/api/connections/{id}"]?["delete"]);
    }
}
