// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;

namespace Kumbu.Core.Attachments;

public enum AttachmentKind
{
    Image,
    Pdf,
    Folder,
    Text,
    File,
}

/// <summary>
/// A file format Kumbu² keeps besides notes. The extension names the format; the first bytes decide
/// whether the content really is that format, and for an SVG, which has no magic bytes, its XML (<see cref="SvgImage"/>).
/// Any other name is a <see cref="File"/>, kept as its bytes (spec 2026-11-18).
/// </summary>
public sealed record AttachmentFormat(string Extension, string MediaType, AttachmentKind Kind)
{
    /// <summary>15 MB per file.</summary>
    public const long MaxBytes = 15 * 1024 * 1024;

    /// <summary>Why a file of no other format isn't kept inside a skill's folder, which holds what a skill runs and reads.</summary>
    public const string SkillFolderMessage =
        "a skill's folder keeps text files (scripts, data, LICENSE and the like), PDFs and images (PNG, JPEG, GIF, WebP, SVG) only; keep other files outside it";

    /// <summary>An image of text: checked as XML, its size read from its root, shown only as a picture (spec 2026-11-17).
    /// Inside a skill's folder, <see cref="ForPath"/> keeps <c>.svg</c> a text file.</summary>
    public static AttachmentFormat Svg { get; } = new(".svg", "image/svg+xml", AttachmentKind.Image);

    // After Svg: static initialisers run in order.
    public static IReadOnlyList<AttachmentFormat> All { get; } =
    [
        new(".pdf", "application/pdf", AttachmentKind.Pdf),
        new(".png", "image/png", AttachmentKind.Image),
        new(".jpg", "image/jpeg", AttachmentKind.Image),
        new(".jpeg", "image/jpeg", AttachmentKind.Image),
        new(".gif", "image/gif", AttachmentKind.Image),
        new(".webp", "image/webp", AttachmentKind.Image),
        Svg,
    ];

    /// <summary>The folder marker: kept like an attachment, but no format users may upload, so not in <see cref="All"/>.</summary>
    public static AttachmentFormat Marker { get; } = new(FolderMarker.Name, FolderMarker.MediaType, AttachmentKind.Folder);

    public const string TextMediaType = "text/plain; charset=utf-8";

    /// <summary>The extensions of text files a skill's folder keeps (case-insensitive).</summary>
    public static IReadOnlyList<string> TextExtensions { get; } =
    [
        ".py", ".sh", ".js", ".mjs", ".ts", ".json", ".yaml", ".yml", ".toml", ".txt", ".csv", ".xml", ".html", ".css", ".svg",
    ];

