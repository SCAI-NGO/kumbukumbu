// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Kumbu.Core.Sh;

/// <summary>The pages of a <c>pdf</c> call, counted from 1: N, N-M, or N- (<see cref="Last"/> null: to the end).</summary>
public sealed record PageRange(int First, int? Last)
{
    public static readonly PageRange All = new(1, null);

    public bool IsSingle => Last == First;

    public static bool TryParse(string text, out PageRange range, out string error)
    {
        range = All;
        var dash = text.IndexOf('-');
        var firstText = dash < 0 ? text : text[..dash];
        var lastText = dash < 0 ? firstText : text[(dash + 1)..];
        if (!TryPage(firstText, out var first) || (lastText.Length > 0 && !TryPage(lastText, out _)))
        {
            error = $"'{text}' is not a page range: give N, N-M or N- (pages count from 1), e.g. 5-9";
            return false;
        }

        int? last = lastText.Length == 0 ? null : int.Parse(lastText, CultureInfo.InvariantCulture);
        if (last < first)
        {
            error = $"'{text}' is not a page range: the first page comes first, e.g. 5-9";
            return false;
        }

        range = new PageRange(first, last);
        error = "";
        return true;
    }

    private static bool TryPage(string text, out int page) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1;

    /// <summary>The range within a PDF of <paramref name="count"/> pages; a start past the end is refused, naming <paramref name="name"/>.</summary>
    public bool TryClamp(int count, string name, out PageRange clamped, out string error)
    {
        clamped = this;
        if (First > count)
        {
            error = $"{name} has {count} {(count == 1 ? "page" : "pages")}; there is no page {First}";
            return false;
        }

        clamped = new PageRange(First, Math.Min(Last ?? count, count));
        error = "";
        return true;
    }

    /// <summary>As <c>pdf</c> takes it: "4", "5-9", "5-".</summary>
    public override string ToString() => Last is null ? $"{First}-" : Last == First ? $"{First}" : $"{First}-{Last}";
}
