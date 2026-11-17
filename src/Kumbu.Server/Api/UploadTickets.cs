// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Kumbu.Server.Api;

/// <summary>
/// An open upload: the credential the agent's MCP call carried (<see cref="Bearer"/>, UTF-8), where the file goes and
/// who may have opened it. <see cref="Path"/> is as the caller saw it (<c>/Vault/x.png</c>); <see cref="Holder"/> is the
/// key the cap counts by (key id, connection id or <c>"dev"</c>).
/// </summary>
public sealed record UploadTicket(byte[] Bearer, string? ConnectionId, string Path, string? Version, string Holder, DateTime ExpiresAt);

/// <summary>
/// One-time upload URLs for agents: an MCP call hands out a token, the agent <c>PUT</c>s the file's bytes to it and the
/// upload runs as the credential that asked. The ticket holds a copy of that credential, so it lives in this process's
/// memory only, never in MongoDB or a log, and the copy is zeroed whenever a ticket goes: expired, dropped by the cap,
/// or done with (<see cref="Zero"/>). The token is 256 random bits and only its SHA-256 is kept as the key.
/// </summary>
public sealed class UploadTickets : IDisposable
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider time;
    private readonly ITimer _sweeper;

    public UploadTickets(TimeProvider time)
    {
        this.time = time;
        // An expired ticket goes within a minute, with its copy of the credential, whether or not anyone uploads again.
        _sweeper = time.CreateTimer(_ => { lock (_issuing) Sweep(); }, null, SweepInterval, SweepInterval);
    }

    public const int MaxPerHolder = 20;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, UploadTicket> _tickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _byHolder = new(StringComparer.Ordinal);
    private readonly Lock _issuing = new();

    /// <summary>For tests: how many tickets are held, expired ones not yet swept included.</summary>
    internal int Count => _tickets.Count;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// A new ticket; <paramref name="bearer"/> is taken over (a copy the caller doesn't use again). It expires after
    /// <see cref="Lifetime"/>; a credential that expires sooner is refused by the PUT's own resolution.
    /// </summary>
    public (string Token, DateTime ExpiresAt) Issue(byte[] bearer, string? connectionId, string path, string? version, string holder)
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = Now + Lifetime;
        var ticket = new UploadTicket(bearer, connectionId, path, version, holder, expiresAt);
        var key = KeyOf(token);
        lock (_issuing)
        {
            Sweep();
            _tickets[key] = ticket;
            if (!_byHolder.TryGetValue(holder, out var queue)) _byHolder[holder] = queue = new Queue<string>();
            queue.Enqueue(key);
            // Tickets already taken or swept still sit in the queue; only live ones count towards the cap.
            var live = new Queue<string>(queue.Where(_tickets.ContainsKey));
            while (live.Count > MaxPerHolder)
            {
                if (_tickets.TryRemove(live.Dequeue(), out var dropped)) Zero(dropped);
            }

            _byHolder[holder] = live;
        }

        return (token, expiresAt);
    }

    /// <summary>
    /// The ticket for a token, removed on the way: of two calls at once only one gets it. Null for an unknown, used or
    /// expired token; an expired ticket is zeroed. The caller zeroes the one it got (<see cref="Zero"/>) when done.
    /// </summary>
    public UploadTicket? Take(string token)
    {
        if (!_tickets.TryRemove(KeyOf(token), out var ticket)) return null;
        if (Now < ticket.ExpiresAt) return ticket;
        Zero(ticket);
        return null;
    }

    public void Zero(UploadTicket ticket) => CryptographicOperations.ZeroMemory(ticket.Bearer);

    private void Sweep()
    {
        var now = Now;
        foreach (var (key, ticket) in _tickets)
        {
            if (now >= ticket.ExpiresAt && _tickets.TryRemove(key, out var expired)) Zero(expired);
        }

        foreach (var (holder, queue) in _byHolder)
        {
            if (!queue.Any(_tickets.ContainsKey)) _byHolder.Remove(holder);
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (var key in _tickets.Keys)
        {
            if (_tickets.TryRemove(key, out var ticket)) Zero(ticket);
        }
    }

    private static string KeyOf(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
