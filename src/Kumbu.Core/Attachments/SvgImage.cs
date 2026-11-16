// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Xml;

namespace Kumbu.Core.Attachments;

/// <summary>
/// An SVG file, checked as XML rather than by magic bytes: well-formed, its root element <c>svg</c> in the SVG
/// namespace or none (hand-written files often leave it out). The DTD is ignored, never resolved: Inkscape and
/// Illustrator often write one, and with <see cref="DtdProcessing.Ignore"/> and no resolver nothing outside the file
/// is read (no XXE) and no entity is expanded (an entity used in the document is undeclared, so the file is refused).
/// </summary>
public static class SvgImage
{
    public const string Namespace = "http://www.w3.org/2000/svg";

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    private static XmlReader Reader(ReadOnlySpan<byte> content) => XmlReader.Create(new MemoryStream(content.ToArray(), writable: false), Settings);

    /// <summary>Null when <paramref name="content"/> is an SVG; otherwise why not, for the file <paramref name="name"/>.</summary>
    public static string? Problem(string name, ReadOnlySpan<byte> content)
    {
        try
        {
            using var reader = Reader(content);
            reader.MoveToContent();
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName != "svg") return $"{name} is not an SVG: the root element is {reader.LocalName}";
                if (reader.NamespaceURI is not ("" or Namespace)) return $"{name} is not an SVG: the root element svg is in the namespace {reader.NamespaceURI}";
            }

            while (reader.Read())
            {
            }

            return null;
        }
        catch (XmlException e)
        {
            return $"{name} is not an SVG: it isn't well-formed XML ({e.Message.TrimEnd('.')})";
        }
    }

    /// <summary>
    /// The size of an SVG <see cref="Problem"/> accepted: the root's <c>width</c> and <c>height</c> (in px, or unitless),
    /// else its <c>viewBox</c>, one side given scaling the viewBox to it; null when neither gives one. A huge size is
    /// kept as it is: an SVG is rendered at the size asked for, so its claimed canvas costs nothing.
    /// </summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> content)
    {
        string? width, height, viewBox;
        try
        {
            using var reader = Reader(content);
            if (reader.MoveToContent() != XmlNodeType.Element) return null;
            (width, height, viewBox) = (reader.GetAttribute("width"), reader.GetAttribute("height"), reader.GetAttribute("viewBox"));
        }
        catch (XmlException)
        {
            return null;
        }

        var (w, h) = (Length(width), Length(height));
        if (w is { } givenW && h is { } givenH) return Whole(givenW, givenH);
        if (Box(viewBox) is not var (boxW, boxH)) return null;
        return w is { } onlyW ? Whole(onlyW, onlyW * boxH / boxW)
            : h is { } onlyH ? Whole(onlyH * boxW / boxH, onlyH)
            : Whole(boxW, boxH);
    }

    /// <summary>A length in px or without a unit, above 0; null for any other.</summary>
    private static double? Length(string? value)
    {
        if (value is null) return null;
        var text = value.Trim();
        if (text.EndsWith("px", StringComparison.Ordinal)) text = text[..^2];
        return Positive(text);
    }

    private static (double, double)? Box(string? value)
    {
        var parts = value?.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 4 } && Positive(parts[2]) is { } w && Positive(parts[3]) is { } h ? (w, h) : null;
    }

    private static double? Positive(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v > 0 ? v : null;

    /// <summary>Rounded to whole pixels, at least 1; null for a side beyond what a stored size holds.</summary>
    private static (int, int)? Whole(double w, double h) =>
        w < int.MaxValue && h < int.MaxValue ? (Math.Max(1, (int)Math.Round(w, MidpointRounding.AwayFromZero)), Math.Max(1, (int)Math.Round(h, MidpointRounding.AwayFromZero))) : null;
}
