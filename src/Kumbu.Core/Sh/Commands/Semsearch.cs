// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sections;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// Ranked search over sections (concept §6): embeddings for meaning, BM25 for exact words and names. The hybrid
/// ranks by cosine similarity plus a small bonus for the query's words; <c>--words</c> ranks by BM25 alone.
/// <c>rg</c> stays the plain, unranked regex search. With <c>pixels</c>, pages of PDFs rank among the sections by
/// meaning. A hit shows its whole core (spec 2026-11-09-structural-chunking §3.7), with <c>-C</c> the rolls around it,
/// with <c>-l</c> one line; cores that don't fit the output limit fall back to that line.
/// </summary>
public sealed class Semsearch(INoteReader notes, ISearchIndex index, IAttachmentPixels? pixels = null, WriterMarks? marks = null) : ICommand
{
    private const int DefaultMax = 10;

    /// <summary>Room kept for the footer and its notes, as <c>pdf</c> keeps it.</summary>
    private const int Reserve = 1_000;

    private static readonly Option[] Spec =
    [
        new('n', "max", TakesValue: true), new(null, "words"), new(null, "meaning"), new(null, "tag", TakesValue: true), new(null, "archive"),
        new('C', "context"), new('l', "list"),
    ];

    public string Name => "semsearch";

    public string Usage => "semsearch [-n N] [-C|-l] [--words|--meaning] [--tag T] [--archive] <query> [path]";

    public string Summary => "ranked search by words and meaning; finds sections and PDF pages";

