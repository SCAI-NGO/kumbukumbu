// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>pdf</c>: a PDF read page by page, as text, rendered pages or a crop; and <c>cat</c>'s line for a PDF.</summary>
public class PdfCommandTests
{
    private const string Dropped = "If no image follows, your client dropped it: tell the user so instead of guessing what it shows.";
    private const string Unavailable = "PDF rendering is unavailable right now; try again later";
    private const string Locked = "this PDF is password-protected; Kumbu² can't read it";

    private sealed class FakePixels : IAttachmentPixels
    {
        public Dictionary<string, PdfAnalysis> Analyses { get; } = [];

        public Dictionary<string, Func<int, string>> Texts { get; } = [];

        /// <summary>Page text that can't be read, as with a text part missing.</summary>
        public bool TextsMissing { get; set; }

        /// <summary>Thrown by <see cref="AnalysisAsync"/>, as for a stored failure.</summary>
        public Exception? AnalysisFails { get; set; }

        /// <summary>The exception a page's render throws, or null to render it.</summary>
        public Func<int, Exception?> PageFails { get; set; } = _ => null;

        public List<(int Page, SizeStep Step, Crop? Crop)> PageRequests { get; } = [];

        public List<bool> AnalysisRequests { get; } = [];

        public Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, CancellationToken ct) => throw new NotSupportedException();

