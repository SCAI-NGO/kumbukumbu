# Until in a day's +

Date: 2026-11-16. Status: approved by the maintainer. A small change to the calendar (`2026-11-12-calendar-design.md` §7.5); that section is amended with a pointer here.

## Why

A person who plans her appointments in the web app reported, through her agent, that she can only give a start time: "ZGM until 17:00", a market "from 12 to 21" end up without an end. Her agent writes `until` into the front matter by hand (`calendar set --until`); she can't do that herself. Reading the code: **Edit date…** already has **Until**, and the week and month lists already show "12:00–21:00" (`EntryText.Clock`); what lacks it is the day's +, which is where she adds things. Issue: SCAI-NGO/kumbukumbu#222.

The day's + was kept to "a title and an optional time … Nothing more" (calendar spec §7.5, maintainer 2026-11-13). *Maintainer, 2026-11-16:* an appointment with an end is common enough that the end belongs where the appointment is made; the overlap of nested appointments (one during another) waits for 0.7.0.

## Decisions

- **Until in the day's +**: once a **Time** is given, an optional **Until** time field appears beside it. Left empty, nothing changes: no `until` is written. Without a time there is no Until (a whole day needs no end; several days are **Edit date…**'s).
- **A time on the same day, not a duration.** "Until" is the word **Edit date…** already uses (one word for one thing, calendar spec §7.8); an end time is what people say ("bis 17:00"). An end across midnight or over several days stays with **Edit date…**.
- **An end before or at the start is refused** in the dialog, with the reason in plain words ("Until is before the time"), and by the server, which validates at its boundary as `calendar set` does.
- The note gets `until: <day>T<HH:mm>` beside `when`, as **Edit date…** writes it.
- **Overlaps** (seeing that one appointment runs during another) are not in this change: 0.7.0.

## The change

- Web: `NewEntryDialog` (`web/src/calendar/NewEntryDialog.tsx`) gains the Until field; `newEntryOf` sends `until` only when given; `NewEntryRequest` (`web/src/calendar/api.ts`) gains `until?: string` (a time, `HH:mm`).
- Server: `NewEntryRequest` (`src/Kumbu.Server/Api/CalendarApi.cs:93`) gains `string? Until`; `CalendarService.AddAsync` (`src/Kumbu.Core/Calendar/CalendarService.cs:255`) takes it: refused without a time, refused unless a valid `HH:mm` after the time, written as `until` on the same day.
- `sh`: no change (`calendar set --until` exists).
- Tests first: xUnit for `AddAsync` (written, refused without time, refused when not after, refused when malformed) and the API; Vitest for the dialog (the field appears only with a time, is sent only when filled, refuses an end before the start).
- Docs: calendar spec §7.5 amended with a pointer here; README if it describes the day's +.
