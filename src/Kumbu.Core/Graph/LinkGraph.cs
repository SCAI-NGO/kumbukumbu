// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Calendar;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Graph;

public enum NodeKind { Note, Day, Attachment, Ghost }

[Flags]
public enum EdgeFlags { None = 0, Embed = 1, Alias = 2, Ambiguous = 4 }

/// <param name="Key">The path's <see cref="VaultPath.Value"/>; for a ghost "?" and its <see cref="GhostKey"/>.</param>
/// <param name="Title">A note's name without ".md", an attachment's with its extension, a ghost's target as the first
/// link in path order wrote it.</param>
public sealed record GraphNode(string Key, VaultPath? Path, string Title, NodeKind Kind,
    IReadOnlyList<string> Tags, CalendarTime? When, CalendarTime? Until, bool Archived);

/// <summary>Every link from one note to one node; <paramref name="Lines"/> 0-based, distinct and ascending, and
/// possibly the same list as another edge's of the note.</summary>
public sealed record GraphEdge(int From, int To, EdgeFlags Flags, IReadOnlyList<int> Lines);

/// <summary>
/// The resolved links of one vault (spec §4.3), immutable. Nodes are the notes and the attachments a note links to, by
/// path order, then the ghosts by key; edges are ordered by source, then target, so a graph built from the same
/// extracts is the same whatever order they came in.
/// </summary>
public sealed class LinkGraph
{
    private readonly Dictionary<VaultPath, int> _byPath;
    private readonly Dictionary<VaultPath, IReadOnlyList<ResolvedLink>> _links;
    private readonly int[][] _out;
    private readonly int[][] _in;

