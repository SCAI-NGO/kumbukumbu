# The calendar

Date: 2026-11-12, amended 2026-11-13 after a human-centric review (§11). Status: approved by the maintainer; builder check done.

The flagship of 0.5.0. Every calendar entry is a note: its time lives in the frontmatter, and the calendar is a view of the notes that have one. Search, links, tags, vaults, history, sync, WebDAV and agents work for entries without anything new to learn. Two users asked for a calendar; the maintainer made it the flagship of 0.5.0 on 2026-11-12, in place of the kill switch (now 0.6.0). Why: it brings more, isn't a fringe feature and lifts work, hobbies, thoughts and ideas directly.

**The principle, "campy".** Kumbu²'s strength, like Obsidian's, is working "in your face": notes are files that make sense together through human-readable semantics, with little abstraction. No enterprise layer on top: the good pencil taken into zero gravity, not the million-dollar pressurised pen. More touching, less abstracting away. Where a simple rule gives an odd-looking case, we accept the case: a deliberate design decision for simplicity and the principle of least astonishment, not an excuse. Outlook is the counter-example throughout.

**Invite and lead.** *Maintainer, 2026-11-13:* the software offers people what we built for them and leads them to it, rather than explaining or asking about its own rules. Kumbu² is human-centric, not process-centric: a screen speaks of what the person wants (a time, a to-do, seeing something early), never of our states, folders or fields.

The decisions below were taken by the maintainer on 2026-11-12, after a UX and ergonomics review that went through its findings one by one; the working notes are in Kumbu² at `/SWE/Kumbu/Ideas/Calendar 2026-11-11.md`. Each decision carries its reason. Claude's own calls are marked *(Claude's call)*.

## 1. Goals, non-goals, anti-goals

Goals: §2 to §8.

Non-goals, each deliberately out of this version:

- **Recurrence.** *Maintainer:* overengineered for what this version should be; the feature may well be better without it, its scope clearer.
- **CalDAV and `.ics` feeds.** After 1.0.0 at the earliest: a lot of effort for possibly very little gain. The field names map onto iCalendar (§2.6), so it needs no migration later.
- **Push notifications, a daemon, e-mail.** Notifications are pulled (§5).
- **Drag and drop** of entries. *Maintainer:* wait for feedback.
- **A combined view of several calendars.** "What's on" covers every vault (§5); a combined week (colours per vault, legend, toggling) is the road to Outlook. *Maintainer:* feedback will give a better product than guessing now.
- **Invitations, attendees, free/busy, scheduling across people, rooms.**
- **Localisation.** The UI is English: "Week 46", not "KW 46".
- Vault templates for day and week notes (moved to 0.6.0; they may hook in later).

Anti-goals, which make this a failure even if it works:

- **Something that is on gets lost.** No entry with a time is ever invisible: not in a vault without a calendar, not with a malformed value (it is listed as a problem, §2.7).
- **Stress.** Overdue is said honestly and shown calmly (§7.9); closing is a dignified ending, never "failed".
- **Two truths.** A board card's status is its column, never a second field (§2.5).
- **A day that shifts.** A date without a time is never converted between time zones (§3).
- **Discreet vaults revealed in the web app.** Neither the calendar, "What's on" nor the badge tells a session anything about a discreet vault it isn't showing (§8).
- **An Outlook.** Every special function needs a reason it would be missed; when in doubt, leave it out.

### An example from end to end

Alice has the vaults `Home` and `Work`. In `Work` she taps **Calendar** in the vault's menu: the calendar opens in the week view, week 46, 9–15 November 2026, empty, with one line inviting her to add something.

