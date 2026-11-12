// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
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
        var all = await notes.ReadNotesUnderAsync(vault, ct);
        var resolver = new LinkResolver(
            vault.Segments[0], all.Select(n => n.Path), await StoreAttachments.PathsUnderAsync(notes, vault, ct), NoteAliases.Of(all));
        return [.. NoteLink.Parse(content).Select(link => new ResolvedLink(link, resolver.Resolve(path, link.Target)))];
    }

    /// <summary>The lines in the vault that link to <paramref name="path"/>, by source path and line; then the lines
    /// whose link is ambiguous and may mean it.</summary>
    public static async Task<IReadOnlyList<Backlink>> ToAsync(INoteReader notes, VaultPath path, CancellationToken ct)
    {
        var vault = VaultPath.Root.Child(path.Segments[0]);
        var all = await notes.ReadNotesUnderAsync(vault, ct);
        var resolver = new LinkResolver(
            vault.Segments[0], all.Select(n => n.Path), await StoreAttachments.PathsUnderAsync(notes, vault, ct), NoteAliases.Of(all));
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

    /// <summary>The lines of the other notes in the vault that name <paramref name="path"/> without linking to it (see
    /// <see cref="Mentions"/>), by source path and line; archived notes only when <see cref="ArchiveFolder.Shown"/>.
    /// An attachment has none.</summary>
    public static async Task<IReadOnlyList<Mention>> MentionsAsync(INoteReader notes, VaultPath path, bool archive, CancellationToken ct)
    {
        if (!path.IsNote) return [];
        var title = path.Name[..^".md".Length].Normalize(NormalizationForm.FormC);
        if (title.Length < Mentions.MinTitleLength) return [];

        var vault = VaultPath.Root.Child(path.Segments[0]);
        var shown = ArchiveFolder.Shown(path, archive);
        var result = new List<Mention>();
        foreach (var source in (await notes.ReadNotesUnderAsync(vault, ct)).OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            ct.ThrowIfCancellationRequested();
            if (source.Path == path || (!shown && ArchiveFolder.Contains(source.Path))) continue;
            var lines = Lines.Split(source.Content);
            result.AddRange(Mentions.In(source.Content, title).Select(m => new Mention(source.Path, m.Line, lines[m.Line], m.Marks)));
        }

        return result;
    }
}
