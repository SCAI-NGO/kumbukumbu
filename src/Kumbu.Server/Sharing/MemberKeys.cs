// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>
/// Every user's P-256 keypair (collab design §5.1), to which the member slots of the vaults shared with them are
/// wrapped: the public key in plain on the user document, the private key (PKCS#8) sealed under a key from the user
/// key, with its public key's hash in the AAD, so a public key swapped in the database opens nothing. Created lazily
/// wherever the user key is at hand, once.
/// </summary>
public sealed class MemberKeys(IMongoDatabase db)
{
    private const string PrivateKeyLabel = "kumbu/v1/user-private-key";

    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    /// <summary>For tests: called after the user document was read without a keypair, before the keypair is written.</summary>
    internal Func<Task>? BeforeCreate { get; set; }

    /// <summary>For tests: called when this call's keypair was the one written.</summary>
    internal Action<string>? Created { get; set; }

    /// <summary>
    /// The user's document, with a keypair: the one they have, or a new one, written only if they still have none and
    /// <paramref name="userKey"/> is still their user key (the key id): of two sign-ins at once one writes, and a
    /// rotation of the user key in between makes it write nothing. Null if the user is gone; a document without a
    /// keypair if the user key isn't current (nothing is sealed under a key that no longer opens anything).
    /// </summary>
    public async Task<UserDocument?> EnsureAsync(string userId, byte[] userKey, CancellationToken ct)
    {
        var user = await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
        if (user is null || user.PublicKey is not null) return user;
        if (BeforeCreate is { } hook) await hook();

        var keyId = UserCatalog.KeyId(userKey, userId);
        var (publicKey, privateKey) = NewKeyPair(userId, userKey);
        var f = Builders<UserDocument>.Filter;
        var created = await Users.UpdateOneAsync(
            f.Eq(u => u.Id, userId) & f.Eq(u => u.KeyId, keyId) & f.Exists(u => u.PublicKey, false),
            Builders<UserDocument>.Update.Set(u => u.PublicKey, publicKey).Set(u => u.PrivateKey, privateKey),
            cancellationToken: ct);
        if (created.ModifiedCount == 1) Created?.Invoke(userId);
        return await Users.Find(u => u.Id == userId).FirstOrDefaultAsync(ct);
    }

    /// <summary>A new keypair for <paramref name="userId"/>: the public key, and the private key sealed under <paramref name="userKey"/>.</summary>
    internal static (byte[] PublicKey, byte[] SealedPrivateKey) NewKeyPair(string userId, byte[] userKey)
    {
        using var key = MemberSlotCrypto.NewKeyPair();
        var publicKey = MemberSlotCrypto.PublicOf(key);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            return (publicKey, VaultCrypto.Seal(Kek(userKey), pkcs8, PrivateKeyContext(userId, publicKey)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// The user's private key, opened with <paramref name="userKey"/>; <see cref="CryptographicException"/> if they have
    /// no keypair, it doesn't open (another user key, or a public key changed in the database), or it isn't the private
    /// key of the public key beside it. The caller disposes it.
    /// </summary>
    public static ECDiffieHellman OpenPrivate(UserDocument user, byte[] userKey)
    {
        if (user.PublicKey is not { } publicKey || user.PrivateKey is not { } sealedKey) throw new CryptographicException("The user has no keypair.");
        MemberSlotCrypto.RequireOnCurve(publicKey);
        var pkcs8 = VaultCrypto.Open(Kek(userKey), sealedKey, PrivateKeyContext(user.Id, publicKey));
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (!CryptographicOperations.FixedTimeEquals(PublicFromPrivate(key), publicKey)) throw new CryptographicException("The private key is not the public key's.");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>The public key of a private key: the only source of the key a member slot is wrapped to at a grant
    /// (collab design §5.4), never the user document's plain <c>publicKey</c>.</summary>
    public static byte[] PublicFromPrivate(ECDiffieHellman privateKey) => MemberSlotCrypto.PublicOf(privateKey);

    private static byte[] Kek(byte[] userKey) => VaultCrypto.Derive(userKey, PrivateKeyLabel, []);

    internal static string PrivateKeyContext(string userId, byte[] publicKey) => $"user-private:{userId}:{Convert.ToHexStringLower(SHA256.HashData(publicKey))}";
}
