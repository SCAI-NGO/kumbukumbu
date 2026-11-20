// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// Who wrote what (blame spec §6): the writer who last changed each run of lines of a note, from its record
/// (<see cref="Authorship.Of"/>), or the lines per writer of each note below a folder. Writers are shown through the
/// labels as <c>history</c> shows them, and runs are formed after that, so two writers shown alike make one run.
/// </summary>
/// <param name="labels">How writers are shown to the caller (collab design §8.2); the labels as they are by default.</param>
public sealed class Blame(INoteStore notes, IWriterLabels? labels = null) : ICommand
{
    private readonly IWriterLabels _labels = labels ?? PlainWriterLabels.Instance;

    public string Name => "blame";

    public string Usage => "blame [--lines] <path>[#section]";

    public string Summary => "who wrote what: the writer of each run of lines of a note; of a folder: lines per writer";

    public string Help => $"""
        blame [--lines] <path>[#section]
          Who wrote what: for each run of lines of a note, the writer who last changed them and
          when (the newest write in the run, UTC). A line counts as changed when any character in it
          changed, indentation and reflow included, as in git blame. Recorded at every write since
          Kumbu² 0.6.0; lines older than that come from the history, which goes back {NoteWriter.HistoryLimit}
          versions, and read "earlier than <time>" where it ends. A section (note.md#Heading) or a
          block (note.md#^block) limits it to that part. --lines prints every line with its writer,
          time and text.
          A copy (cp, or Duplicate in the web app) is a new note: every line is its copier's.
        blame <folder>
          For each note below the folder: lines per writer (blank lines not counted), then the
          total per writer and the number of notes. At most {MaxNotes} notes, fewer if they don't fit
          one call; the rest are counted before the total: "… and N more notes; blame a folder below
          it". --lines takes a note, not a folder.

          blame /Work/protocol.md
          blame /Work/protocol.md#Results --lines
          blame /Work/Protocols/
        """;

    public bool ReadsInput => false;

    private const string EarlierThan = "earlier than";

    internal const int MaxNotes = 200;

    private static readonly Option[] Spec = [new(null, "lines")];

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var operands = options.Operands;
        if (operands.Count == 0) return ShellResult.Error("blame: missing operand");
        if (operands.Count > 1) return ShellResult.Error($"blame: too many arguments{CommandArgs.QuoteHint}");

        var (pathText, fragment) = SectionAddress.Split(operands[0]);
        if (!CommandArgs.TryParsePath(Name, pathText, out var path, out var pathError)) return pathError!;
        if (await StoreAttachments.GetAsync(notes, path, ct) is not null) return ShellResult.Error($"blame: {path.Value}: only notes have lines");

        var note = await notes.GetAsync(path, ct);
        if (note is null)
        {
            if (fragment is null && (path.IsRoot || (await notes.ListNotesUnderAsync(path, ct)).Count > 0
                || (path.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(path.Segments[0]))))
            {
                if (options.Has("lines")) return ShellResult.Error("blame: --lines takes a note, not a folder");
                return await FolderAsync(path, ct);
            }

            return await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        }

        var lines = Lines.Split(note.Content);
        var (from, to) = (0, lines.Count);
        if (fragment is not null)
        {
            if (Blocks.TryId(fragment, out var id))
            {
                if (Blocks.Find(lines, id) is not { } block) return ShellResult.Error($"blame: {path.Value}#^{id}: no such block");
                (from, to) = (block.Start, block.End);
            }
            else if (SectionLookup.TryFind(path, note.Content, fragment, out var match, out var sectionError))
            {
                (from, to) = (match.Heading.Line, match.Heading.ContentEnd);
            }
            else
            {
                return ShellResult.Error($"blame: {sectionError}");
            }
        }

