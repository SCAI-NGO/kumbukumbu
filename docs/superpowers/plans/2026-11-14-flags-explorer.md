# Plan: flags, the explorer's search and order, Recent changes, Take photo, and the calendar's fixes

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to carry out this plan task by task. Each task is test-first: red, green, then each listed mutation on its own (break, watch it fail, restore with `git checkout -- <file>`, or from a copy for a new file).

**Goal:** the last features of 0.5.0 (spec `docs/superpowers/specs/2026-11-14-flags-explorer-photo-design.md`): flags as tags, a search and an order by last change in the explorer, Recent changes, Take photo, and five fixes to the calendar.

**Architecture:** a flag is one of eight tags below `flag/`, read with `TagReader` and written by a small line-level editor of the frontmatter's `tags` (`FlagTags`), through a `FlagService` that follows `CalendarService`'s compare-and-swap write. The tree's data gains each note's and folder's last write time and each note's flag. Recent changes is one server read over the session's store (in-memory working set, no index). The web app gets a `web/src/flags/` module (API, icons, menu), a toolbar at the top of the tree, a `/recent` page and Take photo through the existing upload sheet.

**Tech stack:** C# .NET 10 (xUnit, Microsoft.Testing.Platform), MongoDB through the test container, React 18 + Vite + Vitest, TanStack Query, lucide-react. No new package.

**Shared conventions for every task**

