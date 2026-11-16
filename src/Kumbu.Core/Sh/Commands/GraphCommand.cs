// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Graph;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

public sealed class GraphCommand(INoteReader notes) : ICommand
{
    public string Name => "graph";

    private static readonly Option[] Spec =
    [
        new(null, "path"), new(null, "orphans"), new(null, "hubs"), new(null, "unresolved"),
        new(null, "depth", true), new(null, "out"), new(null, "in"), new(null, "cap", true), new('n', "top", true),
        new(null, "tag", true), new(null, "archive"), new(null, "no-days"), new(null, "no-attachments"), new(null, "no-ghosts"),
    ];

    private static readonly string[] Modes = ["path", "orphans", "hubs", "unresolved"];
    
    // What the last line may take on top of the budget: the line that names how to narrow it.
    private const int TailReserve = 1_500;

    public string Usage => "graph <note> [--depth N] [--out|--in] [--cap N] | --path <a> <b> [--out] | --orphans|--hubs [-n N]|--unresolved [path]";

    public string Summary => "how notes link: the neighbourhood of a note, paths between two notes, orphans, hubs, unresolved links";

    public string Help => """
        graph <note> [--depth N] [--out|--in] [--cap N]
          The notes around a note by their links: a heading with the counts, then one line per link as
          'depth  source -> target', grouped by depth (how many links from the note the farther end was first
          met). Depth is 1 to 3 (default 1). Both directions by default: --out follows the links the note
          writes, --in the links that lead to it. A note with more than --cap neighbours (1 to 100, default 20)
          shows the best-connected ones and a line '+N more linking to X: graph X --in' to go on from X; at
          depth 2 and 3 that count can include notes already shown elsewhere. '(embed)' marks an embed,
          '(alias)' a link by an alias, '(ambiguous)' a link that several notes match, 'Name (no note)' a link
          to nothing. Attachments and links to nothing end a walk.
        graph --path <a> <b> [--out]
          The shortest paths between two notes (at most 5, at most 6 links), one per line, each arrow in its
          link's own direction: /W/A.md -> /W/C.md <- /W/B.md. --out follows links as written. Says whether the
          notes are not connected at all or farther apart than 6 steps. Takes the filters: --no-days finds connections that don't run through day notes.
        graph --orphans [path]
          The notes below a path (default: every vault) that no note links to and that link to nothing.
        graph --hubs [path] [-n N]
          The N best-connected notes (default 20) with their links in and out.
        graph --unresolved [path]
          The links that lead to no note, by how many notes write them, with the notes and lines.

          --tag T         only notes with the tag (several: all of them); attachments and names stay when a kept note links to them
          --archive       take in the notes of the archive (_archive/); a path inside it always does
          --no-days       leave out day and week notes
          --no-attachments, --no-ghosts   leave out attachments, or the links that lead to nothing

          graph /Work/Plan.md
          graph /Work/Plan.md --depth 2 --out
          graph --path /Work/Plan.md /Work/Budget.md
          graph --orphans /Work
          graph --hubs -n 10 --no-days
          graph --unresolved /Work
        """;

    public bool ReadsInput => false;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (CommandArgs.RefuseVersion(Name, args, "graph reads the notes as they are now") is { } versionError) return versionError;
        if (!Options.TryParse(Name, args, Spec, out var options, out var optionError)) return optionError;

        var modes = Modes.Where(options.Has).ToList();
        if (modes.Count > 1) return ShellResult.Error("graph: --path, --orphans, --hubs and --unresolved are exclusive: give one");
        var mode = modes.FirstOrDefault();
        if (Misuse(options, mode) is { } misuse) return ShellResult.Error(misuse);

        if (mode == "path") return await PathsAsync(options, ct);

        var depth = 1;
        var cap = 20;
        if (options.Value("depth") is { } d && !(int.TryParse(d, out depth) && depth is >= 1 and <= 3)) return ShellResult.Error("graph: --depth must be 1 to 3");
        if (options.Value("cap") is { } c && !(int.TryParse(c, out cap) && cap is >= 1 and <= 100)) return ShellResult.Error("graph: --cap must be 1 to 100");
        var top = 20;
        if (options.Value("top") is { } n && !(int.TryParse(n, out top) && top >= 1)) return ShellResult.Error("graph: -n must be 1 or more");

        if (mode is null)
        {
            if (options.Operands.Count == 0) return ShellResult.Error("graph: missing operand");
            if (options.Operands.Count > 1) return ShellResult.Error($"graph: too many arguments{CommandArgs.QuoteHint}");
            if (!CommandArgs.TryParsePath(Name, options.Operands[0], out var note, out var pathError)) return pathError!;
            var direction = options.Has("out") ? GraphDirection.Out : options.Has("in") ? GraphDirection.In : GraphDirection.Both;
            return await LocalAsync(note, depth, direction, cap, Filter(options, [], note), ct);
        }

