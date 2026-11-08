// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>pdfimages</c>: its arguments, and <c>--list</c>, a PDF's images page by page.</summary>
public class PdfImagesTests
{
    private const string UsageLine = "usage: pdfimages <pdf> [pages] <folder>/ | <pdf> <page> --crop x,y,w,h <file.png> | --list <pdf> [pages]";

    private sealed class FakePixels(FakeTimeProvider time) : IAttachmentPixels
    {
        public int PageCount { get; set; } = 3;

        /// <summary>The images of a page; a page not given paints none.</summary>
        public Dictionary<int, List<PdfImageInfo>> Pages { get; } = [];

        /// <summary>Images over 50 MP a page leaves out.</summary>
        public Dictionary<int, int> Over { get; } = [];

        public Dictionary<(int Page, int N), Picture> Images { get; } = [];

        public Picture? Region { get; set; }

        /// <summary>Thrown by the next calls, one each, before they answer.</summary>
        public Queue<Exception> Throws { get; } = new();

        /// <summary>How far each call moves the clock, as a call to the sidecar takes time.</summary>
        public TimeSpan PerCall { get; set; }

        /// <summary>The exception listing a page throws, or null to list it.</summary>
        public Func<int, Exception?> ListFails { get; set; } = _ => null;

        public List<string> Calls { get; } = [];

        private void Called(string call)
        {
            Calls.Add(call);
            time.Advance(PerCall);
            if (Throws.TryDequeue(out var e)) throw e;
        }

        public Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, int rotate, int? frame, CancellationToken ct) => throw new NotSupportedException();

        public Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, int rotate, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<PageTextBoxes>> TextBoxesAsync(VaultPath path, AttachmentState state, IReadOnlyList<int> pages, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfPageImages> PdfImagesAsync(VaultPath path, AttachmentState state, int page, CancellationToken ct)
        {
            Called($"list {page}");
            // As AttachmentPixels words the sidecar's no_such_page.
            if (ListFails(page) is { } e) throw e;
            if (page > PageCount) throw new PixelsUnavailableException($"{path} has no page {page}");
            return Task.FromResult(new PdfPageImages(page, PageCount, 595, 842, Over.GetValueOrDefault(page), Pages.GetValueOrDefault(page, [])));
        }

        public Task<Picture> PdfImageAsync(VaultPath path, AttachmentState state, int page, int n, CancellationToken ct)
        {
            Called($"image {page} {n}");
            return Task.FromResult(Images[(page, n)]);
        }

        public Task<Picture> PdfRegionAsync(VaultPath path, AttachmentState state, int page, Crop crop, int rotate, bool jpeg, CancellationToken ct)
        {
            Called($"region {page} {crop} {rotate} {(jpeg ? "jpeg" : "png")}");
            return Task.FromResult(Region!);
        }

        public void Count(AttachmentUse use)
        {
        }
    }

    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "a note\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 11, 8, 11, 0, 0, DateTimeKind.Utc));
    private readonly FakePixels _pixels;
    private readonly AttachmentWriter _files;
    private readonly Shell _sh;

