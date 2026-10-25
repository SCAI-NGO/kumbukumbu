// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Driver;

namespace Kumbu.Server.OAuth;

/// <summary>Finds the client an authorisation request names: a metadata document for a URL, else a registered one.</summary>
public sealed class OAuthClients(IMongoDatabase db, ClientMetadataFetcher fetcher, TimeProvider time)
{
    // Kept for the store of dynamically registered clients, which joins in the next task.
    private readonly IMongoDatabase _db = db;
    private readonly TimeProvider _time = time;

    /// <summary>Null when the client is unknown or its document unusable; <see cref="ClientMetadataFetcher.FetchAsync"/> says why when that matters.</summary>
    public async Task<OAuthClient?> ResolveAsync(string clientId, CancellationToken ct)
    {
        if (ClientMetadataFetcher.IsMetadataUrl(clientId)) return (await fetcher.FetchAsync(clientId, ct)).Client;

        // Seam: registered (DCR) clients are looked up here by id in _db, expiring by _time.
        _ = (_db, _time);
        return null;
    }

    /// <summary>Ruling 6: exact, except an http loopback redirect whose port is ignored (RFC 8252 §7.3).</summary>
    public static bool RedirectMatches(OAuthClient client, string redirectUri)
    {
        if (client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal)) return true;
        if (!TryLoopback(redirectUri, out var asked)) return false;

        foreach (var registered in client.RedirectUris)
        {
            if (TryLoopback(registered, out var known)
                && asked.Host == known.Host
                && string.Equals(asked.PathAndQuery, known.PathAndQuery, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryLoopback(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == "http" && ClientMetadataFetcher.IsLoopbackHost(uri) && uri.Fragment.Length == 0;
}
