// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tags;

/// <summary>A note's flag: its name (<c>red</c>), whether it stands in the text rather than the frontmatter, and the tag as written
/// (<c>flag/Red</c>).</summary>
public sealed record NoteFlag(string Name, bool InText, string Written);

/// <summary>
/// A flag is one of eight tags below <c>flag/</c>. <c>flag</c> itself and any other tag below it (<c>flag/maybe</c>,
/// <c>flag/red/x</c>) is an ordinary tag.
/// </summary>
public static class Flags
{
    public static IReadOnlyList<string> Names { get; } = ["red", "green", "blue", "purple", "yellow", "white", "done", "important"];

    public static bool IsFlag(string name) => Names.Contains(name, StringComparer.Ordinal);

    public static string Tag(string name) =>
        IsFlag(name) ? $"flag/{name}" : throw new ArgumentException($"'{name}' is no flag.", nameof(name));

    /// <summary>The flag a tag (without '#') stands for, ignoring case, or null.</summary>
    public static string? NameOf(string tag)
    {
        if (!tag.StartsWith("flag/", StringComparison.OrdinalIgnoreCase)) return null;
        var name = tag[5..];
        return Names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The note's first flag in the frontmatter's tags, else its first in the text; null if it has none.</summary>
    public static NoteFlag? Of(string content)
    {
        var tags = TagReader.Of(content);
        if (tags.Count == 0) return null;
        var end = Outline.FrontmatterEnd(Lines.Split(content));
        return First(tags.Where(t => t.Line < end), false) ?? First(tags.Where(t => t.Line >= end), true);
    }

    private static NoteFlag? First(IEnumerable<TagUse> tags, bool inText) =>
        tags.Select(t => NameOf(t.Tag) is { } name ? new NoteFlag(name, inText, t.Tag) : null).FirstOrDefault(f => f is not null);
}