- She taps **Today**: the folder `/Work/Calendar/` and `/Work/Calendar/2026-11-12.md` are created with `# Thursday, 12 November 2026` and the cursor below it; she writes what's on today, with `#kumbu` beside one paragraph.
- She opens her note `/Work/Pump/Certificate.md`, taps ⋯ → **Add to calendar…**, picks 15 December, ticks **To do** and sets **Show early: 7 days before**. The note's frontmatter now holds `when: 2026-12-15`, `task: open`, `upcoming: 7d`; its header shows "📅 Tue 15 Dec · To do · ✓ · Later".
- In `Home`, which has no `Calendar/` folder, she gives her note "Zahnarzt" 20 November at 14:00 from its ⋯ menu; nothing asks about folders and the note stays where it is.
- On 8 December the badge on the calendar in the navigation shows 1. "What's on" lists "Certificate · upcoming · in 7 days". On 16 December it says "Certificate · overdue · 1 day", calmly. She renews the certificate and taps ✓; the message "Closed · Undo · Add a reason" appears, she taps **Add a reason** and writes "renewed until 2027"; the frontmatter gets `task: closed`, `closed: 2026-12-16` and `reason: renewed until 2027`, the entry leaves "What's on", and a week later the note's header still says "Closed 16 Dec · renewed until 2027".
- Her agent, at the start of a session, runs `sh calendar notifications` and tells her what's on. On Friday it runs `calendar /Work --week 46 --tag kumbu`, reads the notes it lists with `cat`, and writes her weekly report.
- The month view of `Work` shows November as a stack of weeks, empty days left out; **Show grid** adds a small month grid with the weekend darkened, in which 12 and 15 are marked.

### Reading cost

*Maintainer, 2026-11-13.* The calendar, What's on and the badge read the notes of the vaults they cover on demand, as `rg` and `tags` do: **no persistent index, no stored copy of entries**, so encryption at rest is untouched. Requests are cheap to repeat (a note's version can short-cut re-parsing within a request), and the plan measures it: a vault of 5000 notes should answer What's on in about a second. If it doesn't, the builder reports it rather than adding an index.

### Security boundary

New: the calendar's reading of notes and the commands and endpoints that change an entry's frontmatter (§6, §7), a per-user time zone (§3) and the calendar view state (§7.3). They read and write only notes the caller may read or write today, through the same stores and checks as `edit` (`NoteWriter`): a read-only member of a shared vault sees its entries and can close none. Not touched: authentication, sessions, encryption and keys, sharing and roles, publishing, WebDAV, sync, the board's semantics, the CSPs. Discreet vaults: for agents (MCP, `sh`) they are vaults like any other, as today; in the web app the calendar follows the session's discreet state everywhere (§8).

## 2. An entry is a note with a time

### 2.1 The fields

All optional; a note is an entry when it has `when`, or when its name is a date (§2.3).

```yaml
when: 2026-11-20T14:00      # a day (2026-11-20), a day with a time, or a week (2026-W47)
until: 2026-11-20T15:00     # optional: the end, inclusive for a day
task: open                  # optional: open or closed; only this makes the entry a task
closed: 2026-12-16          # written when a task is closed: the day it was closed
reason: renewed until 2027  # optional, with task: closed (the board's key)
upcoming: 7d                # optional: when it starts to show in "What's on"
tz: Europe/Vienna           # optional: the zone of the times above
```

