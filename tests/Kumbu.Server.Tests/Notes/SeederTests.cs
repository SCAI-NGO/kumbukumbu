// SPDX-License-Identifier: AGPL-3.0-or-later
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
        new(Options.Create(new KumbuOptions { Seed = new SeedOptions { Path = seedPath } }), store, log);

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
