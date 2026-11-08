// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>rg</c> over the stored text of PDFs: after the notes, page by page, from stored analyses only.</summary>
public class RgPdfTests
{
    private sealed class FakePixels : IAttachmentPixels
    {
        public Dictionary<string, PdfAnalysis> Analyses { get; } = [];

        public Dictionary<string, Func<int, string>> Texts { get; } = [];

        public List<(string Path, int First, int Last)> TextRequests { get; } = [];

        public List<bool> AnalysisRequests { get; } = [];

        public HashSet<string> Failing { get; } = [];

        public HashSet<string> TextFails { get; } = [];

        /// <summary>The exception the text of a PDF throws from a page on, as a failure part way through.</summary>
        public Dictionary<string, (int From, Exception Error)> TextFailsFrom { get; } = [];

        public Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, int rotate, int? frame, CancellationToken ct) => throw new NotSupportedException();

        public Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, int rotate, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct)
        {
            AnalysisRequests.Add(runIfPending);
            if (Failing.Contains(path.Value)) throw new PixelsUnavailableException("this PDF is password-protected; Kumbu² can't read it");
            return Task.FromResult(Analyses.GetValueOrDefault(path.Value));
        }

        public Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct)
        {
            TextRequests.Add((path.Value, first, last));
            if (TextFails.Contains(path.Value)) throw new InvalidOperationException("the text part is gone");
            if (TextFailsFrom.TryGetValue(path.Value, out var fails) && last >= fails.From) throw fails.Error;
            if (!Analyses.ContainsKey(path.Value)) return Task.FromResult<IReadOnlyList<string>?>(null);
            var text = Texts[path.Value];
            return Task.FromResult<IReadOnlyList<string>?>([.. Enumerable.Range(first, last - first + 1).Select(text)]);
        }

        /// <summary>The text boxes of a PDF's pages asked; null: none can be had.</summary>
        public Func<string, IReadOnlyList<int>, IReadOnlyList<PageTextBoxes>>? Boxes { get; set; }

        public List<(string Path, IReadOnlyList<int> Pages)> BoxRequests { get; } = [];

        /// <summary>What every text-boxes request throws, as with the sidecar away.</summary>
        public Exception? BoxesFail { get; set; }

        public Task<IReadOnlyList<PageTextBoxes>> TextBoxesAsync(VaultPath path, AttachmentState state, IReadOnlyList<int> pages, CancellationToken ct)
        {
            BoxRequests.Add((path.Value, [.. pages]));
            // As the real one: a page list that isn't ascending, distinct and at most 50 long is refused.
            if (pages.Count is 0 or > 50 || pages.Zip(pages.Skip(1)).Any(p => p.First >= p.Second)) throw new ArgumentException("bad pages", nameof(pages));
            if (BoxesFail is not null) throw BoxesFail;
            if (Boxes is null) throw new NotSupportedException();
            return Task.FromResult(Boxes(path.Value, pages));
        }

        public Task<PdfPageImages> PdfImagesAsync(VaultPath path, AttachmentState state, int page, CancellationToken ct) => throw new NotSupportedException();

        public Task<Picture> PdfImageAsync(VaultPath path, AttachmentState state, int page, int n, CancellationToken ct) => throw new NotSupportedException();

        public Task<Picture> PdfRegionAsync(VaultPath path, AttachmentState state, int page, Crop crop, int rotate, bool jpeg, CancellationToken ct) => throw new NotSupportedException();

        public void Count(AttachmentUse use)
        {
        }
    }

    private readonly InMemoryNoteStore _store = new(
        ("/V/plan.md", "the plan\nneedle in a note\n"),
        ("/V/sub/more.md", "nothing here\n"),
        ("/W/other.md", "needle elsewhere\n"),
        ("/X/tagged.md", "#research\nneedle too\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 29, 11, 0, 0, DateTimeKind.Utc));
    private readonly FakePixels _pixels = new();
    private readonly AttachmentWriter _files;
    private readonly Shell _sh;

    public RgPdfTests()
    {
        var caller = new FixedCaller(Writers.Dev);
        _files = new AttachmentWriter(_store, _store, _time, caller);
        _sh = new Shell(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller), pixels: _pixels);
    }

    private static CancellationToken Ct => CancellationToken.None;

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, Ct);

    private static PdfAnalysis Analysis(int pages) =>
        new("a", pages, pages, null, null, [.. Enumerable.Range(1, pages).Select(n => new PdfPage(n, 595, 842, 0, PageType.Text, 100, 0.3, 0, 0, 0, []))]);

    /// <summary>Uploads a PDF whose page text is given by <paramref name="text"/>; without it the PDF has no stored analysis.</summary>
    private async Task Upload(string path, int pages, Func<int, string>? text)
    {
        var bytes = "%PDF-1.7"u8.ToArray();
        var result = await _files.WriteAsync(path, bytes, null, null, Ct);
        Assert.False(result.Result.IsError, result.Result.Output);
        if (text is null) return;
        _pixels.Analyses[path] = Analysis(pages);
        _pixels.Texts[path] = text;
    }

    private Task Paper(string path = "/V/paper.pdf") => Upload(path, 8, n => n switch
    {
        2 => "intro\nthe needle sits here\nand ends",
        7 => "methods\nmore\nnothing\nlast needle line\nneedle again",
        _ => $"page {n} text",
    });

    [Fact]
    public async Task A_match_shows_the_page_after_the_path()
    {
        await Paper();

        var result = await Run("rg needle /V/paper.pdf");

        Assert.Equal("/V/paper.pdf:p2: the needle sits here\n/V/paper.pdf:p7: last needle line\n/V/paper.pdf:p7: needle again", result.Output);
    }

    [Fact]
    public async Task Line_numbers_count_within_the_page()
    {
        await Paper();

        var result = await Run("rg -n needle /V/paper.pdf");

        Assert.Equal("/V/paper.pdf:p2:2: the needle sits here\n/V/paper.pdf:p7:4: last needle line\n/V/paper.pdf:p7:5: needle again", result.Output);
    }

    [Fact]
    public async Task Notes_come_first_then_the_pdfs_by_path()
    {
        await Paper("/V/b.pdf");
        await Paper("/V/a.pdf");

        var result = await Run("rg needle /V");

        Assert.Equal(
            "/V/plan.md:2:needle in a note\n/V/a.pdf:p2: the needle sits here\n/V/a.pdf:p7: last needle line\n/V/a.pdf:p7: needle again\n"
            + "/V/b.pdf:p2: the needle sits here\n/V/b.pdf:p7: last needle line\n/V/b.pdf:p7: needle again",
            result.Output);
    }

    [Fact]
    public async Task Context_stays_within_a_page_and_pages_are_separated()
    {
        await Paper();

        var result = await Run("rg -n -C 1 needle /V/paper.pdf");

        Assert.Equal(
            "/V/paper.pdf:p2-1- intro\n/V/paper.pdf:p2:2: the needle sits here\n/V/paper.pdf:p2-3- and ends\n--\n"
            + "/V/paper.pdf:p7-3- nothing\n/V/paper.pdf:p7:4: last needle line\n/V/paper.pdf:p7:5: needle again",
            result.Output);
    }

    [Fact]
    public async Task Context_without_line_numbers_uses_the_dash_after_the_page()
    {
        await Paper();

        var result = await Run("rg -A 1 'the needle' /V/paper.pdf");

        Assert.Equal("/V/paper.pdf:p2: the needle sits here\n/V/paper.pdf:p2- and ends", result.Output);
    }

    [Fact]
    public async Task Files_with_matches_lists_a_pdf_once()
    {
        await Paper();

        Assert.Equal("/V/plan.md\n/V/paper.pdf", (await Run("rg -l needle /V")).Output);
    }

    [Fact]
    public async Task Count_is_per_pdf()
    {
        await Paper();

        Assert.Equal("/V/plan.md:1\n/V/paper.pdf:3", (await Run("rg -c needle /V")).Output);
    }

    [Fact]
    public async Task Max_count_limits_the_lines_per_pdf_across_pages()
    {
        await Paper();
        await Paper("/V/second.pdf");

        var result = await Run("rg -m 2 needle /V/paper.pdf /V/second.pdf");

        Assert.Equal(
            "/V/paper.pdf:p2: the needle sits here\n/V/paper.pdf:p7: last needle line\n"
            + "/V/second.pdf:p2: the needle sits here\n/V/second.pdf:p7: last needle line",
            result.Output);
        Assert.Equal("/V/paper.pdf:2\n/V/second.pdf:2", (await Run("rg -c -m 2 needle /V/paper.pdf /V/second.pdf")).Output);
    }

    [Fact]
    public async Task Case_words_literals_and_inversion_work_as_for_notes()
    {
        await Upload("/V/odd.pdf", 1, _ => "Needle\nneedles\nx.y\nxzy\nplain");

        Assert.Equal("/V/odd.pdf:p1: Needle\n/V/odd.pdf:p1: needles", (await Run("rg -i needle /V/odd.pdf")).Output);
        Assert.Equal("/V/odd.pdf:p1: needles", (await Run("rg needles /V/odd.pdf")).Output);
        Assert.Equal("/V/odd.pdf:p1: Needle", (await Run("rg -w Needle /V/odd.pdf")).Output);
        Assert.Equal("/V/odd.pdf:p1: x.y", (await Run("rg -F x.y /V/odd.pdf")).Output);
        Assert.Equal("/V/odd.pdf:p1: plain", (await Run("rg -v -e eedle -e x /V/odd.pdf")).Output);
    }

    [Fact]
    public async Task A_pdf_without_stored_analysis_is_skipped_and_never_analysed_on_demand()
    {
        await Upload("/V/pending.pdf", 3, null);
        await Paper();

        var folder = await Run("rg needle /V");
        var single = await Run("rg needle /V/pending.pdf");

        Assert.DoesNotContain("pending.pdf", folder.Output);
        Assert.Contains("/V/paper.pdf:p2:", folder.Output);
        Assert.False(single.IsError);
        Assert.Equal("", single.Output);
        Assert.All(_pixels.AnalysisRequests, run => Assert.False(run));
    }

    [Fact]
    public async Task A_pdf_whose_analysis_failed_is_skipped_silently()
    {
        await Paper();
        await Paper("/V/locked.pdf");
        _pixels.Failing.Add("/V/locked.pdf");

        var result = await Run("rg -l needle /V");

        Assert.Equal("/V/plan.md\n/V/paper.pdf", result.Output);
    }

    private async Task Scanned(string path, params PageType[] types)
    {
        await Upload(path, types.Length, _ => "ocr text");
        _pixels.Analyses[path] = new PdfAnalysis("a", types.Length, types.Length, null, null, [.. types.Select((t, i) =>
            new PdfPage(i + 1, 595, 842, 0, t, t == PageType.Mixed ? 0 : 100, 0.3, 0.9, 0, 1, []))]);
    }

    private const string PictureHintStart = "no match in the text, but pictures may hold it, on pages that are scans or photos, whose text is missing or may be wrong, or in images on pages with text: ";
    private const string PictureHintEnd = " (pdf info shows which; pdf <path> --images shows them)";

    [Fact]
    public async Task Nothing_found_in_a_pdf_with_scans_is_not_taken_for_proof()
    {
        await Scanned("/V/scan.pdf", PageType.Image, PageType.Mixed, PageType.Text);

        Assert.Equal($"{PictureHintStart}/V/scan.pdf (2 pages of scans or photos){PictureHintEnd}", (await Run("rg zzz /V/scan.pdf")).Output);
    }

    /// <summary>A PDF whose pages have text beside images, as in a report with charts.</summary>
    private async Task Report(string path, int pages)
    {
        await Upload(path, pages, _ => "text");
        _pixels.Analyses[path] = new PdfAnalysis("a", pages, pages, null, null, [.. Enumerable.Range(1, pages).Select(i =>
            new PdfPage(i, 595, 842, 0, PageType.Mixed, 400, 0.3, 0.4, 0, 2, []))]);
    }

    [Fact]
    public async Task Images_on_pages_with_text_are_named_too()
    {
        await Report("/V/report.pdf", 3);

        Assert.Equal($"{PictureHintStart}/V/report.pdf (3 pages with images){PictureHintEnd}", (await Run("rg zzz /V/report.pdf")).Output);
    }

    [Fact]
    public async Task Scans_and_images_are_counted_apart()
    {
        await Upload("/V/both.pdf", 3, _ => "text");
        _pixels.Analyses["/V/both.pdf"] = new PdfAnalysis("a", 3, 3, null, null, [
            new PdfPage(1, 595, 842, 0, PageType.Image, 0, 0, 0.9, 0, 1, []),
            new PdfPage(2, 595, 842, 0, PageType.Mixed, 400, 0.3, 0.4, 0, 1, []),
            new PdfPage(3, 595, 842, 0, PageType.Mixed, 400, 0.3, 0.4, 0, 1, []),
        ]);

        Assert.Equal($"{PictureHintStart}/V/both.pdf (1 page of scans or photos, 2 with images){PictureHintEnd}", (await Run("rg zzz /V/both.pdf")).Output);
    }

    [Fact]
    public async Task A_mixed_page_without_images_gets_no_hint()
    {
        await Upload("/V/drawn.pdf", 1, _ => "text");
        _pixels.Analyses["/V/drawn.pdf"] = new PdfAnalysis("a", 1, 1, null, null, [new PdfPage(1, 595, 842, 0, PageType.Mixed, 400, 0.3, 0, 0.4, 0, [])]);

        Assert.Equal("", (await Run("rg zzz /V/drawn.pdf")).Output);
    }

    [Theory]
    [InlineData("rg --files /Leer")]
    [InlineData("rg zzz /Leer")]
    public async Task A_vault_without_notes_is_empty_not_missing(string line)
    {
        _store.AddVault("Leer");

        var result = await Run(line);

        Assert.False(result.IsError, result.Output);
        Assert.Equal("", result.Output);
    }

    [Theory]
    [InlineData("rg --files /V/pdfs")]
    public async Task A_folder_of_attachments_only_lists_no_notes(string line)
    {
        await Paper("/V/pdfs/paper.pdf");

        var result = await Run(line);

        Assert.False(result.IsError, result.Output);
        Assert.Equal("", result.Output);
    }

    [Theory]
    [InlineData("rg --files /Nirgends")]
    [InlineData("rg --files /V/nope")]
    [InlineData("rg zzz /Nirgends")]
    public async Task What_isnt_there_stays_an_error(string line)
    {
        var result = await Run(line);

        Assert.True(result.IsError);
        Assert.Contains("No such file or directory", result.Output);
    }

    [Fact]
    public async Task A_match_or_a_pipe_or_a_pdf_of_text_pages_gets_no_hint_about_scans()
    {
        await Scanned("/V/scan.pdf", PageType.Image);
        await Paper();

        Assert.Equal("/V/scan.pdf:p1: ocr text", (await Run("rg ocr /V/scan.pdf")).Output);
        Assert.Equal("", (await Run("rg zzz /V/paper.pdf")).Output);
        Assert.Equal("", (await Run("cat /V/plan.md | rg zzz")).Output);
    }

    [Fact]
    public async Task Pages_without_text_have_nothing_to_find_not_even_by_inversion()
    {
        await Upload("/V/scan.pdf", 2, n => n == 1 ? "" : "a line");

        Assert.Equal("/V/scan.pdf:p2: a line", (await Run("rg -v zzz /V/scan.pdf")).Output);
    }

    [Fact]
    public async Task Tag_and_files_leave_pdfs_out()
    {
        await Paper("/X/paper.pdf");

        var tagged = await Run("rg --tag research needle /X");
        var files = await Run("rg --files /X");

        Assert.Equal("/X/tagged.md:2:needle too", tagged.Output);
        Assert.DoesNotContain("pdf", files.Output);
    }

    [Fact]
    public async Task A_folder_scopes_the_pdfs_searched()
    {
        await Paper("/V/sub/deep.pdf");
        await Paper("/V/top.pdf");
        await Paper("/W/other.pdf");

        Assert.Equal("/V/sub/deep.pdf", (await Run("rg -l needle /V/sub")).Output);
        Assert.Equal("/V/plan.md\n/V/sub/deep.pdf\n/V/top.pdf", (await Run("rg -l needle /V")).Output);
        Assert.Equal("/V/sub/deep.pdf\n/V/top.pdf", (await Run("rg -l needle /V/top.pdf /V/sub")).Output);
        Assert.Equal("/V/plan.md\n/W/other.md\n/V/sub/deep.pdf\n/V/top.pdf\n/W/other.pdf", (await Run("rg -l needle /V /W")).Output);
    }

    [Fact]
    public async Task A_folder_with_only_pdfs_is_searched_and_a_missing_one_is_still_an_error()
    {
        await Paper("/V/papers/only.pdf");

        Assert.Equal("/V/papers/only.pdf", (await Run("rg -l needle /V/papers")).Output);
        Assert.Equal("rg: /V/nothing: No such file or directory", (await Run("rg needle /V/nothing")).Output);
    }

    [Fact]
    public async Task Max_count_used_up_stops_reading_further_chunks()
    {
        await Upload("/V/big.pdf", 250, _ => "x");

        var result = await Run("rg -m 1 x /V/big.pdf");

        Assert.Equal("/V/big.pdf:p1: x", result.Output);
        Assert.Equal([("/V/big.pdf", 1, 100)], _pixels.TextRequests);
    }

    [Fact]
    public async Task The_output_limit_is_checked_per_page()
    {
        var line = new string('x', 10_000);
        await Upload("/V/wide.pdf", 100, _ => line);

        // Called directly, so the shell's own cut does not hide how many pages rg added.
        var result = await new Kumbu.Core.Sh.Commands.Rg(_store, _pixels).RunAsync(["x", "/V/wide.pdf"], null, Ct);

        Assert.Equal(3, result.Output.Split('\n').Length);
    }

    [Fact]
    public async Task A_pdf_whose_text_cannot_be_read_is_skipped()
    {
        await Paper();
        await Paper("/V/broken.pdf");
        _pixels.TextFails.Add("/V/broken.pdf");

        var result = await Run("rg -l needle /V");

        Assert.Equal("/V/plan.md\n/V/paper.pdf", result.Output);
    }

    [Fact]
    public async Task A_pdf_whose_text_fails_part_way_keeps_its_count_so_far()
    {
        await Upload("/V/long.pdf", 250, n => n is 50 or 60 ? "needle" : "filler");
        _pixels.TextFailsFrom["/V/long.pdf"] = (101, new InvalidOperationException("the text part is gone"));
        await Paper("/V/z.pdf");

        Assert.Equal("/V/long.pdf:2\n/V/z.pdf:3", (await Run("rg -c needle /V/long.pdf /V/z.pdf")).Output);
    }

    [Fact]
    public async Task A_search_that_times_out_on_pdf_text_says_it_took_too_long()
    {
        await Paper();
        _pixels.TextFailsFrom["/V/paper.pdf"] = (1, new System.Text.RegularExpressions.RegexMatchTimeoutException("needle", "needle", TimeSpan.FromSeconds(2)));

        var result = await Run("rg needle /V");

        Assert.Equal(ShellResult.Error("rg: the search took too long; narrow the path or simplify the pattern"), result);
    }

    [Fact]
    public async Task A_checkpoint_does_not_search_pdfs()
    {
        await Paper();
        Assert.False((await Run("mkcp /V cp")).IsError);
        _pixels.TextRequests.Clear();

        var result = await Run("rg --checkpoint cp -l needle /V");

        Assert.Equal("/V/plan.md", result.Output);
        Assert.Empty(_pixels.TextRequests);
    }

    [Fact]
    public async Task Pages_are_read_in_hundreds()
    {
        await Upload("/V/long.pdf", 250, n => n == 250 ? "needle at the end" : "filler");

        var result = await Run("rg needle /V/long.pdf");

        Assert.Equal("/V/long.pdf:p250: needle at the end", result.Output);
        Assert.Equal([("/V/long.pdf", 1, 100), ("/V/long.pdf", 101, 200), ("/V/long.pdf", 201, 250)], _pixels.TextRequests);
    }

    [Fact]
    public async Task Reading_stops_once_the_output_limit_is_passed()
    {
        var line = new string('x', 400);
        await Upload("/V/huge.pdf", 250, _ => line);
        await Upload("/V/zz.pdf", 3, _ => line);

        var result = await Run("rg x /V");

        Assert.Contains("[truncated", result.Output);
        Assert.Equal([("/V/huge.pdf", 1, 100)], _pixels.TextRequests);
    }

    [Fact]
    public async Task Images_are_still_not_searched()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var written = await _files.WriteAsync("/V/shot.png", png, (1, 1), null, Ct);
        Assert.False(written.Result.IsError, written.Result.Output);

        Assert.Equal("rg: /V/shot.png is an attachment; rg searches the text of notes only", (await Run("rg x /V/shot.png")).Output);
    }

    [Fact]
    public async Task Without_a_pixels_service_a_pdf_is_an_attachment_rg_does_not_search()
    {
        var caller = new FixedCaller(Writers.Dev);
        var bare = new Shell(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller));
        await Upload("/V/plain.pdf", 1, _ => "needle");

        var result = await bare.RunAsync("rg needle /V/plain.pdf", Ct);

        Assert.Equal("rg: /V/plain.pdf is an attachment; rg searches the text of notes only", result.Output);
        Assert.Equal("/V/plan.md:2:needle in a note", (await bare.RunAsync("rg needle /V", Ct)).Output);
    }

    [Fact]
    public async Task Help_says_what_rg_reads()
    {
        var help = (await Run("help rg")).Output;

        Assert.Contains("p7", help);
        Assert.Contains("scans", help);
        Assert.Contains("not with --checkpoint", help);
    }

    private const string Revenue = "Net revenue (after adjustments)";

    private const string Unavailable = "No regions: PDF rendering is unavailable right now. The matches are all here.";

    private static PageTextBoxes Page(int n, string text, params TextBox[] items) => new(n, 595.28, 841.89, text, items);

    /// <summary>The sidecar answers each page with <paramref name="text"/> (the stored text unless given) and <paramref name="items"/>.</summary>
    private void BoxesOf(Func<int, TextBox[]> items, Func<string, int, string>? text = null) =>
        _pixels.Boxes = (path, pages) => [.. pages.Select(n => Page(n, text is null ? _pixels.Texts[path](n) : text(path, n), items(n)))];

    /// <summary>A PDF of <paramref name="pages"/> pages, page 7 reading <see cref="Revenue"/>, its one item where the spec puts it.</summary>
    private async Task Revenues(string path = "/V/p.pdf", int pages = 7)
    {
        await Upload(path, pages, n => n == 7 ? Revenue : "");
        BoxesOf(_ => [new TextBox(0, 31, 74.2, 300.1, 210.3, 11.0)]);
    }

    [Fact]
    public async Task A_match_gets_its_region_before_the_text()
    {
        await Revenues();

        Assert.Equal("/V/p.pdf:p7:crop=70,296,218,19: Net revenue (after adjustments)", (await Run("rg --regions revenue /V/p.pdf")).Output);
    }

    [Fact]
    public async Task With_n_the_region_follows_the_line_number()
    {
        var lines = Enumerable.Range(1, 11).Select(i => $"filler {i}").Append(Revenue).ToList();
        await Upload("/V/p.pdf", 7, n => n == 7 ? string.Join('\n', lines) : "");
        var at = lines.Take(11).Sum(l => l.Length + 1);
        BoxesOf(_ => [new TextBox(0, 8, 72, 100, 40, 10), new TextBox(at, at + 31, 74.2, 300.1, 210.3, 11.0)]);

        Assert.Equal("/V/p.pdf:p7:12:crop=70,296,218,19: Net revenue (after adjustments)", (await Run("rg --regions -n revenue /V/p.pdf")).Output);
    }

    [Fact]
    public async Task Two_matches_in_a_line_give_two_regions()
    {
        await Upload("/V/p.pdf", 12, n => n == 12 ? "Net revenue  Net revenue" : "");
        BoxesOf(_ => [new TextBox(0, 11, 310, 507, 90, 10), new TextBox(13, 24, 470, 507, 90, 10)]);

        Assert.Equal("/V/p.pdf:p12:crop=307,504,96,16;467,504,96,16: Net revenue  Net revenue", (await Run("rg --regions 'Net revenue' /V/p.pdf")).Output);
    }

    [Fact]
    public async Task Equal_boxes_in_a_line_count_once()
    {
        await Upload("/V/p.pdf", 1, _ => "revenue and revenue");
        BoxesOf(_ => [new TextBox(0, 19, 310, 507, 90, 10)]);

        Assert.Equal("/V/p.pdf:p1:crop=307,504,96,16: revenue and revenue", (await Run("rg --regions revenue /V/p.pdf")).Output);
    }

    [Fact]
    public async Task At_most_three_regions_in_a_line()
    {
        await Upload("/V/p.pdf", 1, _ => "a1 a2 a3 a4 a5");
        BoxesOf(_ => [.. Enumerable.Range(0, 5).Select(i => new TextBox(i * 3, i * 3 + 2, 100 + i * 100, 100, 50, 10))]);

        Assert.Equal("/V/p.pdf:p1:crop=97,97,56,16;197,97,56,16;297,97,56,16;+2 more: a1 a2 a3 a4 a5", (await Run(@"rg --regions 'a\d' /V/p.pdf")).Output);
    }

    [Fact]
    public async Task A_match_over_two_items_is_their_union()
    {
        await Upload("/V/p.pdf", 1, _ => "Net revenue");
        BoxesOf(_ => [new TextBox(0, 4, 100, 200, 30, 10), new TextBox(4, 11, 130, 201, 60, 10)]);

        // 100..190 by 200..211, grown by 3.3.
        Assert.Equal("/V/p.pdf:p1:crop=96,196,98,19: Net revenue", (await Run("rg --regions 'Net revenue' /V/p.pdf")).Output);
        // A match within the first item has that item's box alone.
        Assert.Equal("/V/p.pdf:p1:crop=97,197,36,16: Net revenue", (await Run("rg --regions Net /V/p.pdf")).Output);
    }

    [Fact]
    public async Task Small_text_grows_by_two_points()
    {
        await Upload("/V/p.pdf", 1, _ => "tiny print");
        BoxesOf(_ => [new TextBox(0, 10, 100, 200, 50, 5)]);

        Assert.Equal("/V/p.pdf:p1:crop=98,198,54,9: tiny print", (await Run("rg --regions tiny /V/p.pdf")).Output);
    }

    [Fact]
    public async Task A_region_stays_on_the_page()
    {
        await Upload("/V/p.pdf", 1, _ => "corner");
        BoxesOf(_ => [new TextBox(0, 6, 590, 836, 10, 10)]);

        // The page is 595.28 × 841.89: cut at 595 and 841.
        Assert.Equal("/V/p.pdf:p1:crop=587,833,8,8: corner", (await Run("rg --regions corner /V/p.pdf")).Output);
    }

    [Fact]
    public async Task A_match_in_the_last_line_of_a_page_is_found_by_its_offset()
    {
        await Upload("/V/p.pdf", 1, _ => "alpha\nbeta\ngamma needle");
        BoxesOf(_ => [new TextBox(0, 5, 72, 100, 40, 10), new TextBox(6, 10, 72, 120, 40, 10), new TextBox(11, 17, 72, 140, 40, 10), new TextBox(17, 23, 112, 140, 40, 10)]);

        Assert.Equal("/V/p.pdf:p1:crop=109,137,46,16: gamma needle", (await Run("rg --regions needle /V/p.pdf")).Output);
    }

    [Fact]
    public async Task Context_lines_print_as_today()
    {
        await Upload("/V/p.pdf", 7, n => n == 7 ? $"before\n{Revenue}\nafter" : "");
        BoxesOf(_ => [new TextBox(7, 38, 74.2, 300.1, 210.3, 11.0)]);

        Assert.Equal(
            "/V/p.pdf:p7- before\n/V/p.pdf:p7:crop=70,296,218,19: Net revenue (after adjustments)\n/V/p.pdf:p7- after",
            (await Run("rg --regions -C 1 revenue /V/p.pdf")).Output);
    }

    [Fact]
    public async Task The_group_separator_stays()
    {
        await Upload("/V/p.pdf", 2, n => n == 1 ? "one\ntwo needle\nthree\nfour\nfive\nsix needle" : "needle on two");
        BoxesOf(n => n == 1 ? [new TextBox(4, 14, 72, 100, 60, 10), new TextBox(31, 41, 72, 140, 60, 10)] : [new TextBox(0, 13, 72, 100, 60, 10)]);

        // Between groups within a page and between pages.
        Assert.Equal(
            "/V/p.pdf:p1-1- one\n/V/p.pdf:p1:2:crop=69,97,66,16: two needle\n/V/p.pdf:p1-3- three\n--\n/V/p.pdf:p1-5- five\n/V/p.pdf:p1:6:crop=69,137,66,16: six needle\n--\n"
            + "/V/p.pdf:p2:1:crop=69,97,66,16: needle on two",
            (await Run("rg --regions -n -C 1 needle /V/p.pdf")).Output);
    }

    [Fact]
    public async Task A_note_line_prints_as_today()
    {
        await Revenues();

        Assert.Equal("/V/plan.md:2:needle in a note", (await Run("rg --regions needle /V")).Output);
        Assert.Equal("/V/plan.md:2:needle in a note\n/V/p.pdf:p7:crop=70,296,218,19: Net revenue (after adjustments)", (await Run("rg --regions -e needle -e revenue /V")).Output);
    }

    [Fact]
    public async Task A_zero_width_match_prints_as_today()
    {
        await Upload("/V/p.pdf", 1, _ => "abc");
        BoxesOf(_ => [new TextBox(0, 3, 72, 100, 30, 10)]);

        Assert.Equal("/V/p.pdf:p1: abc", (await Run("rg --regions 'x*' /V/p.pdf")).Output);
    }

    [Fact]
    public async Task A_match_only_in_white_space_prints_as_today()
    {
        await Upload("/V/p.pdf", 1, _ => "a   b");
        BoxesOf(_ => [new TextBox(0, 1, 72, 100, 10, 10), new TextBox(4, 5, 112, 100, 10, 10)]);

        Assert.Equal("/V/p.pdf:p1: a   b", (await Run("rg --regions '  ' /V/p.pdf")).Output);
    }

    [Fact]
    public async Task A_forged_crop_in_the_text_stays_text()
    {
        await Upload("/V/p.pdf", 1, _ => "crop=1,1,1,1: forged");
        BoxesOf(_ => []);

        Assert.Equal("/V/p.pdf:p1: crop=1,1,1,1: forged", (await Run("rg --regions forged /V/p.pdf")).Output);

        BoxesOf(_ => [new TextBox(0, 20, 10, 10, 100, 10)]);
        Assert.Equal("/V/p.pdf:p1:crop=7,7,106,16: crop=1,1,1,1: forged", (await Run("rg --regions forged /V/p.pdf")).Output);
    }

    [Theory]
    [InlineData("rg --regions -l needle /V")]
    [InlineData("rg --regions -c needle /V")]
    [InlineData("rg --regions -v needle /V")]
    [InlineData("rg --regions --files /V")]
    [InlineData("rg --regions --tag t needle /V")]
    [InlineData("rg --regions --version 1 needle /V/plan.md")]
    [InlineData("cat /V/plan.md | rg --regions needle")]
    public async Task The_refusals_name_what_they_refuse(string line)
    {
        await Revenues();

        Assert.Equal(
            ShellResult.Error("rg: --regions says where matches lie in PDFs; it doesn't go with -l, -c, -v, --files, --tag or --version, nor right of a pipe"),
            await Run(line));
    }

    [Fact]
    public async Task A_pipe_to_the_right_is_fine()
    {
        await Revenues();

        Assert.Contains("crop=", (await Run("rg --regions revenue /V | head -1")).Output);
    }

    [Fact]
    public async Task Boxes_are_asked_once_a_pdf_for_the_pages_with_matches()
    {
        await Upload("/V/p.pdf", 14, n => n is 7 or 12 ? "needle\nand needle" : "hay");
        BoxesOf(_ => [new TextBox(0, 6, 72, 100, 40, 10), new TextBox(7, 17, 72, 120, 60, 10)]);

        var result = await Run("rg --regions needle /V/p.pdf");

        Assert.Equal(4, result.Output.Split('\n').Count(l => l.Contains(":crop=")));
        var request = Assert.Single(_pixels.BoxRequests);
        Assert.Equal("/V/p.pdf", request.Path);
        Assert.Equal([7, 12], request.Pages);
    }

    [Fact]
    public async Task More_than_eight_pdfs_get_no_regions_after_the_eighth()
    {
        for (var i = 1; i <= 9; i++) await Upload($"/V/d/p{i}.pdf", 1, _ => "needle");
        BoxesOf(_ => [new TextBox(0, 6, 72, 100, 40, 10)]);

        var result = await Run("rg --regions needle /V/d");

        Assert.Equal(
            "Regions only for the first 8 PDFs with matches; name a PDF to get its own.\n"
            + string.Concat(Enumerable.Range(1, 8).Select(i => $"/V/d/p{i}.pdf:p1:crop=69,97,46,16: needle\n"))
            + "/V/d/p9.pdf:p1: needle",
            result.Output);
        Assert.Equal(8, _pixels.BoxRequests.Count);
    }

    [Fact]
    public async Task More_than_fifty_pages_get_no_regions_after_the_fiftieth()
    {
        await Upload("/V/p.pdf", 55, _ => "needle");
        BoxesOf(_ => [new TextBox(0, 6, 72, 100, 40, 10)]);

        var lines = (await Run("rg --regions needle /V/p.pdf")).Output.Split('\n');

        Assert.Equal("Regions for /V/p.pdf only on its first 50 pages with matches; use -m, or look at one page: pdf /V/p.pdf 51.", lines[0]);
        Assert.Equal(56, lines.Length);
        Assert.Equal("/V/p.pdf:p50:crop=69,97,46,16: needle", lines[50]);
        Assert.Equal("/V/p.pdf:p51: needle", lines[51]);
        Assert.Equal("/V/p.pdf:p55: needle", lines[55]);
        Assert.Equal(Enumerable.Range(1, 50), Assert.Single(_pixels.BoxRequests).Pages);
    }

    [Fact]
    public async Task A_page_of_many_items_gives_each_line_its_own_region()
    {
        await Upload("/V/p.pdf", 1, _ => string.Join('\n', Enumerable.Repeat("needle", 300)));
        BoxesOf(_ => [.. Enumerable.Range(0, 300).Select(k => new TextBox(7 * k, 7 * k + 6, 72, 20 + 2 * k, 40, 10))]);

        var lines = (await Run("rg --regions needle /V/p.pdf")).Output.Split('\n');

        Assert.Equal(300, lines.Length);
        Assert.Equal("/V/p.pdf:p1:crop=69,17,46,16: needle", lines[0]);
        Assert.Equal("/V/p.pdf:p1:crop=69,317,46,16: needle", lines[150]);
        Assert.Equal("/V/p.pdf:p1:crop=69,615,46,16: needle", lines[299]);
    }

    [Fact]
    public async Task The_suggested_commands_quote_a_path_with_a_space()
    {
        await Upload("/V/my paper.pdf", 55, _ => "needle");
        BoxesOf(_ => [new TextBox(0, 6, 72, 100, 40, 10)], (_, n) => n == 2 ? "other" : "needle");

        var lines = (await Run("rg --regions needle \"/V/my paper.pdf\"")).Output.Split('\n');

        Assert.Equal("No regions on some pages of /V/my paper.pdf: its stored text comes from an older analysis. pdf info \"/V/my paper.pdf\" renews it.", lines[0]);
        Assert.Equal("Regions for /V/my paper.pdf only on its first 50 pages with matches; use -m, or look at one page: pdf \"/V/my paper.pdf\" 51.", lines[1]);
    }

    [Fact]
    public async Task A_stopped_sidecar_loses_no_match()
    {
        await Revenues();
        _pixels.BoxesFail = new PixelsUnavailableException("PDF rendering is unavailable right now", PixelsProblem.Unavailable);

        var result = await Run("rg --regions -e revenue -e needle /V");

        Assert.False(result.IsError);
        Assert.Equal($"{Unavailable}\n/V/plan.md:2:needle in a note\n/V/p.pdf:p7: Net revenue (after adjustments)", result.Output);
    }

    [Fact]
    public async Task A_stopped_sidecar_is_asked_once_a_search()
    {
        await Revenues("/V/a.pdf");
        await Revenues("/V/b.pdf");
        await Revenues("/V/c.pdf");
        _pixels.BoxesFail = new PixelsUnavailableException("PDF rendering is unavailable right now", PixelsProblem.Unavailable);

        var result = await Run("rg --regions revenue /V");

        Assert.Equal($"{Unavailable}\n/V/a.pdf:p7: {Revenue}\n/V/b.pdf:p7: {Revenue}\n/V/c.pdf:p7: {Revenue}", result.Output);
        Assert.Single(_pixels.BoxRequests);
    }

    [Fact]
    public async Task Any_failure_of_the_boxes_loses_no_match()
    {
        await Revenues("/V/a.pdf");
        await Revenues("/V/b.pdf");
        _pixels.BoxesFail = new PixelsUnavailableException("the sidecar's answer is not valid", PixelsProblem.Failed);

        var result = await Run("rg --regions revenue /V");

        Assert.Equal($"{Unavailable}\n/V/a.pdf:p7: {Revenue}\n/V/b.pdf:p7: {Revenue}", result.Output);
        Assert.Single(_pixels.BoxRequests);
    }

    [Fact]
    public async Task Text_that_differs_gets_no_region_on_that_page()
    {
        await Upload("/V/p.pdf", 12, n => n is 7 or 12 ? Revenue : "");
        BoxesOf(_ => [new TextBox(0, 31, 74.2, 300.1, 210.3, 11.0)], (_, n) => n == 7 ? "Net revenue (after adjustment)" : Revenue);

        Assert.Equal(
            "No regions on some pages of /V/p.pdf: its stored text comes from an older analysis. pdf info /V/p.pdf renews it.\n"
            + $"/V/p.pdf:p7: {Revenue}\n/V/p.pdf:p12:crop=70,296,218,19: {Revenue}",
            (await Run("rg --regions revenue /V/p.pdf")).Output);
    }

    [Fact]
    public async Task A_failure_of_the_text_part_way_keeps_the_lines_held()
    {
        await Upload("/V/long.pdf", 250, n => n is 50 or 150 ? "needle" : "filler");
        _pixels.TextFailsFrom["/V/long.pdf"] = (101, new InvalidOperationException("the text part is gone"));
        BoxesOf(_ => [new TextBox(0, 6, 72, 100, 40, 10)]);

        Assert.Equal("/V/long.pdf:p50:crop=69,97,46,16: needle", (await Run("rg --regions needle /V/long.pdf")).Output);
        Assert.Equal([50], Assert.Single(_pixels.BoxRequests).Pages);
    }

    [Fact]
    public async Task The_notice_joins_the_note_about_other_writers()
    {
        var store = SharedVaults.SharedGarten(asMember: false);
        var caller = new FixedCaller(Writers.Dev);
        var files = new AttachmentWriter(store, store, _time, caller);
        Assert.False((await files.WriteAsync("/Garten/p.pdf", "%PDF-1.7"u8.ToArray(), null, null, Ct)).Result.IsError);
        _pixels.Analyses["/Garten/p.pdf"] = Analysis(1);
        _pixels.Texts["/Garten/p.pdf"] = _ => "Beete planen";
        _pixels.BoxesFail = new PixelsUnavailableException("PDF rendering is unavailable right now", PixelsProblem.Unavailable);
        var alice = new Shell(store, new NoteWriter(store, _time, caller), new NoteMover(store, _time, caller), pixels: _pixels);

        var result = await alice.RunAsync("rg --regions planen /Garten", Ct);

        Assert.Equal($"[/Garten/plan.md: last written by another user]\n{Unavailable}\n/Garten/plan.md:1:Beete planen\n/Garten/p.pdf:p1: Beete planen", result.Output);
    }

    [Fact]
    public async Task Plain_rg_asks_for_no_boxes()
    {
        await Revenues();
        await Paper();

        Assert.Equal("/V/p.pdf:p7: Net revenue (after adjustments)", (await Run("rg revenue /V/p.pdf")).Output);
        Assert.Equal("/V/paper.pdf:p2: the needle sits here\n/V/paper.pdf:p7: last needle line\n/V/paper.pdf:p7: needle again", (await Run("rg needle /V/paper.pdf")).Output);
        Assert.Empty(_pixels.BoxRequests);
    }

    [Fact]
    public async Task Past_the_time_budget_no_further_pdf_is_asked()
    {
        await Revenues("/V/a.pdf");
        await Revenues("/V/b.pdf");
        await Revenues("/V/c.pdf");
        var rg = new Kumbu.Core.Sh.Commands.Rg(_store, _pixels) { RegionBudget = TimeSpan.Zero };

        var result = await rg.RunAsync(["--regions", "revenue", "/V/a.pdf", "/V/b.pdf", "/V/c.pdf"], null, Ct);

        Assert.Equal($"/V/a.pdf:p7:crop=70,296,218,19: {Revenue}\n/V/b.pdf:p7: {Revenue}\n/V/c.pdf:p7: {Revenue}", result.Output);
        Assert.Equal(Unavailable, result.Notice);
        Assert.Single(_pixels.BoxRequests);
    }

    [Fact]
    public async Task The_help_names_regions()
    {
        Assert.Contains(
            """
              --regions                  for matches in PDFs, also where they lie on the page, ready for pdf's --crop:
                                         /Work/paper.pdf:p7:crop=72,298,214,17: text. In points of the page as displayed,
                                         around the piece of text that holds the match. It shows where the text box lies,
                                         not that anyone can see it: look with 'pdf /Work/paper.pdf 7 --crop 72,298,214,17'.
                                         Regions come from the PDF itself each time, for up to 8 PDFs a search; not with
                                         -l, -c, -v, --files, --tag or --version, nor right of a pipe.
            """,
            (await Run("help rg")).Output);
        Assert.Contains("rg --regions also says where on the page a match lies.", (await Run("help pdf")).Output);
    }

    [Fact]
    public async Task An_archived_pdf_is_searched_only_when_asked()
    {
        await Paper("/V/_archive/paper.pdf");

        Assert.Equal("/V/plan.md:2:needle in a note", (await Run("rg needle /V")).Output);
        Assert.DoesNotContain("_archive", (await Run("rg needle")).Output);
        Assert.Equal(
            "/V/plan.md:2:needle in a note\n/V/_archive/paper.pdf:p2: the needle sits here\n/V/_archive/paper.pdf:p7: last needle line\n/V/_archive/paper.pdf:p7: needle again",
            (await Run("rg --archive needle /V")).Output);
        Assert.Equal("/V/_archive/paper.pdf:p2: the needle sits here\n/V/_archive/paper.pdf:p7: last needle line\n/V/_archive/paper.pdf:p7: needle again", (await Run("rg needle /V/_archive/")).Output);
        Assert.Equal("/V/_archive/paper.pdf:p2: the needle sits here\n/V/_archive/paper.pdf:p7: last needle line\n/V/_archive/paper.pdf:p7: needle again", (await Run("rg needle /V/_archive/paper.pdf")).Output);
    }

    [Fact]
    public async Task The_scan_hint_names_no_archived_pdf()
    {
        await Scanned("/V/_archive/scan.pdf", PageType.Image);

        Assert.Equal("", (await Run("rg zzz /V")).Output);
        Assert.Equal($"{PictureHintStart}/V/_archive/scan.pdf (1 page of scans or photos){PictureHintEnd}", (await Run("rg --archive zzz /V")).Output);
        Assert.Equal($"{PictureHintStart}/V/_archive/scan.pdf (1 page of scans or photos){PictureHintEnd}", (await Run("rg zzz /V/_archive/")).Output);
    }

    [Fact]
    public async Task A_vault_holding_only_archived_pdfs_finds_nothing()
    {
        _store.AddVault("P");
        await Paper("/P/_archive/paper.pdf");

        var result = await Run("rg needle /P");

        Assert.False(result.IsError, result.Output);
        Assert.Equal("", result.Output);
    }
}
