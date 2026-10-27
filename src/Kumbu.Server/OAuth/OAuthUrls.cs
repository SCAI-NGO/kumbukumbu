// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Kumbu.Server.OAuth;

/// <summary>The scopes of a connection's tokens (Ruling 3): <c>kumbu:read</c> caps the grant to read.</summary>
public static class OAuthScopes
{
    public const string Read = "kumbu:read";
    public const string Write = "kumbu:write";
    public static readonly string[] Supported = [Read, Write];

    /// <summary>What the 401 challenge on a connection's URL asks for.</summary>
    public const string Challenge = "kumbu:read kumbu:write";
}

/// <summary>The addresses OAuth hands out and compares. The issuer is the origin.</summary>
public static partial class OAuthUrls
{
    public const string ConnectionPrefix = "/mcp/c/";

    public const string ResourceMetadataPrefix = "/.well-known/oauth-protected-resource";

    /// <summary>Ids are 16 lower-case Base32 characters (Ruling 10); anything else is no connection, whatever the database says.</summary>
    [GeneratedRegex("^[a-z2-7]{16}$")]
    private static partial Regex IdShape();

    public static bool IsConnectionId(string? id) => id is not null && IdShape().IsMatch(id);

    /// <summary><c>PublicUrl</c> without its trailing slash (behind a proxy the request's own host may be an internal
    /// one), else the request's scheme and host.</summary>
    public static string Origin(HttpContext http, string? publicUrl) =>
        publicUrl is { Length: > 0 } ? publicUrl.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}";

    public static string Resource(string origin, string connectionId) => $"{origin}{ConnectionPrefix}{connectionId}";

    public static string ResourceMetadata(string origin, string connectionId, bool trailingSlash = false) =>
        $"{origin}{ResourceMetadataPrefix}{ConnectionPrefix}{connectionId}{(trailingSlash ? "/" : "")}";

    /// <summary>Ruling 5: scheme and host lower-cased, a default port dropped, one trailing slash dropped; the path
    /// (and query, which no resource has) is kept as is, so it compares ordinally. Not a URL: returned unchanged.</summary>
    public static string Canonical(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo != "" || uri.Fragment != "") return url;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        var rest = uri.PathAndQuery;
        if (rest.EndsWith('/')) rest = rest[..^1];
        return $"{uri.Scheme}://{uri.Host}{port}{rest}";
    }

    /// <summary>The connection a <c>resource</c> names: the id if its canonical form is exactly
    /// <c>{canonical origin}/mcp/c/{id}</c>, else null.</summary>
    public static string? ConnectionIdOf(string origin, string resource)
    {
        var prefix = Canonical(origin) + ConnectionPrefix;
        var canonical = Canonical(resource);
        if (!canonical.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var id = canonical[prefix.Length..];
        return IsConnectionId(id) ? id : null;
    }
}
