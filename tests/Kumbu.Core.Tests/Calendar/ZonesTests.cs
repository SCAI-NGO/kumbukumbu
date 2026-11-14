// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;

namespace Kumbu.Core.Tests.Calendar;

public class ZonesTests
{
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    [Fact]
    public void Zones_find_iana_ids()
    {
        Assert.Equal("Europe/Vienna", Zones.Find("Europe/Vienna")?.Id);
        Assert.NotNull(Zones.Find("America/Argentina/Buenos_Aires"));
        Assert.NotNull(Zones.Find("UTC"));

        Assert.Null(Zones.Find("Mars/Olympus"));
        Assert.Null(Zones.Find(""));
        Assert.Null(Zones.Find(null));
        Assert.Null(Zones.Find("../../../etc/passwd"));
        Assert.Null(Zones.Find(new string('A', 65)));
    }

    [Fact]
    public void A_time_in_the_spring_gap_is_read_as_the_hour_after()
    {
        var utc = Zones.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(2, 30), Vienna);

        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc), utc);
        Assert.Equal((new DateOnly(2026, 3, 29), new TimeOnly(3, 30)), Zones.ToLocal(utc, Vienna));
    }

    [Fact]
    public void A_time_in_the_autumn_overlap_is_the_earlier()
    {
        var utc = Zones.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30), Vienna);

        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void An_ordinary_time_converts_both_ways()
    {
        var utc = Zones.ToUtc(new DateOnly(2026, 7, 1), new TimeOnly(9, 15), Vienna);

        Assert.Equal(new DateTime(2026, 7, 1, 7, 15, 0, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
        Assert.Equal((new DateOnly(2026, 7, 1), new TimeOnly(9, 15)), Zones.ToLocal(utc, Vienna));
        Assert.Equal((new DateOnly(2026, 12, 31), new TimeOnly(23, 30)),
            Zones.ToLocal(new DateTime(2026, 12, 31, 22, 30, 0, DateTimeKind.Utc), Vienna));
    }
}
