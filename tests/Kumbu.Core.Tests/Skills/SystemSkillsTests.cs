// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Skills;

namespace Kumbu.Core.Tests.Skills;

public class SystemSkillsTests
{
    private static string Skill(string name) => SystemSkills.Find(name)!.Files[0].Content;

    [Fact]
    public void Create_skill_is_built_in()
    {
        var skill = SystemSkills.Find("create-skill");
        Assert.NotNull(skill);
        Assert.Equal("SKILL.md", skill.Files[0].Path);
        Assert.Contains(SystemSkills.All, s => s.Name == "create-skill");
    }

    [Fact]
    public void Every_system_skill_follows_the_rules()
    {
        Assert.NotEmpty(SystemSkills.All);
        foreach (var s in SystemSkills.All)
        {
            Assert.Equal("SKILL.md", s.Files[0].Path);
            Assert.Empty(SkillManifest.Read(s.Name, s.Files[0].Content).Problems);
        }
    }

    [Fact]
    public void Create_skill_says_what_to_do_without_write() =>
        Assert.Contains("give the user each file's path and content", Skill("create-skill"));

    [Fact]
    public void Create_skill_ends_with_skills_check() =>
        Assert.Contains("Run sh `skills check <folder>` and fix what it names.", Skill("create-skill"));

    [Fact]
    public void System_skills_name_no_organisation()
    {
        foreach (var s in SystemSkills.All)
        {
            foreach (var (_, content) in s.Files)
            {
                Assert.DoesNotContain("Anthropic", content);
                Assert.DoesNotContain("Claude", content);
                Assert.DoesNotContain("GitHub", content);
            }
        }
    }
}
