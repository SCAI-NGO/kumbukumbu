// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

public enum ActivityFileKind { Added, Changed, Renamed, Deleted }

/// <summary>One note or attachment in a changeset.</summary>
/// <param name="Path">The note's path now (its history moves with it, and so do its renames); for a trashed note,
/// its path when deleted.</param>
/// <param name="From">For <see cref="ActivityFileKind.Renamed"/>: the path the rename moved it from.</param>
/// <param name="To">For <see cref="ActivityFileKind.Renamed"/>: the path the rename moved it to (it may have moved
/// on since, to <paramref name="Path"/>).</param>
/// <param name="Version">The state this changeset left (null for Deleted, or a rename whose text is no longer kept).</param>
/// <param name="Before">The state before it: null when there was none (added) or it is no longer kept.</param>
/// <param name="BeforeLost">The state before it existed but the history no longer keeps it.</param>
/// <param name="Trashed">The note is in the trash now.</param>
/// <param name="FileKind">What the file is: "note", "image", "pdf" or "folder" (a folder's marker, whose <c>Path</c> is the marker's) (<see cref="AttachmentFormat.KindOf"/>).</param>
public sealed record ActivityFile(
    string Path, ActivityFileKind Kind, string? From, string? Version, string? Before, bool BeforeLost, bool Trashed, string? To = null,
    string FileKind = "note");

/// <summary>A changeset of a vault: what one writer did close together in time.</summary>
/// <param name="Id">Null for writes that join no changeset (restore, seed) or moves/removals recorded before moves
/// and trash records carried one; those of one writer at one instant (one commit) are one row.</param>
/// <param name="Key">Tells rows apart, also rows that end at the same instant: the paging cursor is (<c>To</c>, <c>Key</c>).
/// Opaque per viewer when <see cref="Activity.Rows"/> is given a row key (collab design §8.2).</param>
/// <param name="Restore">The row is a restore (a version, a checkpoint).</param>
/// <param name="Ref">The reference of the row's writer, to render it for a viewer (<see cref="IWriterLabels"/>).</param>
public sealed record ActivityRow(
    string? Id, string Writer, DateTime From, DateTime To, IReadOnlyList<ActivityFile> Files, string Key = "", bool Restore = false, WriterRef? Ref = null);

/// <summary>
/// Folds a vault's note and attachment states (current and history, also of trashed ones), trash records and move
/// records into changeset rows, newest first; an attachment's states fold as a note's do. Only as complete as what is
/// kept: <see cref="NoteWriter.HistoryLimit"/> versions per note or attachment, trash and move records for 30 days.
/// </summary>
public static class Activity
{
    private const string RestoreSuffix = " (restore)";

    /// <summary>One state of a note or attachment, with the state before it in the same file (null: none kept).</summary>
    private sealed record State(
        string Path, string Version, string Writer, DateTime At, Changeset? Changeset, string? Before, bool First, bool Lost, bool Trashed, WriterRef? Ref);

    /// <summary>A note or attachment going to the trash, with the version it had then.</summary>
    private sealed record Removal(string Path, string Version, string DeletedBy, DateTime DeletedAt, Changeset? Changeset, WriterRef? Ref);

    /// <param name="Path">Where the note is now (or was deleted): states, moves and removals of one note meet there.</param>
    private sealed record Item(string? Id, string Label, WriterRef? Ref, DateTime At, string Path, State? State = null, MoveRecord? Move = null, Removal? Trash = null)
    {
        public string Writer => Writers.Identity(Label);
    }

    /// <param name="before">With <paramref name="beforeKey"/>, the cursor of the last row already shown: rows that
    /// ended earlier, or at that instant with a greater key. Without a key, rows that ended earlier.</param>
    /// <param name="attachments">The vault's attachments, folded with the notes; <paramref name="moves"/> holds their
    /// moves too.</param>
    /// <param name="rowKey">Makes a row's key, from its changeset (or, without one, its commit), what the viewer gets:
    /// opaque, so that the keys of another person's rows say nothing that groups them by writer.</param>
    public static IReadOnlyList<ActivityRow> Rows(
        IReadOnlyList<StoredNote> notes, IReadOnlyList<TrashedNote> trash, IReadOnlyList<MoveRecord> moves, DateTime? before, int limit, string? beforeKey = null,
        IReadOnlyList<StoredAttachment>? attachments = null, IReadOnlyList<TrashedAttachment>? attachmentTrash = null, Func<string, string>? rowKey = null)
    {
        attachments ??= [];
        attachmentTrash ??= [];
        var states = notes.SelectMany(n => States(n, trashed: false))
            .Concat(trash.SelectMany(t => States(t.Note, trashed: true)))
            .Concat(attachments.SelectMany(a => States(a, trashed: false)))
            .Concat(attachmentTrash.SelectMany(t => States(t.Attachment, trashed: true)))
            .ToList();
        var removals = trash.Select(t => new Removal(t.Note.Path.Value, t.Note.Version, t.DeletedBy, t.DeletedAt, t.Changeset, t.Ref))
            .Concat(attachmentTrash.Select(t => new Removal(t.Attachment.Path.Value, t.Attachment.Version, t.DeletedBy, t.DeletedAt, t.Changeset, t.Ref)))
            .ToList();
        var statesByPath = states.ToLookup(s => s.Path, StringComparer.Ordinal);
        var where = new Whereabouts(moves, removals);

        var items = states.Select(s => new Item(s.Changeset?.Id, s.Writer, s.Ref, s.At, s.Path, State: s))
            .Concat(moves.Select(m => new Item(m.Changeset?.Id, m.MovedBy, m.Ref, m.MovedAt, where.After(m.To.Value, m.MovedAt), Move: m)))
            .Concat(removals.Select(r => new Item(r.Changeset?.Id, r.DeletedBy, r.Ref, r.DeletedAt, r.Path, Trash: r)));

        // By changeset; without one, by commit: one writer at one instant (a restore, an older folder mv). The writer by
        // its reference: members store no label, and two of them restoring at one instant are two rows.
        var groups = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = item.Id is { } id ? "c:" + id : $"w:{item.At.Ticks}:{item.Ref?.ChangesetKey(item.Label) ?? "label:" + item.Writer}";
            if (groups.TryGetValue(key, out var group)) group.Add(item);
            else groups[key] = [item];
        }

