// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Fakes;

public sealed class InMemoryNoteStore : INoteStore
{
    public static readonly DateTime Seeded = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<VaultPath, StoredNote> _notes = [];
    private readonly List<TrashedNote> _trash = [];
    private readonly List<MoveRecord> _moves = [];
    private readonly HashSet<string> _vaults = new(StringComparer.Ordinal);

    public InMemoryNoteStore(params (string Path, string Content)[] notes)
    {
        foreach (var (path, content) in notes)
        {
            if (!VaultPath.TryParse(path, out var parsed)) throw new ArgumentException($"Bad path: {path}");
            _notes[parsed] = new StoredNote(parsed, content, 1, Writers.Seed, Seeded, [], content);
            _vaults.Add(parsed.Vault!);
        }
    }

    /// <summary>Records a move directly, for tests that need a chosen writer and time.</summary>
    public void AddMove(MoveRecord move) => _moves.Add(move);

    public void AddVault(string name) => _vaults.Add(name);

    /// <summary>Paths whose stored document doesn't open.</summary>
    public HashSet<VaultPath> Damaged { get; } = [];

    public int LockedVaults { get; set; }

    public Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct) => Task.FromResult(Damaged.Contains(path));

    public Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct) =>
        Task.FromResult(Damaged.Count(d => vaultOrRoot.IsRoot || d.Vault == vaultOrRoot.Vault));

    public Task<int> CountLockedVaultsAsync(CancellationToken ct) => Task.FromResult(LockedVaults);

    public Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([.. _vaults]);

    public Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct) =>
        Task.FromResult(_moves.Where(m => m.From == from).OrderByDescending(m => m.MovedAt).FirstOrDefault());

    public Task<IReadOnlyList<MoveRecord>> ListMovesAsync(VaultPath dir, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MoveRecord>>([.. _moves.Where(m => IsBelow(m.From, dir) || IsBelow(m.To, dir))]);

    /// <summary>Makes the next compare-and-swap fail once, as if another writer got there first.</summary>
    public Func<StoredNote, StoredNote>? Interfere { get; set; }

    public Task<string?> ReadVersionAsync(VaultPath path, string version, CancellationToken ct) =>
        Task.FromResult(_notes.TryGetValue(path, out var n) ? Versions.Find(n, version) : null);

    public Task<string?> ReadAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(_notes.TryGetValue(path, out var note) ? note.Content : null);

    public Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        IReadOnlyList<VaultPath> result = [.. _notes.Keys.Where(p => IsBelow(p, dir))];
        return Task.FromResult(result);
    }

    // Reversed on purpose: callers must not rely on the store's order.
    public Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        IReadOnlyList<Note> result =
            [.. _notes.Where(n => IsBelow(n.Key, dir)).Select(n => new Note(n.Key, n.Value.Content)).Reverse()];
        return Task.FromResult(result);
    }

    public Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(_notes.GetValueOrDefault(path));

    public Task<(Changeset Changeset, DateTime LastWrite)?> LatestChangesetAsync(string vault, string writer, CancellationToken ct) =>
        Task.FromResult(_notes.Values
            .Where(n => n.Path.Vault == vault && n.Changeset is not null && Writers.Identity(n.LastWriter) == writer)
            .OrderByDescending(n => n.LastWriteAt)
            .Select(n => ((Changeset, DateTime)?)(n.Changeset!, n.LastWriteAt))
            .FirstOrDefault());

    public Task<bool> TryCreateAsync(StoredNote note, CancellationToken ct) =>
        Task.FromResult(_vaults.Contains(note.Path.Vault!) && !Damaged.Contains(note.Path) && _notes.TryAdd(note.Path, note));

    public Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct)
    {
        Race(current.Path);
        if (!_notes.TryGetValue(current.Path, out var stored) || stored.Revision != current.Revision) return Task.FromResult(false);
        _notes[current.Path] = next;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<VaultPath>> TryCommitAsync(NoteBatch batch, string by, DateTime at, CancellationToken ct)
    {
        var existing = batch.Replaces.Select(r => r.Current).Concat(batch.Moves.Select(m => m.Current)).Concat(batch.Trashes).ToList();
        foreach (var current in existing) Race(current.Path);

        // Check everything first, then write everything: all or nothing.
        var conflicts = new List<VaultPath>();
        foreach (var current in existing)
        {
            if (!_notes.TryGetValue(current.Path, out var stored) || stored.Revision != current.Revision) conflicts.Add(current.Path);
        }

        var leaving = batch.Moves.Select(m => m.Current.Path).Concat(batch.Trashes.Select(t => t.Path)).ToHashSet();
        foreach (var target in batch.Moves.Select(m => m.Target).Concat(batch.Creates))
        {
            if ((_notes.ContainsKey(target.Path) && !leaving.Contains(target.Path)) || Damaged.Contains(target.Path)) conflicts.Add(target.Path);
        }

        if (conflicts.Count > 0) return Task.FromResult<IReadOnlyList<VaultPath>>(conflicts);

        foreach (var (current, next) in batch.Replaces) _notes[current.Path] = next;
        foreach (var current in batch.Trashes)
        {
            _notes.Remove(current.Path);
            _trash.Add(new TrashedNote(current, by, at));
        }

        foreach (var (current, _) in batch.Moves) _notes.Remove(current.Path);
        foreach (var created in batch.Creates) _notes[created.Path] = created;
        foreach (var (current, target) in batch.Moves)
        {
            _notes[target.Path] = target;
            _moves.Add(new MoveRecord(current.Path, target.Path, by, at));
        }

        return Task.FromResult<IReadOnlyList<VaultPath>>([]);
    }

    private readonly List<(Checkpoint Checkpoint, IReadOnlyList<Note> Notes)> _checkpoints = [];

    public Task<IReadOnlyList<Checkpoint>> ListCheckpointsAsync(VaultPath vaultOrRoot, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Checkpoint>>([.. _checkpoints.Select(c => c.Checkpoint).Where(c => vaultOrRoot.IsRoot || c.Vault == vaultOrRoot.Vault)]);

    public Task<bool> TryAddCheckpointAsync(Checkpoint checkpoint, IReadOnlyList<Note> notes, CancellationToken ct)
    {
        if (_checkpoints.Any(c => c.Checkpoint.Vault == checkpoint.Vault && c.Checkpoint.Name == checkpoint.Name)) return Task.FromResult(false);
        _checkpoints.Add((checkpoint, notes));
        return Task.FromResult(true);
    }

    public Task<bool> TryRemoveCheckpointAsync(string vault, string name, CancellationToken ct) =>
        Task.FromResult(_checkpoints.RemoveAll(c => c.Checkpoint.Vault == vault && c.Checkpoint.Name == name) > 0);

    public Task<IReadOnlyList<Note>> ReadCheckpointAsync(Checkpoint checkpoint, CancellationToken ct) =>
        Task.FromResult(_checkpoints.Single(c => c.Checkpoint == checkpoint).Notes);

    public Task<bool> TryTrashAsync(StoredNote current, string deletedBy, DateTime deletedAt, CancellationToken ct)
    {
        Race(current.Path);
        if (!_notes.TryGetValue(current.Path, out var stored) || stored.Revision != current.Revision) return Task.FromResult(false);
        _notes.Remove(current.Path);
        _trash.Add(new TrashedNote(stored, deletedBy, deletedAt));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TrashedNote>> ListTrashAsync(VaultPath path, CancellationToken ct)
    {
        IReadOnlyList<TrashedNote> result =
            [.. _trash.Where(t => t.Note.Path == path || IsBelow(t.Note.Path, path)).OrderByDescending(t => t.DeletedAt)];
        return Task.FromResult(result);
    }

    public Task<StoredNote?> TryRestoreFromTrashAsync(VaultPath path, string? version, string writer, DateTime at, CancellationToken ct)
    {
        var latest = _trash
            .Where(t => t.Note.Path == path && (version is null || t.Note.Version == version))
            .OrderByDescending(t => t.DeletedAt)
            .FirstOrDefault();
        if (latest is null || _notes.ContainsKey(path)) return Task.FromResult<StoredNote?>(null);

        var restored = latest.Note with { Revision = latest.Note.Revision + 1, LastWriter = writer, LastWriteAt = at, Changeset = null };
        _trash.Remove(latest);
        _notes[path] = restored;
        return Task.FromResult<StoredNote?>(restored);
    }

    private void Race(VaultPath path)
    {
        if (Interfere is not { } interfere || !_notes.TryGetValue(path, out var stored)) return;
        Interfere = null;
        _notes[path] = interfere(stored) with { Revision = stored.Revision + 1 };
    }

    private static bool IsBelow(VaultPath path, VaultPath dir) =>
        path.Value.StartsWith(dir.IsRoot ? "/" : dir.Value + "/", StringComparison.Ordinal);
}
