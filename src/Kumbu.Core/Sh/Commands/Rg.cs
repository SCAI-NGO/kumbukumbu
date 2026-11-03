// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// Plain, unranked regex search in the style of ripgrep: over notes when it starts the pipeline,
/// over the piped lines when it stands right of a pipe. With <c>pixels</c> it also reads the stored text of PDFs,
/// page by page, after the notes.
/// </summary>
public sealed class Rg(INoteReader notes, IAttachmentPixels? pixels = null, WriterMarks? marks = null) : ICommand
{
    // The engine is linear in the input, so the timeout is a backstop, not the protection.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>PDFs a search asks regions for: each means sending a whole file to the sidecar.</summary>
    public const int MaxRegionPdfs = 8;

    /// <summary>Pages with matches of one PDF that get regions, as many as one text-boxes request takes.</summary>
    public const int MaxRegionPages = 50;

    public const int MaxRegionsPerLine = 3;

    private const string NoRegions = "No regions: PDF rendering is unavailable right now. The matches are all here.";

    /// <summary>The time a search spends on regions before it asks for no further PDFs; a call under way runs to its own limit.</summary>
    internal TimeSpan RegionBudget { get; init; } = TimeSpan.FromSeconds(20);

    private static readonly Option[] Spec =
    [
        new('i', "ignore-case"), new('F', "fixed-strings"), new('w', "word-regexp"), new('v', "invert-match"),
        new('l', "files-with-matches"), new('c', "count"), new('n', "line-number"), new('N', "no-line-number"),
        new('m', "max-count", TakesValue: true), new('e', "regexp", TakesValue: true),
        new('A', "after-context", TakesValue: true), new('B', "before-context", TakesValue: true),
        new('C', "context", TakesValue: true), new(null, "files"), new(null, "version", TakesValue: true), new(null, "tag", TakesValue: true),
        new(null, "regions"),
    ];

    public string Name => "rg";

    public string Usage => "rg [options] <regex> [path...]";

    public string Summary => "search notes and PDF text (prints path:line:text); --files lists note paths";

    public string Help => """
        rg [options] <regex> [path...]
          Search the notes below each path (a directory or a note) for lines matching a regex.
          Without a path, search every vault. Prints path:line:text, sorted by path; context lines
          print as path-line-text, and '--' separates groups that are not adjacent.
          Right of a pipe, rg filters the piped lines instead and prints them without a prefix.
          If nothing matches in the text but note names do, a hint line lists them (not when piped).
          PDFs below the paths are searched after the notes, in the text stored when they were uploaded,
          page by page: a match prints /Work/paper.pdf:p7: text (with -n the line within the page:
          /Work/paper.pdf:p7:12: text), context lines /Work/paper.pdf:p7- text, and context stays within
          a page. -l lists a PDF once, -c and -m count per PDF. Pages without a text layer (scans) and PDFs
          not analysed yet have no text to find, and the text of a scan may have wrong or missing words: if
          nothing matches, a hint line names the PDFs with such pages. Look at pages with 'pdf'. PDFs are
          searched in the current state only, not with --checkpoint; --files and --tag leave them out.
          With --regions a match in a PDF also says where on the page it lies.
          Images are not searched ('ls' lists attachments, 'backlinks' finds the notes that embed one; 'help attachments').
        rg --files [path...]
          List the paths of all notes below each path; with no path, of every note.

          -i, --ignore-case          case-insensitive
          -F, --fixed-strings        the pattern is a literal string, not a regex
          -w, --word-regexp          match whole words only
          -v, --invert-match         select lines that do not match
          -l, --files-with-matches   print only the paths of notes and PDFs with a match
          -c, --count                print path:count per note or PDF (piped: one total)
          -n, --line-number          show line numbers (default for notes; piped: off; PDFs: off)
          -N, --no-line-number       hide line numbers
          -m, --max-count N          at most N matching lines per note or PDF
          -e, --regexp PATTERN       a pattern, for one that starts with '-'; repeat for several
          --version V                search an earlier version (from 'history') of the one note given
          --tag T                    only notes that carry #T (nested too, ignoring case; 'help tags');
                                     repeat for notes with all of them
          --regions                  for matches in PDFs, also where they lie on the page, ready for pdf's --crop:
                                     /Work/paper.pdf:p7:crop=72,298,214,17: text. In points of the page as displayed,
                                     around the piece of text that holds the match. It shows where the text box lies,
                                     not that anyone can see it: look with 'pdf /Work/paper.pdf 7 --crop 72,298,214,17'.
                                     Regions come from the PDF itself each time, for up to 8 PDFs a search; not with
                                     -l, -c, -v, --files, --tag or --version, nor right of a pipe.
          -A N, -B N, -C N           N lines of context after, before, or around each match

        The syntax is that of ripgrep: no look-around and no backreferences, so a search
        never backtracks. Quote patterns that contain spaces or | ( ) < > & ; $.

          rg -i hecate
          rg -n -C 2 'TODO|FIXME' /Work/projects
          rg --files | rg -i kumbu          find a note by name (e.g. for a [[wikilink]])
          rg -l '\[\[kumbu\]\]'              which notes link to kumbu
          rg -n -i 'attention is' /Work/Papers   notes and PDFs below a folder
          rg --tag research -i capabilit /Work
          cat /Demo/welcome.md | rg -n '^#'
        """;

