// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Kumbu.Core.Calendar;

public enum TimeKind { Day, Time, Week }

/// <summary>
/// A <c>when</c> or <c>until</c> value (spec §2.2): a day (<c>2026-11-20</c>), a day with a time (<c>2026-11-20T14:00</c>, seconds
/// allowed, an offset or <c>Z</c> read when written by hand) or an ISO 8601 week (<c>2026-W47</c>). For a day <see cref="Date"/> is
/// that day, for a time the day of the time as written, for a week its Monday. A day and a week are never converted to a zone
/// (spec §3); only their boundaries are.
/// </summary>
public sealed partial record CalendarTime(TimeKind Kind, DateOnly Date, TimeOnly Clock = default, TimeSpan? Offset = null)
{
    private static readonly string[] LocalForms = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];
    private static readonly string[] OffsetForms = ["yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd HH:mmzzz", "yyyy-MM-dd HH:mm:sszzz"];
    private static readonly string[] UtcForms = ["yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd HH:mm'Z'", "yyyy-MM-dd HH:mm:ss'Z'"];

    [GeneratedRegex(@"^(\d{4})-W(\d{2})$")]
    private static partial Regex WeekForm();

    /// <summary>
    /// Reads a frontmatter value: the string <see cref="Board.Frontmatter.Read"/> hands over, or a <see cref="DateOnly"/>,
    /// <see cref="DateTime"/> or <see cref="DateTimeOffset"/>. A list, a map or anything else is not a date.
    /// </summary>
    public static bool TryParse(object? raw, out CalendarTime value)
    {
        CalendarTime? read = raw switch
        {
            string text => TryParse(text, out var parsed) ? parsed : null,
            DateOnly day => new CalendarTime(TimeKind.Day, day),
            DateTime time => new CalendarTime(TimeKind.Time, DateOnly.FromDateTime(time), Seconds(time),
                time.Kind == DateTimeKind.Utc ? TimeSpan.Zero : null),
            DateTimeOffset time => new CalendarTime(TimeKind.Time, DateOnly.FromDateTime(time.DateTime), Seconds(time.DateTime), time.Offset),
            _ => null,
        };
        value = read is not null && InRange(read.Date) ? read : default!;
        return value is not null;
    }

    public static bool TryParse(string text, out CalendarTime value)
    {
        value = default!;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            value = new CalendarTime(TimeKind.Day, day);
        else if (DateTime.TryParseExact(text, LocalForms, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            value = new CalendarTime(TimeKind.Time, DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
        else if (DateTime.TryParseExact(text, UtcForms, CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc))
            value = new CalendarTime(TimeKind.Time, DateOnly.FromDateTime(utc), TimeOnly.FromDateTime(utc), TimeSpan.Zero);
        else if (DateTimeOffset.TryParseExact(text, OffsetForms, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
            value = new CalendarTime(TimeKind.Time, DateOnly.FromDateTime(offset.DateTime), TimeOnly.FromDateTime(offset.DateTime), offset.Offset);
        else if (WeekForm().Match(text) is { Success: true } match
                 && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) is var year and > 1 and < 9999
                 && int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) is var week and >= 1
                 && week <= ISOWeek.GetWeeksInYear(year))
            value = Week(year, week);
        else
            return false;

        if (InRange(value.Date)) return true;
        value = default!;
        return false;
    }

    /// <summary>The week <paramref name="week"/> of the ISO year <paramref name="isoYear"/>, which may start in the year before.</summary>
    public static CalendarTime Week(int isoYear, int week) =>
        new(TimeKind.Week, DateOnly.FromDateTime(ISOWeek.ToDateTime(isoYear, week, DayOfWeek.Monday)));

    /// <summary>
    /// The short form Kumbu² writes: <c>2026-11-20</c>, <c>2026-11-20T14:00</c> (<c>:ss</c> only when not zero) or <c>2026-W47</c>.
    /// Never an offset (spec §2.2): a caller moving a value that has one converts it to local time in the note's <c>tz</c>, else the
    /// caller's zone, first.
    /// </summary>
    public string Write() => Kind switch
    {
        TimeKind.Day => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeKind.Time => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T"
            + Clock.ToString(Clock.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture),
        _ => $"{IsoYear:D4}-W{IsoWeek:D2}",
    };

    /// <summary>The first day: a week's Monday, else <see cref="Date"/>.</summary>
    public DateOnly FirstDay => Date;

    /// <summary>The last day: a week's Sunday, else <see cref="Date"/>.</summary>
    public DateOnly LastDay => Kind == TimeKind.Week ? Date.AddDays(6) : Date;

    public int IsoYear => ISOWeek.GetYear(FirstDay.ToDateTime(TimeOnly.MinValue));

    public int IsoWeek => ISOWeek.GetWeekOfYear(FirstDay.ToDateTime(TimeOnly.MinValue));

    /// <summary>A day or week: local midnight at the start of its first day in <paramref name="zone"/>. A time: its instant, in <paramref name="zone"/> unless it carries an offset.</summary>
    public DateTime StartUtc(TimeZoneInfo zone) =>
        Kind == TimeKind.Time ? Instant(zone) : Zones.ToUtc(FirstDay, TimeOnly.MinValue, zone);

    /// <summary>A day or week: local midnight at the end of its last day in <paramref name="zone"/>. A time: its instant.</summary>
    public DateTime EndUtc(TimeZoneInfo zone) =>
        Kind == TimeKind.Time ? Instant(zone) : Zones.ToUtc(LastDay.AddDays(1), TimeOnly.MinValue, zone);

    /// <summary>The same value <paramref name="days"/> later (or earlier); a week only moves by whole weeks.</summary>
    public CalendarTime AddDays(int days)
    {
        if (Kind == TimeKind.Week && days % 7 != 0) throw new ArgumentException("A week moves by whole weeks only.", nameof(days));
        return this with { Date = Date.AddDays(days) };
    }

    private DateTime Instant(TimeZoneInfo zone) =>
        Offset is { } offset
            ? DateTime.SpecifyKind(Date.ToDateTime(Clock) - offset, DateTimeKind.Utc)
            : Zones.ToUtc(Date, Clock, zone);

    private static TimeOnly Seconds(DateTime time) => new(time.Hour, time.Minute, time.Second);

    // Years 1 and 9999 are refused, so that a day's end and any zone's shift stay within DateTime's range.
    private static bool InRange(DateOnly day) => day.Year is > 1 and < 9999;
}
