// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// A member slot is never ownership (collab design §2.2): only a <c>user</c> slot makes its user the vault's owner, for
/// account deletion, the admin's list, the owners a vault's deletion marks and the dev token's locked vaults.
/// </summary>
[Collection("mongo")]
public sealed class OwnershipTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly VaultWorld _world = new(fixture.NewDatabase());
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
    private VaultCatalog? _catalog;

    private IMongoDatabase Db => _world.Db;

    private VaultCatalog Catalog() => _catalog ??= new(Db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private ApiKeys Keys() => new(Db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private Connections Connections() => new(Db, Catalog(), _time, NullLogger<Connections>.Instance);

    private UserCatalog Users() =>
        new(Db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), Keys(), Connections(), _time, NullLogger<UserCatalog>.Instance);

    private Admin Admin() => new(Db, Catalog(), new Sessions(Db, _time), Keys(), Connections(), _time, NullLogger<Admin>.Instance);

    private Deletion Deletion()
    {
        var list = new DeletionList(Options.Create(new KumbuOptions()), NullLogger<DeletionList>.Instance);
        return new(Db, Catalog(), Users(), list, new RestoredDeletions(Db, list), _time, NullLogger<Deletion>.Instance);
    }

    private async Task<(User User, byte[] Key)> SignedInAsync(string username, string role = Roles.User)
    {
        await Users().CreateAsync(username, Password, role, Ct);
        var (user, key, _) = (await Users().TrySignInAsync(username, Password, Ct))!.Value;
        return (user, key);
    }

    /// <summary>
    /// A member slot for <paramref name="memberId"/> on <paramref name="vaultId"/>, written straight to the database. It
    /// carries an <c>owner</c> naming the member on purpose, which a real one never does: the kind alone must decide.
    /// </summary>
    private Task MemberSlotAsync(string vaultId, string memberId, byte[] memberKey) =>
        Catalog().Slots.InsertOneAsync(new SlotDocument
        {
            Vault = vaultId,
            Kind = SlotDocument.Member,
            Lookup = VaultCatalog.UserSlotLookup(memberKey, memberId),
            Owner = memberId,
            Membership = "membership-1",
            Wrapped = [1, 2, 3],
            CreatedAt = _time.GetUtcNow().UtcDateTime,
        }, cancellationToken: Ct);

    [Fact]
    public async Task A_member_slot_is_not_ownership_for_account_deletion()
    {
        var (alice, aliceKey) = await SignedInAsync("alice");
        var (bob, bobKey) = await SignedInAsync("bob");
        var vault = await Catalog().CreateForUserAsync("Gartenarbeit", alice.Id, aliceKey, Ct);
        var store = new EncryptedNoteStore(Catalog(), new UserCredential(alice.Id, aliceKey));
        Assert.True(await store.TryCreateAsync(new StoredNote(P("/Gartenarbeit/beete.md"), "Tomaten", 1, "alice", _time.GetUtcNow().UtcDateTime, [], Ref: WriterRef.Server), Ct));
        await MemberSlotAsync(vault.Id, bob.Id, bobKey);

        Assert.True(await Deletion().DeleteOwnAccountAsync(bob, bobKey, Password, "bob", Ct));

        Assert.True(await Catalog().Vaults.Find(v => v.Id == vault.Id).AnyAsync(Ct));
        Assert.Equal(1, await Catalog().Notes.CountDocumentsAsync(n => n.Vault == vault.Id, cancellationToken: Ct));
        Assert.Equal(["Gartenarbeit"], (await _world.Catalog().OpenForUserAsync(alice.Id, aliceKey, Ct)).Select(v => v.Name));
    }

    [Fact]
    public async Task The_admin_list_counts_only_owned_vaults()
    {
        var (anna, _) = await SignedInAsync("anna", Roles.Admin);
        var (alice, aliceKey) = await SignedInAsync("alice");
        var (bob, bobKey) = await SignedInAsync("bob");
        var vault = await Catalog().CreateForUserAsync("Gartenarbeit", alice.Id, aliceKey, Ct);
        await MemberSlotAsync(vault.Id, bob.Id, bobKey);

        var users = await Admin().ListUsersAsync(anna, Ct);

        Assert.Equal(1, users.Single(u => u.Username == "alice").Vaults);
        var row = users.Single(u => u.Username == "bob");
        Assert.Equal(0, row.Vaults);
        Assert.Equal(0, row.BytesStored);
    }

    [Fact]
    public async Task Deleting_a_shared_vault_marks_only_its_owner()
    {
        var vault = await Catalog().CreateForUserAsync("Gartenarbeit", "u1", VaultCrypto.NewKey(), Ct);
        await MemberSlotAsync(vault.Id, "u2", VaultCrypto.NewKey());
        var users = Db.GetCollection<UserDocument>("users");
        var before = await users.Find(u => u.Id == "u2").FirstAsync(Ct);

        await Catalog().DeleteVaultsAsync([vault.Id], (_, _) => Task.CompletedTask, Ct);

        Assert.Equal(1, (await users.Find(u => u.Id == "u1").FirstAsync(Ct)).VaultsDeleted);
        var after = await users.Find(u => u.Id == "u2").FirstAsync(Ct);
        Assert.Equal(before.ToBsonDocument(), after.ToBsonDocument());
    }

    [Fact]
    public async Task A_member_slot_does_not_count_as_a_locked_vault_of_the_dev_token()
    {
        var catalog = Catalog();
        var shared = await catalog.CreateForUserAsync("Gartenarbeit", "u1", VaultCrypto.NewKey(), Ct);
        await MemberSlotAsync(shared.Id, "u2", VaultCrypto.NewKey());
        // A dev-token vault of another token: locked to this one, and a member slot on it makes nobody its owner.
        var locked = await catalog.CreateAsync("Work", "another-token-0123456789", null, Ct);
        await MemberSlotAsync(locked.Id, "u2", VaultCrypto.NewKey());
        await catalog.CreateAsync("Dev", Token, null, Ct);

        Assert.Equal(1, await catalog.CountLockedAsync(Token, Ct));
    }

    [Fact]
    public async Task A_member_never_resumes_the_owners_re_keying()
    {
        var catalog = Catalog();
        var (alice, bob) = (VaultCrypto.NewKey(), VaultCrypto.NewKey());
        var vault = await catalog.CreateForUserAsync("Gartenarbeit", "u1", alice, Ct);
        await Share.GrantAsync(catalog, _world.Memberships(catalog), ("u1", alice), vault.Id, ("u2", bob), "Garten", Kumbu.Server.Sharing.MemberRole.Write);
        // A run whose start committed: the vault is in its two-key period.
        await catalog.Vaults.UpdateOneAsync(v => v.Id == vault.Id, Builders<VaultDocument>.Update.Set(v => v.RekeyStartedAt, _time.GetUtcNow().UtcDateTime), cancellationToken: Ct);
        var found = new List<string>();
        var other = _world.Catalog();
        other.RekeyPendingFound += (_, user, _) => found.Add(user);

        Assert.True(Assert.Single(await other.OpenForUserAsync("u2", bob, Ct)).Vault.Pending);
        Assert.Empty(found);
        await other.OpenForUserAsync("u1", alice, Ct);
        Assert.Equal(["u1"], found);
    }
}
