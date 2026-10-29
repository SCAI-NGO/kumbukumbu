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
    public async Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, state, ct);
        if (ImageHeader.Read(bytes, out var problem) is null) throw new PixelsUnavailableException(TooLarge(path, state, problem), PixelsProblem.TooLarge);
        return await MeasuredAsync(KumbuMetrics.RenderKind.Image, () => sidecar.RenderImageAsync(bytes, SizeSteps.Of(step), crop, ct), e => Refused(e, "image rendering", path, null, crop));
    }

    public async Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, CancellationToken ct)
    {
        var bytes = await BytesAsync(path, state, ct);
        return await MeasuredAsync(KumbuMetrics.RenderKind.Page, () => sidecar.RenderPageAsync(bytes, page, SizeSteps.Of(step), crop, ct), e => Refused(e, "PDF rendering", path, page, crop));
    }

    /// <summary>A render timed and counted: its duration whatever came of it, a failure by reason, a picture by size.</summary>
    private static async Task<Picture> MeasuredAsync(KumbuMetrics.RenderKind kind, Func<Task<Picture>> render, Func<SidecarException, PixelsUnavailableException> refuse)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var picture = await render();
            KumbuMetrics.ImageDelivered(kind, picture.Data.Length, picture.Width, picture.Height);
            return picture;
        }
        catch (SidecarException e)
        {
            if (!KumbuMetrics.IsRequestMistake(e)) KumbuMetrics.RenderFailed(kind, KumbuMetrics.FailReasonOf(e));
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

    private static PixelsUnavailableException Refused(SidecarException e, string what, VaultPath path, int? page, Crop? crop) => new(e.Code switch
    {
        SidecarException.Unavailable or SidecarException.Busy => $"{what} is unavailable right now; try again later",
        SidecarException.Timeout => $"rendering {path} took too long; try a smaller --size or a --crop",
        SidecarException.Aborted => $"rendering {path} stopped part way (the renderer may have run out of memory); try a smaller --size or a --crop",
        SidecarException.Encrypted => PdfAnalysisRunner.PasswordProtected,
        "no_such_page" => $"{path} has no page {page}",
        "bad_crop" => $"the crop {crop} lies outside {(page is null ? "the image" : $"page {page}")}",
        _ => $"{path} couldn't be rendered",
    }, e.Code switch
    {
        SidecarException.Unavailable or SidecarException.Busy => PixelsProblem.Unavailable,
        "bad_crop" => PixelsProblem.BadCrop,
        _ => PixelsProblem.Failed,
    });
}
