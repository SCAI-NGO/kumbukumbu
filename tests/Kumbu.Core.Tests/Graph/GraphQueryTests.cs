// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Graph;

public class GraphQueryTests
{
    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private static LinkGraph Build(params (string Path, string Content)[] notes) =>
        LinkGraph.Build("W", notes.ToDictionary(n => P(n.Path), n => LinkExtract.Of(P(n.Path), n.Content)), [P("/W/pic.png")]);

    private static int Index(LinkGraph graph, string key) => graph.Nodes.ToList().FindIndex(n => n.Key == key);

    private static List<string> Keys(LinkGraph graph, IEnumerable<int> nodes) => [.. nodes.Select(n => graph.Nodes[n].Key)];

    private static GraphFilter With(Func<GraphFilter, GraphFilter> change) => change(GraphFilter.All);

    [Fact]
    public void Global_keeps_every_edge_uncapped()
    {
        var notes = Enumerable.Range(0, 400).Select(i => ($"/W/N{i:000}.md", "[[Hub]]")).Append(("/W/Hub.md", "")).ToArray();
        var graph = Build(notes);

        var selection = GraphQueries.Global(graph, GraphFilter.All);

        Assert.Equal(401, selection.Nodes.Count);
        Assert.Equal(400, selection.Edges.Count);
    }

    [Fact]
    public void Filter_by_path_keeps_linked_ghosts_and_attachments()
    {
        var graph = Build(("/W/In/A.md", "[[Nowhere]] ![[pic.png]] [[B]]"), ("/W/Out/B.md", "[[Other]]"), ("/W/Out/C.md", ""));

        var selection = GraphQueries.Global(graph, With(f => f with { Paths = [P("/W/In")] }));

        var kept = graph.Nodes.Where((_, i) => selection.Nodes.Contains(i)).ToList();
        Assert.Equal(["/W/In/A.md", "/W/pic.png"], kept.Where(n => n.Kind != NodeKind.Ghost).Select(n => n.Key));
        Assert.Equal("Nowhere", Assert.Single(kept, n => n.Kind == NodeKind.Ghost).Title);
    }

    [Fact]
    public void Filter_by_tag_includes_day_notes()
    {
        var graph = Build(("/W/A.md", "#work/x"), ("/W/2026-11-16.md", "#work"), ("/W/C.md", "#play"));

        var keys = Keys(graph, GraphQueries.Global(graph, With(f => f with { Tags = ["work"] })).Nodes);

        Assert.Equal(["/W/2026-11-16.md", "/W/A.md"], keys);
        Assert.Equal(["/W/A.md"], Keys(graph, GraphQueries.Global(graph, With(f => f with { Tags = ["#work"], Days = false })).Nodes));
    }

    [Fact]
    public void Archive_hidden_by_default()
    {
        var graph = Build(("/W/A.md", "[[Old]]"), ("/W/_archive/Old.md", ""));

        Assert.Equal(["/W/A.md"], Keys(graph, GraphQueries.Global(graph, GraphFilter.All).Nodes));
        Assert.Equal(2, GraphQueries.Global(graph, With(f => f with { Archive = true })).Nodes.Count);
    }

    [Fact]
    public void Local_depth_two_each_node_once()
    {
        var graph = Build(("/W/A.md", "[[B]] [[C]]"), ("/W/B.md", "[[C]] [[D]]"), ("/W/C.md", "[[D]]"), ("/W/D.md", "[[E]]"), ("/W/E.md", ""));

        var local = GraphQueries.Local(graph, P("/W/A.md"), 2, GraphDirection.Out, 20, GraphFilter.All);

        Assert.Equal([("/W/A.md", 0), ("/W/B.md", 1), ("/W/C.md", 1), ("/W/D.md", 2)], local.Nodes.Select(n => (graph.Nodes[n.Node].Key, n.Depth)).ToList());
        Assert.Equal(5, local.Edges.Count);
        Assert.Empty(local.LeftOut);
    }

    [Fact]
    public void Local_direction_in_only()
    {
        var graph = Build(("/W/A.md", "[[B]]"), ("/W/B.md", "[[C]]"), ("/W/C.md", ""));

        var local = GraphQueries.Local(graph, P("/W/B.md"), 2, GraphDirection.In, 20, GraphFilter.All);

        Assert.Equal(["/W/B.md", "/W/A.md"], Keys(graph, local.Nodes.Select(n => n.Node)));
    }

