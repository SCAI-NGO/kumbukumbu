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
    [InlineData("/a/x.docx", new byte[] { 0x50, 0x4B }, null)]
    [InlineData("/a/Makefile", new byte[] { 0x00, 0xFF, 0xFE }, null)]
    public void Problem_checks_extension_and_magic_bytes(string path, byte[] head, string? expected) =>
        Assert.Equal(expected, AttachmentFormat.Problem(P(path), head));

    [Theory]
    [InlineData("/a/x.PNG", true)]
    [InlineData("/a/x.Pdf", true)]
    [InlineData("/a/x.webp", true)]
    [InlineData("/a/x.svg", true)]
    [InlineData("/a/x.md", false)]
    [InlineData("/a/x.heic", true)]
    [InlineData("/a/png", true)]
    [InlineData("/a/x.MD", true)]
    [InlineData("/a/v1.2", true)]
    [InlineData("/", false)]
    public void IsAttachment_is_every_path_but_the_root_and_notes(string path, bool expected) =>
        Assert.Equal(expected, P(path).IsAttachment);

    [Theory]
    [InlineData("/Skills/pdf/scripts/run.py", AttachmentKind.Text)]
    [InlineData("/Work/_skills/x/LICENSE", AttachmentKind.Text)]
    [InlineData("/Work/_skills/x/makefile", AttachmentKind.Text)]
    [InlineData("/Skills/pdf/logo.SVG", AttachmentKind.Text)]
    [InlineData("/Skills/pdf/shot.png", AttachmentKind.Image)]
    public void Skill_folders_keep_text_files(string path, AttachmentKind kind) => Assert.Equal(kind, AttachmentFormat.ForPath(P(path))!.Kind);

    [Theory]
    [InlineData("/Work/run.py")] [InlineData("/Skills/run.py")] [InlineData("/Work/_skills/x/scripts")] [InlineData("/Skills/pdf/a.exe")]
    [InlineData("/Work/report.docx")] [InlineData("/Work/data.csv")] [InlineData("/Work/Makefile")] [InlineData("/Work/archive.tar.gz")]
    [InlineData("/Work/page.html")] [InlineData("/Work/x.MD")] [InlineData("/Work/.vaultkeep2")]
    public void Elsewhere_any_other_name_is_a_file(string path) => Assert.Equal(AttachmentFormat.File, AttachmentFormat.ForPath(P(path)));

    [Theory]
    [InlineData("/Work/x.md")] [InlineData("/Skills/pdf/SKILL.md")] [InlineData("/")]
    public void Notes_and_the_root_are_no_attachment(string path) => Assert.Null(AttachmentFormat.ForPath(P(path)));

    [Theory]
    [InlineData("/Work/x.png", ".png")] [InlineData("/Work/x.JPEG", ".jpeg")] [InlineData("/Work/x.svg", ".svg")] [InlineData("/Work/x.pdf", ".pdf")]
    public void Known_formats_keep_their_format(string path, string extension) => Assert.Equal(extension, AttachmentFormat.ForPath(P(path))!.Extension);

    [Fact]
    public void A_file_is_its_own_kind_and_no_format_listed_to_users()
    {
        Assert.Equal(AttachmentKind.File, AttachmentFormat.File.Kind);
        Assert.DoesNotContain(AttachmentFormat.File, AttachmentFormat.All);
        Assert.Equal("file", AttachmentFormat.KindOf("/Work/report.docx"));
        Assert.Equal("file", AttachmentFormat.KindOf("/Work/Makefile"));
        Assert.Equal("note", AttachmentFormat.KindOf("/Work/x.md"));
    }

    [Theory]
    [InlineData("a,b\n1,2\n")]
    [InlineData("\uFEFFhé, with its BOM")]
    [InlineData("")]
    public void A_file_of_utf8_text_is_kept_as_text(string content) =>
        Assert.Equal(AttachmentFormat.TextMediaType, AttachmentFormat.MediaTypeOf(System.Text.Encoding.UTF8.GetBytes(content)));

    [Theory]
    [InlineData(new byte[] { 0xC3, 0x28 })]
    [InlineData(new byte[] { 0x61, 0x00, 0x62 })]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 })]
    public void Any_other_file_is_kept_as_bytes(byte[] content) => Assert.Equal("application/octet-stream", AttachmentFormat.MediaTypeOf(content));

    [Fact]
    public void Text_over_15_mebibytes_is_kept_as_bytes()
    {
        var content = new byte[AttachmentFormat.MaxBytes + 1];
        Array.Fill(content, (byte)'a');
        Assert.Equal("application/octet-stream", AttachmentFormat.MediaTypeOf(content));
        Assert.Equal(AttachmentFormat.TextMediaType, AttachmentFormat.MediaTypeOf(content.AsSpan(0, (int)AttachmentFormat.MaxBytes)));
    }

    [Theory]
    [InlineData("/Work/report.docx", "/Work/old/report.v2.docx")]
    [InlineData("/Work/report.docx", "/Work/Makefile")]
    [InlineData("/Work/data.csv", "/Work/data.txt")]
    public void A_file_takes_any_name_but_a_notes_or_a_known_formats(string source, string target) =>
        Assert.Null(AttachmentFormat.RenameProblem(P(source), AttachmentFormat.OctetStream, P(target)));

    [Theory]
    [InlineData("/Work/report.docx", "/Work/report.png", "report.docx is a file, not a PNG image; its new name can't end in .png (a new name doesn't change what the file is)")]
    [InlineData("/Work/report.docx", "/Work/report.pdf", "report.docx is a file, not a PDF document; its new name can't end in .pdf (a new name doesn't change what the file is)")]
    [InlineData("/Work/report.docx", "/Work/report.svg", "report.docx is a file, not an SVG image; its new name can't end in .svg (a new name doesn't change what the file is)")]
    [InlineData("/Work/report.docx", "/Work/report.md", "report.docx is a file, not a note; its new name can't end in .md (a new name doesn't change what the file is)")]
    [InlineData("/Work/data.csv", "/Work/D/.vaultkeep", "data.csv is a file; only a folder's marker is named .vaultkeep")]
    [InlineData("/Work/run.py", "/Skills/pdf/run.py", "run.py isn't text; " + AttachmentFormat.SkillFolderMessage)]
    public void A_file_cant_take_a_notes_or_a_known_formats_name(string source, string target, string expected) =>
        Assert.Equal(expected, AttachmentFormat.RenameProblem(P(source), AttachmentFormat.OctetStream, P(target)));

    [Fact]
    public void A_text_file_enters_a_skills_folder_under_a_text_name()
    {
        Assert.Null(AttachmentFormat.RenameProblem(P("/Work/run.py"), AttachmentFormat.TextMediaType, P("/Skills/pdf/run.py")));
        Assert.Equal("run.dat: " + AttachmentFormat.SkillFolderMessage, AttachmentFormat.RenameProblem(P("/Work/run.py"), AttachmentFormat.TextMediaType, P("/Skills/pdf/run.dat")));
    }

    [Theory]
    [InlineData("/Work/x.png", "/Work/x.docx", "x.png is a PNG image; its new name must end in .png (a new name doesn't change what the file is)")]
    [InlineData("/Work/x.pdf", "/Work/x", "x.pdf is a PDF document; its new name must end in .pdf (a new name doesn't change what the file is)")]
    [InlineData("/Work/x.webp", "/Work/x.bin", "x.webp is a WebP image; its new name must end in .webp (a new name doesn't change what the file is)")]
    [InlineData("/Work/x.jpg", "/Work/y.jpeg", null)]
    public void A_known_format_doesnt_become_a_file(string source, string target, string? expected) =>
        Assert.Equal(expected, AttachmentFormat.RenameProblem(P(source), AttachmentFormat.ForPath(P(source))!.MediaType, P(target)));

    [Fact]
    public void Text_is_utf8_without_nul()
    {
        Assert.Null(AttachmentFormat.Problem(P("/Skills/pdf/a.py"), "print('hé')\n"u8));
        Assert.Equal("a.py isn't UTF-8 text", AttachmentFormat.Problem(P("/Skills/pdf/a.py"), new byte[] { 0xC3, 0x28 }));
        Assert.Equal("a.py contains a NUL byte; a skill's folder keeps text only", AttachmentFormat.Problem(P("/Skills/pdf/a.py"), "a\0b"u8));
        Assert.Null(AttachmentFormat.Problem(P("/Work/a.py"), "x\0"u8));
    }

    [Fact]
    public void A_skills_folder_keeps_no_other_file()
    {
        var refusal = $": {AttachmentFormat.SkillFolderMessage}";
        Assert.Equal("tool.bin" + refusal, AttachmentFormat.Problem(P("/Skills/pdf/tool.bin"), "x"u8));
        Assert.Equal("notes" + refusal, AttachmentFormat.Problem(P("/Work/_skills/x/notes"), "x"u8));
        Assert.Equal("report.docx" + refusal, AttachmentFormat.RenameProblem(P("/Work/report.docx"), AttachmentFormat.TextMediaType, P("/Skills/pdf/report.docx")));
        // The skill's folder itself and the location hold no files of a skill.
        Assert.Null(AttachmentFormat.Problem(P("/Work/_skills/notes"), "x"u8));
        Assert.Equal("x.md is a note's name (it ends in .md); notes are written as text, not uploaded", AttachmentFormat.Problem(P("/Work/x.md"), "x"u8));
    }

    [Fact]
    public void A_text_file_leaves_a_skills_folder_as_a_file()
    {
        Assert.Null(AttachmentFormat.RenameProblem(P("/Skills/pdf/a.py"), AttachmentFormat.TextMediaType, P("/Skills/pdf/b.txt")));
        Assert.Null(AttachmentFormat.RenameProblem(P("/Skills/pdf/a.py"), AttachmentFormat.TextMediaType, P("/Skills/a.py")));
        Assert.Null(AttachmentFormat.RenameProblem(P("/Skills/pdf/a.py"), AttachmentFormat.TextMediaType, P("/Work/a.py")));
        Assert.Equal("a.py is a text file, not a PNG image; its new name can't end in .png (a new name doesn't change what the file is)",
            AttachmentFormat.RenameProblem(P("/Skills/pdf/a.py"), AttachmentFormat.TextMediaType, P("/Work/a.png")));
        Assert.Equal("a.py is a text file, not a note; its new name can't end in .md (a new name doesn't change what the file is)",
            AttachmentFormat.RenameProblem(P("/Skills/pdf/a.py"), AttachmentFormat.TextMediaType, P("/Work/a.md")));
    }

    [Fact]
    public void KindOf_a_full_path_says_text()
    {
        Assert.Equal("text", AttachmentFormat.KindOf("/Skills/pdf/a.py"));
        Assert.Equal("file", AttachmentFormat.KindOf("a.py"));
    }

    [Fact] public void A_text_summary_says_text() => Assert.Equal("text 2 KB", AttachmentText.Summary(new AttachmentState(AttachmentFormat.TextMediaType, 2048, null, null, "v", "b")));

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
    public void The_marker_is_no_format_listed_to_users() => Assert.DoesNotContain(AttachmentFormat.Marker, AttachmentFormat.All);

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
        Assert.Null(AttachmentFormat.RenameProblem(P("/W/D/.vaultkeep"), FolderMarker.MediaType, P("/W/E/.vaultkeep")));
        Assert.Equal(".vaultkeep keeps its name", AttachmentFormat.RenameProblem(P("/W/D/.vaultkeep"), FolderMarker.MediaType, P("/W/D/x.png")));
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
