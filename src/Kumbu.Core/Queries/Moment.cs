// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Kumbu.Core.Queries;

/// <summary>A date written as Moment writes it, which Obsidian's <c>format()</c> and <c>relative()</c> are: in English,
/// without a zone (bases spec 2026-11-25 decision 14).</summary>
public static class Moment
{
    // Longest first, so MMMM is not read as MM twice. Moment's other tokens (Mo, DDD, Q, W, w, E, e, X, x, Z, k, N, gg,
    // GG, Y) are left out: their letters stand as written.
    private static readonly string[] Tokens =
        ["YYYY", "YY", "MMMM", "MMM", "MM", "M", "Do", "DD", "D", "dddd", "ddd", "dd", "d", "HH", "H", "hh", "h", "mm", "m", "ss", "s", "SSS", "SS", "S", "A", "a"];

    /// <summary>The date in Moment's tokens: YYYY YY M MM MMM MMMM D DD Do d dd ddd dddd H HH h hh m mm s ss S SS SSS A a;
    /// <c>[text]</c> and <c>\x</c> stand as written. Null when the text would grow past <paramref name="limit"/>
    /// characters.</summary>
    public static string? Format(DateTime date, string pattern, int limit)
    {
        var text = new StringBuilder();
        for (var i = 0; i < pattern.Length;)
        {
            if (text.Length > limit) return null;
            // [text] is what stands up to the next ], unless a [ comes first: then this [ is a character of its own. Read
            // forward to whichever comes first, so a pattern of many [ stays linear.
            if (pattern[i] == '[' && pattern.IndexOfAny(Brackets, i + 1) is var close and > 0 && pattern[close] == ']')
            {
                text.Append(pattern, i + 1, close - i - 1);
                i = close + 1;
                continue;
            }

            if (pattern[i] == '\\' && i + 1 < pattern.Length)
            {
                text.Append(pattern[i + 1]);
                i += 2;
                continue;
            }

            var token = Tokens.FirstOrDefault(t => string.CompareOrdinal(pattern, i, t, 0, t.Length) == 0);
            if (token is null)
            {
                text.Append(pattern[i++]);
                continue;
            }

            text.Append(Written(date, token));
            i += token.Length;
        }

        return text.Length > limit ? null : text.ToString();
    }

    private static readonly char[] Brackets = ['[', ']'];

    private static string Written(DateTime date, string token)
    {
        var hour12 = date.Hour % 12 == 0 ? 12 : date.Hour % 12;
        return token switch
        {
            "YYYY" => date.Year.ToString("0000", CultureInfo.InvariantCulture),
            "YY" => (date.Year % 100).ToString("00", CultureInfo.InvariantCulture),
            "MMMM" => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month),
            "MMM" => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(date.Month),
            "MM" => date.Month.ToString("00", CultureInfo.InvariantCulture),
            "M" => date.Month.ToString(CultureInfo.InvariantCulture),
            "Do" => Ordinal(date.Day),
            "DD" => date.Day.ToString("00", CultureInfo.InvariantCulture),
            "D" => date.Day.ToString(CultureInfo.InvariantCulture),
            "dddd" => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName(date.DayOfWeek),
            "ddd" => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek),
            "dd" => CultureInfo.InvariantCulture.DateTimeFormat.GetShortestDayName(date.DayOfWeek),
            "d" => ((int)date.DayOfWeek).ToString(CultureInfo.InvariantCulture),
            "HH" => date.Hour.ToString("00", CultureInfo.InvariantCulture),
            "H" => date.Hour.ToString(CultureInfo.InvariantCulture),
            "hh" => hour12.ToString("00", CultureInfo.InvariantCulture),
            "h" => hour12.ToString(CultureInfo.InvariantCulture),
            "mm" => date.Minute.ToString("00", CultureInfo.InvariantCulture),
            "m" => date.Minute.ToString(CultureInfo.InvariantCulture),
            "ss" => date.Second.ToString("00", CultureInfo.InvariantCulture),
            "s" => date.Second.ToString(CultureInfo.InvariantCulture),
            "SSS" => date.Millisecond.ToString("000", CultureInfo.InvariantCulture),
            "SS" => (date.Millisecond / 10).ToString("00", CultureInfo.InvariantCulture),
            "S" => (date.Millisecond / 100).ToString(CultureInfo.InvariantCulture),
            "A" => date.Hour < 12 ? "AM" : "PM",
            _ /* a */ => date.Hour < 12 ? "am" : "pm",
        };
    }

    private static string Ordinal(int day)
    {
        var suffix = day % 100 / 10 == 1 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return day.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>How far the date is from now, as Moment's <c>fromNow()</c> says it: the calendar months between them and
    /// the rest in milliseconds, rounded at Moment's thresholds ("a few seconds ago", "in 3 hours", "2 months ago").</summary>
    public static string FromNow(DateTime date, DateTime now)
    {
        var future = date > now;
        var (from, to) = future ? (now, date) : (date, now);
        var months = to.Month - from.Month + (to.Year - from.Year) * 12;
        if (from.AddMonths(months) > to) months--;
        var milliseconds = (to - from.AddMonths(months)).Ticks / (double)TimeSpan.TicksPerMillisecond;

        // Moment's Duration.as(): days from months on Moment's average month for the small units, months from days for the large.
        var days = (double)Round(months * 146097 / 4800.0);
        var seconds = Round(days * 86400 + milliseconds / 1000);
        var minutes = Round(days * 1440 + milliseconds / 60_000);
        var hours = Round(days * 24 + milliseconds / 3_600_000);
        var wholeDays = Round(days + milliseconds / 86_400_000);
        var inMonths = months + milliseconds / 86_400_000 * 4800 / 146097;
        var (monthsRounded, years) = (Round(inMonths), Round(inMonths / 12));

        var said = seconds <= 44 ? "a few seconds"
            : minutes <= 1 ? "a minute"
            : minutes < 45 ? $"{minutes} minutes"
            : hours <= 1 ? "an hour"
            : hours < 22 ? $"{hours} hours"
            : wholeDays <= 1 ? "a day"
            : wholeDays < 26 ? $"{wholeDays} days"
            : monthsRounded <= 1 ? "a month"
            : monthsRounded < 11 ? $"{monthsRounded} months"
            : years <= 1 ? "a year"
            : $"{years} years";
        return future ? $"in {said}" : $"{said} ago";
    }

    // JavaScript's Math.round, which Moment rounds with.
    private static long Round(double n) => (long)Math.Floor(n + 0.5);
}
