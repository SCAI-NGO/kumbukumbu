// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>API keys: named, for chosen vaults with a right each, sealed into the key's slots.</summary>
[Collection("app")]
public sealed class ApiKeyTests(KumbuAppFactory app)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiKeys Keys => app.Services.GetRequiredService<ApiKeys>();

    /// <summary>A sign-in just now: these tests aren't about a credentials change since the session began.</summary>
    private static DateTime JustRead => DateTime.UtcNow;

    private IMongoCollection<BsonDocument> KeyDocs => app.Database.GetCollection<BsonDocument>("api_keys");

    private IMongoCollection<BsonDocument> SlotDocs => app.Database.GetCollection<BsonDocument>("slots");

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    private static async Task<CallToolResult> ShAsync(McpClient client, string command) =>
        await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);

    /// <summary>A new user, signed in, with a vault of their own for each name: the user, their user key and the vault ids by name.</summary>
    private async Task<(User User, byte[] UserKey, Dictionary<string, string> Vaults)> UserWithVaultsAsync(params string[] vaults)
    {
        var users = new UserCatalog(
            app.Database, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), Keys, app.Services.GetRequiredService<Connections>(), TimeProvider.System, NullLogger<UserCatalog>.Instance);
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, key, _) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        var catalog = app.Services.GetRequiredService<VaultCatalog>();
        var ids = new Dictionary<string, string>();
        foreach (var name in vaults) ids[name] = (await catalog.CreateForUserAsync(name, user.Id, key, Ct)).Id;
        return (user, key, ids);
    }

    private static HttpRequestMessage Initialize(string bearer, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return request;
    }

    private async Task<HttpStatusCode> InitializeStatusAsync(string bearer)
    {
        using var http = app.CreateClient();
        using var request = Initialize(bearer);
        using var response = await http.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_new_key_is_kumbu_and_43_base62_characters_and_only_derived_values_are_stored()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");

        var (key, info) = await Keys.CreateAsync(user, userKey, JustRead, "Claude Code", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read }, Ct);

        Assert.Matches("^kumbu_[0-9A-Za-z]{43}$", key);
        Assert.Equal(key[6..14], info.Prefix);
        Assert.Equal("Claude Code", info.Name);
        Assert.Equal(KeyRight.Read, info.Vaults[vaults["Notes"]]);

        var doc = await KeyDocs.Find(Builders<BsonDocument>.Filter.Eq("_id", info.Id)).SingleAsync(Ct);
        Assert.Equal(user.Id, doc["user"].AsString);
        Assert.Equal(info.Prefix, doc["prefix"].AsString);
        var stored = doc.ToJson() + string.Concat((await SlotDocs.Find(Builders<BsonDocument>.Filter.Eq("apiKey", info.Id)).ToListAsync(Ct)).Select(s => s.ToJson()));
        Assert.DoesNotContain(key[6..], stored, StringComparison.Ordinal);
        Assert.DoesNotContain("Claude Code", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_opens_only_its_vaults()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work", "Diary", "Ideas");
        var (key, _) = await Keys.CreateAsync(
            user, userKey, JustRead, "agent", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Write, [vaults["Ideas"]] = KeyRight.Read }, Ct);

        await using var client = await app.ConnectAsync(key);
        var ls = Text(await ShAsync(client, "ls /"));

        Assert.Contains("Work/", ls, StringComparison.Ordinal);
        Assert.Contains("Ideas/", ls, StringComparison.Ordinal);
        Assert.DoesNotContain("Diary/", ls, StringComparison.Ordinal);
        Assert.DoesNotContain("Demo/", ls, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_without_vaults_cannot_be_created()
    {
        var (user, userKey, _) = await UserWithVaultsAsync("Notes");

        await Assert.ThrowsAsync<ArgumentException>(() => Keys.CreateAsync(user, userKey, JustRead, "empty", new Dictionary<string, KeyRight>(), Ct));
        Assert.Empty(await Keys.ListAsync(user, userKey, Ct));
    }

    [Fact]
    public async Task A_vault_the_user_does_not_own_cannot_be_granted()
    {
        var (user, userKey, _) = await UserWithVaultsAsync("Notes");
        var (_, _, theirs) = await UserWithVaultsAsync("Secret");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Keys.CreateAsync(user, userKey, JustRead, "thief", new Dictionary<string, KeyRight> { [theirs["Secret"]] = KeyRight.Read }, Ct));
        Assert.Empty(await Keys.ListAsync(user, userKey, Ct));
    }

    [Fact]
    public async Task A_revoked_key_is_401_at_once_also_with_its_vault_in_the_cache()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var (key, info) = await Keys.CreateAsync(user, userKey, JustRead, "laptop", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Write }, Ct);
        await using (var client = await app.ConnectAsync(key))
        {
            Assert.Contains("Notes/", Text(await ShAsync(client, "ls /")), StringComparison.Ordinal);
        }

        Assert.True(await Keys.RevokeAsync(user, info.Id, Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeStatusAsync(key));
        Assert.Empty(await SlotDocs.Find(Builders<BsonDocument>.Filter.Eq("apiKey", info.Id)).ToListAsync(Ct));
        Assert.NotNull(Assert.Single(await Keys.ListAsync(user, userKey, Ct)).RevokedAt);
        Assert.False(await Keys.RevokeAsync(user, info.Id, Ct));
    }

    [Fact]
    public async Task Another_user_cannot_revoke_a_key()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var (other, _, _) = await UserWithVaultsAsync("Other");
        var (key, info) = await Keys.CreateAsync(user, userKey, JustRead, "laptop", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read }, Ct);

        Assert.False(await Keys.RevokeAsync(other, info.Id, Ct));

        Assert.Equal(HttpStatusCode.OK, await InitializeStatusAsync(key));
    }

    [Fact]
    public async Task A_slot_whose_stored_rights_were_edited_in_mongodb_does_not_open()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Shared", "Own");
        var (key, info) = await Keys.CreateAsync(
            user, userKey, JustRead, "agent", new Dictionary<string, KeyRight> { [vaults["Shared"]] = KeyRight.Read, [vaults["Own"]] = KeyRight.Write }, Ct);
        await using (var client = await app.ConnectAsync(key))
        {
            Assert.Contains("Shared/", Text(await ShAsync(client, "ls /")), StringComparison.Ordinal);
        }

        var bySlot = (string vault) => Builders<BsonDocument>.Filter.Eq("apiKey", info.Id) & Builders<BsonDocument>.Filter.Eq("vault", vault);
        var readSlot = await SlotDocs.Find(bySlot(vaults["Shared"])).SingleAsync(Ct);
        var writeSlot = await SlotDocs.Find(bySlot(vaults["Own"])).SingleAsync(Ct);

        // The right is nowhere in the clear, so there is nothing to flip but the sealed payload: the write slot's
        // sealed payload copied over the read slot's (the only way to "upgrade" it without the key) doesn't open.
        Assert.DoesNotContain(readSlot.Names, n => n.Contains("right", StringComparison.OrdinalIgnoreCase));
        await SlotDocs.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", readSlot["_id"]), Builders<BsonDocument>.Update.Set("wrapped", writeSlot["wrapped"]), cancellationToken: Ct);

        await using var after = await app.ConnectAsync(key);
        var ls = Text(await ShAsync(after, "ls /"));
        Assert.DoesNotContain("Shared/", ls, StringComparison.Ordinal);
        Assert.Contains("Own/", ls, StringComparison.Ordinal);
        var write = await after.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Shared/x.md", ["content"] = "x\n" }, cancellationToken: Ct);
        Assert.True(write.IsError);
    }

    [Fact]
    public async Task Two_keys_of_one_user_write_as_two_writers()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var grant = new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Write };
        var (laptop, _) = await Keys.CreateAsync(user, userKey, JustRead, "laptop", grant, Ct);
        var (phone, _) = await Keys.CreateAsync(user, userKey, JustRead, "phone", grant, Ct);

        string version;
        await using (var first = await app.ConnectAsync(laptop))
        {
            var created = await first.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Notes/a.md", ["content"] = "one\n" }, cancellationToken: Ct);
            Assert.NotEqual(true, created.IsError);
            var token = Assert.IsType<TextContentBlock>(created.Content[1]).Text;
            version = token[(token.LastIndexOf(' ') + 1)..];
        }

        await using var second = await app.ConnectAsync(phone);
        var edited = await second.CallToolAsync(
            "edit", new Dictionary<string, object?> { ["path"] = "/Notes/a.md", ["version"] = version, ["new_text"] = "two\n" }, cancellationToken: Ct);
        Assert.NotEqual(true, edited.IsError);

        // Within ten minutes, one writer would have made one version; two keys are two writers, two changesets.
        var history = Text(await ShAsync(second, "history /Notes/a.md")).Split('\n');
        Assert.Equal(2, history.Length);
        Assert.Contains(history, l => l.Contains("laptop", StringComparison.Ordinal));
        Assert.Contains(history, l => l.Contains("phone", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Claude (laptop)")]
    [InlineData("tab\there")]
    [InlineData("chris\u200B")]
    [InlineData("laptop\u202Edm.txt")]
    [InlineData("dev")]
    [InlineData("seed")]
    public async Task An_unusable_name_is_refused(string name)
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Keys.CreateAsync(user, userKey, JustRead, name, new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read }, Ct));
    }

    [Fact]
    public async Task A_name_has_at_most_64_characters_after_trimming()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var grant = new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read };

        var (_, info) = await Keys.CreateAsync(user, userKey, JustRead, "  " + new string('k', 64) + "  ", grant, Ct);

        Assert.Equal(new string('k', 64), info.Name);
        await Assert.ThrowsAsync<ArgumentException>(() => Keys.CreateAsync(user, userKey, JustRead, new string('k', 65), grant, Ct));
    }

    // The agent banner tells a key's writes from the user's own by the writer label.
    [Fact]
    public async Task A_key_cannot_be_named_like_its_user()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Keys.CreateAsync(user, userKey, JustRead, user.Username.ToUpperInvariant(), new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read }, Ct));
    }

    [Fact]
    public async Task Names_are_unique_per_user_until_the_key_is_revoked()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var (other, otherKey, otherVaults) = await UserWithVaultsAsync("Notes");
        var grant = new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read };
        var (_, first) = await Keys.CreateAsync(user, userKey, JustRead, "Laptop", grant, Ct);

        await Assert.ThrowsAsync<ApiKeyNameTakenException>(() => Keys.CreateAsync(user, userKey, JustRead, " laptop ", grant, Ct));
        await Keys.CreateAsync(other, otherKey, JustRead, "Laptop", new Dictionary<string, KeyRight> { [otherVaults["Notes"]] = KeyRight.Read }, Ct);

        await Keys.RevokeAsync(user, first.Id, Ct);
        await Keys.CreateAsync(user, userKey, JustRead, "Laptop", grant, Ct);
    }

    [Fact]
    public async Task List_shows_name_prefix_vaults_and_rights_of_the_users_own_keys()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work", "Diary");
        var (other, otherKey, otherVaults) = await UserWithVaultsAsync("Elsewhere");
        await Keys.CreateAsync(other, otherKey, JustRead, "theirs", new Dictionary<string, KeyRight> { [otherVaults["Elsewhere"]] = KeyRight.Read }, Ct);
        var (_, created) = await Keys.CreateAsync(
            user, userKey, JustRead, "agent", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Write, [vaults["Diary"]] = KeyRight.Read }, Ct);

        var listed = Assert.Single(await Keys.ListAsync(user, userKey, Ct));

        Assert.Equal(created.Id, listed.Id);
        Assert.Equal("agent", listed.Name);
        Assert.Equal(created.Prefix, listed.Prefix);
        Assert.Equal(KeyRight.Write, listed.Vaults[vaults["Work"]]);
        Assert.Equal(KeyRight.Read, listed.Vaults[vaults["Diary"]]);
        Assert.Null(listed.LastUsedAt);
        Assert.Null(listed.RevokedAt);
    }

    [Fact]
    public async Task A_device_key_is_listed_as_a_device()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work");

        var (_, created) = await Keys.CreateAsync(
            user, userKey, JustRead, "Work – Obsidian Laptop", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Write }, Ct, kind: ApiKeyKind.Device);

        Assert.Equal(ApiKeyKind.Device, created.Kind);
        Assert.Equal(ApiKeyKind.Device, Assert.Single(await Keys.ListAsync(user, userKey, Ct)).Kind);
    }

    [Fact]
    public async Task A_key_made_before_kinds_is_an_agent()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work");
        var (_, created) = await Keys.CreateAsync(
            user, userKey, JustRead, "old", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Write }, Ct, kind: ApiKeyKind.Device);
        // The meta as every key had it before kinds existed: name and vaults only.
        var oldMeta = Encoding.UTF8.GetBytes($$$"""{"name":"old","vaults":{"{{{vaults["Work"]}}}":"write"}}""");
        await KeyDocs.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", created.Id),
            Builders<BsonDocument>.Update.Set("meta", ApiKeys.SealMeta(userKey, user.Id, created.Id, oldMeta)),
            cancellationToken: Ct);

        var listed = Assert.Single(await Keys.ListAsync(user, userKey, Ct));

        Assert.Equal("old", listed.Name);
        Assert.Equal(ApiKeyKind.Agent, listed.Kind);
    }

    [Fact]
    public async Task An_agent_keys_meta_has_no_kind()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work");
        var (_, created) = await Keys.CreateAsync(user, userKey, JustRead, "agent", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Write }, Ct);

        var doc = await KeyDocs.Find(Builders<BsonDocument>.Filter.Eq("_id", created.Id)).SingleAsync(Ct);
        var plain = Encoding.UTF8.GetString(ApiKeys.OpenMeta(userKey, user.Id, created.Id, doc["meta"].AsByteArray));

        Assert.DoesNotContain("kind", plain, StringComparison.Ordinal);
        Assert.Equal(ApiKeyKind.Agent, created.Kind);
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var (key, info) = await Keys.CreateAsync(user, userKey, JustRead, "agent", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read }, Ct);
        var byId = Builders<BsonDocument>.Filter.Eq("_id", info.Id);
        DateTime LastUsed(BsonDocument d) => d["lastUsedAt"].ToUniversalTime();

        Assert.Equal(HttpStatusCode.OK, await InitializeStatusAsync(key));
        var first = LastUsed(await KeyDocs.Find(byId).SingleAsync(Ct));

        var recent = first.AddSeconds(-30);
        await KeyDocs.UpdateOneAsync(byId, Builders<BsonDocument>.Update.Set("lastUsedAt", recent), cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, await InitializeStatusAsync(key));
        Assert.Equal(recent, LastUsed(await KeyDocs.Find(byId).SingleAsync(Ct)));

        var stale = first.AddMinutes(-2);
        await KeyDocs.UpdateOneAsync(byId, Builders<BsonDocument>.Update.Set("lastUsedAt", stale), cancellationToken: Ct);
        Assert.Equal(HttpStatusCode.OK, await InitializeStatusAsync(key));
        Assert.True(LastUsed(await KeyDocs.Find(byId).SingleAsync(Ct)) >= first);
    }

    /// <summary>The app's keys, but on a clock of the test's own: for keys made in the past, or checked in the future.</summary>
    private ApiKeys KeysAt(FakeTimeProvider time) =>
        new(app.Database, app.Services.GetRequiredService<VaultCatalog>(), time, NullLogger<ApiKeys>.Instance);

    [Fact]
    public async Task A_key_with_an_expiry_works_until_then_and_is_refused_from_then_on()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var keys = KeysAt(time);

        var (key, info) = await keys.CreateAsync(user, userKey, JustRead, "agent", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read }, Ct, expiresInDays: 30);

        var expiresAt = Assert.IsType<DateTime>(info.ExpiresAt);
        Assert.InRange(expiresAt - time.GetUtcNow().UtcDateTime, TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(30));
        Assert.Equal(expiresAt, Assert.Single(await keys.ListAsync(user, userKey, Ct)).ExpiresAt);
        Assert.NotNull(await keys.ResolveAsync(key, Ct));
        time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        Assert.NotNull(await keys.ResolveAsync(key, Ct));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await keys.ResolveAsync(key, Ct));
    }

    [Fact]
    public async Task An_expired_key_is_401_like_a_revoked_one()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        // Made 31 days ago for 30 days: expired a day ago, by the app's own clock.
        var keys = KeysAt(new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-31)));
        var (key, _) = await keys.CreateAsync(user, userKey, JustRead, "old", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Write }, Ct, expiresInDays: 30);
        var (fresh, _) = await Keys.CreateAsync(user, userKey, JustRead, "new", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Write }, Ct, expiresInDays: 30);

        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeStatusAsync(key));
        Assert.Equal(HttpStatusCode.OK, await InitializeStatusAsync(fresh));
    }

    [Fact]
    public async Task An_expired_key_presented_once_loses_its_slots_and_frees_its_name()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var (key, info) = await KeysAt(new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-31)))
            .CreateAsync(user, userKey, JustRead, "old", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read }, Ct, expiresInDays: 30);
        Assert.NotEmpty(await SlotDocs.Find(Builders<BsonDocument>.Filter.Eq("apiKey", info.Id)).ToListAsync(Ct));

        Assert.Null(await Keys.ResolveAsync(key, Ct));

        Assert.Empty(await SlotDocs.Find(Builders<BsonDocument>.Filter.Eq("apiKey", info.Id)).ToListAsync(Ct));
        Assert.False((await KeyDocs.Find(Builders<BsonDocument>.Filter.Eq("_id", info.Id)).SingleAsync(Ct)).Contains("nameId"));
        Assert.Null(await Keys.ResolveAsync(key, Ct));
    }

    [Fact]
    public async Task An_expiry_moved_or_removed_in_mongodb_makes_the_key_open_nothing()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var grant = new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read };
        var (moved, movedInfo) = await Keys.CreateAsync(user, userKey, JustRead, "moved", grant, Ct, expiresInDays: 30);
        var (removed, removedInfo) = await Keys.CreateAsync(user, userKey, JustRead, "removed", grant, Ct, expiresInDays: 30);
        Assert.Equal(HttpStatusCode.OK, await InitializeStatusAsync(moved));
        Assert.Equal(HttpStatusCode.OK, await InitializeStatusAsync(removed));

        await KeyDocs.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", movedInfo.Id), Builders<BsonDocument>.Update.Set("expiresAt", movedInfo.ExpiresAt!.Value.AddYears(10)), cancellationToken: Ct);
        await KeyDocs.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", removedInfo.Id), Builders<BsonDocument>.Update.Unset("expiresAt"), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeStatusAsync(moved));
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeStatusAsync(removed));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(31)]
    [InlineData(-30)]
    public async Task An_expiry_other_than_30_90_or_365_days_is_refused(int days)
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Keys.CreateAsync(user, userKey, JustRead, "agent", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read }, Ct, days));
        Assert.Empty(await Keys.ListAsync(user, userKey, Ct));
    }

    [Fact]
    public async Task An_expired_key_frees_its_name_and_loses_its_slots_when_the_next_key_is_made()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var grant = new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Read };
        var (_, old) = await KeysAt(new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-91)))
            .CreateAsync(user, userKey, JustRead, "laptop", grant, Ct, expiresInDays: 90);
        Assert.NotEmpty(await SlotDocs.Find(Builders<BsonDocument>.Filter.Eq("apiKey", old.Id)).ToListAsync(Ct));

        var (key, _) = await Keys.CreateAsync(user, userKey, JustRead, "laptop", grant, Ct);

        Assert.Equal(HttpStatusCode.OK, await InitializeStatusAsync(key));
        Assert.Empty(await SlotDocs.Find(Builders<BsonDocument>.Filter.Eq("apiKey", old.Id)).ToListAsync(Ct));
        var listed = await Keys.ListAsync(user, userKey, Ct);
        Assert.Equal(2, listed.Count);
        Assert.Null(listed.Single(k => k.Id == old.Id).RevokedAt);
    }

    [Fact]
    public async Task A_legacy_write_slot_opens_as_maintain()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work");
        var (key, info) = await Keys.CreateAsync(user, userKey, JustRead, "old agent", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Write }, Ct);
        var filter = Builders<BsonDocument>.Filter.Eq("apiKey", info.Id) & Builders<BsonDocument>.Filter.Eq("vault", vaults["Work"]);
        var slot = await SlotDocs.Find(filter).SingleAsync(Ct);
        var vaultKey = LegacySlot.Open(key, vaults["Work"], info.Id, slot["wrapped"].AsByteArray).VaultKey;

        // The payload as every key had it before 0.3.0: no version.
        await SlotDocs.UpdateOneAsync(
            filter, Builders<BsonDocument>.Update.Set("wrapped", LegacySlot.Seal(key, vaults["Work"], info.Id, vaultKey, "write")), cancellationToken: Ct);

        var opened = Assert.Single(await app.Services.GetRequiredService<VaultCatalog>().OpenForApiKeyAsync(info.Id, key, Ct));
        Assert.Equal(KeyRight.Maintain, opened.Right);
    }

    [Fact]
    public async Task A_new_write_slot_stays_write()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work");
        var (key, info) = await Keys.CreateAsync(user, userKey, JustRead, "agent", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Write }, Ct);
        var slot = await SlotDocs.Find(Builders<BsonDocument>.Filter.Eq("apiKey", info.Id)).SingleAsync(Ct);

        Assert.Equal(2, LegacySlot.Open(key, vaults["Work"], info.Id, slot["wrapped"].AsByteArray).V);
        var opened = Assert.Single(await app.Services.GetRequiredService<VaultCatalog>().OpenForApiKeyAsync(info.Id, key, Ct));
        Assert.Equal(KeyRight.Write, opened.Right);
    }

    [Fact]
    public async Task A_maintain_key_may_write()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work", "Diary");
        var (key, info) = await Keys.CreateAsync(
            user, userKey, JustRead, "maintainer", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Maintain, [vaults["Diary"]] = KeyRight.Read }, Ct);
        var credential = new ApiKeyCredential(info.Id, key);

        await credential.OpenWithRightsAsync(app.Services.GetRequiredService<VaultCatalog>(), Ct);

        Assert.True(credential.CanWrite("Work"));
        Assert.False(credential.CanWrite("Diary"));
    }

    [Fact]
    public async Task The_list_shows_a_legacy_write_key_as_maintain()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work", "Diary");
        var (_, created) = await Keys.CreateAsync(
            user, userKey, JustRead, "old", new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Write, [vaults["Diary"]] = KeyRight.Read }, Ct);
        // The meta as every key had it before 0.3.0: no version.
        var oldMeta = Encoding.UTF8.GetBytes($$$"""{"name":"old","vaults":{"{{{vaults["Work"]}}}":"write","{{{vaults["Diary"]}}}":"read"}}""");
        await KeyDocs.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", created.Id),
            Builders<BsonDocument>.Update.Set("meta", ApiKeys.SealMeta(userKey, user.Id, created.Id, oldMeta)),
            cancellationToken: Ct);

        var listed = Assert.Single(await Keys.ListAsync(user, userKey, Ct));

        Assert.Equal(KeyRight.Maintain, listed.Vaults[vaults["Work"]]);
        Assert.Equal(KeyRight.Read, listed.Vaults[vaults["Diary"]]);
    }

    [Fact]
    public async Task A_well_formed_but_unknown_key_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeStatusAsync("kumbu_" + new string('A', 43)));
    }

    // The permit is taken before the key is looked up in MongoDB: otherwise a burst sent at once would pass the
    // check before any of its failures counted.
    [Fact]
    public async Task A_parallel_burst_of_wrong_keys_gets_no_more_than_twenty_tries()
    {
        await using var proxied = app.WithWebHostBuilder(b => b.UseSetting("Kumbu:Proxy:Trusted", "true").UseSetting("Kumbu:PublicUrl", KumbuAppFactory.PublicUrl));
        using var http = proxied.CreateClient();
        const string ip = "203.0.113.66";

        var statuses = await Task.WhenAll(Enumerable.Range(0, 60).Select(async i =>
        {
            using var request = Initialize("kumbu_" + i.ToString("D43", System.Globalization.CultureInfo.InvariantCulture), ip);
            using var response = await http.SendAsync(request, Ct);
            return response.StatusCode;
        }));

        Assert.True(statuses.Count(s => s == HttpStatusCode.Unauthorized) <= 20, $"{statuses.Count(s => s == HttpStatusCode.Unauthorized)} tries");
        Assert.All(statuses, s => Assert.Contains(s, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests }));
    }
}

