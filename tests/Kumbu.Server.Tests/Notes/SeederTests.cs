// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Notes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Notes;

[Collection("mongo")]
public sealed class SeederTests(MongoFixture fixture) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kumbu-seeder-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static Seeder NewSeeder(string? seedPath, MongoNoteStore store, ListLogger<Seeder> log) =>
        new(Options.Create(new KumbuOptions { Seed = new SeedOptions { Path = seedPath } }), store, TimeProvider.System, log);

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Missing_seed_root_logs_an_error_and_does_not_throw()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var log = new ListLogger<Seeder>();
        var seeder = NewSeeder(missing, new MongoNoteStore(fixture.NewDatabase()), log);

        await seeder.StartAsync(Ct);

        var entry = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal($"Kumbu:Seed:Path does not exist: {missing}", entry.Message);
    }

    [Fact]
    public async Task Running_twice_keeps_one_note_per_file_with_the_latest_content()
    {
        var db = fixture.NewDatabase();
        var store = new MongoNoteStore(db);
        Write("Demo/a.md", "one");
        Write("Demo/b.md", "b");
        await NewSeeder(_root, store, new ListLogger<Seeder>()).StartAsync(Ct);

        Write("Demo/a.md", "two");
        await NewSeeder(_root, store, new ListLogger<Seeder>()).StartAsync(Ct);

        var count = await db.GetCollection<BsonDocument>("notes")
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
        Assert.Equal(2, count);
        Assert.Equal("two", await store.ReadAsync(P("/Demo/a.md"), Ct));
        Assert.Equal("b", await store.ReadAsync(P("/Demo/b.md"), Ct));
    }

    private static async Task SeedAsync(MongoNoteStore store, string root, ListLogger<Seeder>? log = null) =>
        await NewSeeder(root, store, log ?? new ListLogger<Seeder>()).StartAsync(Ct);

    private static NoteWriter Writer(MongoNoteStore store) => new(store, TimeProvider.System);

    [Fact]
    public async Task A_changed_file_updates_a_note_nobody_edited_and_keeps_the_old_state_in_the_history()
    {
        var store = new MongoNoteStore(fixture.NewDatabase());
        Write("Demo/a.md", "one");
        await SeedAsync(store, _root);

        Write("Demo/a.md", "two");
        await SeedAsync(store, _root);

        var note = (await store.GetAsync(P("/Demo/a.md"), Ct))!;
        Assert.Equal("two", note.Content);
        Assert.Equal("two", note.SeedContent);
        Assert.Equal(Writers.Seed, note.LastWriter);
    }

    [Fact]
    public async Task A_note_edited_through_mcp_wins_over_an_unchanged_file()
    {
        var store = new MongoNoteStore(fixture.NewDatabase());
        Write("Demo/a.md", "one");
        await SeedAsync(store, _root);
        await Writer(store).WriteAsync("/Demo/a.md", "agent", VersionToken.Of("one"), Writers.Dev, Ct);

        var log = new ListLogger<Seeder>();
        await SeedAsync(store, _root, log);

        Assert.Equal("agent", await store.ReadAsync(P("/Demo/a.md"), Ct));
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task When_both_sides_changed_the_database_wins_and_the_log_says_so()
    {
        var store = new MongoNoteStore(fixture.NewDatabase());
        Write("Demo/a.md", "one");
        await SeedAsync(store, _root);
        await Writer(store).WriteAsync("/Demo/a.md", "agent", VersionToken.Of("one"), Writers.Dev, Ct);
        Write("Demo/a.md", "disk");

        var log = new ListLogger<Seeder>();
        await SeedAsync(store, _root, log);

        Assert.Equal("agent", await store.ReadAsync(P("/Demo/a.md"), Ct));
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.StartsWith("Kept /Demo/a.md: it changed on disk and through MCP since the last seed", warning.Message);
    }

    [Fact]
    public async Task A_removed_note_is_not_brought_back_from_disk()
    {
        var store = new MongoNoteStore(fixture.NewDatabase());
        Write("Demo/a.md", "one");
        Write("Demo/b.md", "keeps the vault");
        await SeedAsync(store, _root);
        await Writer(store).RemoveAsync(P("/Demo/a.md"), Writers.Dev, Ct);

        await SeedAsync(store, _root);

        Assert.Null(await store.ReadAsync(P("/Demo/a.md"), Ct));
        Assert.Single(await store.ListTrashAsync(P("/Demo/a.md"), Ct));
    }

    [Fact]
    public async Task Notes_seeded_before_step_3_update_from_disk()
    {
        var store = new MongoNoteStore(fixture.NewDatabase());
        await store.EnsureIndexesAsync(Ct);
        await store.UpsertAsync(P("/Demo/a.md"), "old", Ct);
        Write("Demo/a.md", "new");

        await SeedAsync(store, _root);

        var note = (await store.GetAsync(P("/Demo/a.md"), Ct))!;
        Assert.Equal("new", note.Content);
        Assert.Equal(["old"], note.History.Select(h => h.Content));
    }

    [Fact]
    public async Task The_summary_counts_what_happened()
    {
        var store = new MongoNoteStore(fixture.NewDatabase());
        Write("Demo/a.md", "a");
        Write("Demo/b.md", "b");
        var log = new ListLogger<Seeder>();

        await SeedAsync(store, _root, log);

        Assert.Contains(log.Entries, e => e.Message ==
            "Seeded 2 notes in 1 vaults: 2 created, 0 updated, 0 kept as changed through MCP, 0 left in the trash (0 files skipped)");
    }
}

/// <summary>Collects formatted log messages so tests can assert on what an operator would see.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
