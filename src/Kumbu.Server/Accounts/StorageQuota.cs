// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>A size as <c>KUMBU_QUOTA_DEFAULT</c> takes it (WebDAV design §3.10): <c>2GB</c>, <c>500MB</c>, <c>15 MB</c>,
/// <c>1.5GB</c>; <c>B</c>, <c>KB</c>, <c>MB</c>, <c>GB</c>, <c>TB</c> in any case, with binary prefixes (<c>2GB</c> is 2 GiB, as
/// the code already calls 15 MiB "15 MB"); a bare number is bytes.</summary>
public static partial class QuotaSize
{
    [GeneratedRegex(@"^\s*([0-9]{1,15}(?:\.[0-9]{1,6})?)\s*([KMGT]?B)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Size();

    /// <summary>The bytes; null for empty or 0 (no limit). Anything else is a <see cref="FormatException"/>.</summary>
    public static long? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Size().Match(text);
        if (!match.Success) throw new FormatException($"'{text.Trim()}' is not a size such as 2GB or 500MB.");
        var shift = match.Groups[2].Success ? match.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 10,
            "MB" => 20,
            "GB" => 30,
            "TB" => 40,
            _ => 0,
        } : 0;
        var bytes = decimal.Floor(decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * (1L << shift));
        if (bytes > long.MaxValue) throw new FormatException($"'{text.Trim()}' is larger than any disk.");
        return bytes == 0 ? null : (long)bytes;
    }
}

/// <summary>What one owner's vaults take in the database, counted now (<c>df</c>, the web app): bytes in all and by vault
/// id, and the limit that applies (null: none).</summary>
public sealed record StorageFigure(long Used, long? Limit, IReadOnlyDictionary<string, long> ByVault);

/// <summary>
/// The storage quota (WebDAV design §3.10): what a user's own vaults take in the database, exactly the figure of the
/// admin's user list (<see cref="Admin.BytesByVaultAsync"/>, from what MongoDB holds in the clear), against her limit: her
/// override (<see cref="UserDocument.Quota"/>), else <c>KUMBU_QUOTA_DEFAULT</c>. Data in a shared vault counts against its
/// owner, whoever writes it.
/// <para>
/// The usage is cached per owner, in memory. A write's check (<see cref="Require"/>) reads the cache only, and never waits
/// for MongoDB (brief open point 8): what isn't known yet (a vault's owner, the limit, the first count after a start) is
/// read in the background, and the write is allowed meanwhile. Every write adjusts the figure; the write path recounts an
/// owner whose figure is ten minutes old (<see cref="RecountAfter"/>), which corrects what the cache doesn't see: the
/// trash's TTL, history trimmed, embeddings, a write by another process. A count or lookup that fails is tried again
/// after the same ten minutes, not on every write. The check is against the usage plus what is about to be written, and
/// may be off by a write's worth under concurrency, which is accepted.
/// </para>
/// <para>
/// A server whose admin set no default and no override behaves as before (brief, "breaks nothing"): the cache then asks
/// MongoDB nothing but, at most every ten minutes, whether any user document carries an override at all. Only once one
/// does (or an admin sets one here) are owners looked up and limits read; an owner without a limit is never counted.
/// </para>
/// </summary>
public sealed class StorageQuota
{
    /// <summary>How old the write path lets an owner's figure get before it counts again, and how long a failed count or
    /// lookup waits before it is tried again.</summary>
    public static readonly TimeSpan RecountAfter = TimeSpan.FromMinutes(10);

    /// <summary>How long <c>df</c> serves a figure it counted before it counts again (<see cref="RecentFigureAsync"/>).</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(30);

    /// <summary>The stored override for "unlimited" (<see cref="UserDocument.Quota"/>); null is the default.</summary>
    public const long Unlimited = -1;

