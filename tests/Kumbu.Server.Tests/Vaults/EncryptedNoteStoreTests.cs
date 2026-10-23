// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

[Collection("mongo")]
public sealed class EncryptedNoteStoreTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static StoredNote New(string path, string content) => new(P(path), content, 1, Writers.Dev, T0, []);

    private async Task<(VaultWorld World, VaultCatalog Catalog, EncryptedNoteStore Store)> WorldAsync(params string[] vaults)
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        foreach (var v in vaults.Length == 0 ? ["Work"] : vaults) await catalog.CreateAsync(v, Token, (_, _) => Task.CompletedTask, Ct);
        return (world, catalog, Store(catalog));
    }

    [Fact]
    public async Task Create_then_get_round_trips_every_field_also_in_a_second_process()
    {
        var (world, _, store) = await WorldAsync();
        var note = New("/Work/a.md", "a") with { History = [new HistoryEntry("older", Writers.Seed, T0.AddDays(-1))], SeedContent = "older" };

        Assert.True(await store.TryCreateAsync(note, Ct));

        var read = await Store(world.Catalog()).GetAsync(P("/Work/a.md"), Ct);
        Assert.NotNull(read);
        Assert.Equal(note with { History = [] }, read with { History = [] });
        Assert.Equal(note.History, read.History);
    }

    [Fact]
    public async Task Nothing_readable_reaches_the_database()
    {
        var (world, _, store) = await WorldAsync("Geheimprojekt");
        await store.TryCreateAsync(New("/Geheimprojekt/Pläne/Übernahme.md", "Wir kaufen die Firma Acme."), Ct);
        var created = (await store.GetAsync(P("/Geheimprojekt/Pläne/Übernahme.md"), Ct))!;
        await store.TryReplaceAsync(created, NoteWriter.Next(created, "Wir kaufen die Firma Acme nicht.", "other", T0.AddDays(1)), Ct);
        await store.TryCreateAsync(New("/Geheimprojekt/weg.md", "Acme Papierkorb"), Ct);
        await store.TryTrashAsync((await store.GetAsync(P("/Geheimprojekt/weg.md"), Ct))!, Writers.Dev, T0, Ct);
        var other = (await store.GetAsync(P("/Geheimprojekt/Pläne/Übernahme.md"), Ct))!;
        await store.TryMoveAsync(other, other with { Path = P("/Geheimprojekt/Archiv/Acme.md"), Revision = other.Revision + 1 }, Writers.Dev, T0, Ct);

        var everything = new StringBuilder();
        foreach (var name in await (await world.Db.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct))
        {
            foreach (var doc in await world.Db.GetCollection<BsonDocument>(name).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
            {
                everything.Append(doc.ToJson());
                foreach (var element in doc.Elements.Where(e => e.Value.IsBsonBinaryData))
                {
                    everything.Append(Encoding.UTF8.GetString(element.Value.AsByteArray));
                }
            }
        }

        var dump = everything.ToString();
        foreach (var secret in new[] { "Acme", "Geheimprojekt", "Pläne", "Übernahme", "Archiv", "Papierkorb", "kaufen", Token })
        {
            Assert.DoesNotContain(secret, dump, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Another_token_sees_no_vault_at_all()
    {
        var (world, _, store) = await WorldAsync();
        await store.TryCreateAsync(New("/Work/a.md", "a"), Ct);

        var stranger = Store(world.Catalog(), "another-token-0123456789abcdef");

        Assert.Empty(await stranger.ListVaultsAsync(Ct));
        Assert.Null(await stranger.ReadAsync(P("/Work/a.md"), Ct));
        Assert.False(await stranger.TryCreateAsync(New("/Work/b.md", "b"), Ct));
    }

    [Fact]
    public async Task A_recovery_key_gives_a_new_token_access()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        string? recovery = null;
        await catalog.CreateAsync("Work", Token, (_, key) => { recovery = key; return Task.CompletedTask; }, Ct);
        await Store(catalog).TryCreateAsync(New("/Work/a.md", "kept"), Ct);
        const string newToken = "a-new-token-0123456789abcdef";

        Assert.Equal([("Work", true)], await world.Catalog().RecoverAsync(recovery!, newToken, Ct));
        Assert.Equal([("Work", false)], await world.Catalog().RecoverAsync(recovery!.ToLowerInvariant(), newToken, Ct));

        var after = Store(world.Catalog(), newToken);
        Assert.Equal("kept", await after.ReadAsync(P("/Work/a.md"), Ct));
        // One slot for the old token, one for the new; recovering twice adds no second.
        Assert.Equal(2, await world.Db.GetCollection<BsonDocument>("slots").CountDocumentsAsync(new BsonDocument("kind", "key"), cancellationToken: Ct));
        Assert.Empty(await world.Catalog().RecoverAsync(RecoveryKey.Format(RecoveryKey.New()), newToken, Ct));
    }

    [Fact]
    public async Task An_empty_vault_exists_and_vaults_are_listed()
    {
        var (_, _, store) = await WorldAsync("Work", "Home");
        Assert.Equal(["Home", "Work"], (await store.ListVaultsAsync(Ct)).Order());
        Assert.Empty(await store.ListNotesUnderAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Create_fails_if_the_note_exists()
    {
        var (_, _, store) = await WorldAsync();
        Assert.True(await store.TryCreateAsync(New("/Work/a.md", "a"), Ct));
        Assert.False(await store.TryCreateAsync(New("/Work/a.md", "b"), Ct));
        Assert.Equal("a", await store.ReadAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Replace_is_a_compare_and_swap_also_between_processes()
    {
        var (world, _, first) = await WorldAsync();
        var v1 = New("/Work/a.md", "one");
        await first.TryCreateAsync(v1, Ct);
        var second = Store(world.Catalog());
        var seenBySecond = (await second.GetAsync(P("/Work/a.md"), Ct))!;

        Assert.True(await first.TryReplaceAsync(v1, NoteWriter.Next(v1, "two", Writers.Dev, T0.AddHours(1)), Ct));
        Assert.False(await second.TryReplaceAsync(seenBySecond, NoteWriter.Next(seenBySecond, "rival", "other", T0.AddHours(1)), Ct));

        // The failed swap refreshed the second process's working set.
        Assert.Equal("two", await second.ReadAsync(P("/Work/a.md"), Ct));
        var read = (await Store(world.Catalog()).GetAsync(P("/Work/a.md"), Ct))!;
        Assert.Equal(("two", 2L), (read.Content, read.Revision));
        Assert.Equal(["one"], read.History.Select(h => h.Content));
    }

    [Fact]
    public async Task Trash_moves_the_note_with_its_history_and_restore_brings_it_back()
    {
        var (world, _, store) = await WorldAsync();
        var v1 = New("/Work/a.md", "one");
        await store.TryCreateAsync(v1, Ct);
        var v2 = NoteWriter.Next(v1, "two", "other", T0.AddHours(1));
        await store.TryReplaceAsync(v1, v2, Ct);

        Assert.True(await store.TryTrashAsync(v2, Writers.Dev, T0.AddHours(2), Ct));
        Assert.Null(await store.GetAsync(P("/Work/a.md"), Ct));
        var trashed = Assert.Single(await Store(world.Catalog()).ListTrashAsync(P("/Work"), Ct));
        Assert.Equal((T0.AddHours(2), Writers.Dev, "two"), (trashed.DeletedAt, trashed.DeletedBy, trashed.Note.Content));

        var restored = await store.TryRestoreFromTrashAsync(P("/Work/a.md"), null, Writers.Dev, T0.AddHours(3), Ct);

        Assert.NotNull(restored);
        Assert.Equal(["one"], restored.History.Select(h => h.Content));
        Assert.Equal("two", await Store(world.Catalog()).ReadAsync(P("/Work/a.md"), Ct));
        Assert.Empty(await Store(world.Catalog()).ListTrashAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Trash_fails_and_leaves_no_copy_if_the_note_changed()
    {
        var (world, _, store) = await WorldAsync();
        var v1 = New("/Work/a.md", "one");
        await store.TryCreateAsync(v1, Ct);
        await Store(world.Catalog()).TryReplaceAsync(v1, NoteWriter.Next(v1, "two", Writers.Dev, T0), Ct);

        Assert.False(await store.TryTrashAsync(v1, Writers.Dev, T0, Ct));
        Assert.Empty(await Store(world.Catalog()).ListTrashAsync(Kumbu.Core.Paths.VaultPath.Root, Ct));
        Assert.Equal("two", await store.ReadAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Restore_from_trash_by_version_and_never_over_a_note()
    {
        var (world, _, store) = await WorldAsync();
        foreach (var (content, hour) in new[] { ("first", 1), ("second", 2) })
        {
            var note = New("/Work/a.md", content);
            await store.TryCreateAsync(note, Ct);
            await store.TryTrashAsync(note, Writers.Dev, T0.AddHours(hour), Ct);
        }

        await store.TryCreateAsync(New("/Work/a.md", "occupied"), Ct);
        Assert.Null(await store.TryRestoreFromTrashAsync(P("/Work/a.md"), null, Writers.Dev, T0, Ct));

        await store.TryTrashAsync((await store.GetAsync(P("/Work/a.md"), Ct))!, Writers.Dev, T0.AddHours(3), Ct);
        await store.TryRestoreFromTrashAsync(P("/Work/a.md"), VersionToken.Of("first"), Writers.Dev, T0.AddHours(4), Ct);
        Assert.Equal("first", await Store(world.Catalog()).ReadAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Move_carries_the_note_and_leaves_a_record()
    {
        var (world, _, store) = await WorldAsync();
        var v1 = New("/Work/a.md", "one");
        await store.TryCreateAsync(v1, Ct);

        Assert.True(await store.TryMoveAsync(v1, v1 with { Path = P("/Work/b.md"), Revision = 2 }, Writers.Dev, T0, Ct));

        var later = Store(world.Catalog());
        Assert.Null(await later.GetAsync(P("/Work/a.md"), Ct));
        Assert.Equal(("one", 2L), ((await later.GetAsync(P("/Work/b.md"), Ct))!.Content, (await later.GetAsync(P("/Work/b.md"), Ct))!.Revision));
        Assert.Equal(new MoveRecord(P("/Work/a.md"), P("/Work/b.md"), Writers.Dev, T0), await later.FindMoveAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Move_never_overwrites_and_never_loses_a_concurrent_write()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(New("/Work/b.md", "b"), Ct);

        Assert.False(await store.TryMoveAsync(a, a with { Path = P("/Work/b.md"), Revision = 2 }, Writers.Dev, T0, Ct));
        Assert.Equal("b", await store.ReadAsync(P("/Work/b.md"), Ct));

        await Store(world.Catalog()).TryReplaceAsync(a, NoteWriter.Next(a, "a2", Writers.Dev, T0), Ct);
        Assert.False(await store.TryMoveAsync(a, a with { Path = P("/Work/c.md"), Revision = 2 }, Writers.Dev, T0, Ct));
        var later = Store(world.Catalog());
        Assert.Equal("a2", await later.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Null(await later.ReadAsync(P("/Work/c.md"), Ct));
    }

    [Fact]
    public async Task A_tampered_document_is_skipped_not_shown()
    {
        var (world, _, store) = await WorldAsync();
        await store.TryCreateAsync(New("/Work/a.md", "a"), Ct);
        await store.TryCreateAsync(New("/Work/b.md", "b"), Ct);
        var notes = world.Db.GetCollection<BsonDocument>("notes");
        var doc = await notes.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct);
        var blob = doc["blob"].AsByteArray;
        blob[^1] ^= 1;
        await notes.UpdateOneAsync(new BsonDocument("_id", doc["_id"]), new BsonDocument("$set", new BsonDocument("blob", blob)), cancellationToken: Ct);

        Assert.Single(await Store(world.Catalog()).ListNotesUnderAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Plaintext_notes_from_before_encryption_are_refused()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        await world.Db.GetCollection<BsonDocument>("notes").InsertOneAsync(new BsonDocument { ["path"] = "/Work/a.md", ["content"] = "old" }, cancellationToken: Ct);

        var e = await Assert.ThrowsAsync<VaultUnavailableException>(() => Store(world.Catalog()).ListVaultsAsync(Ct));
        Assert.Contains("docker compose down -v", e.Message);
    }

    [Fact]
    public async Task Idle_vaults_are_locked_and_unlock_again()
    {
        var (world, _, store) = await WorldAsync();
        await store.TryCreateAsync(New("/Work/a.md", "a"), Ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var catalog = world.Catalog(idleMinutes: 15, time: clock);
        await Store(catalog).ListVaultsAsync(Ct);
        clock.Advance(TimeSpan.FromMinutes(14));
        catalog.Sweep();
        Assert.Equal(1, catalog.OpenCount);

        clock.Advance(TimeSpan.FromMinutes(2));
        catalog.Sweep();

        Assert.Equal(0, catalog.OpenCount);
        Assert.Equal("a", await Store(catalog).ReadAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Trash_and_moves_expire_after_thirty_days()
    {
        var (world, _, store) = await WorldAsync();
        await store.ListVaultsAsync(Ct);

        foreach (var (collection, field) in new[] { ("trash", "deletedAt"), ("moves", "at") })
        {
            var indexes = await (await world.Db.GetCollection<BsonDocument>(collection).Indexes.ListAsync(Ct)).ToListAsync(Ct);
            var ttl = Assert.Single(indexes, i => i.Contains("expireAfterSeconds"));
            Assert.Equal(new BsonDocument(field, 1), ttl["key"].AsBsonDocument);
            Assert.Equal(30 * 24 * 3600, ttl["expireAfterSeconds"].ToInt64());
        }
    }

    [Fact]
    public async Task Above_the_memory_limit_the_least_recently_used_vault_is_locked()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var setup = world.Catalog();
        await setup.CreateAsync("Old", Token, saveRecoveryKey: null, Ct);
        await setup.CreateAsync("New", Token, saveRecoveryKey: null, Ct);
        var big = new string('x', 300_000);
        await Store(setup).TryCreateAsync(New("/Old/a.md", big), Ct);
        await Store(setup).TryCreateAsync(New("/New/a.md", big), Ct);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var catalog = world.Catalog(memoryMegabytes: 1, time: clock);
        await Store(catalog).ListVaultsAsync(Ct);

        // 2 × 600 KB of UTF-16 is above 1 MB, but both were just used: a request may still hold them.
        catalog.Sweep();
        Assert.Equal(2, catalog.OpenCount);

        clock.Advance(TimeSpan.FromMinutes(2));
        await Store(catalog).ReadAsync(P("/New/a.md"), Ct);
        catalog.Sweep();
        Assert.Equal(1, catalog.OpenCount);
        Assert.Equal(big, await Store(catalog).ReadAsync(P("/Old/a.md"), Ct));
    }

    [Fact]
    public async Task A_forged_slot_gets_nothing_even_while_the_vault_is_unlocked()
    {
        var (world, catalog, store) = await WorldAsync();
        await store.TryCreateAsync(New("/Work/a.md", "secret"), Ct);
        const string attacker = "attacker-token-0123456789abcdef";
        var vaultId = (await world.Db.GetCollection<BsonDocument>("vaults").Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct))["_id"].AsString;
        await world.Db.GetCollection<BsonDocument>("slots").InsertOneAsync(new BsonDocument
        {
            ["vault"] = vaultId, ["kind"] = "key",
            ["lookup"] = VaultCrypto.Derive(attacker, "kumbu/v1/key-lookup", []),
            ["wrapped"] = VaultCrypto.Seal(VaultCrypto.NewKey(), VaultCrypto.NewKey(), $"slot:{vaultId}:key"),
            ["createdAt"] = DateTime.UtcNow,
        }, cancellationToken: Ct);

        // The vault is in this catalog's memory, unlocked by the real token.
        Assert.Equal(["Work"], await Store(catalog).ListVaultsAsync(Ct));
        Assert.Empty(await Store(catalog, attacker).ListVaultsAsync(Ct));
        Assert.Null(await Store(catalog, attacker).ReadAsync(P("/Work/a.md"), Ct));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("rev")]
    public async Task Changing_the_clear_metadata_of_a_note_makes_it_unreadable(string field)
    {
        var (world, _, store) = await WorldAsync();
        await store.TryCreateAsync(New("/Work/a.md", "a"), Ct);
        await store.TryCreateAsync(New("/Work/b.md", "b"), Ct);
        var notes = world.Db.GetCollection<BsonDocument>("notes");
        var doc = await notes.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct);
        BsonValue changed = field == "pid" ? new BsonBinaryData(new byte[32]) : new BsonInt64(99);
        await notes.UpdateOneAsync(new BsonDocument("_id", doc["_id"]), new BsonDocument("$set", new BsonDocument(field, changed)), cancellationToken: Ct);

        Assert.Single(await Store(world.Catalog()).ListNotesUnderAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Plaintext_trash_from_before_encryption_is_refused_too()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        await world.Db.GetCollection<BsonDocument>("trash").InsertOneAsync(new BsonDocument { ["path"] = "/Work/a.md", ["content"] = "old" }, cancellationToken: Ct);

        await Assert.ThrowsAsync<VaultUnavailableException>(() => Store(world.Catalog()).ListVaultsAsync(Ct));
    }

    [Fact]
    public async Task Expired_trash_leaves_the_working_set_at_the_sweep()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var catalog = world.Catalog(idleMinutes: 60 * 24 * 40, time: clock);
        await catalog.CreateAsync("Work", Token, saveRecoveryKey: null, Ct);
        var store = Store(catalog);
        var note = New("/Work/a.md", "a");
        await store.TryCreateAsync(note, Ct);
        await store.TryTrashAsync(note, Writers.Dev, clock.GetUtcNow().UtcDateTime, Ct);

        clock.Advance(TimeSpan.FromDays(31));
        await Store(catalog).ListVaultsAsync(Ct);
        catalog.Sweep();

        Assert.Empty(await Store(catalog).ListTrashAsync(P("/Work"), Ct));
    }

    private static async Task DamageAsync(VaultWorld world, string field)
    {
        var notes = world.Db.GetCollection<BsonDocument>("notes");
        var doc = await notes.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct);
        var update = field == "blob"
            ? new BsonDocument("blob", new BsonBinaryData([.. doc["blob"].AsByteArray.Take(40), .. new byte[20]]))
            : new BsonDocument("rev", doc["rev"].ToInt64() + 7);
        await notes.UpdateOneAsync(new BsonDocument("_id", doc["_id"]), new BsonDocument("$set", update), cancellationToken: Ct);
    }

    [Fact]
    public async Task A_damaged_note_is_known_by_its_path_and_blocks_it_cleanly()
    {
        var (world, _, store) = await WorldAsync();
        await store.TryCreateAsync(New("/Work/a.md", "a"), Ct);
        await DamageAsync(world, "blob");

        var later = Store(world.Catalog());
        Assert.Null(await later.GetAsync(P("/Work/a.md"), Ct));
        Assert.True(await later.IsDamagedAsync(P("/Work/a.md"), Ct));
        Assert.False(await later.IsDamagedAsync(P("/Work/b.md"), Ct));
        Assert.Equal(1, await later.CountDamagedAsync(P("/Work"), Ct));
        // The create is refused by the unique index; no exception escapes.
        Assert.False(await later.TryCreateAsync(New("/Work/a.md", "new"), Ct));
    }

    [Fact]
    public async Task A_note_damaged_after_loading_is_found_by_the_failed_swap()
    {
        var (world, catalog, store) = await WorldAsync();
        var note = New("/Work/a.md", "a");
        await store.TryCreateAsync(note, Ct);
        await DamageAsync(world, "rev");

        Assert.False(await Store(catalog).TryReplaceAsync(note, NoteWriter.Next(note, "b", Writers.Dev, T0), Ct));

        var after = Store(catalog);
        Assert.Null(await after.GetAsync(P("/Work/a.md"), Ct));
        Assert.True(await after.IsDamagedAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Locked_vaults_are_counted_for_the_token()
    {
        var (world, _, _) = await WorldAsync("Mine");
        await world.Catalog().CreateAsync("Theirs", "someone-elses-token-0123456789", saveRecoveryKey: null, Ct);

        Assert.Equal(1, await Store(world.Catalog()).CountLockedVaultsAsync(Ct));
    }
}
