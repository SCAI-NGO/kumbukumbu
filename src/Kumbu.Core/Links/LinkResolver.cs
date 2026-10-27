// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Links;

public enum LinkKind
{
    /// <summary>Resolves to exactly one note.</summary>
    Note,

    /// <summary>[[#Heading]]: a heading of the source note itself.</summary>
    Self,

    /// <summary>Several notes match and none is in the source's folder.</summary>
    Ambiguous,

    /// <summary>No note matches, and the target looks like a file (image.png).</summary>
    Attachment,

    /// <summary>No note matches.</summary>
    NotFound,
}

public sealed record LinkResolution(LinkKind Kind, VaultPath? Target, IReadOnlyList<VaultPath> Candidates);

/// <summary>
/// Resolves link paths to notes within one vault, close to Obsidian's rules: case-insensitive; relative
/// to the source's folder for "./" and "../"; otherwise the note whose path ends with the link path,
/// preferring for a path with a folder the one below the source's folder, then the one at that path from the
/// vault's root, and for a bare name the source's own folder. Never guesses beyond that.
/// </summary>
public sealed class LinkResolver
{
    private readonly string _vault;
    private readonly Dictionary<string, List<VaultPath>> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="notes">Every note of one vault.</param>
    public LinkResolver(string vault, IEnumerable<VaultPath> notes)
    {
        _vault = vault;
        foreach (var note in notes)
        {
            if (note.Vault != vault) continue;
            var name = StripMd(note.Name);
            if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = [];
            list.Add(note);
        }
    }

    /// <summary>A note's path inside its vault without ".md": the most specific link form.</summary>
    public static string LinkText(VaultPath note) => StripMd(string.Join('/', note.Segments.Skip(1)));

    public LinkResolution Resolve(VaultPath source, string target)
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

        var path = StripMd(text.TrimStart('/'));
        var name = path[(path.LastIndexOf('/') + 1)..];
        var suffix = "/" + path + ".md";
        var named = _byName.GetValueOrDefault(name) ?? [];
        var candidates = named.Where(n => Matches(InVault(n), suffix, exact)).ToList();

        // A path with a folder ("Work/plan") is, as in Obsidian, first a path below the source's folder,
        // then a path from the vault's root, and only then a path suffix.
        if (!exact && path.Contains('/') && candidates.Count > 1)
        {
            var folder = string.Join("", source.Segments.Skip(1).SkipLast(1).Select(s => "/" + s));
            var preferred = named.FirstOrDefault(n => Matches(InVault(n), folder + suffix, true))
                ?? named.FirstOrDefault(n => Matches(InVault(n), suffix, true));
            if (preferred is not null) return new LinkResolution(LinkKind.Note, preferred, candidates);
        }

        if (candidates.Count == 1) return new LinkResolution(LinkKind.Note, candidates[0], candidates);
        if (candidates.Count > 1)
        {
            var sameFolder = candidates.Where(c => c.Segments.SkipLast(1).SequenceEqual(source.Segments.SkipLast(1))).ToList();
            return sameFolder.Count == 1
                ? new LinkResolution(LinkKind.Note, sameFolder[0], candidates)
                : new LinkResolution(LinkKind.Ambiguous, null, candidates);
        }

        var looksLikeFile = name.LastIndexOf('.') is var dot and > 0 && dot < name.Length - 1 && !name[(dot + 1)..].Contains(' ');
        return new LinkResolution(looksLikeFile ? LinkKind.Attachment : LinkKind.NotFound, null, []);
    }

    private static string InVault(VaultPath note) => "/" + string.Join('/', note.Segments.Skip(1));

    private static bool Matches(string notePath, string suffix, bool exact) =>
        exact ? notePath.Equals(suffix, StringComparison.OrdinalIgnoreCase) : notePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    private static string StripMd(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
}
