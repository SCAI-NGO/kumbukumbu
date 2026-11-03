// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Attachments;

public enum PageType { Text, Mixed, Image }

/// <summary>A figure on a page, in points of the page as displayed, from the top left.</summary>
public sealed record PdfRegion(string Kind, double X, double Y, double Width, double Height);

public sealed record PdfPage(
    int Number, double Width, double Height, int Rotation, PageType Type, int Chars,
    double TextCoverage, double ImageCoverage, double VectorCoverage, int Images, IReadOnlyList<PdfRegion> Regions);

/// <summary>What the analysis found in a PDF, without the page text.</summary>
public sealed record PdfAnalysis(string Analyser, int PageCount, int AnalysedPages, string? Title, string? Author, IReadOnlyList<PdfPage> Pages);
