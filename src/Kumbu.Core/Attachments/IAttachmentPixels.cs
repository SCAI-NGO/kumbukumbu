// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Attachments;

/// <summary>An image ready for the model, with the size it had before scaling (pixels for an image, points for a page).</summary>
public sealed record Picture(byte[] Data, string MediaType, int Width, int Height, double OriginalWidth, double OriginalHeight);

/// <summary>Raised for an expected failure; the message is a sentence for the model.</summary>
public sealed class PixelsUnavailableException(string message) : Exception(message);

/// <summary>What was looked at, for the usage metrics; filled in where it is counted.</summary>
public abstract record AttachmentUse;

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
