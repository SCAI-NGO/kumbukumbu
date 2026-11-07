// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Dav;

/// <summary>Where a <c>/dav</c> path lies for the parking area and the locks: the vault's id and the path below it
/// (<c>""</c> for the vault's own folder).</summary>
public readonly record struct DavPlace(string VaultId, string Path);

/// <summary>
/// The WebDAV share a request comes from, as <see cref="CallerMiddleware"/> resolved it: its id, which keys what it parks
/// and locks, and the vaults it opened on this request, by the names it sees them under. Fails closed: a request
/// without a share's caller never reaches a handler that asks.
/// </summary>
public sealed class DavCaller
{
    private readonly RequestCaller _caller;

    private DavCaller(RequestCaller caller, string shareId)
    {
        _caller = caller;
        ShareId = shareId;
    }

    public string ShareId { get; }

    /// <summary>The user whose share it is.</summary>
    public string UserId => _caller.User?.Id ?? throw new InvalidOperationException("A WebDAV share's caller without its user");

    public static DavCaller Of(HttpContext http)
    {
        var caller = http.Features.Get<RequestCaller>();
        return caller?.Credential is DavShareCredential share
            ? new DavCaller(caller, share.ShareId)
            : throw new InvalidOperationException("A /dav request without a WebDAV share's caller");
    }

    /// <summary>The id of the vault the share sees as <paramref name="name"/>, or null if it has none such.</summary>
    public string? VaultId(string name) => Access(name)?.Id;

    /// <summary>The share's access to the vault it sees as <paramref name="name"/>, as this request opened it, or null.</summary>
    internal VaultAccess? Access(string name) => (_caller.Credential as IRightsCredential)?.Access(name);

    /// <summary>The vault and the path below it of <paramref name="path"/>; null for the root and for a vault the share
    /// doesn't have.</summary>
    public DavPlace? Place(VaultPath path) =>
        path.IsRoot || VaultId(path.Segments[0]) is not { } vault ? null : new DavPlace(vault, string.Join('/', path.Segments.Skip(1)));

    /// <summary>Whether the share may write into the vault it sees as <paramref name="name"/> (its right and, for a
    /// member's share, her role; a vault read-only for every member reads as <c>read</c>).</summary>
    public bool CanWrite(string name) => _caller.CanWrite(name);

    /// <summary>Why it may not, in the words every refusal of the vault uses, as a sentence of its own.</summary>
    public string ReadOnly(string name)
    {
        var sentence = _caller.ReadOnlySentence(name);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".";
    }
}