- **One word for time.** `when`, optionally `until`. *Maintainer:* the most common calendar entry is an appointment ("Zahnarzt 14:00", "Urlaub 3.–14."), not a task; `due` is jargon for most people.
- **Only `task` makes a task.** Without it an entry is an appointment or a dated note, never overdue. With `task: open` or `task: closed` it is a task. Any other value of `task` is a problem (§2.7), not a guess.
- **Why these names and not `date`, `end`, `status`** *(maintainer, after a pressure test with real Obsidian vaults)*: `date:` is very common as a created date from templates, so an imported vault would get thousands of entries; `status:` is even more common, with `draft`, `active`, `done`. The cost, accepted: Obsidian plugins such as Full Calendar read `date` and won't see our entries.
- **`upcoming`** is named after the state it brings ("upcoming from 7 days before"). *Maintainer:* "preroll" was jargon. Read `7d`, `2w`, `12h` and ISO 8601 `P7D`; written always in the short form. No months (28 to 31 days): `4w` or `30d`.
- **Weeks** are ISO 8601 (`2026-W47`, Monday to Sunday, week 1 holds the year's first Thursday), as week numbers are used at work in Austria ("KW 25"). *Maintainer:* a must; the week is a first-class unit.

### 2.2 Values

- `when` and `until`: `YYYY-MM-DD`, `YYYY-MM-DDTHH:mm` (seconds allowed), or `YYYY-Www`; YAML may give a date or a timestamp, or a string: all are read. A time may carry an offset or `Z` (read, never written by us). `until` is of the same kind as `when` (a day with a day, a time with a time); a week's `until` is a week.
- An `until` before `when` is a problem (§2.7).
- Writing: Kumbu² writes `when: 2026-11-20` for a day, `when: 2026-11-20T14:00` for a time, `when: 2026-W47` for a week, unquoted, in this key order after existing keys, and never touches other keys (the board's frontmatter writer, `src/Kumbu.Core/Board/Frontmatter.cs`, is the way: change keys, keep everything else byte for byte).

### 2.3 Day and week notes

- A note named exactly `YYYY-MM-DD.md` is the **day note** of that day, and `YYYY-Www.md` the **week note** of that week, **anywhere in the vault**, without frontmatter. *Maintainer:* the diary in `/SWE/Diary/Week 2026-46/2026-11-12.md` then shows without moving. `2026-11-12 Standup.md` is an ordinary note: give it `when`. Why: anything looser is guessing ("2026-11-12 Export alt", "2026-11 Budget").
- A `when` in the frontmatter wins over the name.
- A day or week note may carry `task`, `upcoming` and the rest like any entry.

### 2.4 Kinds and states

| Kind | Fields | Derived state |
|---|---|---|
| Dated note, appointment | `when`, optionally `until` | upcoming (from `upcoming` before `when`), ongoing (within `when` to `until`, or within the day or week of `when`), then nothing |
| Task | `task: open` | upcoming, ongoing (if it has `until` or is a day or week), **overdue** after its end (`until`, else `when`) |
| Closed task | `task: closed`, `closed`, optionally `reason` | done, also when closed early |

- An entry's **end** is `until`, else `when`; a day ends at the end of that day, a week at the end of its Sunday, a time at that time.
- Without `upcoming`, an entry starts to matter at `when`.
- An appointment with only a time (`when: …T14:00`, no `until`, the common "Zahnarzt 14:00") is ongoing from the start of its day until that time, then nothing *(Claude's call, accepted by the maintainer 2026-11-13: it shows in What's on and the badge on its day)*.
- A task with only a time is ongoing from the start of its day until that time, then overdue.
- **"What's on" holds upcoming, ongoing and overdue, ever; never what isn't relevant yet, never what is done or merely past.** *Maintainer, explicitly.*
- An appointment over a window counts as ongoing all along (a holiday of two weeks is two weeks in "What's on" and the badge). *Maintainer:* a deliberate expression of being campy, one rule, no exceptions.

### 2.5 Board cards

A card (`/<vault>/Board/*.md` with `column`) with a `due` is an entry: its `due` counts as its `when`. **Its column is its status** *(maintainer: one truth, not two)*: an archivable column (Done) is closed, a column needing a reason (Rejected) is closed with the card's `reason`, any other column is open. A card is always a task when it has `due`. Cards carry no `task` and no `closed` *(Claude's call: the board owns their status)*; a card with `task` has it ignored and listed as a problem ("on a card the column is the status"). Closing or reopening a card is the board's (§6.3, §7.8). The board itself is unchanged.

### 2.6 iCalendar, for later

Not built now; the names are chosen so it needs no migration: with `task` an entry is a VTODO (`when` → `DTSTART` if there is an `until`, else `DUE`; `until` → `DUE`; `task: closed` → `STATUS:COMPLETED`, `closed` → `COMPLETED`), without it a VEVENT (`DTSTART`, `DTEND`); `tz` → `TZID`; `upcoming` → a `VALARM` trigger.

### 2.7 Problems, honestly

A value the calendar can't read (`when: tomorrow`, `task: maybe`, `until` before `when`, an unknown `tz`) never makes the entry vanish: the calendar lists it with the note and what is wrong (in the web app under **Dates I couldn't read**, §7.12; for agents as "problems"), as the board does with cards it can't place (`board`'s output, `BoardReader`). `calendar notifications` names how many problems there are and how to see them. Why: honesty is part of the security architecture here, and a silently dropped deadline is the worst failure (anti-goal 1).

