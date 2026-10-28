// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
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
        var vault = VaultPath.Root.Child(path.Segments[0]);
        var resolver = new LinkResolver(
            vault.Segments[0], await notes.ListNotesUnderAsync(vault, ct), await StoreAttachments.PathsUnderAsync(notes, vault, ct));
        return [.. NoteLink.Parse(content).Select(link => new ResolvedLink(link, resolver.Resolve(path, link.Target)))];
    }

    /// <summary>The lines in the vault that link to <paramref name="path"/>, by source path and line; then the lines
    /// whose link is ambiguous and may mean it.</summary>
    public static async Task<IReadOnlyList<Backlink>> ToAsync(INoteReader notes, VaultPath path, CancellationToken ct)
    {
        var vault = VaultPath.Root.Child(path.Segments[0]);
        var all = await notes.ReadNotesUnderAsync(vault, ct);
        var resolver = new LinkResolver(vault.Segments[0], all.Select(n => n.Path), await StoreAttachments.PathsUnderAsync(notes, vault, ct));
        var exact = new List<Backlink>();
        var maybe = new List<Backlink>();
        foreach (var source in all.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var lines = Lines.Split(source.Content);
            var resolved = NoteLink.Parse(source.Content)
                .Where(l => l.Target.Trim().Length > 0)
                .Select(l => (l.Line, Resolution: resolver.Resolve(source.Path, l.Target)))
                .ToList();
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
