// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;

namespace Kumbu.Core.Tests.Crypto;

public class MemberSlotCryptoTests
{
    private const string Aad = "slot:v1:member:m1";

    private static readonly byte[] VaultKey = VaultCrypto.NewKey();

    [Fact]
    public void A_wrapped_vault_key_unwraps_with_the_recipients_private_key()
    {
        using var bob = MemberSlotCrypto.NewKeyPair();
        var slot = MemberSlotCrypto.Wrap(MemberSlotCrypto.PublicOf(bob), VaultKey, Aad);

        Assert.Equal(MemberSlotCrypto.P256, slot[0]);
        Assert.Equal(0x04, slot[1]);
        Assert.Equal(1 + 65 + 1 + 12 + 32 + 16, slot.Length);
        Assert.Equal(VaultKey, MemberSlotCrypto.Unwrap(bob, slot, Aad));
        // A fresh ephemeral key every time.
        Assert.NotEqual(slot.AsSpan(1, 65).ToArray(), MemberSlotCrypto.Wrap(MemberSlotCrypto.PublicOf(bob), VaultKey, Aad).AsSpan(1, 65).ToArray());
    }

    [Fact]
    public void Another_private_key_opens_nothing()
    {
        using var bob = MemberSlotCrypto.NewKeyPair();
        using var carol = MemberSlotCrypto.NewKeyPair();
        var slot = MemberSlotCrypto.Wrap(MemberSlotCrypto.PublicOf(bob), VaultKey, Aad);

        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(carol, slot, Aad));
    }

    [Fact]
    public void An_off_curve_public_key_is_refused_on_wrap()
    {
        using var bob = MemberSlotCrypto.NewKeyPair();
        var offCurve = YPlusOne(MemberSlotCrypto.PublicOf(bob));

        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.RequireOnCurve(offCurve));
        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Wrap(offCurve, VaultKey, Aad));
    }

    [Fact]
    public void An_off_curve_ephemeral_key_is_refused_on_unwrap()
    {
        using var bob = MemberSlotCrypto.NewKeyPair();
        var slot = MemberSlotCrypto.Wrap(MemberSlotCrypto.PublicOf(bob), VaultKey, Aad);
        YPlusOne(slot.AsSpan(1, 65));

        var e = Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(bob, slot, Aad));
        Assert.Contains("not a point on P-256", e.Message);
    }

    [Fact]
    public void A_changed_algorithm_byte_opens_nothing()
    {
        using var bob = MemberSlotCrypto.NewKeyPair();
        var slot = MemberSlotCrypto.Wrap(MemberSlotCrypto.PublicOf(bob), VaultKey, Aad);
        foreach (var alg in new byte[] { 0, 2, 0xff })
        {
            var changed = (byte[])slot.Clone();
            changed[0] = alg;
            Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(bob, changed, Aad));
        }

        // The byte is part of both the HKDF info and the AAD: opened by hand, the slot opens only with it in both.
        Assert.Equal(VaultKey, OpenByHand(bob, slot, info: "kumbu/v1/member-slot\u0001", aad: Aad + "\u0001"));
        Assert.ThrowsAny<CryptographicException>(() => OpenByHand(bob, slot, info: "kumbu/v1/member-slot", aad: Aad + "\u0001"));
        Assert.ThrowsAny<CryptographicException>(() => OpenByHand(bob, slot, info: "kumbu/v1/member-slot\u0001", aad: Aad));
    }

    [Fact]
    public void The_kek_comes_from_the_raw_x_coordinate_salted_with_both_public_keys()
    {
        using var bob = MemberSlotCrypto.NewKeyPair();
        var slot = MemberSlotCrypto.Wrap(MemberSlotCrypto.PublicOf(bob), VaultKey, Aad);

        Assert.Equal(VaultKey, OpenByHand(bob, slot, "kumbu/v1/member-slot\u0001", Aad + "\u0001"));
        // Salt without the recipient's public key.
        Assert.ThrowsAny<CryptographicException>(() => OpenByHand(bob, slot, "kumbu/v1/member-slot\u0001", Aad + "\u0001", withRecipient: false));
        // SHA-256 of the shared secret instead of the raw x-coordinate.
        Assert.ThrowsAny<CryptographicException>(() => OpenByHand(bob, slot, "kumbu/v1/member-slot\u0001", Aad + "\u0001", hashed: true));
    }

    [Fact]
    public void A_fixed_test_vector_unwraps()
    {
        // Computed once with this construction; any change to it (IKM, info, salt, AAD, layout) breaks this.
        using var bob = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromHexString(VectorPrivate),
            Q = new ECPoint { X = Convert.FromHexString(VectorPublic[2..66]), Y = Convert.FromHexString(VectorPublic[66..]) },
        });

        Assert.Equal(VectorPublic, Convert.ToHexStringLower(MemberSlotCrypto.PublicOf(bob)));
        Assert.Equal(VectorVaultKey, Convert.ToHexStringLower(MemberSlotCrypto.Unwrap(bob, Convert.FromHexString(VectorSlot), Aad)));
    }

    [Fact]
    public void The_aad_binds_the_vault_and_the_membership()
    {
        using var bob = MemberSlotCrypto.NewKeyPair();
        var slot = MemberSlotCrypto.Wrap(MemberSlotCrypto.PublicOf(bob), VaultKey, "slot:v1:member:m1");

        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(bob, slot, "slot:v2:member:m1"));
        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Unwrap(bob, slot, "slot:v1:member:m2"));
        Assert.Equal(VaultKey, MemberSlotCrypto.Unwrap(bob, slot, "slot:v1:member:m1"));
    }

    [Fact]
    public void A_compressed_or_short_public_key_is_refused()
    {
        using var bob = MemberSlotCrypto.NewKeyPair();
        var full = MemberSlotCrypto.PublicOf(bob);
        var compressed = new byte[33];
        compressed[0] = (byte)(2 + (full[^1] & 1));
        full.AsSpan(1, 32).CopyTo(compressed.AsSpan(1));
        var wrongPrefix = (byte)0x06;

        foreach (var key in new[] { compressed, full[..64], full[1..], [], [wrongPrefix, .. full[1..]], [.. full, 0] })
        {
            Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.RequireOnCurve(key));
            Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.Wrap(key, VaultKey, Aad));
        }

        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        Assert.ThrowsAny<CryptographicException>(() => MemberSlotCrypto.PublicOf(p384));
    }

    /// <summary>The slot opened step by step as design §5.2 describes it, independently of <see cref="MemberSlotCrypto"/>.</summary>
    private static byte[] OpenByHand(ECDiffieHellman recipient, byte[] slot, string info, string aad, bool withRecipient = true, bool hashed = false)
    {
        var ephemeralPublic = slot.AsSpan(1, 65).ToArray();
        using var ephemeral = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = ephemeralPublic[1..33], Y = ephemeralPublic[33..] },
        });
        var p = recipient.ExportParameters(false);
        byte[] recipientPublic = [0x04, .. p.Q.X!, .. p.Q.Y!];
        var ikm = hashed ? recipient.DeriveKeyFromHash(ephemeral.PublicKey, HashAlgorithmName.SHA256) : recipient.DeriveRawSecretAgreement(ephemeral.PublicKey);
        var kek = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, kek, withRecipient ? [.. ephemeralPublic, .. recipientPublic] : ephemeralPublic, Encoding.UTF8.GetBytes(info));
        return VaultCrypto.Open(kek, slot.AsSpan(66), aad);
    }

    /// <summary>Adds one to the point's y-coordinate in place (big-endian), which takes it off the curve.</summary>
    private static byte[] YPlusOne(Span<byte> point)
    {
        for (var i = point.Length - 1; i >= 33; i--)
        {
            if (++point[i] != 0) break;
        }

        return point.ToArray();
    }

    private const string VectorPrivate = "380aeb87dad253f2f481588ea73caec98380956ab76338ca13d9b6c1fca5d73e";
    private const string VectorPublic =
        "0401024d9cdc40c52a8c0b227dd36a1cc924826bfa570518036317f8974c1e12c10d95b1060fb6d8da218a5b65953367ae435a7628d9f8c3646d82da887e83e4a9";
    private const string VectorSlot =
        "01042869c8078bf995aa0339d502e60977bb78c32aa727c4ed2c3f3dc7b15021144a7a679a70591ea329a1a902398e111751e6b2ce0796b06d0a0d73bc0590e6aaac0124294930c8be17eb855e254a027772f2489b6817e79293b20077b4152d204d8bd41b7e746dd5f6bd343200861709774336094b959ac100702415f49d";
    private const string VectorVaultKey = "5bd5416be78c9f5c9da1410840af32ceb8fd82c17e4c7989aaeb573798aa41f6";
}
