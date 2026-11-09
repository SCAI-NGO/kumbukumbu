// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Kumbu.Server.Accounts;

/// <summary>
/// A WebDAV share's password (WebDAV design §3.2): 32 random bytes as 52 Crockford Base32 characters, shown in groups of
/// four. Typed in by hand or pasted into a system keychain, so it is read as a pairing code is (<see cref="Pairings.Normalise"/>):
/// case, hyphens and spaces don't matter, I and L read as 1, O as 0. Everything derived from it starts from the normalised form.
/// </summary>
public static class DavTokens
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>256 bits, five to a character: 51 characters and one more for the last bit.</summary>
    public const int Length = 52;

    private const int Bytes = 32;

    /// <summary>A new token, normalised.</summary>
    public static string New()
    {
        var bytes = RandomNumberGenerator.GetBytes(Bytes);
        var text = new StringBuilder(Length);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
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
        CryptographicOperations.ZeroMemory(bytes);
        return text.ToString();
    }

    /// <summary>Groups of four joined with hyphens, as the share's dialog shows it once.</summary>
    public static string Format(string token) => string.Join('-', token.Chunk(4).Select(c => new string(c)));

    /// <summary>The token as <see cref="New"/> made it, or null unless exactly <see cref="Length"/> characters of
    /// <see cref="Alphabet"/> remain once read as a pairing code is.</summary>
    public static string? Normalise(string presented)
    {
        var normal = Pairings.Normalise(presented);
        return normal.Length == Length && normal.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) ? normal : null;
    }
}
