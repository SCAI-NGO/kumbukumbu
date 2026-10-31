// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh;

/// <summary>
/// Which notes of a shared vault were last written by someone other than the caller's own user (collab design §10.3,
/// §13 M6): <c>cat</c>, <c>rg</c> and <c>semsearch</c> say so, a cue for an agent that their text is data from another
/// person, not instructions. Decided by the writer reference (<see cref="IWriterLabels.IsOwnAsync"/>), never the
/// label, and only in vaults others reach too.
/// </summary>
public sealed class WriterMarks(INoteStore notes, IWriterLabels labels)
{
    public const string Mark = "last written by another user";

    /// <summary>Of <paramref name="paths"/>, those whose current version another user wrote, in their order.</summary>
    public async Task<IReadOnlyList<VaultPath>> ByOthersAsync(IEnumerable<VaultPath> paths, CancellationToken ct)
    {
        var shared = new Dictionary<string, bool>(StringComparer.Ordinal);
        var marked = new List<VaultPath>();
        foreach (var path in paths)
        {
            if (path.Vault is not { } vault) continue;
            if (!shared.TryGetValue(vault, out var isShared)) shared[vault] = isShared = (await notes.VaultInfoAsync(vault, ct))?.Shared == true;
            if (!isShared || await notes.GetAsync(path, ct) is not { } note) continue;
            if (!await labels.IsOwnAsync(vault, note.Ref, ct)) marked.Add(path);
        }

        return marked;
    }

    /// <summary>The notice for <paramref name="paths"/> another user wrote last, or null if none.</summary>
    public async Task<string?> NoticeAsync(IEnumerable<VaultPath> paths, CancellationToken ct)
    {
        var marked = await ByOthersAsync(paths.Distinct(), ct);
        return marked.Count switch
        {
            0 => null,
            1 => $"[{marked[0].Value}: {Mark}]",
            _ => $"[{Mark}: {string.Join(", ", marked.Select(p => p.Value))}]",
        };
    }
}
