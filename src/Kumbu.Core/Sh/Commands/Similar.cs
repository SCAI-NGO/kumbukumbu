// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Numerics;
using Kumbu.Core.Graph;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// Search by place (spec 2026-11-22-knowledge-synthesis): the sections closest in meaning to a note or a section, one per
/// note, each with its link distance to it, from the stored embeddings alone. <c>--between</c> ranks the sections close to
/// two notes at once, by the lower of the two. Swanson's open and closed discovery, for the skill
/// <c>synthesize-knowledge</c>.
/// </summary>
public sealed class Similar(INoteReader notes, ISearchIndex index) : ICommand
{
    private const int DefaultMax = 10;
    private const int MaxMax = 50;

    private static readonly Option[] Spec =
    [
        new('n', "max", TakesValue: true), new(null, "unlinked"), new(null, "between"), new(null, "archive"), .. Exclusions.Spec,
    ];

    public string Name => "similar";

    public string Usage => "similar <note>[#section] [path] [-n N] [--unlinked] [--outside P] [--without-tag T] | --between <a> <c> [path]";

    public string Summary => "the notes closest in meaning to a note, with how far they are from it in links";

    public string Help => $"""
        similar <note>[#section] [path] [-n N] [--unlinked] [--outside P] [--without-tag T] [--archive]
          The sections closest in meaning to a note, or to one of its sections, in every vault or below a
          path: one per note, its best section, with the lines, the score and how many links the note is
          from yours, in either direction ('1 step' links them directly, 'not connected' never, 'other
          vault' can't, as links never cross vaults). Then the start of the section. Compares the stored
          embeddings only: nothing is sent to an embedding provider. Notes embedded by another model than
          yours, and notes not indexed yet, aren't compared; the last line counts them. Scores are
          comparable within one run. cat "path#Section" reads a hit.
          Close in meaning and far in links is what nobody connected yet: a candidate, not a finding.
          -n N           how many notes (default {DefaultMax}, at most {MaxMax})
          --unlinked     leave out the notes that link yours or that yours links (1 step)
          --outside P    leave out the notes at or below P, such as your note's own folder; repeat for more
          --without-tag T  leave out the notes that carry #T (nested too), also from the link distances;
                         repeat for more. Earlier findings that link what they connect go so.
          --archive      take in a vault's archive (_archive/); a path inside it always does
        similar --between <a>[#section] <c>[#section] [path] [-n N] [options above]
          The sections close to both notes at once, ranked by the lower of the two scores, with their link
          distance to each: what could bridge two notes that never mention each other. graph --path a c
          gives the bridges by links.

          similar /Research/Fish oil.md
          similar "/Research/Fish oil.md#Effects" --unlinked --outside /Research
          similar --between /Research/Fish oil.md /Health/Raynaud.md
        """;

    public bool ReadsInput => false;

    /// <param name="Space">The model the source is embedded by: a note's vectors all come from its vault's.</param>
    private sealed record Source(VaultPath Path, string Shown, string Space, IReadOnlyList<float[]> Vectors);

