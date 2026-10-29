// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sections;

namespace Kumbu.Core.Sh.Commands;

/// <summary><c>pdf</c>: a PDF page by page, as its text, with rendered pages where asked, or one region of a page.</summary>
public sealed class PdfCommand(INoteReader notes, IAttachmentPixels pixels) : ICommand
{
    public const int MaxImages = 4;
    public const int MaxPages = 20;
    private const int MaxRegions = 8;
    private const int MaxInfoRuns = 200;

    // Room left under the output limit for what follows the pages: a picture block, the line naming where to go on.
    private const int Reserve = 1_000;

    private static readonly Option[] Spec = [new(null, "images"), new(null, "size", TakesValue: true), new(null, "crop", TakesValue: true)];

    private enum Show { Text, Images, All }

    public string Name => "pdf";

    public string Usage => "pdf [info] <path> [pages]";

    public string Summary => "read a PDF page by page: text, rendered pages, a part of one";

    public string Help => $"""
        pdf <path> [pages] [--images | --images=all] [--crop x,y,w,h] [--size small|medium|large]
        pdf info <path>
          Read a PDF page by page. pages is N, N-M or N- (to the end), all pages if omitted. Each page
          starts with a line naming it, its type and what was found on it, followed by its text:
            --- Page 4 of 12: mixed (text; vector drawing ≈ 35 % of the page) — add --images to see it ---
          The types come from an analysis made when the PDF is uploaded:
            image   one image covers at least 85 % of the page: a scan or a photo, with or without a text layer
            mixed   images and vector drawings together cover at least 25 % of the page: charts, figures
            text    anything else, an empty page too
          An image inside the PDF of more than 50 megapixels is left out of the analysis, so its page reads
          as text.
          pdf info <path>   the page count, title and author, the types in runs (1-3 text, 4 mixed), and the
                            figures of each page that isn't text with their regions in points, ready for
                            --crop. A cheap first look.
          --images          also show the mixed and image pages of the range as an image, after their text
          --images=all      show every page of the range as an image, whatever its type
          --crop x,y,w,h    one page only: a part of it, rendered anew at full sharpness, without the text;
                            in points of the page as displayed (1 pt = 1/72 inch), x and y from the top left,
                            as 'pdf info' gives the regions
          --size S          the step for images, medium if omitted:
        {SizeSteps.Table}
          At most 4 images come from one call, and at most {MaxPages} pages, fewer when their text would pass the
          output limit (about {OutputLimit.MaxChars / OutputLimit.CharsPerToken} tokens); the last line then names the range to go on with.
          rg finds text in PDFs (/Work/paper.pdf:p7: text), and semsearch finds their pages by
          meaning (1. /Work/paper.pdf p7). Neither finds the text of pages without a text layer (scans),
          which have none: look at them with --images. A PDF of more than 2000 pages is analysed up to page 2000, and says so.
          If the analysis hasn't run yet, pdf runs it first (up to a minute); until it is there the text and
          the types aren't known, and only --images=all and --crop show pages.
          pdf stands alone, not in a pipeline. A client may drop images; if none follows a page's text, say
          so to the user rather than guess. For image attachments see 'help img'.

          pdf info /Work/Papers/paper.pdf
          pdf /Work/Papers/paper.pdf 5-9
          pdf /Work/Papers/paper.pdf 1-20 --images
          pdf /Work/Papers/paper.pdf 4 --images=all
          pdf /Work/Papers/paper.pdf 4 --crop 72,300,451,260 --size large
        """;

    public bool ReadsInput => false;

    public bool ProducesImages => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        // "--images=all" is a value of a flag that otherwise takes none, so it is taken out before the options are read.
        var show = Show.Text;
        var rest = new List<string>();
        var options = true;
        foreach (var arg in args)
        {
            if (arg == "--") options = false;
            if (options && arg == "--images=all") show = Show.All;
            else if (options && arg.StartsWith("--images=", StringComparison.Ordinal))
            {
                return Invalid($"pdf: --images shows the mixed and image pages, --images=all every page; not '{arg}'");
            }
            else rest.Add(arg);
        }