    [Fact]
    public void Local_caps_a_hub_by_degree_then_path()
    {
        var notes = new List<(string, string)> { ("/W/Hub.md", string.Join(' ', Enumerable.Range(0, 30).Select(i => $"[[N{i:00}]]"))) };
        notes.AddRange(Enumerable.Range(0, 30).Select(i => ($"/W/N{i:00}.md", i < 5 ? "[[X]] [[Y]]" : i < 15 ? "[[X]]" : "")));
        notes.Add(("/W/X.md", ""));
        notes.Add(("/W/Y.md", ""));
        var graph = Build([.. notes]);

        var local = GraphQueries.Local(graph, P("/W/Hub.md"), 1, GraphDirection.Out, 20, GraphFilter.All);

        Assert.Equal(10, local.LeftOut[Index(graph, "/W/Hub.md")]);
        var shown = Keys(graph, local.Nodes.Skip(1).Select(n => n.Node));
        Assert.Equal(20, shown.Count);
        Assert.Equal(Enumerable.Range(0, 15).Select(i => $"/W/N{i:00}.md"), shown.Take(15));
        Assert.Equal(Enumerable.Range(15, 5).Select(i => $"/W/N{i:00}.md"), shown.Skip(15));
    }

    [Fact]
    public void Local_start_shown_despite_filter()
    {
        var graph = Build(("/W/A.md", "[[B]]"), ("/W/B.md", ""));

        var local = GraphQueries.Local(graph, P("/W/A.md"), 1, GraphDirection.Out, 20, With(f => f with { Tags = ["nothing"] }));

        Assert.Equal(["/W/A.md"], Keys(graph, local.Nodes.Select(n => n.Node)));
    }

    [Fact]
    public void Local_does_not_walk_through_ghosts()
    {
        var graph = Build(("/W/A.md", "[[Nowhere]]"), ("/W/B.md", "[[Nowhere]]"), ("/W/P.md", "![[pic.png]]"), ("/W/Q.md", "![[pic.png]]"));

        var local = GraphQueries.Local(graph, P("/W/A.md"), 3, GraphDirection.Both, 20, GraphFilter.All);
        Assert.Equal(2, local.Nodes.Count);

        var viaPicture = GraphQueries.Local(graph, P("/W/P.md"), 3, GraphDirection.Both, 20, GraphFilter.All);
        Assert.Equal(2, viaPicture.Nodes.Count);
    }

    [Fact]
    public void Node_reads_degree_times_and_first_paragraph()
    {
        var words = string.Join(' ', Enumerable.Range(0, 80).Select(i => $"word{i}"));
        var content = $"---\ntitle: x\n---\n# Heading\n\n{words}\n\nSecond.\n";
        var graph = Build(("/W/A.md", content), ("/W/B.md", "[[A]]"), ("/W/C.md", "[[A]]"));
        var written = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        var created = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        var note = new StoredNote(P("/W/A.md"), content, 3, "me", written, [], CreatedAt: created, CreatedAtLeast: true);

        var detail = GraphQueries.Node(graph, Index(graph, "/W/A.md"), note);

        Assert.Equal((2, 0), (detail.In, detail.Out));
        Assert.Equal(written, detail.Changed);
        Assert.Equal(created, detail.Created);
        Assert.True(detail.CreatedAtLeast);
        Assert.EndsWith("…", detail.FirstParagraph);
        Assert.InRange(detail.FirstParagraph!.Length, 250, 301);
        var body = detail.FirstParagraph[..^1];
        Assert.StartsWith("word0 word1", body);
        Assert.Contains(body.Split(' ')[^1], words.Split(' '));
        Assert.Null(GraphQueries.Node(graph, 0, null).FirstParagraph);
    }

    [Fact]
    public void Orphans_are_notes_only()
    {
        var graph = Build(("/W/A.md", "[[B]] ![[pic.png]] [[Nowhere]]"), ("/W/B.md", ""), ("/W/Lone.md", ""), ("/W/2026-11-16.md", ""), ("/W/In/Lone2.md", ""));

        Assert.Equal(["/W/2026-11-16.md", "/W/In/Lone2.md", "/W/Lone.md"], Keys(graph, GraphQueries.Orphans(graph, GraphFilter.All)));
        Assert.Equal(["/W/In/Lone2.md"], Keys(graph, GraphQueries.Orphans(graph, With(f => f with { Paths = [P("/W/In")] }))));
    }

