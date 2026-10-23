// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Notes;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Notes;

[Collection("mongo")]
public sealed class SeederTests(MongoFixture fixture) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kumbu-seeder-").FullName;
    private readonly string _recovery = Directory.CreateTempSubdirectory("kumbu-recovery-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_recovery, recursive: true);
    }

    private Seeder NewSeeder(VaultCatalog catalog, ListLogger<Seeder> log, string? seedPath = null, string? recoveryKeys = null) => new(
        Options.Create(new KumbuOptions
        {
            DevToken = Token,
            Seed = new SeedOptions { Path = seedPath ?? _root },
            Recovery = new RecoveryOptions { Path = _recovery, Keys = recoveryKeys },
        }),
        catalog, TimeProvider.System, log);

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<ListLogger<Seeder>> SeedAsync(VaultWorld world)
    {
        var log = new ListLogger<Seeder>();
        await NewSeeder(world.Catalog(), log).StartAsync(Ct);
        return log;
    }

    [Fact]
    public async Task Missing_seed_root_logs_an_error_and_does_not_throw()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var log = new ListLogger<Seeder>();

        await NewSeeder(new VaultWorld(fixture.NewDatabase()).Catalog(), log, missing).StartAsync(Ct);

        var entry = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal($"Kumbu:Seed:Path does not exist: {missing}", entry.Message);
    }

    [Fact]
    public async Task Folders_become_encrypted_vaults_with_a_recovery_key_file()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        Write("Demo/a.md", "a");
        Write("Demo/sub/b.md", "b");
        Write("Ideas/c.md", "c");

        var log = await SeedAsync(world);

        var store = Store(world.Catalog());
        Assert.Equal(["Demo", "Ideas"], (await store.ListVaultsAsync(Ct)).Order());
        Assert.Equal("b", await store.ReadAsync(P("/Demo/sub/b.md"), Ct));
        Assert.Equal(Writers.Seed, (await store.GetAsync(P("/Demo/a.md"), Ct))!.LastWriter);
        Assert.Contains(log.Entries, e => e.Message == "Seeded 3 notes into 2 vaults; left 0 vaults alone (0 files skipped)");

        var files = Directory.GetFiles(_recovery).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(2, files.Count);
        Assert.StartsWith("Demo-", files[0]);
        var text = await File.ReadAllTextAsync(Path.Combine(_recovery, files[0]!), Ct);
        Assert.Contains("kumbu-rk-", text);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_recovery, files[0]!)));
        }
    }

    [Fact]
    public async Task A_vault_with_notes_is_left_alone()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        Write("Demo/a.md", "one");
        await SeedAsync(world);
        var store = Store(world.Catalog());
        var note = (await store.GetAsync(P("/Demo/a.md"), Ct))!;
        await store.TryReplaceAsync(note, NoteWriter.Next(note, "agent", Writers.Dev, DateTime.UtcNow), Ct);
        Write("Demo/a.md", "disk");
        Write("Demo/new.md", "new");

        var log = await SeedAsync(world);

        var after = Store(world.Catalog());
        Assert.Equal("agent", await after.ReadAsync(P("/Demo/a.md"), Ct));
        Assert.Null(await after.ReadAsync(P("/Demo/new.md"), Ct));
        Assert.Contains(log.Entries, e => e.Message == "Left vault Demo alone: it already holds notes (seeding only fills empty vaults)");
        Assert.Single(Directory.GetFiles(_recovery));
    }

    [Fact]
    public async Task A_vault_with_only_trash_is_left_alone_too()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        Write("Demo/a.md", "one");
        await SeedAsync(world);
        var store = Store(world.Catalog());
        await store.TryTrashAsync((await store.GetAsync(P("/Demo/a.md"), Ct))!, Writers.Dev, DateTime.UtcNow, Ct);

        await SeedAsync(world);

        Assert.Null(await Store(world.Catalog()).ReadAsync(P("/Demo/a.md"), Ct));
    }

    [Fact]
    public async Task Recovery_keys_give_a_new_dev_token_access_at_startup()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        Write("Demo/a.md", "a");
        await SeedAsync(world);
        var key = (await File.ReadAllLinesAsync(Directory.GetFiles(_recovery).Single(), Ct)).Single(l => l.StartsWith("kumbu-rk-"));
        const string newToken = "rotated-token-0123456789abcdef";

        var log = new ListLogger<Seeder>();
        await new Seeder(
            Options.Create(new KumbuOptions { DevToken = newToken, Recovery = new RecoveryOptions { Keys = key } }),
            world.Catalog(), TimeProvider.System, log).StartAsync(Ct);

        Assert.Contains(log.Entries, e => e.Message == "Recovery key opened vault Demo; the dev token now has a slot there");
        Assert.Equal("a", await Store(world.Catalog(), newToken).ReadAsync(P("/Demo/a.md"), Ct));
    }

    [Fact]
    public async Task While_vaults_dont_open_no_new_vault_is_created()
    {
        // After a token change, the locked "Demo" may be the real one: a second "Demo" would hide it.
        var world = new VaultWorld(fixture.NewDatabase());
        await world.Catalog().CreateAsync("Demo", "the-old-token-0123456789abcdef", saveRecoveryKey: null, Ct);
        Write("Demo/a.md", "a");

        var log = await SeedAsync(world);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("1 vault in the database doesn't open with the dev token"));
        Assert.Contains(log.Entries, e => e.Message == "Seeded 0 notes into 0 vaults; left 0 vaults alone; did not create 1 vault (see above) (0 files skipped)");
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("Did not create vault Demo"));
        Assert.Empty(await Store(world.Catalog()).ListVaultsAsync(Ct));
        Assert.Equal(1, await world.Catalog().CountVaultsAsync(Ct));
    }

    [Fact]
    public async Task If_the_recovery_key_cant_be_written_no_vault_is_created()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        Write("Demo/a.md", "a");
        var blocked = Path.Combine(_recovery, "not-a-directory");
        await File.WriteAllTextAsync(blocked, "", Ct);
        var log = new ListLogger<Seeder>();

        await new Seeder(
            Options.Create(new KumbuOptions { DevToken = Token, Seed = new SeedOptions { Path = _root }, Recovery = new RecoveryOptions { Path = blocked } }),
            world.Catalog(), TimeProvider.System, log).StartAsync(Ct);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("Did not create vault Demo: its recovery key could not be written"));
        Assert.Equal(0, await world.Catalog().CountVaultsAsync(Ct));
    }

    [Fact]
    public async Task An_empty_folder_becomes_an_empty_vault()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        Directory.CreateDirectory(Path.Combine(_root, "Leer"));
        Directory.CreateDirectory(Path.Combine(_root, ".hidden"));
        Write("Demo/a.md", "a");

        var log = await SeedAsync(world);

        Assert.Equal(["Demo", "Leer"], (await Store(world.Catalog()).ListVaultsAsync(Ct)).Order());
        Assert.Contains(log.Entries, e => e.Message == "Seeded 1 note into 2 vaults; left 0 vaults alone (0 files skipped)");
    }

    [Fact]
    public async Task An_empty_vault_is_left_alone_on_the_next_start()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        Directory.CreateDirectory(Path.Combine(_root, "Leer"));
        await SeedAsync(world);

        var log = await SeedAsync(world);

        Assert.Contains(log.Entries, e => e.Message == "Left vault Leer alone: it is empty, and so is its folder");
        Assert.Contains(log.Entries, e => e.Message == "Seeded 0 notes into 0 vaults; left 1 vault alone (0 files skipped)");
    }

    [Fact]
    public async Task A_recovery_key_whose_slot_exists_reminds_to_remove_it()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        Write("Demo/a.md", "a");
        await SeedAsync(world);
        var key = (await File.ReadAllLinesAsync(Directory.GetFiles(_recovery).Single(), Ct)).Single(l => l.StartsWith("kumbu-rk-"));

        var log = new ListLogger<Seeder>();
        await NewSeeder(world.Catalog(), log, recoveryKeys: key).StartAsync(Ct);

        Assert.Contains(log.Entries, e => e.Message ==
            "Recovery key for vault Demo: the dev token already has a slot there; remove KUMBU_RECOVERY_KEYS from .env (Kumbu__Recovery__Keys)");
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
