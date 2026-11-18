// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>
/// The user's own TensorX key (BYOK spec, "How the key is kept"), on her user document, sealed under a key derived from
/// the user key as the discreet list is (<see cref="DiscreetVaults"/>), and sealed again when the user key changes
/// (<see cref="UserKeyRotation"/>). Each vault that uses it holds a copy in its own meta; this one is what the settings
/// show and what a new vault starts with.
/// </summary>
public sealed class EmbeddingsKeys(IMongoDatabase db, ILogger<EmbeddingsKeys> log)
{
    private const string Label = "kumbu/v1/embeddings-key";

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    /// <summary>Her key; null for none, or when it doesn't open (logged by the exception's type only).</summary>
    public async Task<string?> GetAsync(string userId, byte[] userKey, CancellationToken ct)
    {
        var sealedKey = await Users.Find(u => u.Id == userId).Project(u => u.EmbeddingsKey).FirstOrDefaultAsync(ct);
        var key = Open(userId, userKey, sealedKey, out var failure);
        if (failure is not null) log.LogWarning("The embeddings key of user {UserId} does not open: {Reason}", userId, failure);
        return key;
    }

    /// <summary>Saves her key, or with null removes it; only while <paramref name="userKey"/> is the one in force
    /// (<see cref="CredentialsChangedException"/> otherwise), so a key sealed under a key a rotation just replaced is never
    /// written.</summary>
    public async Task SetAsync(string userId, byte[] userKey, string? key, CancellationToken ct)
    {
        var f = Builders<UserDocument>.Filter;
        var u = Builders<UserDocument>.Update;
        var written = await Users.UpdateOneAsync(
            f.Eq(d => d.Id, userId) & f.Eq(d => d.KeyId, UserCatalog.KeyId(userKey, userId)),
            key is null ? u.Unset(d => d.EmbeddingsKey) : u.Set(d => d.EmbeddingsKey, Seal(userId, userKey, key)),
            cancellationToken: ct);
        if (written.MatchedCount == 0) throw new CredentialsChangedException();
    }

    /// <summary>The key in <paramref name="sealedKey"/>; null for none, and null with <paramref name="failure"/> naming the
    /// exception's type when it doesn't open (only after tampering with the database).</summary>
    internal static string? Open(string userId, byte[] userKey, byte[]? sealedKey, out string? failure)
    {
        failure = null;
        if (sealedKey is null) return null;
        try
        {
            return Encoding.UTF8.GetString(VaultCrypto.Open(SealKey(userId, userKey), sealedKey, Context(userId)));
        }
        catch (CryptographicException e)
        {
            failure = e.GetType().Name;
            return null;
        }
    }

    internal static byte[] Seal(string userId, byte[] userKey, string key) =>
        VaultCrypto.Seal(SealKey(userId, userKey), Encoding.UTF8.GetBytes(key), Context(userId));

    private static byte[] SealKey(string userId, byte[] userKey) => VaultCrypto.Derive(userKey, Label, Encoding.UTF8.GetBytes(userId));

    private static string Context(string userId) => $"embeddings-key:{userId}";
}
