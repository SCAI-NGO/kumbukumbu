// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Kumbu.Core.Calendar;

/// <summary>An entry's clock as people read it, the same in <c>sh calendar</c> and the web app.</summary>
public static class EntryText
{
    /// <summary>"14:00", "11:00–12:30" (with <paramref name="withEnd"/> and an <c>until</c>), "14:00 (Europe/London)": a time on
    /// the viewer's clock, or on its own zone's when the note's <c>tz</c> pins it to another (spec §3).</summary>
    public static string Clock(Entry entry, TimeZoneInfo zone, bool withEnd = true)
    {
        var (shownZone, pinned) = Shown(entry, zone);
        var text = Local(entry.When, entry, zone, shownZone).Clock.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (withEnd && entry.Until is { } until) text += "–" + Local(until, entry, zone, shownZone).Clock.ToString("HH:mm", CultureInfo.InvariantCulture);
        return text + pinned;
    }

    /// <summary>The zone a time is shown in, and the suffix naming it when it isn't the viewer's.</summary>
    internal static (TimeZoneInfo Zone, string Suffix) Shown(Entry entry, TimeZoneInfo zone) =>
        entry.Zone is { } own && own.Id != zone.Id && entry.When.Offset is null ? (own, $" ({own.Id})") : (zone, "");

    /// <summary>A value of the entry (in its <c>tz</c>, else the viewer's zone) on the clock of <paramref name="shownZone"/>.</summary>
    internal static (DateOnly Day, TimeOnly Clock) Local(CalendarTime value, Entry entry, TimeZoneInfo zone, TimeZoneInfo shownZone) =>
        Zones.ToLocal(value.StartUtc(entry.Zone ?? zone), shownZone);
}
