// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Graph;

/// <summary>
/// <see cref="NoteLinks.OfAsync"/> and <see cref="NoteLinks.ToAsync"/> as they were before the link graph replaced
/// their full scan (spec §10), over the notes and attachments of one vault instead of a reader: the reference the graph
/// is checked against. Keep it as it is; it is the old truth, not code to improve. The only change, so the oracle tests
/// stay fast: what depends on nothing but the vault (the resolver, and each source's lines and resolved links) is made
/// once per vault, not once per call.
/// </summary>
public sealed class ScanOracle
{
    private sealed record Source(Note Note, IReadOnlyList<string> Lines, List<(int Line, LinkResolution Resolution)> Resolved);

    private readonly IReadOnlyList<Note> _all;
    private readonly LinkResolver _resolver;
    private readonly List<Source> _sources;

    public ScanOracle(string vault, IReadOnlyList<Note> notes, IReadOnlyList<VaultPath> attachments)
    {
        _all = notes;
        _resolver = new LinkResolver(vault, _all.Select(n => n.Path), attachments, NoteAliases.Of(_all));
        _sources = [];
        foreach (var source in _all.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var lines = Lines.Split(source.Content);
            var resolved = NoteLink.Parse(source.Content)
                .Where(l => l.Target.Trim().Length > 0)
                .Select(l => (l.Line, Resolution: _resolver.Resolve(source.Path, l)))
                .ToList();
            _sources.Add(new Source(source, lines, resolved));
        }
    }

    public static IReadOnlyList<ResolvedLink> Links(IReadOnlyList<Note> notes, IReadOnlyList<VaultPath> attachments, VaultPath note) =>
        new ScanOracle(note.Segments[0], notes, attachments).Links(note);

    public static IReadOnlyList<Backlink> Backlinks(IReadOnlyList<Note> notes, IReadOnlyList<VaultPath> attachments, VaultPath target) =>
        new ScanOracle(target.Segments[0], notes, attachments).Backlinks(target);

    public IReadOnlyList<ResolvedLink> Links(VaultPath path)
    {
        var content = _all.Single(n => n.Path == path).Content;
        return [.. NoteLink.Parse(content).Select(link => new ResolvedLink(link, _resolver.Resolve(path, link)))];
    }

    public IReadOnlyList<Backlink> Backlinks(VaultPath path)
    {
        var exact = new List<Backlink>();
        var maybe = new List<Backlink>();
        foreach (var (source, lines, resolved) in _sources)
        {
            exact.AddRange(resolved.Where(r => r.Resolution.Target == path).Select(r => r.Line).Distinct()
                .Select(line => new Backlink(source.Path, line, lines[line], Ambiguous: false)));
            maybe.AddRange(resolved
                .Where(r => r.Resolution.Kind == LinkKind.Ambiguous && r.Resolution.Candidates.Contains(path))
                .Select(r => r.Line).Distinct()
                .Select(line => new Backlink(source.Path, line, lines[line], Ambiguous: true)));
        }

        return [.. exact, .. maybe];
    }
}
