// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumbu.Core.Crypto;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>A favourite as kept: a command by its id (<see cref="Favourites.Command"/>), or a vault, its board, calendar or
/// gallery, or a note in it by its vault's id and, for a note, its path inside the vault (<c>projects/plan.md</c>).</summary>
public sealed record StoredFavourite(string Kind, string? CommandId, string? VaultId, string? Path, DateTimeOffset Added)
{
    /// <summary>Whether <paramref name="other"/> names the same thing, whenever either was added.</summary>
    public bool SameAs(StoredFavourite other) =>
        Kind == other.Kind && CommandId == other.CommandId && VaultId == other.VaultId && Path == other.Path;
}

/// <summary>
/// A person's favourites on her user document (command palette spec, "Favourites: storage"), in two parts by the rule of
/// the calendar's settings (<see cref="CalendarSettings"/>): her commands plain, as they name no vault, and so kept over a
/// rotation of the user key; and what is bound to a vault sealed under a key derived from the user key, so it tells nothing
/// of her vaults to whoever reads the database, and after a rotation no longer opens and is gone.
/// </summary>
public sealed class Favourites(IMongoDatabase db, TimeProvider time, ILogger<Favourites> log)
{
    public const string Command = "command";

    /// <summary>The kinds bound to a vault; <see cref="Note"/> also takes a path.</summary>
    public static readonly IReadOnlySet<string> VaultKinds = new HashSet<string>(StringComparer.Ordinal) { "vault", "board", "calendar", "gallery", Note };

    public const string Note = "note";

    /// <summary>How many she keeps, hidden ones counted: a person's favourites are few.</summary>
    public const int Cap = 200;

    private const string SealedLabel = "kumbu/v1/favourites";
    private const int Attempts = 3;

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    /// <summary>For tests: runs between reading the favourites and writing them.</summary>
    internal Func<Task>? BeforeWrite { get; set; }

    /// <summary>All her favourites, in the order added; none for a user who is gone, and none bound to a vault when they don't open.</summary>
    public async Task<IReadOnlyList<StoredFavourite>> ReadAsync(string userId, byte[] userKey, CancellationToken ct)
    {
        var user = await Users.Find(u => u.Id == userId).Project(u => new { u.Favourites, u.FavouritesSealed }).FirstOrDefaultAsync(ct);
        return user is null ? [] : Ordered(OpenCommands(userId, user.Favourites), OpenSealed(userId, userKey, user.FavouritesSealed));
    }

    /// <summary>
    /// Puts <paramref name="visible"/> in place of what a session that lists <paramref name="listedVaultIds"/> sees: her
    /// commands and the entries of those vaults. The entries of every other vault (a hidden discreet vault's, or one that is
    /// gone) stay as they were: nothing is dropped as stale. An entry she already had keeps its <c>Added</c>; a new one is
    /// added now. At most <see cref="Cap"/> in all, else <see cref="FavouritesFullException"/>. As
    /// <see cref="CalendarSettings.SetViewAsync"/>: conditional on what was read (tried again up to three times, then
    /// <see cref="FavouritesConflictException"/>) and on <paramref name="userKey"/> being the one in force
    /// (<see cref="CredentialsChangedException"/> if not). Answers all of them as written, in the order added.
    /// </summary>
    public async Task<IReadOnlyList<StoredFavourite>> ReplaceVisibleAsync(
        string userId, byte[] userKey, IReadOnlySet<string> listedVaultIds, IReadOnlyList<StoredFavourite> visible, CancellationToken ct)
    {
        var keyId = UserCatalog.KeyId(userKey, userId);
        var f = Builders<UserDocument>.Filter;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var user = await Users.Find(u => u.Id == userId).Project(u => new { u.KeyId, u.Favourites, u.FavouritesSealed }).FirstOrDefaultAsync(ct);
            if (user is null || !CryptographicOperations.FixedTimeEquals(user.KeyId, keyId)) throw new CredentialsChangedException();
            var before = OpenCommands(userId, user.Favourites).Concat(OpenSealed(userId, userKey, user.FavouritesSealed)).ToList();
            var kept = before.Where(b => b.VaultId is { } id && !listedVaultIds.Contains(id));
            var now = time.GetUtcNow();
            var given = new List<StoredFavourite>();
            foreach (var v in visible)
            {
                if (given.Any(v.SameAs)) continue;
                given.Add(v with { Added = before.FirstOrDefault(v.SameAs)?.Added ?? now });
            }

            // In the order added; where several were added at once, kept ones where they were and new ones as given.
            var after = kept.Concat(given).OrderBy(a => a.Added).ThenBy(a => IndexIn(before, a)).ToList();
            if (after.Count > Cap) throw new FavouritesFullException();
            var commands = after.Where(a => a.Kind == Command).ToList();
            var bound = after.Where(a => a.Kind != Command).ToList();

            if (BeforeWrite is { } hook) await hook();
            var written = await Users.UpdateOneAsync(
                f.Eq(u => u.Id, userId) & f.Eq(u => u.KeyId, keyId)
                    & f.Eq(u => u.Favourites, user.Favourites) & f.Eq(u => u.FavouritesSealed, user.FavouritesSealed),
                Builders<UserDocument>.Update
                    .Set(u => u.Favourites, [.. commands.Select(c => $"{c.CommandId}@{c.Added.UtcTicks.ToString(CultureInfo.InvariantCulture)}")])
                    .Set(u => u.FavouritesSealed, Seal(userId, userKey, bound)),
                cancellationToken: ct);
            if (written.MatchedCount == 1) return Ordered(commands, bound);
        }

