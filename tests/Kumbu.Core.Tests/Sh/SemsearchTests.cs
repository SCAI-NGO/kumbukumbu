// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Kumbu.Core.Tests.Search;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>What <c>semsearch</c> prints of a hit: its whole core, with <c>-C</c> the rolls around it, with <c>-l</c> one line.</summary>
public class SemsearchTests
{
    private sealed class FakeIndex(ChunkBound bound, params SemanticHit[] hits) : ISearchIndex
    {
        public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct) =>
            Task.FromResult(new SemanticResult(SemanticState.Ready, [.. hits.OrderByDescending(h => h.Score)], 0));

        public ChunkBound Bound => bound;
    }

    /// <summary>Pixels whose every page has the text <paramref name="text"/> gives: null when it can't be read, or a throw.</summary>
    private sealed class FakePixels(Func<string?> text) : IAttachmentPixels
    {
        public FakePixels(string page)
            : this(() => page)
        {
        }

        public Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>?>(text() is { } page ? [.. Enumerable.Range(first, last - first + 1).Select(_ => page)] : null);

        public Task<Picture> ImageAsync(
            VaultPath path, AttachmentState state, SizeStep step, Crop? crop, int rotate, int? frame, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Picture> PageAsync(
            VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, int rotate, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PageTextBoxes>> TextBoxesAsync(
            VaultPath path, AttachmentState state, IReadOnlyList<int> pages, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PdfPageImages> PdfImagesAsync(VaultPath path, AttachmentState state, int page, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Picture> PdfImageAsync(VaultPath path, AttachmentState state, int page, int n, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Picture> PdfRegionAsync(
            VaultPath path, AttachmentState state, int page, Crop crop, int rotate, bool jpeg, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Count(AttachmentUse use)
        {
        }

        public IDisposable? TryStartExtraction() => throw new NotSupportedException();
    }

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static CancellationToken Ct => CancellationToken.None;

    private static readonly Note Pump = new(P("/Work/Pump/Manual.md"), ChunkerFixtures.PumpManual);

    private static Shell ShellOf(InMemoryNoteStore store, ISearchIndex index, IAttachmentPixels? pixels = null)
    {
        var time = new FakeTimeProvider(new DateTime(2026, 11, 9, 11, 0, 0, DateTimeKind.Utc));
        var caller = new FixedCaller(Writers.Dev);
        return new Shell(store, new NoteWriter(store, time, caller), new NoteMover(store, time, caller), search: index, pixels: pixels);
    }

    private static Shell PumpShell(params SemanticHit[] hits) =>
        ShellOf(new InMemoryNoteStore((Pump.Path.Value, Pump.Content)), new FakeIndex(ChunkBound.Fallback, hits));

    /// <summary>The second part of the manual's table, as the search chunks it.</summary>
    private static Chunk SecondTablePart() => Chunker.Split(Pump, ChunkBound.Fallback).Where(c => c.Text.Contains("| E")).ElementAt(1);

    private static IEnumerable<string> Core(string text) => text.Split('\n').Select(l => l.Length == 0 ? "" : "   " + l);

    private static IEnumerable<string> Roll(string text) =>
        text.Length == 0 ? [] : text.Split('\n').Select(l => l.Length == 0 ? "   ┊" : "   ┊ " + l);

    [Fact]
    public async Task Prints_the_whole_core_of_each_hit()
    {
        var part = SecondTablePart();
        var gate = new Note(P("/Work/gate.md"), "# Gate\nThe service checks rules.\n\nAnna and Ben.\n");
        var store = new InMemoryNoteStore((Pump.Path.Value, Pump.Content), (gate.Path.Value, gate.Content));
        var index = new FakeIndex(ChunkBound.Fallback, new SemanticHit(Pump.Path, part.StartLine, 0.71), new SemanticHit(gate.Path, 0, 0.5));
        var sh = ShellOf(store, index);

        var output = (await sh.RunAsync("semsearch --meaning sensor drift /Work", Ct)).Output;

        Assert.Equal(
            string.Join('\n', [
                $"1. /Work/Pump/Manual.md#Pump P-200#Error codes  lines {part.StartLine + 1}-{part.EndLine}  score 0.71",
                .. Core(part.Text),
                "2. /Work/gate.md#Gate  lines 1-4  score 0.50",
                "   # Gate",
                "   The service checks rules.",
                "",
                "   Anna and Ben.",
                "(ranked by meaning; cat \"path#Section\" reads one)",
            ]),
            output);
        Assert.StartsWith("   | Code | Meaning | Remedy |\n   |------|", output.Split('\n', 2)[1]);
    }

    [Fact]
    public async Task With_C_it_prints_the_rolls_marked()
    {
        var part = SecondTablePart();
        var (before, after) = Chunker.Rolls(Pump, part);
        Assert.NotEmpty(before);
        Assert.NotEmpty(after);

        var sh = PumpShell(new SemanticHit(Pump.Path, part.StartLine, 0.71));

        var output = (await sh.RunAsync("semsearch -C --meaning sensor drift /Work", Ct)).Output;

        Assert.Equal(
            string.Join('\n', [
                $"1. /Work/Pump/Manual.md#Pump P-200#Error codes  lines {part.StartLine + 1}-{part.EndLine}  score 0.71",
                .. Roll(before),
                .. Core(part.Text),
                .. Roll(after),
                "(ranked by meaning; cat \"path#Section\" reads one)",
            ]),
            output);
        var body = output.Split('\n')[1..^1];
        Assert.Equal(Lines.Split(before).Count + Lines.Split(after).Count, body.Count(l => l.StartsWith("   ┊ ", StringComparison.Ordinal)));
        Assert.All(Core(part.Text), l => Assert.DoesNotContain("┊", l));
    }

    [Fact]
    public async Task With_l_it_prints_one_line_per_hit()
    {
        var part = SecondTablePart();
        var sh = PumpShell(new SemanticHit(Pump.Path, part.StartLine, 0.71), new SemanticHit(Pump.Path, 246, 0.6));

        var output = (await sh.RunAsync("semsearch -l --meaning sensor drift /Work", Ct)).Output;

        Assert.Equal(
            $"1. /Work/Pump/Manual.md#Pump P-200#Error codes  lines {part.StartLine + 1}-{part.EndLine}  score 0.71\n"
            + "2. /Work/Pump/Manual.md#Pump P-200#Maintenance  lines 247-251  score 0.60\n"
            + "(ranked by meaning; cat \"path#Section\" reads one)",
            output);
        Assert.Equal(output, (await sh.RunAsync("semsearch --list --meaning sensor drift /Work", Ct)).Output);

        // The search as typed again with --archive keeps -l.
        var store = new InMemoryNoteStore(("/V/_archive/a.md", "pumps of old\n"), ("/V/b.md", "other\n"));
        var archived = ShellOf(store, new FakeIndex(ChunkBound.Fallback));
        Assert.StartsWith(
            "(nothing found for 'pumps' below /V outside the archive; semsearch --archive -l --words pumps /V searches it)",
            (await archived.RunAsync("semsearch -l --words pumps /V", Ct)).Output);
    }

    [Theory]
    [InlineData("semsearch -C 3 pump")]
    [InlineData("semsearch -C3 pump")]
    [InlineData("semsearch --words -C 12 pump /Work")]
    public async Task C_takes_no_number(string line)
    {
        var result = await PumpShell().RunAsync(line, Ct);

        Assert.True(result.IsError);
        Assert.Equal(
            "semsearch: -C takes no number; to search for one, put it after --: semsearch -C -- 2025 budget "
            + "(semsearch [-n N] [-C|-l] [--words|--meaning] [--tag T] [--archive] <query> [path])",
            result.Output);
        // After "--" a number is the query's.
        Assert.False((await PumpShell().RunAsync("semsearch --words -C -- 3 pump", Ct)).IsError);
    }

    [Fact]
    public async Task C_and_l_exclude_each_other()
    {
        Assert.Equal(ShellResult.Error("semsearch: -C and -l exclude each other"), await PumpShell().RunAsync("semsearch -C -l pump", Ct));
        Assert.Equal(ShellResult.Error("semsearch: -C and -l exclude each other"), await PumpShell().RunAsync("semsearch -lC pump", Ct));
    }

    private const string Read = "to fit the output; cat \"path#Section\" or pdf path N reads one";

    [Fact]
    public async Task Cores_that_do_not_fit_fall_back_to_one_line()
    {
        var notes = Enumerable.Range(0, 30)
            .Select(i => ($"/V/n{i:00}.md", $"# Note {i}\n" + string.Join(' ', Enumerable.Range(0, 200).Select(w => $"w{i:00}x{w:00000}")) + "\n"))
            .ToArray();
        Assert.All(notes, n => Assert.InRange(n.Item2.Length, 2_000, 2_100));
        var index = new FakeIndex(ChunkBound.Unbounded, [.. notes.Select((n, i) => new SemanticHit(P(n.Item1), 0, 0.9 - i * 0.01))]);

        var output = (await ShellOf(new InMemoryNoteStore(notes), index).RunAsync("semsearch -n 30 --meaning notes /V", Ct)).Output;

        Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters");
        var lines = output.Split('\n');
        Assert.Equal("(ranked by meaning; cat \"path#Section\" reads one)", lines[^1]);
        Assert.DoesNotContain("[truncated", output);
        // The hits with their text first, then the rest as one line each.
        var headers = lines.Select((l, i) => (l, i)).Where(x => x.l.Contains(".md#Note ")).Select(x => x.i).ToList();
        Assert.Equal(30, headers.Count);
        var withText = headers.Count(h => lines[h + 1].StartsWith("   ", StringComparison.Ordinal));
        Assert.InRange(withText, 5, 15);
        Assert.All(headers.Take(withText), h => Assert.StartsWith("   # Note ", lines[h + 1]));
        Assert.All(headers.Skip(withText), h => Assert.False(lines[h + 1].StartsWith("   ", StringComparison.Ordinal), lines[h + 1]));
        Assert.Equal($"(hits {withText + 1}-30 without their text, {Read})", lines[^2]);
        Assert.StartsWith("30. /V/n29.md#Note 29  lines 1-2", lines[^3]);
    }

    [Fact]
    public async Task Hits_whose_lines_do_not_fit_are_left_out()
    {
        var notes = Enumerable.Range(0, 700).Select(i => ($"/V/note{i:000}.md", $"# Topic {i}\nAbout pumps.\n")).ToArray();
        var index = new FakeIndex(ChunkBound.Unbounded, [.. notes.Select((n, i) => new SemanticHit(P(n.Item1), 0, 0.9 - i * 0.001))]);

        var output = (await ShellOf(new InMemoryNoteStore(notes), index).RunAsync("semsearch -l -n 700 --meaning pumps /V", Ct)).Output;

        Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters");
        var lines = output.Split('\n');
        Assert.Equal("(ranked by meaning; cat \"path#Section\" reads one)", lines[^1]);
        var shown = lines.Length - 2;
        Assert.InRange(shown, 100, 699);
        Assert.StartsWith($"{shown}. /V/note{shown - 1:000}.md#Topic {shown - 1}  lines 1-2", lines[^3]);
        Assert.Equal($"(hits {shown + 1}-700 left out, to fit the output)", lines[^2]);
    }

    [Fact]
    public async Task The_notice_above_the_hits_counts_towards_the_limit()
    {
        // A shared vault whose notes another user wrote last, with long names: the notice names every one printed.
        var store = new InMemoryNoteStore();
        var paths = Enumerable.Range(0, 120).Select(i => P($"/Garten/{new string('b', 120)}-{i:000}.md")).ToList();
        var text = "# Beet\n" + string.Join(' ', Enumerable.Range(0, 100).Select(w => $"Wort{w:0000}")) + "\n";
        foreach (var path in paths)
        {
            store.Put(new StoredNote(path, text, 1, "", InMemoryNoteStore.Seeded, [], text, Ref: WriterRef.ForMember("w-carol")));
        }

        store.Infos["Garten"] = new VaultInfo(null, AccessLevel.Owner, true);
        var index = new FakeIndex(ChunkBound.Unbounded, [.. paths.Select((p, i) => new SemanticHit(p, 0, 0.9 - i * 0.001))]);

        foreach (var options in new[] { "", "-l " })
        {
            var output = (await ShellOf(store, index).RunAsync($"semsearch {options}-n 120 --meaning beet /Garten", Ct)).Output;

            Assert.True(output.Length <= OutputLimit.MaxChars, $"{output.Length} characters");
            Assert.DoesNotContain("[truncated", output);
            var lines = output.Split('\n');
            Assert.StartsWith($"[{WriterMarks.Mark}: /Garten/", lines[0]);
            Assert.Equal("(ranked by meaning; cat \"path#Section\" reads one)", lines[^1]);
            var shown = lines.Count(l => l.Contains(".md#Beet  lines", StringComparison.Ordinal));
            Assert.InRange(shown, 10, 119);
            Assert.Equal($"(hits {shown + 1}-120 left out, to fit the output)", lines[^2]);
            // The notice names the notes of the lines printed, and no other.
            Assert.Equal(paths.Take(shown).Select(p => p.Value), lines[0][(WriterMarks.Mark.Length + 3)..^1].Split(", "));
        }
    }

    private static async Task<InMemoryNoteStore> StoreWithPdfAsync()
    {
        var store = new InMemoryNoteStore((Pump.Path.Value, Pump.Content));
        var time = new FakeTimeProvider(new DateTime(2026, 11, 9, 11, 0, 0, DateTimeKind.Utc));
        var writer = new AttachmentWriter(store, store, time, new FixedCaller(Writers.Dev));
        var written = await writer.WriteAsync("/Work/Pump/P-200.pdf", "%PDF-1.7"u8.ToArray(), null, null, Ct);
        Assert.False(written.Result.IsError, written.Result.Output);
        return store;
    }

    private static FakeIndex PageHit(int part) => new(ChunkBound.Fallback, new SemanticHit(P("/Work/Pump/P-200.pdf"), 12, 0.64) { Part = part });

    private const string PagesFooter = "(ranked by meaning; cat \"path#Section\" reads a section, pdf path N a page)";

    [Fact]
    public async Task A_PDF_hit_prints_its_best_part()
    {
        var page = string.Join('\n', Enumerable.Range(0, 94).Select(i => $"Line {i:00} of the page " + new string((char)('a' + i % 26), 76)));
        Assert.InRange(page.Length, 9_000, 9_200);
        var chunks = Chunker.SplitPage("P-200", 12, page, ChunkBound.Fallback);
        Assert.True(chunks.Count > 3, $"{chunks.Count} chunks");
        var store = await StoreWithPdfAsync();
        var sh = ShellOf(store, PageHit(2), new FakePixels(page));

        var output = (await sh.RunAsync("semsearch --meaning seal /Work/Pump/P-200.pdf", Ct)).Output;
        var context = (await sh.RunAsync("semsearch -C --meaning seal /Work/Pump/P-200.pdf", Ct)).Output;

        Assert.Equal(string.Join('\n', ["1. /Work/Pump/P-200.pdf p12  score 0.64", .. Core(chunks[2].Core), PagesFooter]), output);
        Assert.Equal(
            string.Join('\n', [
                "1. /Work/Pump/P-200.pdf p12  score 0.64", .. Roll(chunks[2].PreRoll), .. Core(chunks[2].Core), .. Roll(chunks[2].PostRoll),
                PagesFooter,
            ]),
            context);

        // A part the page no longer has shows the first.
        var gone = ShellOf(store, PageHit(99), new FakePixels(page));
        Assert.Contains(string.Join('\n', Core(chunks[0].Core)), (await gone.RunAsync("semsearch --meaning seal /Work/Pump/P-200.pdf", Ct)).Output);
    }

    [Fact]
    public async Task A_page_whose_text_cant_be_read_says_so()
    {
        var store = await StoreWithPdfAsync();
        const string Expected = "1. /Work/Pump/P-200.pdf p12  score 0.64\n   (the text of this page can't be read right now)\n" + PagesFooter;

        FakePixels[] unreadable = [new(() => null), new(() => throw new PixelsUnavailableException("the PDF service is down"))];
        foreach (var pixels in unreadable)
        {
            Assert.Equal(Expected, (await ShellOf(store, PageHit(0), pixels).RunAsync("semsearch --meaning seal /Work/Pump/P-200.pdf", Ct)).Output);
        }
    }

    [Fact]
    public async Task Help_documents_C_and_l()
    {
        var help = (await PumpShell().RunAsync("help semsearch", Ct)).Output;

        Assert.StartsWith("semsearch [-n N] [-C|-l] [--words|--meaning] [--tag T] [--archive] <query> [path]\n", help);
        Assert.Contains("\n  -C         also print the text around each hit, its lines marked with ┊; context, not part of the match\n", help);
        Assert.Contains("\n  -l         one line per hit: path, lines and score\n", help);
        Assert.Contains("\n  --words    rank by words only", help);
        Assert.Contains("then the\n  section's whole text.", help);
        Assert.Contains("path#Section then names that parent, and the lines are exact.", help);
        Assert.DoesNotContain("the start of", help);
        // No line wider than the option lines that were there before.
        var widest = "  --archive  search a vault's archive (_archive/) too: its notes and PDF pages; a path inside it always is".Length;
        Assert.All(help.Split('\n'), l => Assert.True(l.Length <= widest, l));
    }
}
