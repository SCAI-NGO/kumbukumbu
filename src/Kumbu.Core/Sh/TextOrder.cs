// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Kumbu.Core.Sh;

/// <summary>
/// The order people expect for text, Ü next to U rather than after z. It compares with the invariant
/// culture, so every server sorts the same way, and breaks ties by code point so the order is total.
/// </summary>
public static class TextOrder
{
    public static IComparer<string> Default { get; } = WithTieBreak(CompareOptions.None);

    public static IComparer<string> IgnoreCase { get; } = WithTieBreak(CompareOptions.IgnoreCase);

    /// <summary>The culture comparison alone, without the tie-break (for <c>sort -u</c>).</summary>
    public static IComparer<string> Key(bool ignoreCase) =>
        Comparer<string>.Create((a, b) => CultureInfo.InvariantCulture.CompareInfo.Compare(
            a, b, ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None));

    /// <summary>False when .NET runs without ICU (globalization-invariant mode): comparisons then fall
    /// back to code point order, and umlauts sort after z again.</summary>
    public static bool IsCultureAware => CultureInfo.InvariantCulture.CompareInfo.Compare("Ü", "V") < 0;

    private static IComparer<string> WithTieBreak(CompareOptions options) =>
        Comparer<string>.Create((a, b) =>
            CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, options) is var c && c != 0
                ? c
                : StringComparer.Ordinal.Compare(a, b));
}
