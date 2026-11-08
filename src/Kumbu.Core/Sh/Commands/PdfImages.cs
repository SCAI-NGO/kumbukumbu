// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

/// <summary><c>pdfimages</c>: a PDF's embedded images, or a rendered region of a page, stored as image attachments; or the images only listed.</summary>
public sealed class PdfImages(INoteReader notes, IAttachmentPixels pixels, AttachmentWriter files, TimeProvider time) : ICommand
{
    private const string Forms = "pdfimages <pdf> [pages] <folder>/ | <pdf> <page> --crop x,y,w,h <file.png> | --list <pdf> [pages]";

    // A busy sidecar is its queue at work: asked again this often, this far apart, before its answer stands.
    private const int BusyRetries = 3;
    private static readonly TimeSpan BusyPause = TimeSpan.FromSeconds(1);

    private static readonly Option[] Spec = [new(null, "list"), new(null, "all"), new(null, "crop", TakesValue: true), new(null, "rotate", TakesValue: true)];

    private static readonly string[] RegionExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>The time a call lists and writes in: listing stops at half of it, writing at the first page boundary past it.</summary>
    internal TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The time after which writing stops even within a page.</summary>
    internal TimeSpan Ceiling { get; init; } = TimeSpan.FromSeconds(50);

    public string Name => "pdfimages";

    public string Usage => "pdfimages <pdf> [pages] <dir>/";

    public string Summary => "store a PDF's pictures as images, to place in notes";

    public string Help => """
        pdfimages <pdf> [pages] <folder>/
        pdfimages <pdf> <page> --crop x,y,w,h <file.png>
        pdfimages --list <pdf> [pages]
          Store the pictures of a PDF as image attachments, to place in notes. pages is N, N-M or N- (to the end),
          all pages if omitted.
          <folder>/         the images the pages paint, each as <pdf name>-p<page>-<n>.png, n counting the images
                            of its page from 1 in painting order; the folder is made if it is missing
          --crop x,y,w,h    one region of one page, rendered anew at 300 dpi (within 8 megapixels), into a .png,
                            .jpg or .jpeg file: a PNG unless the target is .jpg or .jpeg. x,y,w,h in points, as 'pdf
                            info' gives the regions. Only drawings need it (charts, diagrams, schematics): the images
                            of a page come whole and sharper from the folder form
          --rotate D        with --crop: turn the page clockwise by 90, 180 or 270 degrees first, as with pdf
                            (-90, -180 and -270 turn the other way); --crop then counts in the turned page, from
                            its top left (see 'help pdf' for turning a region from 'pdf info')
          --list            only list the images: page, number, pixel size, where on the page in points; writes nothing
          --all             also the small ones (under 1 % of their page: bullets, rules, icons), left out otherwise
          An image is stored at 8 megapixels at most (a larger one is scaled down, and the output says from what), as
          a PNG, or a JPEG where the PNG would pass 15 MB. An image painted on several pages is stored once, its line
          naming every page; duplicates are found within one call, so a later call stores a repeated image again.
          It stores at most 500 images in one call; for more, it stores nothing and names a page range that fits.
          pdfimages never overwrites: if a target exists, nothing is written. Images of more than 50 megapixels
          can't be read: the output names their pages, to look at with pdf --crop. A long PDF stops after about
          20 seconds, the last line naming the pages to go on with. A call stopped within a page says which of its
          files are stored. A writing command: it needs write access to the folder.

          pdfimages --list "/Work/Pump manual.pdf" 1-10
          pdfimages "/Work/Pump manual.pdf" 1-10 "/Work/Pump manual/images/"
          pdfimages "/Work/Pump manual.pdf" 12 --crop 60,120,410,260 "/Work/Pump manual/images/wiring diagram.png"
        """;

    public bool ReadsInput => false;

    /// <summary>At most this many images are stored in one call, counted after the small ones and duplicates are left out.</summary>
    private const int MaxImages = 500;