        var record = Authorship.Of(note);
        var shown = await ShownAsync(path, record, ct);
        var byLine = options.Has("lines");
        var (rows, after) = byLine ? (ByLine(lines, shown, record, from, to), new List<string>()) : ByRun(lines, shown, record, from, to);
        if (record.Dropped) after.Add($"(the record started again at {History.Time(record.Earlier!.Value)}: more than {Authorship.MaxRuns} changes of writer)");
        var hint = byLine
            ? $"[too long for one call; blame a section: blame --lines \"{path.Value}#Section\" ('outline {SectionLookup.Quote(path)}' lists them)]"
            : $"[too many runs for one call; blame a section: blame \"{path.Value}#Section\" ('outline {SectionLookup.Quote(path)}' lists them)]";
        return ShellResult.Ok(Fit(rows, after, hint)) with { Version = new NoteVersion(path.Value, note.Version) };
    }

    /// <summary>The rows that fit the shell's output limit beside the lines that always come after them, and the hint in
    /// place of the rest, so that the counts and the record's restart are never what the limit cuts.</summary>
    private static string Fit(IReadOnlyList<string> rows, IReadOnlyList<string> after, string hint)
    {
        var budget = OutputLimit.MaxChars - after.Sum(l => l.Length + 1);
        var kept = new List<string>();
        var used = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            // The last row needs no room for the hint after it.
            var reserve = i + 1 < rows.Count ? hint.Length + 1 : 0;
            if (used + rows[i].Length + 1 + reserve > budget)
            {
                kept.Add(hint);
                break;
            }

            kept.Add(rows[i]);
            used += rows[i].Length + 1;
        }

        return Lines.Join(kept.Concat(after));
    }

    /// <summary>Each line's writer as the caller is shown it, and its time; null for a line not recorded.</summary>
    private async Task<(string Writer, DateTime At)?[]> ShownAsync(VaultPath path, Authorship record, CancellationToken ct)
    {
        var writers = new string[record.Writes.Count];
        for (var i = 0; i < writers.Length; i++)
        {
            writers[i] = (await _labels.RenderAsync(path.Segments[0], record.Writes[i].Label, record.Writes[i].Ref, ct)).Display;
        }

        return [.. record.Runs.SelectMany(r => Enumerable.Repeat<(string, DateTime)?>(r.Write < 0 ? null : (writers[r.Write], record.Writes[r.Write].At), r.Lines))];
    }

    private static string Who((string Writer, DateTime At)? line) => line?.Writer ?? EarlierThan;

    private static string When((string Writer, DateTime At)? line, Authorship record) => History.Time(line?.At ?? record.Earlier!.Value);

    /// <summary>The runs, and the counts below them.</summary>
    private static (List<string> Rows, List<string> After) ByRun(IReadOnlyList<string> lines, (string Writer, DateTime At)?[] shown, Authorship record, int from, int to)
    {
        var runs = new List<(string Range, (string Writer, DateTime At)? Line)>();
        for (var i = from; i < to;)
        {
            var start = i;
            var newest = shown[i];
            while (i < to && shown[i]?.Writer == shown[start]?.Writer)
            {
                if (shown[i] is { } line && line.At > newest!.Value.At) newest = line;
                i++;
            }

            runs.Add((i - start == 1 ? $"{start + 1}" : $"{start + 1}-{i}", newest));
        }

        if (runs.Count == 0) return (["(no lines)"], []);
        var rangeWidth = runs.Max(r => r.Range.Length);
        // Unrecorded runs read as a sentence, "earlier than <time>", and set no column.
        var writerWidth = runs.Where(r => r.Line is not null).Select(r => r.Line!.Value.Writer.Length).DefaultIfEmpty(0).Max();
        var rows = runs.Select(r => r.Line is { } line
            ? $"{r.Range.PadRight(rangeWidth)}  {line.Writer.PadRight(writerWidth)}  {History.Time(line.At)}"
            : $"{r.Range.PadRight(rangeWidth)}  {EarlierThan} {History.Time(record.Earlier!.Value)}").ToList();
        return (rows, [Counts(Count(lines, shown, from, to)) is { Length: > 0 } counts ? $"{counts} (blank lines not counted)" : "(blank lines only)"]);
    }

    private static List<string> ByLine(IReadOnlyList<string> lines, (string Writer, DateTime At)?[] shown, Authorship record, int from, int to)
    {
        if (from == to) return ["(no lines)"];
        var numberWidth = $"{to}".Length;
        var writerWidth = Enumerable.Range(from, to - from).Max(i => Who(shown[i]).Length);
        return [.. Enumerable.Range(from, to - from).Select(i => $"{$"{i + 1}".PadRight(numberWidth)}  {Who(shown[i]).PadRight(writerWidth)}  {When(shown[i], record)}  {lines[i]}")];
    }

    /// <summary>The non-blank lines per writer shown, in the order they first appear; the unrecorded ones (null) last.</summary>
    private static List<(string? Writer, int Lines)> Count(IReadOnlyList<string> lines, (string Writer, DateTime At)?[] shown, int from, int to)
    {
        var counts = new List<(string? Writer, int Lines)>();
        for (var i = from; i < to; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var writer = shown[i]?.Writer;
            var at = counts.FindIndex(c => c.Writer == writer);
            if (at < 0) counts.Add((writer, 1));
            else counts[at] = (writer, counts[at].Lines + 1);
        }

        return [.. counts.OrderBy(c => c.Writer is null)];
    }

    /// <summary>"Brita 18 lines, Veyalt 31, earlier 30": the word once, on the first count.</summary>
    private static string Counts(IEnumerable<(string? Writer, int Lines)> counts) =>
        string.Join(", ", counts.Select((c, i) => $"{c.Writer ?? "earlier"} {c.Lines}{(i > 0 ? "" : c.Lines == 1 ? " line" : " lines")}"));

    private async Task<ShellResult> FolderAsync(VaultPath dir, CancellationToken ct)
    {
        var paths = (await notes.ListNotesUnderAsync(dir, ct)).OrderBy(p => p.Value, TextOrder.Default).ToList();
        var rows = new List<(string Path, List<(string? Writer, int Lines)> Counts)>();
        var damaged = 0;
        foreach (var path in paths.Take(MaxNotes))
        {
            if (await notes.GetAsync(path, ct) is not { } note)
            {
                damaged++;
                continue;
            }

            var lines = Lines.Split(note.Content);
            rows.Add((path.Value, Count(lines, await ShownAsync(path, Authorship.Of(note), ct), 0, lines.Count)));
        }

        if (rows.Count == 0) return ShellResult.Ok($"(no notes below {dir.Value})");
        var width = rows.Max(r => r.Path.Length);
        var shown = rows.Select(r => $"{r.Path.PadRight(width)}  {(r.Counts.Count == 0 ? "(blank lines only)" : string.Join(", ", r.Counts.Select(c => $"{c.Writer ?? "earlier"} {c.Lines}")))}").ToList();

        // As many notes as fit the shell's output limit with the lines below them, which count only the notes shown.
        for (var kept = rows.Count; ; kept--)
        {
            var below = new List<string>();
            var more = paths.Count - damaged - kept;
            if (more > 0) below.Add($"… and {more} more {(more == 1 ? "note" : "notes")}; blame a folder below it");
            if (Total(rows.Take(kept).Select(r => r.Counts)) is { } total) below.Add(total);
            var output = Lines.Join(shown.Take(kept).Concat(below));
            if (output.Length <= OutputLimit.MaxChars || kept == 0) return ShellResult.Ok(output);
        }
    }

    /// <summary>"total: Brita 240 lines in 12 notes, Veyalt 410 in 9"; null when no note has a line that counts.</summary>
    private static string? Total(IEnumerable<List<(string? Writer, int Lines)>> notes)
    {
        var totals = new List<(string? Writer, int Lines, int Notes)>();
        foreach (var (writer, n) in notes.SelectMany(c => c))
        {
            var at = totals.FindIndex(t => t.Writer == writer);
            if (at < 0) totals.Add((writer, n, 1));
            else totals[at] = (writer, totals[at].Lines + n, totals[at].Notes + 1);
        }

        if (totals.Count == 0) return null;
        return "total: " + string.Join(", ", totals.OrderBy(t => t.Writer is null).Select((t, i) =>
            i == 0
                ? $"{t.Writer ?? "earlier"} {t.Lines} {(t.Lines == 1 ? "line" : "lines")} in {t.Notes} {(t.Notes == 1 ? "note" : "notes")}"
                : $"{t.Writer ?? "earlier"} {t.Lines} in {t.Notes}"));
    }
}
