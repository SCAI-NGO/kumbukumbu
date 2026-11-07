// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>/api/dav-shares</c>: the WebDAV tab's list, creation (with the password window) and revocation.</summary>
[Collection("app")]
public sealed class DavShareApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program> Timed() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    private static object WorkPc => new { label = "Work PC", vaults = new Dictionary<string, string> { ["Work"] = "write" } };

    private static async Task<string> ListTextAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/dav-shares");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    [Fact]
    public async Task Create_needs_the_password_window()
    {
        await using var api = Timed();
        var alice = await RestClient.NewAsync(api, vault: "Work", remember: true);
        _time.Advance(TimeSpan.FromMinutes(31));

        using (var refused = await alice.SendAsync(HttpMethod.Post, "/api/dav-shares", WorkPc))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("password-required", (await RestClient.JsonAsync(refused))["type"]?.GetValue<string>());
        }

        Assert.Empty((await RestClient.JsonAsync(await alice.GetAsync("/api/dav-shares")))["shares"]!.AsArray());

        using (var confirmed = await alice.SendAsync(HttpMethod.Post, "/api/session/confirm", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        using var created = await alice.SendAsync(HttpMethod.Post, "/api/dav-shares", WorkPc);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Create_answers_the_password_once_and_the_list_never_contains_it()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");

        using var created = await alice.SendAsync(HttpMethod.Post, "/api/dav-shares", new { label = "Work PC", vaults = new Dictionary<string, string> { ["Work"] = "write" }, expiresInDays = 90 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await RestClient.JsonAsync(created);
        var password = body["password"]!.GetValue<string>();
        var share = body["share"]!;
        var id = share["id"]!.GetValue<string>();
        Assert.Equal($"/api/dav-shares/{id}", created.Headers.Location?.OriginalString);
        Assert.Equal($"{alice.Username}.{id}", share["username"]!.GetValue<string>());
        Assert.Equal("Work PC", share["label"]!.GetValue<string>());
        Assert.Equal("write", share["vaults"]!["Work"]!.GetValue<string>());
        Assert.NotNull(share["expiresAt"]);
        Assert.Null(share["password"]);

        var listed = await ListTextAsync(alice);
        // No assertion message: it would print the password.
        Assert.False(listed.Contains(password, StringComparison.OrdinalIgnoreCase));
        Assert.False(listed.Contains(DavTokens.Normalise(password)!, StringComparison.OrdinalIgnoreCase));
        var list = await RestClient.JsonAsync(await alice.GetAsync("/api/dav-shares"));
        Assert.True(list["enabled"]!.GetValue<bool>());
        var only = Assert.Single(list["shares"]!.AsArray())!;
        Assert.Equal(id, only["id"]!.GetValue<string>());
        Assert.Equal($"{alice.Username}.{id}", only["username"]!.GetValue<string>());
        Assert.Empty(only["lostVaults"]!.AsArray());
    }

    [Fact]
    public async Task Delete_answers_204_then_404()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        using var created = await alice.SendAsync(HttpMethod.Post, "/api/dav-shares", WorkPc);
        var id = (await RestClient.JsonAsync(created))["share"]!["id"]!.GetValue<string>();
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Work");

        using (var other = await bob.SendAsync(HttpMethod.Delete, $"/api/dav-shares/{id}")) Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        using (var revoked = await alice.SendAsync(HttpMethod.Delete, $"/api/dav-shares/{id}")) Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using (var again = await alice.SendAsync(HttpMethod.Delete, $"/api/dav-shares/{id}")) Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var only = Assert.Single((await RestClient.JsonAsync(await alice.GetAsync("/api/dav-shares")))["shares"]!.AsArray())!;
        Assert.NotNull(only["revokedAt"]);
    }

    [Fact]
    public async Task An_unusable_request_is_400_and_a_taken_label_409()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");

        foreach (var request in new object[]
                 {
                     new { label = "PC", vaults = new Dictionary<string, string> { ["Work"] = "admin" } },
                     new { label = "PC", vaults = new Dictionary<string, string> { ["Elsewhere"] = "read" } },
                     new { label = "PC", vaults = new Dictionary<string, string>() },
                     new { label = "", vaults = new Dictionary<string, string> { ["Work"] = "read" } },
                     new { label = "PC", vaults = new Dictionary<string, string> { ["Work"] = "read" }, expiresInDays = 7 },
                 })
        {
            using var refused = await alice.SendAsync(HttpMethod.Post, "/api/dav-shares", request);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }

        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/dav-shares", WorkPc)) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var taken = await alice.SendAsync(HttpMethod.Post, "/api/dav-shares", WorkPc);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
    }

    [Fact]
    public async Task An_api_key_cannot_use_the_endpoints()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var key = await alice.KeyAsync("agent", KeyRight.Maintain);

        using var listed = await alice.SendAsync(HttpMethod.Get, "/api/dav-shares", bearer: key);
        using var created = await alice.SendAsync(HttpMethod.Post, "/api/dav-shares", WorkPc, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }
}