## 3. Time zones

*Maintainer, 2026-11-12: "das ist ein schönes design".*

- **A date stays a date.** `when: 2026-11-05` is 5 November for everyone, in every zone; it is never converted, so no day boundary can shift it.
- **A time is local**, as Obsidian's "Date & time" property writes it (`2026-11-20T14:00`, no offset). It is in the zone named by the note's `tz` (IANA, e.g. `Europe/Vienna`, so summer time is right), else in **the user's zone**.
- An offset or `Z` written by hand is read too.
- **Inside, the server computes in UTC** (ordering, states, notifications). UTC never appears in a note unless someone writes it.
- **The user's zone** is a new per-user setting (Settings → Calendar, an IANA zone with a search), preset from the browser's zone (`Intl.DateTimeFormat().resolvedOptions().timeZone`) the first time the web app runs with none stored, changeable later. Without one (an account only used by agents), the server's `TZ` applies, then UTC. Agents' `sh calendar` uses the caller's zone, so "today" and "overdue" mean the same for the person and their agent.
- In a shared vault each member sees times in their own zone; a `tz` on the note pins it for all. A time in a zone other than the viewer's shows the zone: "14:00 (Europe/London)".
- To verify while building: how Obsidian shows a value with an offset. If it shows it as text, that is acceptable (we never write offsets).

## 4. Where the calendar lives

*Maintainer, 2026-11-12, after the domain collision with the board: "das Board ist ein Ort, der Kalender ist eine Sicht"; he had reached the same resolution on a walk.*

