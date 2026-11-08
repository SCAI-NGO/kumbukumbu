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

    /// <summary>The distinct properties a <c>PROPFIND</c>'s <c>DAV:prop</c> may name. Clients ask for about a dozen
    /// (Windows, Finder and Office alike); every resource of a <c>Depth: 1</c> answer reports each name it lacks, so
    /// without a cap the 64 KB body's eleven thousand names times a folder's children would be an answer of gigabytes.
    /// More is a 400: a client that asks for so many is no client of ours.</summary>
    public const int MaxPropertyNames = 64;

    public const string ContentType = "application/xml; charset=utf-8";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true,
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
            _ when !choice.HasElements => throw BadBody("A DAV:prop names the properties asked for, at least one."),
            _ => new DavPropfind(DavPropfindKind.Prop, Named(choice)),
        };
    }

    /// <summary>The distinct names of a <c>DAV:prop</c>, at most <see cref="MaxPropertyNames"/> of them.</summary>
    private static List<XName> Named(XElement prop)
    {
        var names = new List<XName>();
        var seen = new HashSet<XName>();
        foreach (var element in prop.Elements())
        {
            if (!seen.Add(element.Name)) continue;
            if (names.Count == MaxPropertyNames) throw BadBody($"A DAV:prop may name at most {MaxPropertyNames} properties.");
            names.Add(element.Name);
        }

        return names;
    }

    /// <summary>The properties a <c>PROPPATCH</c> body (RFC 4918 §14.19) sets or removes, in its order, each once. Nothing
    /// is stored: the names are all the answer needs (WebDAV design §3.4).</summary>
    public static IReadOnlyList<XName> ParsePropertyUpdate(XDocument? body)
    {
        if (body?.Root is not { } root || root.Name != Dav + "propertyupdate") throw BadBody("A PROPPATCH body is a DAV:propertyupdate.");
        var names = root.Elements()
            .Where(e => e.Name == Dav + "set" || e.Name == Dav + "remove")
            .SelectMany(e => e.Elements(Dav + "prop"))
            .SelectMany(p => p.Elements())
            .Select(e => e.Name)
            .Distinct()
            .ToList();
        return names.Count > 0 ? names : throw BadBody("A DAV:propertyupdate sets or removes at least one property.");
    }

    /// <summary>A <c>LOCK</c> body that asks for a new lock (RFC 4918 §14.11): its scope and the client's
    /// <c>owner</c>, which the lock gives back as it came. Only write locks exist.</summary>
    public static (bool Exclusive, XElement? Owner) ParseLockInfo(XDocument body)
    {
        if (body.Root is not { } root || root.Name != Dav + "lockinfo") throw BadBody("A LOCK body is a DAV:lockinfo.");
        var scope = root.Element(Dav + "lockscope")?.Elements().FirstOrDefault();
        if (scope is null || scope.Name != Dav + "exclusive" && scope.Name != Dav + "shared") throw BadBody("A DAV:lockinfo has a DAV:lockscope, exclusive or shared.");
        if (root.Element(Dav + "locktype")?.Element(Dav + "write") is null) throw BadBody("A DAV:lockinfo asks for a DAV:locktype of DAV:write.");
        return (scope.Name == Dav + "exclusive", root.Element(Dav + "owner") is { } owner ? new XElement(owner) : null);
    }

    /// <summary>A <c>lockdiscovery</c> with an <c>activelock</c> (RFC 4918 §14.1) for each of <paramref name="locks"/>,
    /// with the seconds it has left.</summary>
    public static XElement LockDiscovery(IEnumerable<(DavLock Lock, int SecondsLeft)> locks) =>
        new(Dav + "lockdiscovery", locks.Select(l => new XElement(
            Dav + "activelock",
            new XElement(Dav + "locktype", new XElement(Dav + "write")),
            new XElement(Dav + "lockscope", new XElement(Dav + (l.Lock.Exclusive ? "exclusive" : "shared"))),
            new XElement(Dav + "depth", l.Lock.Deep ? "infinity" : "0"),
            l.Lock.Owner is { } owner ? XElement.Parse(owner) : null,
            new XElement(Dav + "timeout", $"Second-{l.SecondsLeft}"),
            new XElement(Dav + "locktoken", new XElement(Dav + "href", l.Lock.Token)),
            new XElement(Dav + "lockroot", new XElement(Dav + "href", l.Lock.Href)))));

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
                var found = new List<XElement>();
                var missing = new List<XElement>();
                foreach (var name in propfind.Names)
                {
                    if (properties.TryGetValue(name, out var property)) found.Add(property());
                    else missing.Add(new XElement(name));
                }

                var stats = new List<(int, IReadOnlyList<XElement>)>();
                if (found.Count > 0) stats.Add((StatusCodes.Status200OK, found));
                if (missing.Count > 0) stats.Add((StatusCodes.Status404NotFound, missing));
                return new(href, stats);
        }
    }

    /// <summary>A 207 with a <c>DAV:multistatus</c> of <paramref name="responses"/>.</summary>
    public static Task WriteMultistatusAsync(HttpResponse response, IEnumerable<DavResponse> responses, CancellationToken ct) =>
        WriteMultistatusAsync(response, responses.ToAsyncEnumerable(), ct);

    /// <summary>
    /// A 207 with a <c>DAV:multistatus</c> of <paramref name="responses"/>, written to the body one <c>response</c> at a
    /// time as each is made, without a length: what is in memory is one resource's answer (at most
    /// <see cref="MaxPropertyNames"/> properties) and the writer's buffer, however large the folder a <c>Depth: 1</c>
    /// lists. Once the first bytes are out, a failure can only cut the answer short, as a dropped connection would; the
    /// writer never closes the elements it opened then (<c>WriteEndDocumentOnClose</c> off), so a cut-off answer is never
    /// well-formed XML that a sync client could take for the whole folder, and Kestrel drops the connection.
    /// </summary>
    public static async Task WriteMultistatusAsync(HttpResponse response, IAsyncEnumerable<DavResponse> responses, CancellationToken ct)
    {
        response.StatusCode = StatusCodes.Status207MultiStatus;
        response.ContentType = ContentType;
        await using var writer = XmlWriter.Create(response.Body, new XmlWriterSettings { Encoding = Utf8, Async = true, CloseOutput = false, WriteEndDocumentOnClose = false });
        await writer.WriteStartDocumentAsync();
        await writer.WriteStartElementAsync("D", "multistatus", Dav.NamespaceName);
        await foreach (var r in responses.WithCancellation(ct))
        {
            var element = new XElement(
                Dav + "response",
                new XElement(Dav + "href", r.Href),
                r.PropStats.Select(s => new XElement(
                    Dav + "propstat",
                    new XElement(Dav + "prop", s.Properties),
                    new XElement(Dav + "status", Status(s.Status)))));
            await element.WriteToAsync(writer, ct);
        }

        await writer.WriteEndElementAsync();
        await writer.WriteEndDocumentAsync();
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

    /// <summary>A plain-text body, for a refusal a person may read (a client shows it, or logs it).</summary>
    public static Task WriteTextAsync(HttpResponse response, int status, string message, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        return response.WriteAsync(message, ct);
    }

    /// <summary>A propstat's status line (RFC 4918 §14.28).</summary>
    private static string Status(int status) =>
        $"HTTP/1.1 {status} {Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status)}";
}