    /// <summary>The extension-less names of text files a skill's folder keeps (case-insensitive).</summary>
    public static IReadOnlySet<string> TextNames { get; } = new HashSet<string>(
        ["LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS", "CHANGELOG", "README", "Makefile", "Dockerfile"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Text kept inside a skill's folder; no format users may upload elsewhere, so not in <see cref="All"/>.</summary>
    public static AttachmentFormat Text { get; } = new("", TextMediaType, AttachmentKind.Text);

    /// <summary>Any file that is no other format: kept as it is, its media type decided by its content at each write (<see cref="MediaTypeOf"/>), so not in <see cref="All"/>.</summary>
    public static AttachmentFormat File { get; } = new("", OctetStream, AttachmentKind.File);

    /// <summary>The media type of a <see cref="File"/> that isn't text.</summary>
    public const string OctetStream = "application/octet-stream";

    /// <summary>The media type a <see cref="File"/> is kept as: <see cref="TextMediaType"/> for UTF-8 without NUL bytes up to <see cref="MaxBytes"/> (a BOM is fine), else bytes.</summary>
    public static string MediaTypeOf(ReadOnlySpan<byte> content) =>
        content.Length <= MaxBytes && TextProblem("", content) is null ? TextMediaType : OctetStream;

    /// <summary>The format a path is kept as: <see cref="Text"/> for a text name strictly inside a skill's folder, else by <see cref="ForName"/>.</summary>
    public static AttachmentFormat? ForPath(VaultPath path)
    {
        if (path.IsRoot) return null;
        if (IsTextName(path.Name) && SkillPaths.IsInSkillFolder(path)) return Text;
        return ForName(path.Name);
    }

    private static bool IsTextName(string name) =>
        TextNames.Contains(name) || TextExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a stored attachment is a PDF.</summary>
    public static bool IsPdf(AttachmentState state) => state.MediaType == "application/pdf";

    /// <summary>Whether a path is one a PDF is kept at: an attachment's extension names its format.</summary>
    public static bool IsPdf(VaultPath path) => ForPath(path)?.Kind == AttachmentKind.Pdf;

    /// <summary>The format a file name's extension names (case-insensitive); null for a note's name (<c>.md</c>, as <see cref="VaultPath.IsNote"/>), <see cref="File"/> for any other.</summary>
    public static AttachmentFormat? ForName(string name)
    {
        if (name.EndsWith(".md", StringComparison.Ordinal)) return null;
        // Exactly the marker's name; a whole path (see KindOf) names it as its last segment.
        if (name == FolderMarker.Name || name.EndsWith('/' + FolderMarker.Name, StringComparison.Ordinal)) return Marker;
        foreach (var format in All)
        {
            if (name.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return File;
    }

    /// <summary>
    /// Null when an attachment at <paramref name="source"/> may be named <paramref name="target"/>; otherwise why not.
    /// Renaming doesn't change the bytes, so the new extension must name their format (.jpg and .jpeg are one);
    /// a file may take any name that names no format (spec 2026-11-18, decision 7).
    /// </summary>
    public static string? RenameProblem(VaultPath source, VaultPath target)
    {
        if (ForPath(source) is not { } format) return null;
        if (format == File)
        {
            return ForPath(target) switch
            {
                { } kept when kept == File => SkillPaths.IsInSkillFolder(target) ? $"{target.Name}: {SkillFolderMessage}" : null,
                null => $"{source.Name} is a file, not a note; its new name can't end in .md (a new name doesn't change what the file is)",
                { } kept when kept == Marker => $"{source.Name} is a file; only a folder's marker is named {FolderMarker.Name}",
                { } kept when kept == Text => $"{source.Name} is a file, and inside a skill's folder {target.Name} is a text file; upload it there instead",
                { } kept => $"{source.Name} is a file, not {kept.Description}; its new name can't end in {kept.Extension} (a new name doesn't change what the file is)",
            };
        }

        if (format == Marker) return ForPath(target) == Marker ? null : $"{FolderMarker.Name} keeps its name";
        if (format == Text)
        {
            return ForPath(target)?.Kind == AttachmentKind.Text
                ? null
                : $"{source.Name} is a text file, kept only inside a skill's folder (/Skills/<skill>/ or /<vault>/_skills/<skill>/)";
        }

        if (ForPath(target)?.MediaType == format.MediaType) return null;
        if (format == Svg && ForPath(target) == Text) return $"{source.Name} is an SVG image, and inside a skill's folder an .svg is a text file; upload it there instead";
        var extensions = string.Join(" or ", All.Where(f => f.MediaType == format.MediaType).Select(f => f.Extension));
        return $"{source.Name} is {format.Description}; its new name must end in {extensions} (a new name doesn't change what the file is)";
    }

    /// <summary>What a path holds, as clients tell files apart: "image", "pdf", "folder", "text" (only for a full path inside a skill's folder), "file", or "note" for a note's name.</summary>
    public static string KindOf(string path) => (VaultPath.TryParse(path, out var parsed) && ForPath(parsed) is { } kept ? kept : ForName(path))?.Kind switch
    {
        AttachmentKind.Image => "image",
        AttachmentKind.Pdf => "pdf",
        AttachmentKind.Folder => "folder",
        AttachmentKind.Text => "text",
        AttachmentKind.File => "file",
        _ => "note",
    };

    /// <summary>Null when the file is accepted; otherwise the reason to show the user. <paramref name="head"/>
    /// is the start of the content (at least 12 bytes where the file has them); for a text file and an SVG callers pass the whole content.</summary>
    public static string? Problem(VaultPath path, ReadOnlySpan<byte> head)
    {
        var format = ForPath(path);
        if (format is null) return $"{path.Name} is a note's name (it ends in .md); notes are written as text, not uploaded";

        // Any bytes, but not in a skill's folder; their media type is MediaTypeOf's, the size bound the writer's.
        if (format == File) return SkillPaths.IsInSkillFolder(path) ? $"{path.Name}: {SkillFolderMessage}" : null;

        if (format == Marker)
        {
            return head.IsEmpty ? null : $"A {FolderMarker.Name} keeps its folder and holds nothing; send it empty";
        }

        if (format == Text) return TextProblem(path.Name, head);
        if (format == Svg) return SvgImage.Problem(path.Name, head);

        return format.Matches(head)
            ? null
            : $"{path.Name} isn't {format.Description} (its content doesn't match its name)";
    }

    /// <summary>Null when the content is UTF-8 text without NUL bytes; otherwise why not.</summary>
    public static string? TextProblem(string name, ReadOnlySpan<byte> content)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(content);
        }
        catch (ArgumentException)
        {
            return $"{name} isn't UTF-8 text";
        }

        return content.Contains((byte)0) ? $"{name} contains a NUL byte; a skill's folder keeps text only" : null;
    }

    private string Description => MediaType switch
    {
        "application/pdf" => "a PDF document",
        "image/png" => "a PNG image",
        "image/jpeg" => "a JPEG image",
        "image/gif" => "a GIF image",
        "image/svg+xml" => "an SVG image",
        "image/webp" => "a WebP image",
        TextMediaType => "a text file",
        _ => throw new UnreachableException(MediaType),
    };

    private bool Matches(ReadOnlySpan<byte> head) => MediaType switch
    {
        "application/pdf" => head.StartsWith("%PDF-"u8),
        "image/png" => head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/jpeg" => head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
        "image/gif" => head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8),
        "image/webp" => head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => throw new UnreachableException(MediaType),
    };
}