    private sealed record Call(VaultPath Path, AttachmentState State, PageRange Range, bool All, long Started);

    /// <summary>
    /// The pages listed, one request each, within half the budget: their images in page order, the first page not
    /// listed (null when all were), the last page of the range, cut to the page count once it is known, and the
    /// error that stopped the listing early, if one did.
    /// </summary>
    private sealed record Listing(IReadOnlyList<PdfPageImages> Pages, int? Next, int? Last, ShellResult? Error);

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        if (!Options.TryParse(Name, args, Spec, out var parsed, out var optionError)) return optionError with { Kind = ShellErrorKind.Invalid };
        var list = parsed.Has("list");
        var all = parsed.Has("all");
        var cropText = parsed.Value("crop");
        var operands = parsed.Operands;

        if (parsed.Value("rotate") is not null && cropText is null)
        {
            return Invalid("pdfimages: --rotate turns a region stored with --crop; embedded images are stored as the page paints them");
        }

        if (list && cropText is not null) return UsageError("--list writes nothing and --crop stores a region; give one of them");
        if (all && cropText is not null) return UsageError("--all is for embedded images; --crop stores the region as it is");
        if (operands.Count == 0 || (!list && cropText is null && operands.Count == 1)) return UsageError("missing operand");
        if (operands.Count > (list ? 2 : 3)) return UsageError("too many arguments");
        if (cropText is not null && operands.Count < 3) return UsageError("--crop takes one page and a file to store it in");

        string? target = null;
        if (!list)
        {
            target = operands[^1];
            if (cropText is null && !target.EndsWith('/')) return UsageError($"the folder to store into ends in '/': {target}/");
            if (cropText is not null && target.EndsWith('/')) return UsageError($"--crop stores one file, named .png, .jpg or .jpeg, not a folder: {target}");
            if (cropText is not null && !RegionExtensions.Contains(Path.GetExtension(target).ToLowerInvariant()))
            {
                return UsageError($"--crop stores one file, named .png, .jpg or .jpeg, not {target}");
            }
        }

        var range = PageRange.All;
        var pagesText = list ? operands.ElementAtOrDefault(1) : operands.Count == 3 ? operands[1] : null;
        if (pagesText is not null)
        {
            if (!PageRange.TryParse(pagesText, out range, out var rangeError)) return Invalid($"pdfimages: {rangeError}");
            if (cropText is not null && !range.IsSingle) return UsageError($"--crop takes one page, not {pagesText}");
        }

        Crop? crop = null;
        if (cropText is not null)
        {
            if (!Crop.TryParse(cropText, out var c, out var cropError)) return Invalid($"pdfimages: {cropError}");
            crop = c;
        }

        var rotate = 0;
        if (parsed.Value("rotate") is { } turn && !Rotation.TryParse(turn, out rotate))
        {
            return Invalid($"pdfimages: --rotate turns clockwise by 90, 180 or 270 degrees (or back with -90, -180 or -270), not '{turn}' (leave it out for no turn)");
        }

        VaultPath? into = null;
        if (target is not null)
        {
            if (!CommandArgs.TryParsePath(Name, target, out var targetPath, out var targetError)) return targetError! with { Kind = ShellErrorKind.Invalid };
            if (targetPath.IsRoot) return Invalid("pdfimages: /: images are stored in a folder of a vault, e.g. /Work/images/");
            into = targetPath;
        }

        var (opened, error) = await OpenAsync(operands[0], ct);
        if (opened is null) return error!;
        var call = new Call(opened.Value.Path, opened.Value.State, range, all, started);

        if (!list)
        {
            // Asked as the writer will ask it, before the sidecar is: a reader learns at once, not after the listing.
            var probe = crop is not null ? into! : into!.Child(Path.GetFileNameWithoutExtension(call.Path.Name) + ".png");
            if (files.Caller.Refuse(AttachmentWriter.Upload, probe) is { } refused)
            {
                return refused with { Output = $"pdfimages: {refused.Output}", Kind = ShellErrorKind.Refused };
            }
        }

