// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Server.Metrics;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Attachments;

/// <summary>
/// <see cref="IAttachmentPixels"/> of the server, per request: the blob is opened here, with the request's credential,
/// and its bytes go to the sidecar to be decoded, rendered or analysed; analyses are read from <see cref="PdfAnalyses"/>.
/// An expected failure (sidecar away, a refused page or crop, a PDF that can't be read) becomes a
/// <see cref="PixelsUnavailableException"/> with a sentence for the model.
/// </summary>
public sealed class AttachmentPixels(EncryptedNoteStore store, VaultCatalog catalog, IPdfSidecar sidecar, PdfAnalyses analyses, PdfAnalysisRunner runner) : IAttachmentPixels
{
    /// <summary>The header is checked before the bytes leave this process: a file claiming a huge canvas is never sent to be decoded.</summary>
    public async Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, int rotate, int? frame, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, state, ct);
        if (ImageHeader.Read(bytes, out var problem) is null) throw new PixelsUnavailableException(TooLarge(path, state, problem), PixelsProblem.TooLarge);
        return await MeasuredAsync(KumbuMetrics.RenderKind.Image, () => sidecar.RenderImageAsync(bytes, SizeSteps.Of(step), crop, rotate, frame, ct), e => Refused(e, "image rendering", path, null, crop, frame));
    }

    public async Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, int rotate, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, state, ct);
        return await MeasuredAsync(KumbuMetrics.RenderKind.Page, () => sidecar.RenderPageAsync(bytes, page, SizeSteps.Of(step), crop, rotate, ct), e => Refused(e, "PDF rendering", path, page, crop, null));
    }

    /// <summary>
    /// A render timed and counted: its duration whatever came of it, a failure by reason, a picture for the model by size.
    /// An extraction's picture is stored, not delivered, and its sidecar being busy is the queue at work, tried again.
    /// </summary>
    private static async Task<T> MeasuredAsync<T>(KumbuMetrics.RenderKind kind, Func<Task<T>> render, Func<SidecarException, PixelsUnavailableException> refuse)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await render();
            if (result is Picture picture && kind != KumbuMetrics.RenderKind.Extract) KumbuMetrics.ImageDelivered(kind, picture.Data.Length, picture.Width, picture.Height);
            return result;
        }
        catch (SidecarException e)
        {
            var busy = kind == KumbuMetrics.RenderKind.Extract && e.Code == SidecarException.Busy;
            if (!KumbuMetrics.IsRequestMistake(e) && !busy) KumbuMetrics.RenderFailed(kind, KumbuMetrics.FailReasonOf(e));
            throw refuse(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            KumbuMetrics.RenderFailed(kind, KumbuMetrics.FailReason.Failed);
            throw;
        }
        finally
        {
            KumbuMetrics.Rendered(kind, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>A recorded failure (password-protected, say) is a <see cref="PixelsUnavailableException"/> with its sentence.</summary>
    public async Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct)
    {
        if (await VaultAsync(path, state, ct) is not var (vault, blobId)) return null;
        var stored = await analyses.ReadAsync(vault, blobId, ct);
        if (stored is { Current: true }) return stored.Failure is { } failure ? throw new PixelsUnavailableException(failure.Message) : stored.Analysis;
        if (!runIfPending) return stored?.Analysis;
        // An outdated one is better than none while the new one isn't there.
        try
        {
            return await runner.RunNowAsync(vault, state, ct) ?? stored?.Analysis;
        }
        catch (PixelsUnavailableException e) when (e.Problem == PixelsProblem.AnalysisUnavailable && stored?.Analysis is { } outdated)
        {
            return outdated;
        }
    }

    public async Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct) =>
        await VaultAsync(path, state, ct) is var (vault, blobId) ? await analyses.TextAsync(vault, blobId, first, last, ct) : null;

    public async Task<IReadOnlyList<PageTextBoxes>> TextBoxesAsync(VaultPath path, AttachmentState state, IReadOnlyList<int> pages, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, state, ct);
        try
        {
            return await sidecar.TextBoxesAsync(bytes, pages, ct);
        }
        catch (SidecarException e)
        {
            throw Refused(e, "PDF rendering", path, null, null, null);
        }
    }

    public async Task<PdfPageImages> PdfImagesAsync(VaultPath path, AttachmentState state, int page, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, state, ct);
        return await MeasuredAsync(KumbuMetrics.RenderKind.Extract, () => sidecar.ListImagesAsync(bytes, page, ct), e => RefusedStoring(e, Extraction.List, path, page, 0, null, false));
    }

    public async Task<Picture> PdfImageAsync(VaultPath path, AttachmentState state, int page, int n, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, state, ct);
        return await MeasuredAsync(KumbuMetrics.RenderKind.Extract, () => sidecar.ExtractImageAsync(bytes, page, n, ct), e => RefusedStoring(e, Extraction.Image, path, page, n, null, false));
    }

    public async Task<Picture> PdfRegionAsync(VaultPath path, AttachmentState state, int page, Crop crop, int rotate, bool jpeg, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, state, ct);
        return await MeasuredAsync(KumbuMetrics.RenderKind.Extract, () => sidecar.RenderRegionAsync(bytes, page, crop, rotate, jpeg, ct), e => RefusedStoring(e, Extraction.Region, path, page, 0, crop, jpeg));
    }

    public void Count(AttachmentUse use) => KumbuMetrics.AttachmentUsed(use);

    private async Task<(UnlockedVault Vault, byte[] BlobId)?> VaultAsync(VaultPath path, AttachmentState state, CancellationToken ct) =>
        await store.VaultOfAsync(path, ct) is { } vault && EncryptedNoteStore.TryParseBlobId(state.BlobId, out var blobId) ? (vault, blobId) : null;

    private async Task<byte[]> BytesAsync(VaultPath path, AttachmentState state, CancellationToken ct) =>
        await VaultAsync(path, state, ct) is var (vault, blobId) && await catalog.ReadBlobAsync(vault, blobId, ct) is { } bytes
            ? bytes
            : throw new PixelsUnavailableException($"the content of {path} is no longer stored");

    private static string TooLarge(VaultPath path, AttachmentState state, string? problem)
    {
        if (state is { Width: int w, Height: int h })
        {
            if ((long)w * h > ImageHeader.MaxPixels)
            {
                var megapixels = ((long)w * h / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture);
                return $"{path} is too large to look at ({megapixels} megapixels; the limit is {ImageHeader.MaxPixels / 1_000_000})";
            }

            if (Math.Max(w, h) > ImageHeader.MaxSide)
            {
                return $"{path} is too large to look at ({w}×{h} px; the limit is {ImageHeader.MaxSide.ToString("N0", CultureInfo.InvariantCulture)} px on a side)";
            }
        }

        return $"{path} can't be looked at: {char.ToLowerInvariant(problem![0])}{problem[1..]}";
    }

    private enum Extraction { List, Image, Region }

    /// <summary>
    /// A refusal of a PDF's images or a region to store, in sentences of their own (<c>pdfimages</c> quotes them):
    /// a busy sidecar is <see cref="PixelsProblem.Busy"/>, to be tried again in a moment.
    /// </summary>
    private static PixelsUnavailableException RefusedStoring(SidecarException e, Extraction asked, VaultPath path, int page, int n, Crop? crop, bool jpeg)
    {
        var what = asked switch
        {
            Extraction.List => $"the images of page {page} of {path}",
            Extraction.Image => $"image {n} on page {page} of {path}",
            _ => $"the region of page {page} of {path}",
        };
        var reading = asked == Extraction.Region ? "rendering" : "reading";
        return new(e.Code switch
        {
            SidecarException.Busy => "PDF image extraction is busy; try again in a moment",
            SidecarException.Unavailable => "PDF image extraction is unavailable right now; try again later",
            SidecarException.Timeout => $"{reading} {what} took too long",
            SidecarException.Aborted => $"{reading} {what} stopped part way (the renderer may have run out of memory)",
            SidecarException.Encrypted => PdfAnalysisRunner.PasswordProtected,
            "no_such_page" => $"{path} has no page {page}",
            "no_such_image" => $"page {page} of {path} has no image {n}",
            "bad_crop" => $"the crop {crop} lies outside page {page}",
            "too_large" when asked == Extraction.Region && !jpeg => "the region is over 15 MB as PNG; name the target .jpg",
            "too_large" when asked == Extraction.Region => "the region is over 15 MB even as JPEG at quality 60; crop a smaller region",
            "too_large" when asked == Extraction.Image => $"{what} is over 15 MB even as JPEG at quality 60",
            _ => asked == Extraction.Region ? $"{what} couldn't be rendered" : $"{what} couldn't be read",
        }, e.Code switch
        {
            SidecarException.Busy => PixelsProblem.Busy,
            SidecarException.Unavailable => PixelsProblem.Unavailable,
            "no_such_image" => PixelsProblem.NoSuchImage,
            "no_such_page" => PixelsProblem.NoSuchPage,
            "bad_crop" => PixelsProblem.BadCrop,
            "too_large" when asked != Extraction.List => PixelsProblem.TooLarge,
            _ => PixelsProblem.Failed,
        });
    }

    private static PixelsUnavailableException Refused(SidecarException e, string what, VaultPath path, int? page, Crop? crop, int? frame) => new(e.Code switch
    {
        SidecarException.Unavailable or SidecarException.Busy => $"{what} is unavailable right now; try again later",
        SidecarException.Timeout when frame is not null => $"finding frame {frame} of {path} took too long",
        "too_large" when frame is not null => $"{path} is too large for --frame (at most 4 megapixels and 10,000 frames); img without --frame shows its first frame",
        SidecarException.Timeout => $"rendering {path} took too long; try a smaller --size or a --crop",
        SidecarException.Aborted => $"rendering {path} stopped part way (the renderer may have run out of memory); try a smaller --size or a --crop",
        SidecarException.Encrypted => PdfAnalysisRunner.PasswordProtected,
        "no_such_page" => $"{path} has no page {page}",
        "no_such_frame" => $"{path} has no frame {frame}; img without --frame says how many it has",
        "bad_crop" => $"the crop {crop} lies outside {(page is null ? "the image" : $"page {page}")}",
        _ => $"{path} couldn't be rendered",
    }, e.Code switch
    {
        SidecarException.Unavailable or SidecarException.Busy => PixelsProblem.Unavailable,
        "bad_crop" => PixelsProblem.BadCrop,
        "no_such_frame" => PixelsProblem.BadFrame,
        "too_large" when frame is not null => PixelsProblem.TooLarge,
        _ => PixelsProblem.Failed,
    });
}
