// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>Stage 1 of re-keying (rekeying design §2): a new user key, with every slot and keyed hash that hangs on it.</summary>
[Collection("mongo")]
public sealed class UserKeyRotationTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "tr0ub4dor and three more words";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly Argon2Kdf _kdf = new(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1));
    private readonly CapturedLog _log = new();
    private VaultCatalog? _catalog;
    private UserKeyRotation? _rotation;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private VaultCatalog Catalog() => _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private ApiKeys Keys() => new(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private Connections Connections() => new(_db, Catalog(), _time, NullLogger<Connections>.Instance);

    private UserKeyRotation Rotation() =>
        _rotation ??= new(_db, _kdf, _time, LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<UserKeyRotation>());

    private UserCatalog Users() => new(_db, _kdf, Keys(), Connections(), _time, NullLogger<UserCatalog>.Instance, Rotation());

    private Sessions Sessions() => new(_db, _time);

    private IMongoCollection<SlotDocument> Slots => _db.GetCollection<SlotDocument>("slots");

    private IMongoCollection<BsonDocument> UserDocs => _db.GetCollection<BsonDocument>("users");

    private sealed record Account(User User, byte[] Key, string RecoveryKey, DateTime ReadAt, Dictionary<string, string> Vaults);

    /// <summary>A new user, signed in, with a vault of their own for each name.</summary>
    private async Task<Account> AccountAsync(params string[] vaults)
    {
        var users = Users();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        var (_, recoveryKey) = await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, key, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        var ids = new Dictionary<string, string>();
        foreach (var name in vaults) ids[name] = (await Catalog().CreateForUserAsync(name, user.Id, key, Ct)).Id;
        return new Account(user, key, recoveryKey, readAt, ids);
    }

    private async Task<HashSet<string>> OpenIdsAsync(Account account, byte[] key) =>
        [.. (await Catalog().OpenForUserAsync(account.User.Id, key, Ct)).Select(v => v.Id)];

    private static Dictionary<string, KeyRight> Grant(string vaultId) => new() { [vaultId] = KeyRight.Write };

    [Fact]
    public async Task After_rotation_password_and_new_recovery_key_open_the_new_user_key()
    {
        var account = await AccountAsync("Arbeit");

        var rotation = await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct);

        Assert.NotNull(rotation);
        var (_, signedIn, _) = (await Users().TrySignInAsync(account.User.Username, Password, Ct))!.Value;
        Assert.NotEqual(account.Key, signedIn);
        Assert.Equal(rotation.NewUserKey, signedIn);
        var keyId = (await UserDocs.Find(new BsonDocument("_id", account.User.Id)).FirstAsync(Ct))["keyId"].AsByteArray;
        Assert.Equal(rotation.UserKeyId, keyId);

        Assert.Null(await Users().RecoverAsync(account.User.Username, account.RecoveryKey, NewPassword, Ct));
        var recovered = await Users().RecoverWithKeyAsync(account.User.Username, rotation.RecoveryKey, NewPassword, Ct);
        Assert.NotNull(recovered);
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, recovered.Value.UserKey));
    }

    [Fact]
    public async Task A_wrong_password_rotates_nothing()
    {
        var account = await AccountAsync("Arbeit");

        Assert.Null(await Rotation().RotateAsync(account.User, account.Key, "not the password at all", null, Ct));
        Assert.Null(await Rotation().RotateAsync(account.User, new byte[32], Password, null, Ct));

        Assert.Equal(account.Key, (await Users().TrySignInAsync(account.User.Username, Password, Ct))!.Value.UserKey);
    }

    [Fact]
    public async Task Vaults_api_keys_and_connections_keep_working()
    {
        var account = await AccountAsync("Arbeit", "Diary");
        var (laptop, laptopInfo) = await Keys().CreateAsync(account.User, account.Key, account.ReadAt, "laptop", Grant(account.Vaults["Arbeit"]), Ct);
        var (_, old) = await Keys().CreateAsync(account.User, account.Key, account.ReadAt, "old", Grant(account.Vaults["Arbeit"]), Ct);
        Assert.True(await Keys().RevokeAsync(account.User, old.Id, Ct));
        await Connections().CreateAsync(account.User, account.Key, account.ReadAt, "claude", Grant(account.Vaults["Diary"]), Ct);

        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct))!;
        var key = rotation.NewUserKey;
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, key));
        Assert.Equal("Arbeit", (await Catalog().OpenForUserAsync(account.User.Id, key, Ct)).Single(v => v.Id == account.Vaults["Arbeit"]).Name);
        Assert.Equal(account.Vaults["Diary"], await Catalog().FindForUserAsync(account.User.Id, key, "Diary", Ct));

        // The API key's slots never involved the user key.
        Assert.NotNull(await Keys().ResolveAsync(laptop, Ct));
        var opened = Assert.Single(await Catalog().OpenForApiKeyAsync(laptopInfo.Id, laptop, Ct));
        Assert.Equal(account.Vaults["Arbeit"], opened.Vault.Id);

        Assert.Equal(["laptop", "old"], (await Keys().ListAsync(account.User, key, Ct)).Select(k => k.Name));
        Assert.Equal(["claude"], (await Connections().ListAsync(account.User, key, Ct)).Select(c => c.Name));

        // The name hashes are under the new key: taken names stay taken, across keys and connections.
        await Assert.ThrowsAsync<ApiKeyNameTakenException>(() => Keys().CreateAsync(account.User, key, Now, "LAPTOP", Grant(account.Vaults["Arbeit"]), Ct));
        await Assert.ThrowsAsync<ApiKeyNameTakenException>(() => Keys().CreateAsync(account.User, key, Now, "claude", Grant(account.Vaults["Arbeit"]), Ct));
        await Assert.ThrowsAsync<ConnectionNameTakenException>(() => Connections().CreateAsync(account.User, key, Now, "laptop", Grant(account.Vaults["Diary"]), Ct));
        await Assert.ThrowsAsync<VaultNameTakenException>(() => Catalog().CreateForUserAsync("arbeit", account.User.Id, key, Ct, Now));
        // A revoked key's name stays free.
        await Keys().CreateAsync(account.User, key, Now, "old", Grant(account.Vaults["Arbeit"]), Ct);
    }

    [Fact]
    public async Task Only_the_current_session_survives()
    {
        var account = await AccountAsync("Arbeit");
        var kept = await Sessions().StartAsync(account.User, account.Key, account.ReadAt, Ct);
        var other = await Sessions().StartAsync(account.User, account.Key, account.ReadAt, Ct);

        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, kept, Ct))!;

        var resolved = await Sessions().ResolveAsync(kept, Ct);
        Assert.NotNull(resolved);
        Assert.Equal(rotation.NewUserKey, resolved.Value.UserKey);
        Assert.Null(await Sessions().ResolveAsync(other, Ct));

        // The kept session may still make things: its read time is after the rotation's stamp.
        await Keys().CreateAsync(resolved.Value.User, resolved.Value.UserKey, resolved.Value.CredentialReadAt, "laptop", Grant(account.Vaults["Arbeit"]), Ct);
    }

    [Fact]
    public async Task A_session_refused_since_is_not_brought_back()
    {
        var account = await AccountAsync("Arbeit");
        var secret = await Sessions().StartAsync(account.User, account.Key, account.ReadAt, Ct);
        var userSlots = _db.GetCollection<UserSlotDocument>("user_slots");
        var leftOver = await userSlots.Find(s => s.Kind == UserSlotDocument.Session).SingleAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await Users().ChangePasswordAsync(account.User, account.Key, Password, NewPassword, revokeApiKeys: false, Ct));
        await userSlots.InsertOneAsync(leftOver, cancellationToken: Ct);

        var rotation = await Rotation().RotateAsync(account.User, account.Key, NewPassword, secret, Ct);

        Assert.NotNull(rotation);
        Assert.Null(rotation.KeptSession);
        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task The_old_user_key_opens_nothing()
    {
        var account = await AccountAsync("Arbeit", "Diary");
        await Keys().CreateAsync(account.User, account.Key, account.ReadAt, "laptop", Grant(account.Vaults["Arbeit"]), Ct);
        await Connections().CreateAsync(account.User, account.Key, account.ReadAt, "claude", Grant(account.Vaults["Diary"]), Ct);

        await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct);

        Assert.Empty(await Catalog().OpenForUserAsync(account.User.Id, account.Key, Ct));
        Assert.Null(await Catalog().FindForUserAsync(account.User.Id, account.Key, "Arbeit", Ct));
        Assert.Empty(await Keys().ListAsync(account.User, account.Key, Ct));
        Assert.Empty(await Connections().ListAsync(account.User, account.Key, Ct));
        Assert.Equal(0, await Slots.CountDocumentsAsync(Builders<SlotDocument>.Filter.Eq(s => s.Lookup, VaultCatalog.UserSlotLookup(account.Key, account.User.Id)), cancellationToken: Ct));
    }

    [Theory]
    [InlineData("before-apply", false)]
    [InlineData("inside-apply", false)]
    [InlineData("before-apply", true)]
    [InlineData("inside-apply", true)]
    public async Task A_crash_before_or_inside_apply_leaves_the_old_key_working(string point, bool inRecovery)
    {
        var account = await AccountAsync("Arbeit", "Diary");
        Rotation().FaultAt = at => at == point ? throw new IOException("crash at " + at) : Task.CompletedTask;

        await Assert.ThrowsAsync<IOException>(() => inRecovery
            ? Users().RecoverAsync(account.User.Username, account.RecoveryKey, NewPassword, Ct)
            : Rotation().RotateAsync(account.User, account.Key, Password, null, Ct));

        Rotation().FaultAt = null;
        Assert.Null(await Users().TrySignInAsync(account.User.Username, NewPassword, Ct));
        var (_, key, _) = (await Users().TrySignInAsync(account.User.Username, Password, Ct))!.Value;
        Assert.Equal(account.Key, key);
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, account.Key));
        Assert.False((await UserDocs.Find(new BsonDocument("_id", account.User.Id)).FirstAsync(Ct)).Contains("rekeyPending"));
        // The old recovery key still opens the old user key, so a recovery with it still works.
        var recovered = await Users().RecoverWithKeyAsync(account.User.Username, account.RecoveryKey, NewPassword, Ct);
        Assert.NotNull(recovered);
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, recovered.Value.UserKey));
    }

    [Fact]
    public async Task A_concurrent_password_change_makes_apply_fail()
    {
        var account = await AccountAsync("Arbeit");
        var prepared = await Rotation().PrepareAsync(account.User, account.Key, Password, null, null, Ct);

        Assert.True(await Users().ChangePasswordAsync(account.User, account.Key, Password, NewPassword, revokeApiKeys: false, Ct));

        await Assert.ThrowsAsync<RotationConflictException>(() => ApplyAsync(prepared));
        var (_, key, _) = (await Users().TrySignInAsync(account.User.Username, NewPassword, Ct))!.Value;
        Assert.Equal(account.Key, key);
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, account.Key));
        var user = await UserDocs.Find(new BsonDocument("_id", account.User.Id)).FirstAsync(Ct);
        Assert.NotEqual(prepared.UserKeyId, user["keyId"].AsByteArray);
        Assert.False(user.Contains("rekeyPending"));
    }

    [Fact]
    public async Task A_key_connection_or_slot_added_between_prepare_and_apply_makes_apply_fail()
    {
        var account = await AccountAsync("Arbeit");

        var prepared = await Rotation().PrepareAsync(account.User, account.Key, Password, null, null, Ct);
        await Keys().CreateAsync(account.User, account.Key, account.ReadAt, "laptop", Grant(account.Vaults["Arbeit"]), Ct);
        await Assert.ThrowsAsync<RotationConflictException>(() => ApplyAsync(prepared));

        prepared = await Rotation().PrepareAsync(account.User, account.Key, Password, null, null, Ct);
        await Connections().CreateAsync(account.User, account.Key, account.ReadAt, "claude", Grant(account.Vaults["Arbeit"]), Ct);
        await Assert.ThrowsAsync<RotationConflictException>(() => ApplyAsync(prepared));

        // A vault creation's slot goes in before its transaction.
        prepared = await Rotation().PrepareAsync(account.User, account.Key, Password, null, null, Ct);
        await Slots.InsertOneAsync(
            new SlotDocument { Vault = "v" + Guid.NewGuid().ToString("N"), Kind = SlotDocument.User, Lookup = prepared.OldLookup, Owner = account.User.Id, Wrapped = [1], CreatedAt = Now },
            cancellationToken: Ct);
        await Assert.ThrowsAsync<RotationConflictException>(() => ApplyAsync(prepared));

        Assert.Equal(account.Key, (await Users().TrySignInAsync(account.User.Username, Password, Ct))!.Value.UserKey);
        Assert.Equal(["laptop"], (await Keys().ListAsync(account.User, account.Key, Ct)).Select(k => k.Name));
        Assert.Equal(["claude"], (await Connections().ListAsync(account.User, account.Key, Ct)).Select(c => c.Name));
    }

    private async Task ApplyAsync(PreparedRotation prepared)
    {
        using var session = await _db.Client.StartSessionAsync(cancellationToken: Ct);
        session.StartTransaction();
        try
        {
            await Rotation().ApplyAsync(session, prepared, Ct);
            await session.CommitTransactionAsync(Ct);
        }
        catch
        {
            await session.AbortTransactionAsync(CancellationToken.None);
            throw;
        }
    }

    [Fact]
    public async Task A_vault_created_during_rotation_is_not_lost()
    {
        var account = await AccountAsync("Arbeit");

        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct))!;

        // A request that read the session before the rotation, creating a vault with the old key after it: 401.
        await Assert.ThrowsAsync<CredentialsChangedException>(() => Catalog().CreateForUserAsync("Neu", account.User.Id, account.Key, Ct, account.ReadAt));
        Assert.Equal(0, await Slots.CountDocumentsAsync(Builders<SlotDocument>.Filter.Eq(s => s.Lookup, VaultCatalog.UserSlotLookup(account.Key, account.User.Id)), cancellationToken: Ct));

        _time.Advance(TimeSpan.FromSeconds(1));
        var created = await Catalog().CreateForUserAsync("Neu", account.User.Id, rotation.NewUserKey, Ct, Now);
        Assert.Contains(created.Id, await OpenIdsAsync(account, rotation.NewUserKey));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition never held.");
            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task A_sign_in_during_apply_gets_no_session_with_the_old_key()
    {
        var account = await AccountAsync("Arbeit");
        // Ready before the transaction: building its indexes would wait for it.
        var users = Users();
        Assert.NotNull(await users.TrySignInAsync(account.User.Username, Password, Ct));
        Task<(User User, byte[] UserKey, DateTime CredentialReadAt)?>? signIn = null;
        Rotation().FaultAt = async at =>
        {
            if (at != "inside-apply") return;
            // After the stamp, a sign-in reads the old password slot outside the transaction, and finishes after the commit.
            _time.Advance(TimeSpan.FromSeconds(1));
            var runs = _kdf.Runs;
            signIn = Task.Run(() => users.TrySignInAsync(account.User.Username, Password, Ct), Ct);
            await WaitForAsync(() => _kdf.Runs > runs || signIn.IsCompleted);
        };

        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct))!;

        var (user, key, readAt) = (await signIn!)!.Value;
        Assert.Equal(account.Key, key);
        var changedAt = (await UserDocs.Find(new BsonDocument("_id", account.User.Id)).FirstAsync(Ct))["credentialsChangedAt"].ToUniversalTime();
        Assert.True(readAt > changedAt);
        await Assert.ThrowsAsync<CredentialsChangedException>(() => Sessions().StartAsync(user, key, readAt, Ct));
        Assert.Equal(0, await _db.GetCollection<UserSlotDocument>("user_slots").CountDocumentsAsync(s => s.Kind == UserSlotDocument.Session, cancellationToken: Ct));
        await Assert.ThrowsAsync<CredentialsChangedException>(() => Catalog().CreateForUserAsync("Neu", user.Id, key, Ct, readAt));
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, rotation.NewUserKey));
    }

    /// <summary>A catalog whose Argon2 waits at <paramref name="gate"/>: a password check holds there, after its slot read.</summary>
    private (UserCatalog Users, Argon2Kdf Kdf) GatedUsers(SemaphoreSlim gate)
    {
        var kdf = new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1), gate);
        return (new UserCatalog(_db, kdf, Keys(), Connections(), _time, NullLogger<UserCatalog>.Instance, Rotation()), kdf);
    }

    [Fact]
    public async Task A_password_change_racing_a_rotation_fails_and_changes_nothing()
    {
        var account = await AccountAsync("Arbeit");
        var kept = await Sessions().StartAsync(account.User, account.Key, account.ReadAt, Ct);
        await Keys().CreateAsync(account.User, account.Key, account.ReadAt, "laptop", Grant(account.Vaults["Arbeit"]), Ct);
        using var gate = new SemaphoreSlim(0, int.MaxValue);
        var (users, kdf) = GatedUsers(gate);

        var change = Task.Run(() => users.ChangePasswordAsync(account.User, account.Key, Password, NewPassword, revokeApiKeys: true, Ct), Ct);
        await WaitForAsync(() => kdf.Runs > 0);
        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, kept, Ct))!;
        gate.Release(100);

        await Assert.ThrowsAsync<PasswordConflictException>(() => change);
        Assert.Null(await Users().TrySignInAsync(account.User.Username, NewPassword, Ct));
        Assert.Equal(rotation.NewUserKey, (await Users().TrySignInAsync(account.User.Username, Password, Ct))!.Value.UserKey);
        // Failed before anything signed the user out or revoked a key.
        Assert.Equal(rotation.NewUserKey, (await Sessions().ResolveAsync(kept, Ct))!.Value.UserKey);
        Assert.Null(Assert.Single(await Keys().ListAsync(account.User, rotation.NewUserKey, Ct)).RevokedAt);
    }

    [Fact]
    public async Task A_new_recovery_key_racing_a_rotation_fails_and_the_rotations_key_works()
    {
        var account = await AccountAsync("Arbeit");
        using var gate = new SemaphoreSlim(0, int.MaxValue);
        var (users, kdf) = GatedUsers(gate);

        var regenerate = Task.Run(() => users.RegenerateRecoveryKeyAsync(account.User.Id, account.Key, Password, Ct), Ct);
        await WaitForAsync(() => kdf.Runs > 0);
        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct))!;
        gate.Release(100);

        await Assert.ThrowsAsync<PasswordConflictException>(() => regenerate);
        Assert.Equal(rotation.NewUserKey, (await Users().TrySignInAsync(account.User.Username, Password, Ct))!.Value.UserKey);
        var recovered = await Users().RecoverWithKeyAsync(account.User.Username, rotation.RecoveryKey, NewPassword, Ct);
        Assert.NotNull(recovered);
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, recovered.Value.UserKey));
    }

    [Fact]
    public async Task A_recovery_race_still_lets_one_through()
    {
        for (var round = 0; round < 3; round++)
        {
            var account = await AccountAsync("Arbeit", "Diary");
            var sessions = new List<string>();
            for (var i = 0; i < 4; i++) sessions.Add(await Sessions().StartAsync(account.User, account.Key, account.ReadAt, Ct));
            var first = "first new passphrase " + round;
            var second = "second new passphrase " + round;

            // Requests with the old sessions meanwhile: refused after the stamp, their slots deleted under the recovery's feet.
            var resolving = Task.Run(
                async () =>
                {
                    for (var i = 0; i < 10; i++)
                    {
                        foreach (var secret in sessions) await Sessions().ResolveAsync(secret, Ct);
                    }
                },
                Ct);
            var results = await Task.WhenAll(
                Task.Run(() => Users().RecoverWithKeyAsync(account.User.Username, account.RecoveryKey, first, Ct), Ct),
                Task.Run(() => Users().RecoverWithKeyAsync(account.User.Username, account.RecoveryKey, second, Ct), Ct));
            await resolving;

            var won = Assert.Single(results, r => r is not null)!.Value;
            var winner = results[0] is not null ? first : second;
            var loser = results[0] is not null ? second : first;
            var (_, key, _) = (await Users().TrySignInAsync(account.User.Username, winner, Ct))!.Value;
            Assert.Equal(won.UserKey, key);
            Assert.NotEqual(account.Key, key);
            Assert.Null(await Users().TrySignInAsync(account.User.Username, loser, Ct));
            Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, key));
            Assert.Null(await Users().RecoverAsync(account.User.Username, account.RecoveryKey, Password, Ct));
            Assert.NotNull(await Users().RecoverAsync(account.User.Username, won.RecoveryKey, Password, Ct));
        }
    }

    [Fact]
    public async Task An_old_orphan_slot_is_removed_and_a_young_one_retries()
    {
        var account = await AccountAsync("Arbeit");
        // What a creation leaves between its slot and its vault document (VaultCatalog.CreateForUserAsync).
        var orphanVault = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        var orphan = new SlotDocument
        {
            Vault = orphanVault,
            Kind = SlotDocument.User,
            Lookup = VaultCatalog.UserSlotLookup(account.Key, account.User.Id),
            Owner = account.User.Id,
            NameId = [1, 2, 3],
            Wrapped = VaultCrypto.Seal(
                VaultCrypto.Derive(account.Key, "kumbu/v1/user-slot-kek", System.Text.Encoding.UTF8.GetBytes(orphanVault)), VaultCrypto.NewKey(), $"slot:{orphanVault}:user"),
            CreatedAt = Now,
        };
        await Slots.InsertOneAsync(orphan, cancellationToken: Ct);

        await Assert.ThrowsAsync<RotationConflictException>(() => Rotation().RotateAsync(account.User, account.Key, Password, null, Ct));
        Assert.Equal(3, _log.Lines.Count(l => l.Contains("concurrent change", StringComparison.Ordinal)));
        Assert.Equal(account.Key, (await Users().TrySignInAsync(account.User.Username, Password, Ct))!.Value.UserKey);

        _time.Advance(TimeSpan.FromMinutes(2));
        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct))!;

        Assert.Null(await Slots.Find(s => s.Id == orphan.Id).FirstOrDefaultAsync(Ct));
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, rotation.NewUserKey));
    }

    [Fact]
    public async Task A_slot_with_a_damaged_meta_is_rewrapped_without_its_name()
    {
        var account = await AccountAsync("Arbeit", "Diary");
        var damaged = account.Vaults["Diary"];
        var vaults = _db.GetCollection<VaultDocument>("vaults");
        var meta = (await vaults.Find(v => v.Id == damaged).FirstAsync(Ct)).Meta;
        meta[^1] ^= 1;
        await vaults.UpdateOneAsync(v => v.Id == damaged, Builders<VaultDocument>.Update.Set(v => v.Meta, meta), cancellationToken: Ct);

        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct))!;

        var slot = await Slots.Find(s => s.Vault == damaged && s.Kind == SlotDocument.User).FirstAsync(Ct);
        Assert.Equal(rotation.NewLookup, slot.Lookup);
        Assert.Null(slot.NameId);
        Assert.Single(await Catalog().UserVaultKeysAsync(account.User.Id, rotation.NewUserKey, [damaged], "A test", Ct));
        Assert.Equal(account.Vaults["Arbeit"], await Catalog().FindForUserAsync(account.User.Id, rotation.NewUserKey, "Arbeit", Ct));
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains(damaged, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_slot_that_does_not_open_is_left_as_it_is()
    {
        var account = await AccountAsync("Arbeit");
        var stray = new SlotDocument
        {
            Vault = account.Vaults["Arbeit"],
            Kind = SlotDocument.User,
            Lookup = VaultCatalog.UserSlotLookup(account.Key, account.User.Id),
            Owner = account.User.Id,
            Wrapped = [1, 2, 3],
            CreatedAt = Now,
        };
        await Slots.InsertOneAsync(stray, cancellationToken: Ct);

        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct))!;

        var left = await Slots.Find(s => s.Id == stray.Id).FirstAsync(Ct);
        Assert.Equal(stray.Lookup, left.Lookup);
        Assert.Equal(stray.Wrapped, left.Wrapped);
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, rotation.NewUserKey));
    }

    [Fact]
    public async Task A_third_conflict_in_a_recovery_is_retryable_not_wrong_key()
    {
        var account = await AccountAsync("Arbeit");
        Rotation().FaultAt = at => at == "inside-apply" ? throw new RotationConflictException() : Task.CompletedTask;

        await Assert.ThrowsAsync<RecoveryConflictException>(() => Users().RecoverWithKeyAsync(account.User.Username, account.RecoveryKey, NewPassword, Ct));

        Rotation().FaultAt = null;
        Assert.Equal(account.Key, (await Users().TrySignInAsync(account.User.Username, Password, Ct))!.Value.UserKey);
        var recovered = await Users().RecoverWithKeyAsync(account.User.Username, account.RecoveryKey, NewPassword, Ct);
        Assert.NotNull(recovered);
        Assert.Equal(recovered.Value.UserKey, (await Users().TrySignInAsync(account.User.Username, NewPassword, Ct))!.Value.UserKey);
    }

    [Fact]
    public async Task Rotation_records_every_vault_as_pending()
    {
        var account = await AccountAsync("Arbeit", "Diary", "Rezepte");

        var rotation = (await Rotation().RotateAsync(account.User, account.Key, Password, null, Ct))!;

        var pending = (await UserDocs.Find(new BsonDocument("_id", account.User.Id)).FirstAsync(Ct))["rekeyPending"].AsBsonArray.Select(v => v.AsString);
        Assert.Equal(account.Vaults.Values.Order(), pending.Order());

        // A recovery rotates too, and adds nothing twice.
        var recovered = (await Users().RecoverWithKeyAsync(account.User.Username, rotation.RecoveryKey, NewPassword, Ct))!.Value;
        pending = (await UserDocs.Find(new BsonDocument("_id", account.User.Id)).FirstAsync(Ct))["rekeyPending"].AsBsonArray.Select(v => v.AsString);
        Assert.Equal(account.Vaults.Values.Order(), pending.Order());
        Assert.Equal(account.Vaults.Values.ToHashSet(), await OpenIdsAsync(account, recovered.UserKey));
    }
}