/// <summary>
/// Slot payloads of API keys and connections as the server seals them, reproduced from the outside (derivations and
/// contexts as in <see cref="VaultCatalog"/>): to read a payload's version, and to write one as it was before 0.3.0.
/// </summary>
internal static class LegacySlot
{
    internal sealed record Payload(
        [property: System.Text.Json.Serialization.JsonPropertyName("vaultKey")] byte[] VaultKey,
        [property: System.Text.Json.Serialization.JsonPropertyName("rights")] string Rights,
        [property: System.Text.Json.Serialization.JsonPropertyName("v")] int? V = null);

    private static byte[] ApiKeyKek(string apiKey, string vaultId) =>
        Kumbu.Core.Crypto.VaultCrypto.Derive(apiKey, "kumbu/v1/api-key-kek", Encoding.UTF8.GetBytes(vaultId));

    private static string ApiKeyContext(string vaultId, string keyId) => $"slot:{vaultId}:key:{keyId}";

    private static byte[] ConnectionKek(string token, string vaultId) =>
        Kumbu.Core.Crypto.VaultCrypto.Derive(token, "kumbu/v1/oauth-kek", Encoding.UTF8.GetBytes(vaultId));

    private static string ConnectionContext(string vaultId, string connectionId, string tokenId) => $"slot:{vaultId}:connection:{connectionId}:{tokenId}";

