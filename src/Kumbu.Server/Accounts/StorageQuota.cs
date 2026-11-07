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
/// trash's TTL, history trimmed, embeddings, a write by another process. An owner without a limit is never counted, only
/// her limit read again, so a server whose admin set none behaves as before. The check is against the usage plus what is
/// about to be written, and may be off by a write's worth under concurrency, which is accepted.
/// </para>
/// </summary>
public sealed class StorageQuota
{
    /// <summary>How old the write path lets an owner's figure get before it counts again.</summary>
    public static readonly TimeSpan RecountAfter = TimeSpan.FromMinutes(10);

    /// <summary>The stored override for "unlimited" (<see cref="UserDocument.Quota"/>); null is the default.</summary>
    public const long Unlimited = -1;

    private readonly IMongoCollection<UserDocument> _users;
    private readonly VaultCatalog _catalog;
    private readonly Admin _admin;
    private readonly TimeProvider _time;
    private readonly ILogger<StorageQuota> _log;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);

    /// <summary>Each vault's owner, once read: it never changes. A vault without one (the dev token's) is read again
    /// after <see cref="RecountAfter"/>.</summary>
    private readonly Dictionary<string, (string? Owner, DateTime At)> _owners = new(StringComparer.Ordinal);

    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
    private readonly List<Task> _running = [];

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

        public Task? Refresh;

        /// <summary>A recount was asked for while one ran: it runs once more, so it sees what came after its start.</summary>
        public bool Again;
    }

    /// <summary>The limit a stored override stands for: null the default, −1 none, else bytes (0 refuses every write
    /// that adds data, brief open point 8).</summary>
    public long? Effective(long? stored) => stored switch
    {
        null => Default,
        < 0 => null,
        _ => stored,
    };

    /// <summary>The limit of <paramref name="ownerId"/> as the database has it now; null: none.</summary>
    public async Task<long?> LimitAsync(string ownerId, CancellationToken ct) =>
        Effective(await _users.Find(u => u.Id == ownerId).Project(u => u.Quota).FirstOrDefaultAsync(ct));

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
            if (_accounts.TryGetValue(ownerId, out var account) && account.Used is { } used) account.Used = Math.Max(0, used + delta);
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
    /// The check of a write that adds <paramref name="incoming"/> bytes to the vault of <paramref name="access"/>, from the
    /// cache alone: <see cref="QuotaExceededException"/> if it doesn't fit its owner's limit, else the owner to adjust after
    /// the commit (null: a vault without one, the dev token's, which is never limited). The vault's owner is checked, never
    /// the caller; the exception says whether the caller is that owner.
    /// </summary>
    internal string? Require(VaultAccess access, long incoming)
    {
        lock (_lock)
        {
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
            var account = AccountOf(ownerId);
            if (!account.Known) first = account.Refresh ?? Start(ownerId, account);
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

    private Account AccountOf(string ownerId)
    {
        if (!_accounts.TryGetValue(ownerId, out var account)) _accounts[ownerId] = account = new Account();
        return account;
    }

    /// <summary>Under the lock: a count in the background if the figure is unknown or older than <see cref="RecountAfter"/>
    /// and none runs.</summary>
    private void Freshen(string ownerId, Account account)
    {
        if (account.Refresh is null && (!account.Known || account.ReadAt + RecountAfter <= Now)) Start(ownerId, account);
    }

    /// <summary>Under the lock, with none running: the owner's limit read and her usage counted, in the background.</summary>
    private Task Start(string ownerId, Account account)
    {
        account.Again = false;
        var task = Task.Run(() => RefreshAsync(ownerId, account));
        account.Refresh = task;
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
                account.ReadAt = Now;
                account.Refresh = null;
            }
        }
    }

    /// <summary>The limit, and the usage by the admin's aggregation unless there is no limit and <paramref name="always"/>
    /// is false; both kept in the cache, and the vaults' owner with them.</summary>
    private async Task<(long? Limit, long? Used, IReadOnlyDictionary<string, long> ByVault)> CountAsync(string ownerId, bool always, CancellationToken ct)
    {
        var started = Now;
        var limit = await LimitAsync(ownerId, ct);
        long? used = null;
        IReadOnlyDictionary<string, long> byVault = new Dictionary<string, long>();
        if (limit is not null || always)
        {
            var vaults = await _catalog.OwnedVaultIdsAsync(ownerId, ct);
            var bytes = await _admin.BytesByVaultAsync(vaults, ct);
            byVault = vaults.Distinct(StringComparer.Ordinal).ToDictionary(v => v, v => bytes.GetValueOrDefault(v), StringComparer.Ordinal);
            used = byVault.Values.Sum();
            lock (_lock)
            {
                foreach (var vault in byVault.Keys) _owners[vault] = (ownerId, started);
            }
        }

        lock (_lock)
        {
            var account = AccountOf(ownerId);
            account.Known = true;
            account.Limit = limit;
            account.Used = limit is null ? null : used;
            account.ReadAt = started;
        }

        return (limit, used, byVault);
    }

    /// <summary>Under the lock: the vault's owner if known, else null and a lookup in the background.</summary>
    private string? OwnerOf(string vaultId)
    {
        if (_owners.TryGetValue(vaultId, out var known) && (known.Owner is not null || known.At + RecountAfter > Now)) return known.Owner;
        if (_resolving.Add(vaultId)) _running.Add(Task.Run(() => ResolveAsync(vaultId)));
        return null;
    }

    private async Task ResolveAsync(string vaultId)
    {
        try
        {
            var slot = Builders<SlotDocument>.Filter;
            var owner = await _catalog.Slots.Find(slot.Eq(s => s.Kind, SlotDocument.User) & slot.Eq(s => s.Vault, vaultId) & slot.Exists(s => s.Owner))
                .Project(s => s.Owner).FirstOrDefaultAsync(CancellationToken.None);
            lock (_lock)
            {
                _owners[vaultId] = (owner, Now);
                if (owner is not null) Freshen(owner, AccountOf(owner));
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Finding the owner of vault {VaultId} for the storage quota failed", vaultId);
        }
        finally
        {
            lock (_lock) _resolving.Remove(vaultId);
        }
    }

    /// <summary>A blob sweep freed space in <paramref name="vaultId"/>: its owner, if known, is counted again.</summary>
    private void Released(string vaultId)
    {
        string? owner;
        lock (_lock) owner = _owners.GetValueOrDefault(vaultId).Owner;
        if (owner is not null) Recount(owner);
    }
}
