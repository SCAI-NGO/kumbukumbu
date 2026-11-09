// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>
/// Editing a connection's vaults in a session that hides some (Decision 9): the grants on hidden vaults, which the
/// session can neither see nor name, stay as stored, with their level and "May publish"; one on hidden vaults alone
/// answers as no such connection; revoking stays whole.
/// </summary>
[Collection("app")]
public sealed class HiddenGrantsTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static async Task<string> CreateAsync(HiddenWorld world, Dictionary<string, string> vaults, string[]? publish = null)
    {
        using var created = await world.Alice.SendAsync(HttpMethod.Post, "/api/connections", new { name = "Agent", vaults, publish });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
    }

    /// <summary>The connection as stored, read with Alice's user key, past any session's filter.</summary>
    private async Task<ConnectionInfo> StoredAsync(HiddenWorld world, string id) =>
        (await Api.Services.GetRequiredService<Connections>().ListAsync(world.Alice.User, world.Alice.UserKey, Ct)).Single(c => c.Id == id);

    /// <summary>Authorises the connection as Alice and returns the access token.</summary>
    private async Task<string> ConnectAsync(HiddenWorld world, string id)
    {
        var flow = await OAuthFlow.StartAsync(Api, world.Alice, id, await OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        using var token = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return (await RestClient.JsonAsync(token))["access_token"]!.GetValue<string>();
    }

    [Fact]
    public async Task An_edit_keeps_the_grant_on_a_hidden_vault_with_its_publish_right()
    {
        var world = await HiddenWorld.NewAsync(Api);
        var workId = await world.In("Work").VaultIdAsync();
        var id = await CreateAsync(world, new() { ["Work"] = "write", ["Tagebuch"] = "read" }, ["Tagebuch"]);
        var access = await ConnectAsync(world, id);
        await world.HideAsync();

        using var edited = await world.Alice.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = new Dictionary<string, string> { ["Work"] = "read" } });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var view = await RestClient.JsonAsync(edited);
        Assert.Equal(["Work"], view["vaults"]!.AsObject().Select(v => v.Key));
        Assert.Empty(view["publish"]!.AsArray());
        var stored = await StoredAsync(world, id);
        Assert.Equal(
            new Dictionary<string, KeyRight> { [workId] = KeyRight.Read, [world.TagebuchId] = KeyRight.Read },
            stored.Vaults.ToDictionary(v => v.Key, v => v.Value));
        Assert.Equal([world.TagebuchId], stored.Publish);

        // The rights changed, so the old token is gone; the agent reconnects and reads the hidden vault as before.
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(id, access));
        await world.RevealAsync();
        Assert.Contains(HiddenWorld.TagebuchWord, await CatAsync(id, await ConnectAsync(world, id), "/Tagebuch/geheim.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_after_a_hidden_vault_is_deleted_keeps_only_the_rest()
    {
        var world = await HiddenWorld.NewAsync(Api);
        var workId = await world.In("Work").VaultIdAsync();
        var id = await CreateAsync(world, new() { ["Work"] = "write", ["Tagebuch"] = "read" });
        await world.HideAsync();
        Assert.NotNull(await Api.Services.GetRequiredService<Deletion>().DeleteVaultAsync(
            world.Alice.User, world.Alice.UserKey, "Tagebuch", RestClient.Password, "Tagebuch", null, Ct));

        using var edited = await world.Alice.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = new Dictionary<string, string> { ["Work"] = "read" } });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var stored = await StoredAsync(world, id);
        Assert.Equal(new Dictionary<string, KeyRight> { [workId] = KeyRight.Read }, stored.Vaults.ToDictionary(v => v.Key, v => v.Value));
    }

    [Fact]
    public async Task An_empty_edit_of_no_such_connection_answers_as_for_a_user_without_discreet_vaults()
    {
        var world = await HiddenWorld.NewAsync(Api);
        await world.HideAsync();
        var plain = await RestClient.NewAsync(Api, vault: "Work");
        const string madeUp = "aaaaaaaaaaaaaaaa";

        var hidden = await EditAsync(world.Alice, madeUp);
        var without = await EditAsync(plain, madeUp);

        Assert.Equal(HttpStatusCode.BadRequest, without.Status);
        Assert.Equal(without, hidden);

        static async Task<(HttpStatusCode Status, string Body)> EditAsync(RestClient user, string id)
        {
            using var response = await user.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = new Dictionary<string, string>() });
            return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task A_connection_on_hidden_vaults_alone_answers_an_edit_as_no_such_connection()
    {
        var world = await HiddenWorld.NewAsync(Api);
        var id = await CreateAsync(world, new() { ["Tagebuch"] = "read" }, ["Tagebuch"]);
        var access = await ConnectAsync(world, id);
        await world.HideAsync();
        const string madeUp = "aaaaaaaaaaaaaaaa";

        foreach (var vaults in new[] { new Dictionary<string, string>(), new() { ["Work"] = "write" } })
        {
            Assert.Equal(await EditAsync(madeUp, vaults), await EditAsync(id, vaults));
        }

        var stored = await StoredAsync(world, id);
        Assert.Equal([world.TagebuchId], stored.Vaults.Keys);
        Assert.Equal(KeyRight.Read, stored.Vaults[world.TagebuchId]);
        Assert.Equal([world.TagebuchId], stored.Publish);
        // Nothing changed, so the agent keeps its token.
        Assert.Contains(HiddenWorld.TagebuchWord, await CatAsync(id, access, "/Tagebuch/geheim.md"), StringComparison.Ordinal);

        async Task<(HttpStatusCode Status, string Body)> EditAsync(string connection, Dictionary<string, string> vaults)
        {
            using var response = await world.Alice.SendAsync(HttpMethod.Put, $"/api/connections/{connection}/vaults", new { vaults });
            return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task An_empty_edit_of_a_connection_on_visible_vaults_is_still_refused()
    {
        var world = await HiddenWorld.NewAsync(Api);
        var id = await CreateAsync(world, new() { ["Work"] = "write" });
        await world.HideAsync();

        using var empty = await world.Alice.SendAsync(HttpMethod.Put, $"/api/connections/{id}/vaults", new { vaults = new Dictionary<string, string>() });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("A connection needs at least one vault.", await RestClient.ErrorAsync(empty));
    }

    [Fact]
    public async Task Revoking_while_hidden_revokes_the_whole_connection()
    {
        var world = await HiddenWorld.NewAsync(Api);
        var id = await CreateAsync(world, new() { ["Work"] = "write", ["Tagebuch"] = "write" });
        var access = await ConnectAsync(world, id);

        await world.HideAsync();
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(id, access));

        using var revoke = await world.Alice.SendAsync(HttpMethod.Delete, $"/api/connections/{id}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(id, access));
        var stored = await StoredAsync(world, id);
        Assert.Equal(ConnectionState.Revoked, stored.State);
        Assert.Contains(world.TagebuchId, stored.Vaults.Keys);
    }

    /// <summary>The status of an MCP <c>initialize</c> on the connection's URL with <paramref name="access"/>.</summary>
    private async Task<HttpStatusCode> InitializeAsync(string connectionId, string access)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/c/{connectionId}") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("X-Forwarded-For", $"198.27.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    /// <summary>What <c>cat <paramref name="path"/></c> answers over MCP on the connection's URL with <paramref name="access"/>.</summary>
    private async Task<string> CatAsync(string connectionId, string access, string path)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(OAuthFlow.ResourceOf(connectionId)),
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {access}",
                    ["X-Forwarded-For"] = $"198.27.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}",
                },
            },
            Api.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        var result = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = $"cat {path}" }, cancellationToken: Ct);
        Assert.NotEqual(true, result.IsError);
        return Assert.IsType<TextContentBlock>(result.Content[0]).Text;
    }
}
