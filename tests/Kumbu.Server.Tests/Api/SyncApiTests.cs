// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>GET /api/vaults/{vault}/versions</c>: what the Obsidian plugin syncs against (sync spec §5).</summary>
[Collection("app")]
public sealed class SyncApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Whole seconds, as the other timed tests: MongoDB keeps milliseconds.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private static string VersionsUrl(RestClient user, string? since = null) =>
        $"/api/vaults/{Uri.EscapeDataString(user.Vault)}/versions" + (since is null ? "" : $"?since={Uri.EscapeDataString(since)}");

    private static async Task<JsonNode> VersionsAsync(RestClient user, string? since = null, string? bearer = null)
    {
        using var response = await user.GetAsync(VersionsUrl(user, since), bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static string[] Paths(JsonNode answer, string field = "notes") =>
        [.. answer[field]!.AsArray().Select(n => n is JsonObject o ? o["path"]!.GetValue<string>() : n!.GetValue<string>())];

    private static string Cursor(JsonNode answer) => answer["cursor"]!.GetValue<string>();

    [Fact]
    public async Task The_full_list_has_every_note_and_the_state()
    {
        var user = await NewAsync(Api);
        var b = await user.CreateAsync("b.md", "b\n");
        await user.CreateAsync("a.md", "a\n");
        await user.CreateAsync("Ä/c.md", "c\n");

        using var response = await user.GetAsync(VersionsUrl(user));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await JsonAsync(response);
        Assert.True(answer["full"]!.GetValue<bool>());
        // Text order, as ls: Ä with the A's.
        Assert.Equal(["/Arbeit/a.md", "/Arbeit/Ä/c.md", "/Arbeit/b.md"], Paths(answer));
        Assert.Equal(b, answer["notes"]![2]!["version"]!.GetValue<string>());
        Assert.Equal(0, answer["damaged"]!.GetValue<int>());
        Assert.Empty(answer["deleted"]!.AsArray());
        Assert.Matches("^[0-9a-f]{16}$", answer["state"]!.GetValue<string>());
        Assert.Equal($"\"{answer["state"]!.GetValue<string>()}\"", response.Headers.ETag!.Tag);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, DateTime.Parse(Cursor(answer), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public async Task If_none_match_answers_304_until_a_note_changes()
    {
        var user = await NewAsync(Api);
        var version = await user.CreateAsync("a.md", "a\n");
        var state = (await VersionsAsync(user))["state"]!.GetValue<string>();

        var same = await GetWithEtagAsync(user, state);
        using var _ = await user.SendAsync(HttpMethod.Put, user.Url("a.md"), new { content = "b\n", version });
        var changed = await GetWithEtagAsync(user, state);

        Assert.Equal(HttpStatusCode.NotModified, same);
        Assert.Equal(HttpStatusCode.OK, changed);
    }

    private static async Task<HttpStatusCode> GetWithEtagAsync(RestClient user, string state)
    {
        using var response = await user.SendAsync(HttpMethod.Get, VersionsUrl(user), headers: new Dictionary<string, string> { ["If-None-Match"] = $"\"{state}\"" });
        return response.StatusCode;
    }

    [Fact]
    public async Task Since_lists_the_current_state_of_touched_paths()
    {
        var user = await NewAsync(Api);
        var edited = await user.CreateAsync("edited.md", "one\n");
        await user.CreateAsync("removed.md", "x\n");
        await user.CreateAsync("source.md", "s\n");
        await user.CreateAsync("still.md", "still\n");
        _time.Advance(TimeSpan.FromMinutes(10));
        var cursor = Cursor(await VersionsAsync(user));
        _time.Advance(TimeSpan.FromSeconds(1));

        using var edit = await user.SendAsync(HttpMethod.Put, user.Url("edited.md"), new { content = "two\n", version = edited });
        using var rm = await user.SendAsync(HttpMethod.Delete, user.Url("removed.md"));
        using var mv = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Arbeit/source.md", to = "/Arbeit/target.md", keepLinks = true });
        Assert.True(edit.IsSuccessStatusCode && rm.IsSuccessStatusCode && mv.IsSuccessStatusCode);

        var answer = await VersionsAsync(user, cursor);

        Assert.False(answer["full"]!.GetValue<bool>());
        Assert.Equal(["/Arbeit/edited.md", "/Arbeit/target.md"], Paths(answer));
        Assert.Equal((await user.ReadAsync("edited.md"))["version"]!.GetValue<string>(), answer["notes"]![0]!["version"]!.GetValue<string>());
        Assert.Equal(["/Arbeit/removed.md", "/Arbeit/source.md"], Paths(answer, "deleted"));
        Assert.Equal((await VersionsAsync(user))["state"]!.GetValue<string>(), answer["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(6, false)]
    public async Task Since_overlaps_by_five_minutes(int minutes, bool listed)
    {
        var user = await NewAsync(Api);
        _time.Advance(TimeSpan.FromMinutes(1));
        await user.CreateAsync("a.md", "a\n");
        _time.Advance(TimeSpan.FromMinutes(minutes));
        var cursor = Cursor(await VersionsAsync(user));

        var answer = await VersionsAsync(user, cursor);

        Assert.False(answer["full"]!.GetValue<bool>());
        Assert.Equal(listed ? ["/Arbeit/a.md"] : [], Paths(answer));
    }

    [Fact]
    public async Task Since_before_the_horizon_answers_the_full_list()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        var cursor = Cursor(await VersionsAsync(user));
        // The vault is locked when idle, and unlocked again by the next request: what changed in between, the server can't tell.
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog.Sweep();
        var fresh = await user.SignInAgainAsync();

        var answer = await VersionsAsync(fresh, cursor);

        Assert.True(answer["full"]!.GetValue<bool>());
        Assert.Equal(["/Arbeit/a.md"], Paths(answer));
    }

    [Fact]
    public async Task A_cursor_older_than_25_days_or_in_the_future_or_garbled_answers_the_full_list()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        var now = _time.GetUtcNow().UtcDateTime;

        foreach (var since in new[] { now.AddDays(-26).ToString("O"), now.AddMinutes(10).ToString("O"), "yesterday", "" })
        {
            var answer = await VersionsAsync(user, since);
            Assert.True(answer["full"]!.GetValue<bool>(), since);
            Assert.Equal(["/Arbeit/a.md"], Paths(answer));
        }
    }

    [Fact]
    public async Task Damaged_notes_are_counted()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        await user.CreateAsync("b.md", "b\n");
        var before = await VersionsAsync(user);
        var vaultId = (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault).Id;
        var notes = app.Database.GetCollection<BsonDocument>("notes");
        var one = await notes.Find(new BsonDocument("vault", vaultId)).FirstAsync(Ct);
        await notes.UpdateOneAsync(new BsonDocument("_id", one["_id"]), Builders<BsonDocument>.Update.Set("blob", new BsonBinaryData(new byte[64])), cancellationToken: Ct);
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog.Sweep();

        var after = await VersionsAsync(await user.SignInAgainAsync());

        Assert.Equal(1, after["damaged"]!.GetValue<int>());
        Assert.Single(after["notes"]!.AsArray());
        Assert.NotEqual(before["state"]!.GetValue<string>(), after["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Since_never_lists_a_damaged_note_as_deleted()
    {
        var user = await NewAsync(Api);
        var version = await user.CreateAsync("a.md", "a\n");
        var cursor = Cursor(await VersionsAsync(user));
        using var edit = await user.SendAsync(HttpMethod.Put, user.Url("a.md"), new { content = "b\n", version });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var vault = (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        var notes = app.Database.GetCollection<BsonDocument>("notes");
        await notes.UpdateOneAsync(new BsonDocument("vault", vault.Id), Builders<BsonDocument>.Update.Set("blob", new BsonBinaryData(new byte[64])), cancellationToken: Ct);
        await Catalog.RefreshAsync(vault, Kumbu.Core.Paths.VaultPath.Root.Child(user.Vault).Child("a.md"), Ct);

        var answer = await VersionsAsync(user, cursor);

        Assert.False(answer["full"]!.GetValue<bool>());
        Assert.Equal(1, answer["damaged"]!.GetValue<int>());
        Assert.Empty(answer["deleted"]!.AsArray());
        Assert.Empty(answer["notes"]!.AsArray());
    }

    [Fact]
    public async Task Change_times_older_than_any_cursor_it_answers_are_dropped()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        var vault = (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        Assert.NotEmpty(vault.ChangedAt);

        _time.Advance(Kumbu.Server.Api.SyncApi.MaxCursorAge + Kumbu.Server.Api.SyncApi.Overlap + TimeSpan.FromMinutes(1));
        vault.Touch(_time.GetUtcNow().UtcDateTime);
        Catalog.Sweep();

        Assert.Same(vault, (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault));
        Assert.Empty(vault.ChangedAt);
    }

    [Fact]
    public async Task A_write_through_an_older_object_reaches_the_current_one()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        // A request that opened the vault, and holds it while the vault is locked and opened again by another.
        var stale = new EncryptedNoteStore(Catalog, new UserCredential(user.User.Id, user.UserKey));
        Assert.Contains(user.Vault, await stale.ListVaultsAsync(Ct));
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog.Sweep();
        var current = (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        var path = Kumbu.Core.Paths.VaultPath.Root.Child(user.Vault).Child("late.md");

        Assert.True(await stale.TryCreateAsync(new Kumbu.Core.Notes.StoredNote(path, "late\n", 1, "lea", _time.GetUtcNow().UtcDateTime, []), Ct));

        Assert.True(current.Notes.ContainsKey(path));
        Assert.True(current.ChangedAt.ContainsKey(path));
        var answer = await VersionsAsync(await user.SignInAgainAsync(), _time.GetUtcNow().UtcDateTime.ToString("O"));
        Assert.Contains("/Arbeit/late.md", Paths(answer));
    }

    [Fact]
    public async Task A_committed_write_through_an_older_object_succeeds_when_reading_it_again_fails()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        var stale = new EncryptedNoteStore(Catalog, new UserCredential(user.User.Id, user.UserKey));
        Assert.Contains(user.Vault, await stale.ListVaultsAsync(Ct));
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog.Sweep();
        var current = (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        var path = Kumbu.Core.Paths.VaultPath.Root.Child(user.Vault).Child("late.md");
        var hints = 0;
        Action<string> counted = id =>
        {
            if (id == current.Id) Interlocked.Increment(ref hints);
        };
        Catalog.NotesChanged += counted;
        Catalog.RefreshFaultAt = () => throw new MongoDB.Driver.MongoConnectionException(new MongoDB.Driver.Core.Connections.ConnectionId(new MongoDB.Driver.Core.Servers.ServerId(new MongoDB.Driver.Core.Clusters.ClusterId(), new System.Net.DnsEndPoint("localhost", 27017))), "gone");
        try
        {
            Assert.True(await stale.TryCreateAsync(new Kumbu.Core.Notes.StoredNote(path, "late\n", 1, "lea", _time.GetUtcNow().UtcDateTime, []), Ct));
        }
        finally
        {
            Catalog.RefreshFaultAt = null;
            Catalog.NotesChanged -= counted;
        }

        Assert.Equal(1, hints);
        // The object that couldn't follow is let go: the next request reads the vault afresh, with the note.
        Assert.False(current.ChangedAt.ContainsKey(path));
        var reloaded = (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        Assert.NotSame(current, reloaded);
        Assert.True(reloaded.Notes.ContainsKey(path));
    }

    [Fact]
    public async Task A_pending_vault_answers_503_not_404()
    {
        var user = await NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        var key = await user.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);
        var id = (await Catalog.OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault).Id;
        Catalog.RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(id, user.User.Id, user.UserKey, Ct));
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
        }

        using var asSession = await user.GetAsync(VersionsUrl(user));
        using var since = await user.GetAsync(VersionsUrl(user, _time.GetUtcNow().UtcDateTime.ToString("O")));

        foreach (var response in new[] { asSession, since })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
        }
    }

    [Fact]
    public async Task A_key_for_another_vault_gets_404()
    {
        var owner = await NewAsync(Api);
        using var second = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var other = await NewAsync(Api, "Fremd");
        await other.CreateAsync("secret.md", "theirs\n");
        var key = await owner.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);

        using var foreign = await owner.GetAsync("/api/vaults/Fremd/versions", key);
        using var notGiven = await owner.GetAsync("/api/vaults/Privat/versions", key);
        using var unknown = await owner.GetAsync("/api/vaults/Nirgends/versions", key);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notGiven.StatusCode);
        Assert.Equal("There is no vault 'Fremd'.", await ErrorAsync(foreign));
        Assert.Equal("There is no vault 'Privat'.", await ErrorAsync(notGiven));
        Assert.Equal("There is no vault 'Nirgends'.", await ErrorAsync(unknown));

        // Deleting at a version is no way in either.
        var version = (await other.ReadAsync("secret.md"))["version"]!.GetValue<string>();
        using var delete = await owner.SendAsync(HttpMethod.Delete, $"/api/notes/Fremd/secret.md?version={version}", bearer: key);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("There is no vault 'Fremd'.", await ErrorAsync(delete));
        Assert.Equal("theirs\n", (await other.ReadAsync("secret.md"))["content"]!.GetValue<string>());
    }
}
