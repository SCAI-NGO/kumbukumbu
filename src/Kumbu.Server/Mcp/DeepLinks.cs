// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Server.Auth;
using Kumbu.Server.OAuth;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Mcp;

/// <summary>The link into the web app for a note the caller sees at a path: <c>{origin}/n/{vaultId}/{segments}</c>. The
/// vault id is the same for the owner and every member, whatever name each sees the vault under, so a link can be passed on.</summary>
public sealed class DeepLinks(RequestCaller caller, IHttpContextAccessor http, IOptions<KumbuOptions> options)
{
    /// <summary>The link for a path in the caller's view (the path of a version block, which never carries a <c>#section</c>), or
    /// null when it doesn't parse, is a folder's marker (the tree never lists one), there is no request, or the caller has no
    /// access record of the vault (the dev token has none).</summary>
    public string? For(string callerPath)
    {
        if (http.HttpContext is not { } context) return null;
        if (!VaultPath.TryParse(callerPath, out var path) || path.Segments.Count < 2 || FolderMarker.Is(path)) return null;
        if ((caller.Credential as IRightsCredential)?.Access(path.Segments[0])?.Id is not { } vaultId) return null;
        return Url(OAuthUrls.Origin(context, options.Value.PublicUrl), vaultId, path.Segments.Skip(1));
    }

    /// <summary>Each segment is escaped on its own, so a <c>#</c> or <c>?</c> in a name stays in the path.</summary>
    public static string Url(string origin, string vaultId, IEnumerable<string> segmentsInsideVault) =>
        $"{origin}/n/{vaultId}/{string.Join('/', segmentsInsideVault.Select(Uri.EscapeDataString))}";
}
