// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kumbu.Core.Crypto;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>What a PIN opens: nothing, or the reveal of the discreet vaults. An enum so that a second PIN can be added
/// without changing the callers.</summary>
public enum PinMatch
{
    None,
    Reveal,
}

/// <summary>
/// The user's discreet vaults (discreet vaults design): their ids, sealed on the user document under a key derived from
/// the user key, and the PIN that shows them in the web app, kept as an Argon2id verifier with its own salt. Neither is
/// ever a key nor decrypts anything; logs name ids and exception types only.
/// </summary>
public sealed partial class DiscreetVaults(IMongoDatabase db, Argon2Kdf kdf, ILogger<DiscreetVaults> log)
{
    /// <summary>The answer to every PIN that doesn't reveal, whether or not she has a PIN or discreet vaults.</summary>
    public const string NoneSentence = "You have no discreet vaults.";

    private const string PinSentence = "A PIN has 4 to 6 digits.";
    private const string ListLabel = "kumbu/v1/discreet-vaults";
    private const int Attempts = 3;

    /// <summary>For a user without a PIN, and for a malformed PIN: the derivation still runs, against these.</summary>
    private static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(Argon2Kdf.SaltBytes);
    private static readonly byte[] DummyVerifier = RandomNumberGenerator.GetBytes(Argon2Kdf.OutputBytes);

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    /// <summary>For tests: runs between reading the list and writing it.</summary>
    internal Func<Task>? BeforeWrite { get; set; }

    /// <summary>Exactly 4 to 6 ASCII digits.</summary>
    public static bool IsPin(string? pin) => pin is not null && PinPattern().IsMatch(pin);

    /// <summary>The ids in <paramref name="sealedList"/>: none for no list, null when it doesn't open or doesn't parse
    /// (only tampering with the database can cause it; the caller then counts every vault as discreet).</summary>
    internal static IReadOnlySet<string>? OpenList(string userId, byte[] userKey, byte[]? sealedList)
    {
        if (sealedList is null) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(VaultCrypto.Open(ListKey(userId, userKey), sealedList, ListContext(userId)));
            return ids is null || ids.Any(id => id is null) ? null : ids.ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return null;
        }
    }

    internal static byte[] SealList(string userId, byte[] userKey, IEnumerable<string> ids) =>
        VaultCrypto.Seal(
            ListKey(userId, userKey),
            JsonSerializer.SerializeToUtf8Bytes(ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            ListContext(userId));

    /// <summary>
    /// Marks the vault <paramref name="vaultId"/> discreet or not. The write is conditional on the list read (so a
    /// concurrent mark is never lost: tried again up to three times, then <see cref="InvalidOperationException"/>) and on
    /// <paramref name="userKey"/> being the one in force (<see cref="CredentialsChangedException"/> if not, so a list is
    /// never sealed under an old key). A list that doesn't open starts from <paramref name="allIds"/>.
    /// </summary>
    public async Task SetAsync(string userId, byte[] userKey, string vaultId, bool discreet, IReadOnlyCollection<string> allIds, CancellationToken ct)
    {
        var keyId = UserCatalog.KeyId(userKey, userId);
        var f = Builders<UserDocument>.Filter;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var user = await Users.Find(u => u.Id == userId).Project(u => new { u.KeyId, u.DiscreetVaults }).FirstOrDefaultAsync(ct);
            if (user is null || !CryptographicOperations.FixedTimeEquals(user.KeyId, keyId)) throw new CredentialsChangedException();
            var ids = OpenList(userId, userKey, user.DiscreetVaults)?.ToHashSet(StringComparer.Ordinal);
            if (ids is null)
            {
                log.LogWarning("The discreet vaults of user {UserId} could not be read; every vault counts as discreet", userId);
                ids = allIds.ToHashSet(StringComparer.Ordinal);
            }

            if (discreet) ids.Add(vaultId);
            else ids.Remove(vaultId);
            if (BeforeWrite is { } before) await before();
            var unchanged = user.DiscreetVaults is null ? f.Exists(u => u.DiscreetVaults, false) : f.Eq(u => u.DiscreetVaults, user.DiscreetVaults);
            var written = await Users.UpdateOneAsync(
                f.Eq(u => u.Id, userId) & f.Eq(u => u.KeyId, keyId) & unchanged,
                Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, SealList(userId, userKey, ids)),
                cancellationToken: ct);
            if (written.MatchedCount == 1) return;
        }

        throw new InvalidOperationException("The discreet vaults kept changing; try again.");
    }

    /// <summary>Sets the PIN, replacing any earlier one; <see cref="ArgumentException"/> unless it has 4 to 6 digits.</summary>
    public async Task SetPinAsync(string userId, string pin, CancellationToken ct)
    {
        if (!IsPin(pin)) throw new ArgumentException(PinSentence);
        var salt = RandomNumberGenerator.GetBytes(Argon2Kdf.SaltBytes);
        var verifier = await kdf.DeriveAsync(pin, salt, ct);
        await Users.UpdateOneAsync(
            u => u.Id == userId,
            Builders<UserDocument>.Update.Set(u => u.DiscreetPin, new PinVerifierDocument { Salt = salt, Verifier = verifier }),
            cancellationToken: ct);
    }

    /// <summary>Whether <paramref name="pin"/> is hers. Exactly one derivation in every case (no PIN set, a malformed
    /// one, a wrong one, the right one), so timing tells nothing.</summary>
    public async Task<PinMatch> CheckPinAsync(string userId, string pin, CancellationToken ct)
    {
        var stored = await Users.Find(u => u.Id == userId).Project(u => u.DiscreetPin).FirstOrDefaultAsync(ct);
        var wellFormed = IsPin(pin);
        var salt = wellFormed && stored is not null ? stored.Salt : DummySalt;
        var expected = wellFormed && stored is not null ? stored.Verifier : DummyVerifier;
        var derived = await kdf.DeriveAsync(wellFormed ? pin : "000000", salt, ct);
        return wellFormed && stored is not null && CryptographicOperations.FixedTimeEquals(derived, expected) ? PinMatch.Reveal : PinMatch.None;
    }

    private static byte[] ListKey(string userId, byte[] userKey) => VaultCrypto.Derive(userKey, ListLabel, Encoding.UTF8.GetBytes(userId));

    private static string ListContext(string userId) => $"discreet-vaults:{userId}";

    [GeneratedRegex(@"\A[0-9]{4,6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PinPattern();
}