        return await ListAsync(mode, options, top, ct);
    }

    private static string? Misuse(Options options, string? mode)
    {
        if (options.Has("out") && options.Has("in")) return "graph: --out and --in are exclusive: give one";
        if (options.Has("top") && mode != "hubs") return "graph: -n applies to --hubs only";
        if (mode is "orphans" or "hubs" or "unresolved")
        {
            foreach (var local in new[] { "depth", "cap", "out", "in" }) if (options.Has(local)) return $"graph: --{local} applies to a local graph only";
        }

        if (mode == "path")
        {
            foreach (var local in new[] { "depth", "cap", "in" }) if (options.Has(local)) return $"graph: --{local} applies to a local graph only";
        }

        return null;
    }

    private static GraphFilter Filter(Options options, IReadOnlyList<VaultPath> paths, VaultPath scope) =>
        new(paths, options.Values("tag"), !options.Has("no-days"), !options.Has("no-attachments"), !options.Has("no-ghosts"),
            ArchiveFolder.Shown(scope, options.Has("archive")));

    private static VaultPath VaultOf(VaultPath path) => VaultPath.Root.Child(path.Segments[0]);

    // --- the local graph -----------------------------------------------------------------------------------------

    private async Task<ShellResult> LocalAsync(VaultPath note, int depth, GraphDirection direction, int cap, GraphFilter filter, CancellationToken ct)
    {
        if (note.IsRoot) return ShellResult.Error("graph: give a note, e.g. graph /Work/Plan.md");
        var graph = await notes.GraphAsync(VaultOf(note), ct);
        if (graph.IndexOf(note) is null)
        {
            if (await StoreAttachments.GetAsync(notes, note, ct) is not null) return ShellResult.Ok($"{note.Value} is an attachment that no note links to");
            var read = await CommandArgs.ReadNoteAsync(Name, notes, note, ct);
            return read.IsError ? read : ShellResult.Error($"graph: {note.Value}: not in the vault's link graph");
        }

        var local = GraphQueries.Local(graph, note, depth, direction, cap, filter);
        var depthOf = local.Nodes.ToDictionary(n => n.Node, n => n.Depth);
        var shown = depthOf.Keys.ToHashSet();

        var noteCount = local.Nodes.Count(n => graph.Nodes[n.Node].Kind is NodeKind.Note or NodeKind.Day);
        var attachments = local.Nodes.Count(n => graph.Nodes[n.Node].Kind == NodeKind.Attachment);
        var ghosts = local.Nodes.Count(n => graph.Nodes[n.Node].Kind == NodeKind.Ghost);
        var heading = new StringBuilder($"{note.Value}, depth {depth}, {DirectionText(direction)}: {Count(noteCount, "note")}");
        if (attachments > 0) heading.Append($", {Count(attachments, "attachment")}");
        if (ghosts > 0) heading.Append($", {ghosts} without a note");

        // An edge's depth is that of its farther end, the links of nearer notes first; a capped node's remainder follows the edges of the
        // depth its neighbours would have had.
        var lines = new List<Line>();
        for (var level = 1; level <= depth; level++)
        {
            foreach (var e in local.Edges.Where(e => Math.Max(depthOf[graph.Edges[e].From], depthOf[graph.Edges[e].To]) == level).OrderBy(e => depthOf[graph.Edges[e].From]))
            {
                var edge = graph.Edges[e];
                lines.Add(new Line($"{level}  {Show(graph, edge.From)} -> {Show(graph, edge.To)}{Marks(graph, edge)}", graph.Nodes[edge.From].Path));
            }

            foreach (var (node, _) in local.Nodes.Where(n => n.Depth == level - 1 && local.LeftOut.ContainsKey(n.Node)))
            {
                lines.Add(new Line(More(graph, node, local.LeftOut[node], direction, shown), graph.Nodes[node].Path));
            }
        }

        if (lines.Count == 0) lines.Add(new Line("(no links shown)", null));
        var asked = direction switch { GraphDirection.Out => " --out", GraphDirection.In => " --in", _ => "" };
        return Render(heading.ToString(), lines, from => Tail(lines.Count - from, depth > 1 ? $"a smaller --depth (now {depth}), " : "",
            "--cap or --tag", lines.Skip(from).Select(l => l.Node).FirstOrDefault(n => n is not null && n != note) is { } n
                ? $"go on from {n.Value}: graph {SectionLookup.Quote(n)}{asked}" : null));
    }

    private static string DirectionText(GraphDirection d) => d switch
    {
        GraphDirection.Out => "outgoing links only",
        GraphDirection.In => "incoming links only",
        _ => "both directions",
    };

    private static string Show(LinkGraph g, int node) => g.Nodes[node].Path?.Value ?? $"{g.Nodes[node].Title} (no note)";

    // The ghost's own "(no note)" is part of its name above; the flags follow the target.
    private static string Marks(LinkGraph g, GraphEdge edge)
    {
        var marks = new List<string>();
        if (edge.Flags.HasFlag(EdgeFlags.Embed)) marks.Add("embed");
        if (edge.Flags.HasFlag(EdgeFlags.Alias)) marks.Add("alias");
        if (edge.Flags.HasFlag(EdgeFlags.Ambiguous)) marks.Add("ambiguous");
        return marks.Count == 0 ? "" : $" ({string.Join(", ", marks)})";
    }

    // At depth 2 and 3 the count can include notes that are shown elsewhere, so the line only says what stays true: how
    // many more link to or from the node, and how to go on from it. With both directions it says which when the shown
    // notes already hold every link of the other one.
    private static string More(LinkGraph g, int node, int count, GraphDirection direction, HashSet<int> shown)
    {
        var path = g.Nodes[node].Path!;
        var outAllShown = g.OutEdges(node).All(e => shown.Contains(g.Edges[e].To));
        var inAllShown = g.InEdges(node).All(e => shown.Contains(g.Edges[e].From));
        var (words, flag) = direction switch
        {
            GraphDirection.In => ("linking to", " --in"),
            GraphDirection.Out => ("linked from", " --out"),
            _ when outAllShown => ("linking to", " --in"),
            _ when inAllShown => ("linked from", " --out"),
            _ => ("linking to or from", ""),
        };
        return $"   +{count} more {words} {path.Value}: graph {SectionLookup.Quote(path)}{flag}";
    }

    // --- paths --------------------------------------------------------------------------------------------------------

    private async Task<ShellResult> PathsAsync(Options options, CancellationToken ct)
    {
        if (options.Operands.Count != 2) return ShellResult.Error("graph: --path needs two notes: graph --path /Work/A.md /Work/B.md");
        if (!CommandArgs.TryParsePath(Name, options.Operands[0], out var from, out var fromError)) return fromError!;
        if (!CommandArgs.TryParsePath(Name, options.Operands[1], out var to, out var toError)) return toError!;
        if (from.IsRoot || to.IsRoot) return ShellResult.Error("graph: --path needs two notes: graph --path /Work/A.md /Work/B.md");
        if (from.Segments[0] != to.Segments[0]) return ShellResult.Error($"graph: --path: {from.Value} and {to.Value} are in different vaults, and links never cross vaults");

        var graph = await notes.GraphAsync(VaultOf(from), ct);
        foreach (var end in new[] { from, to })
        {
            if (graph.IndexOf(end) is { } i && graph.Nodes[i].Kind is NodeKind.Note or NodeKind.Day) continue;
            var read = await CommandArgs.ReadNoteAsync(Name, notes, end, ct);
            return read.IsError ? read : ShellResult.Error($"graph: {end.Value}: not a note of the link graph");
        }

        var asWritten = options.Has("out");
        var result = GraphQueries.Paths(graph, from, to, asWritten ? GraphDirection.Out : GraphDirection.Both, Filter(options, [], from));
        if (result.Paths.Count == 0)
        {
            return ShellResult.Ok((result.Why, asWritten) switch
            {
                (Disconnection.FartherThanLimit, false) => $"{from.Value} and {to.Value} are farther than 6 steps apart",
                (Disconnection.FartherThanLimit, true) => $"{from.Value} and {to.Value} are farther than 6 steps apart following links as written",
                (_, false) => $"{from.Value} and {to.Value} are not connected at all (no path of any length)",
                (_, true) => $"no path from {from.Value} to {to.Value} following links as written",
            });
        }

        var steps = result.Paths[0].Count;
        var heading = steps == 0
            ? $"{from.Value} is the same note"
            : $"{from.Value} to {to.Value}, {Count(steps, "step")}, {(asWritten ? "following links as written" : "in either direction")}: {Count(result.Paths.Count, "path")}"
                + (result.More ? " shown, more of the same length exist" : "");
        var lines = result.Paths.Select(p => new Line(steps == 0 ? from.Value : Walk(graph, graph.IndexOf(from)!.Value, p), null)).ToList();
        return Render(heading, lines, first => Tail(lines.Count - first, "", "", null));
    }

    private static string Walk(LinkGraph g, int start, IReadOnlyList<int> path)
    {
        var text = new StringBuilder(Show(g, start));
        var at = start;
        foreach (var e in path)
        {
            var edge = g.Edges[e];
            var forward = edge.From == at;
            at = forward ? edge.To : edge.From;
            text.Append(forward ? " -> " : " <- ").Append(Show(g, at));
        }

        return text.ToString();
    }

    // --- orphans, hubs, unresolved ---------------------------------------------------------------------------------------

    private async Task<ShellResult> ListAsync(string mode, Options options, int top, CancellationToken ct)
    {
        var scope = VaultPath.Root;
        if (options.Operands.Count > 1) return ShellResult.Error($"graph: too many arguments{CommandArgs.QuoteHint}");
        if (options.Operands.Count == 1 && !CommandArgs.TryParsePath(Name, options.Operands[0], out scope, out var pathError)) return pathError!;

        var vaults = await notes.ListVaultsAsync(ct);
        if (!scope.IsRoot && !vaults.Contains(scope.Segments[0])) return ShellResult.Error($"graph: {scope.Value}: No such file or directory");
        var filter = Filter(options, scope.IsRoot ? [] : [scope], scope);

        var orphans = new List<string>();
        var hubs = new List<(string Path, int In, int Out)>();
        var ghosts = new List<(string Title, List<(VaultPath Source, int Line)> From)>();
        foreach (var vault in scope.IsRoot ? vaults : [scope.Segments[0]])
        {
            var graph = await notes.GraphAsync(VaultPath.Root.Child(vault), ct);
            switch (mode)
            {
                case "orphans": orphans.AddRange(GraphQueries.Orphans(graph, filter).Select(i => graph.Nodes[i].Path!.Value)); break;
                case "hubs": hubs.AddRange(GraphQueries.Hubs(graph, filter, top).Select(h => (graph.Nodes[h.Node].Path!.Value, h.In, h.Out))); break;
                default: ghosts.AddRange(GraphQueries.Unresolved(graph, filter).Select(u => (graph.Nodes[u.Ghost].Title, u.From.ToList()))); break;
            }
        }

        var narrow = mode switch
        {
            "hubs" => $"a smaller -n (now {top}) or a path (graph --hubs /Work){(scope.IsRoot ? "" : " below this one")}",
            _ => $"{(scope.IsRoot ? "a path" : "a deeper path")} (graph --{mode} /Work) or --tag",
        };
        List<Line> lines;
        switch (mode)
        {
            case "orphans":
                lines = [.. orphans.Order(TextOrder.Default).Select(p => new Line(p, null))];
                if (lines.Count == 0) return ShellResult.Ok("(no notes without links)");
                break;
            case "hubs":
                lines = [.. hubs.OrderByDescending(h => h.In + h.Out).ThenBy(h => h.Path, TextOrder.Default).Take(top)
                    .Select(h => new Line($"{h.In + h.Out}  {h.Path}  ({h.In} in, {h.Out} out)", null))];
                if (lines.Count == 0) return ShellResult.Ok("(no notes)");
                break;
            default:
                lines = [];
                foreach (var (title, from) in ghosts.OrderByDescending(g => g.From.Select(f => f.Source).Distinct().Count()).ThenBy(g => g.Title, TextOrder.Default))
                {
                    lines.Add(new Line($"{title}, linked from {Count(from.Select(f => f.Source).Distinct().Count(), "note")}:", null));
                    lines.AddRange(from.Select(f => new Line($"  {f.Source.Value}:{f.Line + 1}", null)));
                }

                if (lines.Count == 0) return ShellResult.Ok("(no unresolved links)");
                break;
        }

        return Render(null, lines, first => Tail(lines.Count - first, "", narrow, null));
    }

    // --- output within the budget -----------------------------------------------------------------------------------

    private sealed record Line(string Text, VaultPath? Node);

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>A line naming how to go on, once <paramref name="omitted"/> lines had to be left out.</summary>
    private static string Tail(int omitted, string smaller, string narrow, string? goOn)
    {
        var ways = new List<string>();
        if (smaller.Length + narrow.Length > 0) ways.Add($"narrow it with {smaller}{narrow}".TrimEnd(',', ' '));
        if (goOn is not null) ways.Add(goOn);
        return $"({Count(omitted, "more line")} not shown{(ways.Count == 0 ? "" : "; " + string.Join(", or ", ways))})";
    }

    // Lines are added while they fit under the output limit (less room for the last line); the rest is counted.
    private static ShellResult Render(string? heading, IReadOnlyList<Line> lines, Func<int, string> tail)
    {
        var limit = OutputLimit.MaxChars - TailReserve;
        var text = new StringBuilder(heading);
        var used = 0;
        foreach (var line in lines)
        {
            if (text.Length + line.Text.Length + 1 > limit) break;
            if (text.Length > 0) text.Append('\n');
            text.Append(line.Text);
            used++;
        }

        if (used < lines.Count) text.Append('\n').Append(tail(used));
        return ShellResult.Ok(text.ToString());
    }
}
