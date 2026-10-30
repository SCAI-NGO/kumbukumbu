// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Paths;

public class VaultPathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/Work", "/Work")]
    [InlineData("/Work/", "/Work")]
    [InlineData("//Work//a.md", "/Work/a.md")]
    [InlineData("/Work/./a.md", "/Work/a.md")]
    [InlineData("/Work/x/../a.md", "/Work/a.md")]
    [InlineData("/../..", "/")]
    [InlineData("/Work/Meeting Notes.md", "/Work/Meeting Notes.md")]
    public void TryParse_normalizes_input(string input, string expected)
    {
        Assert.True(VaultPath.TryParse(input, out var path));
        Assert.Equal(expected, path.Value);
        Assert.Equal(expected, path.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Work/a.md")]
    [InlineData("a.md")]
    [InlineData(".")]
    [InlineData("  ")]
    public void TryParse_rejects_relative_or_empty(string input)
    {
        Assert.False(VaultPath.TryParse(input, out var path));
        Assert.Null(path);
    }

    [Fact]
    public void TryParse_rejects_lone_surrogates_without_throwing()
    {
        // Built in the body: xunit's theory data serialization would replace lone surrogates with U+FFFD.
        foreach (var input in new[] { "/a\uD800", "/a\uDC00b", "/\uD800\uD800" })
        {
            Assert.False(VaultPath.TryParse(input, out var path));
            Assert.Null(path);
        }

        Assert.True(VaultPath.TryParse("/a\uD83D\uDE00", out var emoji));
        Assert.Equal("/a\uD83D\uDE00", emoji.Value);
    }

    [Fact]
    public void Root_has_expected_shape()
    {
        var root = VaultPath.Root;
        Assert.True(root.IsRoot);
        Assert.Null(root.Vault);
        Assert.Empty(root.Segments);
        Assert.Equal("/", root.Name);
        Assert.False(root.IsNote);
    }

    [Fact]
    public void Note_path_exposes_parts()
    {
        Assert.True(VaultPath.TryParse("/Work/projects/x.md", out var path));
        Assert.Equal("Work", path.Vault);
        Assert.Equal("x.md", path.Name);
        Assert.True(path.IsNote);
        Assert.Equal(["Work", "projects", "x.md"], path.Segments);
        Assert.False(path.IsRoot);
    }

    [Fact]
    public void Uppercase_extension_is_not_a_note()
    {
        Assert.True(VaultPath.TryParse("/Work/x.MD", out var path));
        Assert.False(path.IsNote);
    }

    [Fact]
    public void Child_appends_segment()
    {
        Assert.Equal("/Work/a.md", VaultPath.Root.Child("Work").Child("a.md").Value);
    }

    [Fact]
    public void Equality_is_case_sensitive()
    {
        Assert.True(VaultPath.TryParse("/Work", out var a));
        Assert.True(VaultPath.TryParse("/work", out var b));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Nfd_and_nfc_inputs_are_equal()
    {
        var nfc = "/Ideas/Überlegungen.md";
        var nfd = "/Ideas/Überlegungen.md";
        Assert.NotEqual(nfc, nfd);
        Assert.True(VaultPath.TryParse(nfc, out var a));
        Assert.True(VaultPath.TryParse(nfd, out var b));
        Assert.Equal(a, b);
        Assert.Equal(nfc, b.Value);
    }

    [Fact]
    public void A_folder_marker_is_an_attachment()
    {
        Assert.True(VaultPath.TryParse("/Work/Drafts/.vaultkeep", out var marker));
        Assert.True(marker.IsAttachment);
    }
}