    private readonly IMongoCollection<UserDocument> _users;
    private readonly VaultCatalog _catalog;
    private readonly Admin _admin;
    private readonly TimeProvider _time;
    private readonly ILogger<StorageQuota> _log;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);

    /// <summary>Each vault's owner, once read, for good: it never changes, and a vault without one (the dev token's) never
    /// gets one.</summary>
    private readonly Dictionary<string, string?> _owners = new(StringComparer.Ordinal);

    /// <summary>When a vault's owner couldn't be read: not again before <see cref="RecountAfter"/>.</summary>
    private readonly Dictionary<string, DateTime> _lookupFailed = new(StringComparer.Ordinal);

    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);

    /// <summary>The count <see cref="RecentFigureAsync"/> serves per owner, and when it started.</summary>
    private readonly Dictionary<string, (Task<StorageFigure> Count, DateTime StartedAt)> _shown = new(StringComparer.Ordinal);

    /// <summary>The background work under way, for <see cref="SettledAsync"/>; what has ended is dropped whenever more is added.</summary>
    private readonly List<Task> _running = [];

    /// <summary>Whether any user document carries an override (null: not read yet), and when that was read.</summary>
    private bool? _overrides;

    private DateTime? _overridesReadAt;
    private Task? _overridesCheck;

    public StorageQuota(IMongoDatabase db, VaultCatalog catalog, Admin admin, IOptions<KumbuOptions> options, TimeProvider time, ILogger<StorageQuota> log)
    {
        _users = db.GetCollection<UserDocument>("users");
        _catalog = catalog;
        _admin = admin;
        _time = time;
        _log = log;
        // The options validator refused an unparsable size before anything resolves this.
        Default = QuotaSize.Parse(options.Value.Quota.Default);
        catalog.BlobsReleased += Released;
    }

    /// <summary>The limit of a user without an override (<c>KUMBU_QUOTA_DEFAULT</c>); null: none.</summary>
    public long? Default { get; }

    /// <summary>For tests: runs before each query the cache makes, with its kind (<c>overrides</c>, <c>owner</c>,
    /// <c>limit</c>, <c>count</c>) and the vault's or user's id; it may wait, or throw as a failing query would.</summary>
    internal Func<string, string, Task>? Querying { get; set; }

    /// <summary>For tests: how much background work is held, ended or not.</summary>
    internal int Tracked
    {
        get
        {
            lock (_lock) return _running.Count;
        }
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>One owner's cached state; every field under <see cref="_lock"/>.</summary>
    private sealed class Account
    {
        /// <summary>The limit was read, and the usage counted if there is one.</summary>
        public bool Known;

        public long? Limit;

        /// <summary>Null while unknown, and for an owner without a limit, who isn't counted.</summary>
        public long? Used;

        public DateTime ReadAt;

        /// <summary>When the last count failed; the write path tries again only <see cref="RecountAfter"/> later.</summary>
        public DateTime? FailedAt;

        public Task? Refresh;

        /// <summary>A recount was asked for while one ran: it runs once more, so it sees what came after its start.</summary>
        public bool Again;

        /// <summary>Counts under way, and what writes adjusted meanwhile: a count's figure gets it added, so a write that
        /// committed while it ran isn't lost (one it already saw counts twice until the next count, which is accepted).</summary>
        public int Counting;

        public long Pending;
    }

    /// <summary>The limit a stored override stands for: null the default, −1 none, else bytes (0 refuses every write
    /// that adds data, brief open point 8).</summary>
    public long? Effective(long? stored) => stored switch
    {
        null => Default,
        < 0 => null,
        _ => stored,
    };

    /// <summary>What a stored override is, in the admin's words: <c>default</c>, <c>unlimited</c> or <c>custom</c>.</summary>
    public static string Kind(long? stored) => stored switch
    {
        null => "default",
        < 0 => "unlimited",
        _ => "custom",
    };

    /// <summary>The limit of <paramref name="ownerId"/> as the database has it now; null: none.</summary>
    public async Task<long?> LimitAsync(string ownerId, CancellationToken ct)
    {
        await QueryAsync("limit", ownerId);
        var stored = await _users.Find(u => u.Id == ownerId).Project(u => u.Quota).FirstOrDefaultAsync(ct);
        if (stored is not null) OverrideSeen();
        return Effective(stored);
    }

    /// <summary>An admin set an override (or one was read): from now on the cache looks owners up.</summary>
    internal void OverrideSeen()
    {
        lock (_lock)
        {
            _overrides = true;
            _overridesReadAt = Now;
        }
    }

    /// <summary>The usage the cache holds for <paramref name="ownerId"/>, or null if it holds none.</summary>
    public long? KnownUsage(string ownerId)
    {
        lock (_lock) return _accounts.TryGetValue(ownerId, out var account) ? account.Used : null;
    }

    /// <summary>What a write or a removal changed, as far as it knows; the recount corrects the rest.</summary>
    public void Adjust(string ownerId, long delta)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(ownerId, out var account)) return;
            if (account.Used is { } used) account.Used = Math.Max(0, used + delta);
            if (account.Counting > 0) account.Pending += delta;
        }
    }

    /// <summary>Counts <paramref name="ownerId"/>'s usage again, in the background, now: a sweep freed space, or an admin
    /// changed the limit. One count runs at a time per owner; asked for during one, it runs once more.</summary>
    public void Recount(string ownerId) => _ = RecountAsync(ownerId);

    /// <summary><see cref="Recount"/>, and when it is done.</summary>
    internal Task RecountAsync(string ownerId)
    {
        lock (_lock)
        {
            var account = AccountOf(ownerId);
            if (account.Refresh is { } running)
            {
                account.Again = true;
                return running;
            }

            return Start(ownerId, account);
        }
    }

    /// <summary>Counted now, on demand (<c>df</c>, the web app's Settings), and kept as the cache's figure.</summary>
    public async Task<StorageFigure> FigureAsync(string ownerId, CancellationToken ct)
    {
        var (limit, used, byVault) = await CountAsync(ownerId, always: true, ct);
        return new StorageFigure(used!.Value, limit, byVault);
    }

    /// <summary>
    /// <see cref="FigureAsync"/> for <c>df</c>, bounded: a figure counted less than <see cref="ShownFor"/> ago is served
    /// again, and calls that meet a count under way share it, so an agent calling df in a loop costs one aggregation per
    /// owner every 30 seconds. The rows may lag behind a write by as much; the write path's check doesn't read this. A
    /// count that failed is not kept. The shared count doesn't take the caller's token: another caller may be waiting on it.
    /// </summary>
    public Task<StorageFigure> RecentFigureAsync(string ownerId, CancellationToken ct)
    {
        Task<StorageFigure> count;
        lock (_lock)
        {
            if (_shown.TryGetValue(ownerId, out var shown) && !shown.Count.IsFaulted && !shown.Count.IsCanceled
                && (!shown.Count.IsCompleted || Now - shown.StartedAt < ShownFor))
            {
                count = shown.Count;
            }
            else
            {
                // Started outside the lock: the count takes it itself.
                count = Task.Run(() => FigureAsync(ownerId, CancellationToken.None), CancellationToken.None);
                _shown[ownerId] = (count, Now);
            }
        }

        return count.WaitAsync(ct);
    }

    /// <summary>
    /// The check of a write that adds <paramref name="incoming"/> bytes to the vault of <paramref name="access"/>, from the
    /// cache alone: <see cref="QuotaExceededException"/> if it doesn't fit its owner's limit, else the owner to adjust after
    /// the commit (null: no limit can apply, or a vault without an owner, the dev token's, which is never limited). The
    /// vault's owner is checked, never the caller; the exception says whether the caller is that owner.
    /// </summary>
    internal string? Require(VaultAccess access, long incoming)
    {
        lock (_lock)
        {
            if (!MayLimit()) return null;
            if ((access.Membership?.Owner ?? OwnerOf(access.Id)) is not { } owner) return null;
            var account = AccountOf(owner);
            Freshen(owner, account);
            if (!account.Known || account.Limit is not { } limit || account.Used is not { } used) return owner;
            if (limit == 0 || used + incoming > limit) throw new QuotaExceededException(access.IsOwner);
            return owner;
        }
    }

    /// <summary>The owner's usage and limit for RFC 4331's properties, if a limit applies: the cached figure, counted
    /// first if there never was one.</summary>
    internal async Task<(long Used, long Limit)?> UsageAsync(string ownerId, CancellationToken ct)
    {
        Task? first = null;
        lock (_lock)
        {
            if (!MayLimit()) return null;
            var account = AccountOf(ownerId);
            if (!account.Known) first = account.Refresh ?? (FailedLately(account) ? null : Start(ownerId, account));
            else Freshen(ownerId, account);
        }

        if (first is not null) await first.WaitAsync(ct);
        lock (_lock) return _accounts[ownerId] is { Known: true, Limit: { } limit, Used: { } used } ? (used, limit) : null;
    }

    /// <summary>For tests: until every count and lookup started so far has ended.</summary>
    internal async Task SettledAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_lock)
            {
                _running.RemoveAll(t => t.IsCompleted);
                running = [.. _running];
            }

            if (running.Length == 0) return;
            await Task.WhenAll(running).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>Under the lock: whether a limit can apply to anyone. With a default, yes; without one, only once a user
    /// document carries an override, which is asked in the background at most every <see cref="RecountAfter"/>.</summary>
    private bool MayLimit()
    {
        if (Default is not null) return true;
        if (_overridesCheck is null && (_overridesReadAt is not { } read || read + RecountAfter <= Now)) _overridesCheck = Track(Task.Run(CheckOverridesAsync));
        return _overrides == true;
    }

    private async Task CheckOverridesAsync()
    {
        bool? found = null;
        try
        {
            await QueryAsync("overrides", "");
            found = await _users.Find(Builders<UserDocument>.Filter.Exists(u => u.Quota)).Limit(1).AnyAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Reading whether any user has a storage override failed");
        }
        finally
        {
            lock (_lock)
            {
                // One seen meanwhile (an admin's change) stands.
                if (found is { } exists && _overrides != true) _overrides = exists;
                _overridesReadAt = Now;
                _overridesCheck = null;
            }
        }
    }

    private Account AccountOf(string ownerId)
    {
        if (!_accounts.TryGetValue(ownerId, out var account)) _accounts[ownerId] = account = new Account();
        return account;
    }

    private bool FailedLately(Account account) => account.FailedAt is { } failed && failed + RecountAfter > Now;

    /// <summary>Under the lock: a count in the background if the figure is unknown or older than <see cref="RecountAfter"/>,
    /// none runs, and none failed lately.</summary>
    private void Freshen(string ownerId, Account account)
    {
        if (account.Refresh is null && !FailedLately(account) && (!account.Known || account.ReadAt + RecountAfter <= Now)) Start(ownerId, account);
    }

    /// <summary>Under the lock, with none running: the owner's limit read and her usage counted, in the background.</summary>
    private Task Start(string ownerId, Account account)
    {
        account.Again = false;
        var task = Track(Task.Run(() => RefreshAsync(ownerId, account)));
        account.Refresh = task;
        return task;
    }

    /// <summary>Under the lock: held for <see cref="SettledAsync"/>, after dropping what has ended.</summary>
    private Task Track(Task task)
    {
        _running.RemoveAll(t => t.IsCompleted);
        _running.Add(task);
        return task;
    }

    private async Task RefreshAsync(string ownerId, Account account)
    {
        try
        {
            while (true)
            {
                await CountAsync(ownerId, always: false, CancellationToken.None);
                lock (_lock)
                {
                    account.FailedAt = null;
                    if (!account.Again)
                    {
                        account.Refresh = null;
                        return;
                    }

                    account.Again = false;
                }
            }
        }
        catch (Exception e)
        {
            // Writes go on as they would without a figure; the write path tries again after RecountAfter.
            _log.LogWarning(e, "Counting the storage of user {UserId} failed", ownerId);
            lock (_lock)
            {
                account.FailedAt = Now;
                account.Refresh = null;
            }
        }
    }

    /// <summary>The limit, and the usage by the admin's aggregation unless there is no limit and <paramref name="always"/>
    /// is false; both kept in the cache, and the vaults' owner with them.</summary>
    private async Task<(long? Limit, long? Used, IReadOnlyDictionary<string, long> ByVault)> CountAsync(string ownerId, bool always, CancellationToken ct)
    {
        var started = Now;
        Account account;
        lock (_lock)
        {
            account = AccountOf(ownerId);
            if (account.Counting++ == 0) account.Pending = 0;
        }

        try
        {
            var limit = await LimitAsync(ownerId, ct);
            long? used = null;
            IReadOnlyDictionary<string, long> byVault = new Dictionary<string, long>();
            if (limit is not null || always)
            {
                await QueryAsync("count", ownerId);
                var vaults = await _catalog.OwnedVaultIdsAsync(ownerId, ct);
                var bytes = await _admin.BytesByVaultAsync(vaults, ct);
                byVault = vaults.Distinct(StringComparer.Ordinal).ToDictionary(v => v, v => bytes.GetValueOrDefault(v), StringComparer.Ordinal);
                used = byVault.Values.Sum();
            }

            lock (_lock)
            {
                foreach (var vault in byVault.Keys) _owners[vault] = ownerId;
                account.Known = true;
                account.Limit = limit;
                account.Used = limit is null ? null : used + account.Pending;
                account.ReadAt = started;
            }

            return (limit, used, byVault);
        }
        finally
        {
            lock (_lock)
            {
                if (--account.Counting == 0) account.Pending = 0;
            }
        }
    }

    /// <summary>Under the lock: the vault's owner if known, else null and a lookup in the background, unless one failed
    /// lately.</summary>
    private string? OwnerOf(string vaultId)
    {
        if (_owners.TryGetValue(vaultId, out var known)) return known;
        if (_lookupFailed.TryGetValue(vaultId, out var failed) && failed + RecountAfter > Now) return null;
        if (_resolving.Add(vaultId)) Track(Task.Run(() => ResolveAsync(vaultId)));
        return null;
    }

    private async Task ResolveAsync(string vaultId)
    {
        try
        {
            await QueryAsync("owner", vaultId);
            var slot = Builders<SlotDocument>.Filter;
            var owner = await _catalog.Slots.Find(slot.Eq(s => s.Kind, SlotDocument.User) & slot.Eq(s => s.Vault, vaultId) & slot.Exists(s => s.Owner))
                .Project(s => s.Owner).FirstOrDefaultAsync(CancellationToken.None);
            lock (_lock)
            {
                _owners[vaultId] = owner;
                _lookupFailed.Remove(vaultId);
                if (owner is not null) Freshen(owner, AccountOf(owner));
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Finding the owner of vault {VaultId} for the storage quota failed", vaultId);
            lock (_lock) _lookupFailed[vaultId] = Now;
        }
        finally
        {
            lock (_lock) _resolving.Remove(vaultId);
        }
    }

    private Task QueryAsync(string kind, string id) => Querying is { } hook ? hook(kind, id) : Task.CompletedTask;

    /// <summary>A blob sweep freed space in <paramref name="vaultId"/>: its owner, if known, is counted again.</summary>
    private void Released(string vaultId)
    {
        string? owner;
        lock (_lock) owner = _owners.GetValueOrDefault(vaultId);
        if (owner is not null) Recount(owner);
    }
}
