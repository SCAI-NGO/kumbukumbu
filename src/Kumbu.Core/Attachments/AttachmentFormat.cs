// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Attachments;

public enum AttachmentKind
{
    Image,
    Pdf,
}

/// <summary>
/// A file format Kumbu² keeps besides notes. The extension names the format; the first bytes decide
/// whether the content really is that format.
/// </summary>
public sealed record AttachmentFormat(string Extension, string MediaType, AttachmentKind Kind)
{
    /// <summary>15 MB per file.</summary>
    public const long MaxBytes = 15 * 1024 * 1024;

    public const string UnsupportedMessage = "Kumbu² keeps notes, PDFs and images (PNG, JPEG, GIF, WebP) only";

    public static IReadOnlyList<AttachmentFormat> All { get; } =
    [
        new(".pdf", "application/pdf", AttachmentKind.Pdf),
        new(".png", "image/png", AttachmentKind.Image),
        new(".jpg", "image/jpeg", AttachmentKind.Image),
        new(".jpeg", "image/jpeg", AttachmentKind.Image),
        new(".gif", "image/gif", AttachmentKind.Image),
        new(".webp", "image/webp", AttachmentKind.Image),
    ];

    public static AttachmentFormat? ForPath(VaultPath path) => path.IsRoot ? null : ForName(path.Name);

    /// <summary>Whether a stored attachment is a PDF.</summary>
    public static bool IsPdf(AttachmentState state) => state.MediaType == "application/pdf";

    /// <summary>Whether a path is one a PDF is kept at: an attachment's extension names its format.</summary>
    public static bool IsPdf(VaultPath path) => ForPath(path)?.Kind == AttachmentKind.Pdf;

    /// <summary>The format a file name's extension names (case-insensitive); null for any other name.</summary>
    public static AttachmentFormat? ForName(string name)
    {
        foreach (var format in All)
        {
            if (name.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>
    /// Null when an attachment at <paramref name="source"/> may be named <paramref name="target"/>; otherwise why not.
    /// Renaming doesn't change the bytes, so the new extension must name their format (.jpg and .jpeg are one).
    /// </summary>
    public static string? RenameProblem(VaultPath source, VaultPath target)
    {
        if (ForPath(source) is not { } format || ForPath(target)?.MediaType == format.MediaType) return null;
        var extensions = string.Join(" or ", All.Where(f => f.MediaType == format.MediaType).Select(f => f.Extension));
        return $"{source.Name} is {format.Description}; its new name must end in {extensions} (a new name doesn't change what the file is)";
    }

    /// <summary>What a path holds, as clients tell files apart: "image", "pdf", or "note" for anything else.</summary>
    public static string KindOf(string path) => ForName(path)?.Kind switch
    {
        AttachmentKind.Image => "image",
        AttachmentKind.Pdf => "pdf",
        _ => "note",
    };

    /// <summary>Null when the file is accepted; otherwise the reason to show the user. <paramref name="head"/>
    /// is the start of the content (at least 12 bytes where the file has them).</summary>
    public static string? Problem(VaultPath path, ReadOnlySpan<byte> head)
    {
        var format = ForPath(path);
        if (format is null)
        {
            return UnsupportedMessage;
        }

        return format.Matches(head)
            ? null
            : $"{path.Name} isn't {format.Description} (its content doesn't match its name)";
    }

    private string Description => MediaType switch
    {
        "application/pdf" => "a PDF document",
        "image/png" => "a PNG image",
        "image/jpeg" => "a JPEG image",
        "image/gif" => "a GIF image",
        _ => "a WebP image",
    };

    private bool Matches(ReadOnlySpan<byte> head) => MediaType switch
    {
        "application/pdf" => head.StartsWith("%PDF-"u8),
        "image/png" => head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/jpeg" => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "image/gif" => head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8),
        _ => head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8),
    };
}
