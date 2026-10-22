// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Fakes;

public sealed class InMemoryNoteReader : INoteReader
{
    private readonly Dictionary<VaultPath, string> _notes = [];

    public InMemoryNoteReader(params (string Path, string Content)[] notes)
    {
        foreach (var (path, content) in notes)
        {
            if (!VaultPath.TryParse(path, out var parsed)) throw new ArgumentException($"Bad path: {path}");
            _notes[parsed] = content;
        }
    }

    public Task<string?> ReadAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(_notes.TryGetValue(path, out var content) ? content : null);

    public Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        IReadOnlyList<VaultPath> result = [.. _notes.Keys.Where(p => IsBelow(p, dir))];
        return Task.FromResult(result);
    }

    // Reversed on purpose: callers must not rely on the store's order.
    public Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        IReadOnlyList<Note> result =
            [.. _notes.Where(n => IsBelow(n.Key, dir)).Select(n => new Note(n.Key, n.Value)).Reverse()];
        return Task.FromResult(result);
    }

    private static bool IsBelow(VaultPath path, VaultPath dir) =>
        path.Value.StartsWith(dir.IsRoot ? "/" : dir.Value + "/", StringComparison.Ordinal);
}
