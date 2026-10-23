// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Kumbu.Core.Crypto;

/// <summary>
/// The cryptographic building blocks, all from .NET itself: AES-256-GCM for sealing, HKDF-SHA256 for
/// deriving keys from high-entropy secrets, HMAC-SHA256 for path ids.
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    /// <summary>The first byte of every sealed blob, so the format can change later.</summary>
    private const byte Format = 1;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>
    /// Encrypts and authenticates <paramref name="plaintext"/>. <paramref name="context"/> is bound as
    /// associated data: a blob only opens in the context it was sealed for, so a sealed note can't be copied
    /// into another document or vault. Layout: format | nonce | ciphertext | tag.
    /// </summary>
    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string context)
    {
        var sealedBlob = new byte[1 + NonceSize + plaintext.Length + TagSize];
        sealedBlob[0] = Format;
        var nonce = sealedBlob.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, sealedBlob.AsSpan(1 + NonceSize, plaintext.Length), sealedBlob.AsSpan(1 + NonceSize + plaintext.Length), Encoding.UTF8.GetBytes(context));
        return sealedBlob;
    }

    /// <summary>Decrypts a blob from <see cref="Seal"/>; throws <see cref="CryptographicException"/> if the key
    /// or context is wrong or a single bit was changed.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sealedBlob, string context)
    {
        if (sealedBlob.Length < 1 + NonceSize + TagSize || sealedBlob[0] != Format)
        {
            throw new CryptographicException("Not a sealed blob of a known format.");
        }

        var length = sealedBlob.Length - 1 - NonceSize - TagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBlob.Slice(1, NonceSize), sealedBlob.Slice(1 + NonceSize, length), sealedBlob[^TagSize..], plaintext, Encoding.UTF8.GetBytes(context));
        return plaintext;
    }

    /// <summary>HKDF-SHA256. Fit for high-entropy input only (random keys and tokens), never for passwords.</summary>
    public static byte[] Derive(ReadOnlySpan<byte> secret, string label, ReadOnlySpan<byte> salt)
    {
        var key = new byte[KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, key, salt, Encoding.UTF8.GetBytes(label));
        return key;
    }

    public static byte[] Derive(string secret, string label, ReadOnlySpan<byte> salt) =>
        Derive(Encoding.UTF8.GetBytes(secret), label, salt);

    public static byte[] Mac(ReadOnlySpan<byte> key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
}
