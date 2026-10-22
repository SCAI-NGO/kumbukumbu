// SPDX-License-Identifier: AGPL-3.0-or-later
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
}
