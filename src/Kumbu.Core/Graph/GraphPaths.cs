// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Graph;

/// <summary>Why <see cref="GraphQueries.Paths"/> found nothing: none to find, the two never meet, or not within the limit.</summary>
public enum Disconnection { None, NotConnected, FartherThanLimit }

/// <param name="Paths">Each path as the edge indices walked from the start; an edge may be walked against its direction.</param>
/// <param name="More">Whether further paths of the same length exist.</param>
public sealed record PathsResult(IReadOnlyList<IReadOnlyList<int>> Paths, bool More, Disconnection Why);

public static partial class GraphQueries
{
    private const int MaxPaths = 5;
    private const int MaxLength = 6;

    /// <summary>
    /// The shortest paths between two notes, at most five, of at most six links, walking notes only: attachments and
    /// ghosts are never passed through. <paramref name="dir"/> <see cref="GraphDirection.Out"/> follows links as written.
    /// </summary>
    public static PathsResult Paths(LinkGraph g, VaultPath from, VaultPath to, GraphDirection dir)
    {
        if (g.IndexOf(from) is not { } start || g.IndexOf(to) is not { } end || !IsNote(g, start) || !IsNote(g, end))
        {
            return new PathsResult([], false, Disconnection.NotConnected);
        }

        if (start == end) return new PathsResult([[]], false, Disconnection.None);

        // Distances from both ends, so the walk below only enters nodes that lie on a shortest path.
        var fromStart = Distances(g, start, dir);
        if (!fromStart.TryGetValue(end, out var length)) return new PathsResult([], false, Disconnection.NotConnected);
        if (length > MaxLength) return new PathsResult([], false, Disconnection.FartherThanLimit);

        var toEnd = Distances(g, end, Flip(dir));
        var found = new List<IReadOnlyList<int>>();
        var walked = new List<int>();

        void Walk(int node)
        {
            if (found.Count > MaxPaths) return;
            if (node == end)
            {
                found.Add([.. walked]);
                return;
            }

            foreach (var (edge, next) in Steps(g, node, dir))
            {
                if (!fromStart.TryGetValue(next, out var a) || a != fromStart[node] + 1
                    || !toEnd.TryGetValue(next, out var b) || a + b != length) continue;
                walked.Add(edge);
                Walk(next);
                walked.RemoveAt(walked.Count - 1);
            }
        }

        Walk(start);
        return new PathsResult([.. found.Take(MaxPaths)], found.Count > MaxPaths, Disconnection.None);
    }

    private static GraphDirection Flip(GraphDirection dir) => dir switch
    {
        GraphDirection.Out => GraphDirection.In,
        GraphDirection.In => GraphDirection.Out,
        _ => GraphDirection.Both,
    };

    private static Dictionary<int, int> Distances(LinkGraph g, int origin, GraphDirection dir)
    {
        var distance = new Dictionary<int, int> { [origin] = 0 };
        var queue = new Queue<int>([origin]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var (_, next) in Steps(g, node, dir))
            {
                if (distance.TryAdd(next, distance[node] + 1)) queue.Enqueue(next);
            }
        }

        return distance;
    }

    // Each edge leading from the node to another note, with that note.
    private static IEnumerable<(int Edge, int Next)> Steps(LinkGraph g, int node, GraphDirection dir)
    {
        if (dir is GraphDirection.Out or GraphDirection.Both)
        {
            foreach (var e in g.OutEdges(node))
            {
                if (g.Edges[e].To != node && IsNote(g, g.Edges[e].To)) yield return (e, g.Edges[e].To);
            }
        }

        if (dir is GraphDirection.In or GraphDirection.Both)
        {
            foreach (var e in g.InEdges(node))
            {
                if (g.Edges[e].From != node && IsNote(g, g.Edges[e].From)) yield return (e, g.Edges[e].From);
            }
        }
    }
}
