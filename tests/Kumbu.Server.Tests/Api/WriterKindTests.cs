// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.OAuth;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary>Writer kinds (spec §5a): the session's views tell the user's own writes, their devices', their agents' and the server's apart.</summary>
[Collection("app")]
public sealed class WriterKindTests(KumbuAppFactory app)
{
    private const string Callback = "https://client.example/callback";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private async Task<HttpStatusCode> PutAsync(RestClient user, string path, string content, string? bearer = null)
    {
        using var response = await user.SendAsync(HttpMethod.Put, user.Url(path), new { content }, bearer: bearer);
        return response.StatusCode;
    }

    /// <summary>The note's writer kind in the three places a session sees it: the note, the version poll and the history's current entry.</summary>
    private static async Task<(string? Note, string? Version, string? History)> KindsAsync(RestClient user, string path, string? bearer = null)
    {
        using var note = await user.GetAsync(user.Url(path), bearer);
        using var version = await user.GetAsync(user.Url(path, "version"), bearer);
        using var history = await user.GetAsync(user.Url(path, "history"), bearer);
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var current = (await JsonAsync(history)).AsArray().Single(e => e!["current"]!.GetValue<bool>())!;
        return (
            (await JsonAsync(note))["lastWriterKind"]?.GetValue<string>(),
            (await JsonAsync(version))["lastWriterKind"]?.GetValue<string>(),
            current["writerKind"]?.GetValue<string>());
    }

    /// <summary>A connection of <paramref name="owner"/> with write on its vault, authorised, and the SDK client on its URL.</summary>
    private async Task<McpClient> ConnectionClientAsync(RestClient owner, string name)
    {
        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name, vaults = new Dictionary<string, string> { [owner.Vault] = "write" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created))["id"]!.GetValue<string>();
        var flow = await OAuthFlow.StartAsync(Api, owner, id, await OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        using var token = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var access = (await JsonAsync(token))["access_token"]!.GetValue<string>();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(OAuthFlow.ResourceOf(id)),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {access}", ["X-Forwarded-For"] = $"198.27.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}" },
            },
            Api.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    [Fact]
    public async Task A_device_write_is_a_device_for_the_session()
    {
        var user = await NewAsync(Api);
        var device = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);

        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "plan.md", "from Obsidian\n", device));

        Assert.Equal(("device", "device", "device"), await KindsAsync(user, "plan.md"));
    }

    [Fact]
    public async Task An_agent_key_and_a_connection_are_agents()
    {
        var user = await NewAsync(Api);
        var agent = await user.KeyAsync("laptop", KeyRight.Write);
        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "by-key.md", "x\n", agent));
        await using var connection = await ConnectionClientAsync(user, "claude.ai");
        var written = await connection.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = $"/{user.Vault}/by-connection.md", ["content"] = "y\n" }, cancellationToken: Ct);
        Assert.NotEqual(true, written.IsError);

        Assert.Equal(("agent", "agent", "agent"), await KindsAsync(user, "by-key.md"));
        Assert.Equal(("agent", "agent", "agent"), await KindsAsync(user, "by-connection.md"));
    }

    [Fact]
    public async Task The_users_own_write_is_user_and_seed_is_server()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("mine.md", "mine\n");
        var store = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var seeded = VaultPath.Root.Child(user.Vault).Child("seeded.md");
        Assert.True(await store.TryCreateAsync(new StoredNote(seeded, "seeded\n", 1, Writers.Seed, DateTime.UtcNow, []), Ct));

        Assert.Equal(("user", "user", "user"), await KindsAsync(user, "mine.md"));
        Assert.Equal(("server", "server", "server"), await KindsAsync(user, "seeded.md"));
    }

    [Fact]
    public async Task An_api_key_sees_no_writer_kinds()
    {
        var user = await NewAsync(Api);
        var device = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "plan.md", "x\n", device));

        Assert.Equal((null, null, null), await KindsAsync(user, "plan.md", device));
    }

    [Fact]
    public async Task Changes_list_agent_writes_only()
    {
        var user = await NewAsync(Api);
        var device = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var agent = await user.KeyAsync("laptop", KeyRight.Write);
        var before = DateTime.UtcNow.AddSeconds(-1);
        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "device.md", "x\n", device));
        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "agent.md", "y\n", agent));
        await using var connection = await ConnectionClientAsync(user, "claude.ai");
        var written = await connection.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = $"/{user.Vault}/connection.md", ["content"] = "z\n" }, cancellationToken: Ct);
        Assert.NotEqual(true, written.IsError);

        using var response = await user.GetAsync($"/api/changes?since={Uri.EscapeDataString(before.ToString("O"))}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await JsonAsync(response)).AsArray().Select(c => c!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal);
        Assert.Equal([$"/{user.Vault}/agent.md", $"/{user.Vault}/connection.md"], paths);
    }
}
