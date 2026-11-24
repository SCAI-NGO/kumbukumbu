// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Tags;

namespace Kumbu.Core.Queries;

/// <summary>A note as a query reads it: its path in the vault, its text and its typed properties.</summary>
public sealed class QueryNote(string path, string content, NoteProperties properties)
{
    public string Path { get; } = path;

    public string Content { get; } = content;

    public NoteProperties Properties { get; } = properties;

    /// <summary>Its tags as Obsidian reads them, frontmatter first, each once (ignoring case), as first written.</summary>
    public IReadOnlyList<string> Tags => tags ??= TagReader.Of(Content).Select(t => t.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private IReadOnlyList<string>? tags;
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