    public PdfImagesTests()
    {
        _pixels = new FakePixels(_time);
        var caller = new FixedCaller(Writers.Dev);
        _files = new AttachmentWriter(_store, _store, _time, caller);
        _sh = new Shell(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller), pixels: _pixels);
    }

    private static CancellationToken Ct => CancellationToken.None;

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, Ct);

    /// <summary>Runs a line whose command waits on the clock (a busy sidecar tried again), moving the clock on until it is done.</summary>
    private async Task<ShellResult> RunWaiting(string line)
    {
        var running = Run(line);
        while (!running.IsCompleted)
        {
            await Task.Delay(1, Ct);
            _time.Advance(TimeSpan.FromMilliseconds(250));
        }

        return await running;
    }

    private static byte[] Pdf(int size = 2048) => [.. "%PDF-1.7"u8.ToArray(), .. new byte[size - 8]];

    private async Task Upload(string path = "/Work/manual.pdf")
    {
        var result = await _files.WriteAsync(path, Pdf(), null, null, Ct);
        Assert.False(result.Result.IsError, result.Result.Output);
    }

    private static PdfImageInfo Image(int n, int w, int h, double x, double y, double bw, double bh, string? hash, bool small = false, bool failed = false) =>
        new(n, w, h, x, y, bw, bh, small, hash, failed);

    private static ShellResult Usage(string why) => ShellResult.Error($"pdfimages: {why}\n{UsageLine}") with { Kind = ShellErrorKind.Invalid };

    [Fact]
    public async Task No_operands_give_the_usage_line()
    {
        Assert.Equal(Usage("missing operand"), await Run("pdfimages"));
        Assert.Equal(Usage("missing operand"), await Run("pdfimages --list"));
        Assert.Equal(Usage("missing operand"), await Run("pdfimages /Work/manual.pdf"));
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public async Task A_folder_without_a_slash_gives_the_usage_line()
    {
        await Upload();

        Assert.Equal(Usage("the folder to store into ends in '/': /Work/images/"), await Run("pdfimages /Work/manual.pdf /Work/images"));
        Assert.Equal(Usage("the folder to store into ends in '/': /Work/images/"), await Run("pdfimages /Work/manual.pdf 2-3 /Work/images"));
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public async Task Too_many_operands_give_the_usage_line()
    {
        await Upload();

        Assert.Equal(Usage("too many arguments"), await Run("pdfimages /Work/manual.pdf 1 2 /Work/images/"));
        Assert.Equal(Usage("too many arguments"), await Run("pdfimages --list /Work/manual.pdf 1 /Work/images/"));
        Assert.Equal(Usage("too many arguments"), await Run("pdfimages /Work/manual.pdf 1 --crop 0,0,10,10 /Work/a.png /Work/b.png"));
    }

    [Fact]
    public async Task Crop_with_a_range_gives_the_usage_line()
    {
        await Upload();

        Assert.Equal(Usage("--crop takes one page, not 2-3"), await Run("pdfimages /Work/manual.pdf 2-3 --crop 0,0,100,100 /Work/images/a.png"));
        Assert.Equal(Usage("--crop takes one page, not 2-"), await Run("pdfimages /Work/manual.pdf 2- --crop 0,0,100,100 /Work/images/a.png"));
        Assert.Equal(Usage("--crop takes one page and a file to store it in"), await Run("pdfimages /Work/manual.pdf --crop 0,0,100,100 /Work/images/a.png"));
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public async Task Crop_into_a_folder_or_another_format_gives_the_usage_line()
    {
        await Upload();

        Assert.Equal(Usage("--crop stores one file, named .png, .jpg or .jpeg, not a folder: /Work/images/"), await Run("pdfimages /Work/manual.pdf 2 --crop 0,0,100,100 /Work/images/"));
        Assert.Equal(Usage("--crop stores one file, named .png, .jpg or .jpeg, not /Work/images/a.gif"), await Run("pdfimages /Work/manual.pdf 2 --crop 0,0,100,100 /Work/images/a.gif"));
        Assert.Equal(Usage("--crop stores one file, named .png, .jpg or .jpeg, not /Work/images/a"), await Run("pdfimages /Work/manual.pdf 2 --crop 0,0,100,100 /Work/images/a"));
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public async Task List_and_all_go_with_embedded_images_only()
    {
        await Upload();

        Assert.Equal(Usage("--list writes nothing and --crop stores a region; give one of them"), await Run("pdfimages --list /Work/manual.pdf 2 --crop 0,0,100,100"));
        Assert.Equal(Usage("--all is for embedded images; --crop stores the region as it is"), await Run("pdfimages /Work/manual.pdf 2 --all --crop 0,0,100,100 /Work/a.png"));
    }

    [Fact]
    public async Task Rotate_without_crop_is_refused()
    {
        await Upload();
        var expected = ShellResult.Error("pdfimages: --rotate turns a region stored with --crop; embedded images are stored as the page paints them") with { Kind = ShellErrorKind.Invalid };

        Assert.Equal(expected, await Run("pdfimages /Work/manual.pdf /Work/images/ --rotate 90"));
        Assert.Equal(expected, await Run("pdfimages --list /Work/manual.pdf --rotate 90"));
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public async Task A_bad_crop_rotation_or_range_is_named()
    {
        await Upload();

        var crop = await Run("pdfimages /Work/manual.pdf 2 --crop 0,0,100 /Work/a.png");
        var rotate = await Run("pdfimages /Work/manual.pdf 2 --crop 0,0,100,100 --rotate 45 /Work/a.png");
        var range = await Run("pdfimages --list /Work/manual.pdf 3-1");

        Assert.StartsWith("pdfimages: '0,0,100' is not a crop", crop.Output);
        Assert.Equal("pdfimages: --rotate turns clockwise by 90, 180 or 270 degrees (or back with -90, -180 or -270), not '45' (leave it out for no turn)", rotate.Output);
        Assert.Equal("pdfimages: '3-1' is not a page range: the first page comes first, e.g. 5-9", range.Output);
        Assert.All([crop, rotate, range], r => Assert.Equal(ShellErrorKind.Invalid, r.Kind));
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public async Task A_missing_pdf_a_note_and_an_image_are_refused()
    {
        await _files.WriteAsync("/Work/shot.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, (10, 10), null, Ct);

        var missing = await Run("pdfimages --list /Work/nothing.pdf");
        var note = await Run("pdfimages /Work/plan.md /Work/images/");
        var image = await Run("pdfimages --list /Work/shot.png");

        Assert.Equal(ShellErrorKind.NotFound, missing.Kind);
        Assert.StartsWith("pdfimages: /Work/nothing.pdf: No such file or directory", missing.Output);
        Assert.Equal(ShellResult.Error("pdfimages: /Work/plan.md is a note, not a PDF") with { Kind = ShellErrorKind.Invalid }, note);
        Assert.Equal(ShellResult.Error("pdfimages: /Work/shot.png is not a PDF") with { Kind = ShellErrorKind.Invalid }, image);
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public async Task List_names_each_image_with_its_box_and_marks_the_duplicate()
    {
        await Upload();
        _pixels.Pages[1] = [Image(1, 1200, 400, 72, 40, 451, 150, "aa"), Image(2, 2400, 1600, 80.4, 300, 434.8, 290, "bb")];
        _pixels.Pages[3] = [Image(1, 2400, 1600, 80, 300, 435, 290, "bb")];

        var result = await Run("pdfimages --list /Work/manual.pdf");

        Assert.Equal(
            """
            /Work/manual.pdf: 3 images on pages 1-3
            p1  #1  1200×400  at 72,40,451,150
            p1  #2  2400×1600  at 80,300,436,290
            p3  #1  2400×1600  at 80,300,435,290  [same as p1 #2]
            """,
            result.Output);
        Assert.False(result.IsError);
        Assert.Equal(["list 1", "list 2", "list 3"], _pixels.Calls);
    }

    [Fact]
    public async Task List_leaves_out_small_ones_and_counts_them_all_lists_them()
    {
        await Upload();
        _pixels.Pages[2] = [Image(1, 16, 16, 40, 40, 8, 8, "dot", small: true), Image(2, 800, 600, 72, 100, 400, 300, "photo"), Image(3, 16, 16, 40, 60, 8, 8, "dot", small: true)];

        var plain = await Run("pdfimages /Work/manual.pdf 2 --list");
        var all = await Run("pdfimages --all /Work/manual.pdf 2 --list");

        Assert.Equal("/Work/manual.pdf: 3 images on page 2 (2 small, --all lists them)\np2  #2  800×600  at 72,100,400,300", plain.Output);
        Assert.Equal(
            "/Work/manual.pdf: 3 images on page 2 (2 small)\np2  #1  16×16  at 40,40,8,8  [small]\np2  #2  800×600  at 72,100,400,300\np2  #3  16×16  at 40,60,8,8  [small] [same as p2 #1]",
            all.Output);
    }

    [Fact]
    public async Task List_names_what_could_not_be_decoded_and_what_was_too_large()
    {
        await Upload();
        _pixels.Pages[1] = [Image(1, 300, 200, 10, 10, 100, 80, null, failed: true)];
        _pixels.Over[1] = 1;
        _pixels.Over[2] = 2;

        var result = await Run("pdfimages --list /Work/manual.pdf 1-2");

        Assert.Equal(
            """
            /Work/manual.pdf: 1 image on pages 1-2
            p1  #1  300×200  at 10,10,100,80  [could not be decoded]
            page 1: 1 image over 50 MP left out (look with pdf --crop)
            page 2: 2 images over 50 MP left out (look with pdf --crop)
            """,
            result.Output);
    }

    [Fact]
    public async Task List_of_pages_without_images_says_so()
    {
        await Upload();

        Assert.Equal("/Work/manual.pdf: no images on pages 2-3", (await Run("pdfimages --list /Work/manual.pdf 2-")).Output);
    }

    [Fact]
    public async Task A_range_past_the_end_is_cut_and_a_page_past_the_end_says_the_page_count()
    {
        await Upload();

        var cut = await Run("pdfimages --list /Work/manual.pdf 2-9");
        var past = await Run("pdfimages --list /Work/manual.pdf 5");

        Assert.Equal("/Work/manual.pdf: no images on pages 2-3", cut.Output);
        Assert.Equal(["list 2", "list 3", "list 5", "list 1"], _pixels.Calls);
        Assert.Equal(ShellResult.Error("pdfimages: /Work/manual.pdf has 3 pages; there is no page 5") with { Kind = ShellErrorKind.Invalid }, past);
    }

    [Fact]
    public async Task A_failure_of_the_sidecar_is_quoted()
    {
        await Upload();
        _pixels.Throws.Enqueue(new PixelsUnavailableException("PDF image extraction is unavailable right now; try again later", PixelsProblem.Unavailable));

        var result = await Run("pdfimages --list /Work/manual.pdf");

        Assert.Equal(ShellResult.Error("pdfimages: PDF image extraction is unavailable right now; try again later") with { Kind = ShellErrorKind.Unavailable }, result);
        Assert.Equal(["list 1"], _pixels.Calls);
    }

    [Fact]
    public async Task A_failure_after_the_first_page_keeps_the_pages_listed()
    {
        await Upload();
        _pixels.Pages[1] = [Image(1, 1200, 400, 72, 40, 451, 150, "aa")];
        _pixels.ListFails = p => p == 2 ? new PixelsUnavailableException("reading the images of page 2 of /Work/manual.pdf took too long") : null;

        var result = await Run("pdfimages --list /Work/manual.pdf");

        Assert.True(result.IsError);
        Assert.Equal(
            "/Work/manual.pdf: 1 image on page 1\np1  #1  1200×400  at 72,40,451,150\npdfimages: reading the images of page 2 of /Work/manual.pdf took too long",
            result.Output);
    }

    [Fact]
    public async Task Busy_is_tried_again_three_times_a_second_apart()
    {
        await Upload();
        _pixels.Pages[1] = [Image(1, 1200, 400, 72, 40, 451, 150, "aa")];
        for (var i = 0; i < 3; i++) _pixels.Throws.Enqueue(new PixelsUnavailableException("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy));

        var started = _time.GetUtcNow();
        var result = await RunWaiting("pdfimages --list /Work/manual.pdf 1");

        Assert.Equal("/Work/manual.pdf: 1 image on page 1\np1  #1  1200×400  at 72,40,451,150", result.Output);
        Assert.Equal(["list 1", "list 1", "list 1", "list 1"], _pixels.Calls);
        Assert.True(_time.GetUtcNow() - started >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Busy_a_fourth_time_is_the_answer()
    {
        await Upload();
        for (var i = 0; i < 4; i++) _pixels.Throws.Enqueue(new PixelsUnavailableException("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy));

        var result = await RunWaiting("pdfimages --list /Work/manual.pdf 1");

        Assert.Equal(ShellResult.Error("pdfimages: PDF image extraction is busy; try again in a moment") with { Kind = ShellErrorKind.Unavailable }, result);
        Assert.Equal(4, _pixels.Calls.Count);
    }

    [Fact]
    public async Task List_stops_at_half_the_budget_and_names_the_pages_to_go_on_with()
    {
        await Upload();
        _pixels.PageCount = 10;
        _pixels.PerCall = TimeSpan.FromSeconds(4);
        _pixels.Pages[2] = [Image(1, 1200, 400, 72, 40, 451, 150, "aa")];

        var result = await Run("pdfimages --list /Work/manual.pdf");

        Assert.Equal(["list 1", "list 2", "list 3"], _pixels.Calls);
        Assert.Equal("/Work/manual.pdf: 1 image on pages 1-3\np2  #1  1200×400  at 72,40,451,150\nlisted pages 1-3; go on with pages 4-", result.Output);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task The_budget_is_the_commands_own()
    {
        await Upload();
        _pixels.PageCount = 10;
        _pixels.PerCall = TimeSpan.FromSeconds(4);
        _sh.Register(new PdfImages(_store, _pixels, _files, _time) { Budget = TimeSpan.FromSeconds(40) });

        var result = await Run("pdfimages --list /Work/manual.pdf 3-");

        Assert.Equal(["list 3", "list 4", "list 5", "list 6", "list 7"], _pixels.Calls);
        Assert.EndsWith("\nlisted pages 3-7; go on with pages 8-", result.Output);
    }

    [Fact]
    public async Task Help_lists_pdfimages_after_pdf()
    {
        var help = await Run("help");
        var own = await Run("help pdfimages");

        Assert.True(help.Output.IndexOf("\n  pdf ", StringComparison.Ordinal) < help.Output.IndexOf("\n  pdfimages <pdf> [pages] <dir>/ ", StringComparison.Ordinal));
        Assert.Contains("store a PDF's pictures as images, to place in notes", help.Output);
        Assert.StartsWith("pdfimages <pdf> [pages] <folder>/\npdfimages <pdf> <page> --crop x,y,w,h <file.png>\npdfimages --list <pdf> [pages]\n", own.Output);
        Assert.Contains("duplicates are found within one call", own.Output);
        Assert.Contains("\n  pdfimages <pdf> [pages] <dir>/  ", Manual.ToolDescription);
    }

    [Fact]
    public async Task It_is_not_run_on_a_checkpoint()
    {
        await Upload();

        var result = await Run("pdfimages --list /Work/manual.pdf --checkpoint before");

        Assert.StartsWith("pdfimages: --checkpoint works with ls, cat", result.Output);
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public void A_shell_without_attachments_has_no_pdfimages()
    {
        Assert.False(TestShell.Of().HasCommand("pdfimages"));
        var store = new InMemoryNoteStore();
        var time = new FakeTimeProvider();
        var caller = new FixedCaller(Writers.Dev);
        Assert.True(new Shell(store, new NoteWriter(store, time, caller), new NoteMover(store, time, caller), pixels: new FakePixels(time)).HasCommand("pdfimages"));
    }
}
