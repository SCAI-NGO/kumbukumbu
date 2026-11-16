// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Kumbu.Core.Graph;

/// <summary>The identity of a link target that resolves to nothing (spec §4.3): two targets meet in one ghost exactly
/// when <see cref="Links.LinkResolver"/> would look them up as the same name. It trims, normalises to NFC and strips
/// ".md" in that order, as the resolver does, and its case-insensitive comparer is an ordinal upper-casing. A folder
/// part stays: "People/Anna" means something else than "Anna".</summary>
public static class GhostKey
{
    public static string Of(string target)
    {
        var text = target.Trim();
        var hash = text.IndexOf('#');
        if (hash >= 0) text = text[..hash];
        text = text.Normalize(NormalizationForm.FormC);
        if (text.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) text = text[..^".md".Length];
        return text.ToUpperInvariant();
    }
}
