// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Fakes;

public sealed class InMemoryNoteStore : INoteStore
{
    public static readonly DateTime Seeded = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<VaultPath, StoredNote> _notes = [];
    private readonly List<TrashedNote> _trash = [];

    public InMemoryNoteStore(params (string Path, string Content)[] notes)
    {
        foreach (var (path, content) in notes)
        {
            if (!VaultPath.TryParse(path, out var parsed)) throw new ArgumentException($"Bad path: {path}");
            _notes[parsed] = new StoredNote(parsed, content, 1, Writers.Seed, Seeded, [], content);
        }
    }

    /// <summary>Makes the next compare-and-swap fail once, as if another writer got there first.</summary>
    public Func<StoredNote, StoredNote>? Interfere { get; set; }

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

    public Task<bool> TryCreateAsync(StoredNote note, CancellationToken ct) => Task.FromResult(_notes.TryAdd(note.Path, note));

    public Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct)
    {
        Race(current.Path);
        if (!_notes.TryGetValue(current.Path, out var stored) || stored.Revision != current.Revision) return Task.FromResult(false);
        _notes[current.Path] = next;
        return Task.FromResult(true);
    }

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

        var restored = latest.Note with { Revision = latest.Note.Revision + 1, LastWriter = writer, LastWriteAt = at };
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
