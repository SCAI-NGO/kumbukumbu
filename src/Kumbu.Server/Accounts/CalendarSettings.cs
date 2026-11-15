// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>A calendar's last view (calendar spec §7.3): <c>week</c> or <c>month</c>, with or without the month grid.</summary>
public sealed record CalendarViewState(string View, bool Grid)
{
    public static readonly CalendarViewState Default = new(Week, false);

    public const string Week = "week";

    public const string Month = "month";

    public static bool IsView(string? view) => view is Week or Month;
}

/// <summary>The levels of a day in her week (fixes spec §2.4).</summary>
public static class DayLevels
{
    public const string Workday = "workday";

    public const string Lighter = "lighter";

    public const string Off = "off";

    /// <summary>Seven levels, Monday to Sunday, each one of the three.</summary>
    public static bool IsWeek(IReadOnlyList<string?>? days) => days is { Count: 7 } && days.All(d => d is Workday or Lighter or Off);
}

/// <summary>
/// The user's calendar settings on her user document: her time zone (calendar spec §3) and her week (fixes spec §2.4),
/// plain, as they name no vault; and
/// her last view per calendar (§7.3), a map from vault id to view, sealed under a key derived from the user key as the
/// discreet list is (<see cref="DiscreetVaults"/>), so it tells nothing of her vaults to whoever reads the database. After
/// a rotation of the user key the views no longer open and every calendar opens in the default view again.
/// </summary>
public sealed class CalendarSettings(IMongoDatabase db, ILogger<CalendarSettings> log)
{
    private const string ViewsLabel = "kumbu/v1/calendar-views";
    private const int Attempts = 3;

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    /// <summary>For tests: runs between reading the views and writing them.</summary>
    internal Func<Task>? BeforeWrite { get; set; }

    /// <summary>The IANA id she chose; null for none, or for a user who is gone.</summary>
    public async Task<string?> ZoneAsync(string userId, CancellationToken ct) =>
        await Users.Find(u => u.Id == userId).Project(u => u.CalendarZone).FirstOrDefaultAsync(ct);

    /// <summary>Her zone and her week, as stored; nulls for none, or for a user who is gone.</summary>
    public async Task<(string? Zone, string[]? Days)> PreferencesAsync(string userId, CancellationToken ct)
    {
        var stored = await Users.Find(u => u.Id == userId).Project(u => new { u.CalendarZone, u.CalendarDays }).FirstOrDefaultAsync(ct);
        return (stored?.CalendarZone, stored?.CalendarDays);
    }

    /// <summary>Sets what is given and leaves what isn't: a zone the caller has checked
    /// (<see cref="Kumbu.Core.Calendar.Zones.Find"/>), null clearing it; a week (<see cref="DayLevels.IsWeek"/>), null clearing
    /// it. Answers both as stored afterwards.</summary>
    public async Task<(string? Zone, string[]? Days)> SetAsync(
        string userId, Kumbu.Core.Board.Optional<string?> zone, Kumbu.Core.Board.Optional<string[]?> days, CancellationToken ct)
    {
        if (days is { IsGiven: true, Value: { } week } && !DayLevels.IsWeek(week)) throw new ArgumentException("A week is seven known levels.", nameof(days));
        var u = Builders<UserDocument>.Update;
        var updates = new List<UpdateDefinition<UserDocument>>();
        if (zone.IsGiven) updates.Add(zone.Value is null ? u.Unset(d => d.CalendarZone) : u.Set(d => d.CalendarZone, zone.Value));
        if (days.IsGiven) updates.Add(days.Value is null ? u.Unset(d => d.CalendarDays) : u.Set(d => d.CalendarDays, days.Value));
        if (updates.Count == 0)
        {
            var user = await Users.Find(d => d.Id == userId).Project(d => new { d.CalendarZone, d.CalendarDays }).FirstOrDefaultAsync(ct);
            if (user is null) throw new CredentialsChangedException();
            return (user.CalendarZone, user.CalendarDays);
        }

        var written = await Users.FindOneAndUpdateAsync<UserDocument, UserDocument>(
            d => d.Id == userId,
            u.Combine(updates),
            new FindOneAndUpdateOptions<UserDocument, UserDocument>
            {
                ReturnDocument = ReturnDocument.After,
                Projection = Builders<UserDocument>.Projection.Include(d => d.CalendarZone).Include(d => d.CalendarDays),
            },
            ct);
        if (written is null) throw new CredentialsChangedException();
        return (written.CalendarZone, written.CalendarDays);
    }

