// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kumbu.Core.Calendar;

/// <summary>
/// The <c>upcoming</c> span (spec §2.1): how long before <c>when</c> an entry starts to show in What's on. Read as <c>7d</c>, <c>2w</c>,
/// <c>12h</c> or ISO 8601 <c>P7D</c>, <c>P2W</c>, <c>PT12H</c>; written in the short form. No months, as they are 28 to 31 days long.
/// </summary>
/// <param name="Unit"><c>d</c>, <c>w</c> or <c>h</c>.</param>
public sealed partial record Upcoming(int Count, char Unit)
{
    // Four digits at most: 9999 weeks are still a TimeSpan, and no one means more.
    [GeneratedRegex(@"^(?:([1-9][0-9]{0,3})([dwh])|P([1-9][0-9]{0,3})([DW])|PT([1-9][0-9]{0,3})H)$")]
    private static partial Regex Form();

    public static bool TryParse(string text, out Upcoming value)
    {
        value = default!;
        var match = Form().Match(text);
        if (!match.Success) return false;

        var g = match.Groups;
        value = g[1].Success ? new Upcoming(Number(g[1]), g[2].Value[0])
            : g[3].Success ? new Upcoming(Number(g[3]), char.ToLowerInvariant(g[4].Value[0]))
            : new Upcoming(Number(g[5]), 'h');
        return true;

        static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);
    }

    public TimeSpan Length => Unit switch
    {
        'd' => TimeSpan.FromDays(Count),
        'w' => TimeSpan.FromDays(7 * Count),
        'h' => TimeSpan.FromHours(Count),
        _ => throw new InvalidOperationException($"'{Unit}' is not a unit of upcoming."),
    };

    public override string ToString() => Count.ToString(CultureInfo.InvariantCulture) + Unit;
}
