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

/// <summary>Every link from one note to one node; <paramref name="Lines"/> 0-based, distinct and ascending.</summary>
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

    public static LinkGraph Build(string vault, IReadOnlyDictionary<VaultPath, LinkExtract> notes, IReadOnlyCollection<VaultPath> attachments)
    {
        // One order for everything that follows: the resolver's tie-breaks, the ghosts' titles, the nodes and edges.
        var sources = notes.Keys.OrderBy(p => p.Value, TextOrder.Default).ToList();
        var resolver = new LinkResolver(vault, sources, attachments,
            notes.Where(n => n.Value.Aliases.Count > 0).ToDictionary(n => n.Key, n => n.Value.Aliases));

        var links = new Dictionary<VaultPath, IReadOnlyList<ResolvedLink>>();
        var linked = new HashSet<VaultPath>();
        var ghosts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var resolved = notes[source].Links.Select(l => new ResolvedLink(l, resolver.Resolve(source, l))).ToList();
            links[source] = resolved;
            foreach (var (link, resolution) in Edged(resolved))
            {
                if (Ghost(resolution))
                {
                    ghosts.TryAdd(GhostKey.Of(link.Target), link.Target.Trim().Normalize(NormalizationForm.FormC));
                    continue;
                }

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
            var from = index[source.Value];
            var byTarget = new SortedDictionary<int, (EdgeFlags Flags, SortedSet<int> Lines)>();
            foreach (var (link, resolution) in Edged(links[source]))
            {
                var flags = (link is WikiLink { Embed: true } or MarkdownLink { Embed: true } ? EdgeFlags.Embed : 0)
                    | (resolution.ViaAlias ? EdgeFlags.Alias : 0)
                    | (resolution.Kind == LinkKind.Ambiguous ? EdgeFlags.Ambiguous : 0);
                IEnumerable<int> targets = Ghost(resolution)
                    ? [index["?" + GhostKey.Of(link.Target)]]
                    : Targets(resolution).Select(t => index[t.Value]);
                foreach (var to in targets)
                {
                    if (!byTarget.TryGetValue(to, out var edge)) byTarget[to] = edge = (EdgeFlags.None, []);
                    edge.Lines.Add(link.Line);
                    byTarget[to] = (edge.Flags | flags, edge.Lines);
                }
            }

            edges.AddRange(byTarget.Select(e => new GraphEdge(from, e.Key, e.Value.Flags, [.. e.Value.Lines])));
        }

        return new LinkGraph(vault, nodes, [.. edges], links, notes, attachments);
    }

    // Links to a heading of the note itself name no target and make no edge, as backlinks skip them today.
    private static IEnumerable<ResolvedLink> Edged(IEnumerable<ResolvedLink> links) =>
        links.Where(l => l.Link.Target.Trim().Length > 0 && l.Resolution.Kind != LinkKind.Self);

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
        && Edges.Zip(other.Edges).All(p => p.First with { Lines = p.Second.Lines } == p.Second && p.First.Lines.SequenceEqual(p.Second.Lines));

    /// <summary>The graph with every path through <paramref name="map"/>, for a renamed vault. Built anew: resolution
    /// reads paths within the vault only, so a rename leads every link where it led before.</summary>
    public LinkGraph Rebased(Func<VaultPath, VaultPath> map) => Build(
        map(VaultPath.Root.Child(Vault)).Segments[0],
        Extracts.ToDictionary(e => map(e.Key), e => e.Value),
        [.. Attachments.Select(map)]);
}
