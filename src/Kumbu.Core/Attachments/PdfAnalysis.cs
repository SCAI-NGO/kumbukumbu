// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Attachments;

public enum PageType { Text, Mixed, Image }

/// <summary>A box in points of the page as displayed, from the top left.</summary>
public sealed record PdfBox(double X, double Y, double Width, double Height);

/// <summary>A figure on a page, in points of the page as displayed, from the top left; <see cref="Labelled"/> the larger box
/// that also holds its labels and legend, found by position, if it has one.</summary>
public sealed record PdfRegion(string Kind, double X, double Y, double Width, double Height, PdfBox? Labelled = null);

public sealed record PdfPage(
    int Number, double Width, double Height, int Rotation, PageType Type, int Chars,
    double TextCoverage, double ImageCoverage, double VectorCoverage, int Images, IReadOnlyList<PdfRegion> Regions);

/// <summary>What the analysis found in a PDF, without the page text.</summary>
public sealed record PdfAnalysis(string Analyser, int PageCount, int AnalysedPages, string? Title, string? Author, IReadOnlyList<PdfPage> Pages);