        // One call per account at a time; a moment's wait, not a mistake in the call, so Unavailable.
        using var place = pixels.TryStartExtraction();
        if (place is null)
        {
            return ShellResult.Error("pdfimages: another pdfimages call of yours is still running; try again when it has finished") with { Kind = ShellErrorKind.Unavailable };
        }

        if (list) return await ListAsync(call, ct);
        if (crop is not null) return await StoreRegionAsync(call, crop, rotate, into!, ct);

        return await StoreAsync(call, into!, ct);
    }

    private static ShellResult Invalid(string message) => ShellResult.Error(message) with { Kind = ShellErrorKind.Invalid };

    private static ShellResult UsageError(string why) => Invalid($"pdfimages: {why}\nusage: {Forms}");

    private static ShellResult Failed(PixelsUnavailableException e) => ShellResult.Error($"pdfimages: {e.Message}") with { Kind = e.Kind };

    /// <summary>The PDF at <paramref name="pathText"/>, or the error for a path that isn't one.</summary>
    private async Task<((VaultPath Path, AttachmentState State)? Opened, ShellResult? Error)> OpenAsync(string pathText, CancellationToken ct)
    {
        if (!CommandArgs.TryParsePath(Name, pathText, out var path, out var pathError)) return (null, pathError! with { Kind = ShellErrorKind.Invalid });
        if (await StoreAttachments.GetAsync(notes, path, ct) is not { } attachment)
        {
            var read = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
            if (!read.IsError) return (null, Invalid($"pdfimages: {path.Value} is a note, not a PDF"));
            return (null, read with { Kind = read.Output.Contains(": No such file or directory", StringComparison.Ordinal) ? ShellErrorKind.NotFound : ShellErrorKind.Invalid });
        }

        if (!AttachmentText.IsPdf(attachment.Current)) return (null, Invalid($"pdfimages: {path.Value} is not a PDF"));
        return ((path, attachment.Current), null);
    }

    /// <summary>A call to the sidecar, asked again while it answers busy, up to <see cref="BusyRetries"/> times.</summary>
    private async Task<T> RetryingAsync<T>(Func<Task<T>> ask, CancellationToken ct)
    {
        for (var retries = 0; ; retries++)
        {
            try
            {
                return await ask();
            }
            catch (PixelsUnavailableException e) when (e.Problem == PixelsProblem.Busy && retries < BusyRetries)
            {
                await Task.Delay(BusyPause, time, ct);
            }
        }
    }

    private Task<PdfPageImages> ListPageAsync(Call call, int page, CancellationToken ct) =>
        RetryingAsync(() => pixels.PdfImagesAsync(call.Path, call.State, page, ct), ct);

    /// <summary>Lists the pages of the call one by one until the range or half the budget is spent.</summary>
    private async Task<Listing> ListPagesAsync(Call call, CancellationToken ct)
    {
        var pages = new List<PdfPageImages>();
        var last = call.Range.Last;
        for (var n = call.Range.First; last is null || n <= last; n++)
        {
            if (pages.Count > 0 && time.GetElapsedTime(call.Started) >= Budget / 2) return new(pages, n, last, null);
            PdfPageImages page;
            try
            {
                page = await ListPageAsync(call, n, ct);
            }
            catch (PixelsUnavailableException e)
            {
                // A first page past the end: the sidecar has no count to give, page 1 has.
                if (pages.Count == 0 && n > 1 && e.Problem == PixelsProblem.NoSuchPage && await PageCountAsync(call, ct) is { } count
                    && !call.Range.TryClamp(count, call.Path.Value, out _, out var clampError))
                {
                    return new(pages, n, last, Invalid($"pdfimages: {clampError}"));
                }

                return new(pages, n, last, Failed(e));
            }

            if (pages.Count == 0) last = Math.Min(last ?? page.PageCount, page.PageCount);
            pages.Add(page);
        }

        return new(pages, null, last, null);
    }

    private async Task<int?> PageCountAsync(Call call, CancellationToken ct)
    {
        try
        {
            return (await ListPageAsync(call, 1, ct)).PageCount;
        }
        catch (PixelsUnavailableException)
        {
            return null;
        }
    }

    private async Task<ShellResult> ListAsync(Call call, CancellationToken ct)
    {
        var listing = await ListPagesAsync(call, ct);
        var lines = new List<string>();
        if (listing.Pages.Count > 0)
        {
            var images = listing.Pages.SelectMany(p => p.Images.Select(i => (Page: p, Image: i))).ToList();
            var small = images.Count(i => i.Image.Small);
            var left = small == 0 ? "" : call.All ? $" ({small} small)" : $" ({small} small, --all lists them)";
            lines.Add($"{call.Path.Value}: {Count(images.Count, "image")} on {Pages(listing.Pages[0].Page, listing.Pages[^1].Page)}{left}");

            var seen = new Dictionary<string, (int Page, int N)>(StringComparer.Ordinal);
            foreach (var (page, image) in images.Where(i => call.All || !i.Image.Small))
            {
                var marks = new List<string>();
                if (image.Small) marks.Add("[small]");
                if (image.Failed || image.Hash is null) marks.Add("[could not be decoded]");
                else if (seen.TryGetValue(image.Hash, out var first)) marks.Add($"[same as p{first.Page} #{first.N}]");
                else seen[image.Hash] = (page.Page, image.N);

                var size = image.Width > 0 && image.Height > 0 ? $"  {image.Width}×{image.Height}" : "";
                lines.Add($"p{page.Page}  #{image.N}{size}  at {Box(image, page)}{(marks.Count > 0 ? "  " + string.Join(' ', marks) : "")}");
            }

            lines.AddRange(OverLines(listing.Pages));
        }

        if (listing.Error is { } error) return error with { Output = string.Join('\n', [.. lines, error.Output]) };
        if (listing.Next is { } next) lines.Add($"listed {Pages(listing.Pages[0].Page, next - 1)}; go on with {GoOn(next, listing.Last, call.Range.Last is null)}");
        return ShellResult.Ok(string.Join('\n', lines));
    }

    /// <summary>An image to store, under the name of the first page painting it; <see cref="Pages"/> are all the pages that do.</summary>
    private sealed record Planned(PdfPageImages Page, PdfImageInfo Image, List<int> Pages, string Name)
    {
        public bool Failed => Image.Failed || Image.Hash is null;

        public string Ref => $"p{Page.Page} #{Image.N}";
    }

    private async Task<ShellResult> StoreAsync(Call call, VaultPath into, CancellationToken ct)
    {
        var listing = await ListPagesAsync(call, ct);
        if (listing.Error is { } error) return error;

        var stem = Path.GetFileNameWithoutExtension(call.Path.Name);
        var plan = new List<Planned>();
        var byHash = new Dictionary<string, Planned>(StringComparer.Ordinal);
        foreach (var page in listing.Pages)
        {
            foreach (var image in page.Images)
            {
                if (image.Small && !call.All) continue;

                if (!image.Failed && image.Hash is { } hash && byHash.TryGetValue(hash, out var first))
                {
                    if (!first.Pages.Contains(page.Page)) first.Pages.Add(page.Page);
                    continue;
                }

                var planned = new Planned(page, image, [page.Page], $"{stem}-p{page.Page}-{image.N}");
                plan.Add(planned);
                if (!planned.Failed) byHash[image.Hash!] = planned;
            }
        }

        if (TooMany(plan, listing) is { } tooMany) return tooMany;
        if (await ExistingAsync(plan, into, ct) is { Count: > 0 } existing)
        {
            return ShellResult.Error($"pdfimages: these exist already: {string.Join(", ", existing)}; choose another folder, or remove them first") with { Kind = ShellErrorKind.Conflict };
        }

        var lines = new List<string>();
        var stored = 0;
        // What a stop reports of the pages it leaves out: the header's small ones and the over lines are of the pages before it.
        string HeaderBefore(int page) => Header(stored, into, call.All ? 0 : listing.Pages.Where(p => p.Page < page).Sum(p => p.Images.Count(i => i.Small)), call.All);
        var ofPage = new List<string>();
        ShellResult StoppedAt(ShellResult why, int i) => Stopped(why, HeaderBefore(plan[i].Page.Page), lines, ofPage, plan.Skip(i), listing, plan[i].Page.Page, call.Range.Last is null);
        int? goOn = listing.Next;
        string? partly = null;
        for (var i = 0; i < plan.Count; i++)
        {
            var item = plan[i];
            var boundary = i == 0 || item.Page.Page != plan[i - 1].Page.Page;
            if (boundary) ofPage.Clear();
            if (i > 0 && boundary && time.GetElapsedTime(call.Started) >= Budget)
            {
                goOn = item.Page.Page;
                break;
            }

            if (i > 0 && !boundary && time.GetElapsedTime(call.Started) >= Ceiling)
            {
                goOn = item.Page.Page;
                if (ofPage.Count > 0)
                {
                    var page = item.Page.Page;
                    partly = Partly(page, ofPage, ", as time ran out");
                }

                break;
            }

            if (item.Failed)
            {
                lines.Add($"{item.Ref} skipped: could not be decoded");
                continue;
            }

            Picture picture;
            try
            {
                picture = await RetryingAsync(() => pixels.PdfImageAsync(call.Path, call.State, item.Page.Page, item.Image.N, ct), ct);
            }
            catch (PixelsUnavailableException e) when (e.Problem == PixelsProblem.TooLarge)
            {
                lines.Add($"{item.Ref} skipped: over 15 MB even as JPEG");
                continue;
            }
            catch (PixelsUnavailableException e)
            {
                return StoppedAt(Failed(e), i);
            }

            var jpeg = picture.MediaType == "image/jpeg";
            var path = into.Child(item.Name + (jpeg ? ".jpg" : ".png"));
            WriteResult written;
            try
            {
                written = await files.WriteAsync(path.Value, picture.Data, (picture.Width, picture.Height), null, ct);
            }
            catch (QuotaExceededException e)
            {
                return StoppedAt(Full(e), i);
            }

            if (written.Result.IsError) return StoppedAt(WriterRefused(written), i);

            stored++;
            ofPage.Add(path.Value);
            lines.Add(Line(path, item, picture, jpeg));
        }

        var end = goOn ?? int.MaxValue;
        lines.InsertRange(0, [HeaderBefore(end)]);
        lines.AddRange(OverLines(listing.Pages.Where(p => p.Page < end)));
        if (partly is not null) lines.Add(partly);
        if (goOn is { } next) lines.Add($"stored {Count(stored, "image")}; go on with {GoOn(next, listing.Last, call.Range.Last is null)}");
        return ShellResult.Ok(string.Join('\n', lines));
    }

    /// <summary>One region of one page, rendered and stored at <paramref name="target"/>, which must not stand yet.</summary>
    private async Task<ShellResult> StoreRegionAsync(Call call, Crop crop, int rotate, VaultPath target, CancellationToken ct)
    {
        if (await StoreAttachments.GetAsync(notes, target, ct) is not null
            || (await notes.ListNotesUnderAsync(target, ct)).Count > 0
            || (await StoreAttachments.PathsUnderAsync(notes, target, ct))?.Count > 0)
        {
            return ShellResult.Error($"pdfimages: {target.Value} exists already; choose another name, or remove it first") with { Kind = ShellErrorKind.Conflict };
        }

        var page = call.Range.First;
        var jpeg = Path.GetExtension(target.Name).ToLowerInvariant() is ".jpg" or ".jpeg";
        Picture picture;
        try
        {
            picture = await RetryingAsync(() => pixels.PdfRegionAsync(call.Path, call.State, page, crop, rotate, jpeg, ct), ct);
        }
        catch (PixelsUnavailableException e)
        {
            return Failed(e);
        }

        WriteResult written;
        try
        {
            written = await files.WriteAsync(target.Value, picture.Data, (picture.Width, picture.Height), null, ct);
        }
        catch (QuotaExceededException e)
        {
            return Full(e);
        }

        if (written.Result.IsError) return WriterRefused(written);

        // The sidecar renders at 300 dpi, its sides floored, unless that passes 8 megapixels or 16,384 px on a side.
        var fullW = Math.Max(1, (int)Math.Floor(crop.Width * StoredDpi / 72 + 1e-6));
        var fullH = Math.Max(1, (int)Math.Floor(crop.Height * StoredDpi / 72 + 1e-6));
        var scaled = picture.Width < fullW || picture.Height < fullH ? $" (scaled down from {fullW}×{fullH})" : "";
        var turned = rotate == 0 ? "" : $" of the page turned {rotate}°";
        return ShellResult.Ok($"stored {target.Value}  p{page}  {picture.Width}×{picture.Height}{scaled}  {AttachmentText.Size(picture.Data.Length)}  at {crop}{turned}");
    }

    private const int StoredDpi = 300;

    /// <summary>A full vault, as ToolResult answers it: only the owner is pointed at df.</summary>
    private static ShellResult Full(QuotaExceededException e) =>
        ShellResult.Error(e.Owner ? e.Message + " See `df`." : e.Message) with { Kind = ShellErrorKind.Refused, Code = e.Owner ? Refusals.Quota : null };

    /// <summary>The writer's refusal (its text starts "upload:"), quoted with its own kind.</summary>
    private static ShellResult WriterRefused(WriteResult written) => written.Result with { Output = $"pdfimages: {written.Result.Output}" };

    private static string Header(int stored, VaultPath into, int small, bool all) =>
        $"stored {Count(stored, "image")} in {into.Value}/{(small > 0 && !all ? $" ({small} left out as smaller than 1 % of their page, --all keeps them)" : "")}";

    /// <summary>
    /// A stop part way at <paramref name="page"/>: what was stored, the over lines of the pages before it, which images
    /// were not stored, the pages not listed, and why. With nothing to tell but the reason, the reason alone. A page
    /// stored in part (<paramref name="ofPage"/>) is told how to store it, as at the ceiling, and the pages after it
    /// where to go on.
    /// </summary>
    private static ShellResult Stopped(ShellResult why, string header, List<string> lines, List<string> ofPage, IEnumerable<Planned> rest, Listing listing, int page, bool open)
    {
        if (lines.Count == 0) return why;
        var left = rest.Where(p => !p.Failed).Select(p => p.Ref).ToList();
        List<string> told = [header, .. lines, .. OverLines(listing.Pages.Where(p => p.Page < page))];
        if (ofPage.Count > 0) told.Add(Partly(page, ofPage, ""));
        if (left.Count > 0) told.Add($"not stored: {string.Join(", ", left)}");
        if (ofPage.Count > 0)
        {
            if (page < listing.Last) told.Add($"go on with {GoOn(page + 1, listing.Last, open)}");
        }
        else if (listing.Next is { } next)
        {
            told.Add($"not listed: {GoOn(next, listing.Last, open)}");
        }

        return why with { Output = string.Join('\n', [.. told, why.Output]) };
    }

    private static string Partly(int page, List<string> stored, string why) =>
        $"page {page} is stored only in part{why}: {string.Join(", ", stored)}; to store page {page}, run it again into another folder, or remove these files first";

    /// <summary>"/Work/images/manual-p4-1.png  p4, p9  2400×1600  1.9 MB  at 80,300,435,290", as spec §3.1 words a stored image.</summary>
    private static string Line(VaultPath path, Planned item, Picture picture, bool jpeg)
    {
        var fromW = (int)Math.Round(picture.OriginalWidth);
        var fromH = (int)Math.Round(picture.OriginalHeight);
        var scaled = fromW != picture.Width || fromH != picture.Height ? $" (scaled down from {fromW}×{fromH})" : "";
        var format = jpeg ? " (JPEG: PNG over 15 MB)" : "";
        var pages = string.Join(", ", item.Pages.Select(p => $"p{p}"));
        return $"{path.Value}  {pages}  {picture.Width}×{picture.Height}{scaled}  {AttachmentText.Size(picture.Data.Length)}{format}  at {Box(item.Image, item.Page)}";
    }

    /// <summary>The refusal of more than <see cref="MaxImages"/> images to store, naming the pages from the first that fit; null when they fit.</summary>
    private static ShellResult? TooMany(List<Planned> plan, Listing listing)
    {
        var toStore = plan.Where(p => !p.Failed).ToList();
        if (toStore.Count <= MaxImages) return null;

        var first = listing.Pages[0].Page;
        var listed = Pages(first, listing.Pages[^1].Page);
        var fits = toStore.Take(MaxImages + 1).Select(p => p.Page.Page).ToList();
        // The pages before the one the 501st image is on fit whole.
        var last = fits[^1] - 1;
        if (last < first)
        {
            return Invalid($"pdfimages: {toStore.Count} images on page {first}, more than {MaxImages} in one call; store that page's pictures in regions with --crop");
        }

        return Invalid($"pdfimages: {toStore.Count} images in {listed}, more than {MaxImages} in one call; give a page range, e.g. {(last == first ? $"{first}" : $"{first}-{last}")}");
    }

    /// <summary>The targets that stand already, as a note, a file or a folder, in either extension an image may get.</summary>
    private async Task<List<string>> ExistingAsync(List<Planned> plan, VaultPath into, CancellationToken ct)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var under = (await notes.ListNotesUnderAsync(into, ct)).Concat(await StoreAttachments.PathsUnderAsync(notes, into, ct) ?? []);
        foreach (var path in under)
        {
            // The path itself and every folder between it and the target folder.
            taken.Add(path.Value);
            for (var slash = path.Value.IndexOf('/', into.Value.Length + 1); slash > 0; slash = path.Value.IndexOf('/', slash + 1)) taken.Add(path.Value[..slash]);
        }

        return
        [
            .. plan.Where(p => !p.Failed)
                .SelectMany(p => new[] { ".png", ".jpg" }.Select(ext => into.Child(p.Name + ext).Value))
                .Where(taken.Contains),
        ];
    }

    /// <summary>"page 4: 1 image over 50 MP left out (look with pdf --crop)" for each page whose images pdf.js couldn't decode for their size.</summary>
    private static IEnumerable<string> OverLines(IEnumerable<PdfPageImages> pages) =>
        pages.Where(p => p.Over > 0).Select(p => $"page {p.Page}: {Count(p.Over, "image")} over 50 MP left out (look with pdf --crop)");

    private static string Count(int count, string noun) => count switch { 0 => $"no {noun}s", 1 => $"1 {noun}", _ => $"{count} {noun}s" };

    private static string Pages(int first, int last) => first == last ? $"page {first}" : $"pages {first}-{last}";

    /// <summary>The pages still to do: to the end where the call asked for that, else to its last page.</summary>
    private static string GoOn(int next, int? last, bool open) => open || last is null ? $"pages {next}-" : Pages(next, last.Value);

    /// <summary>An image's box as whole points, ready for <c>--crop</c>.</summary>
    private static string Box(PdfImageInfo image, PdfPageImages page) =>
        Crop.Enclosing(image.X, image.Y, image.W, image.H, page.Width, page.Height)?.ToString()
        ?? string.Join(',', new[] { image.X, image.Y, image.W, image.H }.Select(v => Math.Round(v, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)));
}
