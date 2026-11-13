// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Links;

public enum LinkKind
{
    /// <summary>Resolves to exactly one note.</summary>
    Note,

    /// <summary>[[#Heading]]: a heading of the source note itself.</summary>
    Self,

    /// <summary>Several notes, or several attachments, match and none is in the source's folder.</summary>
    Ambiguous,

    /// <summary>Resolves to exactly one attachment (<see cref="LinkResolution.Target"/>); or, without a target, names a
    /// file the resolver doesn't know (report.docx, or any file when it was given no attachments).</summary>
    Attachment,

    /// <summary>No note or attachment matches.</summary>
    NotFound,
}

/// <param name="ViaAlias">True if the target was found by a note's <c>aliases</c>, not by its name: for one note or
/// an ambiguity among aliased notes.</param>
public sealed record LinkResolution(LinkKind Kind, VaultPath? Target, IReadOnlyList<VaultPath> Candidates, bool ViaAlias = false);

/// <summary>
/// Resolves link paths to notes within one vault, close to Obsidian's rules: case-insensitive; relative to the
/// source's folder for "./" and "../"; otherwise the note whose path ends with the link path, preferring for a path
/// with a folder the one below the source's folder, then the one at that path from the vault's root, and for a bare
/// name the source's own folder. A source outside the vault's archive prefers, after the folder preferences, what lies
/// outside it over what lies in it; an archived source's links resolve without that preference. Never guesses beyond
/// that. Attachments resolve by the same rules, by their name with its extension ("shot.png"), before notes. Only when
/// neither finds anything does a bare name (no "/", no ".md") lead to the notes whose aliases contain it, with the same
/// archive and same-folder preferences.
/// </summary>
public sealed class LinkResolver
{
    private readonly string _vault;
    private readonly Dictionary<string, List<VaultPath>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<VaultPath>>? _attachmentsByName;
    private readonly Dictionary<string, List<VaultPath>> _byAlias = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="notes">Every note of one vault.</param>
    /// <param name="attachments">Every attachment of the vault; null if unknown, so a link to a file is then
    /// <see cref="LinkKind.Attachment"/> without a target, whether the file exists or not.</param>
    /// <param name="noteAliases">The aliases of notes, as <see cref="NoteAliases"/> reads them; they count only for
    /// paths among <paramref name="notes"/>.</param>
    public LinkResolver(
        string vault, IEnumerable<VaultPath> notes, IEnumerable<VaultPath>? attachments = null,
        IReadOnlyDictionary<VaultPath, IReadOnlyList<string>>? noteAliases = null)
    {
        _vault = vault;
        foreach (var note in notes)
        {
            if (note.Vault != vault) continue;
            var name = StripMd(note.Name);
            Add(_byName, name, note);
            if (noteAliases?.GetValueOrDefault(note) is not { } aliases) continue;
            foreach (var alias in aliases)
            {
                // An alias that is the note's own name already finds it by its name.
                if (alias.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                // NoteAliases lists each alias of a note once; a check of the list here would be quadratic in the notes.
                if (!_byAlias.TryGetValue(alias, out var list)) _byAlias[alias] = list = [];
                list.Add(note);
            }
        }

        if (attachments is null) return;
        _attachmentsByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in attachments)
        {
            // A kept folder's marker is no file of the vault anyone links to.
            if (attachment.Vault == vault && !FolderMarker.Is(attachment)) Add(_attachmentsByName, attachment.Name, attachment);
        }
    }

    private static void Add(Dictionary<string, List<VaultPath>> byName, string name, VaultPath path)
    {
        if (!byName.TryGetValue(name, out var list)) byName[name] = list = [];
        list.Add(path);
    }

    /// <summary>A note's path inside its vault without ".md", an attachment's with its extension: the most specific
    /// link form.</summary>
    public static string LinkText(VaultPath note) => StripMd(string.Join('/', note.Segments.Skip(1)));

    /// <summary>Where <paramref name="link"/> in <paramref name="source"/> leads; a Markdown link names a path, so
    /// never an alias (spec §3.1).</summary>
    public LinkResolution Resolve(VaultPath source, NoteLink link) => Resolve(source, link.Target, wikilink: link is WikiLink);

    /// <summary>Where a wikilink to <paramref name="target"/> in <paramref name="source"/> leads.</summary>
    public LinkResolution Resolve(VaultPath source, string target) => Resolve(source, target, wikilink: true);

