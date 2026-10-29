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
/// meaning, shown with the start of their stored text.
/// </summary>
public sealed class Semsearch(INoteReader notes, ISearchIndex index, IAttachmentPixels? pixels = null) : ICommand
{
    private const int DefaultMax = 10;

    private static readonly Option[] Spec = [new('n', "max", TakesValue: true), new(null, "words"), new(null, "meaning"), new(null, "tag", TakesValue: true)];

    public string Name => "semsearch";

    public string Usage => "semsearch [-n N] [--words|--meaning] [--tag T] <query> [path]";

    public string Summary => "ranked search by words and meaning; finds sections and PDF pages";

    public string Help => """
        semsearch [-n N] [--words|--meaning] [--tag T] <query> [path]
          Ranked search for the sections that best match a question or a topic, in every vault or below
          a path. Combines two rankings: by meaning (embeddings: the same idea in other words, also across
          languages) and by words (BM25: exact terms, names, abbreviations). The meaning score gets a
          bonus for the words of the query a section contains. Prints the best sections (-n, default 10)
          as path#Section with their lines, their score and the start of their text; scores are
          comparable within one search, not across searches. Then cat "path#Section" reads one. For exact
          text or a regex, use rg. Pages of PDFs rank among the sections by meaning, not by words and
          not with --tag: a hit prints as path p7 with its score and the start of the page's text, and
          pdf path 7 reads it; a PDF as the path ranks its own pages.
          Pages without a text layer (scans) and images are not searched.
          The query is the words before the path; quoting it is optional.
          --words    rank by words only        --meaning  rank by meaning only
          --tag T    only notes that carry #T (nested too, ignoring case; 'help tags'); repeat
                     for notes with all of them. The ranking is the same, over fewer notes.

          semsearch how do agents read notes
          semsearch --tag research modelling capabilities
          semsearch -n 5 'Regeln am Eingang' /Demo
          semsearch attention heads /Work/Papers/smith-2025.pdf
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var error)) return error;
        var max = DefaultMax;
        if (options.Value("max") is { } maxText && (!int.TryParse(maxText, out max) || max < 1))
        {
            return ShellResult.Error($"semsearch: invalid number of results: '{maxText}'");
        }

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
        }

        if (options.Values("tag") is { Count: > 0 } tags)
        {
            var (carrying, tagError) = TagFilter.Narrow(found, tags, scope);
            if (carrying is null) return ShellResult.Error($"semsearch: {tagError}");
            found = carrying;
        }

        var mode = options.Has("words") ? SearchMode.Words : options.Has("meaning") ? SearchMode.Meaning : SearchMode.Hybrid;
        var (listed, semantic, notIndexed) = await SectionSearch.RankAsync(found, scope, query, mode, max, index, ct, pages) switch
        {
            { MeaningUnavailable: true } r => (null, r.Semantic, false),
            var r => (r.Hits, r.Semantic, r.FellBackToWords),
        };
        if (listed is null) return ShellResult.Error($"semsearch: {semantic!.Reason ?? "search by meaning isn't available"}; without --meaning, it ranks by words");

        var lines = new List<string>();
        if (listed.Count == 0) lines.Add($"(nothing found for '{query}'{(scope.IsRoot ? "" : " below " + scope.Value)})");
        for (var i = 0; i < listed.Count; i++)
        {
            var (c, score) = listed[i];
            var shown = score is { } s ? string.Create(CultureInfo.InvariantCulture, $"  score {s:0.00}") : "";
            if (listed[i].Page is { } page)
            {
                lines.Add($"{i + 1}. {c.Path.Value} p{page}{shown}");
                lines.Add($"   {await PageStartAsync(c.Path, page, ct)}");
                continue;
            }

            lines.Add($"{i + 1}. {c.Path.Value}{c.Address}  lines {c.StartLine + 1}-{c.EndLine}{shown}");
            lines.Add($"   {SectionSearch.Snippet(c)}");
        }

        lines.Add(Footer(semantic, options.Has("words"), options.Has("meaning"), notIndexed, listed.Count > 0, listed.Any(h => h.Page is not null)));
        return ShellResult.Ok(Lines.Join(lines));
    }

    /// <summary>The start of a page's stored text on one line, as a section's snippet.</summary>
    private async Task<string> PageStartAsync(VaultPath path, int page, CancellationToken ct)
    {
        const string Unavailable = "(the text of this page can't be read right now)";
        if (await StoreAttachments.GetAsync(notes, path, ct) is not { } attachment) return Unavailable;
        IReadOnlyList<string>? text;
        try
        {
            text = await pixels!.PageTextAsync(path, attachment.Current, page, page, ct);
        }
        catch (PixelsUnavailableException)
        {
            return Unavailable;
        }

        if (text is not [var only]) return Unavailable;
        return SectionSearch.Shorten(string.Join(' ', only.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)));
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