    private sealed record Hit(Chunk Chunk, double Score, double[] Scores);

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "similar compares the notes as they are now") is { } versionError) return versionError;
        if (!Options.TryParse(Name, args, Spec, out var options, out var optionError)) return optionError;
        if (!Exclusions.TryParse(Name, options, out var exclusions, out var exclusionError)) return exclusionError!;
        var max = DefaultMax;
        if (options.Value("max") is { } maxText && (!int.TryParse(maxText, out max) || max is < 1 or > MaxMax))
        {
            return ShellResult.Error($"similar: -n must be 1 to {MaxMax}");
        }

        var between = options.Has("between");
        var wanted = between ? 2 : 1;
        var operands = options.Operands;
        if (operands.Count < wanted) return ShellResult.Error(between ? "similar: --between needs two notes: similar --between /Work/A.md /Work/C.md" : $"similar: missing note ({Usage})");
        if (operands.Count > wanted + 1) return ShellResult.Error($"similar: too many arguments{CommandArgs.QuoteHint}");

        var scope = VaultPath.Root;
        if (operands.Count == wanted + 1)
        {
            if (!CommandArgs.TryParsePath(Name, operands[^1], out scope, out var scopeError)) return scopeError!;
            if (scope.IsNote) return ShellResult.Error($"similar: {scope.Value} is a note; the path to search below is a vault or a folder");
        }

        var sources = new List<Source>();
        foreach (var operand in operands.Take(wanted))
        {
            var (source, sourceError) = await SourceAsync(operand, ct);
            if (source is null) return sourceError!;
            sources.Add(source);
        }

        if (between && sources[0].Path == sources[1].Path) return ShellResult.Error("similar: --between needs two different notes");

        var space = sources[0].Space;
        if (between && sources[1].Space != space)
        {
            return ShellResult.Error($"similar: {sources[0].Path.Value} and {sources[1].Path.Value} are embedded by different models, whose scores don't compare");
        }

        var stored = await index.VectorsAsync(scope, ct);
        if (stored.State != SemanticState.Ready) return ShellResult.Error($"similar: {stored.Reason ?? "search by meaning isn't available"}");

        // The candidates: the notes below the scope that aren't left out, as semsearch takes them.
        var archive = ArchiveFolder.Shown(scope, options.Has("archive"));
        var sourcePaths = sources.Select(s => s.Path).ToHashSet();
        var candidates = (await notes.ReadNotesUnderAsync(scope, ct))
            .Where(n => !sourcePaths.Contains(n.Path) && (archive || !ArchiveFolder.Contains(n.Path)) && !exclusions.LeavesOut(n))
            .ToDictionary(n => n.Path);

        var (best, otherModel) = Rank(sources, candidates, stored.Chunks, space);

        // Link distances per source, in its vault's graph, walked without the notes left out by tag or archive.
        var filter = new GraphFilter([], [], Archive: archive) { WithoutTags = exclusions.WithoutTags };
        var distances = new List<IReadOnlyDictionary<VaultPath, int>>();
        foreach (var source in sources)
        {
            var graph = await notes.GraphAsync(VaultPath.Root.Child(source.Path.Vault!), ct);
            distances.Add(GraphQueries.Distances(graph, source.Path, filter));
        }

        var unlinked = options.Has("unlinked");
        var ranked = best.Values
            .Where(h => !unlinked || distances.All(d => d.GetValueOrDefault(h.Chunk.Path) != 1))
            .OrderByDescending(h => h.Score).ThenBy(h => h.Chunk.Path.Value, TextOrder.Default)
            .Take(max).ToList();

        var lines = new List<string> { Heading(sources, scope, ranked.Count) };
        for (var i = 0; i < ranked.Count; i++)
        {
            var hit = ranked[i];
            var c = hit.Chunk;
            var score = string.Create(CultureInfo.InvariantCulture, $"score {hit.Score:0.00}");
            if (between) score += string.Create(CultureInfo.InvariantCulture, $" ({hit.Scores[0]:0.00} to A, {hit.Scores[1]:0.00} to C)");
            var distance = between
                ? $"{Distance(sources[0], distances[0], c.Path)} from A, {Distance(sources[1], distances[1], c.Path)} to C"
                : Distance(sources[0], distances[0], c.Path);
            lines.Add($"{i + 1}. {c.Path.Value}{c.Address}  lines {c.StartLine + 1}-{c.EndLine}  {score}  {distance}");
            lines.Add("   " + SectionSearch.Snippet(c));
        }

        lines.Add(Footer(stored, otherModel, ranked.Count > 0));
        return ShellResult.Ok(Lines.Join(lines));
    }

    /// <summary>The note or section of <paramref name="operand"/> with its stored vectors, or the error that says why there is none.</summary>
    private async Task<(Source?, ShellResult?)> SourceAsync(string operand, CancellationToken ct)
    {
        var (pathText, fragment) = SectionAddress.Split(operand);
        if (!CommandArgs.TryParsePath(Name, pathText, out var path, out var pathError)) return (null, pathError);
        if (await notes.ReadAsync(path, ct) is not { } content)
        {
            var read = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
            return (null, read.IsError ? read : ShellResult.Error($"similar: {path.Value}: not a note"));
        }

        if (!path.IsNote) return (null, ShellResult.Error($"similar: {path.Value}: not a note"));
        var (first, end) = (0, int.MaxValue);
        if (fragment is not null)
        {
            if (!SectionLookup.TryFind(path, content, fragment, out var match, out var sectionError)) return (null, ShellResult.Error($"similar: {sectionError}"));
            (first, end) = (match.Heading.Line, match.Heading.ContentEnd);
        }

        var shown = fragment is null ? path.Value : $"{path.Value}#{fragment}";
        var stored = await index.VectorsAsync(path, ct);
        if (stored.State != SemanticState.Ready) return (null, ShellResult.Error($"similar: {stored.Reason ?? "search by meaning isn't available"}"));
        var own = stored.Chunks.Where(c => c.Path == path && c.StartLine >= first && c.StartLine < end).ToList();
        if (own.Count == 0)
        {
            var what = fragment is null ? "isn't indexed yet" : "has no indexed text yet";
            return (null, ShellResult.Error($"similar: {shown} {what}; try again in a while, or semsearch for its topic meanwhile"));
        }

        return (new Source(path, shown, own[0].Space, [.. own.Select(c => c.Vector)]), null);
    }

    /// <summary>
    /// Each candidate note's best chunk: its score is, per source, the best cosine of any of the source's chunks with it,
    /// and over the sources the lowest of those (with one source, just its). Chunks of another space or length are left
    /// out and their notes counted; a chunk is matched to the note's current chunk as semsearch matches it.
    /// </summary>
    private (Dictionary<VaultPath, Hit> Best, int OtherModel) Rank(
        IReadOnlyList<Source> sources, Dictionary<VaultPath, Note> candidates, IReadOnlyList<ChunkVector> stored, string space)
    {
        var bound = index.Bound;
        var length = sources[0].Vectors[0].Length;
        var best = new Dictionary<VaultPath, Hit>();
        var otherModel = new HashSet<VaultPath>();
        var compared = new HashSet<VaultPath>();
        var chunksOf = new Dictionary<VaultPath, List<Chunk>>();
        foreach (var vector in stored)
        {
            if (!candidates.TryGetValue(vector.Path, out var note)) continue;
            if (vector.Space != space || vector.Vector.Length != length)
            {
                otherModel.Add(vector.Path);
                continue;
            }

            compared.Add(vector.Path);
            if (!chunksOf.TryGetValue(vector.Path, out var chunks)) chunksOf[vector.Path] = chunks = Chunker.Split(note, bound);
            var chunk = chunks.FirstOrDefault(c => c.StartLine == vector.StartLine && c.Part == vector.Part)
                ?? chunks.FirstOrDefault(c => c.StartLine <= vector.StartLine && vector.StartLine < c.EndLine);
            if (chunk is null || SectionSearch.IsHeadingOnly(chunk)) continue;

            var scores = sources.Select(s => s.Vectors.Max(v => Dot(v, vector.Vector))).ToArray();
            var score = scores.Min();
            if (!best.TryGetValue(vector.Path, out var known) || score > known.Score) best[vector.Path] = new Hit(chunk, score, scores);
        }

        otherModel.ExceptWith(compared);
        return (best, otherModel.Count);
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var acc = Vector<float>.Zero;
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count) acc += new Vector<float>(a, i) * new Vector<float>(b, i);
            sum = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    private static string Distance(Source source, IReadOnlyDictionary<VaultPath, int> distances, VaultPath path) =>
        path.Vault != source.Path.Vault ? "other vault"
        : distances.TryGetValue(path, out var d) ? d == 1 ? "1 step" : $"{d} steps"
        : "not connected";

    private static string Heading(IReadOnlyList<Source> sources, VaultPath scope, int count)
    {
        var where = scope.IsRoot ? "in every vault" : $"below {scope.Value}";
        var what = sources.Count == 1 ? $"similar to {sources[0].Shown}" : $"between A = {sources[0].Shown} and C = {sources[1].Shown}";
        return $"{what}, {where}: {count} {(count == 1 ? "note" : "notes")}";
    }

    private static string Footer(StoredVectors stored, int otherModel, bool any)
    {
        var parts = new List<string> { "by the stored embeddings, nothing sent to an embedding provider", "distance counts links either way" };
        if (stored.Pending > 0) parts.Add($"{stored.Pending} {(stored.Pending == 1 ? "note isn't" : "notes aren't")} indexed yet and not compared");
        if (otherModel > 0) parts.Add($"{otherModel} {(otherModel == 1 ? "note is" : "notes are")} embedded by another model and not compared");
        if (stored.Reason is { } reason) parts.Add($"not compared: {reason}");
        if (any) parts.Add("cat \"path#Section\" reads one");
        return $"({string.Join("; ", parts)})";
    }
}