        throw new FavouritesConflictException();
    }

    private static int IndexIn(List<StoredFavourite> before, StoredFavourite entry) =>
        before.FindIndex(entry.SameAs) is var i and >= 0 ? i : int.MaxValue;

    /// <summary>Commands first where both were added at once, each part in its stored order.</summary>
    private static List<StoredFavourite> Ordered(IEnumerable<StoredFavourite> commands, IEnumerable<StoredFavourite> bound) =>
        [.. commands.Concat(bound).OrderBy(e => e.Added)];

    /// <summary>The plain commands, <c>&lt;id&gt;@&lt;ticks&gt;</c> as <see cref="ReplaceVisibleAsync"/> writes them; one that
    /// doesn't parse, as only tampering with the database leaves it, is left out and logged.</summary>
    private List<StoredFavourite> OpenCommands(string userId, string[]? stored)
    {
        var commands = new List<StoredFavourite>();
        foreach (var s in stored ?? [])
        {
            var at = s.LastIndexOf('@');
            if (at > 0 && long.TryParse(s.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks)
                commands.Add(new StoredFavourite(Command, s[..at], null, null, new DateTimeOffset(ticks, TimeSpan.Zero)));
            else
                log.LogWarning("A favourite command of user {UserId} does not parse; it is left out", userId);
        }

        return commands;
    }

    /// <summary>The entries bound to a vault in <paramref name="sealedList"/>; none for none, and none (logged by the
    /// exception's type) when they don't open or don't parse: after a rotation of the user key, or tampering with the database.</summary>
    private List<StoredFavourite> OpenSealed(string userId, byte[] userKey, byte[]? sealedList)
    {
        if (sealedList is null) return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<SealedFavourite>>(VaultCrypto.Open(Key(userId, userKey), sealedList, Context(userId)));
            if (entries is not null && entries.All(e => e is not null))
                return [.. entries.Select(e => new StoredFavourite(e.Kind, null, e.VaultId, e.Path, e.Added))];
            log.LogWarning("The favourites of user {UserId} do not open: {Reason}; those bound to a vault are gone", userId, nameof(JsonException));
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            log.LogWarning("The favourites of user {UserId} do not open: {Reason}; those bound to a vault are gone", userId, e.GetType().Name);
        }

        return [];
    }

    private static byte[] Seal(string userId, byte[] userKey, List<StoredFavourite> bound) =>
        VaultCrypto.Seal(
            Key(userId, userKey),
            JsonSerializer.SerializeToUtf8Bytes(bound.Select(b => new SealedFavourite(b.Kind, b.VaultId!, b.Path, b.Added)).ToList()),
            Context(userId));

    private static byte[] Key(string userId, byte[] userKey) => VaultCrypto.Derive(userKey, SealedLabel, Encoding.UTF8.GetBytes(userId));

    private static string Context(string userId) => $"favourites:{userId}";

    private sealed record SealedFavourite(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("vaultId")] string VaultId,
        [property: JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path,
        [property: JsonPropertyName("added")] DateTimeOffset Added);
}

/// <summary>A write would keep more than <see cref="Favourites.Cap"/> favourites; nothing was written. No count beside the
/// cap: what a session hides must leave no trace.</summary>
public sealed class FavouritesFullException() : Exception($"Your favourites are full ({Favourites.Cap}).");

/// <summary>The favourites kept changing under <see cref="Favourites.ReplaceVisibleAsync"/>; nothing was written.</summary>
public sealed class FavouritesConflictException() : Exception("Your favourites kept changing; try again.");
