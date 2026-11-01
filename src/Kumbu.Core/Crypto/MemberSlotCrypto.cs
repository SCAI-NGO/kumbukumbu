// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using System.Security.Cryptography;

namespace Kumbu.Core.Crypto;

/// <summary>
/// A vault key wrapped to a member's public key (collab design §5.2), as RFC 9180's DHKEM(P-256, HKDF-SHA256) with
/// AES-256-GCM: an ephemeral P-256 key, ECDH with the recipient's public key, the raw x-coordinate of the shared point
/// as input to <c>HKDF-SHA256(info "kumbu/v1/member-slot" ‖ alg, salt = ephemeral public ‖ recipient public)</c>, and
/// the vault key sealed under the result with the caller's AAD ‖ alg. Layout: <c>alg (1) | ephemeral public (65) |
/// sealed</c>. Public keys are uncompressed SEC1 points and are checked to lie on the curve wherever they are used.
/// </summary>
public static class MemberSlotCrypto
{
    /// <summary>The algorithm byte of P-256; room for a hybrid with a post-quantum KEM later.</summary>
    public const byte P256 = 1;

    /// <summary>An uncompressed SEC1 point of P-256: <c>04 | x (32) | y (32)</c>.</summary>
    public const int PublicKeySize = 65;

    private const int CoordinateSize = 32;
    private const string Info = "kumbu/v1/member-slot";

    // P-256 (SEC 2, FIPS 186-4): y² = x³ − 3x + b over the prime field p. Its cofactor is 1, so a point on the curve
    // is in the group.
    private static readonly BigInteger Prime = BigInteger.Parse("0ffffffff00000001000000000000000000000000ffffffffffffffffffffffff", System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger B = BigInteger.Parse("05ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b", System.Globalization.NumberStyles.HexNumber);

    public static ECDiffieHellman NewKeyPair() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The public key of <paramref name="key"/>, uncompressed SEC1; <see cref="CryptographicException"/> if it isn't a P-256 key.</summary>
    public static byte[] PublicOf(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        if (p.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value || p.Q.X?.Length != CoordinateSize || p.Q.Y?.Length != CoordinateSize)
        {
            throw new CryptographicException("Not a P-256 key.");
        }

        return [0x04, .. p.Q.X, .. p.Q.Y];
    }

    /// <summary><see cref="CryptographicException"/> unless <paramref name="publicKey"/> is an uncompressed SEC1 point on P-256.</summary>
    public static void RequireOnCurve(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeySize || publicKey[0] != 0x04) throw new CryptographicException("Not an uncompressed P-256 public key.");
        var x = new BigInteger(publicKey.Slice(1, CoordinateSize), isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(publicKey.Slice(1 + CoordinateSize, CoordinateSize), isUnsigned: true, isBigEndian: true);
        if (x >= Prime || y >= Prime || BigInteger.ModPow(y, 2, Prime) != Mod((BigInteger.ModPow(x, 3, Prime) - (3 * x) + B), Prime))
        {
            throw new CryptographicException("The public key is not a point on P-256.");
        }
    }

    /// <summary>Wraps <paramref name="vaultKey"/> to <paramref name="recipientPublic"/>, bound to <paramref name="aad"/>.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> recipientPublic, byte[] vaultKey, string aad)
    {
        using var recipient = ImportPublic(recipientPublic);
        using var ephemeral = NewKeyPair();
        var ephemeralPublic = PublicOf(ephemeral);
        var kek = Kek(ephemeral, recipient.PublicKey, ephemeralPublic, recipientPublic);
        try
        {
            return [P256, .. ephemeralPublic, .. VaultCrypto.Seal(kek, vaultKey, Aad(aad))];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>The vault key a slot from <see cref="Wrap"/> holds; <see cref="CryptographicException"/> for another
    /// private key, another AAD, an unknown algorithm, an ephemeral key off the curve or any changed bit.</summary>
    public static byte[] Unwrap(ECDiffieHellman recipientPrivate, byte[] slot, string aad)
    {
        if (slot.Length < 1 + PublicKeySize || slot[0] != P256) throw new CryptographicException("Not a member slot of a known algorithm.");
        var ephemeralPublic = slot.AsSpan(1, PublicKeySize);
        using var ephemeral = ImportPublic(ephemeralPublic);
        var kek = Kek(recipientPrivate, ephemeral.PublicKey, ephemeralPublic, PublicOf(recipientPrivate));
        try
        {
            return VaultCrypto.Open(kek, slot.AsSpan(1 + PublicKeySize), Aad(aad));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>A public key as a key object, after <see cref="RequireOnCurve"/> (OpenSSL checks it on import as well).</summary>
    private static ECDiffieHellman ImportPublic(ReadOnlySpan<byte> publicKey)
    {
        RequireOnCurve(publicKey);
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey.Slice(1, CoordinateSize).ToArray(), Y = publicKey.Slice(1 + CoordinateSize, CoordinateSize).ToArray() },
        };
        parameters.Validate();
        var key = ECDiffieHellman.Create();
        key.ImportParameters(parameters);
        return key;
    }

    /// <summary>HKDF over the shared point's raw x-coordinate; the algorithm byte ends the info.</summary>
    private static byte[] Kek(ECDiffieHellman own, ECDiffieHellmanPublicKey other, ReadOnlySpan<byte> ephemeralPublic, ReadOnlySpan<byte> recipientPublic)
    {
        var ikm = own.DeriveRawSecretAgreement(other);
        try
        {
            return VaultCrypto.Derive(ikm, Info + (char)P256, [.. ephemeralPublic, .. recipientPublic]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }

    /// <summary>The AAD with the algorithm byte at its end: a char below 0x80 is that very byte in UTF-8.</summary>
    private static string Aad(string aad) => aad + (char)P256;

    private static BigInteger Mod(BigInteger value, BigInteger m) => ((value % m) + m) % m;
}
