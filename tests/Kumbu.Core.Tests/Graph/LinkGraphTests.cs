// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Graph;

public class LinkGraphTests
{
    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private static Dictionary<VaultPath, LinkExtract> Extracts(params (string Path, string Content)[] notes) =>
        notes.ToDictionary(n => P(n.Path), n => LinkExtract.Of(P(n.Path), n.Content));

    private static LinkGraph Build(params (string Path, string Content)[] notes) => LinkGraph.Build("W", Extracts(notes), []);

    private static GraphNode Node(LinkGraph graph, string key) => Assert.Single(graph.Nodes, n => n.Key == key);

    private static int Index(LinkGraph graph, string key) => graph.Nodes.ToList().FindIndex(n => n.Key == key);

    private static GraphEdge Edge(LinkGraph graph, string from, string to) =>
        Assert.Single(graph.Edges, e => e.From == Index(graph, from) && e.To == Index(graph, to));

    [Fact]
    public void Note_link_makes_one_edge_with_its_lines()
    {
        var graph = Build(("/W/A.md", "x\nx\n[[B]]\nx\nx\nsee [[B|b]]\n"), ("/W/B.md", ""));

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(Index(graph, "/W/A.md"), edge.From);
        Assert.Equal(Index(graph, "/W/B.md"), edge.To);
        Assert.Equal([2, 5], edge.Lines);
        Assert.Equal(EdgeFlags.None, edge.Flags);
        Assert.Equal("B", Node(graph, "/W/B.md").Title);
        Assert.Equal(NodeKind.Note, Node(graph, "/W/B.md").Kind);
    }

    [Fact]
    public void Embed_and_alias_are_flags()
    {
        var graph = Build(("/W/A.md", "![[B]]\n[[Pumpe]]\n"), ("/W/B.md", "---\naliases: [Pumpe]\n---\n"));

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(EdgeFlags.Embed | EdgeFlags.Alias, edge.Flags);
        Assert.Equal([0, 1], edge.Lines);
    }

    [Fact]
    public void Ambiguous_link_points_to_each_candidate()
    {
        var graph = Build(("/W/x/A.md", "[[Plan]]"), ("/W/y/Plan.md", ""), ("/W/z/Plan.md", ""));

        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal(EdgeFlags.Ambiguous, Edge(graph, "/W/x/A.md", "/W/y/Plan.md").Flags);
        Assert.Equal(EdgeFlags.Ambiguous, Edge(graph, "/W/x/A.md", "/W/z/Plan.md").Flags);
    }

    [Fact]
    public void Self_link_makes_no_edge()
    {
        var graph = Build(("/W/A.md", "[[#Heading]]\n# Heading\n"));

        Assert.Empty(graph.Edges);
        Assert.Equal(LinkKind.Self, Assert.Single(graph.LinksOf(P("/W/A.md"))).Resolution.Kind);
    }

    [Fact]
    public void Ghosts_meet_by_folded_name()
    {
        var graph = Build(("/W/A.md", "[[Anna]]"), ("/W/B.md", "[[anna]]"), ("/W/C.md", "[[Aná]]\n[[Aná]]\n"));

        var anna = Node(graph, "?ANNA");
        Assert.Equal(NodeKind.Ghost, anna.Kind);
        Assert.Equal("Anna", anna.Title);
        Assert.Null(anna.Path);
        Assert.Equal(2, graph.InEdges(Index(graph, "?ANNA")).Count);

        var accented = Node(graph, "?ANÁ");
        Assert.Equal("Aná", accented.Title);
        var edge = graph.Edges[Assert.Single(graph.InEdges(Index(graph, "?ANÁ")))];
        Assert.Equal([0, 1], edge.Lines);
        Assert.Equal(2, graph.Nodes.Count(n => n.Kind == NodeKind.Ghost));
    }

    [Fact]
    public void Ghost_with_folder_is_its_own()
    {
        var graph = Build(("/W/A.md", "[[People/Anna]]"), ("/W/B.md", "[[Anna]]"));

        Assert.Equal(["?ANNA", "?PEOPLE/ANNA"], graph.Nodes.Where(n => n.Kind == NodeKind.Ghost).Select(n => n.Key).Order(StringComparer.Ordinal));
        Assert.Equal("People/Anna", Node(graph, "?PEOPLE/ANNA").Title);
    }

    [Fact]
    public void Relative_ghosts_belong_to_their_folder()
    {
        var graph = Build(
            ("/W/a/X.md", "[[./Anna]]\n[[../../Anna]]\n"), ("/W/b/Y.md", "[[./Anna]]"), ("/W/a/Z.md", "[[../Anna]]"),
            ("/W/Q.md", "[[a/Anna]] [x](/a/Anna.md)"));

        Assert.Equal(["?../../ANNA", "?/A/ANNA", "?/ANNA", "?/B/ANNA", "?A/ANNA"],
            graph.Nodes.Where(n => n.Kind == NodeKind.Ghost).Select(n => n.Key).Order(StringComparer.Ordinal));
        Assert.Equal(2, graph.InEdges(Index(graph, "?/A/ANNA")).Count);
    }

