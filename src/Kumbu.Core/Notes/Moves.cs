// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh.Commands;

namespace Kumbu.Core.Notes;

public static class Moves
{
    /// <summary>How long a move is remembered, like the trash.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private const int MaxHops = 10;

    /// <summary>
    /// For a path with no note or attachment: where it was moved, following later moves, as the end of an error message
    /// ("; it was moved to /Demo/b.md on 2026-10-23 14:03 UTC (dev)"); empty if it wasn't moved.
    /// </summary>
    public static async Task<string> HintAsync(INoteReader notes, VaultPath path, CancellationToken ct)
    {
        var first = await notes.FindMoveAsync(path, ct);
        if (first is null) return "";

        var last = first;
        for (var hop = 0; hop < MaxHops && !await ExistsAsync(notes, last.To, ct); hop++)
        {
            if (await notes.FindMoveAsync(last.To, ct) is not { } next || next.MovedAt < last.MovedAt) break;
            last = next;
        }

        var exists = await ExistsAsync(notes, last.To, ct);
        return $"; it was moved to {last.To.Value} on {History.Time(first.MovedAt)} ({first.MovedBy})"
            + (exists ? "" : ", and is not there any more ('trash' may list it)");
    }

    private static async Task<bool> ExistsAsync(INoteReader notes, VaultPath path, CancellationToken ct) =>
        await notes.ReadAsync(path, ct) is not null || await StoreAttachments.GetAsync(notes, path, ct) is not null;
}