    public string Help => """
        semsearch [-n N] [-C|-l] [--words|--meaning] [--tag T] [--archive] <query> [path]
          Ranked search for the sections that best match a question or a topic, in every vault or below
          a path. Combines two rankings: by meaning (embeddings: the same idea in other words, also across
          languages) and by words (BM25: exact terms, names, abbreviations). The meaning score gets a
          bonus for the words of the query a section contains.
          Prints the best sections (-n, default 10): path#Section with their lines and score, then the
          section's whole text. A short section is often merged with its neighbours under their parent
          heading: path#Section then names that parent, and the lines are exact. Scores are comparable
          within one search, not across searches. cat "path#Section" reads the whole section. For exact
          text or a regex, use rg. Pages of PDFs rank among the sections by meaning, not by words and
          not with --tag: a hit prints as path p7 with its score and the part of the page that matches
          best, and pdf path 7 reads the page; a PDF as the path ranks its own pages.
          Pages without a text layer (scans) and images are not searched.
          The query is the words before the path; quoting it is optional.
          -C         also print the text around each hit, its lines marked with ┊; context, not part of the match
          -l         one line per hit: path, lines and score
          --words    rank by words only        --meaning  rank by meaning only
          --tag T    only notes that carry #T (nested too, ignoring case; 'help tags'); repeat
                     for notes with all of them. The ranking is the same, over fewer notes.
          --archive  search a vault's archive (_archive/) too: its notes and PDF pages; a path inside it always is

          semsearch how do agents read notes
          semsearch --tag research modelling capabilities
          semsearch -n 5 'Regeln am Eingang' /Demo
          semsearch attention heads /Work/Papers/smith-2025.pdf
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (NumberAfterC(args))
        {
            return ShellResult.Error($"semsearch: -C takes no number; to search for one, put it after --: semsearch -C -- 2025 budget ({Usage})");
        }

        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var max = DefaultMax;
        if (options.Value("max") is { } maxText && (!int.TryParse(maxText, out max) || max < 1))
        {
            return ShellResult.Error($"semsearch: invalid number of results: '{maxText}'");
        }

        if (options.Has("context") && options.Has("list")) return ShellResult.Error("semsearch: -C and -l exclude each other");
        if (options.Has("words") && options.Has("meaning")) return ShellResult.Error("semsearch: --words and --meaning exclude each other; leave both out to combine them");

        var operands = options.Operands.ToList();
        var scope = VaultPath.Root;
        if (operands.Count > 1 && operands[^1].StartsWith('/'))
        {
            if (!CommandArgs.TryParsePath(Name, operands[^1], out scope, out var pathError)) return pathError!;
            operands.RemoveAt(operands.Count - 1);
        }

        var query = string.Join(' ', operands).Trim();
        if (query.Length == 0) return ShellResult.Error("semsearch: missing query (semsearch <query> [path])");
        if (operands.Count == 1 && query.StartsWith('/') && VaultPath.TryParse(query, out _))
        {
            return ShellResult.Error($"semsearch: missing query; {query} is the path (semsearch <query> {query})");
        }

        // A PDF carries no tags, so a search by tag leaves them out.
        var pages = pixels is not null && !options.Has("tag");
        IReadOnlyList<Note> found;
        IReadOnlyList<Note> archived = [];
        if (!scope.IsRoot && await notes.ReadAsync(scope, ct) is { } single) found = [new Note(scope, single)];
        else if (pages && AttachmentFormat.IsPdf(scope) && await StoreAttachments.GetAsync(notes, scope, ct) is { } pdf
            && AttachmentText.IsPdf(pdf.Current))
        {
            if (options.Has("words")) return ShellResult.Error($"semsearch: {scope.Value} is a PDF; semsearch --words searches notes only, by meaning it finds its pages");
            found = [];
        }
        else
        {
            found = await notes.ReadNotesUnderAsync(scope, ct);
            var isVault = scope.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(scope.Segments[0]);
            // A folder of PDFs only has pages to find.
            if (found.Count == 0 && !scope.IsRoot && !isVault && !(pages && (await StoreAttachments.UnderAsync(notes, scope, ct)).Any(a => AttachmentFormat.IsPdf(a.Current))))
            {
                return await CommandArgs.ReadNoteAsync(Name, notes, scope, ct);
            }

            // After the check above, so that a vault holding nothing but its archive finds nothing rather than nothing there.
            if (!ArchiveFolder.Shown(scope, options.Has("archive")))
            {
                archived = [.. found.Where(n => ArchiveFolder.Contains(n.Path))];
                found = [.. found.Where(n => !ArchiveFolder.Contains(n.Path))];
            }
        }

        if (options.Values("tag") is { Count: > 0 } tags)
        {
            var (carrying, tagError) = TagFilter.Narrow(found, tags, scope, (archived, $"{Archived(options, operands, scope)} searches them"));
            if (carrying is null) return ShellResult.Error($"semsearch: {tagError}");
            found = carrying;
            archived = [.. archived.Where(n => tags.All(t => TagFilter.Parse(t) is { } tag && TagFilter.Carries(n, tag)))];
        }

        // The same bound as the ranking's, so that the cores shown are the chunks ranked.
        var bound = index.Bound;
        var byPath = found.ToDictionary(n => n.Path);
        var mode = options.Has("words") ? SearchMode.Words : options.Has("meaning") ? SearchMode.Meaning : SearchMode.Hybrid;
        var (listed, semantic, notIndexed) = await SectionSearch.RankAsync(found, scope, query, mode, max, index, ct, pages, options.Has("archive")) switch
        {
            { MeaningUnavailable: true } r => (null, r.Semantic, false),
            var r => (r.Hits, r.Semantic, r.FellBackToWords),
        };
        if (listed is null) return ShellResult.Error($"semsearch: {semantic!.Reason ?? "search by meaning isn't available"}; without --meaning, it ranks by words");

        var lines = new List<string>();
        if (listed.Count == 0)
        {
            // Nothing found would pass for nothing there while archived notes hold the very words; those are at hand.
            var below = scope.IsRoot ? "" : " below " + scope.Value;
            lines.Add(SectionSearch.Exact(archived, query, 1).Count > 0
                ? $"(nothing found for '{query}'{below} outside the archive; {Archived(options, operands, scope)} searches it)"
                : $"(nothing found for '{query}'{below})");
        }

        var (shown, notice) = await HitsAsync(listed, lines.Sum(l => l.Length + 1), options, byPath, bound, ct);
        lines.AddRange(shown);
        lines.Add(Footer(semantic, options.Has("words"), options.Has("meaning"), notIndexed, listed.Count > 0, listed.Any(h => h.Page is not null)));
        return ShellResult.Ok(Lines.Join(lines)) with { Notice = notice };
    }

    /// <summary>
    /// Whether a number follows -C (<c>-C 3</c>, <c>-C3</c>) before any "--": with rg's -C in mind, <c>semsearch -C 3
    /// query</c> would otherwise search for "3 query".
    /// </summary>
    private static bool NumberAfterC(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count && args[i] != "--"; i++)
        {
            string? number = null;
            if (args[i] == "-C") number = i + 1 < args.Count ? args[i + 1] : null;
            else if (args[i].StartsWith("-C", StringComparison.Ordinal)) number = args[i][2..];
            if (number is { Length: > 0 } && number.All(char.IsAsciiDigit)) return true;
        }

        return false;
    }

    /// <summary>
    /// The hits as printed within the output limit, after <paramref name="taken"/> characters, and the notice the shell
    /// puts above them for the notes another user wrote last. Every hit's line comes first, as many as fit with the
    /// notice for their notes; then whole texts in order while they fit in the room left; from the first that doesn't,
    /// the hits go without theirs.
    /// </summary>
    private async Task<(List<string> Lines, string? Notice)> HitsAsync(
        IReadOnlyList<SectionHit> listed, int taken, Options options, Dictionary<VaultPath, Note> byPath, ChunkBound bound, CancellationToken ct)
    {
        var headers = listed.Select(Header).ToList();
        var room = OutputLimit.MaxChars - Reserve - taken;
        var count = Fitting(room);
        string? notice;
        while (true)
        {
            notice = marks is null ? null : await marks.NoticeAsync(listed.Take(count).Where(h => h.Page is null).Select(h => h.Chunk.Path), ct);
            // The notice names only the notes of the lines printed, so fewer lines never need a longer one.
            var fitting = Fitting(room - (notice?.Length + 1 ?? 0));
            if (fitting >= count) break;
            count = fitting;
        }

        room -= (notice?.Length + 1 ?? 0) + headers.Take(count).Sum(h => h.Length + 1);
        var (withRolls, textless) = (options.Has("context"), options.Has("list"));
        var lines = new List<string>();
        int? bare = null;
        for (var i = 0; i < count; i++)
        {
            lines.Add(headers[i]);
            if (textless || bare is not null) continue;
            var (hit, c) = (listed[i], listed[i].Chunk);
            var text = hit.Page is { } page
                ? await PageTextAsync(c.Path, page, hit.PagePart ?? 0, bound, withRolls, ct)
                : NoteText(byPath.GetValueOrDefault(c.Path), c, withRolls);
            var size = text.Sum(l => l.Length + 1);
            if (size > room)
            {
                bare = i;
                continue;
            }

            lines.AddRange(text);
            room -= size;
        }

        const string Read = "to fit the output; cat \"path#Section\" or pdf path N reads one";
        if (bare == count - 1) lines.Add($"(hit {count} without its text, {Read})");
        else if (bare is { } k) lines.Add($"(hits {k + 1}-{count} without their text, {Read})");
        if (count + 1 == listed.Count) lines.Add($"(hit {listed.Count} left out, to fit the output)");
        else if (count < listed.Count) lines.Add($"(hits {count + 1}-{listed.Count} left out, to fit the output)");

        return (lines, notice);

        // How many of the first lines fit in `space`.
        int Fitting(int space)
        {
            var n = 0;
            while (n < headers.Count && headers[n].Length + 1 <= space) space -= headers[n++].Length + 1;
            return n;
        }
    }

    private static string Header(SectionHit hit, int i)
    {
        var score = hit.Score is { } s ? string.Create(CultureInfo.InvariantCulture, $"  score {s:0.00}") : "";
        var c = hit.Chunk;
        return hit.Page is { } page
            ? $"{i + 1}. {c.Path.Value} p{page}{score}"
            : $"{i + 1}. {c.Path.Value}{c.Address}  lines {c.StartLine + 1}-{c.EndLine}{score}";
    }

    /// <summary>A note's chunk as printed: its core indented, with <paramref name="withRolls"/> its rolls around it.</summary>
    private static List<string> NoteText(Note? note, Chunk chunk, bool withRolls)
    {
        var (before, after) = withRolls && note is not null ? Chunker.Rolls(note, chunk) : ("", "");
        return Shown(before, chunk.Text, after);
    }

    /// <summary>
    /// The page's chunk of <paramref name="part"/> as printed, split from the page's text as indexing split it (the
    /// first chunk for a part the page no longer has).
    /// </summary>
    private async Task<List<string>> PageTextAsync(VaultPath path, int page, int part, ChunkBound bound, bool withRolls, CancellationToken ct)
    {
        List<string> unavailable = ["   (the text of this page can't be read right now)"];
        if (await StoreAttachments.GetAsync(notes, path, ct) is not { } attachment) return unavailable;
        IReadOnlyList<string>? text;
        try
        {
            text = await pixels!.PageTextAsync(path, attachment.Current, page, page, ct);
        }
        catch (PixelsUnavailableException)
        {
            return unavailable;
        }

        if (text is not [var only]) return unavailable;
        // The title indexing gives it, so that the chunks are the same.
        var chunks = Chunker.SplitPage(Chunker.PdfTitle(path), page, only, bound);
        if (chunks.Count == 0) return unavailable;
        var chunk = part < chunks.Count ? chunks[part] : chunks[0];
        return withRolls ? Shown(chunk.PreRoll, chunk.Core, chunk.PostRoll) : Shown("", chunk.Core, "");
    }

    /// <summary>The core's lines indented, the rolls' marked with ┊ so they can't pass for the core.</summary>
    private static List<string> Shown(string before, string core, string after)
    {
        var lines = new List<string>();
        lines.AddRange(Lines.Split(before).Select(Roll));
        lines.AddRange(Lines.Split(core).Select(l => string.IsNullOrWhiteSpace(l) ? "" : "   " + l));
        lines.AddRange(Lines.Split(after).Select(Roll));
        return lines;

        static string Roll(string line) => string.IsNullOrWhiteSpace(line) ? "   ┊" : "   ┊ " + line;
    }

    /// <summary>This search as typed again with --archive.</summary>
    private static string Archived(Options options, IReadOnlyList<string> query, VaultPath scope)
    {
        var max = options.Value("max") is { } n ? $" -n {n}" : "";
        var shown = options.Has("context") ? " -C" : options.Has("list") ? " -l" : "";
        var mode = options.Has("words") ? " --words" : options.Has("meaning") ? " --meaning" : "";
        var tags = string.Concat(options.Values("tag").Select(t => $" --tag {Rg.Quote(t)}"));
        var words = string.Join(' ', query.Select(Rg.Quote));
        return $"semsearch --archive{max}{shown}{mode}{tags} {words}{(scope.IsRoot ? "" : " " + Rg.Quote(scope.Value))}";
    }

    private static string Footer(SemanticResult? semantic, bool wordsOnly, bool meaningOnly, bool notIndexed, bool any, bool anyPage = false)
    {
        var read = !any ? "" : anyPage ? "; cat \"path#Section\" reads a section, pdf path N a page" : "; cat \"path#Section\" reads one";
        if (wordsOnly) return $"(ranked by words{read})";
        var by = meaningOnly ? "meaning" : "words and meaning";
        if (semantic is null || semantic.State != SemanticState.Ready) return $"(ranked by words only: {semantic?.Reason ?? "no index"}{read})";
        // Some vaults' embedding provider is unavailable: their notes are ranked by words, or not at all by meaning only.
        if (semantic.WordsOnlyVaults.Count > 0 && semantic.Reason is { } partial && !notIndexed)
        {
            return meaningOnly
                ? $"(ranked by meaning, without {partial}; semsearch --words finds those notes{read})"
                : $"(ranked by {by}, and by words only in {partial}{read})";
        }

        // The index counts the pending notes below the scope without knowing which ones the search left out, so the
        // count may take in archived notes; telling them apart would mean asking the index for their paths.
        if (semantic.Pending > 0)
        {
            var one = semantic.Pending == 1;
            var subject = one ? "note isn't" : "notes aren't";
            if (notIndexed) return $"(ranked by words only: {semantic.Pending} {subject} indexed yet{read})";
            var verb = one ? "isn't" : "aren't";
            var rest = meaningOnly ? $"{verb} ranked" : $"{verb} ranked by meaning; semsearch --words finds {(one ? "it" : "them")}";
            return $"(ranked by {by}; {semantic.Pending} {subject} indexed yet and {rest}{read})";
        }

        return $"(ranked by {by}{read})";
    }
}
