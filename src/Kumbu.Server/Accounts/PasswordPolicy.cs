// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Frozen;

namespace Kumbu.Server.Accounts;

/// <summary>
/// What a password must be: at least 12 characters and not one of the 10,000 most common passwords.
/// The list is <c>common-passwords.txt</c>, embedded: SecLists' <c>Passwords/Common-Credentials/10k-most-common.txt</c>
/// at commit 913b327317496d062bcc7cace524aaad8a693be2
/// (https://github.com/danielmiessler/SecLists/blob/913b327317496d062bcc7cace524aaad8a693be2/Passwords/Common-Credentials/10k-most-common.txt),
/// MIT licence, Copyright (c) 2018 Daniel Miessler; the licence text is in THIRD-PARTY-NOTICES.md at the repository root.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    /// <summary>A stripped base shorter than this is never compared, so padding alone isn't mistaken for a common word.</summary>
    private const int MinimumBaseLength = 4;

    private static readonly Lazy<FrozenSet<string>> Common = new(Load);

    /// <summary>Null if <paramref name="password"/> is fine, else the reason it isn't, for the user to read.</summary>
    public static string? Check(string password)
    {
        var normalised = Normalise(password);

        // Characters as people count them, not UTF-16 units: "ä" is one, and so is an emoji.
        if (normalised.EnumerateRunes().Count() < MinimumLength) return $"A password needs at least {MinimumLength} characters.";

        var lower = normalised.ToLowerInvariant();
        if (Common.Value.Contains(lower)) return "This password is one of the 10,000 most common ones.";

        // Almost every listed password is shorter than 12 characters, so the list only bites once the usual
        // padding is gone: "password1234", "Qwertyuiop!!" and "!!dragon2024!!" are a listed word dressed up.
        var core = lower.Trim().AsSpan().Trim(Padding);
        if (core.Length >= MinimumBaseLength && Common.Value.GetAlternateLookup<ReadOnlySpan<char>>().Contains(core))
        {
            return "This password is a common one with digits or punctuation around it.";
        }

        return null;
    }

    /// <summary>
    /// NFC, so the same password typed on two devices (one composing "ö", one sending "o" plus a combining
    /// diaeresis) gives the same key. Text that isn't well-formed UTF-16 stays as it is.
    /// </summary>
    public static string Normalise(string password)
    {
        try
        {
            return password.Normalize();
        }
        catch (ArgumentException)
        {
            return password;
        }
    }

    /// <summary>What people put around a common word: ASCII digits, punctuation, symbols and spaces.</summary>
    private static readonly char[] Padding = [.. Enumerable.Range(0x20, 0x7F - 0x20).Select(c => (char)c).Where(c => !char.IsAsciiLetter(c))];

    private static FrozenSet<string> Load()
    {
        using var stream = typeof(PasswordPolicy).Assembly.GetManifestResourceStream("Kumbu.Server.Accounts.common-passwords.txt")
            ?? throw new InvalidOperationException("The embedded list of common passwords is missing.");
        using var reader = new StreamReader(stream);
        var words = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim() is { Length: > 0 } word) words.Add(word.ToLowerInvariant());
        }

        return words.ToFrozenSet(StringComparer.Ordinal);
    }
}
