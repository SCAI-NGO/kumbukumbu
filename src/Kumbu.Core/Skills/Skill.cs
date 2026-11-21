// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Skills;

/// <summary>
/// A skill as a caller sees it: a system skill (<see cref="Folder"/> null) or a folder in a skill location, with where
/// it comes from. <see cref="LastEditor"/> is the label of the last writer of any of its files, <see cref="ByOther"/>
/// whether another user last wrote one of them or wrote a line still in one, <see cref="Source"/> the manifest's
/// <c>kumbu-source</c> (a claim until <see cref="SourceVerified"/>), <see cref="Warnings"/> the rules it breaks.
/// </summary>
public sealed record Skill(string Name, VaultPath? Folder, string Description, string? Source, string? LastEditor, bool ByOther, IReadOnlyList<string> Warnings)
{
    public bool IsSystem => Folder is null;

    public string? Vault => Folder?.Vault;

    /// <summary>Whether it is a start-up skill: its manifest marks it for the start of every session (<c>metadata</c>
    /// <c>kumbu-when: session-start</c>) and it is a system skill or one in the caller's own vault where no other user
    /// last wrote a file or wrote a line still in one. The handshake instructions name such a skill in both modes.</summary>
    public bool AtStart { get; init; }

    /// <summary>Whether the frontmatter's origin disagrees with the sealed one (Decision 33); set by the server, never by the core.</summary>
    public bool OriginEdited { get; init; }

    /// <summary>Whether a sealed origin agrees with <see cref="Source"/>: only then is the skill "from" it. Anyone who may write
    /// the folder can write a <c>kumbu-source</c>, so without it the source is "unverified origin". Set by the server.</summary>
    public bool SourceVerified { get; init; }

    /// <summary>The owner as <c>VaultInfoAsync</c> names her (quoted), for a vault shared with the caller; null for her own.</summary>
    public string? SharedBy { get; init; }

    /// <summary>
    /// One line for a listing: "name (system): description" or "name (/Work/_skills/x, shared by "Alice", last changed by
    /// Bob, another user, from github:…): description [warning: …]", where "from …" gives way to "origin edited" or
    /// "unverified origin" unless the sealed origin agrees. A name that breaks the rules is quoted,
    /// control and format characters are stripped and line breaks become spaces, and a folder, label or source that holds a
    /// bracket or a comma is quoted, so no line can pass for another origin;
    /// with <paramref name="maxDescription"/> a longer description, a longer name or folder path, and the warnings (all
    /// together) are each cut to it and end with "…".
    /// </summary>
    public string Line(int? maxDescription = null)
    {
        var name = Cut(Clean(Name), maxDescription);
        var line = new StringBuilder(SkillManifest.IsValidName(Name) ? name : Quoted(name));
        var origin = new List<string>();
        if (Folder is null)
        {
            origin.Add("system");
        }
        else
        {
            origin.Add(Part(Cut(Clean(Folder.Value), maxDescription)));
            if (SharedBy is not null) origin.Add($"shared by {Part(Clean(SharedBy))}");
            if (LastEditor is not null) origin.Add($"last changed by {Part(Clean(LastEditor))}");
            if (ByOther) origin.Add("another user");
            if (OriginEdited) origin.Add("origin edited");
            else if (Source is not null) origin.Add(SourceVerified ? $"from {Part(Clean(Source))}" : "unverified origin");
        }

        line.Append(" (").AppendJoin(", ", origin).Append(')');
        var description = Cut(Clean(Description), maxDescription);
        if (description.Length > 0) line.Append(": ").Append(description);
        if (Warnings.Count > 0) line.Append(" [warning: ").Append(Cut(string.Join("; ", Warnings.Select(Clean)), maxDescription)).Append(']');
        return line.ToString();
    }

    private static string Quoted(string text) => $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>A part of the origin that a person named (a folder, a label, a source's path), quoted where it holds what
    /// could close the origin or start another part of it (a quote alone can't: the owner's name is quoted already), so "Bob · k), from github:…" can't pass for the server's own.</summary>
    private static string Part(string text) => text.AsSpan().IndexOfAny("(),") >= 0 ? Quoted(text) : text;

    private static string Cut(string text, int? max)
    {
        if (max is not { } bound || text.Length <= bound) return text;
        var cut = Math.Max(0, bound - 1);
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>Line breaks as spaces, controls (Cc) and format characters (Cf: bidi and zero-width marks) dropped.</summary>
    internal static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var rune in text.Replace("\r\n", "\n").EnumerateRunes())
        {
            if (rune.Value is '\n' or '\r' or '\t' or 0x85 or 0x2028 or 0x2029) clean.Append(' ');
            else if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format)) clean.Append(rune.ToString());
        }

        return clean.ToString().Trim();
    }
}

/// <summary>A file of a skill, by its path relative to the skill's folder.</summary>
public sealed record SkillFile(string Path, bool IsNote);
