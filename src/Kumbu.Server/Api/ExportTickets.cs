// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Api;

/// <summary>
/// One-time tickets for an export: the password, checked over <c>POST</c>, turns into a ticket that the download's
/// <c>GET</c> carries in its URL (a browser download sends no body, and a password never goes into a URL). Valid
/// <see cref="Lifetime"/> for one export by the same user, kept in memory by its SHA-256 only, as the sync tickets
/// are. A user holds one open ticket: a new one replaces it.
/// </summary>
public sealed class ExportTickets(TimeProvider time)
{
    public const string Prefix = "kumbu_ext_";

    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (string Hash, DateTime IssuedAt)> _byUser = new(StringComparer.Ordinal);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public (string Ticket, DateTime ExpiresAt) Issue(string userId)
    {
        var ticket = Secrets.New(Prefix);
        var now = Now;
        _byUser[userId] = (Hash(ticket), now);
        return (ticket, now + Lifetime);
    }

    /// <summary>True, and the ticket used up, if it is the user's open one; false for any other.</summary>
    public bool Take(string userId, string? ticket)
    {
        if (ticket is null || !Secrets.IsWellFormed(ticket, Prefix) || !_byUser.TryGetValue(userId, out var entry)) return false;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry.Hash), Encoding.ASCII.GetBytes(Hash(ticket)))) return false;
        // Removed only if it is still this entry: of two downloads with one ticket, one gets it.
        return _byUser.TryRemove(new KeyValuePair<string, (string, DateTime)>(userId, entry)) && Now - entry.IssuedAt <= Lifetime;
    }

    private static string Hash(string ticket) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
