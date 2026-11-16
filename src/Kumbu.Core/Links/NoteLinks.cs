// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Graph;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Links;

/// <summary>A link of a note and where it leads.</summary>
public sealed record ResolvedLink(NoteLink Link, LinkResolution Resolution);

/// <summary>A line of another note that links to a note or attachment; <paramref name="Line"/> 0-based.
/// <paramref name="Ambiguous"/>: the link matches several, this one among them.</summary>
public sealed record Backlink(VaultPath Source, int Line, string Text, bool Ambiguous);

/// <summary>The links of a note and the notes that link to a note or attachment, resolved within the vault: what
/// <c>links</c>, <c>backlinks</c> and the REST API answer from.</summary>
public static class NoteLinks
{
    /// <summary>The links in <paramref name="content"/> (the note at <paramref name="path"/>, or a version of it),
    /// resolved against the notes and attachments of its vault as they are now.</summary>
    public static async Task<IReadOnlyList<ResolvedLink>> OfAsync(INoteReader notes, VaultPath path, string content, CancellationToken ct)
    {
        var graph = await notes.GraphAsync(VaultPath.Root.Child(path.Segments[0]), ct);
        // The current content's links are the graph's; another version's are read from it, and resolve the same way.
        if (graph.Extracts.ContainsKey(path) && await notes.ReadAsync(path, ct) == content) return graph.LinksOf(path);

        var resolver = new LinkResolver(
            graph.Vault, graph.Extracts.Keys, graph.Attachments, graph.Extracts.ToDictionary(e => e.Key, e => e.Value.Aliases));
        return [.. NoteLink.Parse(content).Select(link => new ResolvedLink(link, resolver.Resolve(path, link)))];
    }

    /// <summary>The lines in the vault that link to <paramref name="path"/>, by source path and line; then the lines
    /// whose link is ambiguous and may mean it.</summary>
    public static async Task<IReadOnlyList<Backlink>> ToAsync(INoteReader notes, VaultPath path, CancellationToken ct)
    {
        var vault = VaultPath.Root.Child(path.Segments[0]);
        var graph = await notes.GraphAsync(vault, ct);
        if (graph.IndexOf(path) is not { } node || graph.InEdges(node).Count == 0) return [];
        var contents = (await notes.ReadNotesUnderAsync(vault, ct)).ToDictionary(n => n.Path, n => n.Content);
        return To(graph, p => contents[p], path);
    }

    /// <summary><see cref="ToAsync"/> from <paramref name="graph"/>, each line's text from <paramref name="content"/>:
    /// per in-edge, the lines of its source where a link leads to <paramref name="target"/>, then those where one is
    /// ambiguous with it among the candidates.</summary>
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

    /// <summary>The lines of the other notes in the vault that name <paramref name="path"/> without linking to it (see
    /// <see cref="Mentions"/>), by source path and line; archived notes only when <see cref="ArchiveFolder.Shown"/>.
    /// An attachment has none.</summary>
    public static async Task<IReadOnlyList<Mention>> MentionsAsync(INoteReader notes, VaultPath path, bool archive, CancellationToken ct)
    {
        if (!path.IsNote) return [];
        var title = path.Name[..^".md".Length].Normalize(NormalizationForm.FormC);

        var vault = VaultPath.Root.Child(path.Segments[0]);
        var all = await notes.ReadNotesUnderAsync(vault, ct);
        // The minimum length counts per term: a short title may still have long aliases.
        string[] terms = [title, .. all.FirstOrDefault(n => n.Path == path) is { } own ? NoteAliases.Of(own.Content) : []];
        var shown = ArchiveFolder.Shown(path, archive);
        var result = new List<Mention>();
        foreach (var source in all.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            ct.ThrowIfCancellationRequested();
            if (source.Path == path || (!shown && ArchiveFolder.Contains(source.Path))) continue;
            var lines = Lines.Split(source.Content);
            result.AddRange(Mentions.In(source.Content, terms, ct).Select(m => new Mention(source.Path, m.Line, lines[m.Line], m.Marks)));
        }

        return result;
    }
}
