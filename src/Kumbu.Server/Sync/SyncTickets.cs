// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Sync;

/// <summary>What holds a sync socket (collab design §9.1): an API key, or a web session.</summary>
public enum SubjectKind
{
    Key,
    Session,
}

/// <summary>Who a sync socket is for: an API key (its id), or a web session (<see cref="Accounts.Sessions.SubjectOf"/>),
/// and the user either belongs to. Two sessions of one user are two subjects.</summary>
public sealed record Subject(SubjectKind Kind, string Id, string User);

/// <summary>
/// One vault of a ticket, as the subject had it when the ticket was made: the subject's name for it (a member's
/// alias), the membership it came through (null for the owner), and the level. <c>Label</c>, for a member's
/// session only: the owner's label for her, which the owner's presence lines show (collab design §8.2).
/// </summary>
public sealed record TicketVault(string VaultId, string VaultName, string? Membership, AccessLevel Level)
{
    public string Label { get; init; } = "";
}

/// <summary>What a ticket lets its WebSocket hear about: the subject and the vaults it opened when the ticket was made.
/// <c>Display</c>: the user's display name, which members see when she, their vault's owner, is editing.
/// <c>Revealed</c>: a session's ticket asked while it showed its discreet vaults, which it may hold then only until the
/// reveal ends, at <c>RevealUntil</c> (null for a reveal until the session ends or hides them).</summary>
public sealed record TicketGrant(Subject Subject, IReadOnlyList<TicketVault> Vaults)
{
    public string Display { get; init; } = "";

    public bool Revealed { get; init; }

    public DateTime? RevealUntil { get; init; }
}

/// <summary>
/// One-time tickets for the sync WebSocket (sync spec §5). A WebSocket can't carry an <c>Authorization</c> header from
/// every client, so an API key or a web session asks for a ticket over REST and puts it in the URL: valid
/// <see cref="Lifetime"/>, for one connection, kept in memory by its SHA-256 only, so neither a log line nor this
/// process's memory holds a usable one for longer. A subject holds at most <see cref="MaxPerKey"/> open tickets: a new
/// one beyond that drops the subject's oldest, so one asking in a loop costs a bounded amount of memory and a client
/// that connects at once never notices.
/// </summary>
public sealed class SyncTickets(TimeProvider time)
{
    public const string Prefix = "kumbu_wst_";

    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public const int MaxPerKey = 10;

    private readonly ConcurrentDictionary<string, (TicketGrant Grant, DateTime IssuedAt, long Number)> _tickets = new();
    private long _issued;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>For tests: how many tickets are held.</summary>
    internal int Count => _tickets.Count;

    public (string Ticket, DateTime ExpiresAt) Issue(TicketGrant grant)
    {
        Sweep();
        var ticket = Secrets.New(Prefix);
        var now = Now;
        _tickets[Hash(ticket)] = (grant, now, Interlocked.Increment(ref _issued));
        // By number, not time: tickets issued within one clock tick still have an order, and the new one is never dropped.
        var held = _tickets.Where(t => t.Value.Grant.Subject == grant.Subject).OrderByDescending(t => t.Value.Number).Skip(MaxPerKey).ToList();
        foreach (var (hash, _) in held) _tickets.TryRemove(hash, out _);
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
