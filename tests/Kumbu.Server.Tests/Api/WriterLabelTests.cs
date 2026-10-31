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
using MongoDB.Driver;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary>Writers as the viewer is shown them (collab design §8.2) and their kinds (spec §5a): the session's views tell
/// the user's own writes, their devices', their agents' and the server's apart.</summary>
[Collection("app")]
public sealed class WriterLabelTests(KumbuAppFactory app)
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
    public async Task The_viewers_own_writes_have_kind_self()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("mine.md", "mine\n");
        var store = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var seeded = VaultPath.Root.Child(user.Vault).Child("seeded.md");
        Assert.True(await store.TryCreateAsync(new StoredNote(seeded, "seeded\n", 1, Writers.Seed, DateTime.UtcNow, [], Ref: WriterRef.Server), Ct));

        Assert.Equal(("self", "self", "self"), await KindsAsync(user, "mine.md"));
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

    private IMongoCollection<ApiKeyDocument> KeyDocuments => app.Database.GetCollection<ApiKeyDocument>("api_keys");

    private Task<ApiKeyDocument> KeyDocumentAsync(string key) =>
        KeyDocuments.Find(k => k.Prefix == key.Substring(ApiKeys.KeyPrefix.Length, 8)).SingleAsync(Ct);

    /// <summary>As if the key last wrote <paramref name="daysAgo"/> days ago.</summary>
    private async Task LastWroteAsync(string key, int daysAgo)
    {
        var id = (await KeyDocumentAsync(key)).Id;
        await KeyDocuments.UpdateOneAsync(k => k.Id == id, Builders<ApiKeyDocument>.Update.Set(k => k.LastWriteAt, DateTime.UtcNow.AddDays(-daysAgo)), cancellationToken: Ct);
    }

    private static async Task<JsonArray> ChangesAsync(RestClient user, DateTime since)
    {
        using var response = await user.GetAsync($"/api/changes?since={Uri.EscapeDataString(since.ToString("O"))}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).AsArray();
    }

    [Fact]
    public async Task A_device_writing_after_a_long_pause_stands_out_in_the_changes()
    {
        var user = await NewAsync(Api);
        var device = await user.KeyAsync("Arbeit – Obsidian Pixel 9", KeyRight.Write, ApiKeyKind.Device);
        await LastWroteAsync(device, 20);
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "plan.md", "from Obsidian\n", device));

        var change = Assert.Single(await ChangesAsync(user, before))!;
        Assert.Equal($"/{user.Vault}/plan.md", change["path"]!.GetValue<string>());
        Assert.Equal("Arbeit – Obsidian Pixel 9", change["writer"]!.GetValue<string>());
        Assert.Equal("device", change["writerKind"]!.GetValue<string>());
        var quietSince = change["quietSince"]!.GetValue<DateTime>();
        var resumedAt = change["resumedAt"]!.GetValue<DateTime>();
        Assert.InRange((resumedAt - quietSince).TotalDays, 19.9, 20.1);
        var doc = await KeyDocumentAsync(device);
        Assert.Equal(doc.ResumedAt, doc.LastWriteAt);
    }

    [Fact]
    public async Task A_device_writing_regularly_or_for_the_first_time_does_not_stand_out()
    {
        var user = await NewAsync(Api);
        var regular = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var fresh = await user.KeyAsync("Arbeit – Obsidian Pixel 9", KeyRight.Write, ApiKeyKind.Device);
        await LastWroteAsync(regular, 13);
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "laptop.md", "x\n", regular));
        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "phone.md", "y\n", fresh));

        Assert.Empty(await ChangesAsync(user, before));
        // The first write starts the record, without a pause before it.
        var doc = await KeyDocumentAsync(fresh);
        Assert.NotNull(doc.LastWriteAt);
        Assert.Null(doc.ResumedAt);
    }

    [Fact]
    public async Task A_return_before_the_previous_sign_in_no_longer_stands_out()
    {
        var user = await NewAsync(Api);
        var device = await user.KeyAsync("Arbeit – Obsidian Pixel 9", KeyRight.Write, ApiKeyKind.Device);
        await LastWroteAsync(device, 20);
        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "plan.md", "x\n", device));
        var signedIn = DateTime.UtcNow.AddMilliseconds(1);
        await Task.Delay(10, Ct);

        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "later.md", "y\n", device));

        Assert.Empty(await ChangesAsync(user, signedIn));
    }

    [Fact]
    public async Task A_long_pause_leaves_agents_and_the_session_as_they_are()
    {
        var user = await NewAsync(Api);
        var agent = await user.KeyAsync("liz", KeyRight.Write);
        await LastWroteAsync(agent, 20);
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "agent.md", "x\n", agent));
        await user.CreateAsync("mine.md", "mine\n");

        var change = Assert.Single(await ChangesAsync(user, before))!;
        Assert.Equal($"/{user.Vault}/agent.md", change["path"]!.GetValue<string>());
        Assert.Equal("agent", change["writerKind"]!.GetValue<string>());
        Assert.Null(change["quietSince"]);
        Assert.Null(change["resumedAt"]);
    }

    /// <summary>A note written by <paramref name="user"/>'s name but sealed with <c>v: 2</c> and no reference, as a
    /// write path that forgot one would have stored it, and the same for its removal: the server's own store, so the
    /// working set holds it as MongoDB does.</summary>
    private async Task<EncryptedNoteStore> UnreferencedAsync(RestClient user)
    {
        var store = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var root = VaultPath.Root.Child(user.Vault);
        Assert.True(await store.TryCreateAsync(new StoredNote(root.Child("orphan.md"), "orphan\n", 1, user.Username, DateTime.UtcNow, [], Ref: WriterRef.Unknown), Ct));
        Assert.True(await store.TryCreateAsync(new StoredNote(root.Child("gone.md"), "gone\n", 1, user.Username, DateTime.UtcNow, [], Ref: WriterRef.Unknown), Ct));
        Assert.True(await store.TryTrashAsync((await store.GetAsync(root.Child("gone.md"), Ct))!, user.Username, WriterRef.Unknown, DateTime.UtcNow, Ct));
        return store;
    }

    [Fact]
    public async Task A_post_0_3_0_entry_without_reference_shows_as_unknown()
    {
        var user = await NewAsync(Api);
        await UnreferencedAsync(user);

        // As sealed: a payload version, no reference.
        var vault = (await Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        var key = await Vaults.SealedPayloads.KeyOfUserVaultAsync(app.Database, vault.Id, user.UserKey, Ct);
        var sealedNote = (await Vaults.SealedPayloads.OpenAllAsync(app.Database, vault.Id, key, "notes", Ct)).Single();
        Assert.Equal(2, (int)sealedNote["v"]!);
        Assert.False(sealedNote.ContainsKey("ref"));

        using var history = await user.GetAsync(user.Url("orphan.md", "history"));
        var entry = (await JsonAsync(history)).AsArray().Single()!;
        Assert.Equal("unknown", entry["writer"]!.GetValue<string>());
        Assert.Null(entry["writerKind"]);
        using var note = await user.GetAsync(user.Url("orphan.md"));
        Assert.Equal("unknown", (await JsonAsync(note))["lastWriter"]!.GetValue<string>());
        using var version = await user.GetAsync(user.Url("orphan.md", "version"));
        Assert.Equal("unknown", (await JsonAsync(version))["lastWriter"]!.GetValue<string>());
    }

    [Fact]
    public async Task History_and_activity_and_trash_render_through_one_service()
    {
        var user = await NewAsync(Api);
        await UnreferencedAsync(user);
        var agent = await user.KeyAsync("liz", KeyRight.Write);
        Assert.Equal(HttpStatusCode.Created, await PutAsync(user, "agent.md", "x\n", agent));

        using var history = await user.GetAsync(user.Url("orphan.md", "history"));
        using var agentHistory = await user.GetAsync(user.Url("agent.md", "history"));
        using var activity = await user.GetAsync($"/api/vaults/{Uri.EscapeDataString(user.Vault)}/activity");
        using var trash = await user.GetAsync($"/api/vaults/{Uri.EscapeDataString(user.Vault)}/trash");
        var rows = (await JsonAsync(activity))["rows"]!.AsArray();
        string Row(string path, string field) =>
            rows.Single(r => r!["files"]!.AsArray().Any(f => f!["path"]!.GetValue<string>() == path))![field]?.GetValue<string>() ?? "(none)";

        // Not the owner's name anywhere: the same display, from the same service, in all three.
        Assert.Equal("unknown", (await JsonAsync(history)).AsArray().Single()!["writer"]!.GetValue<string>());
        Assert.Equal(("unknown", "(none)"), (Row($"/{user.Vault}/orphan.md", "writer"), Row($"/{user.Vault}/orphan.md", "writerKind")));
        Assert.Equal("unknown", (await JsonAsync(trash))["notes"]!.AsArray().Single()!["deletedBy"]!.GetValue<string>());

        // And an owner's writer the same way in history and activity.
        var written = (await JsonAsync(agentHistory)).AsArray().Single()!;
        Assert.Equal(("liz", "agent"), (written["writer"]!.GetValue<string>(), written["writerKind"]!.GetValue<string>()));
        Assert.Equal(("liz", "agent"), (Row($"/{user.Vault}/agent.md", "writer"), Row($"/{user.Vault}/agent.md", "writerKind")));
    }
}
