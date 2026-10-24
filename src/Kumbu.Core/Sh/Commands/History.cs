// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Kumbu.Core.Diffs;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed partial class History(INoteStore notes, Func<DateTime> now) : ICommand
{
    public string Name => "history";

    public string Usage => "history [--since T] <path>";

    public string Summary => "versions of a note; of a directory: the latest writes below it";

    public string Help => $"""
        history <path>
          List the versions of a note, newest first: version, time written (UTC), writer, lines.
          The first line is the current version. Up to {NoteWriter.HistoryLimit} earlier versions are kept. Writes by one
          writer close together in time form a changeset (it ends after {Changeset.Idle.TotalMinutes} minutes without a write, or
          after {Changeset.MaxLength.TotalHours} hour), and a changeset gives each note one version, whatever tool wrote it, so
          the state before a session survives. A restore is a version of its own.
          'restore <path> <version>' makes an earlier version current again; to keep a state for longer,
          ask the user about a named checkpoint ('help mkcp').
          The version is computed from the text, so the same version can appear twice (after a restore,
          or after changing a text back); either line restores the same text.

        history [--since T] <directory>
          The latest writes below a directory or vault (or / for every vault), newest first: time,
          writer, note, version, lines added and removed, and the changeset: writes by one writer
          close together in time, which count as one version per note. Moves get a row of their own
          (a folder move one row). --since takes 30m, 2h, 1d or a UTC time;
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

        var note = await notes.GetAsync(path, ct);
        if (note is null)
        {
            if (path.IsRoot || (await notes.ListNotesUnderAsync(path, ct)).Count > 0
                || (path.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(path.Segments[0])))
            {
                return await WritesBelowAsync(path, since, ct);
            }

            return await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        }

        var lines = new List<string> { Line(note.Content, note.LastWriter, note.LastWriteAt) + "  (current)" };
        lines.AddRange(note.History.Select(h => Line(h.Content, h.WrittenBy, h.WrittenAt)));
        return ShellResult.Ok(Lines.Join(lines)) with { Version = new NoteVersion(path.Value, note.Version) };
    }

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

        var moves = (await notes.ListMovesAsync(dir, ct)).Where(m => since is not { } s || m.MovedAt >= s);
        foreach (var group in moves.GroupBy(m => (m.MovedBy, m.MovedAt)))
        {
            var list = group.ToList();
            var what = list.Count == 1
                ? $"{list[0].From.Value} → {list[0].To.Value}"
                : $"{CommonFolder(list.Select(m => m.From))} → {CommonFolder(list.Select(m => m.To))}";
            rows.Add((group.Key.MovedAt, $"{group.Key.MovedBy} (mv)", what, list.Count == 1 ? "" : $"{list.Count} notes"));
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

    /// <summary>The deepest folder that holds all <paramref name="paths"/>, ending in '/'.</summary>
    private static string CommonFolder(IEnumerable<VaultPath> paths)
    {
        var folders = paths.Select(p => p.Segments.SkipLast(1).ToList()).ToList();
        var common = folders[0].TakeWhile((segment, i) => folders.All(f => f.Count > i && f[i] == segment)).ToList();
        return "/" + string.Join('/', common) + "/";
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
