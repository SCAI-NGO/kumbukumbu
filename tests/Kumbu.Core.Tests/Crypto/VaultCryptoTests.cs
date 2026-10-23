// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;

namespace Kumbu.Core.Tests.Crypto;

public class VaultCryptoTests
{
    private static readonly byte[] Key = VaultCrypto.NewKey();
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("# Geheim\nÜberlegungen");

    [Fact]
    public void SealThenOpenRoundTrips() =>
        Assert.Equal(Plain, VaultCrypto.Open(Key, VaultCrypto.Seal(Key, Plain, "note:v:1"), "note:v:1"));

    [Fact]
    public void SealingTwiceGivesDifferentBlobs() =>
        Assert.NotEqual(VaultCrypto.Seal(Key, Plain, "c"), VaultCrypto.Seal(Key, Plain, "c"));

    [Fact]
    public void TheBlobDoesNotContainThePlaintext()
    {
        var blob = VaultCrypto.Seal(Key, Plain, "c");
        Assert.Equal(1 + 12 + Plain.Length + 16, blob.Length);
        Assert.Equal(-1, blob.AsSpan().IndexOf("Geheim"u8));
    }

    [Fact]
    public void AWrongKeyFails() =>
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(VaultCrypto.NewKey(), VaultCrypto.Seal(Key, Plain, "c"), "c"));

    [Fact]
    public void AnotherContextFails() =>
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(Key, VaultCrypto.Seal(Key, Plain, "note:v:1"), "note:v:2"));

    [Fact]
    public void AnyChangedBitFails()
    {
        var blob = VaultCrypto.Seal(Key, Plain, "c");
        for (var i = 0; i < blob.Length; i++)
        {
            var tampered = (byte[])blob.Clone();
            tampered[i] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(Key, tampered, "c"));
        }
    }

    [Fact]
    public void DeriveIsDeterministicAndSeparatesLabelsAndSalts()
    {
        var a = VaultCrypto.Derive("token-0123456789abcdef", "kek", [1]);
        Assert.Equal(a, VaultCrypto.Derive("token-0123456789abcdef", "kek", [1]));
        Assert.NotEqual(a, VaultCrypto.Derive("token-0123456789abcdef", "lookup", [1]));
        Assert.NotEqual(a, VaultCrypto.Derive("token-0123456789abcdef", "kek", [2]));
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void RecoveryKeyRoundTripsAndForgivesTyping()
    {
        var key = RecoveryKey.New();
        var text = RecoveryKey.Format(key);

        Assert.StartsWith("kumbu-rk-", text);
        Assert.Matches("^kumbu-rk-([A-Z2-7]{4}-){12}[A-Z2-7]{4}$", text);
        Assert.True(RecoveryKey.TryParse(text, out var parsed));
        Assert.Equal(key, parsed);
        Assert.True(RecoveryKey.TryParse("  " + text.ToLowerInvariant().Replace("-", " ") + " ", out var lenient));
        Assert.Equal(key, lenient);
    }

    [Theory]
    [InlineData("kumbu-rk-AAAA")]
    [InlineData("kumbu-rk-0000-1111")]
    [InlineData("")]
    public void InvalidRecoveryKeys(string text) => Assert.False(RecoveryKey.TryParse(text, out _));
}
