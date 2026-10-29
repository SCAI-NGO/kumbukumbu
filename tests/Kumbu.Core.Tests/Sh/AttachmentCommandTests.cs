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
        Assert.Equal("rg: /Work/shot.png is an attachment; rg searches the text of notes only", (await Run("rg x /Work/shot.png")).Output);
        Assert.Contains("is an attachment, not a note", (await Run("semsearch layout /Work/shot.png")).Output);
        Assert.Contains("Images are not searched", (await Run("help rg")).Output);
        Assert.Contains("Images and PDFs are not searched", (await Run("help semsearch")).Output);
        Assert.Contains("semsearch skips attachments", (await Run("help attachments")).Output);
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
