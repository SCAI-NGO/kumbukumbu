// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Crypto;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

[Collection("mongo")]
public sealed class UserVaultTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_users_vault_opens_with_their_user_key_in_a_second_process()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var key = VaultCrypto.NewKey();
        var created = await world.Catalog().CreateForUserAsync("Arbeit", "u1", key, Ct);

        var opened = await world.Catalog().OpenForUserAsync("u1", key, Ct);

        var vault = Assert.Single(opened);
        Assert.Equal(created.Id, vault.Id);
        Assert.Equal("Arbeit", vault.Name);
    }

    [Fact]
    public async Task Another_users_key_opens_nothing_and_neither_does_the_wrong_user_id()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var key = VaultCrypto.NewKey();
        await world.Catalog().CreateForUserAsync("Arbeit", "u1", key, Ct);

        Assert.Empty(await world.Catalog().OpenForUserAsync("u2", VaultCrypto.NewKey(), Ct));
        Assert.Empty(await world.Catalog().OpenForUserAsync("u2", key, Ct));
    }

    [Fact]
    public async Task Two_users_may_both_have_a_vault_of_the_same_name()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var (k1, k2) = (VaultCrypto.NewKey(), VaultCrypto.NewKey());

        await catalog.CreateForUserAsync("Arbeit", "u1", k1, Ct);
        await catalog.CreateForUserAsync("Arbeit", "u2", k2, Ct);

        Assert.Single(await world.Catalog().OpenForUserAsync("u1", k1, Ct));
        Assert.Single(await world.Catalog().OpenForUserAsync("u2", k2, Ct));
    }

    [Fact]
    public async Task One_user_cannot_create_two_vaults_of_the_same_name_however_it_is_spelled()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var key = VaultCrypto.NewKey();
        await catalog.CreateForUserAsync("Arbeit", "u1", key, Ct);

        await Assert.ThrowsAsync<VaultNameTakenException>(() => catalog.CreateForUserAsync("Arbeit", "u1", key, Ct));
        await Assert.ThrowsAsync<VaultNameTakenException>(() => catalog.CreateForUserAsync(" arbeit ", "u1", key, Ct));
        await catalog.CreateForUserAsync("Privat", "u1", key, Ct);
        Assert.Equal(2, (await world.Catalog().OpenForUserAsync("u1", key, Ct)).Count);
    }

    [Fact]
    public async Task Concurrent_creates_of_one_name_let_exactly_one_win()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var key = VaultCrypto.NewKey();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            try { await catalog.CreateForUserAsync("Arbeit", "u1", key, Ct); return true; }
            catch (VaultNameTakenException) { return false; }
        }));

        Assert.Equal(1, results.Count(r => r));
        Assert.Single(await world.Catalog().OpenForUserAsync("u1", key, Ct));
    }

    [Fact]
    public async Task A_user_vault_has_only_a_user_slot_and_no_name_in_the_clear()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        await world.Catalog().CreateForUserAsync("Geheim", "u1", VaultCrypto.NewKey(), Ct);

        var slots = await world.Db.GetCollection<BsonDocument>("slots").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);

        var slot = Assert.Single(slots);
        Assert.Equal("user", slot["kind"].AsString);
        Assert.DoesNotContain("Geheim", slot.ToJson(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_user_slot_that_matches_but_does_not_open_is_skipped()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var key = VaultCrypto.NewKey();
        await world.Catalog().CreateForUserAsync("Arbeit", "u1", key, Ct);
        await world.Catalog().CreateForUserAsync("Privat", "u1", key, Ct);
        var slots = world.Db.GetCollection<BsonDocument>("slots");
        var first = await slots.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct);
        var wrapped = first["wrapped"].AsBsonBinaryData.Bytes;
        wrapped[^1] ^= 1;
        await slots.UpdateOneAsync(new BsonDocument("_id", first["_id"]), new BsonDocument("$set", new BsonDocument("wrapped", wrapped)), cancellationToken: Ct);

        Assert.Single(await world.Catalog().OpenForUserAsync("u1", key, Ct));
    }

    [Fact]
    public async Task A_user_vault_works_with_the_note_store_and_the_unlocked_event_fires()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var key = VaultCrypto.NewKey();
        var unlocked = new List<string>();
        var first = world.Catalog();
        first.Unlocked += v => unlocked.Add(v.Name);
        await first.CreateForUserAsync("Arbeit", "u1", key, Ct);

        var second = world.Catalog();
        second.Unlocked += v => unlocked.Add(v.Name);
        await second.OpenForUserAsync("u1", key, Ct);
        await second.OpenForUserAsync("u1", key, Ct);

        Assert.Equal(["Arbeit", "Arbeit"], unlocked);
        Assert.Equal(1, second.OpenCount);
    }

    [Fact]
    public async Task Dev_token_vaults_are_unaffected_by_user_vaults()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var key = VaultCrypto.NewKey();
        await catalog.CreateAsync("Dev", Token, null, Ct);
        await catalog.CreateForUserAsync("Arbeit", "u1", key, Ct);

        var viaToken = await world.Catalog().OpenAsync(Token, Ct);
        var viaUser = await world.Catalog().OpenForUserAsync("u1", key, Ct);

        Assert.Equal("Dev", Assert.Single(viaToken).Name);
        Assert.Equal("Arbeit", Assert.Single(viaUser).Name);
    }

    [Fact]
    public async Task An_orphan_slot_without_a_vault_does_not_block_its_name()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var time = new FakeTimeProvider();
        var catalog = world.Catalog(time: time);
        var key = VaultCrypto.NewKey();
        await catalog.CreateForUserAsync("Arbeit", "u1", key, Ct);
        var vaults = world.Db.GetCollection<BsonDocument>("vaults");
        await vaults.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty, Ct);

        // Just now it may still be a create between its two inserts, so the name stays taken.
        await Assert.ThrowsAsync<VaultNameTakenException>(() => catalog.CreateForUserAsync("Arbeit", "u1", key, Ct));
        time.Advance(TimeSpan.FromMinutes(2));
        var again = await catalog.CreateForUserAsync("Arbeit", "u1", key, Ct);

        Assert.Equal(1, await world.Db.GetCollection<BsonDocument>("slots").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
        Assert.Equal(again.Id, Assert.Single(await world.Catalog().OpenForUserAsync("u1", key, Ct)).Id);
        await Assert.ThrowsAsync<VaultNameTakenException>(() => catalog.CreateForUserAsync("Arbeit", "u1", key, Ct));
    }
}