    private LinkResolution Resolve(VaultPath source, string target, bool wikilink)
    {
        var text = target.Trim().Normalize(NormalizationForm.FormC);
        if (text.Length == 0) return new LinkResolution(LinkKind.Self, source, []);

        // "./x", "../x" (relative to the source's folder) and "/x" (from the vault's root) name one path exactly;
        // anything else matches the end of a path.
        var exact = text.StartsWith('/');
        if (text.StartsWith("./", StringComparison.Ordinal) || text.StartsWith("../", StringComparison.Ordinal))
        {
            exact = true;
            var folder = string.Join('/', source.Segments.SkipLast(1));
            if (!VaultPath.TryParse($"/{folder}/{text}", out var relative) || relative.Vault != _vault || relative.Segments.Count < 2)
            {
                return new LinkResolution(LinkKind.NotFound, null, []);
            }

            text = string.Join('/', relative.Segments.Skip(1));
        }

        var full = text.TrimStart('/');
        var fileName = full[(full.LastIndexOf('/') + 1)..];
        var attachmentName = _attachmentsByName is not null && AttachmentFormat.ForName(fileName) is not null;
        if (attachmentName
            && Find(_attachmentsByName!, LinkKind.Attachment, source, full, fileName, "/" + full, exact) is { } attachment)
        {
            return attachment;
        }

        var path = StripMd(full);
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (Find(_byName, LinkKind.Note, source, path, name, "/" + path + ".md", exact) is { } note) return note;

        // A bare name that no note or attachment carries may be a note's alias; "Kumbu.md" names a file, never an alias,
        // and so does "shot.png": without the vault's attachments it may name one that exists.
        if (wikilink && !exact && !full.Contains('/') && !full.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && AttachmentFormat.ForName(fileName) is null
            && _byAlias.TryGetValue(name, out var aliased)
            && Choose(LinkKind.Note, source, [.. aliased], exact) is { } byAlias)
        {
            return byAlias with { ViaAlias = true };
        }

        var looksLikeFile = name.LastIndexOf('.') is var dot and > 0 && dot < name.Length - 1 && !name[(dot + 1)..].Contains(' ');
        // A file of a kind the vault keeps, known to be missing, leads nowhere, as a missing note's link does.
        return new LinkResolution(looksLikeFile && !attachmentName ? LinkKind.Attachment : LinkKind.NotFound, null, []);
    }

    /// <summary>The note or attachment (by <paramref name="kind"/>) that <paramref name="path"/> names, as the rules
    /// above say; null if none matches.</summary>
    private static LinkResolution? Find(
        Dictionary<string, List<VaultPath>> byName, LinkKind kind, VaultPath source, string path, string name, string suffix, bool exact)
    {
        var named = byName.GetValueOrDefault(name) ?? [];
        var candidates = named.Where(n => Matches(InVault(n), suffix, exact)).ToList();

        // A path with a folder ("Work/plan") is, as in Obsidian, first a path below the source's folder,
        // then a path from the vault's root, and only then a path suffix.
        if (!exact && path.Contains('/') && candidates.Count > 1)
        {
            var folder = string.Join("", source.Segments.Skip(1).SkipLast(1).Select(s => "/" + s));
            var preferred = named.FirstOrDefault(n => Matches(InVault(n), folder + suffix, true))
                ?? named.FirstOrDefault(n => Matches(InVault(n), suffix, true));
            if (preferred is not null) return new LinkResolution(kind, preferred, candidates);
        }

        return Choose(kind, source, candidates, exact);
    }

    /// <summary>The one of <paramref name="candidates"/> a link from <paramref name="source"/> leads to, after the
    /// archive and same-folder preferences; ambiguous if several remain; null if there are none.</summary>
    private static LinkResolution? Choose(LinkKind kind, VaultPath source, List<VaultPath> candidates, bool exact)
    {
        // For a living note, what lies in the vault's archive counts only when nothing else matches: an archived copy
        // never makes its link ambiguous. An archived note keeps leading to its archived siblings.
        if (!exact && !ArchiveFolder.Contains(source) && candidates.Any(ArchiveFolder.Contains) && candidates.Any(c => !ArchiveFolder.Contains(c)))
        {
            candidates = [.. candidates.Where(c => !ArchiveFolder.Contains(c))];
        }

        if (candidates.Count == 1) return new LinkResolution(kind, candidates[0], candidates);
        if (candidates.Count == 0) return null;

        var sameFolder = candidates.Where(c => c.Segments.SkipLast(1).SequenceEqual(source.Segments.SkipLast(1))).ToList();
        return sameFolder.Count == 1
            ? new LinkResolution(kind, sameFolder[0], candidates)
            : new LinkResolution(LinkKind.Ambiguous, null, candidates);
    }

    private static string InVault(VaultPath note) => "/" + string.Join('/', note.Segments.Skip(1));

    private static bool Matches(string notePath, string suffix, bool exact) =>
        exact ? notePath.Equals(suffix, StringComparison.OrdinalIgnoreCase) : notePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static string StripMd(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
}
