// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Kumbu.Server.Dav;

/// <summary>What a <c>PROPFIND</c> asks for (RFC 4918 §9.1): every live property, their names only, or the ones named.</summary>
public enum DavPropfindKind
{
    AllProp,
    PropName,
    Prop,
}

/// <param name="Names">For <see cref="DavPropfindKind.Prop"/>, the properties asked for, in the body's order.</param>
public sealed record DavPropfind(DavPropfindKind Kind, IReadOnlyList<XName> Names)
{
    public static DavPropfind AllProp { get; } = new(DavPropfindKind.AllProp, []);
}

/// <summary>One <c>response</c> of a multistatus: a resource's URL and its properties grouped by status.</summary>
public sealed record DavResponse(string Href, IReadOnlyList<(int Status, IReadOnlyList<XElement> Properties)> PropStats);

/// <summary>
/// The XML of WebDAV request and response bodies (WebDAV design §5). <see cref="ReadBodyAsync"/> is the one way every
/// handler reads a body (<c>PROPFIND</c>, <c>PROPPATCH</c>, <c>LOCK</c>): at most <see cref="MaxBodyBytes"/>, counted
/// as they arrive, so a chunked body without a length is held to it too; no DTD (which would allow entity expansion
/// and, with a resolver, reading files or URLs) and no resolver.
/// </summary>
public static class DavXml
{
    public static readonly XNamespace Dav = "DAV:";

    public const int MaxBodyBytes = 64 * 1024;

    public const string ContentType = "application/xml; charset=utf-8";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true,
        Async = false,
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The request's XML body, or null if it has none. A body over <see cref="MaxBodyBytes"/> is a 413, one with
    /// a DTD or that isn't well-formed XML a 400 (<see cref="DavException"/>).</summary>
    public static async Task<XDocument?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes) throw TooLarge();
        if (request.ContentLength == 0) return null;

        // One more byte than may come, to tell "exactly the limit" from "more".
        var buffer = ArrayPool<byte>.Shared.Rent(MaxBodyBytes + 1);
        try
        {
            var length = 0;
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(length, MaxBodyBytes + 1 - length), ct)) > 0)
            {
                length += read;
                if (length > MaxBodyBytes) throw TooLarge();
            }

            if (length == 0) return null;
            using var stream = new MemoryStream(buffer, 0, length, writable: false);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            throw new DavException(StatusCodes.Status400BadRequest, "The request body isn't well-formed XML without a DTD.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static DavException TooLarge() =>
        new(StatusCodes.Status413PayloadTooLarge, $"An XML request body can have at most {MaxBodyBytes / 1024} KB.");

    /// <summary>A <c>PROPFIND</c> body (RFC 4918 §14.20): none means <c>allprop</c>. <c>allprop</c>'s <c>include</c> adds
    /// nothing here, since every property is a live one and <c>allprop</c> gives them all.</summary>
    public static DavPropfind ParsePropfind(XDocument? body)
    {
        if (body?.Root is not { } root) return DavPropfind.AllProp;
        if (root.Name != Dav + "propfind") throw BadBody("A PROPFIND body is a DAV:propfind.");
        var choice = root.Elements().FirstOrDefault(e => e.Name.Namespace == Dav && e.Name.LocalName is "allprop" or "propname" or "prop")
            ?? throw BadBody("A DAV:propfind holds DAV:allprop, DAV:propname or DAV:prop.");
        return choice.Name.LocalName switch
        {
            "allprop" => DavPropfind.AllProp,
            "propname" => new DavPropfind(DavPropfindKind.PropName, []),
            _ => new DavPropfind(DavPropfindKind.Prop, [.. choice.Elements().Select(e => e.Name).Distinct()]),
        };
    }

    private static DavException BadBody(string message) => new(StatusCodes.Status400BadRequest, message);

    /// <summary>The propstats of a resource with <paramref name="properties"/> (its live properties, by name, each a
    /// function giving the element with its value) for what <paramref name="propfind"/> asks: a property it doesn't
    /// have is reported 404, as RFC 4918 §9.1 wants.</summary>
    public static DavResponse Response(string href, IReadOnlyDictionary<XName, Func<XElement>> properties, DavPropfind propfind)
    {
        switch (propfind.Kind)
        {
            case DavPropfindKind.AllProp:
                return new(href, [(StatusCodes.Status200OK, [.. properties.Values.Select(p => p())])]);
            case DavPropfindKind.PropName:
                return new(href, [(StatusCodes.Status200OK, [.. properties.Keys.Select(n => new XElement(n))])]);
            default:
                var found = propfind.Names.Where(properties.ContainsKey).Select(n => properties[n]()).ToList();
                var missing = propfind.Names.Where(n => !properties.ContainsKey(n)).Select(n => new XElement(n)).ToList();
                var stats = new List<(int, IReadOnlyList<XElement>)>();
                if (found.Count > 0) stats.Add((StatusCodes.Status200OK, found));
                if (missing.Count > 0) stats.Add((StatusCodes.Status404NotFound, missing));
                return new(href, stats);
        }
    }

    /// <summary>A 207 with a <c>DAV:multistatus</c> of <paramref name="responses"/>.</summary>
    public static Task WriteMultistatusAsync(HttpResponse response, IEnumerable<DavResponse> responses, CancellationToken ct)
    {
        var multistatus = new XElement(
            Dav + "multistatus",
            new XAttribute(XNamespace.Xmlns + "D", Dav.NamespaceName),
            responses.Select(r => new XElement(
                Dav + "response",
                new XElement(Dav + "href", r.Href),
                r.PropStats.Select(s => new XElement(
                    Dav + "propstat",
                    new XElement(Dav + "prop", s.Properties),
                    new XElement(Dav + "status", Status(s.Status)))))));
        return WriteAsync(response, StatusCodes.Status207MultiStatus, multistatus, ct);
    }

    /// <summary>A <c>DAV:error</c> with one precondition element (RFC 4918 §16), such as <c>propfind-finite-depth</c>.</summary>
    public static XElement Error(string condition) =>
        new(Dav + "error", new XAttribute(XNamespace.Xmlns + "D", Dav.NamespaceName), new XElement(Dav + condition));

    /// <summary><paramref name="root"/> as the whole body, UTF-8 without a BOM, with its length.</summary>
    public static async Task WriteAsync(HttpResponse response, int status, XElement root, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = Utf8, Async = true }))
        {
            await new XDocument(new XDeclaration("1.0", "utf-8", null), root).SaveAsync(writer, ct);
        }

        response.StatusCode = status;
        response.ContentType = ContentType;
        response.ContentLength = buffer.Length;
        await response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct);
    }

    /// <summary>A propstat's status line (RFC 4918 §14.28).</summary>
    private static string Status(int status) =>
        $"HTTP/1.1 {status} {Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status)}";
}