    private LinkGraph(
        string vault, GraphNode[] nodes, GraphEdge[] edges, Dictionary<VaultPath, IReadOnlyList<ResolvedLink>> links,
        IReadOnlyDictionary<VaultPath, LinkExtract> extracts, IReadOnlyCollection<VaultPath> attachments)
    {
        Vault = vault;
        Nodes = nodes;
        Edges = edges;
        Extracts = extracts;
        Attachments = attachments;
        _links = links;
        _byPath = [];
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Path is { } path) _byPath[path] = i;
        }

        var outs = nodes.Select(_ => new List<int>()).ToArray();
        var ins = nodes.Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < edges.Length; i++)
        {
            outs[edges[i].From].Add(i);
            ins[edges[i].To].Add(i);
        }

        _out = [.. outs.Select(l => l.ToArray())];
        _in = [.. ins.Select(l => l.ToArray())];
    }

    private LinkGraph(LinkGraph graph, string? version)
    {
        Vault = graph.Vault;
        Nodes = graph.Nodes;
        Edges = graph.Edges;
        Extracts = graph.Extracts;
        Attachments = graph.Attachments;
        _links = graph._links;
        _byPath = graph._byPath;
        _out = graph._out;
        _in = graph._in;
        Version = version;
    }

    public string Vault { get; }

    public IReadOnlyList<GraphNode> Nodes { get; }

    public IReadOnlyList<GraphEdge> Edges { get; }

    public IReadOnlyDictionary<VaultPath, LinkExtract> Extracts { get; }

    public IReadOnlyCollection<VaultPath> Attachments { get; }

    /// <summary>The ETag a store sets; null elsewhere.</summary>
    public string? Version { get; init; }

    /// <summary>The node of a note or attachment; null for an attachment no note links to.</summary>
    public int? IndexOf(VaultPath path) => _byPath.TryGetValue(path, out var index) ? index : null;

    /// <summary>The node's edges (indices into <see cref="Edges"/>), by target path order, ghosts last.</summary>
    public IReadOnlyList<int> OutEdges(int node) => _out[node];

    /// <summary>The edges into the node (indices into <see cref="Edges"/>), by source path order.</summary>
    public IReadOnlyList<int> InEdges(int node) => _in[node];

    /// <summary>Every link of the note with where it leads, in source order, as <see cref="NoteLinks.OfAsync"/> gives
    /// them; empty for a path that is no note of the graph.</summary>
    public IReadOnlyList<ResolvedLink> LinksOf(VaultPath note) => _links.GetValueOrDefault(note) ?? [];

    /// <summary>The graph of <paramref name="notes"/>: each distinct link of a note (its target text and kind) is resolved
    /// once, and its lines go to every note it reaches together.</summary>
    public static LinkGraph Build(
        string vault, IReadOnlyDictionary<VaultPath, LinkExtract> notes, IReadOnlyCollection<VaultPath> attachments,
        CancellationToken ct = default)
    {
        // One order for everything that follows: the resolver's tie-breaks, the ghosts' titles, the nodes and edges.
        var sources = notes.Keys.OrderBy(p => p.Value, TextOrder.Default).ToList();
        var resolver = new LinkResolver(vault, sources, attachments,
            notes.Where(n => n.Value.Aliases.Count > 0).ToDictionary(n => n.Key, n => n.Value.Aliases));

        var links = new Dictionary<VaultPath, IReadOnlyList<ResolvedLink>>();
        var linked = new HashSet<VaultPath>();
        var ghosts = new Dictionary<string, string>(StringComparer.Ordinal);
        var read = 0;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            // Resolve reads only the target, as SameResolution keys it, and whether the link is a wikilink, so the links
            // of a note that it reads alike share one resolution. [Markdown, wikilink] for text that ignores case, and again
            // for text whose case counts.
            Dictionary<string, LinkResolution>[] ignoringCase = [new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase)];
            Dictionary<string, LinkResolution>[] withCase = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
            var resolved = notes[source].Links.Select(l =>
            {
                if (++read % 256 == 0) ct.ThrowIfCancellationRequested();
                var (text, caseCounts) = LinkResolver.SameResolution(l.Target);
                var cache = (caseCounts ? withCase : ignoringCase)[l is WikiLink ? 1 : 0];
                if (!cache.TryGetValue(text, out var resolution)) cache[text] = resolution = resolver.Resolve(source, l);
                return new ResolvedLink(l, resolution);
            }).ToList();
            links[source] = resolved;
            var counted = new HashSet<LinkResolution>(ReferenceEqualityComparer.Instance);
            foreach (var (link, resolution) in Edged(resolved))
            {
                if (Ghost(resolution))
                {
                    ghosts.TryAdd(GhostKeyOf(source, link.Target), link.Target.Trim().Normalize(NormalizationForm.FormC));
                    continue;
                }

                if (!counted.Add(resolution)) continue;
                foreach (var target in Targets(resolution))
                {
                    if (!notes.ContainsKey(target)) linked.Add(target);
                }
            }
        }

        var nodes = sources.Select(p => NoteNode(p, notes[p]))
            .Concat(linked.Select(p => new GraphNode(p.Value, p, p.Name, NodeKind.Attachment, [], null, null, ArchiveFolder.Contains(p))))
            .OrderBy(n => n.Key, TextOrder.Default)
            .Concat(ghosts.OrderBy(g => g.Key, TextOrder.Default)
                .Select(g => new GraphNode("?" + g.Key, null, g.Value, NodeKind.Ghost, [], null, null, false)))
            .ToArray();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nodes.Length; i++) index[nodes[i].Key] = i;

        var edges = new List<GraphEdge>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            var from = index[source.Value];
            // The links that share a resolution reach the same notes: their lines are gathered once, and an ambiguous
            // link's candidates share that one list. A ghost is the link's own, by its key.
            var groups = new Dictionary<LinkResolution, (EdgeFlags Flags, List<int> Lines)>(ReferenceEqualityComparer.Instance);
            var ghostGroups = new Dictionary<string, (EdgeFlags Flags, List<int> Lines)>(StringComparer.Ordinal);
            foreach (var (link, resolution) in Edged(links[source]))
            {
                if (++read % 256 == 0) ct.ThrowIfCancellationRequested();
                var flags = (link is WikiLink { Embed: true } or MarkdownLink { Embed: true } ? EdgeFlags.Embed : 0)
                    | (resolution.ViaAlias ? EdgeFlags.Alias : 0)
                    | (resolution.Kind == LinkKind.Ambiguous ? EdgeFlags.Ambiguous : 0);
                if (Ghost(resolution)) Gather(ghostGroups, "?" + GhostKeyOf(source, link.Target), flags, link.Line);
                else Gather(groups, resolution, flags, link.Line);
            }

            var byTarget = new Dictionary<int, (EdgeFlags Flags, List<int[]> Lines)>();
            void Reach(IEnumerable<int> targets, (EdgeFlags Flags, List<int> Lines) group)
            {
                int[] lines = [.. group.Lines.Order().Distinct()];
                foreach (var to in targets)
                {
                    if (!byTarget.TryGetValue(to, out var edge)) edge = (EdgeFlags.None, []);
                    edge.Lines.Add(lines);
                    byTarget[to] = (edge.Flags | group.Flags, edge.Lines);
                }
            }

            foreach (var (resolution, group) in groups) Reach(Targets(resolution).Select(t => index[t.Value]), group);
            foreach (var (ghost, group) in ghostGroups) Reach([index[ghost]], group);

            foreach (var to in byTarget.Keys.Order())
            {
                var (flags, lines) = byTarget[to];
                edges.Add(new GraphEdge(from, to, flags, lines.Count == 1 ? lines[0] : [.. lines.SelectMany(l => l).Order().Distinct()]));
            }
        }

        return new LinkGraph(vault, nodes, [.. edges], links, notes, attachments);
    }

    private static void Gather<TKey>(Dictionary<TKey, (EdgeFlags Flags, List<int> Lines)> groups, TKey key, EdgeFlags flags, int line)
        where TKey : notnull
    {
        if (!groups.TryGetValue(key, out var group)) group = (EdgeFlags.None, []);
        group.Lines.Add(line);
        groups[key] = (group.Flags | flags, group.Lines);
    }

    // Links to a heading of the note itself name no target and make no edge, as backlinks skip them today.
    private static IEnumerable<ResolvedLink> Edged(IEnumerable<ResolvedLink> links) =>
        links.Where(l => l.Link.Target.Trim().Length > 0 && l.Resolution.Kind != LinkKind.Self);

    /// <summary>A "./" or "../" target names a path relative to the source's folder, so its ghost is that path from the
    /// vault's root, as the resolver reads it; one that leaves the vault keeps its text.</summary>
    private static string GhostKeyOf(VaultPath source, string target)
    {
        var text = target.Trim();
        if ((text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith("../", StringComparison.Ordinal))
            && VaultPath.TryParse($"/{string.Join('/', source.Segments.SkipLast(1))}/{text}", out var relative)
            && relative.Vault == source.Vault && relative.Segments.Count >= 2)
        {
            text = "/" + string.Join('/', relative.Segments.Skip(1));
        }

        return GhostKey.Of(text);
    }

    private static bool Ghost(LinkResolution resolution) =>
        resolution.Kind == LinkKind.NotFound || resolution is { Kind: LinkKind.Attachment, Target: null };

    private static IEnumerable<VaultPath> Targets(LinkResolution resolution) =>
        resolution.Kind == LinkKind.Ambiguous ? resolution.Candidates : [resolution.Target!];

    private static GraphNode NoteNode(VaultPath path, LinkExtract extract) => new(
        path.Value, path, path.Name.EndsWith(".md", StringComparison.Ordinal) ? path.Name[..^".md".Length] : path.Name,
        EntryReader.IsDayOrWeekName(path.Name, out _) ? NodeKind.Day : NodeKind.Note,
        extract.Tags, extract.When, extract.Until, ArchiveFolder.Contains(path));

    /// <summary>Whether both hold the same nodes, edges, flags, lines and attributes; the version is not compared.</summary>
    public bool SameAs(LinkGraph other) =>
        Vault == other.Vault
        && Nodes.Count == other.Nodes.Count
        && Nodes.Zip(other.Nodes).All(p => p.First with { Tags = p.Second.Tags } == p.Second && p.First.Tags.SequenceEqual(p.Second.Tags))
        && Edges.Count == other.Edges.Count
        && Edges.Zip(other.Edges).All(p => p.First with { Lines = p.Second.Lines } == p.Second
            && (ReferenceEquals(p.First.Lines, p.Second.Lines) || p.First.Lines.SequenceEqual(p.Second.Lines)));

    /// <summary>The same graph under another version; it shares everything else.</summary>
    public LinkGraph WithVersion(string? version) => new(this, version);

    /// <summary>The graph with every path through <paramref name="map"/>, for a renamed vault, its version kept. Built
    /// anew: resolution reads paths within the vault only, so a rename leads every link where it led before.</summary>
    public LinkGraph Rebased(Func<VaultPath, VaultPath> map) => Build(
        map(VaultPath.Root.Child(Vault)).Segments[0],
        Extracts.ToDictionary(e => map(e.Key), e => e.Value),
        [.. Attachments.Select(map)]).WithVersion(Version);
}
