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
        var prefix = dir.IsRoot ? "/" : dir.Value + "/";
        IReadOnlyList<VaultPath> result =
            [.. _notes.Keys.Where(p => p.Value.StartsWith(prefix, StringComparison.Ordinal))];
        return Task.FromResult(result);
    }
}