        return [.. groups
            .Select(g => Row(rowKey is null ? g.Key : rowKey(g.Key), g.Value, statesByPath))
            .Where(r => before is null || r.To < before || (beforeKey is not null && r.To == before && string.CompareOrdinal(r.Key, beforeKey) > 0))
            .OrderByDescending(r => r.To)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .Take(limit)];
    }

    private static IEnumerable<State> States(StoredNote note, bool trashed) =>
        States(
            note.Path.Value,
            [
                .. note.History.Reverse().Select(h => (VersionToken.Of(h.Content), h.WrittenBy, h.WrittenAt, h.Changeset, h.Ref)),
                (note.Version, note.LastWriter, note.LastWriteAt, note.Changeset, note.Ref),
            ],
            note.History.Count,
            trashed);

    private static IEnumerable<State> States(StoredAttachment attachment, bool trashed) =>
        States(
            attachment.Path.Value,
            [
                .. attachment.History.Reverse().Select(h => (h.State.Version, h.WrittenBy, h.WrittenAt, h.Changeset, h.Ref)),
                (attachment.Version, attachment.LastWriter, attachment.LastWriteAt, attachment.Changeset, attachment.Ref),
            ],
            attachment.History.Count,
            trashed);

    /// <summary>A file's states oldest first (<paramref name="chain"/>: history, then the current state), each with its
    /// predecessor.</summary>
    private static IEnumerable<State> States(
        string path, List<(string Version, string Writer, DateTime At, Changeset? Changeset, WriterRef? Ref)> chain, int history, bool trashed)
    {
        // A full history may have dropped the states before its oldest.
        var lost = history >= NoteWriter.HistoryLimit;
        for (var i = 0; i < chain.Count; i++)
        {
            var (version, writer, at, changeset, reference) = chain[i];
            yield return new State(path, version, writer, at, changeset, i == 0 ? null : chain[i - 1].Version, First: i == 0 && !lost, Lost: i == 0 && lost, trashed, reference);
        }
    }

    /// <summary>
    /// Where a note that was at a path at some time went afterwards: along the moves made after that time, until it
    /// was deleted (a later note at the same path is another note).
    /// </summary>
    private sealed class Whereabouts(IReadOnlyList<MoveRecord> moves, IReadOnlyList<Removal> removals)
    {
        private readonly ILookup<string, MoveRecord> _from = moves.ToLookup(m => m.From.Value, StringComparer.Ordinal);
        private readonly ILookup<string, DateTime> _deleted = removals.ToLookup(r => r.Path, r => r.DeletedAt, StringComparer.Ordinal);

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
        return new ActivityRow(group[0].Id, group[0].Writer, group.Min(i => i.At), group.Max(i => i.At), files, key, restore, group[0].Ref);
    }

    private static ActivityFile File(string path, List<Item> items, ILookup<string, State> statesByPath)
    {
        var states = items.Where(i => i.State is not null).Select(i => i.State!).OrderBy(s => s.At).ToList();
        var move = items.Where(i => i.Move is not null).Select(i => i.Move!).OrderBy(m => m.MovedAt).FirstOrDefault();
        var removal = items.FirstOrDefault(i => i.Trash is not null)?.Trash;
        var oldest = states.FirstOrDefault();
        var newest = states.LastOrDefault();
        var trashed = removal is not null || states.Any(s => s.Trashed);
        var fileKind = AttachmentFormat.KindOf(path);

        if (removal is not null)
        {
            // Deleted: the text before is what this changeset found, or, if it also edited it, what was before that edit.
            return new ActivityFile(path, ActivityFileKind.Deleted, null, null, oldest is null ? removal.Version : oldest.Before, oldest?.Lost ?? false, true, FileKind: fileKind);
        }

        if (oldest is not null)
        {
            var kind = oldest.First ? ActivityFileKind.Added : move is not null ? ActivityFileKind.Renamed : ActivityFileKind.Changed;
            return kind == ActivityFileKind.Renamed
                ? new ActivityFile(path, kind, move!.From.Value, newest!.Version, oldest.Before, oldest.Lost, trashed, move.To.Value, fileKind)
                : new ActivityFile(path, kind, null, newest!.Version, oldest.Before, oldest.Lost, trashed, FileKind: fileKind);
        }

        // A rename without a new text: the note's state at that time, unchanged, on both sides (when still kept).
        var then = statesByPath[path].Where(s => s.At <= move!.MovedAt).MaxBy(s => s.At);
        return new ActivityFile(path, ActivityFileKind.Renamed, move!.From.Value, then?.Version, then?.Version, false, then?.Trashed ?? false, move.To.Value, fileKind);
    }
}
