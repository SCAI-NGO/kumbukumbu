// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Xml.Linq;
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Dav;

/// <summary>
/// The live properties of a resource for one request (WebDAV design §3.4), each built when a <c>PROPFIND</c> asks for it.
/// Per request and asynchronous, since what isn't in <see cref="DavResource"/> needs the request: a resource's locks
/// (<c>lockdiscovery</c>, the share's own from <see cref="DavLocks"/>) and RFC 4331's quota properties join in
/// <see cref="PropertiesAsync"/>. Those come only on the root and on the vaults the share's user owns (a shared vault
/// counts against its owner, whose figures a member never learns), only while a limit applies to her, and only when the
/// <c>PROPFIND</c> names them: RFC 4331 keeps them out of <c>allprop</c>, so the listings every client asks for never
/// wait for a count. Both are her account's, on the root as on each of her vaults: the quota is per user.
/// </summary>
public sealed class DavProperties(DavCaller caller, DavLocks locks, StorageQuota quota, DavPropfind propfind)
{
    private static readonly XNamespace D = DavXml.Dav;

    private static readonly XName Available = D + "quota-available-bytes";
    private static readonly XName Used = D + "quota-used-bytes";

    private readonly bool _quotaAsked = propfind.Kind == DavPropfindKind.Prop && propfind.Names.Any(n => n == Available || n == Used);

    /// <summary>The share's user's figure, once per request.</summary>
    private Task<(long Used, long Limit)?>? _usage;

    /// <summary>The properties of <paramref name="resource"/>, by name.</summary>
    public async Task<IReadOnlyDictionary<XName, Func<XElement>>> PropertiesAsync(DavResource resource, CancellationToken ct)
    {
        var properties = Live(resource);
        properties[D + "lockdiscovery"] = () => DavXml.LockDiscovery(
            caller.Place(resource.Path) is { } place ? locks.On(caller.ShareId, place.VaultId, place.Path).Select(l => (l, locks.SecondsLeft(l))) : []);
        if (_quotaAsked && (resource.Path.IsRoot || (resource.Path.Segments.Count == 1 && caller.Access(resource.Path.Segments[0]) is { IsOwner: true }))
            && await (_usage ??= quota.UsageAsync(caller.UserId, ct)) is { } usage)
        {
            properties[Available] = () => new XElement(Available, Math.Max(0, usage.Limit - usage.Used).ToString(CultureInfo.InvariantCulture));
            properties[Used] = () => new XElement(Used, usage.Used.ToString(CultureInfo.InvariantCulture));
        }

        return properties;
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
