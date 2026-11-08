// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Skills;

namespace Kumbu.Core.Tests.Skills;

public class SkillHashTests
{
    private static (string, ReadOnlyMemory<byte>) F(string path, string content) => (path, System.Text.Encoding.UTF8.GetBytes(content));

    [Fact]
    public void The_hash_is_order_independent() =>
        Assert.Equal(SkillHash.Of([F("SKILL.md", "x"), F("a.py", "y")]), SkillHash.Of([F("a.py", "y"), F("SKILL.md", "x")]));

    [Fact]
    public void The_hash_names_its_algorithm() => Assert.Matches("^sha256:[0-9a-f]{64}$", SkillHash.Of([F("SKILL.md", "x")]));

    [Fact]
    public void A_renamed_file_changes_the_hash() =>
        Assert.NotEqual(SkillHash.Of([F("a.py", "x")]), SkillHash.Of([F("b.py", "x")]));

    [Fact]
    public void Moving_bytes_between_path_and_content_changes_the_hash() =>
        Assert.NotEqual(SkillHash.Of([F("ab", "c")]), SkillHash.Of([F("a", "bc")]));

    [Fact]
    public void A_file_boundary_is_not_ambiguous() =>
        Assert.NotEqual(SkillHash.Of([F("a", "xb\0y")]), SkillHash.Of([F("a", "x"), F("b", "y")]));

    [Fact]
    public void Composed_and_decomposed_paths_hash_alike() =>
        Assert.Equal(SkillHash.Of([F("café.txt", "x")]), SkillHash.Of([F("café.txt", "x")]));

    [Fact]
    public void The_hash_of_a_known_skill_is_pinned() =>
        Assert.Equal("sha256:edb25c1896de64333047015a7d4c3ef40151597385360b0fed50c5b99cd7fb5b", SkillHash.Of([F("SKILL.md", "x")]));
}
