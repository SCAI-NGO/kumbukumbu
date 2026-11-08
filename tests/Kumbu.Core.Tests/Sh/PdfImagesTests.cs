// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>pdfimages</c>: its arguments, <c>--list</c>, a PDF's images page by page, storing them, and a region with <c>--crop</c>.</summary>
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
            if (page > PageCount) throw new PixelsUnavailableException($"{path} has no page {page}", PixelsProblem.NoSuchPage);
            return Task.FromResult(new PdfPageImages(page, PageCount, 595, 842, Over.GetValueOrDefault(page), Pages.GetValueOrDefault(page, [])));
        }

        /// <summary>The exception fetching an image throws, every time it is asked for.</summary>
        public Dictionary<(int Page, int N), Exception> ImageFails { get; } = [];

        /// <summary>Thrown by the next image fetches, one each.</summary>
        public Queue<Exception> ImageThrows { get; } = new();

        public Task<Picture> PdfImageAsync(VaultPath path, AttachmentState state, int page, int n, CancellationToken ct)
        {
            Called($"image {page} {n}");
            if (ImageThrows.TryDequeue(out var next)) throw next;
            if (ImageFails.TryGetValue((page, n), out var e)) throw e;
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
    /// <summary>A clock that moves on by a timer's time as the timer is set, so a pause through it is over at once.</summary>
    private sealed class SteppingTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) Advance(dueTime);
            return timer;
        }
    }

    private readonly FakeTimeProvider _time = new SteppingTime(new DateTime(2026, 11, 8, 11, 0, 0, DateTimeKind.Utc));
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
    public async Task The_root_and_a_relative_target_are_refused()
    {
        await Upload();

        var root = await Run("pdfimages /Work/manual.pdf /");
        var relative = await Run("pdfimages /Work/manual.pdf images/");

        Assert.Equal(ShellResult.Error("pdfimages: /: images are stored in a folder of a vault, e.g. /Work/images/") with { Kind = ShellErrorKind.Invalid }, root);
        Assert.True(relative.IsError);
        Assert.Equal(ShellErrorKind.Invalid, relative.Kind);
        Assert.StartsWith("pdfimages: images/: paths must be absolute", relative.Output);
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
    public async Task A_first_page_above_1_that_fails_otherwise_asks_for_no_page_count()
    {
        await Upload();
        _pixels.ListFails = p => p == 2 ? new PixelsUnavailableException("reading the images of page 2 of /Work/manual.pdf took too long") : null;

        var result = await Run("pdfimages --list /Work/manual.pdf 2-");

        Assert.Equal(ShellResult.Error("pdfimages: reading the images of page 2 of /Work/manual.pdf took too long"), result);
        Assert.Equal(["list 2"], _pixels.Calls);
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
        var result = await Run("pdfimages --list /Work/manual.pdf 1");

        Assert.Equal("/Work/manual.pdf: 1 image on page 1\np1  #1  1200×400  at 72,40,451,150", result.Output);
        Assert.Equal(["list 1", "list 1", "list 1", "list 1"], _pixels.Calls);
        Assert.True(_time.GetUtcNow() - started >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Busy_a_fourth_time_is_the_answer()
    {
        await Upload();
        for (var i = 0; i < 4; i++) _pixels.Throws.Enqueue(new PixelsUnavailableException("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy));

        var result = await Run("pdfimages --list /Work/manual.pdf 1");

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

    private static byte[] Bytes(int size, params byte[] magic)
    {
        var bytes = new byte[size];
        magic.CopyTo(bytes, 0);
        // Distinct bytes for distinct pictures, as the blob store keys them by content.
        BitConverter.GetBytes(size).CopyTo(bytes, magic.Length);
        return bytes;
    }

    private static Picture Png(int size, int w, int h, double? fromW = null, double? fromH = null) =>
        new(Bytes(size, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), "image/png", w, h, fromW ?? w, fromH ?? h);

    private static Picture Jpeg(int size, int w, int h) => new(Bytes(size, 0xFF, 0xD8, 0xFF), "image/jpeg", w, h, w, h);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private Task<StoredAttachment?> Stored(string path) => _store.GetAttachmentAsync(P(path), Ct);

    private async Task<IReadOnlyList<string>> StoredUnder(string folder) =>
        [.. (await _store.ListAttachmentsUnderAsync(P(folder), Ct)).Select(a => a.Path.Value)];

    /// <summary>Two images on page 1, one on page 3: the call the store tests start from.</summary>
    private async Task TwoPages()
    {
        await Upload();
        _pixels.Pages[1] = [Image(1, 1200, 400, 72, 40, 451, 150, "aa")];
        _pixels.Pages[3] = [Image(1, 2400, 1600, 80, 300, 435, 290, "bb")];
        _pixels.Images[(1, 1)] = Png(90_112, 1200, 400);
        _pixels.Images[(3, 1)] = Png(1_992_294, 2400, 1600);
    }

    [Fact]
    public async Task Stores_the_images_each_with_its_line()
    {
        await TwoPages();

        var result = await Run("pdfimages /Work/manual.pdf /Work/Manual/images/");

        Assert.Equal(
            """
            stored 2 images in /Work/Manual/images/
            /Work/Manual/images/manual-p1-1.png  p1  1200×400  88 KB  at 72,40,451,150
            /Work/Manual/images/manual-p3-1.png  p3  2400×1600  1.9 MB  at 80,300,435,290
            """,
            result.Output);
        Assert.False(result.IsError);
        Assert.Equal(["list 1", "list 2", "list 3", "image 1 1", "image 3 1"], _pixels.Calls);
        var first = await Stored("/Work/Manual/images/manual-p1-1.png");
        Assert.NotNull(first);
        Assert.Equal(("image/png", 1200, 400, 90_112L), (first.Current.MediaType, first.Current.Width, first.Current.Height, first.Current.Size));
        Assert.Equal(_pixels.Images[(1, 1)].Data, await StoreAttachments.ReadBytesAsync(_store, first.Path, first.Current, Ct));
        Assert.NotNull(await Stored("/Work/Manual/images/manual-p3-1.png"));
    }

    [Fact]
    public async Task A_range_stores_its_pages_only()
    {
        await TwoPages();

        var result = await Run("pdfimages /Work/manual.pdf 2- /Work/images/");

        Assert.Equal("stored 1 image in /Work/images/\n/Work/images/manual-p3-1.png  p3  2400×1600  1.9 MB  at 80,300,435,290", result.Output);
        Assert.Equal(["/Work/images/manual-p3-1.png"], await StoredUnder("/Work/images"));
    }

    [Fact]
    public async Task An_image_on_several_pages_is_stored_once_naming_each_page()
    {
        await Upload();
        for (var page = 1; page <= 3; page++) _pixels.Pages[page] = [Image(1, 300, 100, 20, 20, 120, 40, "logo"), Image(2, 800, 600, 72, 100, 400, 300, $"photo{page}")];
        _pixels.Images[(1, 1)] = Png(4096, 300, 100);
        for (var page = 1; page <= 3; page++) _pixels.Images[(page, 2)] = Png(50_000 + page, 800, 600);

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.Equal(
            """
            stored 4 images in /Work/images/
            /Work/images/manual-p1-1.png  p1, p2, p3  300×100  4 KB  at 20,20,120,40
            /Work/images/manual-p1-2.png  p1  800×600  49 KB  at 72,100,400,300
            /Work/images/manual-p2-2.png  p2  800×600  49 KB  at 72,100,400,300
            /Work/images/manual-p3-2.png  p3  800×600  49 KB  at 72,100,400,300
            """,
            result.Output);
        Assert.DoesNotContain("image 2 1", _pixels.Calls);
        Assert.DoesNotContain("image 3 1", _pixels.Calls);
        Assert.Equal(4, (await StoredUnder("/Work/images")).Count);
    }

    [Fact]
    public async Task Small_ones_are_left_out_and_counted_all_keeps_them_under_the_same_numbers()
    {
        await Upload();
        _pixels.Pages[2] = [Image(1, 16, 16, 40, 40, 8, 8, "dot", small: true), Image(2, 800, 600, 72, 100, 400, 300, "photo"), Image(3, 16, 16, 40, 60, 8, 8, "rule", small: true)];
        _pixels.Images[(2, 1)] = Png(100, 16, 16);
        _pixels.Images[(2, 2)] = Png(50_000, 800, 600);
        _pixels.Images[(2, 3)] = Png(101, 16, 16);

        var plain = await Run("pdfimages /Work/manual.pdf 2 /Work/plain/");
        var all = await Run("pdfimages --all /Work/manual.pdf 2 /Work/all/");

        Assert.Equal(
            "stored 1 image in /Work/plain/ (2 left out as smaller than 1 % of their page, --all keeps them)\n/Work/plain/manual-p2-2.png  p2  800×600  49 KB  at 72,100,400,300",
            plain.Output);
        Assert.Equal(
            """
            stored 3 images in /Work/all/
            /Work/all/manual-p2-1.png  p2  16×16  100 B  at 40,40,8,8
            /Work/all/manual-p2-2.png  p2  800×600  49 KB  at 72,100,400,300
            /Work/all/manual-p2-3.png  p2  16×16  101 B  at 40,60,8,8
            """,
            all.Output);
    }

    [Fact]
    public async Task A_jpeg_answer_is_named_jpg_and_a_scaled_down_one_says_from_what()
    {
        await Upload();
        _pixels.Pages[1] = [Image(1, 4000, 3000, 0, 0, 595, 842, "big"), Image(2, 5000, 2000, 10, 10, 300, 120, "wide")];
        _pixels.Images[(1, 1)] = Jpeg(6_501_171, 3264, 2448) with { OriginalWidth = 4000, OriginalHeight = 3000 };
        _pixels.Images[(1, 2)] = Png(1_000_000, 4472, 1788, 5000, 2000);

        var result = await Run("pdfimages /Work/manual.pdf 1 /Work/images/");

        Assert.Equal(
            """
            stored 2 images in /Work/images/
            /Work/images/manual-p1-1.jpg  p1  3264×2448 (scaled down from 4000×3000)  6.2 MB (JPEG: PNG over 15 MB)  at 0,0,595,842
            /Work/images/manual-p1-2.png  p1  4472×1788 (scaled down from 5000×2000)  977 KB  at 10,10,300,120
            """,
            result.Output);
        Assert.Equal("image/jpeg", (await Stored("/Work/images/manual-p1-1.jpg"))!.Current.MediaType);
        Assert.Null(await Stored("/Work/images/manual-p1-1.png"));
    }

    [Fact]
    public async Task Images_too_large_or_not_decoded_are_named_as_skipped()
    {
        await Upload();
        _pixels.Pages[1] = [Image(1, 300, 200, 10, 10, 100, 80, null, failed: true), Image(2, 9000, 9000, 0, 0, 595, 842, "noise"), Image(3, 1200, 400, 72, 40, 451, 150, "aa")];
        _pixels.ImageFails[(1, 2)] = new PixelsUnavailableException("image 2 on page 1 of /Work/manual.pdf is over 15 MB even as JPEG at quality 60", PixelsProblem.TooLarge);
        _pixels.Images[(1, 3)] = Png(90_112, 1200, 400);

        var result = await Run("pdfimages /Work/manual.pdf 1 /Work/images/");

        Assert.Equal(
            """
            stored 1 image in /Work/images/
            p1 #1 skipped: could not be decoded
            p1 #2 skipped: over 15 MB even as JPEG
            /Work/images/manual-p1-3.png  p1  1200×400  88 KB  at 72,40,451,150
            """,
            result.Output);
        Assert.False(result.IsError);
        Assert.DoesNotContain("image 1 1", _pixels.Calls);
        Assert.Equal(["/Work/images/manual-p1-3.png"], await StoredUnder("/Work/images"));
    }

    [Fact]
    public async Task An_existing_target_refuses_and_writes_nothing()
    {
        await TwoPages();
        await _files.WriteAsync("/Work/images/manual-p3-1.jpg", Jpeg(100, 1, 1).Data, (1, 1), null, Ct);
        await _files.WriteAsync("/Work/images/manual-p1-1.png/old.png", Png(100, 1, 1).Data, (1, 1), null, Ct);
        await _files.WriteAsync("/Work/images/other.png", Png(101, 1, 1).Data, (1, 1), null, Ct);

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.Equal(
            ShellResult.Error("pdfimages: these exist already: /Work/images/manual-p1-1.png, /Work/images/manual-p3-1.jpg; choose another folder, or remove them first") with { Kind = ShellErrorKind.Conflict },
            result);
        Assert.DoesNotContain(_pixels.Calls, c => c.StartsWith("image", StringComparison.Ordinal));
        Assert.Equal(["/Work/images/manual-p1-1.png/old.png", "/Work/images/manual-p3-1.jpg", "/Work/images/other.png"], (await StoredUnder("/Work/images")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_existing_note_of_a_targets_name_refuses()
    {
        await TwoPages();
        await new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).WriteAsync("/Work/images/manual-p1-1.png/notes.md", "a note\n", null, Ct);

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.Equal("pdfimages: these exist already: /Work/images/manual-p1-1.png; choose another folder, or remove them first", result.Output);
        Assert.Empty(await StoredUnder("/Work/images"));
    }

    [Fact]
    public async Task More_than_500_to_store_refuses_with_a_range_that_fits()
    {
        await Upload();
        _pixels.Pages[1] = [.. Enumerable.Range(1, 200).Select(n => Image(n, 100, 100, 0, 0, 100, 100, $"a{n}"))];
        _pixels.Pages[2] = [.. Enumerable.Range(1, 200).Select(n => Image(n, 100, 100, 0, 0, 100, 100, $"b{n}")), Image(201, 100, 100, 0, 0, 100, 100, "a1")];
        _pixels.Pages[3] = [.. Enumerable.Range(1, 101).Select(n => Image(n, 100, 100, 0, 0, 100, 100, $"c{n}")), Image(102, 4, 4, 0, 0, 2, 2, "dot", small: true)];

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.Equal(ShellResult.Error("pdfimages: 501 images in pages 1-3, more than 500 in one call; give a page range, e.g. 1-2") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(["list 1", "list 2", "list 3"], _pixels.Calls);
        Assert.Empty(await StoredUnder("/Work/images"));
    }

    [Fact]
    public async Task More_than_500_on_the_first_page_alone_says_so()
    {
        await Upload();
        _pixels.Pages[2] = [.. Enumerable.Range(1, 501).Select(n => Image(n, 100, 100, 0, 0, 100, 100, $"a{n}"))];

        var result = await Run("pdfimages /Work/manual.pdf 2- /Work/images/");

        Assert.Equal("pdfimages: 501 images on page 2, more than 500 in one call; store that page's pictures in regions with --crop", result.Output);
        Assert.Empty(await StoredUnder("/Work/images"));
    }

    [Fact]
    public async Task A_full_vault_stops_keeping_what_was_stored_and_naming_the_rest()
    {
        await TwoPages();
        _pixels.Pages[2] = [Image(1, 640, 480, 0, 0, 320, 240, "cc")];
        _pixels.Images[(2, 1)] = Png(30_000, 640, 480);
        var second = _pixels.Images[(2, 1)].Data;
        _store.BlobFails = b => b.Span.SequenceEqual(second) ? new QuotaExceededException(owner: true) : null;

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.Equal(
            """
            stored 1 image in /Work/images/
            /Work/images/manual-p1-1.png  p1  1200×400  88 KB  at 72,40,451,150
            not stored: p2 #1, p3 #1
            This vault is out of space. See `df`.
            """,
            result.Output);
        Assert.True(result.IsError);
        Assert.Equal((ShellErrorKind.Refused, Refusals.Quota), (result.Kind, result.Code));
        Assert.Equal(["/Work/images/manual-p1-1.png"], await StoredUnder("/Work/images"));
        Assert.DoesNotContain("image 3 1", _pixels.Calls);
    }

    [Fact]
    public async Task A_full_vault_tells_a_member_the_sentence_alone()
    {
        await TwoPages();
        _store.BlobFails = _ => new QuotaExceededException(owner: false);

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.Equal(ShellResult.Error(QuotaExceededException.Sentence) with { Kind = ShellErrorKind.Refused }, result);
    }

    [Fact]
    public async Task A_refusal_of_the_writer_is_quoted()
    {
        await TwoPages();
        var reader = new FixedCaller(Writers.Dev, CanWriteVault: _ => false);
        var sh = new Shell(_store, new NoteWriter(_store, _time, reader), new NoteMover(_store, _time, reader), pixels: _pixels);
        var refused = (await new AttachmentWriter(_store, _store, _time, reader).WriteAsync("/Work/images/manual-p1-1.png", _pixels.Images[(1, 1)].Data, (1200, 400), null, Ct)).Result;

        var result = await sh.RunAsync("pdfimages /Work/manual.pdf /Work/images/", Ct);

        Assert.StartsWith("upload: ", refused.Output);
        Assert.Equal(refused with { Output = "pdfimages: " + refused.Output }, result);
        Assert.Equal(ShellErrorKind.Refused, result.Kind);
        Assert.Empty(await StoredUnder("/Work/images"));
    }

    [Fact]
    public async Task A_failure_of_the_writer_midway_keeps_what_was_stored()
    {
        await TwoPages();
        _pixels.Images[(3, 1)] = Png(1000, 2400, 1600) with { MediaType = "image/jpeg" };

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Invalid, result.Kind);
        Assert.Equal(
            "stored 1 image in /Work/images/\n/Work/images/manual-p1-1.png  p1  1200×400  88 KB  at 72,40,451,150\nnot stored: p3 #1\npdfimages: upload: manual-p3-1.jpg isn't a JPEG image (its content doesn't match its name)",
            result.Output);
    }

    [Fact]
    public async Task Pages_with_images_over_50_megapixels_are_named()
    {
        await TwoPages();
        _pixels.Over[2] = 1;

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.EndsWith("\npage 2: 1 image over 50 MP left out (look with pdf --crop)", result.Output);
    }

    [Fact]
    public async Task Writing_stops_at_a_page_boundary_once_the_budget_is_spent()
    {
        await Upload();
        _pixels.PerCall = TimeSpan.FromSeconds(3);
        _pixels.Pages[1] = [Image(1, 100, 100, 0, 0, 100, 100, "a")];
        _pixels.Pages[2] = [.. Enumerable.Range(1, 4).Select(n => Image(n, 100, 100, 0, 0, 100, 100, $"b{n}"))];
        _pixels.Pages[3] = [Image(1, 100, 100, 0, 0, 100, 100, "c")];
        _pixels.Images[(1, 1)] = Png(1000, 100, 100);
        for (var n = 1; n <= 4; n++) _pixels.Images[(2, n)] = Png(1000 + n, 100, 100);
        _pixels.Images[(3, 1)] = Png(2000, 100, 100);

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.Equal(["list 1", "list 2", "list 3", "image 1 1", "image 2 1", "image 2 2", "image 2 3", "image 2 4"], _pixels.Calls);
        Assert.StartsWith("stored 5 images in /Work/images/\n", result.Output);
        Assert.EndsWith("/Work/images/manual-p2-4.png  p2  100×100  1004 B  at 0,0,100,100\nstored 5 images; go on with pages 3-", result.Output);
        Assert.False(result.IsError);
        Assert.Equal(5, (await StoredUnder("/Work/images")).Count);
    }

    [Fact]
    public async Task Writing_names_the_pages_listing_left_for_later()
    {
        await Upload();
        _pixels.PageCount = 10;
        _pixels.PerCall = TimeSpan.FromSeconds(4);
        _pixels.Pages[2] = [Image(1, 1200, 400, 72, 40, 451, 150, "aa")];
        _pixels.Images[(2, 1)] = Png(90_112, 1200, 400);

        var result = await Run("pdfimages /Work/manual.pdf 1-8 /Work/images/");

        Assert.Equal(["list 1", "list 2", "list 3", "image 2 1"], _pixels.Calls);
        Assert.Equal("stored 1 image in /Work/images/\n/Work/images/manual-p2-1.png  p2  1200×400  88 KB  at 72,40,451,150\nstored 1 image; go on with pages 4-8", result.Output);
    }

    [Fact]
    public async Task The_ceiling_stops_within_a_page_and_says_what_of_it_was_stored()
    {
        await Upload();
        _pixels.PerCall = TimeSpan.FromSeconds(10);
        _pixels.Pages[1] = [.. Enumerable.Range(1, 4).Select(n => Image(n, 100, 100, 0, 0, 100, 100, $"a{n}"))];
        for (var n = 1; n <= 4; n++) _pixels.Images[(1, n)] = Png(1000 + n, 100, 100);
        _sh.Register(new PdfImages(_store, _pixels, _files, _time) { Ceiling = TimeSpan.FromSeconds(30) });

        var result = await Run("pdfimages /Work/manual.pdf 1 /Work/images/");

        Assert.Equal(["list 1", "image 1 1", "image 1 2"], _pixels.Calls);
        Assert.Equal(
            """
            stored 2 images in /Work/images/
            /Work/images/manual-p1-1.png  p1  100×100  1001 B  at 0,0,100,100
            /Work/images/manual-p1-2.png  p1  100×100  1002 B  at 0,0,100,100
            page 1 is stored only in part, as time ran out: /Work/images/manual-p1-1.png, /Work/images/manual-p1-2.png; to store page 1, run it again into another folder, or remove these files first
            stored 2 images; go on with page 1
            """,
            result.Output);
    }

    [Fact]
    public async Task Busy_while_fetching_an_image_is_tried_again()
    {
        await TwoPages();
        _pixels.Pages[3] = [];
        _pixels.ImageThrows.Enqueue(new PixelsUnavailableException("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy));

        var result = await Run("pdfimages /Work/manual.pdf 1 /Work/images/");

        Assert.False(result.IsError, result.Output);
        Assert.NotNull(await Stored("/Work/images/manual-p1-1.png"));
        Assert.Equal(["list 1", "image 1 1", "image 1 1"], _pixels.Calls);
    }

    [Fact]
    public async Task A_sidecar_failure_while_fetching_stops_keeping_what_was_stored()
    {
        await TwoPages();
        _pixels.ImageFails[(3, 1)] = new PixelsUnavailableException("PDF image extraction is unavailable right now; try again later", PixelsProblem.Unavailable);

        var result = await Run("pdfimages /Work/manual.pdf /Work/images/");

        Assert.Equal(ShellErrorKind.Unavailable, result.Kind);
        Assert.Equal(
            "stored 1 image in /Work/images/\n/Work/images/manual-p1-1.png  p1  1200×400  88 KB  at 72,40,451,150\nnot stored: p3 #1\npdfimages: PDF image extraction is unavailable right now; try again later",
            result.Output);
    }

    private const string Diagram = "/Work/Pump manual/images/wiring diagram";

    private async Task PumpManual()
    {
        await Upload("/Work/Pump manual.pdf");
        _pixels.Region = Png(421_888, 1958, 1583, 595, 842);
    }

    [Fact]
    public async Task Crop_stores_a_rendered_region_with_its_line()
    {
        await PumpManual();

        var result = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}.png\"");

        Assert.Equal(ShellResult.Ok($"stored {Diagram}.png  p12  1958×1583  412 KB  at 60,120,470,380"), result);
        Assert.Equal(["region 12 60,120,470,380 0 png"], _pixels.Calls);
        var stored = await Stored(Diagram + ".png");
        Assert.NotNull(stored);
        Assert.Equal(("image/png", 1958, 1583, 421_888L), (stored.Current.MediaType, stored.Current.Width, stored.Current.Height, stored.Current.Size));
        Assert.Equal(_pixels.Region!.Data, await StoreAttachments.ReadBytesAsync(_store, stored.Path, stored.Current, Ct));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".JPG")]
    public async Task Crop_into_a_jpg_asks_for_a_jpeg(string extension)
    {
        await PumpManual();
        _pixels.Region = Jpeg(300_000, 1958, 1583) with { OriginalWidth = 595, OriginalHeight = 842 };

        var result = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}{extension}\"");

        Assert.Equal($"stored {Diagram}{extension}  p12  1958×1583  293 KB  at 60,120,470,380", result.Output);
        Assert.Equal(["region 12 60,120,470,380 0 jpeg"], _pixels.Calls);
        Assert.Equal("image/jpeg", (await Stored(Diagram + extension))!.Current.MediaType);
    }

    [Fact]
    public async Task Crop_into_another_format_asks_for_nothing()
    {
        await PumpManual();

        var result = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}.gif\"");

        Assert.Equal(Usage($"--crop stores one file, named .png, .jpg or .jpeg, not {Diagram}.gif"), result);
        Assert.Empty(_pixels.Calls);
    }

    [Fact]
    public async Task Crop_turned_asks_for_the_turn_and_says_so()
    {
        await PumpManual();
        _pixels.Region = Png(421_888, 1583, 1958, 595, 842);

        var result = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 120,60,380,470 --rotate -90 \"{Diagram}.png\"");

        Assert.Equal($"stored {Diagram}.png  p12  1583×1958  412 KB  at 120,60,380,470 of the page turned 270°", result.Output);
        Assert.Equal(["region 12 120,60,380,470 270 png"], _pixels.Calls);
    }

    [Fact]
    public async Task Crop_scaled_down_says_from_what()
    {
        await PumpManual();
        _pixels.Region = Png(5_000_000, 3384, 2391, 842, 595);

        var result = await Run($"pdfimages \"/Work/Pump manual.pdf\" 1 --crop 0,0,842,595 --rotate 90 \"{Diagram}.png\"");

        Assert.Equal($"stored {Diagram}.png  p1  3384×2391 (scaled down from 3508×2479)  4.8 MB  at 0,0,842,595 of the page turned 90°", result.Output);
    }

    [Fact]
    public async Task Crop_over_an_existing_target_refuses_and_writes_nothing()
    {
        await PumpManual();
        var before = Png(1000, 10, 10);
        Assert.False((await _files.WriteAsync(Diagram + ".png", before.Data, (10, 10), null, Ct)).Result.IsError);
        await new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).WriteAsync($"{Diagram}.jpg/notes.md", "a note\n", null, Ct);

        var file = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}.png\"");
        var folder = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}.jpg\"");

        Assert.Equal(ShellResult.Error($"pdfimages: {Diagram}.png exists already; choose another name, or remove it first") with { Kind = ShellErrorKind.Conflict }, file);
        Assert.Equal(ShellResult.Error($"pdfimages: {Diagram}.jpg exists already; choose another name, or remove it first") with { Kind = ShellErrorKind.Conflict }, folder);
        Assert.Empty(_pixels.Calls);
        var stored = (await Stored(Diagram + ".png"))!;
        Assert.Equal(before.Data, await StoreAttachments.ReadBytesAsync(_store, stored.Path, stored.Current, Ct));
        Assert.Null(await Stored(Diagram + ".jpg"));
    }

    [Fact]
    public async Task Crop_too_large_as_png_names_jpg()
    {
        await PumpManual();
        _pixels.Throws.Enqueue(new PixelsUnavailableException("the region is over 15 MB as PNG; name the target .jpg", PixelsProblem.TooLarge));

        var result = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}.png\"");

        Assert.Equal(ShellResult.Error("pdfimages: the region is over 15 MB as PNG; name the target .jpg") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Null(await Stored(Diagram + ".png"));
    }

    [Fact]
    public async Task Crop_outside_the_page_or_past_the_end_is_named()
    {
        await PumpManual();
        _pixels.Throws.Enqueue(new PixelsUnavailableException("the crop 0,0,900,900 lies outside page 2", PixelsProblem.BadCrop));
        _pixels.Throws.Enqueue(new PixelsUnavailableException("/Work/Pump manual.pdf has no page 40", PixelsProblem.NoSuchPage));

        var outside = await Run($"pdfimages \"/Work/Pump manual.pdf\" 2 --crop 0,0,900,900 \"{Diagram}.png\"");
        var past = await Run($"pdfimages \"/Work/Pump manual.pdf\" 40 --crop 0,0,100,100 \"{Diagram}.png\"");

        Assert.Equal(ShellResult.Error("pdfimages: the crop 0,0,900,900 lies outside page 2") with { Kind = ShellErrorKind.Invalid }, outside);
        Assert.Equal(ShellResult.Error("pdfimages: /Work/Pump manual.pdf has no page 40") with { Kind = ShellErrorKind.Invalid }, past);
        Assert.Null(await Stored(Diagram + ".png"));
    }

    [Fact]
    public async Task Crop_asks_again_while_the_sidecar_is_busy()
    {
        await PumpManual();
        _pixels.Throws.Enqueue(new PixelsUnavailableException("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy));
        _pixels.Throws.Enqueue(new PixelsUnavailableException("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy));

        var result = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}.png\"");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(3, _pixels.Calls.Count);
        Assert.NotNull(await Stored(Diagram + ".png"));
    }

    [Fact]
    public async Task Crop_into_a_full_vault_gives_the_sentence()
    {
        await PumpManual();
        _store.BlobFails = _ => new QuotaExceededException(owner: true);

        var result = await Run($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}.png\"");

        Assert.Equal(ShellResult.Error("This vault is out of space. See `df`.") with { Kind = ShellErrorKind.Refused, Code = Refusals.Quota }, result);
    }

    [Fact]
    public async Task Crop_refused_by_the_writer_is_quoted()
    {
        await PumpManual();
        var reader = new FixedCaller(Writers.Dev, CanWriteVault: _ => false);
        var sh = new Shell(_store, new NoteWriter(_store, _time, reader), new NoteMover(_store, _time, reader), pixels: _pixels);
        var refused = (await new AttachmentWriter(_store, _store, _time, reader).WriteAsync(Diagram + ".png", _pixels.Region!.Data, (1958, 1583), null, Ct)).Result;

        var result = await sh.RunAsync($"pdfimages \"/Work/Pump manual.pdf\" 12 --crop 60,120,470,380 \"{Diagram}.png\"", Ct);

        Assert.StartsWith("upload: ", refused.Output);
        Assert.Equal(refused with { Output = "pdfimages: " + refused.Output }, result);
        Assert.Null(await Stored(Diagram + ".png"));
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
        Assert.Contains("at most 500 images in one call", own.Output);
        Assert.Contains("300 dpi", own.Output);
        Assert.Contains("Only drawings need it", own.Output);
        Assert.Contains("x,y,w,h counts in the turned page", own.Output);
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
        var notesOnly = NotesOnly.Over(store);
        Assert.False(new Shell(notesOnly, new NoteWriter(notesOnly, time, caller), new NoteMover(notesOnly, time, caller), pixels: new FakePixels(time)).HasCommand("pdfimages"));
    }

    /// <summary>A store of notes that keeps no attachments: the calls of <see cref="INoteStore"/> passed on, and nothing else.</summary>
    public class NotesOnly : DispatchProxy
    {
        private INoteStore _store = null!;

        public static INoteStore Over(INoteStore store)
        {
            var proxy = Create<INoteStore, NotesOnly>();
            ((NotesOnly)(object)proxy)._store = store;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Invoke(_store, args);
    }
}