- Environment: `source /tmp/run/env.sh` (Node 26 on the path, `KUMBU_TOKENIZER`, `KUMBU_TEST_MONGO`, `TESTCONTAINERS_RYUK_DISABLED=true`). A second MongoDB on 27018 (`kumbu-test-mongo-2`) for a worker running server tests while a gate runs on 27017.
- Core test, one class: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ClassName"`. Server: the same with `tests/Kumbu.Server.Tests`. Web, one file: `pnpm -C web test --run <file>`. Web gate: `pnpm -C web build`.
- Clocks: `FakeTimeProvider` / an injected `Func<DateTime>`; server tests in 2037 (2026's weekdays).
- Workers never commit, push or switch branches; the controller stages files by name, commits ("imperative, no prefix", ending `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`) and pushes to `feature/flags-explorer` after each task.
- New files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`. British English. No organisation names in tests (Alice, Bob, `Home`, `Work`).
- Don't regenerate `web/src/api/schema.d.ts`; new web types are written by hand in the feature's API module.
- Untouched: `Auth/`, `CallerMiddleware`, `Crypto/`, sharing and roles, publishing, `src/Kumbu.Server/Dav/`, sync, OAuth, the board's semantics, `src/Kumbu.Server/Api/Security.cs`, the shell's command list (`ConsoleApiTests`, `ConsoleFixtureTests`, `ToolMetricsTests`, `web/src/console/paths.ts` stay green untouched).

---

## Task 0: baseline

Run the full gate (`/tmp/run/gate.sh gate0`, the brief's commands in order, a fresh `kumbu-test-mongo` before each server group) on `feature/flags-explorer` at `63d7d72` before any change. Record total, failed and skipped per line and every failure; a known flaky failure is rerun alone three times.

Expected (brief): core about 2,700 total, 4 skipped; server 847 + 413 + 353 + 20 + 780 (6 skipped) + about 1,199 (2 skipped); web about 152 files, 2,604 tests; build green with the chunk-size warning.

---

## Phase 1: the calendar's fixes (spec §6)

### Task 1: states and spans in Core (§6.1, §6.2, §6.5)

**Files:**
- Modify: `src/Kumbu.Core/Calendar/EntryStates.cs` (`EntryStatus` at line 8, `OngoingFromUtc` at line 41, `Behind` at line 105)
- Modify: `src/Kumbu.Core/Calendar/CalendarLayout.cs` (`WhatsOn.Of`, line 103)
- Modify: `src/Kumbu.Core/Sh/Commands/CalendarCommand.cs` (`State`, line 409)
- Modify: `src/Kumbu.Server/Api/CalendarApi.cs` (`EntryView`, line 33; `View`, line 353)
- Test: `tests/Kumbu.Core.Tests/Calendar/EntryStatesTests.cs`, `tests/Kumbu.Core.Tests/Calendar/WhatsOnTests.cs` (or the class that tests `WhatsOn.Of` today: `grep -rln "WhatsOn.Of" tests/Kumbu.Core.Tests`), `tests/Kumbu.Core.Tests/Sh/CalendarCommandTests.cs`

**Rules:**
1. `WhatsOn.Of` leaves out an entry with `ByName && Task == TaskMark.None` (a day or week note by its name that isn't a task). The calendar views (`CalendarLayout.Week`, `Month`) keep it. The badge's count comes from `WhatsOn.Of`, so it follows.
2. `OngoingFromUtc` returns the start of the viewer's local day of `when` for every time, with or without `until`. `EntryStatus` gains `bool BeforeTime = false`: true when the state is ongoing, `when` is a time and now is before its instant; its `Span` is then the clock of `when` as `EntryText.Clock(entry, zone, withEnd: false)` ("14:00", or "14:00 (Europe/London)"). Shown as "today · 14:00" (sh calendar, web). Within a window the span stays "until 15:00".
3. `Behind`: from 12 months on (`days / 30 >= 12`), years: `Count(Math.Max(1, days / 365), "year")`.

**Steps:**
1. Red tests (FakeTimeProvider / fixed `DateTime` instants, zone `Europe/Vienna`, 2026-11-14 as today):
   - `A_day_note_today_without_task_is_not_on` (WhatsOn: `/Work/Calendar/2026-11-14.md`, no frontmatter → absent from `WhatsOn.Of`, present in `CalendarLayout.Week`).
   - `A_day_note_today_with_task_open_is_on` (frontmatter `task: open` → in WhatsOn, ongoing).
   - `A_week_note_without_task_is_not_on`.
   - `A_timed_window_is_ongoing_from_the_start_of_its_day_before_its_time` (`when: 2026-11-14T14:00`, `until: 2026-11-14T15:00`, now 08:00 local → Ongoing, `BeforeTime` true, span "14:00").
   - `A_timed_window_within_its_time_says_until` (now 14:30 → Ongoing, `BeforeTime` false, span "until 15:00").
   - `A_timed_window_after_its_time_is_past` (now 15:30, no task → Past; with `task: open` → Overdue).
   - `A_time_without_until_reads_today_before_its_time` (`when: …T14:00`, now 09:00 → Ongoing, BeforeTime, span "14:00").
   - `A_timed_window_the_day_before_is_not_yet` (now 2026-11-13 20:00 → NotYet).
   - `Overdue_says_months_until_11` (task `when` = today minus 11 months via `AddMonths(-11)` → "11 months"), `Overdue_says_a_year_at_12_months` (`AddMonths(-12)` → "1 year"), `Overdue_says_years` (`AddYears(-3)` → "3 years").
   - CalendarCommandTests `Notifications_say_today_and_the_time_before_it` (`calendar notifications` line holds "today · 14:00").
   Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*EntryStatesTests" --filter-class "*WhatsOnTests" --filter-class "*CalendarCommandTests"`. Expected: compile error (`BeforeTime`) then, once the property exists, the new tests fail.
2. Implement rules 1–3. `CalendarCommand.State`: `EntryState.Ongoing when status.BeforeTime => "today · " + status.Span`. `EntryView` gains `bool BeforeTime = false` as its last parameter, set from the status.
3. Green: the same command, 0 failed; then the whole Calendar namespace: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace "*Calendar*"` and `--filter-class "*CalendarCommandTests"`. Existing tests that pinned "ongoing · until 14:00" before a time or "not yet" before a window's time change to the new rule (each such change is listed in the report, never loosened: the new value is asserted).
4. Mutations (each on its own, red, restore):
   - WhatsOn keeping by-name notes → `A_day_note_today_without_task_is_not_on` fails.
   - WhatsOn dropping by-name tasks too → `A_day_note_today_with_task_open_is_on` fails.
   - `OngoingFromUtc` back to the time for a window → `A_timed_window_is_ongoing_from_the_start_of_its_day_before_its_time` fails.
   - `BeforeTime` always false → the "today" tests fail.
   - Years from 11 months (`>= 11`) → `Overdue_says_months_until_11` fails.

### Task 2: calendar keys ignoring case (§6.4)

**Files:**
- Create: `src/Kumbu.Core/Calendar/CalendarKeys.cs` — `internal static class CalendarKeys` with `Names` (`when`, `until`, `task`, `upcoming`, `tz`, `closed`, `reason`), `Fold(IReadOnlyDictionary<string, object?> values) -> (IReadOnlyDictionary<string, object?> Values, IReadOnlyList<string> Twice)` (calendar keys under their lower-case name; every other key as written; `Twice` lists each calendar key written in two spellings), and `Spelled(IReadOnlyDictionary<string, object?> values, IReadOnlyList<(string Key, string? Yaml)> changes)` (a set uses the spelling in the note, else the canonical one; a removal removes every spelling).
- Modify: `src/Kumbu.Core/Calendar/EntryReader.cs` (`Read`, line 30: fold the values first; two spellings → problem "`when` and `When` are both there – I need just one" / for agents "when is written twice (when, When); keep one"; `ReadCard` likewise for its calendar keys; `due` and `column` stay exact; `CalendarKeyLine` regex line 300 → `(?<key>(?i:when|until|task)|due)`).
- Modify: `src/Kumbu.Core/Calendar/CalendarService.cs` (`WriteAsync`, line 382: `Current.Values` folded; `TaskOf`, `Zones.Find(c.Values["tz"])`, `closed` read from folded values; changes passed through `CalendarKeys.Spelled` before `FrontmatterWrites.TryOwnedKeys`).
- Test: `tests/Kumbu.Core.Tests/Calendar/EntryReaderTests.cs`, `tests/Kumbu.Core.Tests/Calendar/CalendarServiceTests.cs`

**Steps:**
1. Red tests: `When_with_a_capital_is_read` (`When: 2026-11-20` → entry), `TASK_in_capitals_is_read`, `Two_spellings_of_when_are_a_problem` (`when:` and `When:` → problem, no entry, plain text names both), `Due_stays_case_sensitive_on_a_card` (`Due:` on a card is no date: no entry and no problem, as today), `Tags_stay_case_sensitive` (a `Tags:` key is not read by `TagReader`, unchanged), `A_write_keeps_When` (CalendarService `SetAsync` with `--until` on a note with `When:` → the file keeps `When:` byte for byte and gains `until:`), `Close_keeps_Task` (`Task: open` → `Task: closed`, `closed:` added), `Unset_removes_every_spelling`. Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*EntryReaderTests" --filter-class "*CalendarServiceTests"`. Expected: the new tests fail.
2. Implement as above.
3. Green: those two classes, then `--filter-namespace "*Calendar*"`, `--filter-class "*BoardServiceTests"`, `--filter-class "*BoardReaderTests"`, `--filter-class "*TagReaderTests"`: 0 failed.
4. Mutations: `Fold` case-sensitive → `When_with_a_capital_is_read` fails; `Twice` ignored (last wins) → `Two_spellings_of_when_are_a_problem` fails; `Spelled` writing canonical → `A_write_keeps_When` fails; `due` folded too → `Due_stays_case_sensitive_on_a_card` fails.

### Task 3: the web's calendar wording and the OS badge (§6.2, §6.3); the calendar spec's amendments

**Files:**
- Modify: `web/src/calendar/api.ts` (`EntryView` type: `beforeTime?: boolean`)
- Modify: `web/src/calendar/EntryItem.tsx` (line 69: `entry.beforeTime ? \`today · ${entry.span}\` : …`), and `web/src/calendar/EntryLine.tsx` if it prints the state word the same way (`grep -n "state.word\|span" web/src/calendar/*.tsx`)
- Modify: `web/src/calendar/useBadge.ts` (line 34–49: the app badge is the count minus the shares of discreet vaults, `useDiscreet.getState().names`, whether shown or hidden; the in-app count unchanged)
- Modify: `docs/superpowers/specs/2026-11-12-calendar-design.md` (§2.4 table and bullets, §5, §7.6, §7.7, §8, and a note for case-insensitive keys in §2.1), each change marked "(amended 2026-11-14)"
- Test: `web/src/calendar/WhatsOnPage.test.tsx`, `web/src/calendar/Badge.test.tsx`

**Steps:**
1. Red: `WhatsOnPage.test.tsx` "an appointment before its time reads today and the time" (fixture item `state: "ongoing", span: "14:00", beforeTime: true` → text "today · 14:00", no "ongoing"); `Badge.test.tsx` "the app icon leaves discreet vaults out, shown or not" (whats-on count 3 with vaults Work 1, Home 2, Home discreet and revealed → `navigator.setAppBadge` called with 1; Home's share only → `clearAppBadge`) and "the in-app badge still counts what the session shows" (3). Run `pnpm -C web test --run src/calendar/WhatsOnPage.test.tsx src/calendar/Badge.test.tsx`. Expected: the new tests fail.
2. Implement.
3. Green: `pnpm -C web test --run src/calendar`, 0 failed; `pnpm -C web build` green.
4. Mutations: the app badge from `data.count` → the discreet test fails; `beforeTime` ignored → the wording test fails.

**Phase gate 1:** the full gate (`/tmp/run/gate.sh gate1`).

---

## Phase 2: flags (spec §2)

### Task 4: reading and writing a flag in Core

**Files:**
- Create: `src/Kumbu.Core/Tags/Flags.cs` — `public static class Flags` with `Names` (`red`, `green`, `blue`, `purple`, `yellow`, `white`, `done`, `important`, in that order), `IsFlag(string name)`, `Tag(name)` (`flag/red`), and `public static NoteFlag? Of(string content)`: the first tag from `TagReader.Of(content)` standing in the frontmatter (line < `Outline.FrontmatterEnd`) that is exactly `flag/<name>` (ignoring case) of a known name; else the first such tag in the text. `public sealed record NoteFlag(string Name, bool InText, string Written)` (`Written`: the tag as written, `flag/Red`).
- Create: `src/Kumbu.Core/Tags/FlagTags.cs` — `public static class FlagTags` with `public static string? Set(string content, string? flag)`: the content with every known flag tag removed from the frontmatter's `tags` and `flag/<flag>` added; null when `tags` has a form it can't edit (a map, a nested list, a multi-line scalar) — the caller refuses. Line-level, keeping each line's ending (`\r\n` stays):
  - no frontmatter → `---\ntags: [flag/red]\n---\n` + content (in the note's line ending); no flag → unchanged.
  - frontmatter without `tags` → `tags: [flag/red]` before the closing `---`.
  - flow list `tags: [a, "b"] # c` → items split on commas outside quotes; flag items removed with their separator; the new one appended as `, flag/red` (or `[flag/red]` in `[]`); the rest of the line (quoting, spacing, comment) byte for byte.
  - block list `tags:` + `  - a` → flag item lines removed; the new one appended after the last item with the items' indent and marker (`  - flag/red`).
  - string `tags: a, b` / `tags: a b` / `tags: a` → tokens; the separator `, ` when the value holds a comma or is one tag, else ` `.
  - empty `tags:`, `tags: []`, `tags: ""` → set gives `tags: [flag/red]`.
  - a `tags` left empty by the removal is removed (its line and item lines); a frontmatter left empty is removed (`---\n---\n` at the start), so set then remove gives the note back.
  - the key written quoted (`"tags":`) is the same key; `Tags:` is not (tags stay case-sensitive).
- Test: `tests/Kumbu.Core.Tests/Tags/FlagsTests.cs`, `tests/Kumbu.Core.Tests/Tags/FlagTagsTests.cs`

**Steps:**
1. Red (FlagsTests): `A_flag_in_the_frontmatter_is_read`, `The_frontmatter_wins_over_the_text`, `A_flag_in_the_text_is_read_and_says_so` (`#flag/red` → InText true, Written `flag/red`), `The_first_known_flag_wins`, `Unknown_tags_below_flag_are_no_flag` (`flag/maybe`, `flag/red/x`, `flag`), `Case_is_ignored` (`flag/RED` → red), `A_flag_in_code_is_no_flag`.
   Red (FlagTagsTests): for each of the four brief notes (no frontmatter; `tags: [a, b]`; `tags:\n  - a`; other keys around `tags`) `Set_then_remove_gives_the_note_back_byte_for_byte` and `Set_changes_only_the_tags_line_or_item` (every other line equal); plus `Crlf_is_kept`, `Quoted_items_keep_their_quotes`, `A_comment_after_the_list_stays`, `The_string_form_stays_a_string`, `Setting_replaces_the_old_flag`, `No_flag_removes_every_known_flag_and_nothing_else`, `An_empty_tags_left_is_removed`, `A_map_under_tags_is_refused` (null), `Tags_with_a_capital_is_another_key`, and `Every_result_reads_back` (for each case: `TagReader.Read` of the result's frontmatter = the old frontmatter tags without flag tags, plus the new one).
   Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*FlagsTests" --filter-class "*FlagTagsTests"`. Expected: CS0103/CS0246 (no `Flags`, `FlagTags`).
2. Implement.
3. Green: the same, 0 failed; `--filter-namespace "*Tags*"` 0 failed.
4. Mutations: frontmatter not preferred over text → `The_frontmatter_wins_over_the_text` fails; flow removal dropping the following item's leading space → the byte test fails; block item appended without indent → its byte test fails; empty `tags` kept → `Set_then_remove…` (no frontmatter) fails; `flag/maybe` counted → `Unknown_tags_below_flag_are_no_flag` fails.

### Task 5: `FlagService`, and `help tags`

**Files:**
- Create: `src/Kumbu.Core/Tags/FlagService.cs` — `public sealed class FlagService(INoteStore store, NoteWriter writer, Func<DateTime> now, ICallerZone zone)` with `Task<ShellResult> SetAsync(string path, string? flag, FlagTask task, CancellationToken ct)`; `public enum FlagTask { None, Close, Reopen }`. As `CalendarService.WriteAsync` (line 382): `Caller.Refuse("flag", path)` first (Kind Refused); read; damaged → Conflict; frontmatter that doesn't parse → Conflict; the note's flag in the text (no frontmatter flag) → Invalid "This flag is written in the note's text (#flag/red); change it there."; unknown flag → Invalid; `FlagTags.Set` null → Invalid "I can't change this note's tags; edit them in the note"; `Close` only on an open task that is no card (`EntryReader.IsCard` false and folded `task` is `open`, else Invalid) and adds `task: closed`, `closed: <today in the caller's zone>` through `CalendarKeys.Spelled` and `FrontmatterWrites.TryOwnedKeys`; `Reopen` only on a closed task, `task: open` and `closed` removed (a reason stays); read-back check (the note's tags after = before minus flags plus the new; `Flags.Of` gives the new flag); unchanged → Ok with the version; `NoteWriter.Next` + `TryReplaceAsync`, three attempts, then Conflict "kept changing".
- Modify: `src/Kumbu.Server/Program.cs` (register `FlagService` scoped beside `CalendarService`: `grep -n "CalendarService" src/Kumbu.Server/Program.cs`).
- Modify: `src/Kumbu.Core/Sh/Commands/TagsCommand.cs` (Help, line 19): a paragraph "Flags: a note's flag is one of eight tags, flag/red, flag/green, flag/blue, flag/purple, flag/yellow, flag/white, flag/done and flag/important, one per note; 'tags flag' finds every flagged note, rg --tag flag/red the red ones. Set or change one by editing the frontmatter's tags."
- Test: `tests/Kumbu.Core.Tests/Tags/FlagServiceTests.cs`, `tests/Kumbu.Core.Tests/Sh/HelpTests.cs` (only if it pins `help tags`' text).

**Steps:**
1. Red: `Sets_a_flag_and_keeps_every_other_byte`, `Removes_it_again`, `A_read_only_member_can_not_flag` (a caller whose `Refuse` refuses → Refused, nothing written), `A_flag_in_the_text_is_left_alone`, `Mark_done_closes_the_task_in_the_same_write` (one new version: `flag/done` and `task: closed`, `closed: 2026-11-14`), `Undo_takes_both_back` (`SetAsync(previous flag, Reopen)` → the note as before apart from nothing), `Close_on_a_card_is_refused`, `Close_on_a_closed_task_is_refused`, `A_write_in_between_is_retried` (a store that changes the note once between read and replace), `An_unknown_flag_is_refused`, `A_broken_frontmatter_is_refused`, `Help_tags_names_the_flags`. Use the same in-memory store and caller doubles as `CalendarServiceTests`. Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*FlagServiceTests"`. Expected: CS0246.
2. Implement.
3. Green: that class, `--filter-class "*HelpTests"`, `--filter-class "*TagsCommandTests"`, 0 failed.
4. Mutations: the refusal removed → `A_read_only_member_can_not_flag` fails; Close as a second write → `Mark_done_closes_the_task_in_the_same_write` fails (two versions); `Attempts = 1` → the retry test fails; the text check removed → `A_flag_in_the_text_is_left_alone` fails.

### Task 6: the flag's endpoints, and the tree's new fields

**Files:**
- Create: `src/Kumbu.Server/Api/FlagApi.cs` — `GET /api/vaults/{vault}/flag?path=` (Background) → `FlagView(string? Flag, string? InText, bool OpenTask, bool CanWrite)` (`InText`: the tag as written, `#flag/red`, when the flag is the text's; `OpenTask`: the note is an open task and no card, from `EntryReader` with the board's definition as `CalendarApi.ReadEntryAsync`); `POST /api/vaults/{vault}/flag` with `SetFlagRequest(string Path, string? Flag, string? Calendar = null)` (`calendar`: `close` or `reopen`) → `FlagWrittenView(string Path, string Version, FlagView Note)`. The vault checked first (`NoteApi.RequireVaultAsync`, as `CalendarApi.NoteInAsync`: a hidden discreet vault is the 404 of an unknown one), the path in that vault and a note (400), the role in the service (403), `BoardApi.Check(result, "flag")` (409 on a conflict). Errors: GET `400, 401, 404, 503`; POST `400, 401, 403, 404, 409, 503, 507`.
- Modify: `src/Kumbu.Server/Program.cs` (`app.MapFlagApi()` beside `MapCalendarApi`).
- Modify: `src/Kumbu.Server/Api/NoteApi.cs`: `TreeNote` (line 126) gains `[JsonIgnore(WhenWritingNull)] string? Flag = null`; notes get `WrittenAt` (their last write, as attachments have it) and `Flag` (`Flags.Of(content)?.Name`); `TreeFolder` (line 119) gains `[JsonIgnore(WhenWritingNull)] DateTime? WrittenAt = null`: the newest write anywhere below it, folder markers included. `TreeAsync` (line 468) reads `StoredNote`s (`ListNotesUnderAsync` then `GetAsync`, in memory in `EncryptedNoteStore`); `TreeOf` keeps its signature for checkpoints (no times there) and gains an overload taking the times (a `Func<VaultPath, DateTime?>`). The checkpoint tree is unchanged. Doc comments updated.
- Test: `tests/Kumbu.Server.Tests/Flags/FlagApiTests.cs` (new namespace `Kumbu.Server.Tests.Flags`, run by the gate's last line), `tests/Kumbu.Server.Tests/Api/TreeApiTests.cs` (or the class that tests `/tree` today: `grep -rln "/tree\"" tests/Kumbu.Server.Tests`)

**Steps:**
1. Red (FlagApiTests, `[Collection("app")]`, FakeTimeProvider 2037-11-14 10:00Z): `Sets_and_reads_a_flag`, `Removing_gives_the_note_back` (byte for byte, through `/api/notes`), `A_read_only_member_sees_the_flag_and_can_not_set_it` (Bob Read on Alice's Work: GET 200 with `canWrite` false, POST 403, note unchanged), `A_hidden_discreet_vault_answers_as_an_unknown_one` (`HiddenWorld`: GET and POST on `Tagebuch` answer as `Nowhere`, `AsNowhereAsync`), `A_conflict_is_409` (a write with a note that keeps changing: a store double, or the text flag case for 400), `Mark_done_closes_the_task` (`calendar: "close"` → `task: closed`, `closed: 2037-11-14`, `/calendar/entry` says closed), `A_path_outside_the_vault_is_400`, `The_flag_in_the_text_is_named`. Tree: `The_tree_carries_each_notes_time_and_flag`, `A_folder_is_as_new_as_its_newest_file_at_any_depth`, `An_empty_folder_counts_by_its_marker`, `The_checkpoint_tree_is_unchanged`. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*FlagApiTests"` and the tree class. Expected: 404s / missing JSON properties.
2. Implement.
3. Green: those classes; `--filter-class "*CalendarWriteApiTests" --filter-class "*CheckpointApiTests" --filter-class "*PasswordWindowTests" --filter-class "*ConsoleApiTests" --filter-class "*ToolMetricsTests"`: 0 failed.
4. Mutations: the vault check after the path parse (a hidden vault's path answering 400) → the discreet test fails; the folder time from direct children only → the depth test fails; `Refuse` dropped from the service → the read-only test fails.

### Task 7: flags in the web app — the header, the menu, the tick's question, the tree's icon

**Files:**
- Create: `web/src/flags/api.ts` — hand-typed `FlagView`, `FlagWrittenView`, `FlagName` (the eight), `useFlag(path)` (`["flag", path]`, GET), `useSetFlag()` (POST after `editorSettled(path)` from `web/src/calendar/api.ts`; on success invalidates `["flag", path]`, `["tree", vault]`, `["recent"]`, and with `calendar` set the calendar's keys through `afterEntryWrite`).
- Create: `web/src/flags/FlagIcon.tsx` — `FlagIcon({ flag, className })`: lucide `Flag` filled in the flag's colour for red, green, blue, purple, yellow; `Flag` outlined for white; `Check` (green) for done; `ExclamationMark` for important; always with an `aria-label`/`title` of its name ("Red flag", "Done", "Important"). Colours from new CSS tokens `--flag-red`… in `web/src/index.css` and `web/src/theme/schemes.ts` for every scheme, held to 3:1 against `bg` and `surface` by `web/src/theme/contrast.test.ts` (non-text contrast, WCAG 1.4.11).
- Create: `web/src/flags/FlagMenu.tsx` — a sheet/menu with the eight choices and **No flag**, each icon + name ("Red", "Green", "Blue", "Purple", "Yellow", "White", "Done", "Important"); with a text flag only the sentence "This flag is written in the note's text (#flag/red); change it there."; `TickQuestion` dialog: "Also mark it done in the calendar?" with **Mark done** and **Just the flag**; after a write a toast "Flagged Red · Undo" / "Flag removed · Undo" in the style of `ClosedToast`; Undo restores the previous flag (and with Mark done also `calendar: "reopen"`) in one write.
- Modify: `web/src/note/NoteView.tsx` (line 407, beside `EntryLine`): `<FlagButton path>` showing the flag's icon (a 48 px target) opening `FlagMenu` for a writer; read-only: the icon only. Nothing when there is no flag.
- Modify: `web/src/shell/MobileShell.tsx` (`noteActions`, line 373–389): **Flag** (icon `Flag`) when the note may be written, opening `FlagMenu`; and `web/src/shell/TreeDrawer.tsx` `FileMenu` (line 238–249) the same for notes.
- Modify: `web/src/shell/TreeDrawer.tsx` `NoteButton` (line 271): `{entry.flag && <FlagIcon flag={entry.flag} />}` after the name; `web/src/api/notes.ts` (`TreeFolder` type: `writtenAt?: string | null` on folders, `flag?: string | null` on notes, by hand beside the generated type).
- Test: `web/src/flags/FlagMenu.test.tsx`, `web/src/flags/FlagIcon.test.tsx`, `web/src/note/NoteView.test.tsx`, `web/src/shell/TreeDrawerFlags.test.tsx`

**Steps:**
1. Red: "the header shows the note's flag and nothing without one"; "⋯ → Flag offers the eight and No flag, by icon and name"; "choosing Red posts flag red after the editor settled"; "a read-only member sees the flag and no menu"; "a text flag says where to change it and offers nothing"; "✓ on an open task asks Also mark it done in the calendar?" → Mark done posts `calendar: close`, Just the flag doesn't; "no question for a card or a note that isn't a task"; "Undo puts the old flag back (and reopens after Mark done)"; "the tree shows the flag after the name"; "the white flag is outlined, every flag has its name". Run `pnpm -C web test --run src/flags src/note/NoteView.test.tsx src/shell/TreeDrawerFlags.test.tsx`. Expected: missing modules, then failures.
2. Implement.
3. Green: those, then `pnpm -C web test --run src/shell src/note src/calendar src/theme`; `pnpm -C web build`.
4. Mutations: the question shown for a card → its test fails; Undo without reopen → fails; the menu offered read-only → fails; `editorSettled` skipped → "after the editor settled" fails.

**Phase gate 2:** the full gate.

---

## Phase 3: the explorer (spec §3)

### Task 8: search by words and flag

**Files:**
- Create: `web/src/shell/treeFilter.ts` — `filterTree(root, words, flag)`: words split on spaces, case-insensitive, AND over the path within the vault without `.md` (`Pump/Certificate`); `flag` `any` or one of the eight; keeps matching notes and attachments and the folders on the way; returns the folders to open. Pure.
- Create: `web/src/shell/TreeTools.tsx` — the toolbar at the top of the tree (under the vault's heading, `TreeDrawer.tsx` line 626–631): a search button (Search icon, "Find in this vault") opening a field ("Find by name"), **Any flag** and the eight flag chips (one at a time, `aria-pressed`), Escape or ✕ clears and closes; **Search the contents…** below the field opens the search sheet with the words (`useEditor.getState().setSearchQuery(words); setOverlay("search")`). Also hosts the order switch (Task 9) and the Recent changes item (Task 11).
- Modify: `web/src/shell/TreeDrawer.tsx` (`rows`, line 60–68, and the list at 655–689): with a filter, the rows of `filterTree`'s result, every folder on the way open; nothing matching: "Nothing called that here." followed by **Search the contents…**.
- Test: `web/src/shell/treeFilter.test.ts`, `web/src/shell/TreeTools.test.tsx`

**Steps:**
1. Red: treeFilter "every word must match" (`pump cert` finds `Pump/Certificate.md`, `pump x` nothing), "case is ignored", "folders on the way are kept and opened", "attachments match by name", "the flag chip keeps flagged notes; any flag every flagged one", "words and flag together"; TreeTools "Escape gives the full tree back", "Nothing called that here.", "Search the contents… opens the search with the words". Run `pnpm -C web test --run src/shell/treeFilter.test.ts src/shell/TreeTools.test.tsx`.
2. Implement. 3. Green: `pnpm -C web test --run src/shell`, `pnpm -C web build`.
4. Mutations: OR instead of AND → "every word must match" fails; folders not opened → fails; flag ignored → fails.

### Task 9: order by name or by last change

**Files:**
- Create: `web/src/shell/treeOrder.ts` — `orderTree(root, "name" | "changed")`: "changed" orders folders by `writtenAt` (newest first, without one last) and files by `writtenAt`, folders above files; ties by name. `useTreeOrder()` remembered in `localStorage` (`kumbu.treeOrder`, wrapped in try/catch), for every vault, as the calendar's grid (`web/src/calendar/preferences.ts`).
- Modify: `web/src/shell/TreeTools.tsx` (a two-way switch **Name** / **Last changed**, 48 px targets), `web/src/shell/TreeDrawer.tsx` (rows from `orderTree`, after the filter).
- Test: `web/src/shell/treeOrder.test.ts`, `web/src/shell/TreeTools.test.tsx`

**Steps:**
1. Red: "a folder is as new as its newest file at any depth" (uses the server's folder `writtenAt`), "folders stay above files", "an empty folder without a time goes last", "the choice is remembered on the device", "Name is today's order". 2. Implement. 3. Green: `pnpm -C web test --run src/shell`, build. 4. Mutations: files above folders → fails; not remembered → fails.

**Phase gate 3:** the full gate.

---

## Phase 4: Take photo, Recent changes

### Task 10: Take photo

**Files:**
- Create: `web/src/upload/photoName.ts` — `photoName(now: Date, ext: string, taken: Set<string>)`: `Photo YYYY-MM-DD HHMMSS.<ext>` in local time (the archive's stamp format), ` 2`, ` 3` while taken (ignoring case); `extOf(type)`: `image/jpeg` → `jpg`, `image/png` → `png`, `image/webp` → `webp`, `image/heic` → `heic`, else from the file's name.
- Modify: `web/src/shell/TreeDrawer.tsx` (`FolderMenu`, line 121–134): **Take photo** (icon `Camera`) after **Upload here**, only when `writes` and `matchMedia("(pointer: coarse)")` matches; a hidden `<input type="file" accept="image/*" capture="environment">` owned by `TreeDrawer` is clicked from the menu; its file goes to `useEditor.getState().openUpload(folder, [{ path: name, file }])` with the upload sheet starting at once (the existing upload path, its checks, its shrinking — `shrunkName` changes the extension when the bytes change — and its confirmation).
- Modify: `web/src/upload/UploadSheet.tsx` only if `dropped` doesn't start by itself: a `start` flag on the dropped list, as `uploadPasted` does (line 368).
- Test: `web/src/upload/photoName.test.ts`, `web/src/shell/TreeDrawerPhoto.test.tsx`; update `web/src/shell/TreeDrawerFolders.test.tsx` (line 316–317) and `TreeDrawerDiscreet.test.tsx` (line 64) with a coarse-pointer case that lists **Take photo** after **Upload here** (the fine-pointer lists stay as they are).

**Steps:**
1. Red: photoName "names by local time", "a name taken gets 2, then 3"; TreeDrawerPhoto "Take photo on a touch device for a writer", "not on a desktop", "not for a read-only member", "the photo lands in that folder through the upload sheet as Photo … .jpg". 2. Implement. 3. Green: `pnpm -C web test --run src/shell src/upload`, build. 4. Mutations: offered without coarse pointer → fails; offered read-only → fails; no ` 2` → fails.

### Task 11: Recent changes

**Server files:**
- Create: `src/Kumbu.Server/Api/RecentApi.cs` — `GET /api/recent` (session-only, as Activity; Errors `400, 401, 403, 503`), query `words`, `flag` (`any` or a name), `skip` (repeated: vaults switched off), `limit` (1–500, default 50). Reads every current note (`ListNotesUnderAsync(Root)` + `GetAsync`) and attachment (`StoreAttachments.UnderAsync(store, Root)`) of the vaults the session's store lists (hidden discreet vaults are absent there), leaving out `_archive/` and folder markers; filters; orders by last write, newest first, then path; renders each writer with `WriterLabels.RenderAsync` (Activity's labels). `RecentView(IReadOnlyList<RecentItemView> Items, bool More, IReadOnlyList<string> Vaults)`, `RecentItemView(string Path, string Vault, string Title, string Folder, string Kind, string? Flag, string Writer, string? WriterKind, DateTime WrittenAt)`. `Vaults`: every vault listed (for the chips), whatever is skipped.
- Modify: `src/Kumbu.Server/Program.cs` (`app.MapRecentApi()`).
- Test: `tests/Kumbu.Server.Tests/Flags/RecentApiTests.cs`: `Lists_the_newest_first_across_vaults`, `Says_who_and_when`, `Filters_by_words_flag_and_vault`, `Leaves_out_the_archive_and_the_trash`, `A_moved_file_shows_at_its_new_path`, `A_hidden_discreet_vault_is_absent_chips_included` (`HiddenWorld`: no item, no vault name of Tagebuch; after `RevealAsync` it is there), `A_read_only_member_sees_the_shared_vault`, `Show_more_takes_the_next_ones` (`limit`, `more`), `A_key_is_refused` (session-only), and `[Fact(Explicit = true)] Recent_changes_for_5000_notes_over_the_server` (seeded as `CalendarApiTests.Whats_on_for_5000_notes_over_the_server`, line 353; cold and warm times written to the test output).
- Run: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*RecentApiTests"`; the measurement: `tests/Kumbu.Server.Tests/bin/Debug/net10.0/Kumbu.Server.Tests -method "*Recent_changes_for_5000_notes_over_the_server" -explicit only -showLiveOutput` (and the same in Release).

**Web files:**
- Create: `web/src/recent/api.ts` (hand-typed, `useRecent({ words, flag, skip, limit })`, key `["recent", …]`), `web/src/recent/RecentPage.tsx` (route `/recent`: heading "Recent changes", the words field, vault chips all on, the flag chips; rows "icon · title · flag · Work/Pump · Bob · 10 min ago" with `writerText` and `ago` from Activity; tapping opens the note, or an image or PDF in the lightbox; **Show more** adds 50; empty: "Nothing changed here yet."; back to the notes).
- Modify: `web/src/router.tsx` (line 144–145: `{ path: "/recent", lazy: lazyPage(() => import("./recent/RecentPage")) }`); `web/src/shell/TreeTools.tsx` (a **Recent changes** item, icon `History`, linking to `/recent`); `web/src/discreet/neutralWindow.ts` untouched (the route names no vault) — a test in `neutralWindow.test.tsx` pins that `/recent` is no vault route.
- Test: `web/src/recent/RecentPage.test.tsx`: "rows newest first with who and when", "chips for every vault, all on, a tap switches one off", "words and flag narrow it", "Show more", "a row opens the note", "nothing of a hidden vault" (the server's answer is what it shows), "the route names no vault".

**Steps:** red (server, then web), implement, green (`--filter-class "*RecentApiTests"`, `pnpm -C web test --run src/recent src/discreet`, build), measure, mutations: the archive kept → fails; `skip` ignored → fails; session-only dropped → the key test fails; chips off by default → fails.

---

## Phase 5: documentation, checks and the end

### Task 12: documentation

- `docs/security-architecture.md` §4 (after the calendar paragraph, line 298): flags (a tag in the note; the flag write's endpoint, role checked in `FlagService`, compare-and-swap, only the `tags` lines change), the tree's new fields, Recent changes (session-only, on demand, no index, hidden discreet vaults absent), Take photo (the upload path).
- `README.md`: a short paragraph on flags, the explorer's search and order, Recent changes and Take photo where the README lists the web app's features.
- No `CHANGELOG.md` entry.
- Run: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-namespace Kumbu.Server.Tests.Welcome` (README-pinning tests, if any), 0 failed.

### Task 13: the UX check, the security review, the final gate

1. UX check by a fresh subagent (Playwright + Chromium, the server against a test MongoDB on 27018 with the web build served through `ASPNETCORE_WEBROOT`, never a real host): the spec's principles and §2–§5; the walk of the brief at 1280 and 375 px, light and dark, as Alice (Home discreet, Work) and as Bob (read-only member of Work). Fix what it confirms; record the rest.
2. Security review by a fresh subagent: the spec's anti-goals and security boundary and `git diff 63d7d72...HEAD`, nothing of my reasoning; the brief's six questions. Fix what it confirms; record the rest.
3. The final gate (`/tmp/run/gate.sh gatefinal`), every line 0 failed (known flakies rerun alone three times).
4. Push; open the pull request as a **draft** "Flag notes, search and sort the explorer, list recent changes, take photos"; write the report `docs/superpowers/overnighter/2026-11-14-flags-explorer-report.md` and the `docs/superpowers/open-items.md` rows; commit, push, mark the pull request ready; `PushNotification`.
5. **Do not merge**, tag, deploy or change the version.
