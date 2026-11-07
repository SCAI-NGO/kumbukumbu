// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Xml.Linq;

namespace Kumbu.Server.Dav;

/// <summary>A lock as <c>LOCK</c> granted it: on <paramref name="Path"/> below the vault <paramref name="VaultId"/>
/// (both <c>""</c> for nothing below one), and with <paramref name="Deep"/> everything below it (<c>Depth: infinity</c>).
/// <paramref name="Href"/> is the URL it was asked for, its <c>lockroot</c>; <paramref name="Owner"/> the client's
/// <c>DAV:owner</c> as XML text, given back as it came: text, so that no response shares a node of the singleton's, and
/// small (<see cref="DavRules.LockOwnerMax"/>).</summary>
public sealed record DavLock(
    string Token, string VaultId, string Path, string Href, bool Deep, bool Exclusive, string? Owner, TimeSpan Timeout, DateTimeOffset Expires);

/// <summary>
/// Fake locks (WebDAV design §3.7): class 2 is advertised because Windows, Finder and Office won't write without it, but
/// nothing is ever refused for a lock. A lock is a token with a timeout, kept in memory per share, so that a client sees
/// its own locks in <c>lockdiscovery</c> and another share sees none of them. A lock goes when it is unlocked or its
/// timeout passes (checked on every call, no timer); a restart forgets them all, which is harmless.
/// </summary>
public sealed class DavLocks(TimeProvider time)
{
    public const string TokenScheme = "opaquelocktoken:";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, DavLock>> _byShare = new(StringComparer.Ordinal);

    /// <summary>A new lock for <paramref name="timeout"/>, at most <see cref="DavRules.LockDefault"/>. Past
    /// <see cref="DavRules.LockCap"/> locks of the share, the one that expires first goes. An owner longer than
    /// <see cref="DavRules.LockOwnerMax"/> is left out.</summary>
    public DavLock Lock(string shareId, string vaultId, string path, string href, bool deep, bool exclusive, XElement? owner, TimeSpan? timeout)
    {
        lock (_gate)
        {
            var locks = Of(shareId, create: true)!;
            while (locks.Count >= DavRules.LockCap) locks.Remove(locks.Values.MinBy(l => l.Expires)!.Token);
            var granted = Granted(timeout);
            var text = owner?.ToString(SaveOptions.DisableFormatting);
            if (text?.Length > DavRules.LockOwnerMax) text = null;
            var created = new DavLock(TokenScheme + Guid.NewGuid().ToString("D"), vaultId, path, href, deep, exclusive, text, granted, time.GetUtcNow() + granted);
            locks[created.Token] = created;
            return created;
        }
    }

    /// <summary>The share's lock <paramref name="token"/>, its timeout started again; null if it has none such (any
    /// more).</summary>
    public DavLock? Refresh(string shareId, string token, TimeSpan? timeout)
    {
        lock (_gate)
        {
            if (Of(shareId)?.GetValueOrDefault(token) is not { } held) return null;
            var granted = Granted(timeout);
            return _byShare[shareId][token] = held with { Timeout = granted, Expires = time.GetUtcNow() + granted };
        }
    }

    /// <summary>Removes the share's lock <paramref name="token"/>; false if it has none such.</summary>
    public bool Unlock(string shareId, string token)
    {
        lock (_gate)
        {
            if (Of(shareId) is not { } locks || !locks.Remove(token)) return false;
            if (locks.Count == 0) _byShare.Remove(shareId);
            return true;
        }
    }

    /// <summary>The share's locks that cover <paramref name="path"/>: one on it, and a deep one on a folder above it.</summary>
    public IReadOnlyList<DavLock> On(string shareId, string vaultId, string path)
    {
        lock (_gate)
        {
            if (Of(shareId) is not { } locks) return [];
            return [.. locks.Values.Where(l => Covers(l, vaultId, path)).OrderBy(l => l.Expires)];
        }
    }

    /// <summary>The seconds left of <paramref name="held"/>, rounded up, as its <c>Timeout</c> says them.</summary>
    public int SecondsLeft(DavLock held) => Math.Max(0, (int)Math.Ceiling((held.Expires - time.GetUtcNow()).TotalSeconds));

    /// <summary>What the client asked for, but never more than the default; the default if it asked for none.</summary>
    private static TimeSpan Granted(TimeSpan? asked) => asked is { } a && a < DavRules.LockDefault ? a : DavRules.LockDefault;

    private static bool Covers(DavLock held, string vaultId, string path)
    {
        if (held.VaultId != vaultId) return false;
        if (held.Path == path) return true;
        return held.Deep && (held.Path.Length == 0 || path.StartsWith(held.Path + "/", StringComparison.Ordinal));
    }

    /// <summary>The share's locks without those that expired; null if it has none.</summary>
    private Dictionary<string, DavLock>? Of(string shareId, bool create = false)
    {
        var now = time.GetUtcNow();
        if (_byShare.TryGetValue(shareId, out var locks))
        {
            foreach (var gone in locks.Values.Where(l => l.Expires <= now).ToList()) locks.Remove(gone.Token);
            if (locks.Count > 0 || create) return locks;
            _byShare.Remove(shareId);
            return null;
        }

        return create ? _byShare[shareId] = new Dictionary<string, DavLock>(StringComparer.Ordinal) : null;
    }
}