    public bool ReadsInput => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var tags = options.Values("tag");
        if (tags.Count > 0 && (input is not null || options.Has("version")))
        {
            return ShellResult.Error("rg: --tag chooses the notes rg reads; it works neither right of a pipe nor with --version");
        }

        if (options.Has("regions")
            && (input is not null || tags.Count > 0 || new[] { "files-with-matches", "count", "invert-match", "files", "version" }.Any(options.Has)))
        {
            return ShellResult.Error("rg: --regions says where matches lie in PDFs; it doesn't go with -l, -c, -v, --files, --tag or --version, nor right of a pipe");
        }

        if (options.Has("files")) return await ListFilesAsync(options.Operands, tags, input, ct);

        List<string> patterns = [.. options.Values("regexp")];
        var operands = options.Operands;
        if (patterns.Count == 0)
        {
            if (operands.Count == 0) return ShellResult.Error("rg: missing pattern (try 'help rg')");
            patterns.Add(operands[0]);
            operands = [.. operands.Skip(1)];
        }

        if (!TryCount(options, "max-count", null, out var max, out error)
            || !TryCount(options, "context", 0, out var context, out error)
            || !TryCount(options, "before-context", context, out var before, out error)
            || !TryCount(options, "after-context", context, out var after, out error))
        {
            return error;
        }

        if (!TryBuildRegex(patterns, options, out var regex, out error)) return error;
        var search = new Search(regex, options.Has("invert-match"), max, before!.Value, after!.Value);

        Note? versioned = null;
        if (options.Value("version") is { } version)
        {
            if (input is not null || operands.Count != 1)
            {
                return ShellResult.Error("rg: --version names a version of one note; give rg a single note");
            }

            if (!CommandArgs.TryParsePath(Name, operands[0], out var notePath, out var pathError)) return pathError!;
            if (notePath.IsRoot || (await notes.ListNotesUnderAsync(notePath, ct)).Count > 0)
            {
                return ShellResult.Error("rg: --version names a version of one note; give rg a single note");
            }

            var old = await CommandArgs.ReadVersionAsync(Name, notes, notePath, version, ct);
            if (old.IsError) return old;
            input = null;
            operands = [];
            versioned = new Note(notePath, old.Output);
        }