        public Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, CancellationToken ct)
        {
            PageRequests.Add((page, step, crop));
            if (PageFails(page) is { } e) throw e;
            return Task.FromResult(new Picture(new byte[1024 + page], "image/png", 1109, 1568, 595, 842));
        }

        public Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct)
        {
            AnalysisRequests.Add(runIfPending);
            if (AnalysisFails is not null) throw AnalysisFails;
            return Task.FromResult(Analyses.GetValueOrDefault(path.Value));
        }

        public Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct)
        {
            if (!Analyses.ContainsKey(path.Value) || TextsMissing) return Task.FromResult<IReadOnlyList<string>?>(null);
            var text = Texts.GetValueOrDefault(path.Value, n => $"Text of page {n}");
            return Task.FromResult<IReadOnlyList<string>?>([.. Enumerable.Range(first, last - first + 1).Select(text)]);
        }

        public void Count(AttachmentUse use)
        {
        }
    }

    private readonly InMemoryNoteStore _store = new(("/Papers/plan.md", "a note\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 29, 11, 0, 0, DateTimeKind.Utc));
    private readonly FakePixels _pixels = new();
    private readonly AttachmentWriter _files;
    private readonly Shell _sh;

    public PdfCommandTests()
    {
        var caller = new FixedCaller(Writers.Dev);
        _files = new AttachmentWriter(_store, _store, _time, caller);
        _sh = new Shell(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller), pixels: _pixels);
    }

    private static CancellationToken Ct => CancellationToken.None;

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, Ct);

    private static PdfPage Text(int n, int chars = 400, int images = 0, double imageCoverage = 0) =>
        new(n, 595, 842, 0, PageType.Text, chars, 0.3, imageCoverage, 0, images, []);

    private static PdfPage Mixed(int n, double vector = 0.35, PdfRegion[]? regions = null) =>
        new(n, 595, 842, 0, PageType.Mixed, 900, 0.2, 0.004, vector, 1, regions ?? []);

    private static PdfPage Scan(int n) => new(n, 595, 842, 0, PageType.Image, 0, 0, 0.98, 0, 1, [new PdfRegion("image", 0, 0, 595, 842)]);

    /// <summary>12 pages: 1-3 text (page 2 with a small image), 4 mixed, 5-12 text.</summary>
    private static PdfAnalysis Twelve() => new("pdfjs-dist@6.4.299+rules.1", 12, 12, "Attention to detail", "A. Smith", [
        Text(1), Text(2, images: 1, imageCoverage: 0.03), Text(3),
        Mixed(4, regions: [new PdfRegion("drawing", 72, 300, 451, 260), new PdfRegion("image", 80, 90, 200, 120)]),
        .. Enumerable.Range(5, 8).Select(n => Text(n)),
    ]);

    /// <summary>12 pages, not text on 2, 4, 7, 9 and 11 (7 and 9 scans).</summary>
    private static PdfAnalysis Figures() => new("a", 12, 12, null, null,
        [.. Enumerable.Range(1, 12).Select(n => n switch { 2 or 4 or 11 => Mixed(n), 7 or 9 => Scan(n), _ => Text(n) })]);

    private static PdfAnalysis Plain(int pages, int analysed = -1) =>
        new("a", pages, analysed < 0 ? pages : analysed, null, null, [.. Enumerable.Range(1, analysed < 0 ? pages : analysed).Select(n => Text(n))]);

    private static byte[] Pdf(int size) => [.. "%PDF-1.7"u8.ToArray(), .. new byte[size - 8]];

    private async Task<string> Upload(string path, PdfAnalysis? analysis, int size = 2_202_009)
    {
        var result = await _files.WriteAsync(path, Pdf(size), null, null, Ct);
        Assert.False(result.Result.IsError, result.Result.Output);
        if (analysis is not null) _pixels.Analyses[path] = analysis;
        return result.Attachment!.Version;
    }

    private static string[] Texts(ShellResult result) => [.. result.Parts!.OfType<TextPart>().Select(t => t.Text)];

    private static string Kinds(ShellResult result) => string.Concat(result.Parts!.Select(p => p is ImagePart ? "I" : "T"));

    [Fact]
    public async Task Text_pages_start_with_a_header_that_says_what_was_found()
    {
        await Upload("/Papers/a.pdf", Twelve());

        var result = await Run("pdf /Papers/a.pdf 2-4");

        Assert.False(result.IsError, result.Output);
        Assert.Null(result.Parts);
        Assert.Equal("""
            --- Page 2 of 12: text (1 small image ≈ 3 %) ---
            Text of page 2

            --- Page 3 of 12: text ---
            Text of page 3

            --- Page 4 of 12: mixed (text; vector drawing ≈ 35 % of the page) — add --images to see it ---
            Text of page 4
            """.ReplaceLineEndings("\n"), result.Output);
        Assert.Empty(_pixels.PageRequests);
        Assert.Equal([true], _pixels.AnalysisRequests);
    }

    /// <summary>9 pages of text; page 5 holds two charts of about 11 % each, page 6 two photos of 12 % together.</summary>
    private static PdfAnalysis Charts() => new("a", 9, 9, null, null, [
        .. Enumerable.Range(1, 9).Select(n => n switch
        {
            5 => new PdfPage(5, 595, 842, 0, PageType.Text, 2400, 0.4, 0, 0.22, 0,
                [new PdfRegion("drawing", 60, 120, 230, 240), new PdfRegion("drawing", 305, 120, 230, 240)]),
            6 => new PdfPage(6, 595, 842, 0, PageType.Text, 2000, 0.4, 0.12, 0, 2,
                [new PdfRegion("image", 60, 500, 230, 260), new PdfRegion("image", 305, 500, 230, 260)]),
            _ => Text(n),
        }),
    ]);

    [Fact]
    public async Task A_text_page_with_figures_of_5_percent_or_more_says_how_to_see_them()
    {
        await Upload("/Papers/c.pdf", Charts());

        var text = await Run("pdf /Papers/c.pdf 4-6");
        var images = await Run("pdf /Papers/c.pdf 5 --images");

        Assert.Equal("""
            --- Page 4 of 9: text ---
            Text of page 4

            --- Page 5 of 9: text (vector drawing ≈ 22 % of the page) — add --images=all or --crop to see it ---
            Text of page 5

            --- Page 6 of 9: text (2 images ≈ 12 % of the page) — add --images=all or --crop to see it ---
            Text of page 6
            """.ReplaceLineEndings("\n"), text.Output);
        Assert.Equal("--- Page 5 of 9: text (vector drawing ≈ 22 % of the page) — add --images=all or --crop to see it ---\nText of page 5", images.Output);
        Assert.Empty(_pixels.PageRequests);
    }

    [Fact]
    public async Task Info_lists_the_text_pages_with_figures_and_leaves_out_small_ones()
    {
        await Upload("/Papers/c.pdf", Charts() with { Pages = [.. Charts().Pages.Select(p => p.Number == 2 ? Text(2, images: 1, imageCoverage: 0.04) : p)] });

        var lines = (await Run("pdf info /Papers/c.pdf")).Output.Split('\n');

        Assert.Equal([
            "c.pdf: PDF, 9 pages, 2.1 MB",
            "Pages: 1-9 text",
            "p5 text: vector drawing ≈ 22 % — regions (pt, x,y,w,h): drawing 60,120,230,240; drawing 305,120,230,240",
            "p6 text: 2 images ≈ 12 % — regions (pt, x,y,w,h): image 60,500,230,260; image 305,500,230,260",
        ], lines);
    }

    [Fact]
    public async Task A_scan_says_it_has_no_text_layer_and_an_empty_page_says_so()
    {
        await Upload("/Papers/b.pdf", Figures());
        _pixels.Texts["/Papers/b.pdf"] = n => n == 7 ? "" : $"Text of page {n}";
        await Upload("/Papers/e.pdf", new PdfAnalysis("a", 1, 1, null, null, [Text(1, chars: 0)]));
        _pixels.Texts["/Papers/e.pdf"] = _ => "";

        Assert.Equal("--- Page 7 of 12: image (no text layer; one image covers the page) — add --images to see it ---", (await Run("pdf /Papers/b.pdf 7")).Output);
        Assert.Equal("--- Page 1 of 1: text (empty) ---", (await Run("pdf /Papers/e.pdf")).Output);
    }

    [Fact]
    public async Task Images_renders_the_pages_that_are_not_text_and_stops_after_the_fourth()
    {
        var version = await Upload("/Papers/b.pdf", Figures());

        var result = await Run("pdf /Papers/b.pdf 1-12 --images");

        Assert.False(result.IsError, result.Output);
        Assert.Equal([2, 4, 7, 9], _pixels.PageRequests.Select(r => r.Page));
        Assert.All(_pixels.PageRequests, r => Assert.Equal((SizeStep.Medium, (Crop?)null), (r.Step, r.Crop)));
        Assert.Equal("TITITITIT", Kinds(result));
        var texts = Texts(result);
        Assert.Equal($"""
            --- Page 1 of 12: text ---
            Text of page 1

            --- Page 2 of 12: mixed (text; vector drawing ≈ 35 % of the page) ---
            Text of page 2
            /Papers/b.pdf (version {version}), page 2 of 12 (mixed): page 595×842 pt; medium: 1109×1568 px PNG, 1 KB
            {Dropped}
            """.ReplaceLineEndings("\n"), texts[0]);
        Assert.Contains("\n\n--- Page 7 of 12: image (no text layer; one image covers the page) ---\nText of page 7\n/Papers/b.pdf (version ", texts[2]);
        Assert.Equal("--- Stopped after page 9: at most 4 images per call. Continue with: pdf /Papers/b.pdf 10-12 --images ---", texts[4]);
        Assert.Equal(1024 + 9, Assert.IsType<ImagePart>(result.Parts![7]).Data.Length);
        Assert.Equal(string.Join("\n\n", texts), result.Output);
    }

    [Fact]
    public async Task Images_all_renders_every_page_and_keeps_the_flag_and_the_size_in_the_continuation()
    {
        await Upload("/Papers/a.pdf", Twelve());

        var result = await Run("pdf /Papers/a.pdf 1-12 --images=all --size large");

        Assert.Equal([1, 2, 3, 4], _pixels.PageRequests.Select(r => r.Page));
        Assert.All(_pixels.PageRequests, r => Assert.Equal(SizeStep.Large, r.Step));
        Assert.Equal("TITITITIT", Kinds(result));
        Assert.StartsWith("--- Page 1 of 12: text ---\nText of page 1\n", Texts(result)[0]);
        Assert.Equal("--- Stopped after page 4: at most 4 images per call. Continue with: pdf /Papers/a.pdf 5-12 --images=all --size large ---", Texts(result)[^1]);
    }

    [Fact]
    public async Task The_fourth_image_on_the_last_page_needs_no_continuation()
    {
        await Upload("/Papers/a.pdf", Twelve());

        var result = await Run("pdf /Papers/a.pdf 9-12 --images=all");

        Assert.Equal("TITITITI", Kinds(result));
        Assert.DoesNotContain("Stopped", result.Output);
    }

    [Fact]
    public async Task Text_stops_after_twenty_pages_with_the_range_to_continue_with()
    {
        await Upload("/Papers/long.pdf", Plain(30));

        var result = await Run("pdf /Papers/long.pdf 1-30");
        var open = await Run("pdf /Papers/long.pdf");

        Assert.Contains("--- Page 20 of 30: text ---\nText of page 20", result.Output);
        Assert.DoesNotContain("Page 21", result.Output);
        Assert.EndsWith("\n\n--- Stopped after page 20: at most 20 pages per call. Continue with: pdf /Papers/long.pdf 21-30 ---", result.Output);
        Assert.EndsWith("Continue with: pdf /Papers/long.pdf 21- ---", open.Output);
    }

    [Fact]
    public async Task Text_stops_before_a_page_that_would_pass_the_output_limit()
    {
        await Upload("/Papers/big.pdf", Plain(30));
        _pixels.Texts["/Papers/big.pdf"] = n => new string((char)('a' + n % 26), 5000);

        var result = await Run("pdf /Papers/big.pdf 1-30");

        Assert.True(result.Output.Length <= OutputLimit.MaxChars, $"{result.Output.Length}");
        Assert.Contains("--- Page 4 of 30: text ---", result.Output);
        Assert.DoesNotContain("Page 5 of", result.Output);
        Assert.EndsWith("\n\n--- Stopped after page 4: the next page would pass the output limit. Continue with: pdf /Papers/big.pdf 5-30 ---", result.Output);
        Assert.DoesNotContain("truncated", result.Output);
    }

    [Fact]
    public async Task A_single_page_longer_than_the_limit_is_cut_with_a_note()
    {
        await Upload("/Papers/huge.pdf", Plain(3));
        _pixels.Texts["/Papers/huge.pdf"] = n => string.Join('\n', Enumerable.Repeat(new string('x', 99), 600));

        var result = await Run("pdf /Papers/huge.pdf 1-3");

        Assert.True(result.Output.Length <= OutputLimit.MaxChars, $"{result.Output.Length}");
        Assert.StartsWith("--- Page 1 of 3: text ---\nxxx", result.Output);
        Assert.Contains("\n--- Page 1 is cut here: it has 59,999 characters, more than one call shows ---", result.Output);
        Assert.EndsWith("\n\n--- Stopped after page 1: page 1 was cut at the output limit, and pdf can't show the rest of it. Continue with: pdf /Papers/huge.pdf 2-3 ---", result.Output);
    }

    [Theory]
    [InlineData("pdf /Papers/a.pdf 4-5 --crop 72,300,451,260", "pdf: --crop takes a single page, e.g. pdf /Papers/a.pdf 4 --crop 72,300,451,260")]
    [InlineData("pdf /Papers/a.pdf --crop 72,300,451,260", "pdf: --crop takes a single page, e.g. pdf /Papers/a.pdf 1 --crop 72,300,451,260")]
    [InlineData("pdf /Papers/a.pdf 4 --crop 72,300,451,900", "pdf: the crop 72,300,451,900 lies outside page 4 (595×842 pt)")]
    [InlineData("pdf /Papers/a.pdf 4 --crop 1,2,3", "pdf: '1,2,3' is not a crop")]
    [InlineData("pdf /Papers/a.pdf 13", "pdf: /Papers/a.pdf has 12 pages; there is no page 13")]
    [InlineData("pdf /Papers/a.pdf 9-5", "pdf: '9-5' is not a page range: the first page comes first, e.g. 5-9")]
    [InlineData("pdf /Papers/a.pdf x", "pdf: 'x' is not a page range: give N, N-M or N- (pages count from 1), e.g. 5-9")]
    [InlineData("pdf /Papers/a.pdf 4 --size huge", "pdf: --size is small, medium or large, not 'huge' (see 'help pdf')")]
    [InlineData("pdf /Papers/a.pdf 4 --images=some", "pdf: --images shows the mixed and image pages, --images=all every page; not '--images=some'")]
    [InlineData("pdf /Papers/a.pdf 4 5", "pdf: too many arguments")]
    [InlineData("pdf info /Papers/a.pdf 4", "pdf: info takes a path and nothing else, e.g. pdf info /Papers/a.pdf")]
    [InlineData("pdf info /Papers/a.pdf --images", "pdf: info takes a path and nothing else, e.g. pdf info /Papers/a.pdf")]
    [InlineData("pdf", "pdf: missing operand")]
    [InlineData("pdf /Papers/a.pdf --frobnicate", "pdf: unrecognized option '--frobnicate'")]
    public async Task Wrong_use_is_refused_before_any_page_is_rendered(string line, string start)
    {
        await Upload("/Papers/a.pdf", Twelve());

        var result = await Run(line);

        Assert.True(result.IsError);
        Assert.StartsWith(start, result.Output);
        Assert.Empty(_pixels.PageRequests);
    }

    [Fact]
    public async Task A_crop_of_one_page_is_rendered_without_its_text()
    {
        var version = await Upload("/Papers/a.pdf", Twelve());

        var result = await Run("pdf /Papers/a.pdf 4 --crop 72,300,451,260 --size large");

        Assert.False(result.IsError, result.Output);
        Assert.Equal((4, SizeStep.Large, (Crop?)new Crop(72, 300, 451, 260)), Assert.Single(_pixels.PageRequests));
        Assert.Equal("TI", Kinds(result));
        Assert.Equal($"/Papers/a.pdf (version {version}), page 4 of 12 (mixed): page 595×842 pt; crop 72,300,451,260; large: 1109×1568 px PNG, 1 KB\n{Dropped}", Texts(result)[0]);
    }

    [Fact]
    public async Task Info_gives_the_count_the_metadata_the_types_and_the_regions()
    {
        await Upload("/Papers/smith-2025.pdf", Twelve());

        var result = await Run("pdf info /Papers/smith-2025.pdf");

        Assert.False(result.IsError, result.Output);
        Assert.Equal("""
            smith-2025.pdf: PDF, 12 pages, 2.1 MB
            Title: Attention to detail
            Author: A. Smith
            Pages: 1-3 text, 4 mixed, 5-12 text
            p4 mixed: vector drawing ≈ 35 % — regions (pt, x,y,w,h): drawing 72,300,451,260; image 80,90,200,120
            """.ReplaceLineEndings("\n"), result.Output);
        Assert.Empty(_pixels.PageRequests);
    }

    [Fact]
    public async Task Info_lists_eight_regions_a_page_and_a_scan_without_text()
    {
        PdfRegion[] many = [.. Enumerable.Range(0, 11).Select(i => new PdfRegion("drawing", 10 * i, 20.4, 30.6, 40))];
        await Upload("/Papers/b.pdf", new PdfAnalysis("a", 2, 2, null, null, [Mixed(1, 0.5, many), Scan(2)]));

        var lines = (await Run("pdf info /Papers/b.pdf")).Output.Split('\n');

        Assert.Equal("Pages: 1 mixed, 2 image", lines[1]);
        Assert.Equal("p1 mixed: vector drawing ≈ 50 % — regions (pt, x,y,w,h): " + string.Join("; ", Enumerable.Range(0, 8).Select(i => $"drawing {10 * i},20,31,40")) + "; …and 3 more", lines[2]);
        Assert.Equal("p2 image: one image covers the page, no text layer — regions (pt, x,y,w,h): image 0,0,595,842", lines[3]);
    }

    [Fact]
    public async Task Info_of_a_long_pdf_says_what_was_analysed_and_stays_within_the_limit()
    {
        PdfRegion[] eight = [.. Enumerable.Range(0, 8).Select(i => new PdfRegion("drawing", 100 + i, 200, 300, 400))];
        await Upload("/Papers/book.pdf", new PdfAnalysis("a", 2400, 2000, null, null, [.. Enumerable.Range(1, 2000).Select(n => Mixed(n, 0.4, eight))]));

        var output = (await Run("pdf info /Papers/book.pdf")).Output;
        var lines = output.Split('\n');

        Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length}");
        Assert.Equal("book.pdf: PDF, 2400 pages, 2.1 MB", lines[0]);
        Assert.Equal("Pages: 1-2000 mixed, 2001-2400 not analysed", lines[1]);
        Assert.Equal("Analysed the first 2000 of 2400 pages; later pages have no text or types.", lines[2]);
        var listed = lines.Count(l => l.StartsWith('p'));
        Assert.Equal($"…pages {listed + 1}-2000 not listed; pdf info shows the first {listed}", lines[^1]);
        Assert.DoesNotContain("truncated", output);
    }

    [Fact]
    public async Task A_page_past_the_analysed_ones_has_no_type_and_renders_only_with_images_all()
    {
        await Upload("/Papers/book.pdf", Plain(2400, 2000));

        var text = await Run("pdf /Papers/book.pdf 2000-2001");
        var all = await Run("pdf /Papers/book.pdf 2001 --images=all");

        Assert.EndsWith("\n\n--- Page 2001 of 2400: not analysed (only the first 2000 pages are) — add --images=all to see it ---", text.Output);
        Assert.StartsWith("--- Page 2001 of 2400: not analysed (only the first 2000 pages are) ---\n/Papers/book.pdf (version ", Texts(all)[0]);
        Assert.Contains(", page 2001 of 2400: page 595×842 pt; ", Texts(all)[0]);
        Assert.Equal([2001], _pixels.PageRequests.Select(r => r.Page));
    }

    [Fact]
    public async Task A_render_that_fails_keeps_the_text_and_is_not_tried_again()
    {
        await Upload("/Papers/b.pdf", Figures());
        _pixels.PageFails = _ => new PixelsUnavailableException(Unavailable);

        var result = await Run("pdf /Papers/b.pdf 1-5 --images");

        Assert.False(result.IsError, result.Output);
        Assert.Null(result.Parts);
        Assert.Contains($"--- Page 2 of 12: mixed (text; vector drawing ≈ 35 % of the page) — not rendered: {Unavailable} ---\nText of page 2", result.Output);
        Assert.Contains($"--- Page 4 of 12: mixed (text; vector drawing ≈ 35 % of the page) — not rendered: {Unavailable} ---\nText of page 4", result.Output);
        Assert.Contains("Text of page 5", result.Output);
        Assert.Equal([2], _pixels.PageRequests.Select(r => r.Page));
    }

    [Fact]
    public async Task Text_that_cannot_be_read_is_an_error_not_empty_pages()
    {
        await Upload("/Papers/a.pdf", Twelve());
        _pixels.TextsMissing = true;

        var result = await Run("pdf /Papers/a.pdf 3-5");

        Assert.Equal(ShellResult.Error("pdf: the text of /Papers/a.pdf can't be read right now; pdf /Papers/a.pdf 3 --images=all shows a page as an image."), result);
        Assert.Empty(_pixels.PageRequests);
    }

    [Fact]
    public async Task Metadata_cannot_forge_lines_of_info()
    {
        await Upload("/Papers/a.pdf", Twelve() with { Title = "Forged\n--- Page 1 of 1: text ---\r\u2028x", Author = "\t" });

        var lines = (await Run("pdf info /Papers/a.pdf")).Output.Split('\n');

        Assert.Equal("Title: Forged --- Page 1 of 1: text ---  x", lines[1]);
        Assert.StartsWith("Pages: ", lines[2]);
    }

    [Fact]
    public async Task A_page_missing_inside_the_analysed_span_says_so()
    {
        var analysis = Plain(5);
        await Upload("/Papers/gap.pdf", analysis with { Pages = [.. analysis.Pages.Where(p => p.Number != 3)] });

        var output = (await Run("pdf /Papers/gap.pdf 3")).Output;

        Assert.Equal("--- Page 3 of 5: not in the analysis — add --images=all to see it ---", output);
    }

    [Fact]
    public async Task Pending_text_and_types_are_not_known()
    {
        await Upload("/Papers/a.pdf", null);

        var text = await Run("pdf /Papers/a.pdf 2-3");
        var info = await Run("pdf info /Papers/a.pdf");
        var images = await Run("pdf /Papers/a.pdf 2-3 --images");

        const string NotYet = "pdf: /Papers/a.pdf hasn't been analysed yet: its text and page types aren't known. Try again in a while; pdf /Papers/a.pdf 1 --images=all shows a page as an image.";
        Assert.Equal(ShellResult.Error(NotYet), text);
        Assert.Equal(ShellResult.Error(NotYet), info);
        Assert.Equal(ShellResult.Error("pdf: --images chooses pages by their type, which isn't known yet; use --images=all for a range"), images);
        Assert.Equal([true, true, true], _pixels.AnalysisRequests);
        Assert.Empty(_pixels.PageRequests);
    }

    [Fact]
    public async Task Pending_images_all_and_crop_render_anyway()
    {
        var version = await Upload("/Papers/a.pdf", null);

        var range = await Run("pdf /Papers/a.pdf 2-3 --images=all");
        var crop = await Run("pdf /Papers/a.pdf 5 --crop 10,10,100,100");

        Assert.Equal("TITI", Kinds(range));
        Assert.Equal($"--- Page 2 of ?: not analysed yet ---\n/Papers/a.pdf (version {version}), page 2: page 595×842 pt; medium: 1109×1568 px PNG, 1 KB\n{Dropped}", Texts(range)[0]);
        Assert.Equal("TI", Kinds(crop));
        Assert.StartsWith($"/Papers/a.pdf (version {version}), page 5: page 595×842 pt; crop 10,10,100,100; medium: ", Texts(crop)[0]);
        Assert.Equal([2, 3, 5], _pixels.PageRequests.Select(r => r.Page));
    }

    [Fact]
    public async Task Pending_without_a_range_renders_four_pages_and_stops_at_the_end()
    {
        await Upload("/Papers/a.pdf", null);
        _pixels.PageFails = p => p > 3 ? new PixelsUnavailableException($"/Papers/a.pdf has no page {p}") : null;

        var open = await Run("pdf /Papers/a.pdf --images=all");
        _pixels.PageFails = p => p > 30 ? new PixelsUnavailableException($"/Papers/a.pdf has no page {p}") : null;
        var four = await Run("pdf /Papers/a.pdf 7- --images=all");
        var past = await Run("pdf /Papers/a.pdf 40 --images=all");

        Assert.Equal("TITITIT", Kinds(open));
        Assert.Equal("--- Page 4 of ?: not analysed yet (/Papers/a.pdf has no page 4) ---", Texts(open)[^1]);
        Assert.Equal("--- Stopped after page 10: at most 4 images per call. If the PDF has more pages, continue with: pdf /Papers/a.pdf 11- --images=all ---", Texts(four)[^1]);
        Assert.Equal(ShellResult.Error("pdf: /Papers/a.pdf has no page 40"), past);
    }

    [Fact]
    public async Task Pending_with_the_sidecar_down_says_rendering_is_unavailable()
    {
        await Upload("/Papers/a.pdf", null);
        _pixels.PageFails = _ => new PixelsUnavailableException(Unavailable);

        Assert.Equal(ShellResult.Error($"pdf: {Unavailable}"), await Run("pdf /Papers/a.pdf 1-3 --images=all"));
        Assert.Equal(ShellResult.Error($"pdf: {Unavailable}"), await Run("pdf /Papers/a.pdf 2 --crop 1,1,10,10"));
        Assert.StartsWith("pdf: /Papers/a.pdf hasn't been analysed yet", (await Run("pdf /Papers/a.pdf")).Output);
    }

    [Fact]
    public async Task A_stored_failure_is_the_answer_but_images_all_still_tries()
    {
        var version = await Upload("/Papers/a.pdf", null);
        _pixels.AnalysisFails = new PixelsUnavailableException(Locked);

        Assert.Equal(ShellResult.Error($"pdf: {Locked}"), await Run("pdf /Papers/a.pdf"));
        Assert.Equal(ShellResult.Error($"pdf: {Locked}"), await Run("pdf info /Papers/a.pdf"));
        Assert.Equal(ShellResult.Error($"pdf: {Locked}"), await Run("pdf /Papers/a.pdf 1 --images"));
        Assert.Empty(_pixels.PageRequests);

        var all = await Run("pdf /Papers/a.pdf 1 --images=all");
        Assert.Equal($"--- Page 1 of ?: not analysed ({Locked}) ---\n/Papers/a.pdf (version {version}), page 1: page 595×842 pt; medium: 1109×1568 px PNG, 1 KB\n{Dropped}", Texts(all)[0]);
        Assert.Equal([1], _pixels.PageRequests.Select(r => r.Page));
    }

    [Fact]
    public async Task A_note_an_image_and_a_missing_path_are_refused()
    {
        await _files.WriteAsync("/Papers/shot.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, (10, 10), null, Ct);

        var note = await Run("pdf /Papers/plan.md");
        var image = await Run("pdf /Papers/shot.png");
        var missing = await Run("pdf /Papers/nothing.pdf");

        Assert.Equal(ShellResult.Error("pdf: /Papers/plan.md is a note; cat reads notes") with { Kind = ShellErrorKind.Invalid }, note);
        Assert.Equal(ShellResult.Error("pdf: /Papers/shot.png is an image; look at it with img /Papers/shot.png (see 'help img')") with { Kind = ShellErrorKind.Invalid }, image);
        Assert.Equal(ShellErrorKind.NotFound, missing.Kind);
        Assert.StartsWith("pdf: /Papers/nothing.pdf: No such file or directory", missing.Output);
        Assert.Empty(_pixels.AnalysisRequests);
    }

    [Fact]
    public async Task It_cannot_be_part_of_a_pipeline()
    {
        await Upload("/Papers/a.pdf", Twelve());

        Assert.Equal("sh: pdf shows images and can't be part of a pipeline; run it on its own", (await Run("pdf /Papers/a.pdf | head")).Output);
        Assert.Empty(_pixels.AnalysisRequests);
    }

    [Fact]
    public async Task Cat_gives_the_pages_and_their_types_and_points_to_pdf()
    {
        await Upload("/Papers/paper.pdf", Twelve());
        await Upload("/Papers/new.pdf", null);
        await Upload("/Papers/one.pdf", Plain(1), 2048);

        Assert.Equal("paper.pdf: PDF, 12 pages (1-3 text, 4 mixed, 5-12 text), 2.1 MB — read it with pdf (help pdf)", (await Run("cat /Papers/paper.pdf")).Output);
        Assert.Equal("new.pdf: PDF, 2.1 MB — not analysed yet; pdf reads it (help pdf)", (await Run("cat /Papers/new.pdf")).Output);
        Assert.Equal("one.pdf: PDF, 1 page (1 text), 2 KB — read it with pdf (help pdf)", (await Run("cat /Papers/one.pdf")).Output);
        Assert.Equal([false, false, false], _pixels.AnalysisRequests);
    }

    [Fact]
    public async Task Cat_shortens_many_runs_and_names_a_stored_failure()
    {
        await Upload("/Papers/striped.pdf", new PdfAnalysis("a", 30, 30, null, null, [.. Enumerable.Range(1, 30).Select(n => n % 2 == 0 ? Mixed(n) : Text(n))]));

        Assert.Equal(
            "striped.pdf: PDF, 30 pages (1 text, 2 mixed, 3 text, 4 mixed, 5 text, 6 mixed, 7 text, 8 mixed, …and 22 more runs), 2.1 MB — read it with pdf (help pdf)",
            (await Run("cat /Papers/striped.pdf")).Output);

        _pixels.AnalysisFails = new PixelsUnavailableException(Locked);
        Assert.Equal($"striped.pdf: PDF, 2.1 MB — {Locked}", (await Run("cat /Papers/striped.pdf")).Output);
    }

    [Fact]
    public async Task Help_pdf_explains_it_all_with_the_numbers()
    {
        var help = (await Run("help pdf")).Output;

        Assert.StartsWith("pdf <path> [pages]", help);
        Assert.Contains("pdf info <path>", help);
        Assert.Contains(SizeSteps.Table, help);
        Assert.Contains("At most 4 images", help);
        Assert.Contains("20 pages", help);
        Assert.Contains("points", help);
        Assert.Contains("85 %", help);
        Assert.Contains("25 %", help);
        Assert.Contains("50 megapixels", help);
        Assert.Contains("rg finds text in PDFs", help);
        Assert.Contains("semsearch finds their pages by\n  meaning (1. /Work/paper.pdf p7)", help);
        Assert.Contains("Neither finds the text of pages without a text layer (scans)", help);
        Assert.Contains("A PDF of more than 2000 pages is analysed up to\n  page 2000, and says so.", help);
        Assert.Contains("cover 5 % of it or more", help);
        Assert.Contains("— add --images=all or --crop to see it ---", help);
        Assert.Contains("1 small image ≈ 3 %", help);
        Assert.Contains("figures of each page that has more than small ones", help);
        Assert.All(help.Split('\n'), line => Assert.True(line.Length <= 106, line));
        Assert.Contains("A client may drop images", help);
        Assert.Contains("'help img'", help);
        Assert.Contains("pdf /Work/Papers/paper.pdf 4 --crop", help);
        Assert.Contains("'help pdf'", (await Run("help img")).Output);
        var attachments = (await Run("help attachments")).Output;
        Assert.Contains("pdf        reads a PDF", attachments);
        Assert.Contains("12 pages (1-3 text, 4 mixed, 5-12 text)", attachments);
    }

    [Fact]
    public async Task Without_pixels_the_shell_has_no_pdf_and_cat_keeps_its_plain_line()
    {
        var store = new InMemoryNoteStore(("/Papers/plan.md", "a note\n"));
        var plain = TestShell.Of(store);
        var caller = new FixedCaller(Writers.Dev);
        await new AttachmentWriter(store, store, _time, caller).WriteAsync("/Papers/a.pdf", Pdf(2048), null, null, Ct);

        Assert.StartsWith("command not found: pdf", (await plain.RunAsync("pdf /Papers/a.pdf", Ct)).Output);
        Assert.Equal("a.pdf: PDF, 2 KB", (await plain.RunAsync("cat /Papers/a.pdf", Ct)).Output);
    }
}
