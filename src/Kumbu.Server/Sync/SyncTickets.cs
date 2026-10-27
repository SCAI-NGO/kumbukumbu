// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Sync;

/// <summary>What a ticket lets its WebSocket hear about: the key and the vaults it opened when the ticket was made.</summary>
public sealed record TicketGrant(string KeyId, IReadOnlyList<(string VaultId, string VaultName)> Vaults);

/// <summary>
/// One-time tickets for the sync WebSocket (sync spec §5). A WebSocket can't carry an <c>Authorization</c> header from
/// every client, so an API key asks for a ticket over REST and puts it in the URL: valid <see cref="Lifetime"/>, for
/// one connection, kept in memory by its SHA-256 only, so neither a log line nor this process's memory holds a
/// usable one for longer.
/// </summary>
public sealed class SyncTickets(TimeProvider time)
{
    public const string Prefix = "kumbu_wst_";

    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (TicketGrant Grant, DateTime IssuedAt)> _tickets = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public (string Ticket, DateTime ExpiresAt) Issue(string keyId, IReadOnlyList<(string VaultId, string VaultName)> vaults)
    {
        Sweep();
        var ticket = Secrets.New(Prefix);
        var now = Now;
        _tickets[Hash(ticket)] = (new TicketGrant(keyId, vaults), now);
        return (ticket, now + Lifetime);
    }

    /// <summary>The grant, removed so it serves one connection; null for an unknown, used or expired ticket.</summary>
    public TicketGrant? Take(string ticket)
    {
        if (!Secrets.IsWellFormed(ticket, Prefix) || !_tickets.TryRemove(Hash(ticket), out var entry)) return null;
        return Now - entry.IssuedAt <= Lifetime ? entry.Grant : null;
    }

    private void Sweep()
    {
        var now = Now;
        foreach (var (hash, entry) in _tickets)
        {
            if (now - entry.IssuedAt > Lifetime) _tickets.TryRemove(hash, out _);
        }
    }

    private static string Hash(string ticket) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
