// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Sh;

public class GlobTests
{
    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private static Glob G(string pattern) =>
        Glob.TryParse(pattern, out var glob, out var error) ? glob : throw new InvalidOperationException(error);

    private static bool Keeps(string pattern, string path, string root = "/SWE") => Glob.Keeps([G(pattern)], P(path), P(root));

    [Fact]
    public void Exclude_with_double_star_drops_a_folder_at_any_depth()
    {
        Assert.True(G("!**/Memory/**").Excludes);
        Assert.False(Keeps("!**/Memory/**", "/SWE/Kumbu/Memory/a.md"));
        Assert.True(Keeps("!**/Memory/**", "/SWE/Kumbu/a.md"));
    }

    [Fact]
    public void Exclude_without_slash_prunes_by_any_folder_name() =>
        Assert.False(Keeps("!Memory", "/SWE/Kumbu/Memory/a.md"));

    [Fact]
    public void Include_without_slash_tests_the_file_name()
    {
        Assert.True(Keeps("*.md", "/SWE/Kumbu/Ideas/x.md"));
        Assert.False(Keeps("*.md", "/SWE/x.pdf"));
    }

    [Fact]
    public void Pattern_with_slash_is_anchored_and_stars_stay_in_a_segment()
    {
        Assert.True(Keeps("Kumbu/*.md", "/SWE/Kumbu/a.md"));
        Assert.False(Keeps("Kumbu/*.md", "/SWE/Kumbu/Ideas/a.md"));
        Assert.False(Keeps("Kumbu/*.md", "/SWE/Other/Kumbu/a.md"));
        Assert.True(Keeps("Kumbu/**", "/SWE/Kumbu/Ideas/a.md"));
        Assert.True(Keeps("/Kumbu/*.md", "/SWE/Kumbu/a.md"));
    }

    [Fact]
    public void Question_mark_and_classes()
    {
        Assert.True(Keeps("?.md", "/SWE/a.md"));
        Assert.False(Keeps("?.md", "/SWE/ab.md"));
        Assert.True(Keeps("[ab].md", "/SWE/b.md"));
        Assert.False(Keeps("[ab].md", "/SWE/c.md"));
        Assert.False(Keeps("[!ab].md", "/SWE/a.md"));
        Assert.True(Keeps("[!ab].md", "/SWE/c.md"));
    }

    [Fact]
    public void Root_of_the_whole_tree_sees_the_vault_name() =>
        Assert.True(Keeps("SWE/Kumbu/**", "/SWE/Kumbu/a.md", "/"));

    [Fact]
    public void Includes_and_excludes_combine()
    {
        Glob[] globs = [G("*.md"), G("!Memory")];
        Assert.True(Glob.Keeps(globs, P("/SWE/a.md"), P("/SWE")));
        Assert.False(Glob.Keeps(globs, P("/SWE/Memory/a.md"), P("/SWE")));
        Assert.False(Glob.Keeps(globs, P("/SWE/a.pdf"), P("/SWE")));
    }

    [Fact]
    public void Unclosed_class_is_an_error()
    {
        Assert.False(Glob.TryParse("[ab", out _, out var error));
        Assert.Equal("rg: bad glob '[ab': unclosed [", error);
    }

    [Theory]
    [InlineData("[z-a].md")]
    [InlineData("[a-\\].md")]
    public void Reversed_class_range_is_an_error(string pattern)
    {
        Assert.False(Glob.TryParse(pattern, out _, out var error));
        Assert.Equal($"rg: bad glob '{pattern}': invalid class", error);
    }

    [Theory]
    [InlineData("!**/Memory")]
    [InlineData("!Kumbu/Memory")]
    [InlineData("!/Kumbu/Memory")]
    [InlineData("!Memory/")]
    [InlineData("!Kumbu/Memory/")]
    public void Anchored_or_folder_excludes_prune_the_folder(string pattern)
    {
        Assert.False(Keeps(pattern, "/SWE/Kumbu/Memory/m.md"));
        Assert.True(Keeps(pattern, "/SWE/Kumbu/a.md"));
    }

    [Fact]
    public void Folders_only_pattern_leaves_a_file_of_that_name()
    {
        Assert.True(Keeps("!Memory/", "/SWE/Kumbu/Memory"));
        Assert.False(Keeps("!Memory", "/SWE/Kumbu/Memory"));
    }

    [Fact]
    public void Double_star_slash_also_matches_at_the_root() =>
        Assert.False(Keeps("!**/Memory/**", "/SWE/Memory/a.md"));

    [Fact]
    public void Braces_are_alternatives()
    {
        Assert.True(Keeps("*.{md,pdf}", "/SWE/a.pdf"));
        Assert.True(Keeps("*.{md,pdf}", "/SWE/a.md"));
        Assert.False(Keeps("*.{md,pdf}", "/SWE/a.png"));
        Assert.True(Keeps("{Kumbu,Other}/*.md", "/SWE/Other/b.md"));
    }

    [Fact]
    public void Bad_braces_are_errors()
    {
        Assert.False(Glob.TryParse("{a,{b}}", out _, out var nested));
        Assert.Equal("rg: bad glob '{a,{b}}': nested {}", nested);
        Assert.False(Glob.TryParse("{a,b", out _, out var unclosed));
        Assert.Equal("rg: bad glob '{a,b': unclosed {", unclosed);
    }

    [Fact]
    public void Classes_never_match_a_slash()
    {
        Assert.True(Keeps("[!-a].md", "/SWE/b.md"));
        Assert.False(Keeps("[!-a].md", "/SWE/-.md"));
        Assert.False(Keeps("[!-a].md", "/SWE/a.md"));
        Assert.False(Keeps("Kumbu[+-0]a.md", "/SWE/Kumbu/a.md"));
        Assert.True(Keeps("[a-]x", "/SWE/-x"));
    }

    [Theory]
    [InlineData("[")]
    [InlineData("a[")]
    [InlineData("[!")]
    [InlineData("{[}")]
    [InlineData("{a,[}")]
    public void Class_opened_at_the_end_is_unclosed(string pattern)
    {
        Assert.False(Glob.TryParse(pattern, out _, out var error));
        Assert.Equal($"rg: bad glob '{pattern}': unclosed [", error);
    }

    [Fact]
    public void Backslash_in_a_class_is_literal_before_a_trailing_dash()
    {
        Assert.True(Keeps("[\\-]x", "/SWE/\\x"));
        Assert.True(Keeps("[\\-]x", "/SWE/-x"));
        Assert.False(Keeps("[\\-]x", "/SWE/ax"));
    }
}
