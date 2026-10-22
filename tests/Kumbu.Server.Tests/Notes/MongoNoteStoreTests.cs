// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Notes;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Notes;

[Collection("mongo")]
public sealed class MongoNoteStoreTests(MongoFixture fixture)
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static async Task<MongoNoteStore> NewStoreAsync(IMongoDatabase db)
    {
        var store = new MongoNoteStore(db);
        await store.EnsureIndexesAsync(CancellationToken.None);
        return store;
    }

    private static async Task<string[]> ListAsync(MongoNoteStore store, string dir) =>
        [.. (await store.ListNotesUnderAsync(P(dir), CancellationToken.None)).Select(p => p.Value).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Upsert_then_read_round_trips()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        await store.UpsertAsync(P("/Work/a.md"), "# A", CancellationToken.None);
        Assert.Equal("# A", await store.ReadAsync(P("/Work/a.md"), CancellationToken.None));
    }

    [Fact]
    public async Task Upsert_twice_keeps_one_document_with_latest_content()
    {
        var db = fixture.NewDatabase();
        var store = await NewStoreAsync(db);
        await store.UpsertAsync(P("/Work/a.md"), "one", CancellationToken.None);
        await store.UpsertAsync(P("/Work/a.md"), "two", CancellationToken.None);

        Assert.Equal(1, await db.GetCollection<BsonDocument>("notes").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("two", await store.ReadAsync(P("/Work/a.md"), CancellationToken.None));
    }

    [Fact]
    public async Task Read_of_missing_note_is_null()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        Assert.Null(await store.ReadAsync(P("/Work/none.md"), CancellationToken.None));
    }

    [Fact]
    public async Task List_root_returns_all_notes()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        await store.UpsertAsync(P("/Work/a.md"), "a", CancellationToken.None);
        await store.UpsertAsync(P("/Home/b/c.md"), "c", CancellationToken.None);
        Assert.Equal(["/Home/b/c.md", "/Work/a.md"], await ListAsync(store, "/"));
    }

    [Fact]
    public async Task List_does_not_confuse_sibling_vaults_sharing_a_prefix()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        await store.UpsertAsync(P("/Work/a.md"), "a", CancellationToken.None);
        await store.UpsertAsync(P("/Workshop/w.md"), "w", CancellationToken.None);
        Assert.Equal(["/Work/a.md"], await ListAsync(store, "/Work"));
    }

    [Fact]
    public async Task List_handles_regex_metacharacters_in_names()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        await store.UpsertAsync(P("/C++/x.md"), "x", CancellationToken.None);
        await store.UpsertAsync(P("/(draft)/y.md"), "y", CancellationToken.None);

        Assert.Equal(["/C++/x.md"], await ListAsync(store, "/C++"));
        Assert.Equal(["/(draft)/y.md"], await ListAsync(store, "/(draft)"));
        Assert.Empty(await ListAsync(store, "/C+"));
    }

    [Fact]
    public async Task Read_notes_under_returns_paths_with_content_below_the_directory_only()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        await store.UpsertAsync(P("/Work/a.md"), "a", CancellationToken.None);
        await store.UpsertAsync(P("/Work/sub/b.md"), "b", CancellationToken.None);
        await store.UpsertAsync(P("/Workshop/w.md"), "w", CancellationToken.None);

        var notes = await store.ReadNotesUnderAsync(P("/Work"), CancellationToken.None);

        Assert.Equal(
            ["/Work/a.md:a", "/Work/sub/b.md:b"],
            notes.Select(n => $"{n.Path.Value}:{n.Content}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Read_notes_under_root_returns_every_note()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        await store.UpsertAsync(P("/Work/a.md"), "a", CancellationToken.None);
        await store.UpsertAsync(P("/(draft)/y.md"), "y", CancellationToken.None);

        var notes = await store.ReadNotesUnderAsync(VaultPath.Root, CancellationToken.None);

        Assert.Equal(["/(draft)/y.md", "/Work/a.md"], notes.Select(n => n.Path.Value).Order(StringComparer.Ordinal));
    }

    private static readonly DateTime T0 = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private static StoredNote New(string path, string content) => new(P(path), content, 1, Writers.Dev, T0, []);

    [Fact]
    public async Task Create_then_get_round_trips_every_field()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        var note = New("/Work/a.md", "a") with
        {
            History = [new HistoryEntry("older", Writers.Seed, T0.AddDays(-1))],
            SeedContent = "older",
        };

        Assert.True(await store.TryCreateAsync(note, CancellationToken.None));
        var read = await store.GetAsync(P("/Work/a.md"), CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(note with { History = [] }, read with { History = [] });
        Assert.Equal(note.History, read.History);
    }

    [Fact]
    public async Task Create_fails_if_the_note_exists()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        Assert.True(await store.TryCreateAsync(New("/Work/a.md", "a"), CancellationToken.None));
        Assert.False(await store.TryCreateAsync(New("/Work/a.md", "b"), CancellationToken.None));
        Assert.Equal("a", await store.ReadAsync(P("/Work/a.md"), CancellationToken.None));
    }

    [Fact]
    public async Task Replace_is_a_compare_and_swap_on_the_revision()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        var v1 = New("/Work/a.md", "one");
        await store.TryCreateAsync(v1, CancellationToken.None);
        var v2 = NoteWriter.Next(v1, "two", Writers.Dev, T0.AddHours(1));
        var rival = NoteWriter.Next(v1, "rival", "other", T0.AddHours(1));

        Assert.True(await store.TryReplaceAsync(v1, v2, CancellationToken.None));
        Assert.False(await store.TryReplaceAsync(v1, rival, CancellationToken.None));

        var read = (await store.GetAsync(P("/Work/a.md"), CancellationToken.None))!;
        Assert.Equal("two", read.Content);
        Assert.Equal(2, read.Revision);
        Assert.Equal(["one"], read.History.Select(h => h.Content));
    }

    [Fact]
    public async Task Replace_works_on_a_document_from_before_step_3()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        await store.UpsertAsync(P("/Work/a.md"), "legacy", CancellationToken.None);
        var legacy = (await store.GetAsync(P("/Work/a.md"), CancellationToken.None))!;
        Assert.Equal(0, legacy.Revision);
        Assert.Equal(Writers.Seed, legacy.LastWriter);
        Assert.Empty(legacy.History);
        Assert.Null(legacy.SeedContent);

        Assert.True(await store.TryReplaceAsync(legacy, NoteWriter.Next(legacy, "new", Writers.Dev, T0), CancellationToken.None));
        Assert.Equal("new", await store.ReadAsync(P("/Work/a.md"), CancellationToken.None));
    }

    [Fact]
    public async Task Trash_moves_the_note_with_its_history_and_restore_brings_it_back()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        var v1 = New("/Work/a.md", "one");
        await store.TryCreateAsync(v1, CancellationToken.None);
        var v2 = NoteWriter.Next(v1, "two", "other", T0.AddHours(1));
        await store.TryReplaceAsync(v1, v2, CancellationToken.None);

        Assert.True(await store.TryTrashAsync(v2, Writers.Dev, T0.AddHours(2), CancellationToken.None));
        Assert.Null(await store.GetAsync(P("/Work/a.md"), CancellationToken.None));
        var trashed = Assert.Single(await store.ListTrashAsync(P("/Work"), CancellationToken.None));
        Assert.Equal(T0.AddHours(2), trashed.DeletedAt);
        Assert.Equal(Writers.Dev, trashed.DeletedBy);

        var restored = await store.TryRestoreFromTrashAsync(P("/Work/a.md"), null, Writers.Dev, T0.AddHours(3), CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("two", restored.Content);
        Assert.Equal(["one"], restored.History.Select(h => h.Content));
        Assert.Equal(restored with { History = [] }, (await store.GetAsync(P("/Work/a.md"), CancellationToken.None))! with { History = [] });
        Assert.Empty(await store.ListTrashAsync(P("/Work"), CancellationToken.None));
    }

    [Fact]
    public async Task Trash_fails_and_leaves_no_copy_if_the_note_changed()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        var v1 = New("/Work/a.md", "one");
        await store.TryCreateAsync(v1, CancellationToken.None);
        await store.TryReplaceAsync(v1, NoteWriter.Next(v1, "two", Writers.Dev, T0), CancellationToken.None);

        Assert.False(await store.TryTrashAsync(v1, Writers.Dev, T0, CancellationToken.None));
        Assert.Empty(await store.ListTrashAsync(VaultPath.Root, CancellationToken.None));
        Assert.Equal("two", await store.ReadAsync(P("/Work/a.md"), CancellationToken.None));
    }

    [Fact]
    public async Task Restore_from_trash_takes_the_latest_and_never_overwrites()
    {
        var store = await NewStoreAsync(fixture.NewDatabase());
        foreach (var (content, hour) in new[] { ("first", 1), ("second", 2) })
        {
            var note = New("/Work/a.md", content);
            await store.TryCreateAsync(note, CancellationToken.None);
            await store.TryTrashAsync(note, Writers.Dev, T0.AddHours(hour), CancellationToken.None);
        }

        await store.TryCreateAsync(New("/Work/a.md", "occupied"), CancellationToken.None);
        Assert.Null(await store.TryRestoreFromTrashAsync(P("/Work/a.md"), null, Writers.Dev, T0, CancellationToken.None));
        Assert.Equal(2, (await store.ListTrashAsync(P("/Work/a.md"), CancellationToken.None)).Count);

        await store.TryTrashAsync((await store.GetAsync(P("/Work/a.md"), CancellationToken.None))!, Writers.Dev, T0.AddHours(3), CancellationToken.None);
        await store.TryRestoreFromTrashAsync(P("/Work/a.md"), null, Writers.Dev, T0.AddHours(4), CancellationToken.None);
        Assert.Equal("occupied", await store.ReadAsync(P("/Work/a.md"), CancellationToken.None));
        Assert.Equal(
            ["second", "first"],
            (await store.ListTrashAsync(P("/Work/a.md"), CancellationToken.None)).Select(t => t.Note.Content));
    }

    [Fact]
    public async Task Trash_documents_expire_after_thirty_days()
    {
        var db = fixture.NewDatabase();
        await NewStoreAsync(db);

        var indexes = await (await db.GetCollection<BsonDocument>("trash").Indexes.ListAsync(TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);

        var ttl = Assert.Single(indexes, i => i.Contains("expireAfterSeconds"));
        Assert.Equal(new BsonDocument("deletedAt", 1), ttl["key"].AsBsonDocument);
        Assert.Equal(30 * 24 * 3600, ttl["expireAfterSeconds"].ToInt64());
    }
}
