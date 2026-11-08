// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Attachments;

/// <summary>
/// An image ready for the model, with the size it had before scaling (pixels for an image, points for a page).
/// <paramref name="Transparent"/>: it had see-through areas, which are delivered on white.
/// <paramref name="Frames"/>: the number of frames of a GIF whose count the sidecar knows.
/// </summary>
public sealed record Picture(byte[] Data, string MediaType, int Width, int Height, double OriginalWidth, double OriginalHeight, bool Transparent = false, int? Frames = null);

/// <summary>
/// Why pixels or an analysis couldn't be had: what <c>img</c> tells apart in its refusals, the sidecar away or busy
/// (<c>Unavailable</c>), and an analysis that couldn't run for that reason (<c>AnalysisUnavailable</c>), which
/// <c>pdf</c> tells apart from one that is merely pending. A PDF's images and regions to store tell a busy sidecar
/// apart (<c>Busy</c>, worth a retry in a moment) and an image number past the page's (<c>NoSuchImage</c>).
/// <c>Failed</c> is anything else (this file, this page).
/// </summary>
public enum PixelsProblem { Failed, Unavailable, TooLarge, BadCrop, BadFrame, AnalysisUnavailable, Busy, NoSuchImage }

/// <summary>Raised for an expected failure; the message is a sentence for the model.</summary>
public sealed class PixelsUnavailableException(string message, PixelsProblem problem = PixelsProblem.Failed) : Exception(message)
{
    public PixelsProblem Problem { get; } = problem;

    /// <summary>The shell's error kind for it, for the tool metrics; null for a failure of the file itself.</summary>
    public Sh.ShellErrorKind? Kind => Problem switch
    {
        PixelsProblem.Unavailable or PixelsProblem.AnalysisUnavailable or PixelsProblem.Busy => Sh.ShellErrorKind.Unavailable,
        PixelsProblem.TooLarge or PixelsProblem.BadCrop or PixelsProblem.BadFrame or PixelsProblem.NoSuchImage => Sh.ShellErrorKind.Invalid,
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
public enum ImgRefusal { Note, Pdf, NotFound, TooLarge, BadCrop, BadSize, BadRotate, BadFrame, Unavailable }

/// <summary>The command that turned a picture with <c>--rotate</c>.</summary>
public enum TurnCommand { Img, Pdf }

/// <summary>
/// What <c>rg --regions</c> could give a PDF with matches: the sidecar answered (<c>Delivered</c>), on
/// fewer or none for its limits (<c>Limited</c>), or none as the sidecar failed or the time was spent (<c>Unavailable</c>).
/// </summary>
public enum RegionOutcome { Delivered, Limited, Unavailable }

/// <summary>What was looked at, for the usage metrics. Only enums and flags in the records: nothing a person typed.</summary>
public abstract record AttachmentUse;

public sealed record PdfCalled(PdfMode Mode) : AttachmentUse;

public sealed record PdfPageDelivered(PageDelivery As) : AttachmentUse;

/// <summary>A page rendered as an image, in a call of this mode (<c>Images</c>, <c>ImagesAll</c> or <c>Crop</c>).</summary>
public sealed record PdfPageRendered(PageKind Type, PdfMode Mode) : AttachmentUse;

public sealed record ImgCalled(SizeStep Step, bool Cropped) : AttachmentUse;

public sealed record ImgRefused(ImgRefusal Reason) : AttachmentUse;

/// <summary>A picture delivered turned by <c>--rotate</c>.</summary>
public sealed record PictureTurned(TurnCommand Command) : AttachmentUse;

/// <summary>A frame of a GIF delivered on request by <c>--frame</c>.</summary>
public sealed record ImgFrameShown : AttachmentUse;

/// <summary>A PDF with matches in an <c>rg --regions</c> search, and what came of its regions.</summary>
public sealed record RegionsAsked(RegionOutcome Outcome) : AttachmentUse;

/// <summary>A run of text on a page: <c>[At, End)</c> in the page's text, and the box it prints in (points, as displayed, origin top left).</summary>
public sealed record TextBox(int At, int End, double X, double Y, double Width, double Height);

/// <summary>A page's text with the boxes of its items, ascending by offset.</summary>
public sealed record PageTextBoxes(int Number, double Width, double Height, string Text, IReadOnlyList<TextBox> Items);

/// <summary>
/// An image a PDF page paints: <paramref name="N"/> from 1 in painting order, its decoded pixel size and its box in
/// displayed points (clipped to the page), both as painted (after a quarter turn); <paramref name="Small"/> below 1 %
/// of the page. <paramref name="Hash"/> (SHA-256, lowercase hex) is the same for the same pixels; null with
/// <paramref name="Failed"/>, an image that couldn't be decoded.
/// </summary>
public sealed record PdfImageInfo(int N, int Width, int Height, double X, double Y, double W, double H, bool Small, string? Hash, bool Failed);

/// <summary>The images of one page, in a PDF of <paramref name="PageCount"/> pages; <paramref name="Over"/> images were left out as too large to decode.</summary>
public sealed record PdfPageImages(int Page, int PageCount, double Width, double Height, int Over, IReadOnlyList<PdfImageInfo> Images);

/// <summary>Pixels and analyses of attachments, which the commands <c>img</c> and <c>pdf</c> reach without knowing where they are made.</summary>
public interface IAttachmentPixels
{
    /// <summary>The image at a step, optionally cropped (original pixel coordinates, EXIF-oriented; in the turned frame when <paramref name="rotate"/> is not 0); <paramref name="frame"/> (1-based) picks a frame of a GIF.</summary>
    Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, int rotate, int? frame, CancellationToken ct);

    /// <summary>A rendered page (1-based), optionally a region in displayed points.</summary>
    Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, int rotate, CancellationToken ct);

    /// <summary>The stored analysis (without page text); <paramref name="runIfPending"/> runs a missing or outdated one now, within its time limit. Null if there is none and it can't be had.</summary>
    Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct);

    /// <summary>The text of pages <paramref name="first"/> to <paramref name="last"/> of a stored analysis (index 0 = page first); null without one.</summary>
    Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct);

    /// <summary>The text of the given pages (ascending, distinct) with the boxes of its items, read from the file itself; nothing is stored. Throws <see cref="PixelsUnavailableException"/> if the sidecar can't.</summary>
    Task<IReadOnlyList<PageTextBoxes>> TextBoxesAsync(VaultPath path, AttachmentState state, IReadOnlyList<int> pages, CancellationToken ct);

    /// <summary>The images page <paramref name="page"/> (1-based) of a PDF paints, read from the file itself.</summary>
    Task<PdfPageImages> PdfImagesAsync(VaultPath path, AttachmentState state, int page, CancellationToken ct);

    /// <summary>
    /// Image <paramref name="n"/> of a page to store: turned as painted, within 8 megapixels, a PNG, or a JPEG where the
    /// PNG would pass 15 MB. Its original size is its pixels before scaling (turned).
    /// </summary>
    Task<Picture> PdfImageAsync(VaultPath path, AttachmentState state, int page, int n, CancellationToken ct);

    /// <summary>A region of a page in displayed points to store, at 300 dpi within 8 megapixels, a PNG or (<paramref name="jpeg"/>) a JPEG; its original size is the page's in points.</summary>
    Task<Picture> PdfRegionAsync(VaultPath path, AttachmentState state, int page, Crop crop, int rotate, bool jpeg, CancellationToken ct);

    void Count(AttachmentUse use);
}
