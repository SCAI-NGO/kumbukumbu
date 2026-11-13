// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;

namespace Kumbu.Core.Tests.Calendar;

public class CalendarTimeTests
{
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    private static CalendarTime Parse(string text)
    {
        Assert.True(CalendarTime.TryParse(text, out var value), text);
        return value;
    }

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Reads_a_day_a_time_and_a_week()
    {
        var day = Parse("2026-11-20");
        Assert.Equal(TimeKind.Day, day.Kind);
        Assert.Equal(new DateOnly(2026, 11, 20), day.Date);
        Assert.Equal(new DateOnly(2026, 11, 20), day.FirstDay);
        Assert.Equal(new DateOnly(2026, 11, 20), day.LastDay);

        var time = Parse("2026-11-20T14:00");
        Assert.Equal(TimeKind.Time, time.Kind);
        Assert.Equal(new DateOnly(2026, 11, 20), time.Date);
        Assert.Equal(new TimeOnly(14, 0), time.Clock);
        Assert.Null(time.Offset);

        var seconds = Parse("2026-11-20T14:00:30");
        Assert.Equal(TimeKind.Time, seconds.Kind);
        Assert.Equal(new TimeOnly(14, 0, 30), seconds.Clock);

        var week = Parse("2026-W47");
        Assert.Equal(TimeKind.Week, week.Kind);
        Assert.Equal(new DateOnly(2026, 11, 16), week.Date);
        Assert.Equal(new DateOnly(2026, 11, 16), week.FirstDay);
        Assert.Equal(new DateOnly(2026, 11, 22), week.LastDay);
        Assert.Equal((2026, 47), (week.IsoYear, week.IsoWeek));
    }

    [Fact]
    public void Reads_an_offset_or_Z()
    {
        var offset = Parse("2026-11-20T14:00+01:00");
        Assert.Equal(TimeKind.Time, offset.Kind);
        Assert.Equal(new TimeOnly(14, 0), offset.Clock);
        Assert.Equal(TimeSpan.FromHours(1), offset.Offset);
        Assert.Equal(Utc(2026, 11, 20, 13), offset.StartUtc(TimeZoneInfo.Utc));

        var z = Parse("2026-11-20T13:00Z");
        Assert.Equal(TimeSpan.Zero, z.Offset);
        // The offset wins over the zone.
        Assert.Equal(Utc(2026, 11, 20, 13), z.StartUtc(Vienna));
    }

    [Fact]
    public void Reads_what_YAML_or_the_frontmatter_hands_over()
    {
        Assert.True(CalendarTime.TryParse((object?)"2026-11-20", out var text));
        Assert.Equal(TimeKind.Day, text.Kind);
        Assert.False(CalendarTime.TryParse((object?)new DateOnly(2026, 11, 20), out _));
    }

    [Theory]
    [InlineData("٢٠٢٦-W47")]
    [InlineData("2026-W47\n")]
    [InlineData("2026-11-20\n")]
    public void Refuses_other_digits_and_a_trailing_newline(string text) => Assert.False(CalendarTime.TryParse(text, out _));

    [Fact]
    public void A_day_across_the_spring_change_is_23_hours()
    {
        var day = Parse("2026-03-29");
        Assert.Equal(new DateTime(2026, 3, 28, 23, 0, 0, DateTimeKind.Utc), day.StartUtc(Vienna));
        Assert.Equal(new DateTime(2026, 3, 29, 22, 0, 0, DateTimeKind.Utc), day.EndUtc(Vienna));
    }

    [Fact]
    public void An_offset_becomes_local_time_in_a_zone()
    {
        Assert.Equal(Parse("2026-11-20T15:00"), Parse("2026-11-20T14:00Z").Local(Vienna));
        Assert.Equal(Parse("2026-11-20"), Parse("2026-11-20").Local(Vienna));
    }

