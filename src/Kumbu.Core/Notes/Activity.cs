// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

public enum ActivityFileKind { Added, Changed, Renamed, Deleted }

/// <summary>One note in a changeset.</summary>
/// <param name="Path">The note's path now (its history moves with it); for a trashed note, its path when deleted.</param>
/// <param name="From">For <see cref="ActivityFileKind.Renamed"/>: the path it had before.</param>
/// <param name="Version">The state this changeset left (null for Deleted).</param>
/// <param name="Before">The state before it: null when there was none (added) or it is no longer kept.</param>
/// <param name="BeforeLost">The state before it existed but the history no longer keeps it.</param>
/// <param name="Trashed">The note is in the trash now.</param>
public sealed record ActivityFile(string Path, ActivityFileKind Kind, string? From, string? Version, string? Before, bool BeforeLost, bool Trashed);

/// <summary>A changeset of a vault: what one writer did close together in time.</summary>
/// <param name="Id">Null for a write that joins no changeset (restore, seed) or a move/removal recorded before
/// moves and trash records carried one; each such write is a row of its own.</param>
public sealed record ActivityRow(string? Id, string Writer, DateTime From, DateTime To, IReadOnlyList<ActivityFile> Files);

/// <summary>
/// Folds a vault's note states (current and history, also of trashed notes), trash records and move records into
/// changeset rows, newest first. Only as complete as what is kept: <see cref="NoteWriter.HistoryLimit"/> versions per
/// note, trash and move records for 30 days.
/// </summary>
public static class Activity
{
    /// <summary>One state of a note, with the state before it in the same note (null: none kept).</summary>
    private sealed record State(string Path, string Version, string Writer, DateTime At, Changeset? Changeset, string? Before, bool First, bool Lost, bool Trashed);

    private sealed record Item(string? Id, string Writer, DateTime At, string Path, State? State = null, MoveRecord? Move = null, TrashedNote? Trash = null);

    public static IReadOnlyList<ActivityRow> Rows(
        IReadOnlyList<StoredNote> notes, IReadOnlyList<TrashedNote> trash, IReadOnlyList<MoveRecord> moves, DateTime? before, int limit)
    {
        var states = notes.SelectMany(n => States(n, trashed: false))
            .Concat(trash.SelectMany(t => States(t.Note, trashed: true)))
            .ToList();

        var items = states.Select(s => new Item(s.Changeset?.Id, Writers.Identity(s.Writer), s.At, s.Path, State: s))
            .Concat(moves.Select(m => new Item(m.Changeset?.Id, Writers.Identity(m.MovedBy), m.MovedAt, m.To.Value, Move: m)))
            .Concat(trash.Select(t => new Item(t.Changeset?.Id, Writers.Identity(t.DeletedBy), t.DeletedAt, t.Note.Path.Value, Trash: t)));

        // Items without a changeset are rows of their own; the rest group by id.
        var groups = new List<List<Item>>();
        var byId = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Id is null) groups.Add([item]);
            else if (byId.TryGetValue(item.Id, out var group)) group.Add(item);
            else groups.Add(byId[item.Id] = [item]);
        }

        return [.. groups
            .Select(g => Row(g, states))
            .Where(r => before is null || r.To < before)
            .OrderByDescending(r => r.To)
            .ThenByDescending(r => r.From)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
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

    private static ActivityRow Row(List<Item> group, List<State> all)
    {
        var files = group
            .GroupBy(i => i.Path, StringComparer.Ordinal)
            .Select(g => File(g.Key, [.. g], all))
            .OrderBy(f => f.Path, TextOrder.Default)
            .ToList();
        return new ActivityRow(group[0].Id, group[0].Writer, group.Min(i => i.At), group.Max(i => i.At), files);
    }

    private static ActivityFile File(string path, List<Item> items, List<State> all)
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
            return new ActivityFile(path, kind, kind == ActivityFileKind.Renamed ? move!.From.Value : null, newest!.Version, oldest.Before, oldest.Lost, trashed);
        }

        // A rename without a new text: the note's state at that time, unchanged, on both sides (when still kept).
        var then = all.Where(s => s.Path == path && s.At <= move!.MovedAt).MaxBy(s => s.At);
        return new ActivityFile(path, ActivityFileKind.Renamed, move!.From.Value, then?.Version, then?.Version, false, then?.Trashed ?? false);
    }
}
