// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
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
        blame [--lines] <path>
          Who wrote what: for each run of lines of a note, the writer who last changed them and
          when (the newest write in the run, UTC). A line counts as changed when any character in it
          changed, indentation and reflow included, as in git blame. Recorded at every write since
          Kumbu² 0.6.0; lines older than that come from the history, which goes back {NoteWriter.HistoryLimit}
          versions, and read "earlier than <time>" where it ends. A section (note.md#Heading)
          limits it to that section. --lines prints every line with its writer, time and text.
          A copy (cp, or Duplicate in the web app) is a new note: every line is its copier's.
        blame <folder>
          For each note below the folder: lines per writer (blank lines not counted), then the
          total per writer and the number of notes.

          blame /Work/protocol.md
          blame /Work/protocol.md#Results --lines
          blame /Work/Protocols/
        """;

    public bool ReadsInput => false;

    private const string EarlierThan = "earlier than";

    private const int MaxNotes = 200;

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
        var output = options.Has("lines") ? ByLine(path, lines, shown, record, from, to) : ByRun(lines, shown, record, from, to);
        if (record.Dropped) output += $"\n(the record started again at {History.Time(record.Earlier!.Value)}: more than {Authorship.MaxRuns} changes of writer)";
        return ShellResult.Ok(output) with { Version = new NoteVersion(path.Value, note.Version) };
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

    private static string ByRun(IReadOnlyList<string> lines, (string Writer, DateTime At)?[] shown, Authorship record, int from, int to)
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

        if (runs.Count == 0) return "(no lines)";
        var rangeWidth = runs.Max(r => r.Range.Length);
        var writerWidth = runs.Max(r => Who(r.Line).Length);
        var output = runs.Select(r => $"{r.Range.PadRight(rangeWidth)}  {Who(r.Line).PadRight(writerWidth)}  {When(r.Line, record)}").ToList();
        output.Add(Counts(Count(lines, shown, from, to)) is { Length: > 0 } counts ? $"{counts} (blank lines not counted)" : "(blank lines only)");
        return Lines.Join(output);
    }

    private static string ByLine(VaultPath path, IReadOnlyList<string> lines, (string Writer, DateTime At)?[] shown, Authorship record, int from, int to)
    {
        if (from == to) return "(no lines)";
        var numberWidth = $"{to}".Length;
        var writerWidth = Enumerable.Range(from, to - from).Max(i => Who(shown[i]).Length);
        var hint = $"\n[too long for one call; blame a section: blame --lines \"{path.Value}#Section\" ('outline {SectionLookup.Quote(path)}' lists them)]";
        var output = new StringBuilder();
        for (var i = from; i < to; i++)
        {
            var line = $"{$"{i + 1}".PadRight(numberWidth)}  {Who(shown[i]).PadRight(writerWidth)}  {When(shown[i], record)}  {lines[i]}";
            if (output.Length + line.Length + 1 + hint.Length > OutputLimit.MaxChars) return output + hint;
            if (output.Length > 0) output.Append('\n');
            output.Append(line);
        }

        return output.ToString();
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
        var rows = new List<(string Path, string Counts)>();
        var totals = new List<(string? Writer, int Lines, int Notes)>();
        foreach (var path in paths.Take(MaxNotes))
        {
            if (await notes.GetAsync(path, ct) is not { } note) continue;
            var lines = Lines.Split(note.Content);
            var record = Authorship.Of(note);
            var counts = Count(lines, await ShownAsync(path, record, ct), 0, lines.Count);
            rows.Add((path.Value, counts.Count == 0 ? "(blank lines only)" : string.Join(", ", counts.Select(c => $"{c.Writer ?? "earlier"} {c.Lines}"))));
            foreach (var (writer, n) in counts)
            {
                var at = totals.FindIndex(t => t.Writer == writer);
                if (at < 0) totals.Add((writer, n, 1));
                else totals[at] = (writer, totals[at].Lines + n, totals[at].Notes + 1);
            }
        }

        if (rows.Count == 0) return ShellResult.Ok($"(no notes below {dir.Value})");
        var width = rows.Max(r => r.Path.Length);
        var output = rows.Select(r => $"{r.Path.PadRight(width)}  {r.Counts}").ToList();
        var more = paths.Count - MaxNotes;
        if (more > 0) output.Add($"… and {more} more {(more == 1 ? "note" : "notes")}; blame a folder below it");
        if (totals.Count > 0)
        {
            output.Add("total: " + string.Join(", ", totals.OrderBy(t => t.Writer is null).Select((t, i) =>
                i == 0
                    ? $"{t.Writer ?? "earlier"} {t.Lines} {(t.Lines == 1 ? "line" : "lines")} in {t.Notes} {(t.Notes == 1 ? "note" : "notes")}"
                    : $"{t.Writer ?? "earlier"} {t.Lines} in {t.Notes}")));
        }

        return ShellResult.Ok(Lines.Join(output));
    }
}
