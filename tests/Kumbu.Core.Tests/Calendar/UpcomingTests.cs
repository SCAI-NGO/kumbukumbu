// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;

namespace Kumbu.Core.Tests.Calendar;

public class UpcomingTests
{
    [Theory]
    [InlineData("7d", 7 * 24, "7d")]
    [InlineData("2w", 14 * 24, "2w")]
    [InlineData("12h", 12, "12h")]
    [InlineData("30d", 30 * 24, "30d")]
    [InlineData("P7D", 7 * 24, "7d")]
    [InlineData("PT12H", 12, "12h")]
    [InlineData("P2W", 14 * 24, "2w")]
    public void Upcoming_reads_short_and_iso_forms(string text, int hours, string written)
    {
        Assert.True(Upcoming.TryParse(text, out var upcoming), text);

        Assert.Equal(TimeSpan.FromHours(hours), upcoming.Length);
        Assert.Equal(written, upcoming.ToString());
    }

    [Theory]
    [InlineData("1m")]
    [InlineData("P1M")]
    [InlineData("P1Y")]
    [InlineData("0d")]
    [InlineData("P0D")]
    [InlineData("-1d")]
    [InlineData("7")]
    [InlineData("d")]
    [InlineData("7 d")]
    [InlineData("P1DT12H")]
    [InlineData("99999d")]
    [InlineData("")]
    [InlineData("7d\n")]
    public void Upcoming_refuses_months_nothing_and_the_rest(string text)
    {
        Assert.False(Upcoming.TryParse(text, out _), text);
    }
}
