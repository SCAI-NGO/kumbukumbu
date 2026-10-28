// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Attachments;

/// <summary>The analyses of PDFs, sealed per blob.</summary>
public sealed class PdfAnalyses
{
    /// <summary>PDFs with more pages are analysed up to this one, and say so.</summary>
    public const int MaxPages = 2000;

    /// <summary>The sidecar's <c>ANALYSER</c> (<c>pdf/src/analyse.ts</c>): an analysis by another counts as outdated.</summary>
    public const string Analyser = "pdfjs-dist@6.4.299+rules.1";
}
