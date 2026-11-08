// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Http.Features;

namespace Kumbu.Server.Dav;

/// <summary>What a <c>/dav</c> URL names: the root (the share's vaults), a vault by the caller's name for it, or a path
/// below one. <paramref name="Bare"/>: the URL was <c>/dav</c> without its slash.</summary>
public sealed record DavTarget(VaultPath Path, bool Bare = false)
{
    /// <summary>The path the client named, where <see cref="DavTree.ResolveAsync"/> took it for another
    /// (<c>Plan.MD</c> for the note <c>Plan.md</c>); null where it is <see cref="Path"/>.</summary>
    public VaultPath? Asked { get; init; }
}

/// <summary>
/// <c>/dav</c> URLs and the vault paths they name (WebDAV design §5). A URL is read as the client sent it, not as Kestrel
/// resolved it: Kestrel removes <c>.</c> and <c>..</c> segments before routing, and leaves a percent-encoded <c>/</c>
/// in a decoded path, where it can't be told from a name holding "%2F". So each segment of the raw target is decoded
/// on its own, an encoded slash refused before it could become a separator, and the result goes through
/// <see cref="ApiPaths.Parse(string, string?, PathKind)"/> as every REST path does (NFC; no empty, <c>.</c> or
/// <c>..</c> segment, no backslash).
/// </summary>
public static class DavPaths
{
    public const string Prefix = "/dav";

    private const string Refused = "A path may not contain empty, '.' or '..' segments, backslashes or encoded slashes.";

    /// <summary>A request target never carries a fragment (RFC 9112 §3.2), and a <c>Destination</c> names a resource, not
    /// a part of one. Kestrel keeps a raw <c>#</c> in the path; cut at it, <c>DELETE …/Projects/#1 ideas.md</c> would act
    /// on the folder. A name holding one is sent as <c>%23</c>.</summary>
    internal const string Fragment = "A WebDAV URL has no fragment: a '#' in a name is sent as %23.";

    /// <summary>The target of <paramref name="request"/>; throws a 400 <see cref="DavException"/> for a path that isn't
    /// one.</summary>
    public static DavTarget Parse(HttpRequest request)
    {
        var raw = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        return Parse(RawPath(raw) ?? request.Path.Value ?? Prefix);
    }

    /// <summary>The target of a path as sent (percent-encoded, without query), starting with <see cref="Prefix"/>.</summary>
    public static DavTarget Parse(string rawPath)
    {
        if (!rawPath.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) throw new DavException(StatusCodes.Status400BadRequest, Refused);
        var rest = rawPath[Prefix.Length..];
        if (rest.Length == 0) return new DavTarget(VaultPath.Root, Bare: true);
        if (rest[0] != '/') throw new DavException(StatusCodes.Status400BadRequest, Refused);

        // "/dav/" and "/dav/Work/a/" end in a slash: one empty segment at the end is a collection's spelling, not a segment.
        var parts = rest[1..].Split('/');
        if (parts[^1].Length == 0) parts = parts[..^1];
        if (parts.Length == 0) return new DavTarget(VaultPath.Root);

        var segments = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Contains("%2F", StringComparison.OrdinalIgnoreCase)) throw new DavException(StatusCodes.Status400BadRequest, Refused);
            segments.Add(Uri.UnescapeDataString(part));
        }

        try
        {
            return new DavTarget(ApiPaths.Parse(segments[0], segments.Count == 1 ? null : string.Join('/', segments.Skip(1)), PathKind.Folder));
        }
        catch (ApiException)
        {
            throw new DavException(StatusCodes.Status400BadRequest, Refused);
        }
    }

    /// <summary>The URL of a path: each segment percent-encoded (RFC 3986's unreserved characters kept, everything else
    /// as UTF-8), a collection's with a trailing slash.</summary>
    public static string Href(VaultPath path, bool collection)
    {
        if (path.IsRoot) return Prefix + "/";
        var href = Prefix + "/" + string.Join('/', path.Segments.Select(Uri.EscapeDataString));
        return collection ? href + "/" : href;
    }

    /// <summary>The path of a request target in origin or absolute form, without its query; null for any other (none,
    /// <c>*</c>), and then the decoded path is read instead. A target with a <c>#</c> is a 400 (<see cref="Fragment"/>).</summary>
    private static string? RawPath(string? target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var path = target.Split('?')[0];
        if (path.Contains('#')) throw new DavException(StatusCodes.Status400BadRequest, Fragment);
        if (path.StartsWith('/')) return path;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        var slash = scheme < 0 ? -1 : path.IndexOf('/', scheme + 3);
        return slash < 0 ? null : path[slash..];
    }
}