    /// <summary>An API key's slot payload as sealed before 0.3.0: vault key and right, no version.</summary>
    public static byte[] Seal(string apiKey, string vaultId, string keyId, byte[] vaultKey, string rights) =>
        Kumbu.Core.Crypto.VaultCrypto.Seal(ApiKeyKek(apiKey, vaultId), Legacy(vaultKey, rights), ApiKeyContext(vaultId, keyId));

    public static Payload Open(string apiKey, string vaultId, string keyId, byte[] wrapped) =>
        System.Text.Json.JsonSerializer.Deserialize<Payload>(Kumbu.Core.Crypto.VaultCrypto.Open(ApiKeyKek(apiKey, vaultId), wrapped, ApiKeyContext(vaultId, keyId)))!;

    /// <summary>A connection token's slot payload as sealed before 0.3.0.</summary>
    public static byte[] SealConnection(string token, string vaultId, string connectionId, string tokenId, byte[] vaultKey, string rights) =>
        Kumbu.Core.Crypto.VaultCrypto.Seal(ConnectionKek(token, vaultId), Legacy(vaultKey, rights), ConnectionContext(vaultId, connectionId, tokenId));

    public static Payload OpenConnection(string token, string vaultId, string connectionId, string tokenId, byte[] wrapped) =>
        System.Text.Json.JsonSerializer.Deserialize<Payload>(
            Kumbu.Core.Crypto.VaultCrypto.Open(ConnectionKek(token, vaultId), wrapped, ConnectionContext(vaultId, connectionId, tokenId)))!;

    // Without "v" at all, as the old record had no such property.
    private static byte[] Legacy(byte[] vaultKey, string rights) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["vaultKey"] = vaultKey, ["rights"] = rights });
}
