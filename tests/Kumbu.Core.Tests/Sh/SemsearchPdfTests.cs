// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>semsearch</c> with pages of PDFs among the hits by meaning: named by page, shown with the start of the page's text.</summary>
public class SemsearchPdfTests
{
    private sealed class FakeIndex(params SemanticHit[] hits) : ISearchIndex
    {
        public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct) =>
            Task.FromResult(new SemanticResult(SemanticState.Ready, [.. hits.OrderByDescending(h => h.Score)], 0));
    }

    private sealed class FakePixels : IAttachmentPixels
    {
        public List<(string Path, int First, int Last)> TextRequests { get; } = [];

        public Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, CancellationToken ct) => throw new NotSupportedException();

        public Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct)
        {
            TextRequests.Add((path.Value, first, last));
            return Task.FromResult<IReadOnlyList<string>?>([.. Enumerable.Range(first, last - first + 1).Select(n => $"Methods\n\n  page {n} measures how {new string('x', 200)}")]);
        }

        public void Count(AttachmentUse use)
        {
        }
    }

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static CancellationToken Ct => CancellationToken.None;

    private readonly InMemoryNoteStore _store = new(
        ("/V/plan.md", "# Plan\nwe measure the zebras\n"),
        ("/V/tagged.md", "#research\nzebras again\n"));

    private readonly FakePixels _pixels = new();

    private async Task<Shell> ShellAsync(ISearchIndex index, bool pixels = true)
    {
        var time = new FakeTimeProvider(new DateTime(2026, 10, 29, 11, 0, 0, DateTimeKind.Utc));
        var caller = new FixedCaller(Writers.Dev);
        var written = await new AttachmentWriter(_store, _store, time, caller).WriteAsync("/V/paper.pdf", "%PDF-1.7"u8.ToArray(), null, null, Ct);
        Assert.False(written.Result.IsError, written.Result.Output);
        return new Shell(_store, new NoteWriter(_store, time, caller), new NoteMover(_store, time, caller), search: index, pixels: pixels ? _pixels : null);
    }

    private static FakeIndex Index() => new(new SemanticHit(P("/V/paper.pdf"), 7, 0.9), new SemanticHit(P("/V/plan.md"), 0, 0.5));

    [Fact]
    public async Task A_pdf_hit_names_its_page_and_shows_the_start_of_its_text()
    {
        var sh = await ShellAsync(Index());

        var result = await sh.RunAsync("semsearch zebras /V", Ct);

        Assert.Equal(
            "1. /V/paper.pdf p7  score 0.90\n"
            + $"   Methods page 7 measures how {new string('x', 132)}…\n"
            + "2. /V/plan.md#Plan  lines 1-2  score 0.60\n"
            + "   we measure the zebras\n"
            + "(ranked by words and meaning; cat \"path#Section\" reads a section, pdf path N a page)",
            result.Output);
        Assert.Equal(("/V/paper.pdf", 7, 7), Assert.Single(_pixels.TextRequests));
    }

    [Fact]
    public async Task By_meaning_only_a_pdf_page_ranks_by_its_cosine()
    {
        var sh = await ShellAsync(new FakeIndex(new SemanticHit(P("/V/paper.pdf"), 3, 0.55), new SemanticHit(P("/V/plan.md"), 0, 0.6)));

        var result = await sh.RunAsync("semsearch --meaning zebras /V", Ct);

        Assert.StartsWith("1. /V/plan.md#Plan  lines 1-2  score 0.60\n   we measure the zebras\n2. /V/paper.pdf p3  score 0.55\n", result.Output);
    }

    [Fact]
    public async Task By_words_no_pdf_is_listed()
    {
        var sh = await ShellAsync(Index());

        var result = await sh.RunAsync("semsearch --words zebras /V", Ct);

        Assert.DoesNotContain("paper.pdf", result.Output);
        Assert.Contains("/V/plan.md#Plan", result.Output);
        Assert.Empty(_pixels.TextRequests);
    }

    [Fact]
    public async Task A_tag_leaves_the_pdfs_out_since_they_carry_none()
    {
        var sh = await ShellAsync(Index());

        var result = await sh.RunAsync("semsearch --tag research zebras /V", Ct);

        Assert.DoesNotContain("paper.pdf", result.Output);
    }

    [Fact]
    public async Task Without_pixels_pdf_hits_are_left_out()
    {
        var sh = await ShellAsync(Index(), pixels: false);

        var result = await sh.RunAsync("semsearch zebras /V", Ct);

        Assert.DoesNotContain("paper.pdf", result.Output);
    }

    [Fact]
    public async Task A_pdf_as_the_path_ranks_its_pages()
    {
        var sh = await ShellAsync(Index());

        var result = await sh.RunAsync("semsearch zebras /V/paper.pdf", Ct);
        var words = await sh.RunAsync("semsearch --words zebras /V/paper.pdf", Ct);

        Assert.StartsWith("1. /V/paper.pdf p7  score 0.90\n", result.Output);
        Assert.True(words.IsError);
        Assert.Equal("semsearch: /V/paper.pdf is a PDF; semsearch --words searches notes only, by meaning it finds its pages", words.Output);
    }

    [Fact]
    public async Task Help_says_pages_of_pdfs_rank_by_meaning()
    {
        var sh = await ShellAsync(Index());

        var help = (await sh.RunAsync("help semsearch", Ct)).Output;

        Assert.Contains("Pages of PDFs rank among the sections by meaning", help);
        Assert.Contains("Pages without a text layer (scans) and images are not searched.", help);
        Assert.DoesNotContain("semsearch skips", (await sh.RunAsync("help attachments", Ct)).Output);
        Assert.DoesNotContain("semsearch skips", Manual.ToolDescription);
    }

    [Fact]
    public async Task A_folder_with_only_pdfs_is_searched_for_their_pages()
    {
        var sh = await ShellAsync(new FakeIndex(new SemanticHit(P("/V/Papers/b.pdf"), 2, 0.7)));
        var time = new FakeTimeProvider(new DateTime(2026, 10, 29, 11, 0, 0, DateTimeKind.Utc));
        await new AttachmentWriter(_store, _store, time, new FixedCaller(Writers.Dev)).WriteAsync("/V/Papers/b.pdf", "%PDF-1.7"u8.ToArray(), null, null, Ct);

        var result = await sh.RunAsync("semsearch zebras /V/Papers", Ct);
        var empty = await sh.RunAsync("semsearch zebras /V/Nothing", Ct);

        Assert.False(result.IsError, result.Output);
        Assert.StartsWith("1. /V/Papers/b.pdf p2  score 0.70\n", result.Output);
        Assert.True(empty.IsError);
    }
}