- The board decides by location (every note directly in `/<vault>/Board/` must be a card, flat, or it is a problem); the calendar decides by property. A shared folder would soften the board's problem detection, so they stay apart.
- **Every vault has a calendar.** No `Calendar.md`, no definition file, no "Create calendar". *Maintainer, 2026-11-13:* nobody should have to learn "folder = calendar"; the calendar is a view, so every vault has one, and an empty one invites (§7.2). This also spares the tree a scan of every vault to decide what its menu says. The folder `/<vault>/Calendar/` appears quietly the first time something needs a home there (**Today**, **This week**, a day's +).
- `Calendar/` is the home of the additional layer: day and week notes, and the entries made by tapping a day (§7.5). Sub-folders are fine (`Calendar/Week 2026-46/2026-11-12.md`): day notes are found by name, not by place, so no naming pattern is needed.
- **The calendar view of a vault shows every entry of the vault**, wherever its note lives, board cards included. New entries made in the calendar land in `Calendar/`; a note given a day stays where it is. *Maintainer:* letting people organise and work with their notes their own way is a virtue (campy), not a mess the calendar should tidy. Notes in `_archive/` are left out, as `backlinks` leaves them out.
- **"What's on", the badge and `calendar notifications` cover every vault the caller can read, with or without `Calendar/`.** Time is a property; nothing that is on gets lost.

## 5. Notifications are pulled

- No daemon, no push. Whoever wants them asks: the web app's "What's on" and badge (§7.6, §7.7), an agent's `sh calendar notifications`, for instance from a `CLAUDE.md` line such as "At the start of a session, get the calendar notifications from Kumbu² and tell me what's on".
- The list: overdue first (longest overdue first), then ongoing (ending soonest first), then upcoming (soonest first). Each item: title, vault and path, its state with a human span ("overdue · 3 weeks", "ongoing · until Fri", "upcoming · in 7 days"), and whether it is a task.
- `--tag` narrows it (§6.1).

## 6. For agents: `sh calendar`

*Maintainer: agents make, move and close entries, and turn notes into entries and back, through `sh calendar`.* The board's writes go through its own MCP tool; the calendar's go through `sh`, as `mv`, `archive` and `publish` already do. Editing the frontmatter by hand (`edit`, `write`) stays allowed: the commands are the convenient, checked way, and write the format of §2.2.

### 6.1 Reading

```
calendar <vault> [--week 2026-W46 | --week 46 | --month 2026-11] [--tag <tag>]
calendar notifications [--tag <tag>]
```

- `calendar /Work` prints the current week of the caller's zone: week entries and entries spanning several days at the top (with their span), then each day with its entries (time, title, path, kind, state), then problems. `--week 46` is week 46 of the current ISO year. `--month` prints the month as its weeks (§7.4).
- A vault without `Calendar/` answers like any other (§4).
- `--tag` as in `board` and `rg` (nested tags, with or without `#`, ignoring case; frontmatter and inline tags, `TagReader`): whole notes, not lines. For a weekly report the agent reads the listed notes with `cat` (or combines with `rg --tag`). *Maintainer:* no `--lines` option.
- `calendar` is a reader: it runs on a checkpoint as the others in `Shell.Readers` do.

### 6.2 Writing

```
calendar set <note> --when <value> [--until <value>] [--task | --no-task] [--upcoming 7d] [--tz <zone>] [--reason <text>]
calendar move <note> <when>
calendar close <note> [--reason <text>]
calendar reopen <note>
calendar unset <note>
```

- `set` makes an existing note an entry or changes it; making a new entry is writing the note, then `set` (an entry *is* a note: no `create`). Each option sets its key; `--no-task` removes `task`, `closed` and `reason`.
- `move` sets a new `when`; a window keeps its length (`until` moves with it).
- `close` sets `task: closed`, `closed` (today in the caller's zone) and `reason` if given; `reopen` sets `task: open` and removes `closed` and `reason`. `calendar set <note> --reason <text>` adds or changes the reason of a closed task later. On an entry without `task` they refuse with a hint (`calendar set <note> --task`).
- `unset` removes `when`, `until`, `task`, `closed`, `reason`, `upcoming` and `tz`; the note stays. On a day or week note by name it refuses honestly: "<path> is the day note of 12 November by its name; rename it to take it out of the calendar".
- **On a board card**, `close`, `reopen` and `move` refuse and name the board tool ("on a card the column is the status: move it with the board tool"); `set` refuses every option but `--upcoming` likewise (its `due` and status are the board's). One truth. *Maintainer, 2026-11-13:* the basis for this version; he wants to experience how it works with cards in practice and expects feedback.
- Each write goes through `NoteWriter` with the caller's permissions and leaves a version in the history, like `edit`; the answer names the new version token. Values are validated (§2.2); a bad one is refused with what would be valid.
- `calendar`'s help and the manual (`src/Kumbu.Core/Sh/Manual.cs`) say all of this in the style of `board`'s.

## 7. The web app

*Maintainer: not Google Calendar or Outlook; few special functions; the user makes an entry, closes one and sees what is going on, intuitively. Effort and the central function go into the list.*

### 7.1 Navigation

As the board's (`web/src/shell/TreeDrawer.tsx`, the board's menu entry): every vault's menu in the tree has **Calendar** (§4). The calendar is a page of its own. **What's on** is in the navigation, with the badge (§7.7).

### 7.2 Week view (the default)

- Semantically a list of the seven days, each a heading ("Thursday, 12 November") with its entries beneath; laid out as seven columns on wide screens and stacked on a phone, from one markup. A screen reader reads it as a document; heading navigation jumps from day to day.
- At the top: "Week 46 · 9–15 November 2026", previous and next week, **Today**, **This week** (§7.5), the view switch (week, month), **Show grid** (§7.4), the tag filter (§7.10).
- Week entries (`when: 2026-W46`, week notes) stand above the days, and so does **every entry spanning more than one day**, once in each week it touches, with its span ("Urlaub · Mon 3 – Fri 14") *(Claude's call, accepted by the maintainer 2026-11-13: it is missing from no week and crowds no day; nothing that is on gets lost)*.
- **Also ongoing** *(maintainer, 2026-11-13)*: each day has a collapsed line "Also ongoing (2)" that opens to the entries spanning several days that touch it, so a day shows everything on it. Collapsed by default; left out when there are none. The day heading's count (§7.11) counts the day's own entries; "Also ongoing" has its own count.
- An entry shows its time (if any), title, state with icon and word, and a small mark for its kind. **A to-do and a note given a day are told apart at a glance** *(maintainer: memories and notes given to a day are not tasks)*: a to-do has a ✓ box and **Later**, a note given a day only its title. Tapping opens the note.
- **An empty week invites** *(maintainer: "lädt ihn ein")*: beneath the days one calm line, "Nothing this week yet. Tap a day to add something, or give any note a day from its ⋯ menu. Your AI can do it too: \"put this on Friday\".", gone with the first entry. No tour, no pop-up.
- Tapping a day's empty space or its **+** makes an entry on that day (§7.5).

### 7.3 The last view, per user

The view (week or month) is remembered **per user and per calendar** on the server, and opens next time. *Maintainer:* each person has their preference and working mode; some vaults are monthly, others weekly. The default is the week.

### 7.4 Month view and grid

- **The month is a stack of weeks** *(maintainer, after "do we need a table?")*: each week a heading ("Week 46 · 9–15 Nov", the current week marked) with its entries by day; **empty days are left out**; an empty week says "nothing yet"; week entries and entries spanning several days stand at the top of each week they touch, with their span, and an empty day holding only those stays left out. Same on phone and desktop. Tapping a week heading opens that week. Example:

  ```
  November 2026

  Week 45 · 2–8 Nov
     Tue 3   Zahnarzt 14:00
     Fri 6   ⚠ Certificate · overdue

  Week 46 · 9–15 Nov        this week
     Mon 9   2026-11-09 (day note)
     Sun 15  Release planning

  Week 47 · 16–22 Nov
     Week    Look at calendar templates again
     Fri 20  0.5.0-pre.1 · upcoming

  Week 48 · 23–29 Nov
     nothing yet
  ```

- **Show grid**, in week and month view, adds a small month grid you can show and hide: "which weekday is 30 November", "is there something on that day already". The weekend darkened, a week-number column, days with entries marked, today marked; tapping a day jumps the list to it (in the week view: opens its week). A plain table, no arrow-key grid logic: the list is its full equivalent for screen readers and keyboards. *Maintainer:* "yes, we have this view too"; rethink if feedback asks.
- Whether the grid is shown is remembered with the view (§7.3).

### 7.5 Making entries

- **Today** opens the day note of today (`YYYY-MM-DD.md`, anywhere in the vault; several: the last changed) or creates `/<vault>/Calendar/YYYY-MM-DD.md`; **This week** the same for `YYYY-Www.md`. A new day note starts with `# Thursday, 12 November 2026`, a week note with `# Week 46, 9–15 November 2026`, the cursor on the line below. *Maintainer:* an empty page puts people off; he sees himself writing what's on today under the date. The 0.4.0 rule that names a new note from its first heading doesn't apply (it is for "Untitled …" only).
- **A day's +**: a small dialog asks for a **title** and an optional **time**, then creates `/<vault>/Calendar/<title>.md` with `when` that day (and time) and opens it. *Maintainer, 2026-11-13:* the appointment with a time ("Zahnarzt 14:00") is the most common entry, so the time is there for every entry, not only for to-dos. **☐ To do** in the same dialog adds `task: open`, and then **Show early: … before** appears. Nothing more; the rest is the note's frontmatter.
- Names follow the existing rules for new notes (cleaning, "Title 2" on a clash).

### 7.6 What's on

A page of its own: the list of §5 over every vault the session shows, each item with its vault, tapping opens the note; an open task has ✓ (§7.8) and **Later** (§7.13). Empty: "Nothing on. Enjoy it." *(Claude's call: a calm sentence, not a blank page.)* The tag filter works here too. A line at the bottom names dates that couldn't be read, if any, with a link to them (§7.12).

### 7.7 The badge

*Maintainer: one rule.* Its number is the length of "What's on" for the vaults the session shows; the scheme's warning colour when anything is overdue, a calm one otherwise; no badge at zero. On What's on in the navigation, on the **Calendar** entry of the active vault's menu in the tree and beside each vault in the vault switcher (that vault's share of the count), on the phone's menu button *(maintainer, 2026-11-13)*; and on the installed app's icon through the Badging API (`navigator.setAppBadge`, set while the app runs, so still pull; nothing where it isn't supported). Its count is refreshed when the app opens, when it comes back to the foreground, and after a change to an entry.

### 7.8 From the note

- The note's ⋯ menu: **Add to calendar…**, or for an entry **Edit date…** and **Remove from calendar**. The dialog: **When** (a day, optionally with a time, or a week), **Until** (optional), **☐ To do** ("something to get done, with a ✓"; sets `task: open`; only then **Show early: … before**, which writes `upcoming`). The same words as the day's + (§7.5): one word for one thing. `tz` stays a frontmatter matter. The dialog never asks about folders (§4); the note stays where it is.
- **A line in the note's header** for every entry: "📅 Tue 15 Dec · To do · ✓ · Later"; the date opens the calendar at that week.
- **✓ closes at once**, without a dialog: closing is the most frequent action. A short message follows, "Closed · Undo · Add a reason"; **Add a reason** opens a one-line field. *Maintainer, 2026-11-13:* the reason belongs to the user, a memory for later ("why did I drop this idea?"), and is often empty because something is simply done.
- **A closed task's header shows when and why**: "📅 Tue 15 Dec · Closed 16 Dec · renewed until 2027 · Reopen", with **Add a reason** or **Edit reason** beside it, so the reason can be added a week later too. *Maintainer:* "PERFEKT".
- A day or week note by name has no Remove: "This is the day note of 12 November (by its name)". *Maintainer:* honest, instead of a button that can't work.
- A board card's line shows its `due` and its column ("📅 Fri 20 Nov · In progress"); its action is **Move to Done** through the board, and only when the board has exactly one archivable column; with none or several, a link to the board *(Claude's call)*.
- Writes from the web app use the same server code as `sh calendar` (§6.2), versioned like any edit, with the conflict handling the editor has today.

### 7.9 The tone

*Maintainer: the opposite of Microsoft-style stress-making.* The word stays "overdue" (honest), shown calmly: a small warning icon and the word in the scheme's warning colour, never a red box or a red week, and the span beside it ("overdue · 3 weeks"). Closing with a reason ("not needed any more") is a full ending, called "closed", never "failed" or "cancelled".

### 7.10 Tags

A tag filter in the week view, month view and What's on, as the board's tag filter where it has one: whole notes with that tag or one nested below it. *Maintainer:* enormously helpful for weekly reports.

### 7.11 Accessibility

Status never by colour alone (icon and word); WCAG AA contrast in every scheme (`web/src/theme/schemes.ts`); targets at least 48 px; motion only without `prefers-reduced-motion`; every control reachable and labelled; the date inputs are the browser's own (`<input type="date">`, `type="time"`, `type="week"` where supported, else a date input whose week is taken). A day heading is read as "Thursday, 12 November, 3 entries, 1 overdue".

## 8. Discreet and shared vaults

- **Agents:** discreet vaults are ordinary vaults (as today): `sh calendar` reads and writes them.
- **Web app:** the calendar, What's on, the badge (count and colour) and the note header follow the session's discreet state: entries of a discreet vault count and show only while that vault is shown. The app-icon badge too. No answer, count or colour lets a session tell that hidden vaults have entries.
- **Shared vaults:** an entry is the vault's, so every member sees it in their calendar and What's on, in their own zone *(Claude's call: one rule; a task in a shared vault is everyone's to see, as a card on a shared board is)*. Writing follows the member's role.

### 7.12 Dates I couldn't read

The web app's name for §2.7's problems, in the calendar views and at the bottom of What's on. Each line names the note and says in plain words what is wrong ("`when: tomorrow` – I need a date like 2026-11-20"), with **Fix…**, which opens the date dialog (§7.8) filled with what could be read; tapping the note opens it. *Maintainer, 2026-11-13:* "Problems" sounds like an error log; this says what the person can do.

### 7.13 Later

*Maintainer, 2026-11-13: "a function I would so often wish for from calendars".* **Every open task**, whatever its state (not yet relevant, upcoming, ongoing or overdue), has **Later** beside ✓, in What's on, the calendar views and the note's header *(maintainer, 2026-11-13, after the review of spec and brief)*: **+1 day**, **+1 week**, **Pick a date…**. It is `calendar move` (§6.2): the new `when` is set, a window keeps its length, a time of day is kept. **+1 day and +1 week count from the later of today and the entry's current `when`**, so Later never moves anything earlier: an overdue task goes to tomorrow or a week from today, an upcoming one a day or a week past its date. On a week entry, +1 week moves it to the next week and +1 day is not offered.

**Postpone and more time** *(maintainer, 2026-11-13)*. A task with a window (`until`) offers two things under **Later**: **Postpone** moves the whole task, start and end together (as above), and **More time** moves only its end (`until`), the start stays. The same three choices under each, counted from the later of today and the end. A task without `until` has only Postpone, since its `when` is its end, and the menu shows no headings then. For agents, Postpone is `calendar move`, More time is `calendar set <note> --until <value>`. *(Claude's call, accepted by the maintainer 2026-11-13: one rule, intuitive in every case he tried.)* Why: someone who didn't get to something moves it on honestly in two taps instead of facing a growing pile (anti-goal: stress). On a board card **Later** (Postpone only: a card has no `until`) sets the card's `due` through the board's code; a card's `due` is a day, so there is no time to keep *(Claude's call: rescheduling doesn't touch the column, so it is no second truth)*.

## 9. Tests

Test-first per section, at the level the repository tests things today: Core tests (xUnit) for parsing and writing the fields, weeks, day and week notes by name, states at their boundaries (with an injected clock and zones on both sides of a summer-time change), board cards, problems, every `calendar` command and its refusals; server tests for the endpoints, permissions (a read-only member can't close), discreet vaults in the web endpoints, the per-user zone and view; Vitest for the views, the dialogs, the header line, the badge and the tag filter. Times in tests come from the injected `TimeProvider`, never the wall clock.

## 10. Documentation

README (the calendar, the fields, the time-zone setting), `calendar`'s help and the manual, the Welcome vault's note that introduces boards or a new short one ("Ask your AI: what's on this week?"), `docs/concept.md` if it lists features, `docs/security-architecture.md` for the new endpoints and the discreet rule of §8.

## 11. The human-centric review, 2026-11-13

The maintainer asked for a read of this spec through the eyes of a user who expects Kumbu² to be human-centric, not process-centric. Seven findings, decided one by one and folded in above: the time in the day's + (§7.5), closing at once with the reason as the user's own memory and `closed` as its date (§2.1, §7.8), **Later** (§7.13), one word for one thing ("To do", "Show early", §7.5, §7.8), no question about folders (§4), the inviting empty week (§7.2), and "Dates I couldn't read" (§7.12). The working notes are in the idea note's last section.

A builder check followed (a fresh agent reading the spec as if planning from it). Its four questions, settled the same day: the reading cost (no index, measured), entries spanning several days in the views (once per week, plus **Also ongoing** per day, the maintainer's addition), a timed appointment without `until` (ongoing on its day until its time), and what **Later** counts from (the later of today and `when`). Every vault's menu now has **Calendar**. The review of spec and brief before the overnighter added: **Later** on every open task, **Postpone** and **More time** for a task with a window, where the per-vault badge sits, and what `set` does on a board card.
