// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Skills;

namespace Kumbu.Core.Tests.Skills;

public class SkillManifestTests
{
    [Fact]
    public void A_good_manifest_has_no_problems()
    {
        var m = SkillManifest.Read("write-as-alice", "---\nname: write-as-alice\ndescription: Writes posts in Alice's voice.\nlicense: MIT\n---\n# Write\n");
        Assert.Empty(m.Problems);
        Assert.Equal("write-as-alice", m.Name);
        Assert.Equal("MIT", m.License);
    }

    [Theory]
    [InlineData("Write-As", "name 'Write-As' must be lowercase letters, digits and hyphens, at most 64 characters")]
    [InlineData("other", "name 'other' must be the folder's name, 'write-as-alice'")]
    public void Bad_names_are_named(string name, string problem) =>
        Assert.Contains(problem, SkillManifest.Read("write-as-alice", $"---\nname: {name}\ndescription: x\n---\n").Problems);

    [Fact]
    public void A_65_character_name_is_too_long()
    {
        var name = new string('a', 65);
        var m = SkillManifest.Read(name, $"---\nname: {name}\ndescription: x\n---\n");
        Assert.Contains($"name '{name}' must be lowercase letters, digits and hyphens, at most 64 characters", m.Problems);
    }

    [Fact]
    public void A_1025_character_description_is_too_long()
    {
        var m = SkillManifest.Read("x", $"---\nname: x\ndescription: {new string('d', 1025)}\n---\n");
        Assert.Contains("description has 1025 characters; at most 1024", m.Problems);
        Assert.Empty(SkillManifest.Read("x", $"---\nname: x\ndescription: {new string('d', 1024)}\n---\n").Problems);
    }

    [Fact]
    public void No_frontmatter_is_named() =>
        Assert.Equal(["SKILL.md has no frontmatter: it starts with ---, then name and description"], SkillManifest.Read("x", "# Just text\n").Problems);

    [Fact]
    public void Yaml_that_doesnt_parse_is_named() =>
        Assert.StartsWith("the frontmatter doesn't parse: ", Assert.Single(SkillManifest.Read("x", "---\nname: [x\n: :\n---\n").Problems));

    [Fact]
    public void A_missing_file_is_named() => Assert.Equal(["SKILL.md is missing"], SkillManifest.Read("x", null).Problems);

    [Fact]
    public void A_missing_name_and_description_are_named()
    {
        var m = SkillManifest.Read("x", "---\nlicense: MIT\n---\n");
        Assert.Equal(["name is missing", "description is missing"], m.Problems);
    }

    [Fact]
    public void Metadata_that_is_not_a_map_is_named() =>
        Assert.Contains("metadata must be a map of keys and values", SkillManifest.Read("x", "---\nname: x\ndescription: y\nmetadata: 3\n---\n").Problems);

    [Fact]
    public void Crlf_and_a_bom_read_alike()
    {
        var lf = "---\nname: x\ndescription: Does x.\nmetadata:\n  author: alice\n---\n# X\n";
        var plain = SkillManifest.Read("x", lf);
        var odd = SkillManifest.Read("x", "\uFEFF" + lf.Replace("\n", "\r\n"));
        Assert.Empty(odd.Problems);
        Assert.Equal(plain.Name, odd.Name);
        Assert.Equal(plain.Description, odd.Description);
        Assert.Equal("alice", odd.Metadata["author"]);
    }

    [Fact]
    public void A_number_as_name_reads_as_its_text()
    {
        var m = SkillManifest.Read("42", "---\nname: 42\ndescription: y\n---\n");
        Assert.Equal("42", m.Name);
        Assert.Empty(m.Problems);
    }

    [Fact]
    public void The_origin_reads_from_metadata()
    {
        var m = SkillManifest.Read("x", "---\nname: x\ndescription: y\nmetadata:\n  kumbu-source: \"github:a/b/x@1\"\n  kumbu-hash: \"sha256:ab\"\n---\n");
        Assert.Equal("github:a/b/x@1", m.Source);
        Assert.Equal("sha256:ab", m.Hash);
    }

