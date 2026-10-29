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

        public Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, CancellationToken ct) => throw new NotSupportedException();

        public Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, CancellationToken ct) => throw new NotSupportedException();

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
}
