// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>The membership document: one per vault and member, statuses that only move forward; granting one.</summary>
[Collection("mongo")]
public sealed class MembershipTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MembershipDocument Doc(string vault, string? member, byte[]? inviteLookup = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Vault = vault,
        Owner = "u1",
        Member = member,
        Role = MemberRoles.ToText(MemberRole.Write),
        Status = member is null ? MembershipStatus.Pending : MembershipStatus.Active,
        InviteLookup = inviteLookup,
        CreatedAt = DateTime.UtcNow,
    };

    private async Task<Memberships> ReadyAsync()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var memberships = world.Memberships(world.Catalog());
        await memberships.EnsureReadyAsync(Ct);
        return memberships;
    }

    [Fact]
    public async Task A_member_has_one_membership_per_vault()
    {
        var memberships = await ReadyAsync();
        await memberships.Docs.InsertOneAsync(Doc("v", "u2"), cancellationToken: Ct);

        var e = await Assert.ThrowsAsync<MongoWriteException>(() => memberships.Docs.InsertOneAsync(Doc("v", "u2"), cancellationToken: Ct));
        Assert.Equal(ServerErrorCategory.DuplicateKey, e.WriteError.Category);
        // Another vault, or another member, is another membership.
        await memberships.Docs.InsertOneAsync(Doc("w", "u2"), cancellationToken: Ct);
        await memberships.Docs.InsertOneAsync(Doc("v", "user-lea"), cancellationToken: Ct);
    }

    [Fact]
    public async Task Pending_memberships_without_a_member_do_not_collide()
    {
        var memberships = await ReadyAsync();

        await memberships.Docs.InsertOneAsync(Doc("v", null, [1]), cancellationToken: Ct);
        await memberships.Docs.InsertOneAsync(Doc("v", null, [2]), cancellationToken: Ct);

        Assert.Equal(2, await memberships.Docs.CountDocumentsAsync(m => m.Vault == "v" && m.Member == null, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_invite_lookup_is_unique_while_it_exists()
    {
        var memberships = await ReadyAsync();
        await memberships.Docs.InsertOneAsync(Doc("v", null, [7, 7]), cancellationToken: Ct);

        var e = await Assert.ThrowsAsync<MongoWriteException>(() => memberships.Docs.InsertOneAsync(Doc("w", null, [7, 7]), cancellationToken: Ct));
        Assert.Equal(ServerErrorCategory.DuplicateKey, e.WriteError.Category);
        // Redeemed memberships have none: any number of them.
        await memberships.Docs.InsertOneAsync(Doc("v", "u2"), cancellationToken: Ct);
        await memberships.Docs.InsertOneAsync(Doc("w", "u2"), cancellationToken: Ct);
    }

    [Theory]
    [InlineData(MembershipStatus.Pending, MembershipStatus.Active, true)]
    [InlineData(MembershipStatus.Pending, MembershipStatus.Revoked, true)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Revoked, true)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Left, true)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Pending, false)]
    [InlineData(MembershipStatus.Revoked, MembershipStatus.Active, false)]
    [InlineData(MembershipStatus.Left, MembershipStatus.Active, false)]
    [InlineData(MembershipStatus.Revoked, MembershipStatus.Left, false)]
    [InlineData(MembershipStatus.Pending, MembershipStatus.Left, false)]
    public void Statuses_only_move_forward(string from, string to, bool allowed) =>
        Assert.Equal(allowed, Memberships.TransitionFilter(from, to) is not null);

    private static readonly (string Id, byte[] Key) Alice = ("u1", VaultCrypto.NewKey());
    private static readonly (string Id, byte[] Key) Bob = ("u2", VaultCrypto.NewKey());

    /// <summary>A world in which Alice owns "Gartenarbeit", with one catalog and its memberships.</summary>
    private async Task<(VaultWorld World, VaultCatalog Catalog, Memberships Memberships, string VaultId)> SharedAsync(int memberLimit = 20)
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var vault = await catalog.CreateForUserAsync("Gartenarbeit", Alice.Id, Alice.Key, Ct);
        return (world, catalog, world.Memberships(catalog, memberLimit), vault.Id);
    }

    private static async Task<long> MembershipsChangedAsync(VaultCatalog catalog, string vaultId) =>
        (await catalog.Vaults.Find(v => v.Id == vaultId).FirstAsync(Ct)).MembershipsChanged;

    private static bool Contains(byte[] haystack, string needle)
    {
        var bytes = Encoding.UTF8.GetBytes(needle);
        return haystack.AsSpan().IndexOf(bytes) >= 0;
    }

    [Fact]
    public async Task Granting_writes_the_membership_and_its_member_slot_together()
    {
        var (world, catalog, memberships, vaultId) = await SharedAsync();

        var id = await Share.GrantAsync(catalog, memberships, Alice, vaultId, Bob, "Gartenarbeit", MemberRole.Write, label: "Bob");

        var doc = await memberships.Docs.Find(m => m.Id == id).FirstAsync(Ct);
        Assert.Equal((vaultId, "u1", "u2", "write", MembershipStatus.Active, 0), (doc.Vault, doc.Owner, doc.Member, doc.Role, doc.Status, doc.Revision));
        Assert.NotNull(doc.AcceptedAt);
        var slot = await catalog.Slots.Find(s => s.Kind == SlotDocument.Member && s.Vault == vaultId).SingleAsync(Ct);
        Assert.Equal(id, slot.Membership);
        Assert.Null(slot.Owner);
        Assert.Equal(VaultCatalog.UserSlotLookup(Bob.Key, Bob.Id), slot.Lookup);
        Assert.Equal("Gartenarbeit", VaultCatalog.OpenAlias(Bob.Key, slot));
        var vaultKey = await catalog.OwnerVaultKeyAsync(Alice.Id, Alice.Key, vaultId, Ct);
        Assert.Equal("Bob", Encoding.UTF8.GetString(VaultCatalog.OpenForVault(vaultKey, vaultId, "kumbu/v1/membership-label", doc.Label, $"membership-label:{vaultId}:{id}")));
        Assert.Equal(1, await MembershipsChangedAsync(catalog, vaultId));

        // Neither the alias nor the label is anywhere in the clear.
        var raw = (await world.Db.GetCollection<BsonDocument>("memberships").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            .Concat(await world.Db.GetCollection<BsonDocument>("slots").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            .Concat(await world.Db.GetCollection<BsonDocument>("vaults").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            .Select(d => d.ToBson()).ToList();
        foreach (var name in new[] { "Gartenarbeit", "gartenarbeit", "Bob", "bob" }) Assert.DoesNotContain(raw, bson => Contains(bson, name));
    }

    [Fact]
    public async Task The_21st_membership_is_refused()
    {
        var (world, catalog, memberships, vaultId) = await SharedAsync(memberLimit: 2);
        var first = await Share.GrantAsync(catalog, memberships, Alice, vaultId, Bob, "Garten", MemberRole.Read);
        await Share.GrantAsync(catalog, memberships, Alice, vaultId, ("user-lea", VaultCrypto.NewKey()), "Garten", MemberRole.Read);

        await Assert.ThrowsAsync<MemberLimitException>(() => Share.GrantAsync(catalog, memberships, Alice, vaultId, ("carol", VaultCrypto.NewKey()), "Garten", MemberRole.Read));

        Assert.Equal(2, await memberships.Docs.CountDocumentsAsync(m => m.Vault == vaultId, cancellationToken: Ct));
        // A tombstone doesn't count: the limit is on the people who share the vault now.
        await Share.SetStatusAsync(world.Db, first, MembershipStatus.Revoked);
        await Share.GrantAsync(catalog, memberships, Alice, vaultId, ("carol", VaultCrypto.NewKey()), "Garten", MemberRole.Read);
    }

    [Fact]
    public async Task Two_concurrent_grants_at_the_limit_admit_one()
    {
        var (_, catalog, memberships, vaultId) = await SharedAsync(memberLimit: 2);
        await Share.GrantAsync(catalog, memberships, Alice, vaultId, Bob, "Garten", MemberRole.Read);

        var grants = new[] { "carol", "user-lea" }.Select(async m =>
        {
            try
            {
                await Share.GrantAsync(catalog, memberships, Alice, vaultId, (m, VaultCrypto.NewKey()), "Garten", MemberRole.Read);
                return true;
            }
            catch (MemberLimitException)
            {
                return false;
            }
        });

        Assert.Equal([false, true], (await Task.WhenAll(grants)).Order());
        Assert.Equal(2, await memberships.Docs.CountDocumentsAsync(m => m.Vault == vaultId, cancellationToken: Ct));
        Assert.Equal(2, await catalog.Slots.CountDocumentsAsync(s => s.Kind == SlotDocument.Member, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_owner_cannot_be_a_member_of_her_own_vault()
    {
        var (_, catalog, memberships, vaultId) = await SharedAsync();

        await Assert.ThrowsAsync<NotPermittedException>(() => Share.GrantAsync(catalog, memberships, Alice, vaultId, Alice, "Garten", MemberRole.Maintain));

        Assert.Equal(0, await memberships.Docs.CountDocumentsAsync(FilterDefinition<MembershipDocument>.Empty, cancellationToken: Ct));
        Assert.Equal(0, await catalog.Slots.CountDocumentsAsync(s => s.Kind == SlotDocument.Member, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_failed_grant_leaves_neither_document()
    {
        var (_, catalog, memberships, vaultId) = await SharedAsync();
        await catalog.CreateForUserAsync("Gartenarbeit", Bob.Id, Bob.Key, Ct);

        // Bob has a vault of that name (compared as vault names are): the alias is taken.
        await Assert.ThrowsAsync<VaultNameTakenException>(() => Share.GrantAsync(catalog, memberships, Alice, vaultId, Bob, " gartenarbeit", MemberRole.Write));

        Assert.Equal(0, await memberships.Docs.CountDocumentsAsync(FilterDefinition<MembershipDocument>.Empty, cancellationToken: Ct));
        Assert.Equal(0, await catalog.Slots.CountDocumentsAsync(s => s.Kind == SlotDocument.Member, cancellationToken: Ct));
        Assert.Equal(0, await MembershipsChangedAsync(catalog, vaultId));
        // Under another alias it works.
        await Share.GrantAsync(catalog, memberships, Alice, vaultId, Bob, "Alices Garten", MemberRole.Write);

        // A grant refused after its slot is made (a second membership of Bob's) leaves no slot either.
        await Assert.ThrowsAsync<AlreadyMemberException>(() => Share.GrantAsync(catalog, memberships, Alice, vaultId, Bob, "Zweiter Garten", MemberRole.Write));
        Assert.Equal(1, await memberships.Docs.CountDocumentsAsync(FilterDefinition<MembershipDocument>.Empty, cancellationToken: Ct));
        Assert.Equal(1, await catalog.Slots.CountDocumentsAsync(s => s.Kind == SlotDocument.Member, cancellationToken: Ct));
        Assert.Equal(1, await MembershipsChangedAsync(catalog, vaultId));
    }
}