        if (!Options.TryParse(Name, rest, Spec, out var parsed, out var optionError)) return optionError;
        if (parsed.Has("images") && show == Show.Text) show = Show.Images;
        if (parsed.Operands.Count == 0) return Invalid("pdf: missing operand");

        if (parsed.Operands[0] == "info")
        {
            var example = parsed.Operands.Count > 1 ? parsed.Operands[1] : "/Work/Papers/paper.pdf";
            if (parsed.Operands.Count != 2 || show != Show.Text || parsed.Has("size") || parsed.Has("crop"))
            {
                return Invalid($"pdf: info takes a path and nothing else, e.g. pdf info {example}");
            }

            return await InfoAsync(parsed.Operands[1], ct);
        }

        if (parsed.Operands.Count > 2) return Invalid($"pdf: too many arguments{CommandArgs.QuoteHint}");

        var step = SizeStep.Medium;
        if (parsed.Value("size") is { } size && !SizeSteps.TryParse(size, out step))
        {
            return Invalid($"pdf: --size is small, medium or large, not '{size}' (see 'help pdf')");
        }

        Crop? crop = null;
        if (parsed.Value("crop") is { } cropText)
        {
            if (!Crop.TryParse(cropText, out var c, out var cropError)) return Invalid($"pdf: {cropError}");
            crop = c;
        }

        var range = PageRange.All;
        if (parsed.Operands.Count == 2 && !PageRange.TryParse(parsed.Operands[1], out range, out var rangeError)) return Invalid($"pdf: {rangeError}");

        var (opened, error) = await OpenAsync(parsed.Operands[0], ct);
        if (opened is null) return error!;
        var (path, state) = opened.Value;
        if (crop is not null && !range.IsSingle)
        {
            return Invalid($"pdf: --crop takes a single page, e.g. pdf {SectionLookup.Quote(path)} {range.First} --crop {parsed.Value("crop")}");
        }

        var call = new Call(path, state, step, crop, show, range);
        pixels.Count(new PdfCalled(call.Mode));
        PdfAnalysis? analysis;
        try
        {
            analysis = await pixels.AnalysisAsync(path, state, runIfPending: true, ct);
        }
        catch (PixelsUnavailableException e)
        {
            // Rendering needs no analysis: a PDF that couldn't be analysed may still show its pages.
            return crop is not null || show == Show.All ? await WithoutAnalysisAsync(call, $"not analysed ({e.Message})", ct) : ShellResult.Error($"pdf: {e.Message}");
        }

        if (analysis is null)
        {
            if (crop is not null || show == Show.All) return await WithoutAnalysisAsync(call, "not analysed yet", ct);
            if (show == Show.Images) return ShellResult.Error("pdf: --images chooses pages by their type, which isn't known yet; use --images=all for a range");
            return NotAnalysedYet(path);
        }

