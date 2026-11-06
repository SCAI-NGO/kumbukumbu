// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;

namespace Kumbu.Server.Publishing;

/// <summary>
/// The share code of a publication (publish spec §4): made by the server, never chosen, 160 random bits as 32
/// characters of <see cref="Pairings.Alphabet"/>, read as pairing codes are. Two HKDF derivations of the normalised
/// code give the lookup id, all the database stores to find the publication, and the reader key, which seals its
/// reader slot; a third, the copy id, ties a file read to its copy. No salt: a publication is found only through its
/// code. The code is a secret, never logged.
/// </summary>
public static class ShareCode
{
    public const int Length = 32;
    private const int MaxTyped = 64;
    public const string LookupLabel = "kumbu/v1/publication-lookup";
    public const string ReaderLabel = "kumbu/v1/publication-reader";
    public const string CopyLabel = "kumbu/v1/publication-copy";
    private const int CopyIdBytes = 16;

    /// <summary>160 random bits, 5 per character, most significant first.</summary>
    public static string New()
    {
        var bytes = RandomNumberGenerator.GetBytes(Length * 5 / 8);
        var chars = new char[Length];
        int bits = 0, held = 0, i = 0;
        foreach (var b in bytes)
        {
            bits = (bits << 8) | b;
            held += 8;
            while (held >= 5)
            {
                held -= 5;
                chars[i++] = Pairings.Alphabet[(bits >> held) & 31];
            }

            bits &= (1 << held) - 1;
        }

        CryptographicOperations.ZeroMemory(bytes);
        var code = new string(chars);
        chars.AsSpan().Clear();
        return code;
    }

    /// <summary>Groups of four joined by hyphens: <c>K7QM-4XDA-…</c>, 39 characters.</summary>
    public static string Format(string normalised) => string.Join('-', normalised.Chunk(4).Select(g => new string(g)));

    /// <summary>The normalised code of what was typed or pasted, or null if it isn't one.</summary>
    public static string? Parse(string typed)
    {
        // Anyone may send one to the reader endpoint; a formatted code with spaces is well under this.
        if (typed.Length > MaxTyped)
        {
            return null;
        }

        var normal = Pairings.Normalise(typed);
        return normal.Length == Length && normal.All(c => Pairings.Alphabet.Contains(c)) ? normal : null;
    }

    public static byte[] Lookup(string normalised) => VaultCrypto.Derive(normalised, LookupLabel, []);

    public static byte[] ReaderKey(string normalised) => VaultCrypto.Derive(normalised, ReaderLabel, []);

    /// <summary>
    /// The reader page's name for the copy it shows, which it sends with each file it reads, so that a reader cookie
    /// set since by another tab opens no file for this one: 16 bytes in lower-case hex, which tell nothing of the code
    /// or the lookup id.
    /// </summary>
    public static string CopyId(string normalised)
    {
        Span<byte> id = stackalloc byte[CopyIdBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(normalised), id, [], Encoding.UTF8.GetBytes(CopyLabel));
        return Convert.ToHexStringLower(id);
    }

    /// <summary>The code goes in the fragment, which a browser never sends to the server.</summary>
    public static string Link(string baseUrl, string normalised) => $"{baseUrl.TrimEnd('/')}/s#{Format(normalised)}";
}
