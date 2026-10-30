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

    internal static bool IsPdf(AttachmentState state) => AttachmentFormat.IsPdf(state);

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

    /// <summary>
    /// <c>cat</c>'s line for a PDF: "paper.pdf: PDF, 12 pages (1-3 text, 4 mixed, 5-12 text), 2.1 MB — read it with pdf (help pdf)";
    /// without an analysis, what is known and why the rest isn't.
    /// </summary>
    public static string PdfLine(string name, AttachmentState state, PdfAnalysis? analysis, string? failure)
    {
        if (analysis is not null) return $"{name}: PDF, {Pages(analysis.PageCount)} ({Runs(analysis, CatRuns)}), {Size(state.Size)} — read it with pdf (help pdf)";
        return $"{name}: PDF, {Size(state.Size)} — {failure ?? "not analysed yet; pdf reads it (help pdf)"}";
    }

    /// <summary>The runs <c>cat</c> shows before it says how many more there are; <c>pdf info</c> shows more.</summary>
    private const int CatRuns = 8;

    public static string Pages(int count) => count == 1 ? "1 page" : $"{count} pages";

    public static string TypeName(PageType type) => type switch
    {
        PageType.Mixed => "mixed",
        PageType.Image => "image",
        _ => "text",
    };

    /// <summary>The page types in runs, "1-3 text, 4 mixed, 5-12 text", pages past the analysed ones as "not analysed".</summary>
    public static string Runs(PdfAnalysis analysis, int maxRuns)
    {
        var runs = new List<string>();
        var pages = analysis.Pages.OrderBy(p => p.Number).ToList();
        for (var i = 0; i < pages.Count;)
        {
            var j = i;
            while (j + 1 < pages.Count && pages[j + 1].Type == pages[i].Type && pages[j + 1].Number == pages[j].Number + 1) j++;
            runs.Add($"{Span(pages[i].Number, pages[j].Number)} {TypeName(pages[i].Type)}");
            i = j + 1;
        }

        var analysed = pages.Count == 0 ? 0 : pages[^1].Number;
        if (analysed < analysis.PageCount) runs.Add($"{Span(analysed + 1, analysis.PageCount)} not analysed");
        return runs.Count <= maxRuns ? string.Join(", ", runs) : $"{string.Join(", ", runs.Take(maxRuns))}, …and {runs.Count - maxRuns} more runs";
    }

    private static string Span(int first, int last) => first == last ? $"{first}" : $"{first}-{last}";

    /// <summary>The one line <c>cat</c> prints instead of bytes: "shot.png: image (PNG), 1440×3120, 412 KB".</summary>
    public static string Line(string name, AttachmentState state) =>
        $"{name}: " + string.Join(", ", new[] { IsPdf(state) ? "PDF" : $"image ({FormatName(state)})", Dimensions(state), Size(state.Size) }.OfType<string>());
}