        if (!range.TryClamp(analysis.PageCount, path.Value, out var pages, out var clampError)) return Invalid($"pdf: {clampError}");
        return crop is not null ? await CropAsync(call, analysis, ct) : await PagesAsync(call with { Range = pages }, analysis, range.Last is null, ct);
    }

    private sealed record Call(VaultPath Path, AttachmentState State, SizeStep Step, Crop? Crop, Show Show, PageRange Range)
    {
        public PdfMode Mode => Crop is not null ? PdfMode.Crop : Show switch { Show.All => PdfMode.ImagesAll, Show.Images => PdfMode.Images, _ => PdfMode.Text };
    }

    private static PageKind KindOf(PdfPage? page) => page is null ? PageKind.Unknown : page.Type switch
    {
        PageType.Mixed => PageKind.Mixed,
        PageType.Image => PageKind.Image,
        _ => PageKind.Text,
    };

    /// <summary>A page delivered as a picture: counted as delivered, and by its type for the mode of the call.</summary>
    private void CountPicture(Call call, PdfPage? page)
    {
        pixels.Count(new PdfPageDelivered(PageDelivery.Image));
        pixels.Count(new PdfPageRendered(KindOf(page), call.Mode));
    }

    private static ShellResult Invalid(string message) => ShellResult.Error(message) with { Kind = ShellErrorKind.Invalid };

    private static ShellResult NotAnalysedYet(VaultPath path)
    {
        var quoted = SectionLookup.Quote(path);
        return ShellResult.Error($"pdf: {path.Value} hasn't been analysed yet: its text and page types aren't known. Try again in a while; pdf {quoted} 1 --images=all shows a page as an image.");
    }

    /// <summary>The PDF at <paramref name="pathText"/>, or the error for a path that isn't one, in the words of <c>cat</c>.</summary>
    private async Task<((VaultPath Path, AttachmentState State)? Opened, ShellResult? Error)> OpenAsync(string pathText, CancellationToken ct)
    {
        if (!CommandArgs.TryParsePath(Name, pathText, out var path, out var pathError)) return (null, pathError! with { Kind = ShellErrorKind.Invalid });
        if (await StoreAttachments.GetAsync(notes, path, ct) is not { } attachment)
        {
            var read = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
            if (!read.IsError) return (null, Invalid($"pdf: {path.Value} is a note; cat reads notes"));
            return (null, read with { Kind = read.Output.Contains(": No such file or directory", StringComparison.Ordinal) ? ShellErrorKind.NotFound : ShellErrorKind.Invalid });
        }

        if (!AttachmentText.IsPdf(attachment.Current))
        {
            return (null, Invalid($"pdf: {path.Value} is an image; look at it with img {SectionLookup.Quote(path)} (see 'help img')"));
        }

        return ((path, attachment.Current), null);
    }

    private async Task<ShellResult> InfoAsync(string pathText, CancellationToken ct)
    {
        var (opened, error) = await OpenAsync(pathText, ct);
        if (opened is null) return error!;
        var (path, state) = opened.Value;
        pixels.Count(new PdfCalled(PdfMode.Info));
        PdfAnalysis? analysis;
        try
        {
            analysis = await pixels.AnalysisAsync(path, state, runIfPending: true, ct);
        }
        catch (PixelsUnavailableException e)
        {
            return ShellResult.Error($"pdf: {e.Message}");
        }

        if (analysis is null) return NotAnalysedYet(path);

        var lines = new List<string> { $"{path.Name}: PDF, {AttachmentText.Pages(analysis.PageCount)}, {AttachmentText.Size(state.Size)}" };
        if (Metadata(analysis.Title) is { Length: > 0 } title) lines.Add($"Title: {title}");
        if (Metadata(analysis.Author) is { Length: > 0 } author) lines.Add($"Author: {author}");
        lines.Add($"Pages: {AttachmentText.Runs(analysis, MaxInfoRuns)}");
        if (analysis.AnalysedPages < analysis.PageCount)
        {
            lines.Add($"Analysed the first {analysis.AnalysedPages} of {analysis.PageCount} pages; later pages have no text or types.");
        }

        var figures = analysis.Pages.Where(p => p.Type != PageType.Text).OrderBy(p => p.Number).ToList();
        var used = lines.Sum(l => l.Length + 1);
        for (var i = 0; i < figures.Count; i++)
        {
            var line = InfoLine(figures[i]);
            if (used + line.Length + 1 > OutputLimit.MaxChars - Reserve)
            {
                lines.Add($"…pages {figures[i].Number}-{figures[^1].Number} not listed; pdf info shows the first {i}");
                break;
            }

            lines.Add(line);
            used += line.Length + 1;
        }

        return ShellResult.Ok(string.Join('\n', lines));
    }

    /// <summary>A title or author on one line: the PDF's metadata is untrusted and must not forge lines of the answer.</summary>
    private static string Metadata(string? text) =>
        text is null ? "" : new string([.. text.Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c)]).Trim();

    /// <summary>"p4 mixed: vector drawing ≈ 35 % — regions (pt, x,y,w,h): drawing 72,300,451,260; image 80,90,200,120".</summary>
    private static string InfoLine(PdfPage page)
    {
        var found = page.Type == PageType.Image
            ? "one image covers the page" + (page.Chars == 0 ? ", no text layer" : "")
            : string.Join(", ", Figures(page, small: false));
        var regions = page.Regions.Take(MaxRegions).Select(r => $"{r.Kind} {Point(r.X)},{Point(r.Y)},{Point(r.Width)},{Point(r.Height)}").ToList();
        if (page.Regions.Count > MaxRegions) regions.Add($"…and {page.Regions.Count - MaxRegions} more");
        var listed = regions.Count == 0 ? "" : $" — regions (pt, x,y,w,h): {string.Join("; ", regions)}";
        return $"p{page.Number} {AttachmentText.TypeName(page.Type)}: {found}{listed}";
    }

    private static string Point(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    private static int Percent(double coverage) => (int)Math.Round(coverage * 100, MidpointRounding.AwayFromZero);

    /// <summary>"1 small image ≈ 6 %", "vector drawing ≈ 35 %": what covers the page, leaving out what rounds to 0 %.</summary>
    private static List<string> Figures(PdfPage page, bool small)
    {
        var found = new List<string>();
        if (Percent(page.ImageCoverage) is > 0 and var images)
        {
            var noun = (small ? "small " : "") + (page.Images == 1 ? "image" : "images");
            found.Add($"{(page.Images > 0 ? $"{page.Images} " : "")}{noun} ≈ {images} %");
        }

        if (Percent(page.VectorCoverage) is > 0 and var vector) found.Add($"{(small ? "small drawing" : "vector drawing")} ≈ {vector} %");
        return found;
    }

    /// <summary>What the header says in brackets: "text; vector drawing ≈ 35 % of the page", "1 small image ≈ 6 %", or nothing.</summary>
    private static string? Found(PdfPage page)
    {
        switch (page.Type)
        {
            case PageType.Image:
                return $"{(page.Chars > 0 ? "text layer" : "no text layer")}; one image covers the page";
            case PageType.Mixed:
                var figures = Figures(page, small: false);
                return $"{(page.Chars > 0 ? "text" : "no text layer")}{(figures.Count > 0 ? $"; {string.Join(", ", figures)} of the page" : "")}";
            default:
                var small = Figures(page, small: true);
                return small.Count > 0 ? string.Join(", ", small) : page.Chars == 0 ? "empty" : null;
        }
    }

    /// <summary>Text and pictures in the order the client is to get them; the text of a call is kept within the output limit here.</summary>
    private sealed class Output
    {
        private readonly List<ShellPart> _parts = [];
        private readonly StringBuilder _text = new();

        public int Chars { get; private set; }

        public int Images { get; private set; }

        /// <summary>A block of text, a blank line from the one before.</summary>
        public void Add(string block)
        {
            if (_text.Length > 0 || _parts.Count > 0) Chars += 2;
            if (_text.Length > 0) _text.Append("\n\n");
            _text.Append(block);
            Chars += block.Length;
        }

        /// <summary>A line right under the block before.</summary>
        public void Append(string line)
        {
            _text.Append('\n').Append(line);
            Chars += line.Length + 1;
        }

        public void Add(Picture picture)
        {
            _parts.Add(new TextPart(_text.ToString()));
            _text.Clear();
            _parts.Add(new ImagePart(picture.Data, picture.MediaType));
            Images++;
        }

        public ShellResult Result()
        {
            if (_text.Length > 0) _parts.Add(new TextPart(_text.ToString()));
            var texts = _parts.OfType<TextPart>().Select(t => t.Text).ToList();
            var output = string.Join("\n\n", texts);
            return Images == 0 ? ShellResult.Ok(output) : ShellResult.Ok(output) with { Parts = [.. _parts] };
        }
    }

    private async Task<ShellResult> PagesAsync(Call call, PdfAnalysis analysis, bool open, CancellationToken ct)
    {
        var (first, last) = (call.Range.First, call.Range.Last!.Value);
        var analysed = Math.Min(analysis.AnalysedPages, analysis.Pages.Count == 0 ? 0 : analysis.Pages.Max(p => p.Number));
        var byNumber = analysis.Pages.GroupBy(p => p.Number).ToDictionary(g => g.Key, g => g.First());
        var textLast = Math.Min(Math.Min(last, first + MaxPages - 1), analysed);
        var texts = textLast >= first ? await pixels.PageTextAsync(call.Path, call.State, first, textLast, ct) : null;
        if (textLast >= first && (texts is null || texts.Count < textLast - first + 1))
        {
            // Headers without their text would read as empty pages.
            return ShellResult.Error($"pdf: the text of {call.Path.Value} can't be read right now; pdf {SectionLookup.Quote(call.Path)} {first} --images=all shows a page as an image.");
        }

        var output = new Output();
        string? failed = null;
        var count = analysis.PageCount.ToString(CultureInfo.InvariantCulture);

        for (var n = first; n <= last; n++)
        {
            if (n - first == MaxPages) return Stop(output, call, n - 1, $"at most {MaxPages} pages per call", n, open);

            var page = byNumber.GetValueOrDefault(n);
            var text = page is null || n > textLast ? "" : texts![n - first].TrimEnd();
            var render = call.Show == Show.All || (call.Show == Show.Images && page is { Type: not PageType.Text });
            var head = page is null
                ? n <= analysis.AnalysedPages
                    ? $"--- Page {n} of {count}: not in the analysis"
                    : $"--- Page {n} of {count}: not analysed (only the first {analysis.AnalysedPages} pages are)"
                : $"--- Page {n} of {count}: {AttachmentText.TypeName(page.Type)}{(Found(page) is { } found ? $" ({found})" : "")}";

            // Whole pages only, but a first page longer than the limit is shown in part rather than not at all.
            var room = OutputLimit.MaxChars - Reserve - output.Chars - head.Length - 100;
            var cut = text.Length > room;
            if (cut && n > first) return Stop(output, call, n - 1, "the next page would pass the output limit", n, open);
            if (cut) text = Cut(text, n, room);

            Picture? picture = null;
            if (render && failed is null)
            {
                try
                {
                    picture = await pixels.PageAsync(call.Path, call.State, n, call.Step, null, ct);
                }
                catch (PixelsUnavailableException e)
                {
                    // The next page would most likely fail the same way; the text is still worth having.
                    failed = e.Message;
                }
            }

            var hint = picture is not null ? ""
                : render ? $" — not rendered: {failed}"
                : page is null ? " — add --images=all to see it"
                : page.Type != PageType.Text ? " — add --images to see it"
                : "";
            output.Add(text.Length == 0 ? $"{head}{hint} ---" : $"{head}{hint} ---\n{text}");
            if (text.Length > 0) pixels.Count(new PdfPageDelivered(PageDelivery.Text));

            if (picture is not null)
            {
                CountPicture(call, page);
                var what = page is null ? "" : $" ({AttachmentText.TypeName(page.Type)})";
                output.Append(PictureText.For($"{call.Path.Value} (version {call.State.Version}), page {n} of {count}{what}", PageSize(picture), call.Step, null, picture));
                output.Add(picture);
                if (output.Images == MaxImages && n < last) return Stop(output, call, n, $"at most {MaxImages} images per call", n + 1, open);
            }

            if (cut && n < last) return Stop(output, call, n, $"page {n} was cut at the output limit, and pdf can't show the rest of it", n + 1, open);
        }

        return output.Result();
    }

    /// <summary>The beginning of a page's text that fits <paramref name="room"/>, and a line saying it was cut.</summary>
    private static string Cut(string text, int page, int room)
    {
        var note = $"--- Page {page} is cut here: it has {text.Length.ToString("N0", CultureInfo.InvariantCulture)} characters, more than one call shows ---";
        var keep = Math.Max(1, room - note.Length - 1);
        var lastBreak = text.LastIndexOf('\n', keep);
        var at = lastBreak > 0 ? lastBreak : keep;
        if (char.IsHighSurrogate(text[at - 1])) at--;
        return $"{text[..at]}\n{note}";
    }

    private static string PageSize(Picture picture) => $"page {Point(picture.OriginalWidth)}×{Point(picture.OriginalHeight)} pt";

    /// <summary>"--- Stopped after page 9: at most 4 images per call. Continue with: pdf /a.pdf 10-20 --images ---".</summary>
    private static ShellResult Stop(Output output, Call call, int after, string why, int next, bool open, string? unsure = null)
    {
        var range = new PageRange(next, open ? null : call.Range.Last);
        var flag = call.Show switch { Show.Images => " --images", Show.All => " --images=all", _ => "" };
        var size = call.Step == SizeStep.Medium ? "" : $" --size {SizeSteps.Name(call.Step)}";
        var shown = range.Last == next ? $"{next}" : range.ToString();
        output.Add($"--- Stopped after page {after}: {why}. Continue with: pdf {SectionLookup.Quote(call.Path)} {shown}{flag}{size}{unsure} ---");
        return output.Result();
    }

    private async Task<ShellResult> CropAsync(Call call, PdfAnalysis analysis, CancellationToken ct)
    {
        var n = call.Range.First;
        var crop = call.Crop!;
        var page = analysis.Pages.FirstOrDefault(p => p.Number == n);
        if (page is not null && (crop.X + crop.Width > page.Width + 1e-6 || crop.Y + crop.Height > page.Height + 1e-6))
        {
            return Invalid($"pdf: the crop {crop} lies outside page {n} ({Point(page.Width)}×{Point(page.Height)} pt)");
        }

        Picture picture;
        try
        {
            picture = await pixels.PageAsync(call.Path, call.State, n, call.Step, crop, ct);
        }
        catch (PixelsUnavailableException e)
        {
            return ShellResult.Error($"pdf: {e.Message}");
        }

        CountPicture(call, page);
        var what = page is null ? "" : $" ({AttachmentText.TypeName(page.Type)})";
        var output = new Output();
        output.Add(PictureText.For($"{call.Path.Value} (version {call.State.Version}), page {n} of {analysis.PageCount}{what}", PageSize(picture), call.Step, crop, picture));
        output.Add(picture);
        return output.Result();
    }

    /// <summary>
    /// Pages rendered without an analysis, whose page count is unknown: up to 4, stopping at the first page that can't be
    /// rendered, the sidecar's "no such page" at the end of the PDF among them. Nothing rendered makes that the error.
    /// </summary>
    private async Task<ShellResult> WithoutAnalysisAsync(Call call, string status, CancellationToken ct)
    {
        var output = new Output();
        for (var n = call.Range.First; call.Range.Last is null || n <= call.Range.Last; n++)
        {
            if (output.Images == MaxImages)
            {
                // Without an analysis the page count is unknown: the page after may not exist.
                return Stop(output, call, n - 1, $"at most {MaxImages} images per call", n, call.Range.Last is null, " (if the PDF has more pages)");
            }

            Picture picture;
            try
            {
                picture = await pixels.PageAsync(call.Path, call.State, n, call.Step, call.Crop, ct);
            }
            catch (PixelsUnavailableException e)
            {
                if (output.Images == 0) return ShellResult.Error($"pdf: {e.Message}");
                output.Add($"--- Page {n} of ?: {status} ({e.Message}) ---");
                return output.Result();
            }

            CountPicture(call, null);
            var text = PictureText.For($"{call.Path.Value} (version {call.State.Version}), page {n}", PageSize(picture), call.Step, call.Crop, picture);
            if (call.Crop is null)
            {
                output.Add($"--- Page {n} of ?: {status} ---");
                output.Append(text);
            }
            else output.Add(text);

            output.Add(picture);
        }

        return output.Result();
    }
}
