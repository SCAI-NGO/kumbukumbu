// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Skills;

/// <summary>
/// Where skills live: a skill is a direct subfolder of the location, which is either the vault marked as the
/// skills vault (<c>/Skills</c> by default) or a <c>_skills</c> folder at the root of any vault.
/// <c>skillsVault</c> is the caller's name for the marked vault, null when it has none; names compare ordinally.
/// </summary>
public static class SkillPaths
{
    public const string SkillsVault = "Skills";
    public const string LocationFolder = "_skills";
    public const string Manifest = "SKILL.md";

    /// <summary>The skills location (<c>/&lt;skillsVault&gt;</c> or <c>/&lt;vault&gt;/_skills</c>) the path is, or is below; null elsewhere.</summary>
    public static VaultPath? LocationOf(VaultPath path, string? skillsVault = SkillsVault)
    {
        var segments = path.Segments;
        if (segments.Count == 0) return null;
        if (skillsVault is not null && string.Equals(segments[0], skillsVault, StringComparison.Ordinal)) return VaultPath.Root.Child(segments[0]);
        if (segments.Count >= 2 && string.Equals(segments[1], LocationFolder, StringComparison.Ordinal)) return VaultPath.Root.Child(segments[0]).Child(segments[1]);
        return null;
    }

    /// <summary>The skill's folder the path is, or is below; null for the location itself and for paths outside one.</summary>
    public static VaultPath? FolderOf(VaultPath path, string? skillsVault = SkillsVault)
    {
        if (LocationOf(path, skillsVault) is not { } location) return null;
        var depth = location.Segments.Count;
        return path.Segments.Count > depth ? location.Child(path.Segments[depth]) : null;
    }

    /// <summary>Whether the path is strictly below a skill's folder (the folder itself is not).</summary>
    public static bool IsInSkillFolder(VaultPath path, string? skillsVault = SkillsVault) =>
        FolderOf(path, skillsVault) is { } folder && path.Segments.Count > folder.Segments.Count;
}
