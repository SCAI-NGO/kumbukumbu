// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Xml.Linq;

namespace Kumbu.Server.Dav;

/// <summary>
/// The live properties of a resource for one request (WebDAV design §3.4), each built when a <c>PROPFIND</c> asks for it.
/// Per request and asynchronous, since what isn't in <see cref="DavResource"/> needs the request: a resource's locks
/// (<c>lockdiscovery</c>, the share's own from <see cref="DavLocks"/>) and the RFC 4331 quota properties on the root and
/// the vaults the share's user owns (from the usage cache, Task 12) join in <see cref="PropertiesAsync"/>.
/// </summary>
public sealed class DavProperties(DavCaller caller, DavLocks locks)
{
    private static readonly XNamespace D = DavXml.Dav;

    /// <summary>The properties of <paramref name="resource"/>, by name.</summary>
    public Task<IReadOnlyDictionary<XName, Func<XElement>>> PropertiesAsync(DavResource resource, CancellationToken ct)
    {
        var properties = Live(resource);
        properties[D + "lockdiscovery"] = () => DavXml.LockDiscovery(
            caller.Place(resource.Path) is { } place ? locks.On(caller.ShareId, place.VaultId, place.Path).Select(l => (l, locks.SecondsLeft(l))) : []);
        return Task.FromResult<IReadOnlyDictionary<XName, Func<XElement>>>(properties);
    }

    /// <summary>What the resource itself says: its type, name, length, type of content, version and last write.</summary>
    private static Dictionary<XName, Func<XElement>> Live(DavResource resource)
    {
        var properties = new Dictionary<XName, Func<XElement>>
        {
            [D + "resourcetype"] = () => new XElement(D + "resourcetype", resource.IsCollection ? new XElement(D + "collection") : null),
        };
        if (!resource.Path.IsRoot) properties[D + "displayname"] = () => new XElement(D + "displayname", resource.Path.Name);
        if (resource.Length is { } length) properties[D + "getcontentlength"] = () => new XElement(D + "getcontentlength", length.ToString(CultureInfo.InvariantCulture));
        if (resource.ContentType is { } type) properties[D + "getcontenttype"] = () => new XElement(D + "getcontenttype", type);
        if (resource.Version is { } version) properties[D + "getetag"] = () => new XElement(D + "getetag", $"\"{version}\"");
        if (resource.LastWrite is { } written)
        {
            // The last write is both: Kumbu² keeps no creation time (WebDAV design §3.4).
            var utc = DateTime.SpecifyKind(written, DateTimeKind.Utc);
            properties[D + "getlastmodified"] = () => new XElement(D + "getlastmodified", utc.ToString("R", CultureInfo.InvariantCulture));
            properties[D + "creationdate"] = () => new XElement(D + "creationdate", utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }

        properties[D + "supportedlock"] = () => new XElement(D + "supportedlock", LockEntry("exclusive"), LockEntry("shared"));
        return properties;

        static XElement LockEntry(string scope) =>
            new(D + "lockentry", new XElement(D + "lockscope", new XElement(D + scope)), new XElement(D + "locktype", new XElement(D + "write")));
    }
}
