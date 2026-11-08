// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;

namespace Kumbu.Core.Tests.Skills;

public class SkillPathsTests
{
    private static VaultPath P(string value)
    {
        Assert.True(VaultPath.TryParse(value, out var path));
        return path;
    }

    [Theory]
    [InlineData("/Skills/pdf/scripts/run.py", "/Skills/pdf")]
    [InlineData("/Skills/pdf", "/Skills/pdf")]
    [InlineData("/Work/_skills/release-notes/SKILL.md", "/Work/_skills/release-notes")]
    [InlineData("/Work/notes/_skills/x/a.py", null)]
    [InlineData("/Work/_Skills/x/a.py", null)]
    [InlineData("/skills/pdf/a.py", null)]
    [InlineData("/Skills", null)]
    [InlineData("/Work/_skills", null)]
    public void FolderOf_finds_the_skill_folder(string path, string? folder) => Assert.Equal(folder, SkillPaths.FolderOf(P(path))?.Value);

    [Fact] public void Without_a_marked_vault_only__skills_counts() => Assert.Null(SkillPaths.FolderOf(P("/Skills/pdf/a.py"), skillsVault: null));
    [Fact] public void The_marked_vault_counts_under_any_name_it_is_given() => Assert.Equal("/Mine/pdf", SkillPaths.FolderOf(P("/Mine/pdf/a.py"), "Mine")!.Value);
    [Fact] public void The_folder_itself_is_not_inside_it() => Assert.False(SkillPaths.IsInSkillFolder(P("/Skills/pdf")));
    [Fact] public void LocationOf_names_the_location() => Assert.Equal("/Work/_skills", SkillPaths.LocationOf(P("/Work/_skills/x/y/z.md"))!.Value);
}
