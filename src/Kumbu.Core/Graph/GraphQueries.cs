// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Graph;

public enum GraphDirection { Out, In, Both }

/// <param name="Nodes">Breadth-first, with the depth each was first reached at; the start is depth 0.</param>
/// <param name="LeftOut">Per node, how many of its neighbours the cap dropped.</param>
public sealed record LocalGraph(IReadOnlyList<(int Node, int Depth)> Nodes, IReadOnlyList<int> Edges, IReadOnlyDictionary<int, int> LeftOut);

public sealed record NodeDetail(GraphNode Node, int In, int Out, DateTime? Changed, DateTime? Created, bool CreatedAtLeast, string? FirstParagraph);

/// <summary>The questions the graph views ask of a <see cref="LinkGraph"/> (spec §6).</summary>
public static partial class GraphQueries
{
    private const int ParagraphLength = 300;

    /// <summary>Every node the filter keeps and every edge between them, uncapped.</summary>
    public static GraphSelection Global(LinkGraph g, GraphFilter f)
    {
        var kept = Kept(g, f);
        var nodes = Enumerable.Range(0, g.Nodes.Count).Where(i => kept[i]).ToList();
        var edges = Enumerable.Range(0, g.Edges.Count).Where(e => kept[g.Edges[e].From] && kept[g.Edges[e].To]).ToList();
        return new GraphSelection(nodes, edges);
    }

    /// <summary>
    /// The neighbourhood of <paramref name="note"/> to <paramref name="depth"/> links, each node once at the depth it is
    /// first met. A node with more than <paramref name="cap"/> neighbours keeps the best-connected ones (then by path).
    /// Ghosts and attachments are shown but never walked through. The start stays whatever the filter says.
    /// </summary>
    public static LocalGraph Local(LinkGraph g, VaultPath note, int depth, GraphDirection dir, int cap, GraphFilter f)
    {
        if (g.IndexOf(note) is not { } start) return new LocalGraph([], [], new Dictionary<int, int>());

        var kept = Kept(g, f);
        kept[start] = true;
        var depths = new Dictionary<int, int> { [start] = 0 };
        var order = new List<(int Node, int Depth)> { (start, 0) };
        var leftOut = new Dictionary<int, int>();
        var level = new List<int> { start };
        for (var d = 1; d <= depth && level.Count > 0; d++)
        {
            var next = new List<int>();
            foreach (var node in level)
            {
                if (g.Nodes[node].Kind is NodeKind.Ghost or NodeKind.Attachment) continue;
                var neighbours = Neighbours(g, node, dir).Where(n => kept[n]).ToList();
                if (neighbours.Count > cap)
                {
                    leftOut[node] = neighbours.Count - cap;
                    neighbours = [.. neighbours
                        .OrderByDescending(n => g.InEdges(n).Count + g.OutEdges(n).Count)
                        .ThenBy(n => g.Nodes[n].Key, TextOrder.Default)
                        .Take(cap)];
                }

                foreach (var n in neighbours)
                {
                    if (!depths.TryAdd(n, d)) continue;
                    order.Add((n, d));
                    next.Add(n);
                }
            }

            level = next;
        }

        var edges = Enumerable.Range(0, g.Edges.Count).Where(e => depths.ContainsKey(g.Edges[e].From) && depths.ContainsKey(g.Edges[e].To));
        return new LocalGraph(order, [.. edges], leftOut);
    }

    /// <summary>The notes (day notes included) the filter keeps that no note links to and that link to nothing.</summary>
    public static IReadOnlyList<int> Orphans(LinkGraph g, GraphFilter f)
    {
        var kept = Kept(g, f);
        return [.. Enumerable.Range(0, g.Nodes.Count)
            .Where(i => kept[i] && g.Nodes[i].Kind is NodeKind.Note or NodeKind.Day && g.InEdges(i).Count == 0 && g.OutEdges(i).Count == 0)];
    }

