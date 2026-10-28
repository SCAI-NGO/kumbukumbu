// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Diffs;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed partial class History(INoteStore notes, Func<DateTime> now) : ICommand
{
    public string Name => "history";

    public string Usage => "history [--since T] <path>";

    public string Summary => "versions of a note or attachment; of a directory: the latest writes below it";

    public string Help => $"""
        history [--since T] <path>
          List the versions of a note, newest first: version, time written (UTC), writer, lines.
          The first line is the current version. Up to {NoteWriter.HistoryLimit} earlier versions are kept. Writes by one
          writer close together in time form a changeset (it ends after {Changeset.Idle.TotalMinutes} minutes without a write,
          {Changeset.HumanIdle.TotalMinutes} for a person in the web app or Obsidian, or after {Changeset.MaxLength.TotalHours} hour), and a changeset
          gives each note one version, whatever tool wrote it, so the state before a session survives. A note's first
          state and a restore are versions of their own.
          'restore <path> <version>' makes an earlier version current again; to keep a state for longer,
          ask the user about a named checkpoint ('help mkcp').
          The version is computed from the text, so the same version can appear twice (after a restore,
          or after changing a text back); either line restores the same text.

          An attachment (an image or a PDF) lists the same way, with its kind and size instead of lines.
          --since T lists only the versions written since then (as for a directory, below).

        history [--since T] <directory>
          The latest writes below a directory or vault (or / for every vault), newest first: time,
          writer, note, version, lines added and removed, and the changeset: writes by one writer
          close together in time, which count as one version per note. Moves and removals (to the
          trash) get a row of their own (a folder move one row). --since takes 30m, 2h, 1d or a UTC time;
          without it, the last 50. Only what the histories still hold (10 versions per note, one per
          changeset). Then 'diff <note> <version>' shows a change.

          history /Demo/welcome.md
          history /Demo --since 2h
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var operands = options.Operands;
        if (operands.Count == 0) return ShellResult.Error("history: missing operand");
        if (operands.Count > 1) return ShellResult.Error($"history: too many arguments{CommandArgs.QuoteHint}");
        if (!CommandArgs.TryParsePath(Name, operands[0], out var path, out var pathError)) return pathError!;

        DateTime? since = null;
        if (options.Value("since") is { } sinceText)
        {
            if (ParseSince(sinceText, now()) is not { } parsed)
            {
                return ShellResult.Error($"history: --since takes 30m, 2h, 1d or a UTC time like 2026-10-23 14:00 (got '{sinceText}')");
            }

            since = parsed;
        }

        if (await StoreAttachments.GetAsync(notes, path, ct) is { } attachment)
        {
            var versions = NoteHistory.Versions(attachment).Where(v => since is not { } s || v.WrittenAt >= s).Select(v =>
                $"{v.Version}  {Time(v.WrittenAt)}  {v.Writer}  {AttachmentText.Summary(v.State)}{(v.Current ? "  (current)" : "")}").ToList();
            return ShellResult.Ok(versions.Count == 0 ? NoneSince(path, since!.Value) : Lines.Join(versions));
        }

        var note = await notes.GetAsync(path, ct);
        if (note is null)
        {
            if (path.IsRoot || (await notes.ListNotesUnderAsync(path, ct)).Count > 0
                || (await StoreAttachments.UnderAsync(notes, path, ct)).Count > 0
                || (path.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(path.Segments[0])))
            {
                return await WritesBelowAsync(path, since, ct);
            }

            return await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        }

        var lines = NoteHistory.Versions(note).Where(v => since is not { } s || v.WrittenAt >= s)
            .Select(v => Line(v.Content, v.Writer, v.WrittenAt) + (v.Current ? "  (current)" : "")).ToList();
        return ShellResult.Ok(lines.Count == 0 ? NoneSince(path, since!.Value) : Lines.Join(lines)) with { Version = new NoteVersion(path.Value, note.Version) };
    }

    private static string NoneSince(VaultPath path, DateTime since) => $"(no versions of {path.Value} written since {Time(since)})";

    private static readonly Option[] Spec = [new(null, "since", TakesValue: true)];

    private const int DefaultLimit = 50;

    /// <summary>
    /// Every write the history still holds below a directory, newest first: one row per state of a note,
    /// with its changeset, and one per move on record (a folder move as one row).
    /// </summary>
    private async Task<ShellResult> WritesBelowAsync(VaultPath dir, DateTime? since, CancellationToken ct)
    {
        var rows = new List<(DateTime At, string Writer, string What, string Tail)>();
        foreach (var path in await notes.ListNotesUnderAsync(dir, ct))
        {
            if (await notes.GetAsync(path, ct) is not { } note) continue;
            var states = new List<(string Content, string Writer, DateTime At, Changeset? Changeset)> { (note.Content, note.LastWriter, note.LastWriteAt, note.Changeset) };
            states.AddRange(note.History.Select(h => (h.Content, h.WrittenBy, h.WrittenAt, h.Changeset)));
            for (var i = 0; i < states.Count; i++)
            {
                var (content, writer, at, changeset) = states[i];
                if (since is { } from && at < from) continue;
                var (added, removed) = TextDiff.Count(i + 1 < states.Count ? states[i + 1].Content : "", content);
                var cs = changeset is null ? "" : $"  changeset {changeset.Short}";
                rows.Add((at, writer, path.Value, $"{VersionToken.Of(content)}  +{added} -{removed}{cs}"));
            }
        }

        foreach (var file in await StoreAttachments.UnderAsync(notes, dir, ct))
        {
            var states = new List<(AttachmentState State, string Writer, DateTime At, Changeset? Changeset)> { (file.Current, file.LastWriter, file.LastWriteAt, file.Changeset) };
            states.AddRange(file.History.Select(h => (h.State, h.WrittenBy, h.WrittenAt, h.Changeset)));
            foreach (var (state, writer, at, changeset) in states)
            {
                if (since is { } from && at < from) continue;
                var cs = changeset is null ? "" : $"  changeset {changeset.Short}";
                rows.Add((at, writer, file.Path.Value, $"{state.Version}  {AttachmentText.Summary(state)}{cs}"));
            }
        }

        var moves = (await notes.ListMovesAsync(dir, ct)).Where(m => since is not { } s || m.MovedAt >= s);
        foreach (var group in moves.GroupBy(m => (m.MovedBy, m.MovedAt)))
        {
            // "dev (restore)" already names the operation; a plain writer gets "(mv)".
            var writer = group.Key.MovedBy.Contains(" (", StringComparison.Ordinal) ? group.Key.MovedBy : $"{group.Key.MovedBy} (mv)";

            // A folder move keeps every path below the folder, so its records share the folders left
            // after stripping that common tail. Records that share nothing with another one stand alone;
            // a restore moves notes from unrelated places and must not be merged into an invented folder move.
            foreach (var folderMove in group.GroupBy(m => FolderPair(m)))
            {
                var list = folderMove.ToList();
                if (list.Count == 1)
                {
                    rows.Add((group.Key.MovedAt, writer, $"{list[0].From.Value} → {list[0].To.Value}", ""));
                    continue;
                }

                // The shared tail includes the moved folder's own name when it keeps it ("mv /W/p/ /W/a/p/");
                // the folders below the pair that every record shares belong to the folder move.
                var below = list.Select(m => m.From.Segments.TakeLast(SharedTail(m)).SkipLast(1).ToList()).ToList();
                var depth = 0;
                while (below.All(b => depth < b.Count && b[depth] == below[0][depth])) depth++;
                var sub = string.Concat(below[0].Take(depth).Select(s => s + "/"));
                var (from, to) = folderMove.Key;
                rows.Add((group.Key.MovedAt, writer, $"{from}{sub} → {to}{sub}", $"{list.Count} notes"));
            }
        }

        foreach (var trashed in await notes.ListTrashAsync(dir, ct))
        {
            if (since is { } s && trashed.DeletedAt < s) continue;
            rows.Add((trashed.DeletedAt, trashed.DeletedBy, $"{trashed.Note.Path.Value} → trash", ""));
        }

        if (notes is IAttachmentStore store)
        {
            foreach (var trashed in await store.ListAttachmentTrashAsync(dir.IsRoot ? null : dir, ct))
            {
                if (since is { } s && trashed.DeletedAt < s) continue;
                rows.Add((trashed.DeletedAt, trashed.DeletedBy, $"{trashed.Attachment.Path.Value} → trash", ""));
            }
        }

        var shown = rows.OrderByDescending(w => w.At).ThenBy(w => w.What, TextOrder.Default).ToList();
        if (since is null && shown.Count > DefaultLimit) shown = shown[..DefaultLimit];
        if (shown.Count == 0)
        {
            return ShellResult.Ok(since is null ? $"(no writes below {dir.Value})" : $"(no writes below {dir.Value} since {Time(since.Value)})");
        }

        var writerWidth = shown.Max(w => w.Writer.Length);
        var whatWidth = shown.Max(w => w.What.Length);
        return ShellResult.Ok(Lines.Join(shown.Select(w =>
            $"{Time(w.At)}  {w.Writer.PadRight(writerWidth)}  {(w.Tail.Length == 0 ? w.What : w.What.PadRight(whatWidth) + "  " + w.Tail)}")));
    }

    /// <summary>
    /// The source and target folder of a move once the path below them (the longest tail both paths share) is
    /// stripped: the folders of a folder move, or the whole paths when the note was renamed or moved alone.
    /// </summary>
    private static (string From, string To) FolderPair(MoveRecord move)
    {
        var from = move.From.Segments;
        var to = move.To.Segments;
        var tail = SharedTail(move);
        if (tail == 0) return (move.From.Value, move.To.Value);

        string Folder(IReadOnlyList<string> segments) => "/" + string.Join('/', segments.Take(segments.Count - tail).Append("")).TrimStart('/');
        return (Folder(from), Folder(to));
    }

    /// <summary>How many trailing segments the source and target path of a move share.</summary>
    private static int SharedTail(MoveRecord move)
    {
        var from = move.From.Segments;
        var to = move.To.Segments;
        var tail = 0;
        while (tail < from.Count && tail < to.Count && from[^(tail + 1)] == to[^(tail + 1)]) tail++;
        return tail;
    }

    /// <summary>"30m", "2h", "1d", or a UTC time such as "2026-10-23 14:00" or "2026-10-23T14:00Z".</summary>
    internal static DateTime? ParseSince(string text, DateTime now)
    {
        if (RelativeRegex().Match(text) is { Success: true } m)
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[2].Value switch { "m" => now.AddMinutes(-n), "h" => now.AddHours(-n), _ => now.AddDays(-n) };
        }

        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;
    }

    [GeneratedRegex(@"^(\d+)([mhd])$")]
    private static partial Regex RelativeRegex();

    private static string Line(string content, string writer, DateTime at) =>
        $"{VersionToken.Of(content)}  {Time(at)}  {writer}  {Lines.Count(content)}";

    internal static string Time(DateTime at) => at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
