// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

public enum ActivityFileKind { Added, Changed, Renamed, Deleted }

/// <summary>One note in a changeset.</summary>
/// <param name="Path">The note's path now (its history moves with it, and so do its renames); for a trashed note,
/// its path when deleted.</param>
/// <param name="From">For <see cref="ActivityFileKind.Renamed"/>: the path the rename moved it from.</param>
/// <param name="To">For <see cref="ActivityFileKind.Renamed"/>: the path the rename moved it to (it may have moved
/// on since, to <paramref name="Path"/>).</param>
/// <param name="Version">The state this changeset left (null for Deleted, or a rename whose text is no longer kept).</param>
/// <param name="Before">The state before it: null when there was none (added) or it is no longer kept.</param>
/// <param name="BeforeLost">The state before it existed but the history no longer keeps it.</param>
/// <param name="Trashed">The note is in the trash now.</param>
public sealed record ActivityFile(
    string Path, ActivityFileKind Kind, string? From, string? Version, string? Before, bool BeforeLost, bool Trashed, string? To = null);

/// <summary>A changeset of a vault: what one writer did close together in time.</summary>
/// <param name="Id">Null for writes that join no changeset (restore, seed) or moves/removals recorded before moves
/// and trash records carried one; those of one writer at one instant (one commit) are one row.</param>
/// <param name="Key">Tells rows apart, also rows that end at the same instant: the paging cursor is (<c>To</c>, <c>Key</c>).</param>
/// <param name="Restore">The row is a restore (a version, a checkpoint).</param>
public sealed record ActivityRow(string? Id, string Writer, DateTime From, DateTime To, IReadOnlyList<ActivityFile> Files, string Key = "", bool Restore = false);

/// <summary>
/// Folds a vault's note states (current and history, also of trashed notes), trash records and move records into
/// changeset rows, newest first. Only as complete as what is kept: <see cref="NoteWriter.HistoryLimit"/> versions per
/// note, trash and move records for 30 days.
/// </summary>
public static class Activity
{
    private const string RestoreSuffix = " (restore)";

    /// <summary>One state of a note, with the state before it in the same note (null: none kept).</summary>
    private sealed record State(string Path, string Version, string Writer, DateTime At, Changeset? Changeset, string? Before, bool First, bool Lost, bool Trashed);

    /// <param name="Path">Where the note is now (or was deleted): states, moves and removals of one note meet there.</param>
    private sealed record Item(string? Id, string Label, DateTime At, string Path, State? State = null, MoveRecord? Move = null, TrashedNote? Trash = null)
    {
        public string Writer => Writers.Identity(Label);
    }

    /// <param name="before">With <paramref name="beforeKey"/>, the cursor of the last row already shown: rows that
    /// ended earlier, or at that instant with a greater key. Without a key, rows that ended earlier.</param>
    public static IReadOnlyList<ActivityRow> Rows(
        IReadOnlyList<StoredNote> notes, IReadOnlyList<TrashedNote> trash, IReadOnlyList<MoveRecord> moves, DateTime? before, int limit, string? beforeKey = null)
    {
        var states = notes.SelectMany(n => States(n, trashed: false))
            .Concat(trash.SelectMany(t => States(t.Note, trashed: true)))
            .ToList();
        var statesByPath = states.ToLookup(s => s.Path, StringComparer.Ordinal);
        var where = new Whereabouts(moves, trash);

        var items = states.Select(s => new Item(s.Changeset?.Id, s.Writer, s.At, s.Path, State: s))
            .Concat(moves.Select(m => new Item(m.Changeset?.Id, m.MovedBy, m.MovedAt, where.After(m.To.Value, m.MovedAt), Move: m)))
            .Concat(trash.Select(t => new Item(t.Changeset?.Id, t.DeletedBy, t.DeletedAt, t.Note.Path.Value, Trash: t)));

        // By changeset; without one, by commit: one writer at one instant (a restore, an older folder mv).
        var groups = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = item.Id is { } id ? "c:" + id : $"w:{item.At.Ticks}:{item.Writer}";
            if (groups.TryGetValue(key, out var group)) group.Add(item);
            else groups[key] = [item];
        }

