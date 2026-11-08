// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Skills;

/// <summary>
/// A skill as a caller sees it: a system skill (<see cref="Folder"/> null) or a folder in a skill location, with where
/// it comes from. <see cref="LastEditor"/> is the label of <c>SKILL.md</c>'s last writer, <see cref="ByOther"/> whether
/// that was another user, <see cref="Source"/> the manifest's <c>kumbu-source</c>, <see cref="Warnings"/> the rules
/// it breaks.
/// </summary>
public sealed record Skill(string Name, VaultPath? Folder, string Description, string? Source, string? LastEditor, bool ByOther, IReadOnlyList<string> Warnings)
{
    public bool IsSystem => Folder is null;

    public string? Vault => Folder?.Vault;

    /// <summary>Whether the frontmatter's origin disagrees with the sealed one (Decision 33); set by the server, never by the core.</summary>
    public bool OriginEdited { get; init; }

    /// <summary>The owner as <c>VaultInfoAsync</c> names her (quoted), for a vault shared with the caller; null for her own.</summary>
    public string? SharedBy { get; init; }

    /// <summary>
    /// One line for a listing: "name (system): description" or "name (/Work/_skills/x, shared by "Alice", last changed by
    /// Bob, another user, from github:…, origin edited): description [warning: …]". A name that breaks the rules is quoted,
    /// control and format characters are stripped and line breaks become spaces, so no line can pass for another origin;
    /// with <paramref name="maxDescription"/> a longer description is cut and ends with "…".
    /// </summary>
    public string Line(int? maxDescription = null)
    {
        var name = Clean(Name);
        var line = new StringBuilder(SkillManifest.IsValidName(Name) ? name : $"\"{name.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        var origin = new List<string>();
        if (Folder is null)
        {
            origin.Add("system");
        }
        else
        {
            origin.Add(Clean(Folder.Value));
            if (SharedBy is not null) origin.Add($"shared by {Clean(SharedBy)}");
            if (LastEditor is not null) origin.Add($"last changed by {Clean(LastEditor)}");
            if (ByOther) origin.Add("another user");
            if (Source is not null) origin.Add($"from {Clean(Source)}");
            if (OriginEdited) origin.Add("origin edited");
        }

        line.Append(" (").AppendJoin(", ", origin).Append(')');
        var description = Clean(Description);
        if (maxDescription is { } max && description.Length > max)
        {
            var cut = Math.Max(0, max - 1);
            if (cut > 0 && char.IsHighSurrogate(description[cut - 1])) cut--;
            description = description[..cut].TrimEnd() + "…";
        }

        if (description.Length > 0) line.Append(": ").Append(description);
        if (Warnings.Count > 0) line.Append(" [warning: ").AppendJoin("; ", Warnings.Select(Clean)).Append(']');
        return line.ToString();
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
