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

    private static StoredNote New(string path, string content) => new(P(path), content, 1, Writers.Dev, T0, [], Ref: WriterRef.Server);

    private static async Task<bool> MoveAsync(INoteStore store, StoredNote current, StoredNote target) =>
        (await store.TryCommitAsync(new NoteBatch { Moves = { (current, target) } }, Writers.Dev, WriterRef.Server, T0, Ct)).Count == 0;

    private async Task<(VaultWorld World, VaultCatalog Catalog, EncryptedNoteStore Store)> WorldAsync(params string[] vaults)
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        foreach (var v in vaults.Length == 0 ? ["Work"] : vaults) await catalog.CreateAsync(v, Token, (_, _) => Task.CompletedTask, Ct);
        return (world, catalog, Store(catalog));
    }

    // Decision 12 of the collab plan: a broken invariant is loud. Before, one of the two was silently hidden.
    [Fact]
    public async Task Two_vaults_of_one_name_fail_loudly()
    {
        var (world, _, _) = await WorldAsync("Work", "Work");

        var store = Store(world.Catalog());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListVaultsAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(world.Catalog()).GetAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Create_then_get_round_trips_every_field_also_in_a_second_process()
    {
        var (world, _, store) = await WorldAsync();
        var note = New("/Work/a.md", "a") with { History = [new HistoryEntry("older", Writers.Seed, T0.AddDays(-1), Ref: WriterRef.Server)], SeedContent = "older" };

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
        await store.TryCreateAsync(New("/Geheimprojekt/Pläne/Übernahme.md", "Wir kaufen die Firma Acme-Übernahme-Geheimprojekt."), Ct);
        var created = (await store.GetAsync(P("/Geheimprojekt/Pläne/Übernahme.md"), Ct))!;
        await store.TryReplaceAsync(created, NoteWriter.Next(created, "Wir kaufen die Firma Acme-Übernahme-Geheimprojekt nicht.", "other", WriterRef.Server, T0.AddDays(1)), Ct);
        await store.TryCreateAsync(New("/Geheimprojekt/weg.md", "Acme-Übernahme-Geheimprojekt Papierkorb-Löschung"), Ct);
        await store.TryTrashAsync((await store.GetAsync(P("/Geheimprojekt/weg.md"), Ct))!, Writers.Dev, WriterRef.Server, T0, Ct);
        var other = (await store.GetAsync(P("/Geheimprojekt/Pläne/Übernahme.md"), Ct))!;
        await MoveAsync(store, other, other with { Path = P("/Geheimprojekt/Archiv-Übernahme/Geheimakte Übernahme.md"), Revision = other.Revision + 1 });
        var moved = (await store.GetAsync(P("/Geheimprojekt/Archiv-Übernahme/Geheimakte Übernahme.md"), Ct))!;
        Assert.True(await store.TryAddCheckpointAsync(
            new Checkpoint("vor-Acme-Übernahme-Geheimprojekt-Kauf", P("/Geheimprojekt/Archiv-Übernahme"), true, Writers.Dev, T0, [new CheckpointNote(moved.Path, moved.Version)]) { Ref = WriterRef.Server },
            [new Note(moved.Path, moved.Content)], Ct));

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
        foreach (var secret in new[] { "Acme-Übernahme-Geheimprojekt", "Geheimakte Übernahme", "Geheimprojekt", "Pläne", "Archiv-Übernahme", "Papierkorb-Löschung", Token })
        {
            Assert.DoesNotContain(secret, dump, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Checkpoint Cp(string name, string path, params StoredNote[] notes) =>
        new(name, P(path), !path.EndsWith(".md", StringComparison.Ordinal), Writers.Dev, T0, [.. notes.Select(n => new CheckpointNote(n.Path, n.Version))]) { Ref = WriterRef.Server };

    [Fact]
    public async Task A_checkpoint_survives_a_restart_and_stores_each_text_once()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "same");
        var b = New("/Work/b.md", "same");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(b, Ct);

        Assert.True(await store.TryAddCheckpointAsync(Cp("one", "/Work", a, b), [new Note(a.Path, "same"), new Note(b.Path, "same")], Ct));
        Assert.True(await store.TryAddCheckpointAsync(Cp("two", "/Work/a.md", a), [new Note(a.Path, "same")], Ct));
        Assert.False(await store.TryAddCheckpointAsync(Cp("two", "/Work", a), [new Note(a.Path, "same")], Ct));

        var texts = world.Db.GetCollection<BsonDocument>("checkpoint_texts");
        Assert.Equal(1, await texts.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));

        var later = Store(world.Catalog());
        var listed = await later.ListCheckpointsAsync(P("/Work"), Ct);
        Assert.Equal(["one", "two"], listed.Select(c => c.Name));
        Assert.Equal([P("/Work/a.md"), P("/Work/b.md")], listed[0].Notes.Select(n => n.Path));
        Assert.Equal(["same", "same"], (await later.ReadCheckpointAsync(listed[0], Ct)).Select(n => n.Content));
    }

    [Fact]
    public async Task Removing_a_checkpoint_removes_only_the_texts_no_other_holds()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        var b = New("/Work/b.md", "b");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(b, Ct);
        await store.TryAddCheckpointAsync(Cp("both", "/Work", a, b), [new Note(a.Path, "a"), new Note(b.Path, "b")], Ct);
        await store.TryAddCheckpointAsync(Cp("only-a", "/Work/a.md", a), [new Note(a.Path, "a")], Ct);
        var texts = world.Db.GetCollection<BsonDocument>("checkpoint_texts");

        Assert.True(await store.TryRemoveCheckpointAsync("Work", "both", Ct));

        Assert.Equal(1, await texts.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
        var later = Store(world.Catalog());
        var left = Assert.Single(await later.ListCheckpointsAsync(P("/Work"), Ct));
        Assert.Equal("a", Assert.Single(await later.ReadCheckpointAsync(left, Ct)).Content);
        Assert.False(await store.TryRemoveCheckpointAsync("Work", "both", Ct));
    }

    [Fact]
    public async Task A_tampered_checkpoint_text_refuses_the_whole_checkpoint()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        await store.TryCreateAsync(a, Ct);
        await store.TryAddCheckpointAsync(Cp("cp", "/Work", a), [new Note(a.Path, "a")], Ct);
        var texts = world.Db.GetCollection<BsonDocument>("checkpoint_texts");
        var doc = await texts.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct);
        await texts.UpdateOneAsync(new BsonDocument("_id", doc["_id"]),
            new BsonDocument("$set", new BsonDocument("blob", new BsonBinaryData([.. doc["blob"].AsByteArray.Take(40), .. new byte[20]]))), cancellationToken: Ct);

        var later = Store(world.Catalog());
        var checkpoint = Assert.Single(await later.ListCheckpointsAsync(P("/Work"), Ct));
        await Assert.ThrowsAsync<VaultUnavailableException>(() => later.ReadCheckpointAsync(checkpoint, Ct));
    }

    [Fact]
    public async Task A_batch_creates_and_trashes_in_one_transaction()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(New("/Work/taken.md", "t"), Ct);

        // The trash goes through, then a create hits an existing note: nothing happens.
        var failing = new NoteBatch { Trashes = { a }, Creates = { New("/Work/back.md", "b"), New("/Work/taken.md", "x") } };
        Assert.Equal([P("/Work/taken.md")], await store.TryCommitAsync(failing, "dev (restore)", WriterRef.Server, T0, Ct));
        var later = Store(world.Catalog());
        Assert.Equal("a", await later.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Null(await later.ReadAsync(P("/Work/back.md"), Ct));
        Assert.Empty(await later.ListTrashAsync(P("/Work"), Ct));

        var ok = new NoteBatch { Trashes = { a }, Creates = { New("/Work/back.md", "b"), New("/Work/a.md", "a again") } };
        Assert.Empty(await store.TryCommitAsync(ok, "dev (restore)", WriterRef.Server, T0, Ct));
        later = Store(world.Catalog());
        Assert.Equal("a again", await later.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Equal("b", await later.ReadAsync(P("/Work/back.md"), Ct));
        Assert.Equal("dev (restore)", Assert.Single(await later.ListTrashAsync(P("/Work"), Ct)).DeletedBy);
    }

    [Fact]
    public async Task A_changeset_survives_a_restart()
    {
        var (world, _, store) = await WorldAsync();
        var changeset = Changeset.New(T0);
        await store.TryCreateAsync(New("/Work/a.md", "a") with { Changeset = changeset }, Ct);

        var later = Store(world.Catalog());
        Assert.Equal(changeset, (await later.GetAsync(P("/Work/a.md"), Ct))!.Changeset);
        Assert.Equal((changeset, T0), await later.LatestChangesetAsync("Work", WriterRef.Server.ChangesetKey(Writers.Dev)!, Ct));
        Assert.Null(await later.LatestChangesetAsync("Work", WriterRef.ForOwner(WriterRef.Agent, "someone-else").ChangesetKey("someone-else")!, Ct));
    }

    [Fact]
    public async Task Move_and_trash_records_keep_their_changeset_when_reopened()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        var b = New("/Work/b.md", "b");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(b, Ct);
        var changeset = Changeset.New(T0);

        var batch = new NoteBatch { Moves = { (a, a with { Path = P("/Work/moved.md"), Revision = 2 }) }, Trashes = { b }, Changeset = changeset };
        Assert.Empty(await store.TryCommitAsync(batch, Writers.Dev, WriterRef.Server, T0.AddMinutes(1), Ct));

        // A fresh catalog: the records come from the database, not the working set.
        var later = Store(world.Catalog());
        Assert.Equal(changeset, Assert.Single(await later.ListMovesAsync(P("/Work"), Ct)).Changeset);
        Assert.Equal(changeset, Assert.Single(await later.ListTrashAsync(P("/Work"), Ct)).Changeset);
        // Removals and moves keep a writer's changeset open, also without a note written in it.
        Assert.Equal((changeset, T0.AddMinutes(1)), await later.LatestChangesetAsync("Work", WriterRef.Server.ChangesetKey(Writers.Dev)!, Ct));
    }

    [Fact]
    public void Records_sealed_before_changesets_open_without_one()
    {
        var vaultKey = VaultCrypto.NewKey();
        var vault = new UnlockedVault("v1", "Work", vaultKey, T0, new VaultLocks());
        var content = VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []);
        var moveId = ObjectId.GenerateNewId();
        var trashId = ObjectId.GenerateNewId();

        // The payloads as written before moves and trash records carried a changeset.
        var move = new { From = "/Work/a.md", To = "/Work/b.md", MovedBy = "dev", MovedAt = T0 };
        var note = new { Path = "/Work/c.md", Content = "c", LastWriter = "dev", LastWriteAt = T0, History = Array.Empty<object>(), SeedContent = (string?)null };
        var trash = new { Note = note, Revision = 3L, DeletedBy = "dev", DeletedAt = T0 };
        var movedDoc = new SealedDocument { Id = moveId, Vault = "v1", Blob = VaultCrypto.Seal(content, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(move), $"move:v1:{moveId}") };
        var trashDoc = new SealedDocument { Id = trashId, Vault = "v1", Blob = VaultCrypto.Seal(content, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(trash), $"trash:v1:{trashId}") };

        Assert.Null(vault.OpenMove(movedDoc).Changeset);
        Assert.Null(vault.OpenTrash(trashDoc).Changeset);

        // And a record without a changeset is still sealed in that shape.
        var written = vault.SealMove(moveId, new MoveRecord(P("/Work/a.md"), P("/Work/b.md"), "dev", T0, Ref: WriterRef.Server));
        Assert.DoesNotContain("Changeset", Encoding.UTF8.GetString(VaultCrypto.Open(content, written, $"move:v1:{moveId}")));
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

        Assert.True(await first.TryReplaceAsync(v1, NoteWriter.Next(v1, "two", Writers.Dev, WriterRef.Server, T0.AddHours(1)), Ct));
        Assert.False(await second.TryReplaceAsync(seenBySecond, NoteWriter.Next(seenBySecond, "rival", "other", WriterRef.Server, T0.AddHours(1)), Ct));

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
        var v2 = NoteWriter.Next(v1, "two", "other", WriterRef.Server, T0.AddHours(1));
        await store.TryReplaceAsync(v1, v2, Ct);

        Assert.True(await store.TryTrashAsync(v2, Writers.Dev, WriterRef.Server, T0.AddHours(2), Ct));
        Assert.Null(await store.GetAsync(P("/Work/a.md"), Ct));
        var trashed = Assert.Single(await Store(world.Catalog()).ListTrashAsync(P("/Work"), Ct));
        Assert.Equal((T0.AddHours(2), Writers.Dev, "two"), (trashed.DeletedAt, trashed.DeletedBy, trashed.Note.Content));

        var restored = await store.TryRestoreFromTrashAsync(P("/Work/a.md"), null, Writers.Dev, WriterRef.Server, T0.AddHours(3), Ct);

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
        await Store(world.Catalog()).TryReplaceAsync(v1, NoteWriter.Next(v1, "two", Writers.Dev, WriterRef.Server, T0), Ct);

        Assert.False(await store.TryTrashAsync(v1, Writers.Dev, WriterRef.Server, T0, Ct));
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
            await store.TryTrashAsync(note, Writers.Dev, WriterRef.Server, T0.AddHours(hour), Ct);
        }

        await store.TryCreateAsync(New("/Work/a.md", "occupied"), Ct);
        Assert.Null(await store.TryRestoreFromTrashAsync(P("/Work/a.md"), null, Writers.Dev, WriterRef.Server, T0, Ct));

        await store.TryTrashAsync((await store.GetAsync(P("/Work/a.md"), Ct))!, Writers.Dev, WriterRef.Server, T0.AddHours(3), Ct);
        await store.TryRestoreFromTrashAsync(P("/Work/a.md"), VersionToken.Of("first"), Writers.Dev, WriterRef.Server, T0.AddHours(4), Ct);
        Assert.Equal("first", await Store(world.Catalog()).ReadAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Move_carries_the_note_and_leaves_a_record()
    {
        var (world, _, store) = await WorldAsync();
        var v1 = New("/Work/a.md", "one");
        await store.TryCreateAsync(v1, Ct);

        Assert.True(await MoveAsync(store, v1, v1 with { Path = P("/Work/b.md"), Revision = 2 }));

        var later = Store(world.Catalog());
        Assert.Null(await later.GetAsync(P("/Work/a.md"), Ct));
        Assert.Equal(("one", 2L), ((await later.GetAsync(P("/Work/b.md"), Ct))!.Content, (await later.GetAsync(P("/Work/b.md"), Ct))!.Revision));
        Assert.Equal(new MoveRecord(P("/Work/a.md"), P("/Work/b.md"), Writers.Dev, T0, Ref: WriterRef.Server), await later.FindMoveAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Move_never_overwrites_and_never_loses_a_concurrent_write()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(New("/Work/b.md", "b"), Ct);

        Assert.False(await MoveAsync(store, a, a with { Path = P("/Work/b.md"), Revision = 2 }));
        Assert.Equal("b", await store.ReadAsync(P("/Work/b.md"), Ct));

        await Store(world.Catalog()).TryReplaceAsync(a, NoteWriter.Next(a, "a2", Writers.Dev, WriterRef.Server, T0), Ct);
        Assert.False(await MoveAsync(store, a, a with { Path = P("/Work/c.md"), Revision = 2 }));
        var later = Store(world.Catalog());
        Assert.Equal("a2", await later.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Null(await later.ReadAsync(P("/Work/c.md"), Ct));
    }

    [Fact]
    public async Task A_batch_that_fails_at_its_last_step_writes_nothing()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        var b = New("/Work/b.md", "b");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(b, Ct);
        await store.TryCreateAsync(New("/Work/taken.md", "taken"), Ct);

        // The replace and the first move go through; the last move's target exists, so the transaction aborts.
        var taken = New("/Work/taken.md", "taken");
        var batch = new NoteBatch
        {
            Replaces = { (a, NoteWriter.Next(a, "a2", Writers.Dev, WriterRef.Server, T0)) },
            Moves = { (b, b with { Path = P("/Work/moved/b.md"), Revision = 2 }), (taken, taken with { Path = P("/Work/a.md"), Revision = 2 }) },
        };

        var conflicts = await store.TryCommitAsync(batch, Writers.Dev, WriterRef.Server, T0, Ct);

        Assert.Equal([P("/Work/a.md")], conflicts);
        var later = Store(world.Catalog());
        Assert.Equal("a", await later.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Equal("b", await later.ReadAsync(P("/Work/b.md"), Ct));
        Assert.Equal("taken", await later.ReadAsync(P("/Work/taken.md"), Ct));
        Assert.Null(await later.ReadAsync(P("/Work/moved/b.md"), Ct));
        Assert.Null(await later.FindMoveAsync(P("/Work/b.md"), Ct));
        // The store that tried still matches the database.
        Assert.Equal("a", await store.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Null(await store.ReadAsync(P("/Work/moved/b.md"), Ct));
    }

    [Fact]
    public async Task A_stale_revision_anywhere_in_a_batch_writes_nothing()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        var b = New("/Work/b.md", "b");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(b, Ct);
        await Store(world.Catalog()).TryReplaceAsync(b, NoteWriter.Next(b, "b by someone else", "other", WriterRef.Server, T0), Ct);

        var batch = new NoteBatch { Replaces = { (a, NoteWriter.Next(a, "a2", Writers.Dev, WriterRef.Server, T0)), (b, NoteWriter.Next(b, "b2", Writers.Dev, WriterRef.Server, T0)) } };

        Assert.Equal([P("/Work/b.md")], await store.TryCommitAsync(batch, Writers.Dev, WriterRef.Server, T0, Ct));
        var later = Store(world.Catalog());
        Assert.Equal("a", await later.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Equal("b by someone else", await later.ReadAsync(P("/Work/b.md"), Ct));
        Assert.Equal("b by someone else", await store.ReadAsync(P("/Work/b.md"), Ct));
    }

    [Fact]
    public async Task Moves_may_swap_into_paths_other_moves_leave()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        var b = New("/Work/b.md", "b");
        await store.TryCreateAsync(a, Ct);
        await store.TryCreateAsync(b, Ct);

        var batch = new NoteBatch { Moves = { (a, a with { Path = P("/Work/b.md"), Revision = 2 }), (b, b with { Path = P("/Work/c.md"), Revision = 2 }) } };

        Assert.Empty(await store.TryCommitAsync(batch, Writers.Dev, WriterRef.Server, T0, Ct));
        var later = Store(world.Catalog());
        Assert.Null(await later.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Equal("a", await later.ReadAsync(P("/Work/b.md"), Ct));
        Assert.Equal("b", await later.ReadAsync(P("/Work/c.md"), Ct));
    }

    [Fact]
    public async Task A_cancelled_trash_leaves_the_note_where_it_was()
    {
        var (world, _, store) = await WorldAsync();
        var a = New("/Work/a.md", "a");
        await store.TryCreateAsync(a, Ct);
        await store.GetAsync(a.Path, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryTrashAsync(a, Writers.Dev, WriterRef.Server, T0, new CancellationToken(canceled: true)));

        var later = Store(world.Catalog());
        Assert.Equal("a", await later.ReadAsync(P("/Work/a.md"), Ct));
        Assert.Empty(await later.ListTrashAsync(P("/Work"), Ct));
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
        await store.TryTrashAsync(note, Writers.Dev, WriterRef.Server, clock.GetUtcNow().UtcDateTime, Ct);

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
    public async Task The_first_read_after_a_repair_in_the_database_finds_the_note()
    {
        var (world, _, store) = await WorldAsync();
        await store.TryCreateAsync(New("/Work/a.md", "a"), Ct);
        var notes = world.Db.GetCollection<BsonDocument>("notes");
        var original = await notes.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct);
        await DamageAsync(world, "blob");
        var running = world.Catalog();
        var damaged = Store(running);
        Assert.Null(await damaged.GetAsync(P("/Work/a.md"), Ct));

        // Put the original blob back, without a restart: the very next read sees it.
        await notes.ReplaceOneAsync(new BsonDocument("_id", original["_id"]), original, cancellationToken: Ct);
        var repaired = Store(running);

        Assert.Equal("a", await repaired.ReadAsync(P("/Work/a.md"), Ct));
        Assert.False(await repaired.IsDamagedAsync(P("/Work/a.md"), Ct));
        Assert.Equal(0, await repaired.CountDamagedAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task A_note_damaged_after_loading_is_found_by_the_failed_swap()
    {
        var (world, catalog, store) = await WorldAsync();
        var note = New("/Work/a.md", "a");
        await store.TryCreateAsync(note, Ct);
        await DamageAsync(world, "rev");

        Assert.False(await Store(catalog).TryReplaceAsync(note, NoteWriter.Next(note, "b", Writers.Dev, WriterRef.Server, T0), Ct));

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

    [Fact]
    public async Task Users_vaults_are_not_counted_as_locked_for_the_dev_token()
    {
        var (world, _, _) = await WorldAsync("Mine");
        await world.Catalog().CreateForUserAsync("Lea's", "user-lea", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), Ct);
        await world.Catalog().CreateAsync("Theirs", "someone-elses-token-0123456789", saveRecoveryKey: null, Ct);

        Assert.Equal(1, await Store(world.Catalog()).CountLockedVaultsAsync(Ct));
    }
}
