// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Queries;

/// <summary>The files one run of a query sees, for what reaches beyond the row's own file: <c>file()</c> and
/// <c>link()</c>, a file's links and backlinks, and the <c>random()</c> of each row, drawn once per run (bases spec
/// 2026-11-25 decision 16). Links resolve as everywhere else in Kumbu² (<see cref="LinkResolver"/>), worked out when a
/// query first asks.</summary>
public sealed class QueryVault
{
    // The resolver works on paths with a vault; the files of one run are of one vault, whatever its name.
    private const string Name = "vault";

    private readonly Dictionary<string, QueryNote> byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<VaultPath, QueryNote> byVaultPath = [];
    private readonly Dictionary<QueryNote, IReadOnlyList<QueryLink>> links = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, double> randoms = new(StringComparer.Ordinal);
    private readonly Random random = new();
    private LinkResolver? resolver;
    private Dictionary<string, List<QueryNote>>? backlinks;

    public QueryVault(IEnumerable<QueryNote> files)
    {
        foreach (var file in files)
        {
            byPath[file.Path] = file;
            if (VaultPath.TryParse($"/{Name}/{file.Path}", out var path)) byVaultPath[path] = file;
        }
    }

    /// <summary>A link of a note as a query sees it: the path in the vault of the file it leads to, or its target as
    /// written when it leads to none, and whether it embeds.</summary>
    public sealed record QueryLink(string Path, bool Embed);

    /// <summary>The file at a path in the vault, if there is one.</summary>
    public QueryNote? File(string path) => byPath.GetValueOrDefault(path);

    /// <summary>Where a wikilink to <paramref name="target"/> in <paramref name="from"/> leads: a file's path in the
    /// vault, or null when it leads to none (or to several).</summary>
    public string? Resolve(QueryNote from, string target) =>
        VaultPath.TryParse($"/{Name}/{from.Path}", out var source) ? InVault(Resolver.Resolve(source, target)) : null;

    /// <summary>The links of a note's text, embeds too, in the order written; a link to a heading of the note itself
    /// isn't one. None for a file that isn't a note.</summary>
    public IReadOnlyList<QueryLink> LinksOf(QueryNote note)
    {
        if (links.TryGetValue(note, out var known)) return known;
        var found = new List<QueryLink>();
        if (note.IsNote && VaultPath.TryParse($"/{Name}/{note.Path}", out var source))
        {
            foreach (var link in NoteLink.Parse(note.Content))
            {
                if (link.Target.Trim().Length == 0) continue;
                var embed = link is WikiLink { Embed: true } or MarkdownLink { Embed: true };
                found.Add(new QueryLink(InVault(Resolver.Resolve(source, link)) ?? link.Target.Trim(), embed));
            }
        }

        return links[note] = found;
    }

    /// <summary>The files whose links lead to <paramref name="note"/>, each once, in path order; worked out for the whole
    /// vault the first time it is asked.</summary>
    public IReadOnlyList<QueryNote> BacklinksOf(QueryNote note)
    {
        if (backlinks is null)
        {
            backlinks = new Dictionary<string, List<QueryNote>>(StringComparer.Ordinal);
            foreach (var source in byPath.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                foreach (var target in LinksOf(source).Select(l => l.Path).Where(byPath.ContainsKey).Distinct(StringComparer.Ordinal))
                {
                    if (!backlinks.TryGetValue(target, out var list)) backlinks[target] = list = [];
                    list.Add(source);
                }
            }
        }

        return backlinks.GetValueOrDefault(note.Path) ?? [];
    }

    /// <summary>The row's random number in [0, 1): the same for every use in the run.</summary>
    public double Random(QueryNote note)
    {
        if (!randoms.TryGetValue(note.Path, out var value)) randoms[note.Path] = value = random.NextDouble();
        return value;
    }

    private LinkResolver Resolver => resolver ??= new LinkResolver(
        Name,
        byVaultPath.Where(f => f.Value.IsNote).Select(f => f.Key),
        byVaultPath.Where(f => !f.Value.IsNote).Select(f => f.Key),
        byVaultPath.Where(f => f.Value.IsNote).ToDictionary(f => f.Key, f => NoteAliases.Of(f.Value.Content)));

    private string? InVault(LinkResolution resolution) =>
        resolution.Kind is LinkKind.Note or LinkKind.Attachment && resolution.Target is { } target && byVaultPath.TryGetValue(target, out var file)
            ? file.Path
            : null;
}