    /// <summary>The <paramref name="top"/> kept notes by links in plus links out, then by path. A link to or from a note
    /// <see cref="GraphFilter.WithoutTags"/> leaves out doesn't count (ADR 0028): an earlier hypothesis mustn't raise a note's degree.</summary>
    public static IReadOnlyList<(int Node, int In, int Out)> Hubs(LinkGraph g, GraphFilter f, int top)
    {
        var kept = Kept(g, f);
        var without = Normalised(f.WithoutTags);
        bool Counts(int other) => !Carries(g.Nodes[other], without);
        return [.. Enumerable.Range(0, g.Nodes.Count)
            .Where(i => kept[i] && g.Nodes[i].Kind is NodeKind.Note or NodeKind.Day)
            .Select(i => (Node: i, In: g.InEdges(i).Count(e => Counts(g.Edges[e].From)), Out: g.OutEdges(i).Count(e => Counts(g.Edges[e].To))))
            .OrderByDescending(h => h.In + h.Out)
            .ThenBy(h => g.Nodes[h.Node].Key, TextOrder.Default)
            .Take(top)];
    }

    /// <summary>The kept ghosts with the lines (0-based) of the kept notes that link to each, by how many notes that is,
    /// then by key.</summary>
    public static IReadOnlyList<(int Ghost, IReadOnlyList<(VaultPath Source, int Line)> From)> Unresolved(LinkGraph g, GraphFilter f)
    {
        var kept = Kept(g, f);
        return [.. Enumerable.Range(0, g.Nodes.Count)
            .Where(i => kept[i] && g.Nodes[i].Kind == NodeKind.Ghost)
            .Select(i => (Ghost: i, Edges: g.InEdges(i).Select(e => g.Edges[e]).Where(e => kept[e.From]).ToList()))
            .OrderByDescending(u => u.Edges.Count)
            .ThenBy(u => g.Nodes[u.Ghost].Key, TextOrder.Default)
            .Select(u => (u.Ghost, (IReadOnlyList<(VaultPath, int)>)[.. u.Edges.SelectMany(e => e.Lines.Select(l => (g.Nodes[e.From].Path!, l)))]))];
    }

    /// <summary>
    /// What a publication of <paramref name="note"/> takes: the notes reached by outgoing links to <paramref name="depth"/>,
    /// entering only notes <paramref name="admit"/> accepts (a refused note is not walked through either), the
    /// attachments those notes link to or embed, and the edges between them. A start the predicate refuses gives
    /// nothing. The graph is one vault's, so nothing crosses a vault.
    /// </summary>
    public static GraphSelection Bundle(LinkGraph g, VaultPath note, int depth, Func<VaultPath, bool> admit)
    {
        if (g.IndexOf(note) is not { } start || !IsNote(g, start) || !admit(note)) return new GraphSelection([], []);

        var selected = new HashSet<int> { start };
        var level = new List<int> { start };
        for (var d = 1; d <= depth && level.Count > 0; d++)
        {
            var next = new List<int>();
            foreach (var node in level)
            {
                foreach (var e in g.OutEdges(node))
                {
                    var to = g.Edges[e].To;
                    if (IsNote(g, to) && admit(g.Nodes[to].Path!) && selected.Add(to)) next.Add(to);
                }
            }

            level = next;
        }

        var notes = selected.ToList();
        foreach (var node in notes)
        {
            foreach (var e in g.OutEdges(node))
            {
                if (g.Nodes[g.Edges[e].To].Kind == NodeKind.Attachment) selected.Add(g.Edges[e].To);
            }
        }

        var edges = Enumerable.Range(0, g.Edges.Count).Where(e => selected.Contains(g.Edges[e].From) && selected.Contains(g.Edges[e].To));
        return new GraphSelection([.. selected.Order()], [.. edges]);
    }

    private static bool IsNote(LinkGraph g, int node) => g.Nodes[node].Kind is NodeKind.Note or NodeKind.Day;

