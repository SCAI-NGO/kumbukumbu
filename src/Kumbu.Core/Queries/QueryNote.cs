// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Queries;

/// <summary>A file as a query reads it: its path in the vault, its text, its typed properties, its size in bytes and its
/// times (null where they aren't known). A note is a <c>.md</c> file; any other file (a <c>.base</c> file, an attachment)
/// is a row too, with no properties and no tags.</summary>
/// <param name="size">The size in bytes; left out, the UTF-8 length of the text, as a note's is.</param>
public sealed class QueryNote(string path, string content, NoteProperties properties, long? size = null, DateTime? created = null, DateTime? modified = null)
{
    public string Path { get; } = path;

    public string Content { get; } = content;

    public NoteProperties Properties { get; } = properties;

    public bool IsNote => IsNotePath(Path);

    public long Size { get; } = size ?? Encoding.UTF8.GetByteCount(content);

    public DateTime? Created { get; } = created;

    public DateTime? Modified { get; } = modified;

    /// <summary>Its tags as Obsidian's file.tags gives them: with their '#', the text's before the frontmatter's (finding 6),
    /// each once (ignoring case), as first written; none for a file that isn't a note.</summary>
    public IReadOnlyList<string> Tags => tags ??= IsNote ? OfNote(Content) : [];

    private IReadOnlyList<string>? tags;

    public static bool IsNotePath(string path) => path.EndsWith(".md", StringComparison.Ordinal);

    private static List<string> OfNote(string content)
    {
        var end = Outline.FrontmatterEnd(Lines.Split(content));
        return TagReader.Of(content).OrderBy(t => t.Line < end ? 1 : 0).Select(t => "#" + t.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>A note's file, as <c>file</c> and <c>this.file</c> give it: two are equal when their paths are.</summary>
public sealed record FileValue(QueryNote Note) : Value
{
    public bool Equals(FileValue? other) => other is not null && other.Note.Path == Note.Path;

    public override int GetHashCode() => Note.Path.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A note as <c>this</c> gives it: <c>this.file</c>, <c>this.note</c>, and its properties by name.</summary>
public sealed record NoteRefValue(QueryNote Note) : Value;

public sealed record RegexValue(System.Text.RegularExpressions.Regex Regex) : Value;