        try
        {
            if (versioned is not null) return SearchContents(search, options, [versioned]);
            return input is null
                ? await SearchNotesAsync(search, options, operands, tags, ct)
                : SearchInput(search, options, operands, input);
        }
        catch (RegexMatchTimeoutException)
        {
            return ShellResult.Error("rg: the search took too long; narrow the path or simplify the pattern");
        }
    }

    private async Task<ShellResult> SearchNotesAsync(Search search, Options options, IReadOnlyList<string> operands, IReadOnlyList<string> tags, CancellationToken ct)
    {
        var found = new Dictionary<VaultPath, Note>();
        var documents = new Dictionary<VaultPath, AttachmentState>();
        var scope = VaultPath.Root;
        foreach (var operand in operands.Count == 0 ? ["/"] : operands)
        {
            if (SectionAddress.Split(operand).Fragment is not null)
            {
                return ShellResult.Error($"rg: {operand}: rg searches whole notes; for one section use: cat \"{operand}\" | rg …");
            }

            if (!CommandArgs.TryParsePath(Name, operand, out var path, out var pathError)) return pathError!;
            if (operands.Count == 1) scope = path;

            var below = await notes.ReadNotesUnderAsync(path, ct);
            var pdfs = pixels is null ? [] : (await StoreAttachments.UnderAsync(notes, path, ct)).Where(a => AttachmentText.IsPdf(a.Current)).ToList();
            if (below.Count == 0 && pdfs.Count == 0 && !path.IsRoot)
            {
                var content = await notes.ReadAsync(path, ct);
                if (content is null && await StoreAttachments.GetAsync(notes, path, ct) is { } attachment)
                {
                    if (pixels is null || !AttachmentText.IsPdf(attachment.Current))
                    {
                        return ShellResult.Error($"rg: {path.Value} is an attachment; rg searches the text of notes only");
                    }

                    documents.TryAdd(path, attachment.Current);
                    continue;
                }

                if (content is null) return ShellResult.Error($"rg: {path.Value}: No such file or directory");
                below = [new Note(path, content)];
            }

            foreach (var note in below) found.TryAdd(note.Path, note);
            foreach (var pdf in pdfs) documents.TryAdd(pdf.Path, pdf.Current);
        }

        IReadOnlyList<Note> searched = [.. found.Values];
        if (tags.Count > 0)
        {
            var (carrying, tagError) = TagFilter.Narrow(searched, tags, scope);
            if (carrying is null) return ShellResult.Error($"rg: {tagError}");
            searched = carrying;
        }

        var matched = new List<VaultPath>();
        var lines = NoteLines(search, options, searched, matched);
        var pictured = new List<(VaultPath Path, int Pages)>();
        var notices = new List<string>();
        if (tags.Count == 0) await AddPdfLinesAsync(search, options, documents, lines, pictured, notices, ct);
        var result = ShellResult.Ok(Lines.Join(lines));
        if (marks is not null && !options.Has("invert-match") && await marks.NoticeAsync(matched, ct) is { } written) notices.Insert(0, written);
        if (notices.Count > 0) result = result with { Notice = string.Join('\n', notices) };
        return result.Output.Length > 0 || options.Has("invert-match") ? result : result with { NoMatchHint = NoMatchHint(NameHint(search, options, searched.Select(n => n.Path)), PictureHint(pictured)) };
    }

    private static string? NoMatchHint(string? names, string? pictures) =>
        names is not null && pictures is not null ? $"{names}\n{pictures}" : names ?? pictures;

    /// <summary>
    /// For a search that found nothing in the text: the PDFs in its scope with pages that are pictures, whose text is
    /// missing or may be wrong, so that the empty answer isn't taken for proof that the words aren't there.
    /// </summary>
    private static string? PictureHint(List<(VaultPath Path, int Pages)> pictured)
    {
        if (pictured.Count == 0) return null;

        var sorted = pictured.OrderBy(p => p.Path.Value, TextOrder.Default).ToList();
        var shown = string.Join(", ", sorted.Take(HintedPaths).Select(p => $"{p.Path.Value} ({p.Pages} page{(p.Pages == 1 ? "" : "s")})"));
        var more = sorted.Count > HintedPaths ? $" and {sorted.Count - HintedPaths} more" : "";
        return $"no match in the text, but pages that are scans or photos may hold it, as their text is missing or may be wrong: {shown}{more} (pdf info shows which; pdf <path> --images shows them)";
    }

    private const int HintedPaths = 5;

    /// <summary>
    /// For a search that found nothing in the text: the notes in its scope whose path matches the pattern, since a
    /// note's title is often only its file name. Null if none does.
    /// </summary>
    private static string? NameHint(Search search, Options options, IEnumerable<VaultPath> scope)
    {
        var names = scope.Select(p => p.Value).Where(search.IsMatch).Order(TextOrder.Default).ToList();
        if (names.Count == 0) return null;

        var shown = string.Join(", ", names.Take(HintedPaths));
        var more = names.Count > HintedPaths ? $" and {names.Count - HintedPaths} more" : "";
        var flags = string.Concat(options.Has("ignore-case") ? "-i " : "", options.Has("fixed-strings") ? "-F " : "", options.Has("word-regexp") ? "-w " : "");
        var explicitPatterns = options.Values("regexp");
        var patterns = explicitPatterns.Count > 0
            ? string.Join(' ', explicitPatterns.Select(p => $"-e {Quote(p)}"))
            : Quote(options.Operands[0]);
        var command = $"rg --files | rg {flags}{patterns}";
        return $"no match in the text; {names.Count} note name{(names.Count == 1 ? "" : "s")} match{(names.Count == 1 ? "es" : "")}: {shown}{more} ({command} lists them)";
    }

    /// <summary>The pattern as the shell reads it back: bare if it is plain, else quoted.</summary>
    private static string Quote(string pattern)
    {
        if (pattern.Length > 0 && pattern.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/')) return pattern;
        return pattern.Contains('\'') ? $"\"{pattern}\"" : $"'{pattern}'";
    }

    private static ShellResult SearchContents(Search search, Options options, IEnumerable<Note> found) =>
        ShellResult.Ok(Lines.Join(NoteLines(search, options, found)));

    private static List<string> NoteLines(Search search, Options options, IEnumerable<Note> found, List<VaultPath>? matched = null)
    {
        var lineNumbers = !options.Has("no-line-number");
        var output = new List<string>();
        foreach (var note in found.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var lines = Lines.Split(note.Content);
            var matches = search.Matches(lines);
            if (matches.Count == 0) continue;

            matched?.Add(note.Path);
            var path = note.Path.Value;
            if (options.Has("files-with-matches")) output.Add(path);
            else if (options.Has("count")) output.Add($"{path}:{matches.Count}");
            else
            {
                if (search.HasContext && output.Count > 0) output.Add("--");
                output.AddRange(search.Render(lines, matches, (n, sep, text) =>
                    lineNumbers ? $"{path}{sep}{n}{sep}{text}" : $"{path}{sep}{text}"));
            }
        }

        return output;
    }

    private const int PagesAtATime = 100;

    /// <summary>
    /// Appends the matches in the stored text of PDFs, one at a time and a hundred pages at a time, and stops reading
    /// once the output is past what the shell would show anyway. A PDF without a stored analysis, or whose analysis
    /// failed, has no text to find: it is skipped, never analysed here. With --regions a PDF's matching pages are held
    /// until its text is read, then printed with the regions of one text-boxes request; why some have none goes to
    /// <paramref name="notices"/>.
    /// </summary>
    private async Task AddPdfLinesAsync(Search search, Options options, Dictionary<VaultPath, AttachmentState> documents, List<string> output, List<(VaultPath Path, int Pages)> pictured, List<string> notices, CancellationToken ct)
    {
        var lineNumbers = options.Has("line-number") && !options.Has("no-line-number");
        var size = output.Sum(l => l.Length + 1);
        var counted = output.Count;
        var regions = options.Has("regions") ? new RegionSearch(notices) : null;
        foreach (var (path, state) in documents.OrderBy(d => d.Key.Value, TextOrder.Default))
        {
            if (size > OutputLimit.MaxChars) return;

            var name = path.Value;
            var total = 0;
            var held = new List<HeldPage>();
            try
            {
                var analysis = await pixels!.AnalysisAsync(path, state, runIfPending: false, ct);
                if (analysis is null) continue;

                var pictures = analysis.Pages.Count(p => p.Type == PageType.Image || (p.Type == PageType.Mixed && p.Chars == 0));
                if (pictures > 0) pictured.Add((path, pictures));

                for (var first = 1; first <= analysis.AnalysedPages && size <= OutputLimit.MaxChars && search.Remaining(total) != 0; first += PagesAtATime)
                {
                    var last = Math.Min(first + PagesAtATime - 1, analysis.AnalysedPages);
                    if (await pixels.PageTextAsync(path, state, first, last, ct) is not { } texts) break;

                    for (var i = 0; i < texts.Count; i++)
                    {
                        if (texts[i].Length == 0) continue;
                        var page = first + i;
                        var lines = Lines.Split(texts[i]);
                        var matches = search.Matches(lines, search.Remaining(total));
                        if (matches.Count == 0) continue;

                        total += matches.Count;
                        if (options.Has("files-with-matches"))
                        {
                            output.Add(name);
                            break;
                        }

                        if (regions is not null)
                        {
                            // Counted as they would print without regions, so that reading stops where it would have.
                            if (search.HasContext && output.Count + held.Count > 0) size += 3;
                            size += search.Render(lines, matches, (n, sep, text) => PdfLine(name, page, lineNumbers, n, sep, text, null)).Sum(l => l.Length + 1);
                            held.Add(new HeldPage(page, texts[i], lines, matches));
                        }
                        else if (!options.Has("count"))
                        {
                            if (search.HasContext && output.Count > 0) output.Add("--");
                            output.AddRange(search.Render(lines, matches, (n, sep, text) =>
                                lineNumbers ? $"{name}:p{page}{sep}{n}{sep} {text}" : $"{name}:p{page}{sep} {text}"));
                        }

                        size += Grown();
                        if (search.Remaining(total) == 0 || size > OutputLimit.MaxChars) break;
                    }

                    if (options.Has("files-with-matches") && total > 0) break;
                }
            }
            catch (Exception e) when (e is not (OperationCanceledException or RegexMatchTimeoutException))
            {
                // Whatever went wrong with this PDF's text, the notes, the other PDFs and its matches so far still count;
                // a search that took too long is rg's error, not this PDF's.
            }

            if (held.Count > 0)
            {
                var boxes = await RegionsAsync(path, state, held, regions!, ct);
                foreach (var (page, _, lines, matches) in held)
                {
                    if (search.HasContext && output.Count > 0) output.Add("--");
                    var answer = boxes.GetValueOrDefault(page);
                    var offsets = new int[lines.Count];
                    for (var i = 1; i < lines.Count; i++) offsets[i] = offsets[i - 1] + lines[i - 1].Length + 1;
                    output.AddRange(search.Render(lines, matches, (n, sep, text) =>
                        PdfLine(name, page, lineNumbers, n, sep, text, sep == ':' && answer is not null ? Region(search, answer, offsets[n - 1], text) : null)));
                }

                // Their size was added as they were held.
                counted = output.Count;
            }

            if (options.Has("count") && total > 0) output.Add($"{name}:{total}");
            size += Grown();
        }

        // The size of the lines added since the last look.
        int Grown()
        {
            var added = output.Skip(counted).Sum(l => l.Length + 1);
            counted = output.Count;
            return added;
        }
    }

    /// <summary>A PDF's line as rg prints it; a region goes before the text, so that no text can pass for one.</summary>
    private static string PdfLine(string name, int page, bool lineNumbers, int n, char sep, string text, string? region)
    {
        var at = lineNumbers ? $"{name}:p{page}{sep}{n}" : $"{name}:p{page}";
        return region is null ? $"{at}{sep} {text}" : $"{at}:crop={region}: {text}";
    }

    /// <summary>A page of a PDF with matches, held until the PDF's text is read.</summary>
    private sealed record HeldPage(int Page, string Text, IReadOnlyList<string> Lines, List<int> Matches);

    /// <summary>What a search with --regions has spent on regions so far, and the notices it gives.</summary>
    private sealed class RegionSearch(List<string> notices)
    {
        public int Pdfs { get; set; }

        public bool Failed { get; set; }

        public Stopwatch? Clock { get; set; }

        public void Note(string notice)
        {
            if (!notices.Contains(notice)) notices.Add(notice);
        }
    }

    /// <summary>
    /// The text boxes of the held pages of one PDF, by page, from one request; a page whose text differs from the stored
    /// text has none. Every failure gives none and asks for no further PDFs: the matches print without regions.
    /// </summary>
    private async Task<Dictionary<int, PageTextBoxes>> RegionsAsync(VaultPath path, AttachmentState state, List<HeldPage> held, RegionSearch regions, CancellationToken ct)
    {
        var found = new Dictionary<int, PageTextBoxes>();
        var outcome = RegionOutcome.Delivered;
        if (++regions.Pdfs > MaxRegionPdfs)
        {
            regions.Note($"Regions only for the first {MaxRegionPdfs} PDFs with matches; name a PDF to get its own.");
            outcome = RegionOutcome.Limited;
        }
        else if (regions.Failed || regions.Clock?.Elapsed >= RegionBudget)
        {
            regions.Note(NoRegions);
            outcome = RegionOutcome.Unavailable;
        }
        else
        {
            var asked = held.Take(MaxRegionPages).ToList();
            regions.Clock ??= Stopwatch.StartNew();
            try
            {
                var answer = new Dictionary<int, PageTextBoxes>();
                foreach (var page in await pixels!.TextBoxesAsync(path, state, [.. asked.Select(h => h.Page)], ct)) answer.TryAdd(page.Number, page);
                foreach (var page in asked)
                {
                    if (!answer.TryGetValue(page.Page, out var boxes)) continue;
                    if (string.Equals(boxes.Text, page.Text, StringComparison.Ordinal)) found[page.Page] = boxes;
                    else regions.Note($"No regions on some pages of {path.Value}: its stored text comes from an older analysis. pdf info {SectionLookup.Quote(path)} renews it.");
                }

                if (held.Count > MaxRegionPages)
                {
                    regions.Note($"Regions for {path.Value} only on its first {MaxRegionPages} pages with matches; use -m, or look at one page: pdf {SectionLookup.Quote(path)} {held[MaxRegionPages].Page}.");
                    outcome = RegionOutcome.Limited;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Away, busy, too slow, or an answer that doesn't hold up: the search still has all its matches.
                regions.Failed = true;
                regions.Note(NoRegions);
                found.Clear();
                outcome = RegionOutcome.Unavailable;
            }
        }

        pixels!.Count(new RegionsAsked(outcome));
        return found;
    }

    /// <summary>
    /// The regions of the matches in one line that starts at <paramref name="offset"/> of the page's text: for each match
    /// the box around the items it touches, grown by max(2, 0.3 × its short side), as a text box leaves out descenders,
    /// and whole by <see cref="Crop.Enclosing"/>. Equal ones once, at most three and how many more; null for none.
    /// </summary>
    private static string? Region(Search search, PageTextBoxes page, int offset, string line)
    {
        var crops = new List<Crop>();
        foreach (var (start, length) in search.Spans(line))
        {
            var (from, to) = (offset + start, offset + start + length);
            var (left, top, right, bottom) = (double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
            for (var i = FirstEndingAfter(page.Items, from); i < page.Items.Count; i++)
            {
                var item = page.Items[i];
                if (item.At >= to) break;
                (left, top) = (Math.Min(left, item.X), Math.Min(top, item.Y));
                (right, bottom) = (Math.Max(right, item.X + item.Width), Math.Max(bottom, item.Y + item.Height));
            }

            if (right < left) continue;
            var (width, height) = (right - left, bottom - top);
            var grow = Math.Max(2, 0.3 * Math.Min(width, height));
            if (Crop.Enclosing(left - grow, top - grow, width + 2 * grow, height + 2 * grow, page.Width, page.Height) is { } crop && !crops.Contains(crop)) crops.Add(crop);
        }

        if (crops.Count == 0) return null;
        var shown = string.Join(';', crops.Take(MaxRegionsPerLine));
        return crops.Count > MaxRegionsPerLine ? $"{shown};+{crops.Count - MaxRegionsPerLine} more" : shown;
    }

    /// <summary>The index of the first item that ends after <paramref name="from"/>; the items ascend, so their ends do too.</summary>
    private static int FirstEndingAfter(IReadOnlyList<TextBox> items, int from)
    {
        int low = 0, high = items.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (items[mid].End <= from) low = mid + 1;
            else high = mid;
        }

        return low;
    }

    private static ShellResult SearchInput(Search search, Options options, IReadOnlyList<string> operands, string input)
    {
        if (operands.Count > 0)
        {
            return ShellResult.Error($"rg: reads from the pipe, so it takes no path (got '{operands[0]}')");
        }

        if (options.Has("files-with-matches")) return ShellResult.Error("rg: -l lists notes, so it needs paths, not piped input");

        var lines = Lines.Split(input);
        var matches = search.Matches(lines);
        if (options.Has("count")) return ShellResult.Ok(matches.Count.ToString());

        var lineNumbers = options.Has("line-number") && !options.Has("no-line-number");
        return ShellResult.Ok(Lines.Join(search.Render(lines, matches, (n, sep, text) =>
            lineNumbers ? $"{n}{sep}{text}" : text)));
    }

    private async Task<ShellResult> ListFilesAsync(IReadOnlyList<string> operands, IReadOnlyList<string> tags, string? input, CancellationToken ct)
    {
        if (input is not null) return ShellResult.Error("rg: --files lists notes; it does not read from a pipe");
        if (tags.Count > 0)
        {
            if (operands.Count > 1) return ShellResult.Error("rg: --files with --tag takes one path");
            var scope = VaultPath.Root;
            if (operands.Count == 1 && !CommandArgs.TryParsePath(Name, operands[0], out scope, out var scopeError)) return scopeError!;
            var (all, readError) = await TagsCommand.ReadScopeAsync(notes, Name, scope, ct);
            if (all is null) return readError!;
            var (carrying, tagError) = TagFilter.Narrow(all, tags, scope);
            return carrying is null
                ? ShellResult.Error($"rg: {tagError}")
                : ShellResult.Ok(Lines.Join(carrying.Select(n => n.Path.Value).Order(TextOrder.Default)));
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operand in operands.Count == 0 ? ["/"] : operands)
        {
            if (!CommandArgs.TryParsePath(Name, operand, out var path, out var pathError)) return pathError!;

            var below = await notes.ListNotesUnderAsync(path, ct);
            if (below.Count == 0 && !path.IsRoot)
            {
                if (await notes.ReadAsync(path, ct) is null) return ShellResult.Error($"rg: {path.Value}: No such file or directory");
                below = [path];
            }

            found.UnionWith(below.Select(p => p.Value));
        }

        return ShellResult.Ok(Lines.Join(found.Order(TextOrder.Default)));
    }

    private static bool TryCount(
        Options options, string name, int? fallback, out int? value, [NotNullWhen(false)] out ShellResult? error)
    {
        value = fallback;
        error = null;
        if (options.Value(name) is not { } text) return true;
        if (int.TryParse(text, out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }

        error = ShellResult.Error($"rg: invalid number for --{name}: '{text}'");
        return false;
    }

    /// <summary>
    /// The regex rg searches with (-F, -w, -i), never backtracking. Throws <see cref="NotSupportedException"/> for
    /// syntax that needs backtracking and <see cref="ArgumentException"/> for one that doesn't parse; a literal
    /// (<paramref name="fixedStrings"/>) never does.
    /// </summary>
    public static Regex Pattern(IReadOnlyList<string> patterns, bool fixedStrings, bool wordRegexp, bool ignoreCase)
    {
        var parts = patterns.Select(p => fixedStrings ? Regex.Escape(p) : p).ToList();
        var pattern = parts.Count == 1 ? parts[0] : string.Join('|', parts.Select(p => $"(?:{p})"));
        if (wordRegexp) pattern = $@"\b(?:{pattern})\b";

        var regexOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (ignoreCase) regexOptions |= RegexOptions.IgnoreCase;
        return new Regex(pattern, regexOptions, MatchTimeout);
    }

    private static bool TryBuildRegex(
        IReadOnlyList<string> patterns, Options options,
        [NotNullWhen(true)] out Regex? regex, [NotNullWhen(false)] out ShellResult? error)
    {
        regex = null;
        error = null;
        try
        {
            regex = Pattern(patterns, options.Has("fixed-strings"), options.Has("word-regexp"), options.Has("ignore-case"));
            return true;
        }
        catch (NotSupportedException)
        {
            error = ShellResult.Error(
                "rg: regex not supported: look-around, backreferences and atomic groups need backtracking, which rg never does");
        }
        catch (ArgumentException e)
        {
            error = ShellResult.Error($"rg: regex parse error: {e.Message}");
        }

        return false;
    }

    /// <summary>Selects matching lines with their context and renders them in ripgrep's layout.</summary>
    private sealed class Search(Regex regex, bool invert, int? max, int before, int after)
    {
        public bool IsMatch(string text) => regex.IsMatch(text);

        public bool HasContext => before > 0 || after > 0;

        /// <summary>Where the regex matches in a line, leaving out empty matches.</summary>
        public List<(int Start, int Length)> Spans(string line)
        {
            var spans = new List<(int Start, int Length)>();
            foreach (var match in regex.EnumerateMatches(line))
            {
                if (match.Length > 0) spans.Add((match.Index, match.Length));
            }

            return spans;
        }

        /// <summary>Indexes of the selected lines, at most <c>max</c> of them.</summary>
        public List<int> Matches(IReadOnlyList<string> lines) => Matches(lines, max);

        /// <summary>What -m still allows after <paramref name="found"/> matching lines; null without -m.</summary>
        public int? Remaining(int found) => max is null ? null : Math.Max(0, max.Value - found);

        public List<int> Matches(IReadOnlyList<string> lines, int? limit)
        {
            var result = new List<int>();
            for (var i = 0; i < lines.Count && (limit is null || result.Count < limit); i++)
            {
                if (regex.IsMatch(lines[i]) != invert) result.Add(i);
            }

            return result;
        }

        /// <param name="format">Line number (1-based), ':' for a match or '-' for context, and the text.</param>
        public IEnumerable<string> Render(IReadOnlyList<string> lines, List<int> matches, Func<int, char, string, string> format)
        {
            var isMatch = matches.ToHashSet();
            var next = 0;
            foreach (var m in matches)
            {
                var first = Math.Max(m - before, next);
                if (HasContext && next > 0 && first > next) yield return "--";

                var last = Math.Min(m + after, lines.Count - 1);
                for (var i = first; i <= last; i++)
                {
                    yield return format(i + 1, isMatch.Contains(i) ? ':' : '-', lines[i]);
                }

                next = Math.Max(next, last + 1);
            }
        }
    }
}