    /// <summary>A node's degree, its times and the start of its text; the times and text are null without the note.</summary>
    public static NodeDetail Node(LinkGraph g, int node, StoredNote? note)
    {
        if (note is null) return new NodeDetail(g.Nodes[node], g.InEdges(node).Count, g.OutEdges(node).Count, null, null, false, null);
        var (created, atLeast) = NoteCreation.Of(note);
        return new NodeDetail(g.Nodes[node], g.InEdges(node).Count, g.OutEdges(node).Count,
            note.LastWriteAt, created, atLeast, FirstParagraph(note.Content));
    }

    private static IEnumerable<int> Neighbours(LinkGraph g, int node, GraphDirection dir)
    {
        var seen = new HashSet<int>();
        if (dir is GraphDirection.Out or GraphDirection.Both)
        {
            foreach (var e in g.OutEdges(node)) if (g.Edges[e].To != node && seen.Add(g.Edges[e].To)) yield return g.Edges[e].To;
        }

        if (dir is GraphDirection.In or GraphDirection.Both)
        {
            foreach (var e in g.InEdges(node)) if (g.Edges[e].From != node && seen.Add(g.Edges[e].From)) yield return g.Edges[e].From;
        }
    }

    private static bool[] Kept(LinkGraph g, GraphFilter f)
    {
        var asked = Normalised(f.Tags);
        var without = Normalised(f.WithoutTags);
        var kept = new bool[g.Nodes.Count];
        for (var i = 0; i < kept.Length; i++)
        {
            var node = g.Nodes[i];
            kept[i] = node.Kind switch
            {
                NodeKind.Note or NodeKind.Day =>
                    (node.Kind == NodeKind.Note || f.Days)
                    && (f.Archive || !node.Archived)
                    && (f.Paths.Count == 0 || f.Paths.Any(p => Below(node.Path!, p)))
                    && asked.All(a => node.Tags.Any(t => TagMatch.Covers(a, t)))
                    && !Carries(node, without),
                _ => false,
            };
        }

        // Attachments and ghosts follow the notes that link to them.
        for (var i = 0; i < kept.Length; i++)
        {
            var node = g.Nodes[i];
            if (node.Kind is not (NodeKind.Attachment or NodeKind.Ghost)) continue;
            kept[i] = (node.Kind == NodeKind.Attachment ? f.Attachments : f.Ghosts)
                && (f.Archive || !node.Archived)
                && g.InEdges(i).Any(e => kept[g.Edges[e].From]);
        }

        return kept;
    }

    private static List<string> Normalised(IReadOnlyList<string> tags) => [.. tags.Select(t => TagMatch.Normalise(t) ?? t)];

    /// <summary>A note or day note carrying any of <paramref name="tags"/>, nested ones included.</summary>
    private static bool Carries(GraphNode node, IReadOnlyList<string> tags) =>
        node.Kind is NodeKind.Note or NodeKind.Day && tags.Any(a => node.Tags.Any(t => TagMatch.Covers(a, t)));

    private static bool Below(VaultPath path, VaultPath folder) =>
        folder.Segments.Count <= path.Segments.Count
        && folder.Segments.Zip(path.Segments).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));

    // The first block after the front matter that is not a heading, in one line, cut on a word boundary.
    private static string? FirstParagraph(string content)
    {
        var lines = Lines.Split(content);
        var i = Outline.FrontmatterEnd(lines);
        while (i < lines.Count && (string.IsNullOrWhiteSpace(lines[i]) || Outline.HeadingRegex().IsMatch(lines[i]))) i++;

        var block = new List<string>();
        for (; i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]); i++) block.Add(lines[i].Trim());
        if (block.Count == 0) return null;

        var text = string.Join(' ', block);
        if (text.Length <= ParagraphLength) return text;
        var space = text.LastIndexOf(' ', ParagraphLength);
        return text[..(space > 0 ? space : ParagraphLength)].TrimEnd() + "…";
    }
}
