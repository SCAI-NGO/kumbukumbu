// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Crypto;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// HARD RULE 1 (a), sessions: a member's session reaches a shared vault only while the membership is active, checked on
/// every request before the vault is loaded or taken from memory (collab design §3.1). Never weakened for another test.
/// </summary>
[Collection("mongo")]
public sealed class MembershipCheckTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string Id, byte[] Key) Alice = ("u1", VaultCrypto.NewKey());
    private static readonly (string Id, byte[] Key) Bob = ("u2", VaultCrypto.NewKey());
    private static readonly (string Id, byte[] Key) Carol = ("user-lea", VaultCrypto.NewKey());

    private readonly VaultWorld _world = new(fixture.NewDatabase());

    /// <summary>Alice's "Gartenarbeit" in <paramref name="catalog"/>, shared with Bob; its id and the membership's.</summary>
    private async Task<(string VaultId, string MembershipId)> SharedAsync(VaultCatalog catalog)
    {
        var vault = await catalog.CreateForUserAsync("Gartenarbeit", Alice.Id, Alice.Key, Ct);
        var membership = await Share.GrantAsync(catalog, _world.Memberships(catalog), Alice, vault.Id, Bob, "Garten", MemberRole.Write);
        return (vault.Id, membership);
    }

    private static async Task<IReadOnlyList<string>> OpenedAsync(VaultCatalog catalog, (string Id, byte[] Key) user) =>
        [.. (await catalog.OpenForUserAsync(user.Id, user.Key, Ct)).Select(v => v.Id)];

    [Fact]
    public async Task A_member_session_opens_the_shared_vault()
    {
        var (vaultId, _) = await SharedAsync(_world.Catalog());
        var own = await _world.Catalog().CreateForUserAsync("Work", Bob.Id, Bob.Key, Ct);

        // A second process, with nothing in memory.
        Assert.Equal(new[] { own.Id, vaultId }.Order(), (await OpenedAsync(_world.Catalog(), Bob)).Order());
    }

    [Fact]
    public async Task A_revoked_membership_refuses_an_already_unlocked_vault_on_the_next_request()
    {
        var catalog = _world.Catalog();
        var (vaultId, membershipId) = await SharedAsync(catalog);
        Assert.Equal([vaultId], await OpenedAsync(catalog, Bob));

        await Share.SetStatusAsync(_world.Db, membershipId, MembershipStatus.Revoked);

        // The vault is still unlocked in this process: only the membership check keeps Bob out.
        Assert.Equal(1, catalog.OpenCount);
        Assert.Empty(await OpenedAsync(catalog, Bob));
        Assert.Equal([vaultId], await OpenedAsync(catalog, Alice));
        Assert.Empty(await OpenedAsync(catalog, Bob));
    }

    [Theory]
    [InlineData(MembershipStatus.Left)]
    [InlineData(MembershipStatus.Pending)]
    [InlineData(MembershipStatus.Revoked)]
    public async Task A_left_or_pending_membership_opens_nothing(string status)
    {
        var catalog = _world.Catalog();
        var (vaultId, membershipId) = await SharedAsync(catalog);
        await Share.SetStatusAsync(_world.Db, membershipId, status);

        Assert.Empty(await OpenedAsync(_world.Catalog(), Bob));
        // Nor with the vault in memory, opened by its owner.
        Assert.Equal([vaultId], await OpenedAsync(catalog, Alice));
        Assert.Empty(await OpenedAsync(catalog, Bob));
    }

    [Fact]
    public async Task A_member_slot_without_a_membership_opens_nothing()
    {
        var catalog = _world.Catalog();
        var garden = await catalog.CreateForUserAsync("Gartenarbeit", Alice.Id, Alice.Key, Ct);
        var work = await catalog.CreateForUserAsync("Work", Alice.Id, Alice.Key, Ct);
        var carols = await Share.GrantAsync(catalog, _world.Memberships(catalog), Alice, garden.Id, Carol, "Garten", MemberRole.Write);
        var vaultKey = await catalog.OwnerVaultKeyAsync(Alice.Id, Alice.Key, garden.Id, Ct);
        var workKey = await catalog.OwnerVaultKeyAsync(Alice.Id, Alice.Key, work.Id, Ct);

        // Inserted by someone with access to the database, wrapped properly for Bob: one without any membership, one
        // pointing at Carol's active membership, one at Carol's membership but on another vault of Alice's.
        await catalog.Slots.InsertManyAsync(
            [
                catalog.MemberSlot(garden.Id, "no-such-membership", Bob.Id, Bob.Key, vaultKey, "Garten"),
                catalog.MemberSlot(garden.Id, carols, Bob.Id, Bob.Key, vaultKey, "Garten 2"),
                catalog.MemberSlot(work.Id, carols, Bob.Id, Bob.Key, workKey, "Work"),
            ],
            cancellationToken: Ct);

        Assert.Empty(await OpenedAsync(_world.Catalog(), Bob));
        Assert.Equal(new[] { garden.Id, work.Id }.Order(), (await OpenedAsync(catalog, Alice)).Order());
        Assert.Empty(await OpenedAsync(catalog, Bob));
        Assert.Equal([garden.Id], await OpenedAsync(catalog, Carol));

        // Bob's own membership opens the vault it is of, and no other.
        var bobs = await Share.GrantAsync(catalog, _world.Memberships(catalog), Alice, garden.Id, Bob, "Mein Garten", MemberRole.Write);
        await catalog.Slots.InsertOneAsync(catalog.MemberSlot(work.Id, bobs, Bob.Id, Bob.Key, workKey, "Work 2"), cancellationToken: Ct);
        Assert.Equal([garden.Id], await OpenedAsync(catalog, Bob));
        Assert.Equal([garden.Id], await OpenedAsync(_world.Catalog(), Bob));
    }

    [Fact]
    public async Task One_membership_query_per_request()
    {
        var catalog = _world.Catalog();
        var memberships = _world.Memberships(catalog);
        foreach (var name in new[] { "Gartenarbeit", "Work" })
        {
            var vault = await catalog.CreateForUserAsync(name, Alice.Id, Alice.Key, Ct);
            await Share.GrantAsync(catalog, memberships, Alice, vault.Id, Bob, name, MemberRole.Read);
        }

        await _world.Db.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 2), cancellationToken: Ct);
        try
        {
            Assert.Equal(2, (await OpenedAsync(catalog, Bob)).Count);
            Assert.Equal(1, await FindsOnMembershipsAsync());

            // Requests of an owner without shared vaults (Alice is one: her slots are user slots) pay nothing.
            Assert.Equal(2, (await OpenedAsync(catalog, Alice)).Count);
            Assert.Equal(1, await FindsOnMembershipsAsync());
        }
        finally
        {
            await _world.Db.RunCommandAsync<BsonDocument>(new BsonDocument("profile", 0), cancellationToken: Ct);
        }
    }

    private Task<long> FindsOnMembershipsAsync() =>
        _world.Db.GetCollection<BsonDocument>("system.profile").CountDocumentsAsync(
            Builders<BsonDocument>.Filter.Eq("ns", $"{_world.Db.DatabaseNamespace.DatabaseName}.{Memberships.CollectionName}")
            & Builders<BsonDocument>.Filter.Eq("op", "query"),
            cancellationToken: Ct);
}