        return [.. groups
            .Select(g => Row(g.Key, g.Value, statesByPath))
            .Where(r => before is null || r.To < before || (beforeKey is not null && r.To == before && string.CompareOrdinal(r.Key, beforeKey) > 0))
            .OrderByDescending(r => r.To)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(limit)];
    }

    /// <summary>A note's states oldest first, each with its predecessor.</summary>
    private static IEnumerable<State> States(StoredNote note, bool trashed)
    {
        var path = note.Path.Value;
        var chain = note.History.Reverse()
            .Select(h => (Version: VersionToken.Of(h.Content), h.WrittenBy, h.WrittenAt, h.Changeset))
            .Append((note.Version, note.LastWriter, note.LastWriteAt, note.Changeset))
            .ToList();
        // A full history may have dropped the states before its oldest.
        var lost = note.History.Count >= NoteWriter.HistoryLimit;
        for (var i = 0; i < chain.Count; i++)
        {
            var (version, writer, at, changeset) = chain[i];
            yield return new State(path, version, writer, at, changeset, i == 0 ? null : chain[i - 1].Version, First: i == 0 && !lost, Lost: i == 0 && lost, trashed);
        }
    }

    /// <summary>
    /// Where a note that was at a path at some time went afterwards: along the moves made after that time, until it
    /// was deleted (a later note at the same path is another note).
    /// </summary>
    private sealed class Whereabouts(IReadOnlyList<MoveRecord> moves, IReadOnlyList<TrashedNote> trash)
    {
        private readonly ILookup<string, MoveRecord> _from = moves.ToLookup(m => m.From.Value, StringComparer.Ordinal);
        private readonly ILookup<string, DateTime> _deleted = trash.ToLookup(t => t.Note.Path.Value, t => t.DeletedAt, StringComparer.Ordinal);

        public string After(string path, DateTime at)
        {
            // Bounded: each step goes strictly forward in time, but records can be odd, so not for ever.
            for (var step = 0; step < 64; step++)
            {
                var next = _from[path].Where(m => m.MovedAt > at).MinBy(m => m.MovedAt);
                if (next is null) return path;
                if (_deleted[path].Any(d => d > at && d <= next.MovedAt)) return path;
                (path, at) = (next.To.Value, next.MovedAt);
            }

            return path;
        }
    }

    private static ActivityRow Row(string key, List<Item> group, ILookup<string, State> statesByPath)
    {
        var files = group
            .GroupBy(i => i.Path, StringComparer.Ordinal)
            .Select(g => File(g.Key, [.. g], statesByPath))
            .OrderBy(f => f.Path, TextOrder.Default)
            .ToList();
        var restore = group[0].Id is null && group.Any(i => i.Label.EndsWith(RestoreSuffix, StringComparison.Ordinal));
        return new ActivityRow(group[0].Id, group[0].Writer, group.Min(i => i.At), group.Max(i => i.At), files, key, restore);
    }

    private static ActivityFile File(string path, List<Item> items, ILookup<string, State> statesByPath)
    {
        var states = items.Where(i => i.State is not null).Select(i => i.State!).OrderBy(s => s.At).ToList();
        var move = items.Where(i => i.Move is not null).Select(i => i.Move!).OrderBy(m => m.MovedAt).FirstOrDefault();
        var removal = items.FirstOrDefault(i => i.Trash is not null)?.Trash;
        var oldest = states.FirstOrDefault();
        var newest = states.LastOrDefault();
        var trashed = removal is not null || states.Any(s => s.Trashed);

        if (removal is not null)
        {
            // Deleted: the text before is what this changeset found, or, if it also edited it, what was before that edit.
            return new ActivityFile(path, ActivityFileKind.Deleted, null, null, oldest is null ? removal.Note.Version : oldest.Before, oldest?.Lost ?? false, true);
        }

        if (oldest is not null)
        {
            var kind = oldest.First ? ActivityFileKind.Added : move is not null ? ActivityFileKind.Renamed : ActivityFileKind.Changed;
            return kind == ActivityFileKind.Renamed
                ? new ActivityFile(path, kind, move!.From.Value, newest!.Version, oldest.Before, oldest.Lost, trashed, move.To.Value)
                : new ActivityFile(path, kind, null, newest!.Version, oldest.Before, oldest.Lost, trashed);
        }

        // A rename without a new text: the note's state at that time, unchanged, on both sides (when still kept).
        var then = statesByPath[path].Where(s => s.At <= move!.MovedAt).MaxBy(s => s.At);
        return new ActivityFile(path, ActivityFileKind.Renamed, move!.From.Value, then?.Version, then?.Version, false, then?.Trashed ?? false, move.To.Value);
    }
}
