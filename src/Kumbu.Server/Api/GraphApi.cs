// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Graph;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>A node of the graph. <c>path</c> is null for a link that leads to no note (<c>kind</c> <c>ghost</c>, <c>title</c>
/// what the link wrote); <c>kind</c> is <c>note</c>, <c>day</c>, <c>attachment</c> or <c>ghost</c>; <c>when</c> and <c>until</c> as
/// the note wrote them. <c>in</c> and <c>out</c>: its links in the whole vault, whatever the filter hides. <c>depth</c>: in a
/// local graph, the links from the note asked for.</summary>
public sealed record GraphNodeView(
    string? Path, string Title, string Kind, IReadOnlyList<string> Tags,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? When,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Until,
    bool Archived, int In, int Out,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Depth = null);

/// <summary>The global graph: <c>edges</c> as <c>[from, to, flags]</c>, indices into <c>nodes</c>, <c>flags</c> the sum of
/// embed (1), alias (2) and ambiguous (4).</summary>
public sealed record GraphView(IReadOnlyList<GraphNodeView> Nodes, IReadOnlyList<int[]> Edges);

/// <summary>A local graph: <c>leftOut</c> maps a node's index to how many of its neighbours the cap dropped.</summary>
public sealed record LocalGraphView(IReadOnlyList<GraphNodeView> Nodes, IReadOnlyList<int[]> Edges, IReadOnlyDictionary<string, int> LeftOut);

/// <summary>One node with the times of its note (absent for an attachment or a link to nothing). <c>createdAtLeast</c>: the
/// creation time is the earliest the history shows.</summary>
public sealed record GraphNodeDetailView(
    GraphNodeView Node, DateTime? Changed, DateTime? Created, bool CreatedAtLeast, string? FirstParagraph);

/// <summary>The shortest paths between two notes. <c>nodes</c> and <c>edges</c> hold what the paths walk; each of <c>paths</c> lists
/// edge indices in the order walked from the first note (an edge may be walked against its direction). <c>why</c> when there is
/// none: <c>none</c> (there are paths), <c>not_connected</c> or <c>farther_than_limit</c>; <c>more</c>: further paths of the same length exist.</summary>
public sealed record GraphPathsView(
    IReadOnlyList<GraphNodeView> Nodes, IReadOnlyList<int[]> Edges, IReadOnlyList<int[]> Paths, bool More, string Why);

/// <summary>The graph of links between notes (graph backend spec §7.2), over the vaults the caller has: a hidden discreet vault
/// answers 404 like an unknown one. The global and the local graph carry an <c>ETag</c> of the vaults' names and their graphs'
/// versions (several vaults: in the order asked), which changes with a link or a rename and not with prose.</summary>
public static class GraphApi
{
    /// <summary>How many <c>path</c> and how many <c>tag</c> a filter takes at most: a person picks a handful, and each costs a
    /// pass over the notes, so a query that repeats them by the thousand is refused before the walk.</summary>
    public const int MaxFilters = 50;

