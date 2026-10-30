// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Attachments;

public class AttachmentFormatTests
{
    private static VaultPath P(string value)
    {
        Assert.True(VaultPath.TryParse(value, out var path));
        return path;
    }

    [Theory]
    [InlineData("/a/x.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, null)]
    [InlineData("/a/x.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, null)]
    [InlineData("/a/x.JPG", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, null)]
    [InlineData("/a/x.jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }, null)]
    [InlineData("/a/x.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 }, null)]
    [InlineData("/a/x.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, null)]
    [InlineData("/a/x.webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, null)]
    [InlineData("/a/x.webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56, 0x45 }, "x.webp isn't a WebP image (its content doesn't match its name)")]
    [InlineData("/a/x.webp", new byte[] { 0x52, 0x49, 0x46, 0x46 }, "x.webp isn't a WebP image (its content doesn't match its name)")]
    [InlineData("/a/x.png", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "x.png isn't a PNG image (its content doesn't match its name)")]
    [InlineData("/a/x.pdf", new byte[0], "x.pdf isn't a PDF document (its content doesn't match its name)")]
    [InlineData("/a/x.docx", new byte[] { 0x50, 0x4B }, "Kumbu² keeps notes, PDFs and images (PNG, JPEG, GIF, WebP) only")]
    public void Problem_checks_extension_and_magic_bytes(string path, byte[] head, string? expected) =>
        Assert.Equal(expected, AttachmentFormat.Problem(P(path), head));

    [Theory]
    [InlineData("/a/x.PNG", true)]
    [InlineData("/a/x.Pdf", true)]
    [InlineData("/a/x.webp", true)]
    [InlineData("/a/x.md", false)]
    [InlineData("/a/x.heic", false)]
    [InlineData("/a/png", false)]
    [InlineData("/", false)]
    public void IsAttachment_follows_the_six_extensions(string path, bool expected) =>
        Assert.Equal(expected, P(path).IsAttachment);

    [Fact]
    public void The_limit_is_15_mebibytes() => Assert.Equal(15_728_640, AttachmentFormat.MaxBytes);

    [Fact]
    public void Of_bytes_is_sha256_prefix_without_newline_normalisation()
    {
        Assert.Equal(VersionToken.Length, VersionToken.Of("abc"u8).Length);
        Assert.Equal("ba7816bf8f01", VersionToken.Of("abc"u8));
        Assert.NotEqual(VersionToken.Of("a\n"), VersionToken.Of("a\r\n"u8));
    }

    [Theory]
    [InlineData(".vaultkeep", true)]
    [InlineData("x.vaultkeep", false)]
    [InlineData(".VaultKeep", false)]
    [InlineData(".vaultkeep.md", false)]
    public void Only_the_exact_name_is_the_folder_marker(string name, bool marker)
    {
        Assert.Equal(marker, AttachmentFormat.ForName(name) == AttachmentFormat.Marker);
        Assert.Equal(marker, AttachmentFormat.KindOf("/W/D/" + name) == "folder");
    }

    [Fact]
    public void The_marker_is_no_format_listed_to_users()
    {
        Assert.DoesNotContain(AttachmentFormat.Marker, AttachmentFormat.All);
        Assert.Equal("Kumbu² keeps notes, PDFs and images (PNG, JPEG, GIF, WebP) only", AttachmentFormat.UnsupportedMessage);
    }

    [Fact]
    public void A_marker_must_be_empty()
    {
        Assert.Null(AttachmentFormat.Problem(P("/W/D/.vaultkeep"), []));
        Assert.Equal(
            "A .vaultkeep keeps its folder and holds nothing; send it empty",
            AttachmentFormat.Problem(P("/W/D/.vaultkeep"), new byte[] { 0x20 }));
    }

    [Fact]
    public void The_marker_version_is_the_token_of_no_bytes() => Assert.Equal("e3b0c44298fc", FolderMarker.Version);

    [Fact]
    public void A_marker_is_named_as_its_folder()
    {
        Assert.True(FolderMarker.Is(P("/W/D/.vaultkeep")));
        Assert.False(FolderMarker.Is(P("/W/D/x.md")));
        Assert.Equal("/W/D/.vaultkeep", FolderMarker.In(P("/W/D")).Value);
        Assert.Equal("/W/D", FolderMarker.FolderOf(P("/W/D/.vaultkeep")).Value);
        Assert.Equal("/W/D/", FolderMarker.Shown(P("/W/D/.vaultkeep")));
        Assert.Equal("/W/D/x.md", FolderMarker.Shown(P("/W/D/x.md")));
        Assert.Equal("folder", AttachmentText.Summary(FolderMarker.State("blob")));
    }

    [Fact]
    public void A_marker_keeps_its_name_and_nothing_else_is_renamed_to_it()
    {
        Assert.Null(AttachmentFormat.RenameProblem(P("/W/D/.vaultkeep"), P("/W/E/.vaultkeep")));
        Assert.Equal(".vaultkeep keeps its name", AttachmentFormat.RenameProblem(P("/W/D/.vaultkeep"), P("/W/D/x.png")));
    }

    [Fact]
    public void NameProblem_lets_a_marker_through_only_as_the_last_segment()
    {
        Assert.Null(NoteNames.NameProblem(P("/Work/D/.vaultkeep")));
        Assert.NotNull(NoteNames.NameProblem(P("/Work/.vaultkeep/a.md")));
        Assert.NotNull(NoteNames.NameProblem(P("/Work/.x/.vaultkeep")));
        Assert.NotNull(NoteNames.NameProblem(P("/Work/D/.vaultkeep2")));
    }
}