    [Fact]
    public void Moving_past_the_last_year_is_refused_not_thrown_at_random()
    {
        Assert.False(Parse("9998-12-31").CanAddDays(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Parse("9998-12-31").AddDays(1));
        Assert.True(Parse("2026-11-20").CanAddDays(7));
    }

    [Theory]
    [InlineData("tomorrow")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("2026-W54")]
    [InlineData("2026-W00")]
    [InlineData("2025-W53")]
    [InlineData("2026-11")]
    [InlineData("2026-11-20T25:00")]
    [InlineData("2026-11-20T14")]
    [InlineData("20.11.2026")]
    [InlineData(" 2026-11-20")]
    [InlineData("")]
    public void Refuses_what_is_not_a_date(string text)
    {
        Assert.False(CalendarTime.TryParse(text, out _));
        Assert.False(CalendarTime.TryParse((object?)text, out _));
    }

    [Fact]
    public void Refuses_a_list_a_map_and_nothing()
    {
        Assert.False(CalendarTime.TryParse((object?)new List<object?> { "2026-11-20" }, out _));
        Assert.False(CalendarTime.TryParse((object?)new Dictionary<string, object?> { ["day"] = "2026-11-20" }, out _));
        Assert.False(CalendarTime.TryParse((object?)null, out _));
        Assert.False(CalendarTime.TryParse((object?)42, out _));
    }

    [Fact]
    public void Week_53_and_week_1_across_the_year_end()
    {
        Assert.Equal(53, System.Globalization.ISOWeek.GetWeeksInYear(2026));

        var w53 = Parse("2026-W53");
        Assert.Equal(new DateOnly(2026, 12, 28), w53.FirstDay);
        Assert.Equal(new DateOnly(2027, 1, 3), w53.LastDay);

        var w01 = Parse("2026-W01");
        Assert.Equal(new DateOnly(2025, 12, 29), w01.FirstDay);
        Assert.Equal((2026, 1), (w01.IsoYear, w01.IsoWeek));
        Assert.Equal("2026-W01", w01.Write());

        Assert.Equal(new DateOnly(2027, 1, 4), Parse("2027-W01").FirstDay);
        Assert.Equal(Parse("2027-W01"), CalendarTime.Week(2027, 1));
    }

    [Theory]
    [InlineData("2026-11-20", "2026-11-20")]
    [InlineData("2026-11-20T14:00", "2026-11-20T14:00")]
    [InlineData("2026-11-20T14:00:30", "2026-11-20T14:00:30")]
    [InlineData("2026-11-20T14:00:00", "2026-11-20T14:00")]
    [InlineData("2026-W47", "2026-W47")]
    [InlineData("2026-W01", "2026-W01")]
    public void Writes_the_short_forms(string text, string written)
    {
        Assert.Equal(written, Parse(text).Write());
        Assert.Equal(Parse(text), Parse(written));
    }

    [Fact]
    public void Writes_no_offset()
    {
        Assert.Equal("2026-11-20T14:00", Parse("2026-11-20T14:00+01:00").Write());
    }

    [Fact]
    public void A_day_ends_at_local_midnight()
    {
        var day = Parse("2026-11-20");

        Assert.Equal(Utc(2026, 11, 19, 23), day.StartUtc(Vienna));
        Assert.Equal(Utc(2026, 11, 20, 23), day.EndUtc(Vienna));
        Assert.Equal(DateTimeKind.Utc, day.EndUtc(Vienna).Kind);
    }

    [Fact]
    public void A_week_ends_at_the_end_of_its_Sunday()
    {
        var week = Parse("2026-W46");

        Assert.Equal(Utc(2026, 11, 8, 23), week.StartUtc(Vienna));
        Assert.Equal(Utc(2026, 11, 15, 23), week.EndUtc(Vienna));
    }

    [Fact]
    public void A_time_starts_and_ends_at_its_instant()
    {
        var time = Parse("2026-11-20T14:00");

        Assert.Equal(Utc(2026, 11, 20, 13), time.StartUtc(Vienna));
        Assert.Equal(Utc(2026, 11, 20, 13), time.EndUtc(Vienna));
    }

    [Fact]
    public void Both_sides_of_the_autumn_change()
    {
        Assert.Equal(Utc(2026, 10, 24, 12), Parse("2026-10-24T14:00").StartUtc(Vienna));
        Assert.Equal(Utc(2026, 10, 26, 13), Parse("2026-10-26T14:00").StartUtc(Vienna));
    }

    [Fact]
    public void Adding_days_keeps_the_kind_clock_and_offset()
    {
        Assert.Equal(Parse("2026-11-27"), Parse("2026-11-20").AddDays(7));
        Assert.Equal(Parse("2026-11-19T14:00:30"), Parse("2026-11-20T14:00:30").AddDays(-1));
        Assert.Equal(Parse("2026-11-21T14:00+01:00"), Parse("2026-11-20T14:00+01:00").AddDays(1));
        Assert.Equal(Parse("2027-W01"), Parse("2026-W53").AddDays(7));
        Assert.Throws<ArgumentException>(() => Parse("2026-W47").AddDays(1));
    }
}
