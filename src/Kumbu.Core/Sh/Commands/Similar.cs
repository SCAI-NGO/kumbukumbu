// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Links;
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
          --unlinked     leave out the notes that link yours or that yours links (1 step), and those
                         a person called not related to it (the not-related property of either note)
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

        var sources = new List<SimilarSource>();
        foreach (var operand in operands.Take(wanted))
        {
            var (pathText, fragment) = SectionAddress.Split(operand);
            if (!CommandArgs.TryParsePath(Name, pathText, out var path, out var pathError)) return pathError!;
            // A missing note gets the error every command gives for it.
            if (await notes.ReadAsync(path, ct) is null)
            {
                var read = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
                if (read.IsError) return read;
            }

            sources.Add(new SimilarSource(path, fragment));
        }

        // A person's "Not related" (link prediction) counts as a link here: a pair they rejected isn't suggested again.
        Func<Note, bool>? leftOut = exclusions.LeavesOut;
        if (options.Has("unlinked"))
        {
            var rejected = new List<Func<Note, bool>>();
            foreach (var source in sources) rejected.Add(NotRelated.LeftOutFor(new Note(source.Path, (await notes.ReadAsync(source.Path, ct))!)));
            var outside = leftOut;
            leftOut = n => outside?.Invoke(n) == true || rejected.Any(r => r(n));
        }

        var result = await SimilarSearch.RunAsync(notes, index, new SimilarQuery(
            sources, scope, max, options.Has("unlinked"), options.Has("archive"), leftOut, exclusions.WithoutTags), ct);
        if (result.Error is { } error) return ShellResult.Error($"similar: {error}");

        var lines = new List<string> { Heading(sources, scope, result.Hits.Count) };
        for (var i = 0; i < result.Hits.Count; i++)
        {
            var hit = result.Hits[i];
            var c = hit.Chunk;
            var score = string.Create(CultureInfo.InvariantCulture, $"score {hit.Score:0.00}");
            if (between) score += string.Create(CultureInfo.InvariantCulture, $" ({hit.Scores[0]:0.00} to A, {hit.Scores[1]:0.00} to C)");
            var distance = between ? $"{Distance(hit, 0)} from A, {Distance(hit, 1)} to C" : Distance(hit, 0);
            lines.Add($"{i + 1}. {c.Path.Value}{c.Address}  lines {c.StartLine + 1}-{c.EndLine}  {score}  {distance}");
            lines.Add("   " + SectionSearch.Snippet(c));
        }

        lines.Add(Footer(result));
        return ShellResult.Ok(Lines.Join(lines));
    }

    private static string Distance(SimilarHit hit, int source) =>
        hit.OtherVault[source] ? "other vault"
        : hit.Distances[source] is { } d ? d == 1 ? "1 step" : $"{d} steps"
        : "not connected";

    private static string Heading(IReadOnlyList<SimilarSource> sources, VaultPath scope, int count)
    {
        var where = scope.IsRoot ? "in every vault" : $"below {scope.Value}";
        var what = sources.Count == 1 ? $"similar to {sources[0].Shown}" : $"between A = {sources[0].Shown} and C = {sources[1].Shown}";
        return $"{what}, {where}: {count} {(count == 1 ? "note" : "notes")}";
    }

    private static string Footer(SimilarResult result)
    {
        var parts = new List<string> { "by the stored embeddings, nothing sent to an embedding provider", "distance counts links either way" };
        if (result.Pending > 0) parts.Add($"{result.Pending} {(result.Pending == 1 ? "note isn't" : "notes aren't")} indexed yet and not compared");
        if (result.OtherModel > 0) parts.Add($"{result.OtherModel} {(result.OtherModel == 1 ? "note is" : "notes are")} embedded by another model and not compared");
        if (result.Reason is { } reason) parts.Add($"not compared: {reason}");
        if (result.Hits.Count > 0) parts.Add("cat \"path#Section\" reads one");
        return $"({string.Join("; ", parts)})";
    }
}
