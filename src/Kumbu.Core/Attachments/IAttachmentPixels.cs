// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Attachments;

/// <summary>An image ready for the model, with the size it had before scaling (pixels for an image, points for a page).</summary>
public sealed record Picture(byte[] Data, string MediaType, int Width, int Height, double OriginalWidth, double OriginalHeight);

/// <summary>
/// Why pixels or an analysis couldn't be had: what <c>img</c> tells apart in its refusals, the sidecar away or busy
/// (<c>Unavailable</c>), and an analysis that couldn't run for that reason (<c>AnalysisUnavailable</c>), which
/// <c>pdf</c> tells apart from one that is merely pending. <c>Failed</c> is anything else (this file, this page).
/// </summary>
public enum PixelsProblem { Failed, Unavailable, TooLarge, BadCrop, AnalysisUnavailable }

/// <summary>Raised for an expected failure; the message is a sentence for the model.</summary>
public sealed class PixelsUnavailableException(string message, PixelsProblem problem = PixelsProblem.Failed) : Exception(message)
{
    public PixelsProblem Problem { get; } = problem;

    /// <summary>The shell's error kind for it, for the tool metrics; null for a failure of the file itself.</summary>
    public Sh.ShellErrorKind? Kind => Problem switch
    {
        PixelsProblem.Unavailable or PixelsProblem.AnalysisUnavailable => Sh.ShellErrorKind.Unavailable,
        PixelsProblem.TooLarge or PixelsProblem.BadCrop => Sh.ShellErrorKind.Invalid,
        _ => null,
    };
}

/// <summary>How a <c>pdf</c> call reads: its mode, for the usage metrics.</summary>
public enum PdfMode { Info, Text, Images, ImagesAll, Crop }

/// <summary>The type of a page that is shown; <c>Unknown</c> where there is no analysis (or no entry for the page).</summary>
public enum PageKind { Text, Mixed, Image, Unknown }

/// <summary>What a page is delivered as.</summary>
public enum PageDelivery { Text, Image }

/// <summary>Why <c>img</c> said no.</summary>
public enum ImgRefusal { Note, Pdf, NotFound, TooLarge, BadCrop, BadSize, Unavailable }

/// <summary>What was looked at, for the usage metrics. Only enums and flags in the records: nothing a person typed.</summary>
public abstract record AttachmentUse;

public sealed record PdfCalled(PdfMode Mode) : AttachmentUse;

public sealed record PdfPageDelivered(PageDelivery As) : AttachmentUse;

/// <summary>A page rendered as an image, in a call of this mode (<c>Images</c>, <c>ImagesAll</c> or <c>Crop</c>).</summary>
public sealed record PdfPageRendered(PageKind Type, PdfMode Mode) : AttachmentUse;

public sealed record ImgCalled(SizeStep Step, bool Cropped) : AttachmentUse;

public sealed record ImgRefused(ImgRefusal Reason) : AttachmentUse;

/// <summary>Pixels and analyses of attachments, which the commands <c>img</c> and <c>pdf</c> reach without knowing where they are made.</summary>
public interface IAttachmentPixels
{
    /// <summary>The image at a step, optionally cropped (original pixel coordinates, EXIF-oriented).</summary>
    Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, CancellationToken ct);

    /// <summary>A rendered page (1-based), optionally a region in displayed points.</summary>
    Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, CancellationToken ct);

    /// <summary>The stored analysis (without page text); <paramref name="runIfPending"/> runs a missing or outdated one now, within its time limit. Null if there is none and it can't be had.</summary>
    Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct);

    /// <summary>The text of pages <paramref name="first"/> to <paramref name="last"/> of a stored analysis (index 0 = page first); null without one.</summary>
    Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct);

    void Count(AttachmentUse use);
}
