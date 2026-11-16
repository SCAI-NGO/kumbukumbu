// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Graph;

/// <summary>A note's or attachment's backlinks as the graph gives them: per line of each in-edge's source, exact when one
/// of its links leads to the target, maybe when one is ambiguous with the target among the candidates, as
/// <see cref="NoteLinks.ToAsync"/> lists them. Task 5 does the same in production.</summary>
public static class GraphBacklinks
{
    public static IReadOnlyList<Backlink> To(LinkGraph graph, Func<VaultPath, string> content, VaultPath target)
    {
        if (graph.IndexOf(target) is not { } node) return [];
        var exact = new List<Backlink>();
        var maybe = new List<Backlink>();
        foreach (var edge in graph.InEdges(node))
        {
            var source = graph.Nodes[graph.Edges[edge].From].Path!;
            var lines = Lines.Split(content(source));
            var links = graph.LinksOf(source).Where(l => l.Link.Target.Trim().Length > 0).ToList();
            exact.AddRange(links.Where(l => l.Resolution.Target == target).Select(l => l.Link.Line).Distinct()
                .Select(line => new Backlink(source, line, lines[line], Ambiguous: false)));
            maybe.AddRange(links
                .Where(l => l.Resolution.Kind == LinkKind.Ambiguous && l.Resolution.Candidates.Contains(target))
                .Select(l => l.Link.Line).Distinct()
                .Select(line => new Backlink(source, line, lines[line], Ambiguous: true)));
        }

        return [.. exact, .. maybe];
    }
}
