// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Kumbu.Core.Tags;

/// <summary>How an asked-for tag matches a note's: ignoring case, and nested (<c>project</c> covers <c>project/kumbu</c>).</summary>
public static class TagMatch
{
    /// <summary>The tag as asked for, with or without '#' and a trailing '/'; null if it can't be a tag.</summary>
    public static string? Normalise(string asked)
    {
        var tag = asked.Trim();
        if (tag.StartsWith('#')) tag = tag[1..];
        tag = tag.TrimEnd('/').Normalize(NormalizationForm.FormC);
        return TagReader.IsTag(tag) ? tag : null;
    }

    /// <summary>Whether <paramref name="tag"/> is <paramref name="asked"/> (normalised) or nested below it.</summary>
    public static bool Covers(string asked, string tag) =>
        tag.StartsWith(asked, StringComparison.OrdinalIgnoreCase)
        && (tag.Length == asked.Length || tag[asked.Length] == '/');
}