    public static void MapGraphApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/graph").AddEndpointFilter(ApiErrors.Filter);
        api.MapGet("", GlobalAsync).Errors(400, 401, 404, 503);
        api.MapGet("/local", LocalAsync).Errors(400, 401, 404, 503);
        api.MapGet("/node", NodeAsync).Errors(400, 401, 404, 503);
        api.MapGet("/paths", PathsAsync).Errors(400, 401, 404, 503);
    }

    /// <summary><c>vault</c>, repeated: the vaults to draw (default: all the caller has). <c>path</c> (repeated, folders or notes) and <c>tag</c>
    /// (repeated, all of them; at most <see cref="MaxFilters"/> of each) select notes; <c>days</c>, <c>attachments</c> and <c>ghosts</c> (default true) and <c>archive</c> (default false).</summary>
    private static async Task<Results<Ok<GraphView>, StatusCodeHttpResult>> GlobalAsync(
        string[]? vault, string[]? path, string[]? tag, bool? days, bool? attachments, bool? ghosts, bool? archive,
        HttpContext http, INoteStore store, CancellationToken ct)
    {
        var have = await store.ListVaultsAsync(ct);
        // Names are normalised (NFC) before they are compared: two spellings of one vault are one vault.
        var names = vault is { Length: > 0 }
            ? vault.Select(v => ApiPaths.Parse(v, null, note: false).Segments[0]).Distinct(StringComparer.Ordinal).ToList()
            : [.. have.Order(TextOrder.Default)];
        if (names.Count > have.Count) throw ApiErrors.BadRequest($"You have {have.Count} vaults; name each at most once.");
        foreach (var name in names)
        {
            await NoteApi.RequireVaultAsync(store, VaultPath.Root.Child(name), ct);
        }

        var filter = Filter(path, tag, days, attachments, ghosts, archive, scope: null);
        var graphs = new List<LinkGraph>();
        foreach (var name in names) graphs.Add(await store.GraphAsync(VaultPath.Root.Child(name), ct));

        var etag = ETag(graphs);
        http.Response.Headers.ETag = etag;
        if (EntityTags.NoneMatchNames(http.Request, new(etag))) return TypedResults.StatusCode(StatusCodes.Status304NotModified);

        var nodes = new List<GraphNodeView>();
        var edges = new List<int[]>();
        foreach (var g in graphs)
        {
            var selection = GraphQueries.Global(g, filter);
            var shown = new Dictionary<int, int>();
            foreach (var n in selection.Nodes)
            {
                shown[n] = nodes.Count;
                nodes.Add(View(g, n));
            }

            foreach (var e in selection.Edges) edges.Add(Edge(g.Edges[e], shown));
        }

        return TypedResults.Ok(new GraphView(nodes, edges));
    }

    /// <summary><c>path</c>: the note or attachment in the middle; <c>depth</c> 1 to 3 (default 1), <c>dir</c> <c>out</c>, <c>in</c> or
    /// <c>both</c> (default), <c>cap</c> 1 to 100 (default 20); the filter as for the global graph.</summary>
    private static async Task<Results<Ok<LocalGraphView>, StatusCodeHttpResult>> LocalAsync(
        string? path, int? depth, string? dir, int? cap, string[]? tag, bool? days, bool? attachments, bool? ghosts, bool? archive,
        HttpContext http, INoteStore store, CancellationToken ct)
    {
        var note = Target(path);
        var d = depth ?? 1;
        var c = cap ?? 20;
        if (d is < 1 or > 3) throw ApiErrors.BadRequest("depth is 1 to 3.");
        if (c is < 1 or > 100) throw ApiErrors.BadRequest("cap is 1 to 100.");
        var direction = Direction(dir);
        // No path filter: `path` names the note, and the walk is its neighbourhood anyway.
        var filter = Filter(null, tag, days, attachments, ghosts, archive, scope: note);

        var g = await GraphOfAsync(store, note, ct);
        if (g.IndexOf(note) is null) throw await NotInGraphAsync(store, note, ct);
        var etag = ETag([g]);
        http.Response.Headers.ETag = etag;
        if (EntityTags.NoneMatchNames(http.Request, new(etag))) return TypedResults.StatusCode(StatusCodes.Status304NotModified);

        var local = GraphQueries.Local(g, note, d, direction, c, filter);
        var shown = new Dictionary<int, int>();
        var nodes = new List<GraphNodeView>();
        foreach (var (node, level) in local.Nodes)
        {
            shown[node] = nodes.Count;
            nodes.Add(View(g, node, level));
        }

        return TypedResults.Ok(new LocalGraphView(
            nodes, [.. local.Edges.Select(e => Edge(g.Edges[e], shown))],
            local.LeftOut.ToDictionary(l => shown[l.Key].ToString(System.Globalization.CultureInfo.InvariantCulture), l => l.Value)));
    }

    private static async Task<Ok<GraphNodeDetailView>> NodeAsync(string? path, INoteStore store, CancellationToken ct)
    {
        var target = Target(path);
        var g = await GraphOfAsync(store, target, ct);
        if (g.IndexOf(target) is not { } index) throw await NotInGraphAsync(store, target, ct);

        var note = target.IsNote ? await store.GetAsync(target, ct) : null;
        var detail = GraphQueries.Node(g, index, note);
        return TypedResults.Ok(new GraphNodeDetailView(View(g, index), detail.Changed, detail.Created, detail.CreatedAtLeast, detail.FirstParagraph));
    }

    /// <summary><c>from</c> and <c>to</c>: two notes of one vault; <c>dir</c> <c>out</c> follows links as written, <c>in</c> against them,
    /// <c>both</c> (default) either way; the filter as for the local graph, which the walk keeps to (the two notes always count).</summary>
    private static async Task<Ok<GraphPathsView>> PathsAsync(
        string? from, string? to, string? dir, string[]? tag, bool? days, bool? attachments, bool? ghosts, bool? archive,
        INoteStore store, CancellationToken ct)
    {
        var start = Target(from, "from", PathKind.Note);
        var end = Target(to, "to", PathKind.Note);
        var direction = Direction(dir);
        var filter = Filter(null, tag, days, attachments, ghosts, archive, scope: start);
        if (start.Segments[0] != end.Segments[0]) throw ApiErrors.BadRequest("from and to are in different vaults, and links never cross vaults.");

        var g = await GraphOfAsync(store, start, ct);
        foreach (var note in new[] { start, end })
        {
            if (g.IndexOf(note) is { } i && g.Nodes[i].Kind is NodeKind.Note or NodeKind.Day) continue;
            throw await NotInGraphAsync(store, note, ct);
        }

        var result = GraphQueries.Paths(g, start, end, direction, filter);
        var shown = new Dictionary<int, int>();
        var nodes = new List<GraphNodeView>();
        int Show(int node)
        {
            if (!shown.TryGetValue(node, out var at))
            {
                at = shown[node] = nodes.Count;
                nodes.Add(View(g, node));
            }

            return at;
        }

        Show(g.IndexOf(start)!.Value);
        var edgeAt = new Dictionary<int, int>();
        var edges = new List<int[]>();
        var paths = new List<int[]>();
        foreach (var walked in result.Paths)
        {
            var steps = new List<int>();
            foreach (var e in walked)
            {
                if (!edgeAt.TryGetValue(e, out var at))
                {
                    var edge = g.Edges[e];
                    Show(edge.From);
                    Show(edge.To);
                    at = edgeAt[e] = edges.Count;
                    edges.Add(Edge(edge, shown));
                }

                steps.Add(at);
            }

            paths.Add([.. steps]);
        }

        return TypedResults.Ok(new GraphPathsView(nodes, edges, paths, result.More, result.Why switch
        {
            Disconnection.FartherThanLimit => "farther_than_limit",
            Disconnection.NotConnected => "not_connected",
            _ => "none",
        }));
    }

    // A rename keeps the graph and its version but changes every path in it, so the caller's names are part of the tag.
    private static string ETag(IReadOnlyList<LinkGraph> graphs) =>
        $"\"{VersionToken.Of(string.Join('/', graphs.Select(g => g.Vault)))}.{string.Join(',', graphs.Select(g => g.Version))}\"";

    private static int[] Edge(GraphEdge edge, Dictionary<int, int> shown) => [shown[edge.From], shown[edge.To], (int)edge.Flags];

    private static GraphNodeView View(LinkGraph g, int index, int? depth = null)
    {
        var node = g.Nodes[index];
        return new GraphNodeView(
            node.Path?.Value, node.Title, node.Kind.ToString().ToLowerInvariant(), node.Tags, node.When?.Write(), node.Until?.Write(),
            node.Archived, g.InEdges(index).Count, g.OutEdges(index).Count, depth);
    }

    private static GraphDirection Direction(string? dir) => dir switch
    {
        null or "both" => GraphDirection.Both,
        "out" => GraphDirection.Out,
        "in" => GraphDirection.In,
        _ => throw ApiErrors.BadRequest("dir is out, in or both."),
    };

    /// <summary>A path of a query, written from the root (<c>/Work/plan.md</c>) and parsed as every path of the API is.</summary>
    private static VaultPath Parse(string? raw, string name, PathKind kind)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith('/')) throw ApiErrors.BadRequest($"{name} is a path from the root, e.g. /Work/plan.md.");
        var parts = raw[1..].Split('/', 2);
        return ApiPaths.Parse(parts[0], parts.Length > 1 ? parts[1] : null, kind);
    }

    private static VaultPath Target(string? raw, string name = "path", PathKind kind = PathKind.NoteOrAttachment)
    {
        var path = Parse(raw, name, kind);
        if (path.Segments.Count < 2) throw ApiErrors.BadRequest($"{name} is a note or file, not a vault.");
        return path;
    }

    private static GraphFilter Filter(string[]? paths, string[]? tags, bool? days, bool? attachments, bool? ghosts, bool? archive, VaultPath? scope)
    {
        if (paths?.Length > MaxFilters) throw ApiErrors.BadRequest($"The filter takes at most {MaxFilters} paths; this named {paths.Length}.");
        if (tags?.Length > MaxFilters) throw ApiErrors.BadRequest($"The filter takes at most {MaxFilters} tags; this named {tags.Length}.");
        var under = (paths ?? []).Select(p => Parse(p, "path", PathKind.Folder)).ToList();
        var inArchive = under.Any(ArchiveFolder.Contains) || (scope is not null && ArchiveFolder.Contains(scope));
        return new GraphFilter(under, tags ?? [], days ?? true, attachments ?? true, ghosts ?? true, archive == true || inArchive);
    }

    private static async Task<LinkGraph> GraphOfAsync(INoteStore store, VaultPath path, CancellationToken ct)
    {
        var vault = VaultPath.Root.Child(path.Segments[0]);
        await NoteApi.RequireVaultAsync(store, vault, ct);
        return await store.GraphAsync(vault, ct);
    }

    // An attachment no note links to is no node; everything else missing is the usual 404.
    private static async Task<ApiException> NotInGraphAsync(INoteStore store, VaultPath path, CancellationToken ct)
    {
        var exists = path.IsAttachment ? await StoreAttachments.GetAsync(store, path, ct) is not null : await store.GetAsync(path, ct) is not null;
        return exists ? ApiErrors.NotFound($"{path.Value} is not in the link graph: no note links to it.") : await NoteApi.MissingAsync(store, path, ct);
    }
}
