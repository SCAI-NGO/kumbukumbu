// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Vaults;
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
    private VaultCatalog? _catalog;

    private VaultCatalog Catalog() => _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

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
}