    /// <summary>Her view of the calendar of <paramref name="vaultId"/>; the default (week, no grid) when she kept none or
    /// her views don't open.</summary>
    public async Task<CalendarViewState> ViewAsync(string userId, byte[] userKey, string vaultId, CancellationToken ct)
    {
        var sealedViews = await Users.Find(u => u.Id == userId).Project(u => u.CalendarViews).FirstOrDefaultAsync(ct);
        var views = OpenViews(userId, userKey, sealedViews);
        return views.TryGetValue(vaultId, out var view) && CalendarViewState.IsView(view.View) ? view : CalendarViewState.Default;
    }

    /// <summary>
    /// Keeps her view of the calendar of <paramref name="vaultId"/>. As <see cref="DiscreetVaults.SetAsync"/>: conditional
    /// on the views read (tried again up to three times, then <see cref="CalendarViewConflictException"/>) and on
    /// <paramref name="userKey"/> being the one in force (<see cref="CredentialsChangedException"/> if not). Views that
    /// don't open are dropped.
    /// </summary>
    public async Task SetViewAsync(string userId, byte[] userKey, string vaultId, CalendarViewState view, CancellationToken ct)
    {
        if (!CalendarViewState.IsView(view.View)) throw new ArgumentException($"Unknown calendar view {view.View}.", nameof(view));
        var keyId = UserCatalog.KeyId(userKey, userId);
        var f = Builders<UserDocument>.Filter;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var user = await Users.Find(u => u.Id == userId).Project(u => new { u.KeyId, u.CalendarViews }).FirstOrDefaultAsync(ct);
            if (user is null || !CryptographicOperations.FixedTimeEquals(user.KeyId, keyId)) throw new CredentialsChangedException();
            var views = OpenViews(userId, userKey, user.CalendarViews);
            views[vaultId] = view;
            if (BeforeWrite is { } before) await before();
            var unchanged = user.CalendarViews is null ? f.Eq(u => u.CalendarViews, null) : f.Eq(u => u.CalendarViews, user.CalendarViews);
            var written = await Users.UpdateOneAsync(
                f.Eq(u => u.Id, userId) & f.Eq(u => u.KeyId, keyId) & unchanged,
                Builders<UserDocument>.Update.Set(u => u.CalendarViews, SealViews(userId, userKey, views)),
                cancellationToken: ct);
            if (written.MatchedCount == 1) return;
        }

        throw new CalendarViewConflictException();
    }

    /// <summary>The views in <paramref name="sealedViews"/>; none for none, and none (logged by the exception's type) when
    /// they don't open or don't parse: after a rotation of the user key, or tampering with the database.</summary>
    private Dictionary<string, CalendarViewState> OpenViews(string userId, byte[] userKey, byte[]? sealedViews)
    {
        if (sealedViews is null) return new(StringComparer.Ordinal);
        try
        {
            var views = JsonSerializer.Deserialize<Dictionary<string, CalendarViewState>>(VaultCrypto.Open(ViewsKey(userId, userKey), sealedViews, ViewsContext(userId)));
            if (views is not null && views.Values.All(v => v is not null)) return new(views, StringComparer.Ordinal);
            log.LogWarning("The calendar views of user {UserId} do not open: {Reason}; every calendar opens in the default view", userId, nameof(JsonException));
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            log.LogWarning("The calendar views of user {UserId} do not open: {Reason}; every calendar opens in the default view", userId, e.GetType().Name);
        }

        return new(StringComparer.Ordinal);
    }

    private static byte[] SealViews(string userId, byte[] userKey, Dictionary<string, CalendarViewState> views) =>
        VaultCrypto.Seal(ViewsKey(userId, userKey), JsonSerializer.SerializeToUtf8Bytes(views), ViewsContext(userId));

    private static byte[] ViewsKey(string userId, byte[] userKey) => VaultCrypto.Derive(userKey, ViewsLabel, Encoding.UTF8.GetBytes(userId));

    private static string ViewsContext(string userId) => $"calendar-views:{userId}";
}

/// <summary>The calendar views kept changing under <see cref="CalendarSettings.SetViewAsync"/>; nothing was written.</summary>
public sealed class CalendarViewConflictException() : Exception("Your calendar views kept changing; try again.");