    [Fact]
    public void Ghost_becomes_note_when_it_exists()
    {
        var graph = Build(("/W/A.md", "[[Anna]]"), ("/W/B.md", "[[anna]]"), ("/W/Anna.md", ""));

        Assert.DoesNotContain(graph.Nodes, n => n.Kind == NodeKind.Ghost);
        Assert.Equal(2, graph.InEdges(graph.IndexOf(P("/W/Anna.md"))!.Value).Count);
        Assert.Equal(2, graph.Edges.Count);
    }

    [Fact]
    public void Day_and_attachment_kinds()
    {
        var graph = LinkGraph.Build("W", Extracts(("/W/2026-11-12.md", "![[a.png]]"), ("/W/A.md", "")), [P("/W/img/a.png"), P("/W/img/b.png")]);

        Assert.Equal(NodeKind.Day, Node(graph, "/W/2026-11-12.md").Kind);
        var attachment = Node(graph, "/W/img/a.png");
        Assert.Equal(NodeKind.Attachment, attachment.Kind);
        Assert.Equal("a.png", attachment.Title);
        Assert.Equal(EdgeFlags.Embed, Edge(graph, "/W/2026-11-12.md", "/W/img/a.png").Flags);
        Assert.Null(graph.IndexOf(P("/W/img/b.png")));
    }

    [Fact]
    public void Archived_note_is_marked()
    {
        var graph = Build(("/W/_archive/Old.md", ""), ("/W/A.md", ""));

        Assert.True(Node(graph, "/W/_archive/Old.md").Archived);
        Assert.False(Node(graph, "/W/A.md").Archived);
    }

    [Fact]
    public void Missing_file_is_a_ghost_with_extension()
    {
        var graph = Build(("/W/A.md", "[[report.docx]]"));

        Assert.Equal(NodeKind.Ghost, Node(graph, "?REPORT.DOCX").Kind);
        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Links_of_a_note_are_resolved_in_source_order()
    {
        var graph = Build(("/W/A.md", "[[B]] and [[Nobody]]\n[[#Top]]\n"), ("/W/B.md", ""));

        Assert.Collection(graph.LinksOf(P("/W/A.md")),
            l => Assert.Equal(P("/W/B.md"), l.Resolution.Target),
            l => Assert.Equal(LinkKind.NotFound, l.Resolution.Kind),
            l => Assert.Equal(LinkKind.Self, l.Resolution.Kind));
    }

    [Fact]
    public void SameAs_ignores_order_of_construction_but_not_a_line()
    {
        (string, string)[] notes = [("/W/A.md", "x\n[[B]] [[Ghost]]\n"), ("/W/B.md", "#tag [[A]]"), ("/W/C.md", "[[B]]")];
        var first = Build(notes);

        Assert.True(first.SameAs(Build([.. notes.Reverse()])));
        Assert.False(first.SameAs(Build(("/W/A.md", "[[B]] [[Ghost]]\nx\n"), ("/W/B.md", "#tag [[A]]"), ("/W/C.md", "[[B]]"))));
        Assert.False(first.SameAs(Build(("/W/A.md", "x\n[[B]] [[Ghost]]\n"), ("/W/B.md", "#other [[A]]"), ("/W/C.md", "[[B]]"))));
    }

    [Fact]
    public void Rebased_maps_every_path()
    {
        var graph = LinkGraph.Build("W", Extracts(("/W/A.md", "[[B]] ![[a.png]] [[Nobody]]"), ("/W/B.md", "")), [P("/W/img/a.png")]);

        var rebased = graph.Rebased(p => p.WithVault("Shared"));

        Assert.Equal("Shared", rebased.Vault);
        Assert.All(rebased.Nodes.Where(n => n.Path is not null), n => Assert.StartsWith("/Shared/", n.Path!.Value));
        Assert.All(rebased.Nodes.Where(n => n.Kind != NodeKind.Ghost), n => Assert.StartsWith("/Shared/", n.Key));
        Assert.All(rebased.Extracts.Keys, p => Assert.StartsWith("/Shared/", p.Value));
        Assert.All(rebased.Attachments, p => Assert.StartsWith("/Shared/", p.Value));
        Assert.Equal([P("/Shared/B.md"), P("/Shared/img/a.png"), null],
            rebased.LinksOf(P("/Shared/A.md")).Select(l => l.Resolution.Target));
        Assert.Equal(graph.Edges.Count, rebased.Edges.Count);
    }
}
