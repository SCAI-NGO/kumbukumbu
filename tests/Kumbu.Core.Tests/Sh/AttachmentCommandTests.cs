// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>The shell's view of attachments: listed, described, moved, removed and brought back; never searched.</summary>
public class AttachmentCommandTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly InMemoryNoteStore _store = new(
        ("/Work/plan.md", "Layout: ![[shot.png]] and [report](/Docs/report.pdf)\nsee [[shot.png]]\n"),
        ("/Work/other.md", "nothing here\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 27, 11, 0, 0, DateTimeKind.Utc));
    private readonly AttachmentWriter _files;
    private readonly Shell _sh;

    public AttachmentCommandTests()
    {
        var caller = new FixedCaller(Writers.Dev);
        _files = new AttachmentWriter(_store, _store, _time, caller);
        _sh = new Shell(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller));
    }

    private static CancellationToken Ct => CancellationToken.None;

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, Ct);

    private async Task<string?> VersionOf(string path) =>
        (await _store.GetAttachmentAsync(VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path), Ct))?.Version;

    private async Task Upload(string path, byte[] bytes, (int, int)? size = null)
    {
        var result = await _files.WriteAsync(path, bytes, size, await VersionOf(path), Ct);
        Assert.False(result.Result.IsError, result.Result.Output);
    }

    private static byte[] Png(int size, byte fill = 0) => [.. PngSignature, .. Enumerable.Repeat(fill, size - PngSignature.Length)];

    private static byte[] Pdf(int size) => [.. "%PDF-1.7"u8.ToArray(), .. new byte[size - 8]];

    [Fact]
    public async Task Ls_marks_attachments_with_kind_and_size()
    {
        await Upload("/Work/Screens/layout_editor_bad1.png", Png(412 * 1024), (1440, 3120));
        await Upload("/Work/report.pdf", Pdf(2_202_009));
        await Upload("/Work/tiny.gif", [.. "GIF89a"u8.ToArray(), 0, 0, 0, 0, 0, 0], (1, 1));

        Assert.Equal("other.md\nplan.md\nreport.pdf  PDF 2.1 MB\nScreens/\ntiny.gif  image 1×1 12 B", (await Run("ls /Work")).Output);
        Assert.Equal("layout_editor_bad1.png  image 1440×3120 412 KB", (await Run("ls /Work/Screens")).Output);
        Assert.Equal("/Work/report.pdf  PDF 2.1 MB", (await Run("ls /Work/report.pdf")).Output);
        Assert.Contains("ls: cannot access", (await Run("ls /Work/missing.png")).Output);
    }

    [Fact]
    public async Task Ls_lists_a_vault_that_holds_only_attachments()
    {
        _store.AddVault("Pics");
        await Upload("/Pics/a.png", Png(100), (3, 4));
        Assert.Equal("a.png  image 3×4 100 B", (await Run("ls /Pics")).Output);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1_048_000L, "1023 KB")]
    [InlineData(1_048_575L, "1.0 MB")]
    [InlineData(1_048_576L, "1.0 MB")]
    [InlineData(15_728_640L, "15.0 MB")]
    public void Sizes_read_like_a_person_would_say_them(long bytes, string expected)
    {
        var state = new AttachmentState("application/pdf", bytes, null, null, "v", "b");
        Assert.Equal($"PDF {expected}", AttachmentText.Summary(state));
    }

    [Fact]
    public async Task Cat_on_an_image_prints_one_line_not_bytes()
    {
        await Upload("/Work/Screens/layout_editor_bad1.png", Png(412 * 1024), (1440, 3120));
        await Upload("/Work/report.pdf", Pdf(2_202_009));

        var image = await Run("cat /Work/Screens/layout_editor_bad1.png");
        Assert.Equal("layout_editor_bad1.png: image (PNG), 1440×3120, 412 KB", image.Output);
        Assert.Null(image.Version);
        Assert.Equal("report.pdf: PDF, 2.1 MB", (await Run("cat /Work/report.pdf")).Output);
        Assert.Contains("is an attachment, not a note", (await Run("head /Work/report.pdf")).Output);
        Assert.Contains("is an attachment, not a note", (await Run("outline /Work/report.pdf")).Output);
    }

    [Fact]
    public async Task Cat_with_a_version_describes_that_state()
    {
        await Upload("/Work/shot.png", Png(2048), (10, 20));
        var first = await VersionOf("/Work/shot.png");
        _time.Advance(TimeSpan.FromHours(1));
        await Upload("/Work/shot.png", Png(4096, 1), (30, 40));

        Assert.Equal("shot.png: image (PNG), 30×40, 4 KB", (await Run("cat /Work/shot.png")).Output);
        Assert.Equal("shot.png: image (PNG), 10×20, 2 KB", (await Run($"cat --version {first} /Work/shot.png")).Output);
        Assert.Contains("has no version", (await Run("cat --version 000000000000 /Work/shot.png")).Output);
        // An attachment has no sections: the fragment is refused, not ignored.
        var section = await Run("cat \"/Work/shot.png#Layout\"");
        Assert.True(section.IsError);
        Assert.Equal("cat: /Work/shot.png is an attachment, which has no sections; 'cat /Work/shot.png' describes it", section.Output);
    }

    [Fact]
    public async Task Rg_skips_attachments_and_help_says_so()
    {
        await Upload("/Work/shot.png", Png(64), (1, 1));

        Assert.Equal("/Work/other.md\n/Work/plan.md", (await Run("rg --files /Work")).Output);
        Assert.DoesNotContain("shot.png:", (await Run("rg -i png /Work")).Output.Replace("/Work/plan.md", ""));
        Assert.Equal("rg: /Work/shot.png is an attachment; rg searches the text of notes and text files only", (await Run("rg x /Work/shot.png")).Output);
        Assert.Contains("is an attachment, not a note", (await Run("semsearch layout /Work/shot.png")).Output);
        Assert.Contains("Images and other files are not searched", (await Run("help rg")).Output);
        Assert.Contains("images and other files are not searched", (await Run("help semsearch")).Output);
        Assert.Contains("semsearch  ranks the pages of PDFs (not scans) by meaning", (await Run("help attachments")).Output);
    }

    [Fact]
    public async Task Mv_and_rm_work_on_attachments()
    {
        await Upload("/Work/shot.png", Png(64), (1, 1));

        var moved = await Run("mv /Work/shot.png /Work/archive/");
        Assert.False(moved.IsError, moved.Output);
        Assert.StartsWith("moved /Work/shot.png to /Work/archive/shot.png", moved.Output);
        Assert.Equal("archive/\nother.md\nplan.md", (await Run("ls /Work")).Output);
        // The bare name still leads there, so the note needs no rewrite.
        Assert.Equal(2, (await Run("backlinks /Work/archive/shot.png")).Output.Split('\n').Length);

        var removed = await Run("rm /Work/archive/shot.png");
        Assert.Equal(
            "removed /Work/archive/shot.png; it stays in the trash for 30 days ('restore /Work/archive/shot.png' brings it back); 2 links in /Work/plan.md now lead nowhere",
            removed.Output);
        Assert.Contains("No such file", (await Run("cat /Work/archive/shot.png")).Output);
        Assert.Matches(@"2026-10-27 11:00 UTC  /Work/archive/shot\.png  \w{12}  image 1×1 64 B  \(by dev\)", (await Run("trash /Work")).Output);

        var restored = await Run("restore /Work/archive/shot.png");
        Assert.Equal("restored /Work/archive/shot.png from the trash", restored.Output);
        Assert.Equal("shot.png  image 1×1 64 B", (await Run("ls /Work/archive")).Output);
        Assert.Equal("", (await Run("trash /Work")).Output);
        Assert.Contains("No such file", (await Run("rm /Work/nope.png")).Output);
    }

    [Fact]
    public async Task Mv_keeps_the_extension_of_the_files_format()
    {
        await Upload("/Work/shot.png", Png(64), (1, 1));
        await Upload("/Work/photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[60]], (1, 1));

        var refused = await Run("mv /Work/shot.png /Work/shot.jpg");

        Assert.True(refused.IsError);
        Assert.Equal("mv: /Work/shot.jpg: shot.png is a PNG image; its new name must end in .png (a new name doesn't change what the file is)", refused.Output);
        Assert.Equal("shot.png: image (PNG), 1×1, 64 B", (await Run("cat /Work/shot.png")).Output);
        Assert.Contains("must end in .jpg or .jpeg", (await Run("mv /Work/photo.jpg /Work/photo.webp")).Output);
        // .jpg and .jpeg name one format.
        Assert.False((await Run("mv /Work/photo.jpg /Work/photo.jpeg")).IsError);
        Assert.Equal("photo.jpeg: image (JPEG), 1×1, 64 B", (await Run("cat /Work/photo.jpeg")).Output);
    }

    [Fact]
    public async Task Rm_of_an_unlinked_attachment_reports_no_links()
    {
        await Upload("/Work/unused.png", Png(64), (1, 1));
        Assert.DoesNotContain("lead nowhere", (await Run("rm /Work/unused.png")).Output);
    }

    [Fact]
    public async Task History_lists_the_versions_of_an_attachment_and_restore_goes_back()
    {
        await Upload("/Work/shot.png", Png(2048), (10, 20));
        var first = await VersionOf("/Work/shot.png");
        _time.Advance(TimeSpan.FromHours(2));
        await Upload("/Work/shot.png", Png(4096, 1), (30, 40));

        var history = (await Run("history /Work/shot.png")).Output.Split('\n');
        Assert.Equal(2, history.Length);
        Assert.Matches(@"^\w{12}  2026-10-27 13:00 UTC  dev  image 30×40 4 KB  \(current\)$", history[0]);
        Assert.Equal($"{first}  2026-10-27 11:00 UTC  dev  image 10×20 2 KB", history[1]);
        // --since keeps the versions written since then, as for a note.
        Assert.Equal(history[0], (await Run("history --since 1h /Work/shot.png")).Output);
        Assert.Equal("(no versions of /Work/shot.png written since 2026-10-27 13:30 UTC)", (await Run("history --since 2026-10-27T13:30 /Work/shot.png")).Output);

        var folder = (await Run("history /Work")).Output;
        Assert.Contains("/Work/shot.png", folder);
        Assert.Contains("image 30×40 4 KB", folder);

        Assert.Equal($"restored /Work/shot.png to version {first}", (await Run($"restore /Work/shot.png {first}")).Output);
        Assert.Equal("shot.png: image (PNG), 10×20, 2 KB", (await Run("cat /Work/shot.png")).Output);
        Assert.Contains("→ trash", await RemoveAndHistory());
    }

    [Fact]
    public async Task An_attachment_restored_from_the_trash_is_marked()
    {
        await Upload("/Work/shot.png", Png(2048), (10, 20));
        var version = await VersionOf("/Work/shot.png");
        _time.Advance(TimeSpan.FromHours(2));
        await Run("rm /Work/shot.png");
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("restored /Work/shot.png from the trash", (await Run("restore /Work/shot.png")).Output);

        Assert.Equal(
            [$"{version}  2026-10-27 13:01 UTC  dev  image 10×20 2 KB  (current)  (restored)", $"{version}  2026-10-27 11:00 UTC  dev  image 10×20 2 KB"],
            (await Run("history /Work/shot.png")).Output.Split('\n'));
        var folder = (await Run("history /Work")).Output.Split('\n');
        Assert.Single(folder, r => r.Contains("(restored)", StringComparison.Ordinal));
        Assert.Matches(@"^2026-10-27 13:01 UTC  dev   /Work/shot\.png\s+\w{12}  image 10×20 2 KB  changeset \w{8}  \(restored\)$", folder[0]);

        // The next upload leaves one line of that version, unmarked.
        _time.Advance(TimeSpan.FromMinutes(1));
        await Upload("/Work/shot.png", Png(4096, 1), (30, 40));
        var after = (await Run("history /Work/shot.png")).Output;
        Assert.DoesNotContain("(restored)", after, StringComparison.Ordinal);
        Assert.Single(after.Split('\n'), l => l.StartsWith(version!, StringComparison.Ordinal));
    }

    private async Task<string> RemoveAndHistory()
    {
        await Run("rm /Work/shot.png");
        return (await Run("history /Work")).Output;
    }

    [Fact]
    public async Task Links_and_backlinks_accept_an_attachment()
    {
        await Upload("/Work/shot.png", Png(64), (1, 1));

        Assert.Equal("/Work/plan.md:1:Layout: ![[shot.png]] and [report](/Docs/report.pdf)\n/Work/plan.md:2:see [[shot.png]]", (await Run("backlinks /Work/shot.png")).Output);
        Assert.StartsWith("(no links", (await Run("links /Work/shot.png")).Output);
        Assert.Contains("is an attachment", (await Run("links --version abc /Work/shot.png")).Output);
        Assert.Equal("1: ![[shot.png]] → /Work/shot.png\n1: [report](/Docs/report.pdf) → not found\n2: [[shot.png]] → /Work/shot.png", (await Run("links /Work/plan.md")).Output);
    }

    [Fact]
    public async Task Backlinks_of_an_ambiguous_attachment_name_say_file()
    {
        var store = new InMemoryNoteStore(("/W/n.md", "see [[dup.png]]\n"));
        store.AddAttachment("/W/a/dup.png");
        store.AddAttachment("/W/b/dup.png");
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var sh = new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)));

        Assert.Equal("/W/n.md:1:see [[dup.png]]  (ambiguous, may mean this file)", (await sh.RunAsync("backlinks /W/a/dup.png", Ct)).Output);
    }

    private static readonly byte[] Binary = [0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF];

    private async Task Note(string path, string content) =>
        Assert.False((await new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).WriteAsync(path, content, null, Ct)).IsError);

    [Fact]
    public async Task Ls_shows_a_file_and_a_folder_with_a_dot_side_by_side()
    {
        await Upload("/Work/Makefile", "all:\n"u8.ToArray());
        await Upload("/Work/sheet.xlsx", Binary);
        await Note("/Work/v1.2/notes.md", "notes\n");
        Assert.False((await Run("mkdir /Work/v1.3")).IsError);

        Assert.Equal("Makefile  text 5 B\nother.md\nplan.md\nsheet.xlsx  file 6 B\nv1.2/\nv1.3/", (await Run("ls /Work")).Output);
        Assert.Equal("notes.md", (await Run("ls /Work/v1.2")).Output);
        Assert.Equal("/Work/Makefile  text 5 B", (await Run("ls /Work/Makefile")).Output);
    }

    [Fact]
    public async Task Cat_prints_a_text_file_and_describes_a_binary_one()
    {
        await Upload("/Work/data.csv", "name;size\nrose;3\n"u8.ToArray());
        await Upload("/Work/sheet.xlsx", Binary);
        await Note("/Work/v1.2/notes.md", "notes\n");

        Assert.Equal("name;size\nrose;3\n", (await Run("cat /Work/data.csv")).Output);
        Assert.Equal("sheet.xlsx: file, 6 B", (await Run("cat /Work/sheet.xlsx")).Output);
        Assert.Equal("cat: /Work/sheet.xlsx is an attachment, which has no sections; 'cat /Work/sheet.xlsx' describes it", (await Run("cat \"/Work/sheet.xlsx#Top\"")).Output);
        Assert.Equal("cat: /Work/v1.2: Is a directory", (await Run("cat /Work/v1.2")).Output);
        Assert.Contains("No such file", (await Run("cat /Work/v1.4")).Output);

        // A long text file is cut as any output is, never outlined as a note: a CSV has no headings.
        await Upload("/Work/long.csv", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("# a;b\n", OutputLimit.MaxChars / 4))));
        Assert.DoesNotContain("its outline is above", (await Run("cat /Work/long.csv")).Output);
    }

    [Fact]
    public async Task Rg_searches_text_files_and_not_binary_ones()
    {
        await Upload("/Work/data.csv", "name;size\nrose;3\n"u8.ToArray());
        await Upload("/Work/rose.bin", [.. "rose"u8.ToArray(), 0]);

        Assert.Equal("/Work/data.csv:2:rose;3", (await Run("rg rose /Work")).Output);
        Assert.Equal("/Work/data.csv:2:rose;3", (await Run("rg rose /Work/data.csv")).Output);
        Assert.Equal("/Work/data.csv\n/Work/other.md\n/Work/plan.md", (await Run("rg --files /Work")).Output);
        Assert.Equal("/Work/data.csv", (await Run("rg --files /Work/data.csv")).Output);
        Assert.Equal("/Work/other.md\n/Work/plan.md", (await Run("rg --files /Work -g '*.md'")).Output);
        Assert.Equal("rg: /Work/rose.bin is an attachment; rg searches the text of notes and text files only", (await Run("rg x /Work/rose.bin")).Output);
    }

    [Fact]
    public async Task Rg_stops_reading_text_files_at_its_bound_and_says_so()
    {
        await Upload("/Work/a.csv", "rose;1\n"u8.ToArray());
        await Upload("/Work/b.csv", "rose;2\n"u8.ToArray());
        await Upload("/Work/c.csv", "rose;3\n"u8.ToArray());
        var rg = new Kumbu.Core.Sh.Commands.Rg(_store) { TextBytesBudget = 15 };

        var result = await rg.RunAsync(["rose", "/Work"], null, Ct);

        Assert.Equal("/Work/a.csv:1:rose;1\n/Work/b.csv:1:rose;2", result.Output);
        Assert.Equal("rg: stopped reading text files after 15 B; narrow the path", result.Notice);
        Assert.Equal(64 * 1024 * 1024, Kumbu.Core.Sh.Commands.Rg.MaxTextBytes);
    }

    [Fact]
    public async Task Rg_reads_no_text_file_a_filter_leaves_out()
    {
        await Upload("/Work/a.csv", "rose;1\n"u8.ToArray());
        await Upload("/Work/skip.csv", "rose;2\n"u8.ToArray());
        await Upload("/Work/_archive/old.csv", "rose;3\n"u8.ToArray());
        await Note("/Work/tagged.md", "#flower rose\n");
        var opened = _store.BlobOpens;

        Assert.Equal("/Work/a.csv:1:rose;1\n/Work/tagged.md:1:#flower rose", (await Run("rg rose /Work -g '!skip.csv'")).Output);
        Assert.Equal(opened + 1, _store.BlobOpens);
        Assert.Equal("/Work/tagged.md:1:#flower rose", (await Run("rg --tag flower rose /Work")).Output);
        Assert.Equal(opened + 1, _store.BlobOpens);
        Assert.Equal("no match outside the archive; 1 archived file matches (rg --archive 'rose;3' /Work searches it)", (await Run("rg 'rose;3' /Work")).Output);
    }

    [Fact]
    public async Task Mv_rm_and_restore_work_on_a_file()
    {
        await Upload("/Work/Makefile", "all:\n"u8.ToArray());
        await Note("/Work/v1.2/notes.md", "notes\n");

        Assert.StartsWith("moved /Work/Makefile to /Work/v1.2/Makefile", (await Run("mv /Work/Makefile /Work/v1.2")).Output);
        Assert.StartsWith("moved /Work/v1.2/Makefile to /Work/build/GNUmakefile", (await Run("mv /Work/v1.2/Makefile /Work/build/GNUmakefile")).Output);
        Assert.Equal("mv: /Work/build/GNUmakefile is a file, not a folder; nothing can be kept below it", (await Run("mv /Work/v1.2/notes.md /Work/build/GNUmakefile/notes.md")).Output);
        Assert.StartsWith("moved /Work/v1.2/ to /Work/v1.3/", (await Run("mv /Work/v1.2/ /Work/v1.3/")).Output);

        Assert.StartsWith("removed /Work/build/GNUmakefile;", (await Run("rm /Work/build/GNUmakefile")).Output);
        Assert.Equal("restored /Work/build/GNUmakefile from the trash", (await Run("restore /Work/build/GNUmakefile")).Output);
        Assert.Equal("all:\n", (await Run("cat /Work/build/GNUmakefile")).Output);
    }

    [Fact]
    public async Task Rm_and_restore_tell_a_folder_with_a_dot_from_a_file_without_one()
    {
        await Upload("/Work/Makefile", "all:\n"u8.ToArray());
        await Note("/Work/v1.2/notes.md", "notes\n");
        Assert.False((await Run("mkdir /Work/v1.3")).IsError);

        Assert.StartsWith("rm: /Work/v1.2: Is a directory holding 1 entry", (await Run("rm /Work/v1.2")).Output);
        Assert.StartsWith("removed folder /Work/v1.3/;", (await Run("rm /Work/v1.3")).Output);
        Assert.StartsWith("removed /Work/Makefile;", (await Run("rm /Work/Makefile")).Output);

        Assert.Equal("restored /Work/v1.3/ from the trash", (await Run("restore /Work/v1.3")).Output);
        Assert.Equal("restored /Work/Makefile from the trash", (await Run("restore /Work/Makefile")).Output);
        Assert.Equal("Makefile  text 5 B\nother.md\nplan.md\nv1.2/\nv1.3/", (await Run("ls /Work")).Output);
        Assert.Equal("mkdir: /Work/Makefile: File exists (it is a file)", (await Run("mkdir /Work/Makefile")).Output);
    }

    [Fact]
    public async Task Archive_keeps_a_files_name_whole()
    {
        await Upload("/Work/Makefile", "all:\n"u8.ToArray());
        await Upload("/Work/sheet.xlsx", Binary);

        Assert.StartsWith("moved /Work/Makefile to /Work/_archive/Makefile 2026-10-27 110000;", (await Run("archive /Work/Makefile")).Output);
        Assert.StartsWith("moved /Work/sheet.xlsx to /Work/_archive/sheet 2026-10-27 110000.xlsx;", (await Run("archive /Work/sheet.xlsx")).Output);
    }

    [Fact]
    public async Task Links_to_a_file_lead_to_it()
    {
        await Upload("/Work/data.csv", "a;b\n"u8.ToArray());
        await Upload("/Work/src/Makefile", "all:\n"u8.ToArray());
        await Note("/Work/Makefile.md", "a note\n");
        await Note("/Work/links.md", "[[data.csv]] [[src/Makefile]] [[nowhere]]\n");

        Assert.Equal("1: [[data.csv]] → /Work/data.csv\n1: [[src/Makefile]] → /Work/src/Makefile\n1: [[nowhere]] → not found", (await Run("links /Work/links.md")).Output);
        Assert.Equal("/Work/links.md:1:[[data.csv]] [[src/Makefile]] [[nowhere]]", (await Run("backlinks /Work/data.csv")).Output);
    }

    [Fact]
    public async Task Write_tools_refuse_an_attachment_path()
    {
        await Upload("/Work/shot.png", Png(64), (1, 1));
        var writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        var result = await writer.WriteAsync("/Work/shot.png", "text", null, Ct);
        Assert.True(result.IsError);
        Assert.Contains("is an attachment", result.Output);
    }
}
