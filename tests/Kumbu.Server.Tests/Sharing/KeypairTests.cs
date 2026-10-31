// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>Every user's keypair (collab design §5.1): created lazily, once, the private key only ever sealed.</summary>
[Collection("mongo")]
public sealed class KeypairTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturedLog _log = new();
    private VaultCatalog? _catalog;

    /// <summary>One catalog, as one server process has it.</summary>
    private VaultCatalog Catalog() => _catalog ??= NewProcess();

    /// <summary>A catalog as a second server process has it: nothing in memory.</summary>
    private VaultCatalog NewProcess() => new(_db, Options.Create(new KumbuOptions()), _time, LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<VaultCatalog>());

    private Memberships Memberships() =>
        new(_db, Catalog(), Options.Create(new KumbuOptions()), _time, LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<Memberships>());

    private UserCatalog Users() =>
        new(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)),
            new ApiKeys(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance), _time,
            NullLogger<UserCatalog>.Instance);

    private IMongoCollection<UserDocument> UserDocs => _db.GetCollection<UserDocument>("users");

    private Task<UserDocument> UserAsync(string id) => UserDocs.Find(u => u.Id == id).SingleAsync(Ct);

    /// <summary>An account as it was before keypairs: created, then stripped of its keypair.</summary>
    private async Task<User> OldAccountAsync(string username)
    {
        var (user, _) = await Users().CreateAsync(username, Password, Roles.User, Ct);
        await UserDocs.UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Unset(u => u.PublicKey).Unset(u => u.PrivateKey), cancellationToken: Ct);
        Assert.Null((await UserAsync(user.Id)).PublicKey);
        return user;
    }

    private async Task<byte[]> SignInAsync(string username, UserCatalog? users = null) => (await (users ?? Users()).TrySignInAsync(username, Password, Ct))!.Value.UserKey;

    [Fact]
    public async Task A_new_account_has_a_keypair()
    {
        var (user, _) = await Users().CreateAsync("bob", Password, Roles.User, Ct);
        var doc = await UserAsync(user.Id);

        Assert.NotNull(doc.PublicKey);
        MemberSlotCrypto.RequireOnCurve(doc.PublicKey);
        using var key = MemberKeys.OpenPrivate(doc, await SignInAsync("bob"));
        Assert.Equal(doc.PublicKey, MemberKeys.PublicFromPrivate(key));
        // Signing in keeps it.
        Assert.Equal(doc.PublicKey, (await UserAsync(user.Id)).PublicKey);
    }

    [Fact]
    public async Task Signing_in_creates_a_keypair_once()
    {
        var user = await OldAccountAsync("bob");

        var userKey = await SignInAsync("bob");
        var first = await UserAsync(user.Id);
        Assert.NotNull(first.PublicKey);
        using (var key = MemberKeys.OpenPrivate(first, userKey)) Assert.Equal(first.PublicKey, MemberKeys.PublicFromPrivate(key));

        await SignInAsync("bob");
        var second = await UserAsync(user.Id);
        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(first.PrivateKey, second.PrivateKey);
    }

    [Fact]
    public async Task Two_concurrent_sign_ins_create_one_keypair()
    {
        var user = await OldAccountAsync("bob");
        var users = Users();
        // Both read the document without a keypair before either writes.
        var read = 0;
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        users.MemberKeys.BeforeCreate = async () =>
        {
            if (Interlocked.Increment(ref read) == 2) bothRead.SetResult();
            await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        };
        var created = 0;
        users.MemberKeys.Created = _ => Interlocked.Increment(ref created);

        var keys = await Task.WhenAll(SignInAsync("bob", users), SignInAsync("bob", users));

        Assert.Equal(2, read);
        Assert.Equal(1, created);
        var doc = await UserAsync(user.Id);
        foreach (var userKey in keys)
        {
            using var key = MemberKeys.OpenPrivate(doc, userKey);
            Assert.Equal(doc.PublicKey, MemberKeys.PublicFromPrivate(key));
        }
    }

    [Fact]
    public async Task A_keypair_is_not_created_under_a_user_key_that_is_no_longer_current()
    {
        var user = await OldAccountAsync("bob");
        var userKey = await SignInAsync("bob");
        await UserDocs.UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Unset(u => u.PublicKey).Unset(u => u.PrivateKey), cancellationToken: Ct);

        // A user key from before a rotation: the key id is another one now.
        await UserDocs.UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.KeyId, UserCatalog.KeyId(VaultCrypto.NewKey(), user.Id)), cancellationToken: Ct);
        var doc = await new MemberKeys(_db).EnsureAsync(user.Id, userKey, Ct);

        Assert.NotNull(doc);
        Assert.Null(doc.PublicKey);
        Assert.Null((await UserAsync(user.Id)).PrivateKey);
    }

    [Fact]
    public async Task A_private_key_sealed_for_another_public_key_does_not_open()
    {
        var (user, _) = await Users().CreateAsync("bob", Password, Roles.User, Ct);
        var userKey = await SignInAsync("bob");
        var doc = await UserAsync(user.Id);
        using (MemberKeys.OpenPrivate(doc, userKey))
        {
        }

        // An attacker's public key put beside Bob's sealed private key.
        using var attacker = MemberSlotCrypto.NewKeyPair();
        await UserDocs.UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.PublicKey, MemberSlotCrypto.PublicOf(attacker)), cancellationToken: Ct);
        var swapped = await UserAsync(user.Id);
        Assert.ThrowsAny<CryptographicException>(() => MemberKeys.OpenPrivate(swapped, userKey));

        // The seal itself binds the public key: under the AAD without its hash, it doesn't open.
        Assert.ThrowsAny<CryptographicException>(() =>
            VaultCrypto.Open(VaultCrypto.Derive(userKey, "kumbu/v1/user-private-key", []), doc.PrivateKey!, $"user-private:{user.Id}"));
        Assert.NotEmpty(VaultCrypto.Open(VaultCrypto.Derive(userKey, "kumbu/v1/user-private-key", []), doc.PrivateKey!, MemberKeys.PrivateKeyContext(user.Id, doc.PublicKey!)));

        // Another user key opens nothing either, and an off-curve public key is refused before anything is opened.
        Assert.ThrowsAny<CryptographicException>(() => MemberKeys.OpenPrivate(doc, VaultCrypto.NewKey()));
        var offCurve = (byte[])doc.PublicKey!.Clone();
        offCurve[^1] ^= 1;
        doc.PublicKey = offCurve;
        Assert.ThrowsAny<CryptographicException>(() => MemberKeys.OpenPrivate(doc, userKey));
    }

    [Fact]
    public async Task The_private_key_is_never_in_the_clear()
    {
        var (user, _) = await Users().CreateAsync("bob", Password, Roles.User, Ct);
        var old = await OldAccountAsync("carol");
        var keys = new[] { (user.Id, await SignInAsync("bob")), (old.Id, await SignInAsync("carol")) };

        foreach (var (id, userKey) in keys)
        {
            using var key = MemberKeys.OpenPrivate(await UserAsync(id), userKey);
            var pkcs8 = key.ExportPkcs8PrivateKey();
            var scalar = key.ExportParameters(true).D!;
            foreach (var name in await (await _db.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct))
            {
                foreach (var raw in await _db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
                {
                    var bytes = raw.ToBson();
                    Assert.True(bytes.AsSpan().IndexOf(pkcs8) < 0, $"PKCS#8 private key in {name}");
                    Assert.True(bytes.AsSpan().IndexOf(scalar) < 0, $"private scalar in {name}");
                }
            }
        }
    }

    /// <summary>Alice with a vault "Gartenarbeit", and Bob, both signed in: ids and user keys, and the vault's id.</summary>
    private async Task<((string Id, byte[] Key) Alice, (string Id, byte[] Key) Bob, string VaultId)> SharedWorldAsync()
    {
        var (alice, _) = await Users().CreateAsync("alice", Password, Roles.User, Ct);
        var (bob, _) = await Users().CreateAsync("bob", Password, Roles.User, Ct);
        var aliceKey = await SignInAsync("alice");
        var vault = await Catalog().CreateForUserAsync("Gartenarbeit", alice.Id, aliceKey, Ct);
        return ((alice.Id, aliceKey), (bob.Id, await SignInAsync("bob")), vault.Id);
    }

    private IMongoCollection<SlotDocument> Slots => _db.GetCollection<SlotDocument>("slots");

    private Task<SlotDocument> MemberSlotAsync(string membershipId) => Slots.Find(s => s.Kind == SlotDocument.Member && s.Membership == membershipId).SingleAsync(Ct);

    private async Task<IReadOnlyList<string>> SharedWithAsync(VaultCatalog catalog, (string Id, byte[] Key) user) =>
        [.. (await catalog.OpenForUserAsync(user.Id, user.Key, Ct)).Where(a => !a.IsOwner).Select(a => a.Name)];

    [Fact]
    public async Task A_member_slot_is_wrapped_to_the_members_public_key()
    {
        var (alice, bob, vaultId) = await SharedWorldAsync();
        var membershipId = await Memberships().GrantAsync(alice.Id, alice.Key, vaultId, bob.Id, bob.Key, "Garten", "Bob", MemberRole.Write, Ct);
        var vaultKey = await Catalog().OwnerVaultKeyAsync(alice.Id, alice.Key, vaultId, Ct);
        var slot = await MemberSlotAsync(membershipId);

        Assert.Equal(MemberSlotCrypto.P256, slot.Wrapped[0]);
        using (var bobs = MemberKeys.OpenPrivate(await UserAsync(bob.Id), bob.Key))
        {
            Assert.Equal(vaultKey, MemberSlotCrypto.Unwrap(bobs, slot.Wrapped, $"slot:{vaultId}:member:{membershipId}"));
        }

        // Not the phase-1 stand-in: the user key opens nothing of it.
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(StandInKek(bob.Key, vaultId), slot.Wrapped, $"slot:{vaultId}:member"));
        Assert.Equal(["Garten"], await SharedWithAsync(NewProcess(), bob));

        // A public key swapped into Carol's user document is never what a grant wraps to: the grant fails and writes nothing.
        var (carol, _) = await Users().CreateAsync("carol", Password, Roles.User, Ct);
        var carolKey = await SignInAsync("carol");
        using var attacker = MemberSlotCrypto.NewKeyPair();
        await UserDocs.UpdateOneAsync(u => u.Id == carol.Id, Builders<UserDocument>.Update.Set(u => u.PublicKey, MemberSlotCrypto.PublicOf(attacker)), cancellationToken: Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Memberships().GrantAsync(alice.Id, alice.Key, vaultId, carol.Id, carolKey, "Garten", "Carol", MemberRole.Write, Ct));
        Assert.False(await _db.GetCollection<MembershipDocument>("memberships").Find(m => m.Member == carol.Id).AnyAsync(Ct));
        Assert.Equal(1, await Slots.CountDocumentsAsync(s => s.Kind == SlotDocument.Member, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_stand_in_member_slot_opens_nothing()
    {
        var (alice, bob, vaultId) = await SharedWorldAsync();
        var membershipId = await Memberships().GrantAsync(alice.Id, alice.Key, vaultId, bob.Id, bob.Key, "Garten", "Bob", MemberRole.Write, Ct);
        var vaultKey = await Catalog().OwnerVaultKeyAsync(alice.Id, alice.Key, vaultId, Ct);
        Assert.Equal(["Garten"], await SharedWithAsync(Catalog(), bob));

        // The slot as phase 1 wrapped it: under Bob's user key, with the AAD of its kind. The membership stays active.
        await Slots.UpdateOneAsync(
            s => s.Membership == membershipId && s.Kind == SlotDocument.Member,
            Builders<SlotDocument>.Update.Set(s => s.Wrapped, VaultCrypto.Seal(StandInKek(bob.Key, vaultId), vaultKey, $"slot:{vaultId}:member")),
            cancellationToken: Ct);

        Assert.Empty(await SharedWithAsync(NewProcess(), bob));
        // Nor with the vault in memory, opened by its owner and by Bob a moment ago.
        Assert.Empty(await SharedWithAsync(Catalog(), bob));
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains($"member slot of vault {vaultId}", StringComparison.Ordinal) && l.Contains("did not open", StringComparison.Ordinal));
        // Nor does it give a key or a connection the vault.
        await Assert.ThrowsAsync<ArgumentException>(() => Catalog().UserVaultKeysAsync(bob.Id, bob.Key, [vaultId], "An API key", Ct));
        Assert.Single(await Catalog().OpenForUserAsync(alice.Id, alice.Key, Ct));
    }

    [Fact]
    public async Task A_member_slot_survives_the_members_sign_out()
    {
        var (alice, bob, vaultId) = await SharedWorldAsync();
        await Memberships().GrantAsync(alice.Id, alice.Key, vaultId, bob.Id, bob.Key, "Garten", "Bob", MemberRole.Write, Ct);
        var sessions = new Kumbu.Server.Accounts.Sessions(_db, _time);
        var signIn = (await Users().TrySignInAsync("bob", Password, Ct))!.Value;
        var secret = await sessions.StartAsync(signIn.User, signIn.UserKey, signIn.CredentialReadAt, Ct);
        await sessions.EndAsync(secret, Ct);
        Assert.Null(await sessions.ResolveAsync(secret, Ct));

        // A new session after a new sign-in, in a new process: the user key comes from the password slot again.
        var again = (await Users().TrySignInAsync("bob", Password, Ct))!.Value;
        var resolved = (await sessions.ResolveAsync(await sessions.StartAsync(again.User, again.UserKey, again.CredentialReadAt, Ct), Ct))!.Value;
        Assert.Equal(["Garten"], await SharedWithAsync(NewProcess(), (bob.Id, resolved.UserKey)));
        Assert.Equal(["Garten"], await SharedWithAsync(Catalog(), (bob.Id, resolved.UserKey)));
    }

    /// <summary>The key a phase-1 stand-in member slot was wrapped under: the user slot's.</summary>
    private static byte[] StandInKek(byte[] userKey, string vaultId) => VaultCrypto.Derive(userKey, "kumbu/v1/user-slot-kek", System.Text.Encoding.UTF8.GetBytes(vaultId));
}
