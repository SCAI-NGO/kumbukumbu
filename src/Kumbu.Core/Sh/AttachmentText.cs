// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Sh;

/// <summary>How the shell words an attachment: kind, displayed size in pixels, and its size in bytes.</summary>
internal static class AttachmentText
{
    /// <summary>"512 B", "412 KB", "2.1 MB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kilobytes = Math.Round(bytes / 1024.0);
        return kilobytes < 1024
            ? $"{kilobytes.ToString(CultureInfo.InvariantCulture)} KB"
            : $"{(bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB";
    }

    public static string? Dimensions(AttachmentState state) => state is { Width: { } w, Height: { } h } ? $"{w}×{h}" : null;

    internal static bool IsPdf(AttachmentState state) => state.MediaType == "application/pdf";

    internal static string FormatName(AttachmentState state) => state.MediaType switch
    {
        "image/png" => "PNG",
        "image/jpeg" => "JPEG",
        "image/gif" => "GIF",
        "image/webp" => "WebP",
        _ => "PDF",
    };

    /// <summary>What <c>ls</c> and <c>history</c> add to a name: "image 1440×3120 412 KB", "PDF 2.1 MB".</summary>
    public static string Summary(AttachmentState state) =>
        string.Join(' ', new[] { IsPdf(state) ? "PDF" : "image", Dimensions(state), Size(state.Size) }.OfType<string>());

    /// <summary>The one line <c>cat</c> prints instead of bytes: "shot.png: image (PNG), 1440×3120, 412 KB".</summary>
    public static string Line(string name, AttachmentState state) =>
        $"{name}: " + string.Join(", ", new[] { IsPdf(state) ? "PDF" : $"image ({FormatName(state)})", Dimensions(state), Size(state.Size) }.OfType<string>());
}