    [Fact]
    public void Hubs_ordered_by_degree_then_path()
    {
        var graph = Build(("/W/A.md", "[[C]] [[D]]"), ("/W/B.md", "[[C]] [[D]] [[pic.png]]"), ("/W/C.md", "[[D]]"), ("/W/D.md", ""), ("/W/E.md", ""));

        var hubs = GraphQueries.Hubs(graph, GraphFilter.All, 3);

        // B, C and D all have three links, so the path orders them; A (two) falls off the top three.
        Assert.Equal([("/W/B.md", 0, 3), ("/W/C.md", 2, 1), ("/W/D.md", 3, 0)], hubs.Select(h => (graph.Nodes[h.Node].Key, h.In, h.Out)));
    }

    [Fact]
    public void Unresolved_ordered_by_sources()
    {
        var graph = Build(("/W/A.md", "[[Once]] [[Twice]]\n[[Twice]]"), ("/W/B.md", "[[Many]]"), ("/W/C.md", "[[Many]] [[Twice]]"), ("/W/D.md", "[[Many]]"));

        var unresolved = GraphQueries.Unresolved(graph, GraphFilter.All);

        Assert.Equal(["Many", "Twice", "Once"], unresolved.Select(u => graph.Nodes[u.Ghost].Title));
        Assert.Equal([("/W/A.md", 0), ("/W/A.md", 1), ("/W/C.md", 0)], unresolved[1].From.Select(f => (f.Source.Value, f.Line)));
        Assert.Empty(GraphQueries.Unresolved(graph, With(f => f with { Ghosts = false })));
    }

    // Below a path, a ghost counts only the notes there that link to it, and is ordered by them: Wide has more sources in
    // all, Near more below /W/In.
    [Fact]
    public void Unresolved_below_a_path_counts_the_notes_there()
    {
        var graph = Build(
            ("/W/In/A.md", "[[Near]] [[Wide]]"), ("/W/In/B.md", "[[Near]]"),
            ("/W/Out/C.md", "[[Wide]]"), ("/W/Out/D.md", "[[Wide]]"), ("/W/Out/E.md", "[[Wide]] [[Far]]"));

        var unresolved = GraphQueries.Unresolved(graph, With(f => f with { Paths = [P("/W/In")] }));

        Assert.Equal(["Near", "Wide"], unresolved.Select(u => graph.Nodes[u.Ghost].Title));
        Assert.Equal([("/W/In/A.md", 0), ("/W/In/B.md", 0)], unresolved[0].From.Select(f => (f.Source.Value, f.Line)));
        Assert.Equal([("/W/In/A.md", 0)], unresolved[1].From.Select(f => (f.Source.Value, f.Line)));
        Assert.Equal(["Wide", "Near", "Far"], GraphQueries.Unresolved(graph, GraphFilter.All).Select(u => graph.Nodes[u.Ghost].Title));
    }

    [Fact]
    public void Bundle_stops_at_depth_and_at_refused_notes()
    {
        var graph = Build(("/W/A.md", "[[B]] [[Secret]]"), ("/W/B.md", "[[C]]"), ("/W/C.md", "[[D]]"), ("/W/D.md", ""), ("/W/Secret.md", "[[E]]"), ("/W/E.md", ""));

        var bundle = GraphQueries.Bundle(graph, P("/W/A.md"), 2, p => p.Name != "Secret.md");

        Assert.Equal(["/W/A.md", "/W/B.md", "/W/C.md"], Keys(graph, bundle.Nodes));
        Assert.Equal([("/W/A.md", "/W/B.md"), ("/W/B.md", "/W/C.md")],
            bundle.Edges.Select(e => (graph.Nodes[graph.Edges[e].From].Key, graph.Nodes[graph.Edges[e].To].Key)));
        Assert.Empty(GraphQueries.Bundle(graph, P("/W/Secret.md"), 2, p => p.Name != "Secret.md").Nodes);
    }

    [Fact]
    public void Bundle_takes_embedded_attachments()
    {
        var graph = Build(("/W/A.md", "![[pic.png]] [[B]] [[Nowhere]]"), ("/W/B.md", "[pic](pic.png)"), ("/W/C.md", "![[pic.png]]"));

        var bundle = GraphQueries.Bundle(graph, P("/W/A.md"), 1, _ => true);

        Assert.Equal(["/W/A.md", "/W/B.md", "/W/pic.png"], Keys(graph, bundle.Nodes));
        Assert.Equal(3, bundle.Edges.Count);
    }
}
