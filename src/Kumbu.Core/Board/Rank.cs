// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Board;

/// <summary>
/// Sortable string keys (fractional indexing in base 62) that order the cards of a column. Keys compare ordinally, so a new
/// key between two neighbours moves one card without touching the others. A key never ends in the lowest digit, so there is always room below it.
/// </summary>
public static class Rank
{
    /// <summary>A column is re-ranked with <see cref="Spread"/> when a new key would be longer than this.</summary>
    public const int MaxLength = 24;

    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Base = 62;

    /// <summary>True for a non-empty string of <c>0-9A-Za-z</c> that does not end in <c>0</c>. The length is not checked.</summary>
    public static bool IsValid(string key) =>
        key.Length > 0 && key[^1] != '0' && key.All(c => Digits.Contains(c));

    /// <summary>A key strictly between <paramref name="before"/> and <paramref name="after"/>; <c>null</c> is an open end.</summary>
    public static string Between(string? before, string? after)
    {
        if (before is not null && !IsValid(before)) throw new ArgumentException($"'{before}' is not a rank.", nameof(before));
        if (after is not null && !IsValid(after)) throw new ArgumentException($"'{after}' is not a rank.", nameof(after));
        if (before is not null && after is not null && string.CompareOrdinal(before, after) >= 0)
            throw new ArgumentException("The key before must sort before the key after.", nameof(before));
        return Midpoint(before ?? "", after);
    }

    /// <summary><paramref name="count"/> ascending keys, evenly spaced over the whole range, with room to insert at either end and between.</summary>
    public static IReadOnlyList<string> Spread(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0) return [];

        // The shortest length whose space holds the keys at least two steps apart, so that no key needs to end in 0.
        var length = 1;
        long space = Base;
        while (space < 2L * (count + 1)) { length++; space *= Base; }

        var keys = new string[count];
        for (var i = 1; i <= count; i++)
        {
            var value = (long)((decimal)space * i / (count + 1));
            if (value % Base == 0) value++;
            var chars = new char[length];
            for (var p = length - 1; p >= 0; p--, value /= Base) chars[p] = Digits[(int)(value % Base)];
            keys[i - 1] = new string(chars);
        }
        return keys;
    }

    /// <summary>Digits of <paramref name="a"/> read as a fraction (missing digits are 0) and of <paramref name="b"/> (<c>null</c> is 1); a &lt; b.</summary>
    private static string Midpoint(string a, string? b)
    {
        if (b is not null)
        {
            var common = 0;
            while (common < b.Length && (common < a.Length ? a[common] : '0') == b[common]) common++;
            if (common > 0) return b[..common] + Midpoint(a.Length > common ? a[common..] : "", b[common..]);
        }

        var low = a.Length > 0 ? Digits.IndexOf(a[0]) : 0;
        var high = b is not null ? Digits.IndexOf(b[0]) : Base;
        if (high - low > 1)
        {
            // Next to an open end take the neighbouring digit: appending or prepending a run of cards then grows a key every 30 cards, not every 6.
            if (b is null && a.Length > 0) return Digits[low + 1].ToString();
            if (a.Length == 0 && b is not null) return Digits[high - 1].ToString();
            return Digits[(low + high + 1) / 2].ToString();
        }

        // Adjacent first digits: a longer b has a prefix of itself to use, otherwise extend a's digit with the middle of what is above a's tail.
        if (b is { Length: > 1 }) return b[..1];
        return Digits[low] + Midpoint(a.Length > 1 ? a[1..] : "", null);
    }
}
