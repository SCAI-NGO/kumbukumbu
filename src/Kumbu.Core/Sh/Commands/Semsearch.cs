// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// Ranked search over sections (concept §6): embeddings for meaning, BM25 for exact words and names. The hybrid
/// ranks by cosine similarity plus a small bonus for the query's words; <c>--words</c> ranks by BM25 alone.
/// <c>rg</c> stays the plain, unranked regex search.
/// </summary>
public sealed class Semsearch(INoteReader notes, ISearchIndex index) : ICommand
{
    private const int DefaultMax = 10;

    private static readonly Option[] Spec = [new('n', "max", TakesValue: true), new(null, "words"), new(null, "meaning")];

    public string Name => "semsearch";

    public string Usage => "semsearch [-n N] [--words|--meaning] <query> [path]";

    public string Summary => "ranked search by words and meaning; finds sections";

    public string Help => """
        semsearch [-n N] [--words|--meaning] <query> [path]
          Ranked search for the sections that best match a question or a topic, in every vault or below
          a path. Combines two rankings: by meaning (embeddings: the same idea in other words, also across
          languages) and by words (BM25: exact terms, names, abbreviations). The meaning score gets a
          bonus for the words of the query a section contains. Prints the best sections (-n, default 10)
          as path#Section with their lines, their score and the start of their text; scores are
          comparable within one search, not across searches. Then cat "path#Section" reads one. For exact
          text or a regex, use rg.
          The query is the words before the path; quoting it is optional.
          --words    rank by words only        --meaning  rank by meaning only

          semsearch how do agents read notes
          semsearch -n 5 'Regeln am Eingang' /Demo
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

        IReadOnlyList<Note> found;
        if (!scope.IsRoot && await notes.ReadAsync(scope, ct) is { } single) found = [new Note(scope, single)];
        else
        {
            found = await notes.ReadNotesUnderAsync(scope, ct);
            var isVault = scope.Segments.Count == 1 && (await notes.ListVaultsAsync(ct)).Contains(scope.Segments[0]);
            if (found.Count == 0 && !scope.IsRoot && !isVault) return await CommandArgs.ReadNoteAsync(Name, notes, scope, ct);
        }

        var mode = options.Has("words") ? SearchMode.Words : options.Has("meaning") ? SearchMode.Meaning : SearchMode.Hybrid;
        var (listed, semantic, notIndexed) = await SectionSearch.RankAsync(found, scope, query, mode, max, index, ct) switch
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
            lines.Add($"{i + 1}. {c.Path.Value}{c.Address}  lines {c.StartLine + 1}-{c.EndLine}{shown}");
            lines.Add($"   {SectionSearch.Snippet(c)}");
        }

        lines.Add(Footer(semantic, options.Has("words"), options.Has("meaning"), notIndexed, listed.Count > 0));
        return ShellResult.Ok(Lines.Join(lines));
    }

    private static string Footer(SemanticResult? semantic, bool wordsOnly, bool meaningOnly, bool notIndexed, bool any)
    {
        var read = any ? "; cat \"path#Section\" reads one" : "";
        if (wordsOnly) return $"(ranked by words{read})";
        var by = meaningOnly ? "meaning" : "words and meaning";
        if (semantic is null || semantic.State != SemanticState.Ready) return $"(ranked by words only: {semantic?.Reason ?? "no index"}{read})";
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
