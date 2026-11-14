# Overnighter report: the calendar

Run of 2026-11-13/14 on `feature/calendar`, from `docs/superpowers/overnighter/2026-11-13-calendar-brief.md` and the spec `docs/superpowers/specs/2026-11-12-calendar-design.md` (unchanged). Pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/197.

## Environment

- **.NET:** 10.0.403 as installed (`global.json` rolls forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Node:** Node 26.13.1 wasn't there, so I installed it by hand into `/opt/node26` from the nodejs.org tarball. pnpm 10.32.1 came from `npm install -g`.
- **Docker:** `dockerd` wasn't running, so I started it by hand. The test MongoDB was `mongo:7.0` with `--ulimit nofile=20000:20000` on 27017. A second one on 27018 let the subagents run server tests while a gate ran on 27017. `TESTCONTAINERS_RYUK_DISABLED=true`.
- **Time zones and tokenizer:** `tzdata` was present (`/usr/share/zoneinfo/Europe/Vienna` exists). I fetched the tokenizer and checked its hash; `KUMBU_TOKENIZER` was set.
- **Machine:** 4 CPUs, 15 GB RAM.
- **Task 0** took about 10 minutes of setup; the baseline gate then took about 55 minutes.
- **Restart:** the worker process restarted once during the run. `/tmp` survived; `dockerd` and the two MongoDB containers had to be started again.
- **Superpowers:** these skills were available. I used `writing-plans`, then subagent-driven work: a fresh subagent per task, and a fresh reviewer per task or per pair of tasks. **A deviation:** each task had one combined spec-and-quality review instead of two separate ones, to save time. Some tasks ran in parallel where they touched different files (server and web, or a separate git worktree with its own MongoDB). Subagents never committed, pushed or switched branches; I did all of that.

## The plan and its review

The plan is `docs/superpowers/plans/2026-11-13-calendar.md`. A fresh subagent reviewed it, given the spec, the brief and the plan. It found 21 points, and I folded all of them into the plan (commit `adae5ee`). The ones that mattered most:

1. **Discreet vault names:** the `/calendar/:vault` route was missing from `neutralWindow`'s `vaultRoute`, which would have leaked a discreet vault's name into the address bar. Added in Task 10.
2. **Byte preservation:** `NoteWriter.WriteAsync` normalises CRLF, so "every other byte" would break. Writes follow `BoardService`'s compare-and-swap pattern instead.
3. **Quoting:** `BoardService.Scalar` would quote `2026-11-20T14:00`, `2026-W47` and `7d`. I extracted `FrontmatterWrites`; calendar values are written raw, and only `reason` is quoted.
4. **Expected values:** the plan's sample output (Task 5) and one span (Task 3) had wrong values.
5. **Open editor:** an editor still in Write mode keeps its own base version, so a calendar write must end Write mode first.
6. **Discreet answers:** the write endpoints must check the vault first, so a hidden vault answers as an unknown one.
7. **Smaller points:**
   - `/account/calendar` is session-only;
   - `notifications` on a checkpoint;
   - the app-icon badge on hiding and sign-out;
   - a broken frontmatter that has a `when` must not vanish;
   - the card refusal's error kind;
   - an injected server zone for tests;
   - the view state after a key rotation and on concurrent updates;
   - title cleaning;
   - offsets never written;
   - four signatures that wouldn't compile;
   - two weak test assertions;
   - a month test across the year's end;
   - "Move to <column>";
   - push after every task and stage files by name.

## Task 0, planned against measured

Release build with `KUMBU_TOKENIZER`, on `feature/calendar` at `cfc840b`, before any change. Every number matches what the brief expected.

| Line | Planned | Measured |
|---|---|---|
| core | 2,484, 3 skipped | total 2484, failed 0, skipped 3 |
| server: Api, Mcp, Auth, Net | 847 | total 847, failed 0 |
| server: Accounts | 413 | total 413, failed 0 |
| server: Sharing | 353 | total 353, failed 0 |
| server: Welcome | 20 | total 20, failed 0 |
| server: Vaults, Skills, Search, Metrics | 780, 6 skipped | total 780, failed 0, skipped 6 |
| server: the rest | 1,164, 1 skipped | total 1164, failed 0, skipped 1 |
| web | 141 files, 2,503 tests | Test Files 141 passed, Tests 2503 passed |
| web build | green, chunk-size warning | green (`✓ built in 13.34s`), the known chunk-size warning |

No flaky test failed in the baseline.

## The tasks, with commits and real test output

Each task went red, then green, then through its mutations. "Red" is the first run, against the missing code. The lines below are quoted from `dotnet test` and `pnpm test`.

**Task 1: values** — `c708e5e`, then the review fixes in `104e3c4`.
- Red: `error CS0234: … 'Calendar' does not exist in the namespace 'Kumbu.Core'`.
- Green: `total: 52 failed: 0`; after the fixes, `total: 59 failed: 0`.

**Task 2: the entry reader** — `6c43c37`, then the review fixes in `00f7727` and `e508f1b`.
- Red: CS0234/CS0246.
- Green: `total: 23 failed: 0`; the calendar namespace `total: 82 failed: 0`; after the fixes, `total: 119 failed: 0`.

**Task 3: states and spans** — `293c220`, then the review fix in `8c3d1b5`.
- Red: CS0246.
- Green: `total: 22 failed: 0`; the namespace `total: 105 failed: 0`.

**Task 4: layout, What's on and the reading cost** — `2cbe2ba`.
- Red: CS0246.
- Green: 12/12; the namespace `total: 118 failed: 0 skipped: 1` (the skip is the explicit cost test); the whole core suite 2598 passed, 4 skipped.

**Task 5: `sh calendar` reads** — `237b311`.
- Red: `CalendarCommandTests.cs(12,52): error CS0246: … 'ICallerZone'`.
- Green: 34/34, and with HelpTests, ShellTests, CheckpointTests and BoardCommandTests together 143 total, 0 failed; core 2637 total, 4 skipped, 0 failed.

**Task 6: `sh calendar` writes** — `4abf9e2`, then the review fixes in `5b4318e` and `d6253f3`.
- Red: `CalendarServiceTests.cs(27,13): error CS0246: … 'CalendarService'`.
- Green: 90/90; the board's test classes 81/0; core 2693 total, 0 failed, 4 skipped; after the fixes, 2702 total, 0 failed, 4 skipped.

**Task 7: the zone and the view settings** — `c8068ab`.
- Red: a compile error, then 10 of 10 failed.
- Green: 10/10; AccountApiTests 74/74; the Discreet namespace 146/146; PasswordWindowTests 70/70.

**Task 8: read endpoints** — `101980c`.
- Red: 12 of 12 failed (404).
- Green: the Calendar namespace 23 total, 22 passed, 1 skipped (explicit); PasswordWindowTests 70/70; HistoryApiTests 20/20; ConsoleApiTests and ToolMetricsTests 90/0.

**Task 9: write endpoints** — `d7e824b`, then `308ba88`.
- Red: 12 failed.
- Green: the Calendar namespace 34 passed plus 1 explicit; PasswordWindowTests 70; HistoryApiTests 20; BoardApiTests 15; after the review fix, CalendarWriteApiTests 10/10.

**Task 10: the web week view** — `68ea744`.
- Red: 20 failed.
- Green: 170 in its set; the full web suite 143 files, 2529 tests. One failure under load in `commentRanges.test.tsx` "reads comments in linear time" was green 3 of 3 times alone; that file wasn't touched.

**Task 11: the header line, the date dialog, ✓ and Later** — `82aba7e`.
- Red: with the implementation taken out, 24 failed.
- Green: 918 in calendar, note and shell; the full web suite 146 files, 2556 tests, 0 failed.

**Task 12: What's on and the badge** — `7bb8c4a`.
- Red: 8 failed.
- Green: the full web suite 149 files, 2581 tests, 0 failed.

**Task 13: Settings → Calendar** — `a9c9cab`.
- Red: 7 of 7 failed (against a stub).
- Green: 7/7.

**Task 14: month, grid, Also ongoing, tags, Dates I couldn't read** — `569bf47`, then `a0e7832`.
- Red: 19 failed.
- Green: `src/calendar` 90/90; the full web suite 152 files, 2604 tests, 0 failed; the web console 209/209.

**Task 15: documentation** — `513482a`.
- Welcome 20/20; HandshakeInstructionsTests and McpClientTests 42/42; core 2698 passed, 4 skipped.

**Task 16: the UX check, the security review and the final gate** — `e79b1a3`, `045350f`. Their outcomes are in the sections below.

**Phase gates.** I ran the full gate twice before the final one: after Task 6 (`/tmp` gate6) and after Task 13 (gate12). Each time the only failures were tests that pin the shell's command list:
- after Task 6: `ConsoleApiTests.Every_shell_command_is_classified` and `ToolMetricsTests.Known_commands_are_the_shells`;
- after Task 13: `ConsoleFixtureTests.Allowed_is_the_servers`.

Each was fixed in the next commit (`5b4318e`, `a0e7832`). Gate12, otherwise:

| Line | Total | Failed | Skipped |
|---|---|---|---|
| core | 2702 | 0 | 4 |
| Accounts | 413 | 0 | 0 |
| Sharing | 353 | 0 | 0 |
| Welcome | 20 | 0 | 0 |
| Vaults, Skills, Search, Metrics | 780 | 0 | 6 |
| the rest | 1199 | 0 | 2 |

The rest grew by 35, exactly the new `Kumbu.Server.Tests.Calendar` namespace, so the gate's last line runs it. The web steps of those two gates ran while web agents were editing, so they count for nothing.

## Every mutation

Each mutation was applied on its own, run until red, then restored (with `git checkout`, or from a copy for new files, then touched).

**Task 1**
- A week's Monday made Sunday: 3 tests failed.
- A day's end without the +1 day: 2 failed.
- The spring gap made the hour before: 1 failed.
- `Upcoming` accepting `m`: 1 failed.

**Task 2**
- `_archive` check dropped: `Archived_notes_are_left_out` failed.
- The name winning over `when`: `When_in_the_frontmatter_wins_over_the_name` failed.
- A card's `task` taken as its status: `A_card_with_task_has_it_ignored_and_listed` failed.
- `task: maybe` read as open: 2 failed.

**Task 3**
- A dated entry able to be overdue: 6 failed.
- A timed appointment ending at the end of its day: 8 failed.
- Upcoming counted from the end: 2, 5 and 1 failed in three variants.
- The viewer's zone winning over `tz`: 1 failed.

**Task 4**
- Spans only in their first week: 2 failed.
- What's on with overdue newest first: 1 failed.
- The tag filter counting uses, not notes: 1 failed.
- A month dropping its last week: 1 failed.
- A time's day taken as written: 1 failed.

**Task 5**
- The current week taken in UTC: 1 failed.
- Spans dropped from the week: 5 failed.
- The problems line dropped from notifications: 2 failed.
- The checkpoint refusal removed: 5 cases failed.
- The year check removed: 4 cases failed.

**Task 6**
- Later counted from `when` only: 3 failed.
- `move` dropping `until`: 2 failed.
- `close` taking today in UTC: 1 failed.
- The card refusal removed: 2 failed.
- `Attempts = 1`: 1 failed.

**Task 7**
- The view stored unsealed: `The_view_is_sealed` failed.
- An unknown zone accepted: 1 failed.
- The zone resolved without the user's setting: `Sh_calendar_uses_the_users_zone` failed.

**Task 8**
- What's on through an unfiltered store: `Whats_on_says_nothing_of_a_hidden_vault` failed.
- Per-vault overdue taken from all items: 1 failed.
- The zone fixed at UTC: 2 failed.

**Task 9**
- The role check removed from the service: 2 failed (the read-only member and the read key).
- Conflict mapped to 400: 1 failed.
- The Later pre-check removed: 1 failed.
- The closed-card check on `boardAction` removed: 1 failed.

**Task 10**
- The empty-week line dropped: 1 failed.
- ✓ shown on a dated note: 1 failed.
- Today navigating without the POST: 2 failed.

**Task 11**
- `beforeRead` skipped: 1 failed.
- Undo calling close: 1 failed.
- Later's headings always shown: 5 failed.

**Task 12**
- The badge shown at 0: 2 failed.
- The warning colour regardless of overdue: 1 failed.
- Seven more each failed their test: no reset on hiding, no `clearAppBadge` on hiding, no clear on sign-out, no refetch on focus, ✓ and Later regardless of role, no invalidation on reveal, the items query dropped.

**Task 13**
- The preset firing although a zone is stored: 1 failed.

**Task 14**
- The month showing empty days: 2 failed.
- The grid not remembered: 3 failed.
- Also ongoing open by default: 1 failed.
- The preset taken out of `RequireSession`: 4 failed.

**Security fix**
- The console's old check: ConsoleApiTests 88 total, 4 failed; with the fix, 88 total, 0 failed.

## Reading cost for 5000 notes

There is no index (the spec's choice).

**In Core** (Release, `InMemoryNoteStore`):
- The vault: 5000 notes of 2 KB, one in ten with `when`, one in fifty a task with `upcoming: 7d`, and a board of 50 cards. That gives 525 entries, 64 of them on.
- Cold, JIT included: 232–282 ms.
- Warm, with the parse cache: 13–28 ms.
- The same notes as new strings (no cache hit, as the server sees them after decrypting): 62–95 ms.
- `dotnet test` doesn't print the output; it shows with `dotnet tests/Kumbu.Core.Tests/bin/Release/net10.0/Kumbu.Core.Tests.dll -method "*Whats_on_for_5000_notes" -explicit only -showLiveOutput`.

**Over the server** (Debug, local MongoDB):
- The setup: 5000 notes written through `EncryptedNoteStore`, the vault locked so the request decrypts it from MongoDB.
- Cold: 459, 483 and 508 ms in three runs.
- Warm: about 30 ms (29–114).
- Run with `tests/Kumbu.Server.Tests/bin/Debug/net10.0/Kumbu.Server.Tests -method "*Whats_on_for_5000_notes_over_the_server" -explicit only -showLiveOutput`.

Both are well under the spec's "about a second", so no index is needed.

## The UX check and what I did with it

A fresh subagent ran the server against the test MongoDB on 27018, with the web build served through `ASPNETCORE_WEBROOT` from a copy. It walked the end-to-end example with Playwright and Chromium at 1280 and 375 px, in light and dark, as Alice (Home discreet, Work) and as Bob, a read-only member of Work.

**Fixed** (`045350f`):

1. **High:** a to-do's title was crushed to one letter per line in the week's seven columns. Entries now lay out by their own width: the title takes the full width and wraps by words.
2. The calendar page didn't name its vault. It now does, above the week title.
3. **Add a reason** opened far from the message, with no label and no Save, and the message kept offering it. Now the field opens in the message, labelled "Reason", with Save; saving closes the message; and "Closed · Undo" keeps its spaces.
4. A card's unreadable date offered **Fix…**, which would write `when` and `task` on a card. It now offers **Open the board**.
5. Targets under 48 px (✓, Later, Reopen, the reason buttons, the date link, back, the Day/Week switch, the links in Dates I couldn't read, the entry titles) are now at least 48 px.
6. Later gave no feedback. It now says "Moved to Sun 15 Nov · Undo", or "More time until … · Undo".
7. The Show early row broke: the field was full width. Fixed.
8. The phone's menu badge covered the breadcrumb. Fixed.
9. The phone's week title was cut off. It now wraps.

**Recorded, not changed** (for Chris):

- The raw properties box under the note's header repeats our field names (`when`, `task`, `upcoming`). It is an existing feature, but it works against "never our fields".
- Day notes are titled by their file name ("2026-11-14") in What's on and in the views.
- A new day note counts as "ongoing · today" in What's on and the badge. That follows spec §2.4, but surprises.
- "nothing yet" under weeks that are already past reads oddly. It is the spec's wording.
- The grid marks only the day an entry starts, not every day of a window.
- In the dark scheme, ongoing (amber) and overdue (orange-red) are close in colour. Icon and word tell them apart and contrast passes, but Show grid's pressed state is faint.
- A hidden discreet vault's calendar says "There is no vault 'Home'." That is right for privacy, but it is model wording.
- An overdue to-do's header line doesn't say it is overdue; the spec's example has none either.

**What worked:**

- the vault menu's Calendar with its badge, and the switcher's badges;
- the empty week's sentence;
- Today, with its heading and the cursor below;
- the dialog's words;
- ✓ and Undo;
- the closed header "Tue 15 Dec · Closed 14 Nov · renewed until 2027 · Reopen · Edit reason";
- Later with Postpone and More time;
- a day's +;
- the month, the grid and Also ongoing;
- "`when: tomorrow` – I need a date like 2026-11-20" with Fix…;
- the calm "⚠ overdue · 13 days";
- Settings → Calendar;
- Bob without any write control;
- What's on going from 4 to 3 when Home is hidden, and back when it is shown;
- no horizontal overflow at 375 px.

## The security review and what I did with it

A fresh subagent got the spec's anti-goals, "Reading cost", the security boundary, §8 and the diff `2aeb72c...HEAD`, and no reasoning from me.

**One confirmed hole, fixed** (`e79b1a3`):
- The read-only web console refused a calendar write only when the verb was the second token. So `calendar -- close …`, `calendar --task set …` and `calendar --when=… set …` ran writes as the session. Roles still applied, so nothing was gained above the caller's rights.
- The console now asks `CalendarCommand.WriteVerb`, which reads the line as the command does. The new cases are in ConsoleApiTests.

**No hole found** on these:
- rights on every REST route and `sh` form, Later on cards, Today, This week and the day's +;
- a read-only member changing anything;
- discreet vaults (the server reads through the session's store, a hidden vault gives the same 404 as an unknown one, and the client caches are cleared or reset on hiding);
- new stores (none; the cache is weak and keyed by the content string, and logs carry ids and exception types only);
- frontmatter writes (only `reason` is free text, and it is escaped by `Scalar` and checked on read-back; titles never reach YAML and can't leave `Calendar/`);
- the board's behaviour (unchanged);
- `Auth/`, `Crypto/`, `Dav/` and `Security.cs` (untouched).

**Lesser points, recorded:**
- The OS app-icon badge keeps a count that includes discreet entries if the app is closed while those vaults are shown; it is only cleared while the app runs.
- The sealed view map grows by one entry per vault id and is never pruned, so its length shows how many calendars she opened.
- Calendar writes skip `NoteWriter.Normalize`, as `BoardService` does.
- Entry titles have no length limit beyond the request body.
- A rooted `tz` relies on .NET refusing it.
- An open "Add a reason" field on What's on, saved after hiding, gets the hidden vault's 404 sentence.

## Decisions made without Chris

Each was settled with the brief's rules: break nothing, the spirit of the application, no new hole, the spec.

1. **Where the per-user settings live** (open point 1).
   - Options: `/account/preferences`, a new collection, or fields on the user document.
   - `/account/preferences` is a full replace that would switch the download password off, and a new collection is a new store.
   - Chosen: fields on the user document.
     - `calendarZone` is plain, like `askPasswordForDownloads`; it names no vault.
     - `calendarViews` is sealed with `VaultCrypto` under `Derive(userKey, "kumbu/v1/calendar-views", userId)`, keyed by vault id, with the same KeyId check and compare-and-swap as the discreet list.
   - Key rotation isn't touched, since Crypto is off limits: after a rotation the views fall back to the week.
   - The zone PUT has no 429. The preferences PUT is limited only when it checks a password, and a general limit would need `Security.cs`.
2. **The entry reader** (open point 2).
   - Options: rules in each caller, or one reader.
   - Chosen: one static `EntryReader.Read(note, board)`, returning an entry, a problem or neither. A card with a stray `task` returns both.
   - The parsed frontmatter is cached per content string, as `TagReader` does.
   - States come from `EntryStates.Of(entry, now, zone)`, where `now` is the clock already injected (`writer.Now`, `FakeTimeProvider` in tests) and `zone` comes from `ICallerZone`.
3. **The web endpoints** (open point 3).
   - Routes follow `BoardApi`; the vault is checked first everywhere; card refusals are 400 and conflicts 409.
   - `schema.d.ts` was not regenerated. Generating it from the running server (`gen:api` against `127.0.0.1:8083`) also changed unrelated parts (`/api/s/ai-links`, `/api/skills/zip`, `AiLinkView`, `ApiKeyView`, `ConnectionView`, `SkillView` and more). So the committed file is already out of date against `master`'s server.
   - I reverted it and typed the calendar's calls by hand in `web/src/calendar/api.ts`, as the brief allows.
4. **Conflicts with the editor** (open point 4).
   - Options: flush the autosave, or write with the editor's version token.
   - The autosave keeps its own base version, so a write under an open editor would make the next save conflict.
   - Chosen: before a calendar write, the note leaves Write mode with `endWrite()`, which saves first; in Read mode, `beforeRead` flushes. If the save fails, nothing is sent ("Your typing isn't saved yet; try again in a moment."). The server writes by compare-and-swap.
5. **A card's date outside `due`** (a `when`, or `task` without `due`) is listed as a date that can't be read. The plan had said "ignored"; anti-goal 1 wins. The same applies to the dated cards of a `Board.md` that doesn't parse.
6. **A timed entry stands on the viewer's local day of its instant.**
   - A `tz` pins only the instant; a time in another zone shows that zone, "14:00 (Europe/London)".
   - Days and weeks are never converted.
   - An offset written by hand is read, and written back as local time (Kumbu² never writes one).
7. **Months take every ISO week that touches them.** So November 2026 starts with week 44, which holds only 1 November; January 2027 runs from 2026-W53 to 2027-W04. (The plan said W05, which was wrong.)
8. **Several small rules**, each the simplest honest one:
   - Empty keys count as absent.
   - `task` is read ignoring case.
   - Several bad keys on one note make one line.
   - Values shown back are one line of at most 60 characters.
   - A write that would leave an unreadable date is refused.
   - An empty frontmatter block left by a calendar write is removed, so `set` then `unset` gives the note back.
   - `close` on a closed task only sets the reason.
   - Later counts from the later of today and the date. The web offers it for open to-dos and open cards only.
   - `move` across kinds is refused.
9. **`calendar notifications [vault]`** may be narrowed to one vault. On a checkpoint it needs that path, since a checkpoint holds one vault.
10. **The console** reads `calendar` and refuses its writes, and the command metrics name it.
11. **The badge's label** says "3 on, some overdue". The server gives overdue as yes or no.
12. **The Welcome note.** "Your AI in Kumbu²" gets the line "Ask your AI: "What's on this week?"". Its tests pin no text there, so none was loosened.
13. **The time-zone preset** runs once per session in `RequireSession`, so every signed-in page asks for the zone once.
14. **The cursor below a lone heading.** A new day or week note opens in Write mode with the cursor below its heading. This is done in the editor for any note that is only a heading, which changes where the cursor starts for such notes.
15. **Server tests use 2037** (a year with 2026's weekdays). MongoDB's TTL expires sessions by the real clock, so a fake clock in the past made sessions vanish mid-test.

## Deviations, surprises and open points for Chris

**Deviations from the plan:**
- One combined review per task instead of two.
- `TaskStatus` became `TaskMark`, to avoid a clash with `System.Threading.Tasks.TaskStatus`.
- `MoveRequest` became `MoveEntryRequest` (`NoteApi` has one), and `PathRequest` became `CalendarPathRequest`.
- The zone PUT has no 429.
- `schema.d.ts` is not regenerated.

**Surprises:**
- The committed `web/src/api/schema.d.ts` is out of date against `master`'s server. It needs a regeneration of its own.
- A fake clock earlier than the real one makes server tests flaky, through the MongoDB TTL on sessions.
- Three tests pin the shell's command list (`ConsoleApiTests`, `ConsoleFixtureTests`, `ToolMetricsTests`), plus the web console's table in `web/src/console/paths.ts`. A new shell command needs all four.

**Open points:**
- A timed window (14:00–15:00) is "not yet" until it starts, while a time without `until` is ongoing from the start of its day. That is the spec's table against the reasoning in §2.4.
- A time at the viewer's midnight has no ongoing period.
- Overdue never says years: an old task reads "40 months".
- Property keys are case-sensitive: `When:` is not read.
- `until` before `when`, with an offset on only one side, is compared in UTC.
- The open UX points listed above.
- The lesser security points listed above.
- A CRLF frontmatter gets LF on the lines the calendar changes. `Frontmatter.Set` does this for the board too.
- On phones, `type="week"` is not used; the dialog uses a date input whose ISO week is taken.

No spec outside this topic looked wrong. The spec was not changed.

## Hand-test checklist

Each step says where to look and what passes. Run it on staging, or through Playwright; the setup scripts in this run were `scratchpad/ux/setup.js` and `w*.js`.

1. **Setup.** Sign in as Alice with the vaults `Home` and `Work`. In Settings → Calendar, check that "Your calendar shows times in Europe/Vienna" (the browser's zone) is set without anyone asking.
2. **The empty week.** In the tree, open Work's vault menu → **Calendar**. Passes if the page shows "Work" above "Week 46 · 9–15 November 2026", seven days with headings, and the line "Nothing this week yet. Tap a day to add something, or give any note a day from its ⋯ menu. Your AI can do it too: "put this on Friday"."
3. **Today.** Tap **Today**. Passes if `/Work/Calendar/YYYY-MM-DD.md` is created with "# Thursday, 12 November 2026" (today's), the cursor is on the line below, and the header says "This is the day note of 12 November (by its name)".
4. **Add to calendar.** Open `/Work/Pump/Certificate.md`, then ⋯ → **Add to calendar…**. Pick 15 December, tick **To do**, and set **Show early** to 7 days before. Passes if:
   - the frontmatter holds `when: 2026-12-15`, `task: open` and `upcoming: 7d`, unquoted, with every other line unchanged;
   - the header shows "📅 Tue 15 Dec · To do · ✓ · Later".
5. **A note with a time.** In Home, open the note "Zahnarzt", then ⋯ → **Add to calendar…**: 20 November, 14:00. Passes if nothing asks about folders, the note stays where it is, and the header shows "📅 Fri 20 Nov · 14:00".
6. **✓ and the reason.** On Certificate, tap ✓. Passes if:
   - "Closed · Undo · Add a reason" appears;
   - **Undo** reopens it;
   - after ✓ again, **Add a reason** shows a field labelled "Reason" with Save, in the message;
   - saving "renewed until 2027" writes `task: closed`, `closed: <today>` and `reason: renewed until 2027`;
   - the header reads "Closed … · renewed until 2027 · Reopen · Edit reason".
7. **What's on and the badge.** Open **What's on** in the navigation. Passes if:
   - the open tasks show with their vault, an icon and a calm span ("upcoming · in 7 days", "overdue · 1 day" in the warning colour, no red box);
   - the badge's number equals the list's length, and is warning-coloured only while something is overdue;
   - the badge also sits on Work's **Calendar** item, beside each vault in the switcher, and on the phone's menu button.
8. **Later.** On an open task, tap **Later** → **+1 day**. Passes if "Moved to … · Undo" appears and the date in the frontmatter moved from the later of today and its date. On a task with `until`, the menu shows **Postpone** and **More time**.
9. **The month and the grid.** Switch to the month and tap **Show grid**. Passes if:
   - the month is a stack of weeks with empty days left out, an empty week says "nothing yet", and the current week says "this week";
   - the grid has week numbers, darker weekends and the days with entries marked;
   - reloading reopens the month with the grid.
10. **Also ongoing.** Give a note a window of 10 to 20 November. Passes if it stands at the top of each week it touches with its span, and each of those days has a collapsed "Also ongoing (1)".
11. **Dates I couldn't read.** Put `when: tomorrow` in a note. Passes if:
    - the calendar shows "Dates I couldn't read" with "`when: tomorrow` – I need a date like 2026-11-20" and **Fix…**;
    - **Fix…** opens the date dialog;
    - What's on's bottom line "1 date I couldn't read" leads there;
    - the word "problem" never appears.
12. **The tag filter.** Filter by `kumbu` in the week, the month and What's on. Passes if only notes carrying #kumbu, or a tag nested below it, remain.
13. **A phone, 375 px, light and dark.** Passes if the week stacks its days, no page scrolls sideways, the titles wrap, and every control is at least 48 px high.
14. **A read-only member.** Bob is a read-only member of Work. Passes if he sees Work's entries in his own zone, and has no ✓, Later, +, Today, This week, Fix… or ⋯ calendar items. A POST to `/api/vaults/Work/calendar/close` answers 403.
15. **A discreet vault.** Mark Home discreet and hide it. Passes if:
    - What's on, the badge's count and its colour, and the switcher show nothing of Home;
    - `/calendar/Home` gives the same answer as a vault that doesn't exist;
    - the address bar shows `/discreet` while Home's calendar is open;
    - after hiding, the app icon's badge is cleared and recomputed;
    - after showing Home again with the PIN, its entries come back.
16. **A board card.** In a vault with a board, give a card `due`. Passes if:
    - the card shows in the calendar with its column ("📅 Fri 20 Nov · In progress");
    - **Move to Done** shows only when the board has exactly one archivable column;
    - `calendar close` on the card refuses and names the board tool;
    - Later moves its `due`, and its column and rank lines are unchanged.
17. **An agent through MCP `sh`.** Run each command and check what it answers:
    - `calendar notifications`: what's on over every vault, in Alice's zone, with the count of dates that can't be read.
    - `calendar /Work`: the current week.
    - `calendar /Work --week 46 --tag kumbu`: only #kumbu notes.
    - `calendar /Work --month 2026-11`: the weeks.
    - `calendar set /Work/Plan.md --when 2026-11-20 --task --upcoming 7d`: answers with the new version.
    - `calendar close /Work/Plan.md --reason "done"`: answers with the new version.
    - `calendar reopen /Work/Plan.md`: answers with the new version.
    - `calendar move /Work/Plan.md 2026-11-27`: answers with the new version.
    - `calendar unset /Work/Plan.md`: answers with the new version.
    - `calendar unset` on a day note: refuses with "… is the day note of … by its name; rename it …".
    - `calendar /Work --checkpoint <cp>`: reads.
    - `calendar close … --checkpoint <cp>`: refuses.
    - In the web console, `calendar /Work` reads, while `calendar close …` and `calendar -- close …` are refused.

## The pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/197: from `feature/calendar` to `master`, opened as a draft and marked ready after this report. It is not merged, tagged or deployed, and the version is unchanged.
