// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>Connections: named like API keys, for chosen vaults with a right each, waiting for an app to authorise.</summary>
[Collection("mongo")]
public sealed class ConnectionTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private VaultCatalog? _catalog;

    private VaultCatalog Catalog() => _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private ApiKeys Keys() => new(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private Connections Connections() => new(_db, Catalog(), _time, NullLogger<Connections>.Instance);

    /// <summary>A sign-in just now: these tests aren't about a credentials change since the session began.</summary>
    private DateTime JustRead => _time.GetUtcNow().UtcDateTime;

    /// <summary>A new user, signed in, with a vault of their own for each name: the user, their user key and the vault ids by name.</summary>
    private async Task<(User User, byte[] UserKey, Dictionary<string, string> Vaults)> UserWithVaultsAsync(params string[] vaults)
    {
        var users = new UserCatalog(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), Keys(), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance), _time, NullLogger<UserCatalog>.Instance);
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, key, _) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        var ids = new Dictionary<string, string>();
        foreach (var name in vaults) ids[name] = (await Catalog().CreateForUserAsync(name, user.Id, key, Ct)).Id;
        return (user, key, ids);
    }

    private static Dictionary<string, KeyRight> Grant(string vaultId, KeyRight right = KeyRight.Write) => new() { [vaultId] = right };

    [Fact]
    public async Task Creates_a_connection_with_rights_and_lists_it_as_waiting()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work", "Diary");

        var created = await Connections().CreateAsync(user, userKey, JustRead, "Work", Grant(vaults["Work"]), Ct);

        Assert.Equal(16, created.Id.Length);
        Assert.Matches("^[a-z2-7]{16}$", created.Id);
        var listed = Assert.Single(await Connections().ListAsync(user, userKey, Ct));
        Assert.Equal(created.Id, listed.Id);
        Assert.Equal("Work", listed.Name);
        Assert.Equal(ConnectionState.Waiting, listed.State);
        Assert.Null(listed.Reason);
        Assert.Null(listed.Client);
        Assert.Null(listed.ExpiresAt);
        Assert.Equal(KeyRight.Write, Assert.Single(listed.Vaults, v => v.Key == vaults["Work"]).Value);
        Assert.DoesNotContain(vaults["Diary"], listed.Vaults.Keys);
    }

    [Fact]
    public void Ids_are_new_each_time()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => Kumbu.Server.Accounts.Connections.NewId()).ToHashSet();

        Assert.Equal(100, ids.Count);
        Assert.All(ids, id => Assert.Matches("^[a-z2-7]{16}$", id));
    }

    [Fact]
    public async Task A_name_is_unique_across_api_keys_and_connections()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var grant = Grant(vaults["Notes"]);

        await Keys().CreateAsync(user, userKey, JustRead, "laptop", grant, Ct);
        var taken = await Assert.ThrowsAsync<ConnectionNameTakenException>(() => Connections().CreateAsync(user, userKey, JustRead, "Laptop", grant, Ct));
        Assert.Equal("You already have an API key or connection named 'Laptop'.", taken.Message);

        var connection = await Connections().CreateAsync(user, userKey, JustRead, "Work", grant, Ct);
        var keyTaken = await Assert.ThrowsAsync<ApiKeyNameTakenException>(() => Keys().CreateAsync(user, userKey, JustRead, "WORK", grant, Ct));
        Assert.Equal("You already have an API key or connection named 'WORK'.", keyTaken.Message);
        await Assert.ThrowsAsync<ConnectionNameTakenException>(() => Connections().CreateAsync(user, userKey, JustRead, " Work ", grant, Ct));

        Assert.True(await Connections().RevokeAsync(user, connection.Id, Ct));
        await Keys().CreateAsync(user, userKey, JustRead, "WORK", grant, Ct);
    }

    [Fact]
    public async Task Another_user_may_use_the_same_name()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var (other, otherKey, otherVaults) = await UserWithVaultsAsync("Notes");

        await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);
        await Connections().CreateAsync(other, otherKey, JustRead, "phone", Grant(otherVaults["Notes"]), Ct);
        await Keys().CreateAsync(other, otherKey, JustRead, "laptop", Grant(otherVaults["Notes"]), Ct);
        await Connections().CreateAsync(user, userKey, JustRead, "laptop", Grant(vaults["Notes"]), Ct);
    }

    [Fact]
    public async Task Two_concurrent_creations_with_one_name_leave_one()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var grant = Grant(vaults["Notes"]);

        for (var round = 0; round < 5; round++)
        {
            var name = $"twin{round}";
            var key = Keys().CreateAsync(user, userKey, JustRead, name, grant, Ct);
            var connection = Connections().CreateAsync(user, userKey, JustRead, name, grant, Ct);
            var results = await Task.WhenAll(Outcome(key), Outcome(connection));

            Assert.Single(results, r => r is null);
            Assert.Single(results, r => r is ApiKeyNameTakenException or ConnectionNameTakenException);
        }

        var keys = await Keys().ListAsync(user, userKey, Ct);
        var connections = await Connections().ListAsync(user, userKey, Ct);
        Assert.Equal(5, keys.Count + connections.Count);
        Assert.Equal(5, keys.Select(k => k.Name).Concat(connections.Select(c => c.Name)).Distinct().Count());

        static async Task<Exception?> Outcome(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("seed")]
    [InlineData("Claude (laptop)")]
    [InlineData("chris​")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Naming_rules_are_the_api_keys_rules(string name)
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Connections().CreateAsync(user, userKey, JustRead, name, Grant(vaults["Notes"]), Ct));
        Assert.DoesNotContain("API key", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await Connections().ListAsync(user, userKey, Ct));
    }

    [Fact]
    public async Task A_connection_cannot_be_named_like_its_user_and_has_at_most_64_characters()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var grant = Grant(vaults["Notes"]);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Connections().CreateAsync(user, userKey, JustRead, user.Username.ToUpperInvariant(), grant, Ct));
        Assert.StartsWith("A connection can't be named like you", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => Connections().CreateAsync(user, userKey, JustRead, new string('c', 65), grant, Ct));
        Assert.Equal(new string('c', 64), (await Connections().CreateAsync(user, userKey, JustRead, " " + new string('c', 64) + " ", grant, Ct)).Name);
    }

    [Fact]
    public async Task A_connection_needs_a_vault_of_the_user()
    {
        var (user, userKey, _) = await UserWithVaultsAsync("Notes");
        var (_, _, theirs) = await UserWithVaultsAsync("Secret");

        await Assert.ThrowsAsync<ArgumentException>(() => Connections().CreateAsync(user, userKey, JustRead, "empty", new Dictionary<string, KeyRight>(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Connections().CreateAsync(user, userKey, JustRead, "thief", Grant(theirs["Secret"], KeyRight.Read), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Connections().CreateAsync(user, userKey, JustRead, "ghost", Grant("no-such-vault", KeyRight.Read), Ct));
        Assert.Empty(await Connections().ListAsync(user, userKey, Ct));
    }

    [Fact]
    public async Task A_session_older_than_the_last_credentials_change_cannot_create_one()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        await _db.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.CredentialsChangedAt, JustRead), cancellationToken: Ct);

        await Assert.ThrowsAsync<CredentialsChangedException>(() =>
            Connections().CreateAsync(user, userKey, JustRead.AddSeconds(-1), "late", Grant(vaults["Notes"]), Ct));
        Assert.Empty(await Connections().ListAsync(user, userKey, Ct));
    }

    [Fact]
    public async Task A_stale_session_or_an_inactive_user_cannot_change_the_rights()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var created = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);
        var users = _db.GetCollection<UserDocument>("users");

        await users.UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.CredentialsChangedAt, JustRead), cancellationToken: Ct);
        await Assert.ThrowsAsync<CredentialsChangedException>(() =>
            Connections().SetVaultsAsync(user, userKey, JustRead.AddSeconds(-1), created.Id, Grant(vaults["Notes"], KeyRight.Read), Ct));

        await users.UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.Active, false), cancellationToken: Ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<CredentialsChangedException>(() =>
            Connections().SetVaultsAsync(user, userKey, JustRead, created.Id, Grant(vaults["Notes"], KeyRight.Read), Ct));

        // Unchanged, tokens and all.
        Assert.Equal(KeyRight.Write, Assert.Single(await Connections().ListAsync(user, userKey, Ct)).Vaults[vaults["Notes"]]);
    }

    [Fact]
    public async Task Creating_one_is_counted_on_the_user()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");

        await Connections().CreateAsync(user, userKey, JustRead, "one", Grant(vaults["Notes"]), Ct);
        await Connections().CreateAsync(user, userKey, JustRead, "two", Grant(vaults["Notes"]), Ct);

        var doc = await _db.GetCollection<BsonDocument>("users").Find(Builders<BsonDocument>.Filter.Eq("_id", user.Id)).SingleAsync(Ct);
        Assert.Equal(2, doc["connectionsCreated"].ToInt64());
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task Expiry_other_than_30_90_or_365_days_is_refused(int days)
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");

        await Assert.ThrowsAsync<ArgumentException>(() => Connections().CreateAsync(user, userKey, JustRead, "agent", Grant(vaults["Notes"]), Ct, days));
        Assert.Empty(await Connections().ListAsync(user, userKey, Ct));
    }

    [Fact]
    public async Task Expiry_is_30_90_or_365_days()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");

        var created = await Connections().CreateAsync(user, userKey, JustRead, "agent", Grant(vaults["Notes"]), Ct, expiresInDays: 30);

        var expiresAt = Assert.IsType<DateTime>(created.ExpiresAt);
        Assert.InRange(expiresAt - _time.GetUtcNow().UtcDateTime, TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(30));
        Assert.Equal(ConnectionState.Waiting, Assert.Single(await Connections().ListAsync(user, userKey, Ct)).State);

        _time.Advance(TimeSpan.FromDays(30));

        var expired = Assert.Single(await Connections().ListAsync(user, userKey, Ct));
        Assert.Equal(ConnectionState.Expired, expired.State);
        Assert.Equal("expired", expired.Reason);
        Assert.Null(await Connections().SetVaultsAsync(user, userKey, JustRead, created.Id, Grant(vaults["Notes"], KeyRight.Read), Ct));
    }

    [Fact]
    public async Task An_expired_connection_frees_its_name()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var grant = Grant(vaults["Notes"]);
        await Connections().CreateAsync(user, userKey, JustRead, "phone", grant, Ct, expiresInDays: 30);
        _time.Advance(TimeSpan.FromDays(31));

        await Keys().CreateAsync(user, userKey, JustRead, "Phone", grant, Ct);
        await Keys().RevokeAsync(user, (await Keys().ListAsync(user, userKey, Ct)).Single().Id, Ct);
        await Connections().CreateAsync(user, userKey, JustRead, "PHONE", grant, Ct);

        var listed = await Connections().ListAsync(user, userKey, Ct);
        Assert.Equal([ConnectionState.Expired, ConnectionState.Waiting], listed.Select(c => c.State));
    }

    [Fact]
    public async Task Revoking_is_final_and_frees_the_name()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var created = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);

        Assert.True(await Connections().RevokeAsync(user, created.Id, Ct));

        var revoked = Assert.Single(await Connections().ListAsync(user, userKey, Ct));
        Assert.Equal(ConnectionState.Revoked, revoked.State);
        Assert.Equal(ConnectionReasons.Revoked, revoked.Reason);
        Assert.NotNull(revoked.RevokedAt);
        Assert.False(await Connections().RevokeAsync(user, created.Id, Ct));
        Assert.Null(await Connections().SetVaultsAsync(user, userKey, JustRead, created.Id, Grant(vaults["Notes"], KeyRight.Read), Ct));
        await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);
    }

    [Fact]
    public async Task Another_user_cannot_revoke_or_change_a_connection()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var (other, otherKey, otherVaults) = await UserWithVaultsAsync("Notes");
        var created = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);

        Assert.False(await Connections().RevokeAsync(other, created.Id, Ct));
        Assert.Null(await Connections().SetVaultsAsync(other, otherKey, JustRead, created.Id, Grant(otherVaults["Notes"]), Ct));
        Assert.Equal(ConnectionState.Waiting, Assert.Single(await Connections().ListAsync(user, userKey, Ct)).State);
        Assert.Empty(await Connections().ListAsync(other, otherKey, Ct));
    }

    [Fact]
    public async Task Changing_the_rights_stores_them_and_needs_vaults_of_the_user()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Work", "Diary");
        var (_, _, theirs) = await UserWithVaultsAsync("Secret");
        var created = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Work"]), Ct);

        var changed = await Connections().SetVaultsAsync(
            user, userKey, JustRead, created.Id, new Dictionary<string, KeyRight> { [vaults["Work"]] = KeyRight.Read, [vaults["Diary"]] = KeyRight.Write }, Ct);

        Assert.NotNull(changed);
        var listed = Assert.Single(await Connections().ListAsync(user, userKey, Ct));
        Assert.Equal("phone", listed.Name);
        Assert.Equal(KeyRight.Read, listed.Vaults[vaults["Work"]]);
        Assert.Equal(KeyRight.Write, listed.Vaults[vaults["Diary"]]);
        var doc = await _db.GetCollection<BsonDocument>("connections").Find(Builders<BsonDocument>.Filter.Eq("_id", created.Id)).SingleAsync(Ct);
        Assert.Equal(new[] { vaults["Work"], vaults["Diary"] }.Order(), doc["vaults"].AsBsonArray.Select(v => v.AsString).Order());

        await Assert.ThrowsAsync<ArgumentException>(() => Connections().SetVaultsAsync(user, userKey, JustRead, created.Id, new Dictionary<string, KeyRight>(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Connections().SetVaultsAsync(user, userKey, JustRead, created.Id, Grant(theirs["Secret"]), Ct));
        Assert.Equal(2, Assert.Single(await Connections().ListAsync(user, userKey, Ct)).Vaults.Count);
    }

    [Fact]
    public async Task Changing_the_rights_of_an_authorised_connection_needs_a_reconnect()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var created = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);
        // What the token endpoint (a later task) marks once a code is exchanged.
        await _db.GetCollection<ConnectionDocument>("connections").UpdateOneAsync(
            c => c.Id == created.Id, Builders<ConnectionDocument>.Update.Set(c => c.AuthorisedAt, JustRead), cancellationToken: Ct);

        await Connections().SetVaultsAsync(user, userKey, JustRead, created.Id, Grant(vaults["Notes"], KeyRight.Read), Ct);

        var listed = Assert.Single(await Connections().ListAsync(user, userKey, Ct));
        Assert.Equal(ConnectionState.NeedsReconnect, listed.State);
        Assert.Equal(ConnectionReasons.RightsChanged, listed.Reason);
    }

    [Fact]
    public async Task Revoking_all_tokens_leaves_authorised_connections_needing_a_reconnect_and_others_waiting()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var authorised = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);
        await Connections().CreateAsync(user, userKey, JustRead, "tablet", Grant(vaults["Notes"]), Ct);
        await _db.GetCollection<ConnectionDocument>("connections").UpdateOneAsync(
            c => c.Id == authorised.Id, Builders<ConnectionDocument>.Update.Set(c => c.AuthorisedAt, JustRead), cancellationToken: Ct);

        Assert.Equal(2, await Connections().RevokeAllTokensAsync(user, ConnectionReasons.PasswordChanged, Ct));

        var listed = await Connections().ListAsync(user, userKey, Ct);
        Assert.Equal((ConnectionState.NeedsReconnect, ConnectionReasons.PasswordChanged), listed.Where(c => c.Name == "phone").Select(c => (c.State, c.Reason)).Single());
        Assert.Equal((ConnectionState.Waiting, (string?)null), listed.Where(c => c.Name == "tablet").Select(c => (c.State, c.Reason)).Single());
    }

    [Fact]
    public async Task The_client_is_sealed_into_the_meta_and_listed()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var created = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);

        await Connections().SetClientAsync(user, userKey, created.Id, new ConnectedClient("Claude", "claude.ai", true), Ct);

        Assert.Equal(new ConnectedClient("Claude", "claude.ai", true), Assert.Single(await Connections().ListAsync(user, userKey, Ct)).Client);
        var doc = (await Connections().FindAsync(created.Id, Ct))!;
        Assert.Equal("phone", Connections().OpenMeta(user, userKey, doc)!.Name);
        Assert.Null(Connections().OpenMeta(user, RandomKey(), doc));
        var raw = (await _db.GetCollection<BsonDocument>("connections").Find(Builders<BsonDocument>.Filter.Eq("_id", created.Id)).SingleAsync(Ct)).ToJson();
        Assert.DoesNotContain("claude.ai", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Claude", raw, StringComparison.Ordinal);

        static byte[] RandomKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    }

    [Fact]
    public async Task A_meta_moved_to_another_connection_does_not_open()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        var first = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);
        var second = await Connections().CreateAsync(user, userKey, JustRead, "tablet", Grant(vaults["Notes"]), Ct);
        var docs = _db.GetCollection<BsonDocument>("connections");
        var meta = (await docs.Find(Builders<BsonDocument>.Filter.Eq("_id", first.Id)).SingleAsync(Ct))["meta"];

        await docs.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", second.Id), Builders<BsonDocument>.Update.Set("meta", meta), cancellationToken: Ct);

        Assert.Equal(first.Id, Assert.Single(await Connections().ListAsync(user, userKey, Ct)).Id);
    }

    [Fact]
    public async Task A_legacy_write_connection_slot_opens_as_maintain()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes", "Diary");
        var created = await Connections().CreateAsync(user, userKey, JustRead, "phone", Grant(vaults["Notes"]), Ct);
        const string tokenId = "token-1";
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var (slots, vaultKeys) = await Catalog().ConnectionSlotsFromUserAsync(
            user.Id, userKey, created.Id, tokenId, token, new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Write, [vaults["Diary"]] = KeyRight.Write }, null, Ct);
        Assert.All(slots, s => Assert.Equal(2, LegacySlot.OpenConnection(token, s.Vault, created.Id, tokenId, s.Wrapped).V));
        // Notes as a slot sealed before 0.3.0 (no version); Diary as one sealed now.
        var legacy = slots.Single(s => s.Vault == vaults["Notes"]);
        legacy.Wrapped = LegacySlot.SealConnection(token, legacy.Vault, created.Id, tokenId, vaultKeys[legacy.Vault].Key, "write");
        await _db.GetCollection<SlotDocument>("slots").InsertManyAsync(slots, cancellationToken: Ct);

        var opened = await Catalog().OpenConnectionSlotsAsync(created.Id, tokenId, token, Ct);

        Assert.Equal(KeyRight.Maintain, opened.Single(k => k.Vault == vaults["Notes"]).Right);
        Assert.Equal(KeyRight.Write, opened.Single(k => k.Vault == vaults["Diary"]).Right);
    }

    [Fact]
    public async Task A_legacy_write_connection_meta_lists_as_maintain()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes", "Diary");
        var created = await Connections().CreateAsync(
            user, userKey, JustRead, "phone", new Dictionary<string, KeyRight> { [vaults["Notes"]] = KeyRight.Write, [vaults["Diary"]] = KeyRight.Read }, Ct);
        Assert.Equal(KeyRight.Write, Assert.Single(await Connections().ListAsync(user, userKey, Ct)).Vaults[vaults["Notes"]]);
        // The meta as every connection had it before 0.3.0: no version.
        var oldMeta = Encoding.UTF8.GetBytes($$$"""{"name":"phone","vaults":{"{{{vaults["Notes"]}}}":"write","{{{vaults["Diary"]}}}":"read"}}""");
        var sealedMeta = Kumbu.Core.Crypto.VaultCrypto.Seal(
            Kumbu.Core.Crypto.VaultCrypto.Derive(userKey, "kumbu/v1/connection-meta", Encoding.UTF8.GetBytes(user.Id)), oldMeta, $"connection:{created.Id}:meta");
        await _db.GetCollection<BsonDocument>("connections").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", created.Id), Builders<BsonDocument>.Update.Set("meta", sealedMeta), cancellationToken: Ct);

        var listed = Assert.Single(await Connections().ListAsync(user, userKey, Ct));

        Assert.Equal(KeyRight.Maintain, listed.Vaults[vaults["Notes"]]);
        Assert.Equal(KeyRight.Read, listed.Vaults[vaults["Diary"]]);
    }

    [Fact]
    public async Task Nothing_readable_about_a_connection_reaches_the_database()
    {
        var (user, userKey, vaults) = await UserWithVaultsAsync("Notes");
        await Connections().CreateAsync(user, userKey, JustRead, "Work laptop", Grant(vaults["Notes"]), Ct);

        var dump = string.Concat((await _db.GetCollection<BsonDocument>("connections").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct)).Select(d => d.ToJson()));

        Assert.Contains(vaults["Notes"], dump, StringComparison.Ordinal);
        Assert.DoesNotContain("Work", dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("laptop", dump, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("write", dump, StringComparison.OrdinalIgnoreCase);
    }
}
