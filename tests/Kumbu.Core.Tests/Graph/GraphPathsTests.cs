// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Graph;

public class GraphPathsTests
{
    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private static LinkGraph Build(params (string Path, string Content)[] notes) =>
        LinkGraph.Build("W", notes.ToDictionary(n => P(n.Path), n => LinkExtract.Of(P(n.Path), n.Content)), [P("/W/pic.png")]);

    private static string Walk(LinkGraph g, string from, IEnumerable<int> path)
    {
        var at = from;
        var text = new System.Text.StringBuilder(at);
        foreach (var e in path)
        {
            var edge = g.Edges[e];
            var (arrow, next) = g.Nodes[edge.From].Key == at ? (" -> ", edge.To) : (" <- ", edge.From);
            text.Append(arrow).Append(g.Nodes[next].Key);
            at = g.Nodes[next].Key;
        }

        return text.ToString();
    }

    [Fact]
    public void Direct_link_is_one_step()
    {
        var g = Build(("/W/A.md", "[[B]]"), ("/W/B.md", ""));

        var result = GraphQueries.Paths(g, P("/W/A.md"), P("/W/B.md"), GraphDirection.Out);

        Assert.Equal(["/W/A.md -> /W/B.md"], result.Paths.Select(p => Walk(g, "/W/A.md", p)));
        Assert.False(result.More);
        Assert.Equal(Disconnection.None, result.Why);
    }

    [Fact]
    public void Both_directions_by_default()
    {
        var g = Build(("/W/A.md", "[[C]]"), ("/W/B.md", "[[C]]"), ("/W/C.md", ""));

        var result = GraphQueries.Paths(g, P("/W/A.md"), P("/W/B.md"), GraphDirection.Both);

        Assert.Equal(["/W/A.md -> /W/C.md <- /W/B.md"], result.Paths.Select(p => Walk(g, "/W/A.md", p)));
    }

    [Fact]
    public void Out_follows_links_as_written()
    {
        var g = Build(("/W/A.md", "[[C]]"), ("/W/B.md", "[[C]]"), ("/W/C.md", ""));

        var result = GraphQueries.Paths(g, P("/W/A.md"), P("/W/B.md"), GraphDirection.Out);

        Assert.Empty(result.Paths);
        Assert.Equal(Disconnection.NotConnected, result.Why);
    }

    [Fact]
    public void At_most_five_paths_with_more()
    {
        var days = Enumerable.Range(1, 8).Select(i => $"/W/2026-11-{i:00}.md").ToArray();
        var g = Build([("/W/A.md", string.Concat(days.Select(d => $"[[{Path.GetFileNameWithoutExtension(d)}]]"))), ("/W/B.md", ""),
            .. days.Select(d => (d, "[[B]]"))]);

        var result = GraphQueries.Paths(g, P("/W/A.md"), P("/W/B.md"), GraphDirection.Both);

        Assert.Equal(5, result.Paths.Count);
        Assert.All(result.Paths, p => Assert.Equal(2, p.Count));
        Assert.Equal(5, result.Paths.Select(p => string.Join(',', p)).Distinct().Count());
        Assert.True(result.More);
    }

    [Fact]
    public void Exactly_five_paths_are_not_more()
    {
        var g = Build([("/W/A.md", "[[D1]] [[D2]] [[D3]] [[D4]] [[D5]]"), ("/W/B.md", ""),
            .. Enumerable.Range(1, 5).Select(i => ($"/W/D{i}.md", "[[B]]"))]);

        var result = GraphQueries.Paths(g, P("/W/A.md"), P("/W/B.md"), GraphDirection.Out);

        Assert.Equal(5, result.Paths.Count);
        Assert.False(result.More);
    }

    [Fact]
    public void Seven_steps_are_farther_than_the_limit()
    {
        var chain = Enumerable.Range(0, 8).Select(i => ($"/W/N{i}.md", i < 7 ? $"[[N{i + 1}]]" : "")).ToArray();
        var g = Build(chain);

        var seven = GraphQueries.Paths(g, P("/W/N0.md"), P("/W/N7.md"), GraphDirection.Both);
        var six = GraphQueries.Paths(g, P("/W/N0.md"), P("/W/N6.md"), GraphDirection.Both);

        Assert.Empty(seven.Paths);
        Assert.Equal(Disconnection.FartherThanLimit, seven.Why);
        Assert.Equal(6, Assert.Single(six.Paths).Count);
    }

    [Fact]
    public void Separate_islands_are_not_connected()
    {
        var g = Build(("/W/A.md", "[[B]]"), ("/W/B.md", ""), ("/W/C.md", "[[D]]"), ("/W/D.md", ""));

        var result = GraphQueries.Paths(g, P("/W/A.md"), P("/W/D.md"), GraphDirection.Both);

        Assert.Empty(result.Paths);
        Assert.Equal(Disconnection.NotConnected, result.Why);
    }

    [Fact]
    public void Attachments_and_ghosts_are_not_walked_through()
    {
        var g = Build(("/W/A.md", "![[pic.png]] [[Nowhere]]"), ("/W/B.md", "![[pic.png]] [[Nowhere]]"));

        Assert.Equal(Disconnection.NotConnected, GraphQueries.Paths(g, P("/W/A.md"), P("/W/B.md"), GraphDirection.Both).Why);
    }

    [Fact]
    public void A_note_to_itself_is_one_empty_path()
    {
        var g = Build(("/W/A.md", ""));

        var result = GraphQueries.Paths(g, P("/W/A.md"), P("/W/A.md"), GraphDirection.Both);

        Assert.Empty(Assert.Single(result.Paths));
        Assert.Equal(Disconnection.None, result.Why);
    }

    [Fact]
    public void A_filter_keeps_the_walk_off_the_notes_it_drops_but_not_the_ends()
    {
        var g = Build(("/W/A.md", "[[2026-11-12]]"), ("/W/2026-11-12.md", "[[B]]"), ("/W/B.md", ""));
        var noDays = GraphFilter.All with { Days = false };

        Assert.Equal(["/W/A.md -> /W/2026-11-12.md -> /W/B.md"],
            GraphQueries.Paths(g, P("/W/A.md"), P("/W/B.md"), GraphDirection.Both).Paths.Select(p => Walk(g, "/W/A.md", p)));
        Assert.Empty(GraphQueries.Paths(g, P("/W/A.md"), P("/W/B.md"), GraphDirection.Both, noDays).Paths);

        // A day note named as an end is still reached.
        Assert.Equal(["/W/2026-11-12.md -> /W/B.md"],
            GraphQueries.Paths(g, P("/W/2026-11-12.md"), P("/W/B.md"), GraphDirection.Both, noDays).Paths.Select(p => Walk(g, "/W/2026-11-12.md", p)));
    }
}