    [Fact]
    public void Origin_fields_go_under_metadata_and_leave_every_other_line()
    {
        var before = "---\nname: pdf\ndescription: Reads PDFs.\nmetadata:\n  author: alice\n---\n# PDF\n";
        var after = SkillManifest.WithOrigin(before, "github:alice-example/skills/pdf@0123456789abcdef0123456789abcdef01234567", "sha256:ab");
        Assert.Equal("---\nname: pdf\ndescription: Reads PDFs.\nmetadata:\n  author: \"alice\"\n  kumbu-source: \"github:alice-example/skills/pdf@0123456789abcdef0123456789abcdef01234567\"\n  kumbu-hash: \"sha256:ab\"\n---\n# PDF\n", after);
    }

    [Fact]
    public void Origin_fields_replace_older_ones()
    {
        var first = SkillManifest.WithOrigin("---\nname: pdf\ndescription: Reads PDFs.\n---\n# PDF\n", "github:a/b/pdf@1", "sha256:01");
        Assert.Equal("---\nname: pdf\ndescription: Reads PDFs.\nmetadata:\n  kumbu-source: \"github:a/b/pdf@1\"\n  kumbu-hash: \"sha256:01\"\n---\n# PDF\n", first);
        var second = SkillManifest.WithOrigin(first, "github:a/b/pdf@2", "sha256:02");
        Assert.Equal("---\nname: pdf\ndescription: Reads PDFs.\nmetadata:\n  kumbu-source: \"github:a/b/pdf@2\"\n  kumbu-hash: \"sha256:02\"\n---\n# PDF\n", second);
    }

    [Fact]
    public void Body_is_what_follows_the_frontmatter()
    {
        Assert.Equal("# PDF\ntext\n", SkillManifest.Body("---\nname: pdf\n---\n# PDF\ntext\n"));
        Assert.Equal("# PDF\n", SkillManifest.Body("# PDF\n"));
    }

    [Fact]
    public void A_name_with_a_trailing_newline_is_refused() =>
        Assert.Contains("name 'x\n' must be lowercase letters, digits and hyphens, at most 64 characters",
            SkillManifest.Read("x", "---\nname: \"x\\n\"\ndescription: y\n---\n").Problems);

    [Fact]
    public void Origin_fields_go_under_metadata_after_a_bom_and_with_crlf()
    {
        var lf = "---\nname: pdf\ndescription: Reads PDFs.\n---\n# PDF\n";
        var expected = SkillManifest.WithOrigin(lf, "s", "h");
        Assert.Equal(expected, SkillManifest.WithOrigin("\uFEFF" + lf, "s", "h"));
        Assert.Equal(expected, SkillManifest.WithOrigin(lf.Replace("\n", "\r\n"), "s", "h"));
    }

    [Fact]
    public void Origin_fields_keep_non_scalar_metadata_verbatim()
    {
        var before = "---\nname: pdf\ndescription: y\nmetadata:\n  tags:\n    - a\n    - b\n  nested:\n    deep: 1\n  author: alice\n  kumbu-hash: \"old\"\n---\n# PDF\n";
        var after = SkillManifest.WithOrigin(before, "s", "h");
        Assert.Equal("---\nname: pdf\ndescription: y\nmetadata:\n  tags:\n    - a\n    - b\n  nested:\n    deep: 1\n  author: \"alice\"\n  kumbu-source: \"s\"\n  kumbu-hash: \"h\"\n---\n# PDF\n", after);
    }

    [Fact]
    public void A_flow_map_in_metadata_keeps_its_entries()
    {
        var after = SkillManifest.WithOrigin("---\nname: x\ndescription: y\nmetadata: {tags: [a, b], author: alice}\n---\n", "s", "h");
        var m = SkillManifest.Read("x", after);
        Assert.Empty(m.Problems);
        Assert.Equal("alice", m.Metadata["author"]);
        Assert.Equal("s", m.Source);
        Assert.Contains("[\"a\",\"b\"]", after);
    }

    [Fact]
    public void The_origin_reads_beside_non_scalar_metadata()
    {
        var m = SkillManifest.Read("x", "---\nname: x\ndescription: y\nmetadata:\n  tags: [a]\n  kumbu-source: s\n  kumbu-hash: h\n---\n");
        Assert.Empty(m.Problems);
        Assert.Equal("s", m.Source);
        Assert.Equal("h", m.Hash);
    }
}
