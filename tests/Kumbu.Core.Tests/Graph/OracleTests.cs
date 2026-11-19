// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Graph;

/// <summary>The link graph against today's full scan (spec §10), over generated vaults and random operations on them.</summary>
public class OracleTests
{
    public static TheoryData<int> Vaults => [.. Enumerable.Range(1, 200)];

    public static TheoryData<int> Sequences => [.. Enumerable.Range(1, 50)];

    [Theory]
    [MemberData(nameof(Vaults))]
    public void Graph_matches_the_scan(int seed)
    {
        var vault = VaultGenerator.Vault(seed, 60);

        Matches(vault, LinkGraph.Build(VaultGenerator.VaultName, Extracts(vault.Notes), vault.Attachments, TestContext.Current.CancellationToken), $"seed {seed}");
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void Graph_after_operations_matches_a_fresh_build_and_the_scan(int seed)
    {
        var vault = VaultGenerator.Vault(seed, 30);
        var carried = Extracts(vault.Notes);
        var steps = new List<string>();
        for (var step = 0; step < 30; step++)
        {
            var before = vault.Notes.ToDictionary(n => n.Path, n => n.Content);
            (vault, var operation) = VaultGenerator.Step(seed * 1000 + step, vault);
            steps.Add(operation);

            // As an index kept current would: untouched notes keep their extract, changed and new ones are read anew.
            var now = vault.Notes.Select(n => n.Path).ToHashSet();
            foreach (var gone in carried.Keys.Where(p => !now.Contains(p)).ToList()) carried.Remove(gone);
            foreach (var note in vault.Notes)
            {
                if (before.GetValueOrDefault(note.Path) != note.Content) carried[note.Path] = LinkExtract.Of(note.Path, note.Content);
            }

            var context = $"seed {seed}, after {string.Join("; ", steps)}";
            var fresh = LinkGraph.Build(VaultGenerator.VaultName, Extracts(vault.Notes), vault.Attachments, TestContext.Current.CancellationToken);
            var kept = LinkGraph.Build(VaultGenerator.VaultName, carried, [.. vault.Attachments], TestContext.Current.CancellationToken);
            Assert.True(kept.SameAs(fresh), $"{context}: the graph from carried extracts differs from a fresh one");
            Matches(vault, fresh, context);
        }
    }

    private static Dictionary<VaultPath, LinkExtract> Extracts(IEnumerable<Note> notes) =>
        notes.ToDictionary(n => n.Path, n => LinkExtract.Of(n.Path, n.Content));

    /// <summary>Every note's links resolution for resolution, every edge link for link, every note's and attachment's
    /// backlinks line for line.</summary>
    private static void Matches(GeneratedVault vault, LinkGraph graph, string context)
    {
        var nodes = Enumerable.Range(0, graph.Nodes.Count).ToList();
        var outPairs = nodes.SelectMany(graph.OutEdges).Select(e => (graph.Edges[e].From, graph.Edges[e].To)).ToHashSet();
        var inPairs = nodes.SelectMany(graph.InEdges).Select(e => (graph.Edges[e].From, graph.Edges[e].To)).ToHashSet();
        Assert.True(outPairs.SetEquals(inPairs), $"{context}: the out-edges and in-edges name different (source, target) pairs");

        var contents = vault.Notes.ToDictionary(n => n.Path, n => n.Content);
        var scan = new ScanOracle(VaultGenerator.VaultName, vault.Notes, vault.Attachments);
        foreach (var note in vault.Notes)
        {
            var expected = scan.Links(note.Path);
            var actual = graph.LinksOf(note.Path);
            Assert.True(expected.Count == actual.Count, Describe(context, note, $"{actual.Count} links, the scan {expected.Count}", vault));
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.True(Same(expected[i], actual[i]), Describe(context, note, $"link {i}: {actual[i]}, the scan {expected[i]}", vault));
            }

            var edges = Edges(expected);
            var got = Edges(graph, note.Path);
            Assert.True(edges.SetEquals(got), Describe(context, note,
                $"edges [{string.Join(", ", got.Except(edges))}] not in the scan, [{string.Join(", ", edges.Except(got))}] missing", vault));
        }

        foreach (var target in vault.Notes.Select(n => n.Path).Concat(vault.Attachments))
        {
            var expected = scan.Backlinks(target);
            var actual = NoteLinks.To(graph, p => contents[p], target);
            Assert.True(expected.SequenceEqual(actual), $"{context}: backlinks of {target}\n  graph: {string.Join("\n         ", actual)}" +
                $"\n  scan:  {string.Join("\n         ", expected)}\n{vault}");
        }
    }

    private static bool Same(ResolvedLink expected, ResolvedLink actual) =>
        expected.Link == actual.Link
        && expected.Resolution.Kind == actual.Resolution.Kind
        && expected.Resolution.Target == actual.Resolution.Target
        && expected.Resolution.ViaAlias == actual.Resolution.ViaAlias
        // The graph meets the notes in path order, the scan in the reader's: only the candidates' order may differ.
        && expected.Resolution.Candidates.ToHashSet().SetEquals(actual.Resolution.Candidates);

    /// <summary>The edges the scan's resolutions make: (target, flags, lines), all ghosts as one target "?".</summary>
    private static HashSet<string> Edges(IEnumerable<ResolvedLink> links)
    {
        var byTarget = new Dictionary<string, (EdgeFlags Flags, SortedSet<int> Lines)>();
        foreach (var (link, resolution) in links)
        {
            if (link.Target.Trim().Length == 0) continue;
            var flags = (link is WikiLink { Embed: true } or MarkdownLink { Embed: true } ? EdgeFlags.Embed : 0)
                | (resolution.ViaAlias ? EdgeFlags.Alias : 0)
                | (resolution.Kind == LinkKind.Ambiguous ? EdgeFlags.Ambiguous : 0);
            IEnumerable<string> targets = resolution.Kind switch
            {
                LinkKind.NotFound => ["?"],
                LinkKind.Attachment when resolution.Target is null => ["?"],
                LinkKind.Ambiguous => resolution.Candidates.Select(c => c.Value),
                _ => [resolution.Target!.Value],
            };
            foreach (var target in targets)
            {
                var edge = byTarget.GetValueOrDefault(target, (Flags: EdgeFlags.None, Lines: new SortedSet<int>()));
                edge.Lines.Add(link.Line);
                byTarget[target] = (edge.Flags | flags, edge.Lines);
            }
        }

        return [.. byTarget.Select(e => Edge(e.Key, e.Value.Flags, e.Value.Lines))];
    }

    private static HashSet<string> Edges(LinkGraph graph, VaultPath source)
    {
        var byTarget = new Dictionary<string, (EdgeFlags Flags, SortedSet<int> Lines)>();
        foreach (var index in graph.OutEdges(graph.IndexOf(source)!.Value))
        {
            var edge = graph.Edges[index];
            var target = graph.Nodes[edge.To].Path?.Value ?? "?";
            var merged = byTarget.GetValueOrDefault(target, (Flags: EdgeFlags.None, Lines: new SortedSet<int>()));
            merged.Lines.UnionWith(edge.Lines);
            byTarget[target] = (merged.Flags | edge.Flags, merged.Lines);
        }

        return [.. byTarget.Select(e => Edge(e.Key, e.Value.Flags, e.Value.Lines))];
    }

    private static string Edge(string target, EdgeFlags flags, IEnumerable<int> lines) => $"{target} {flags} [{string.Join(',', lines)}]";

    private static string Describe(string context, Note note, string what, GeneratedVault vault) => $"{context}: {note.Path}: {what}\n{vault}";
}
