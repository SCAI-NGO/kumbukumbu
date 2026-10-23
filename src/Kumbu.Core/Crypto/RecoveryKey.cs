// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Kumbu.Core.Crypto;

/// <summary>
/// A vault's recovery key: 32 random bytes, written for humans as "kumbu-rk-" and Base32 in groups of
/// four (no 0/1/8/9, no case to get wrong). Parsing ignores case, dashes and spaces.
/// </summary>
public static class RecoveryKey
{
    private const string Prefix = "kumbu-rk-";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] New() => RandomNumberGenerator.GetBytes(VaultCrypto.KeySize);

    public static string Format(ReadOnlySpan<byte> key)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in key)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return Prefix + string.Join('-', text.ToString().Chunk(4).Select(c => new string(c)));
    }

    public static bool TryParse(string text, [NotNullWhen(true)] out byte[]? key)
    {
        key = null;
        var s = text.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (s.StartsWith("KUMBURK", StringComparison.Ordinal)) s = s["KUMBURK".Length..];

        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) return false;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        if (bytes.Count != VaultCrypto.KeySize) return false;
        key = [.. bytes];
        return true;
    }
}
