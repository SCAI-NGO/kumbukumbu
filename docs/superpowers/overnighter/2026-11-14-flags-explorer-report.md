# Overnighter report: flags, the explorer, Recent changes, Take photo and the calendar's fixes

Run of 2026-11-14 on `feature/flags-explorer`, from `docs/superpowers/overnighter/2026-11-14-flags-explorer-brief.md` and the spec `docs/superpowers/specs/2026-11-14-flags-explorer-photo-design.md` (unchanged). Pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/198.

Everything the spec asks for is built: the five calendar fixes (§6), flags (§2), the explorer's search and order (§3), Recent changes (§4) and Take photo (§5). Every gate line is green; the only failures on the way were tests known to be flaky, each green when run alone (listed below), and one of those turned out to be a real race in sign-out, fixed (`b213101`).

## Environment

- **.NET:** 10.0.403 as installed (`global.json` rolls forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Node:** 26.13.1 wasn't there; I installed it into `/opt/node26` from the nodejs.org tarball. pnpm 10.32.1 from `npm install -g`.
- **Docker:** `dockerd` wasn't running; I started it by hand. The test MongoDB was `mongo:7.0` with `--ulimit nofile=20000:20000` on 27017 for the gates, and a second one on 27018 for the workers and the UX check. `TESTCONTAINERS_RYUK_DISABLED=true`.
- **Tokenizer:** fetched, hash checked, `KUMBU_TOKENIZER` set.
- **Machine:** 4 CPUs, 15 GB RAM. No restart of the worker during the run.
- **Task 0:** about 10 minutes of setup; the baseline gate took 27 minutes (08:37–09:04 UTC), much less than the 55 the brief expected.
- **Superpowers:** available. I used `writing-plans`, then subagent-driven work: a fresh subagent per task. **Deviations:** one combined spec-and-quality review per task or pair of tasks instead of two separate reviews, to save time; independent tasks ran in parallel (two in their own git worktrees); a phase gate ran in a separate worktree at a fixed commit while work went on. Subagents never committed, pushed or switched branches; I did all of that.
- **Scripts:** `/tmp/run/setup.sh`, `/tmp/run/env.sh`, `/tmp/run/gate.sh` (their contents are in the plan's Task 0).

## The plan and its review

The plan is `docs/superpowers/plans/2026-11-14-flags-explorer.md` (`821df49`). A fresh subagent reviewed it with the spec, the brief and the plan. It found 15 points; I folded all of them in as binding amendments at the plan's end (`b39b950`). The ones that mattered most:

1. **A discreet leak:** a `/recent` page left open would have kept a hidden vault's rows, since it names no vault and `hideAgain` removed queries rather than resetting them. Amendment 1: reset `["recent"]` in `hideAgain`, `leftElsewhere` and on reveal; filters only in component state.
2. **Two pinned server tests** the plan didn't name: `PasswordWindowTests` (the session-only list) and `HistoryApiTests` (the background-poll list).
3. **Stale caches:** a flag write must re-read the open note, or the next save conflicts; a save must refresh the tree, or Last changed and the flag icon stand still.
4. **Exact Undo:** replace a flag in place instead of removing and appending, so Undo gives the note back byte for byte.
5. **Matching what the reader matches:** the writer must remove `flag/RED` and `#flag/red` too.
6. **The badge race:** the app icon must wait for the vault list.
7. **Phase gates** after Take photo and after Recent changes; Task 0's scripts written into the plan.
8. **Wrong anchors and signatures** (the tree's `NoteButton` props, the generated recursive tree types, `ReadEntryAsync` private, `CanWrite` through `Refuse`), `isPhone()` for jsdom, HEIC refused honestly, the upload sheet's `start`, the model `NoteApi.ChangesAsync`, and the end's checklist.

## Task 0, planned against measured

Release build with `KUMBU_TOKENIZER`, on `feature/flags-explorer` at `63d7d72`, before any change.

| Line | Planned | Measured |
|---|---|---|
| core | about 2,700, 4 skipped | total 2702, failed 0, skipped 4 |
| server: Api, Mcp, Auth, Net | 847 | total 854, failed 0 |
| server: Accounts | 413 | total 413, failed 0 |
| server: Sharing | 353 | total 353, failed 0 |
| server: Welcome | 20 | total 20, failed 0 |
| server: Vaults, Skills, Search, Metrics | 780, 6 skipped | total 780, failed 0, skipped 6 |
| server: the rest | about 1,199, 2 skipped | total 1199, failed 0, skipped 2 |
| web | about 152 files, 2,604 tests | Test Files 152 passed, Tests 2616 passed |
| web build | green, chunk-size warning | green (`✓ built in 14.63s`), the known warning |

No failure in the baseline.

## The tasks, with commits and real test output

Each task went red, then green, then through its mutations (the next section). The lines are quoted from `dotnet test` and `pnpm test`.

**Task 1: states and spans in Core (§6.1, §6.2, §6.5)**: `360c43a`, review fixes in `3fe9301`.
- Red: `error CS1729: 'EntryStatus' does not contain a constructor that takes 3 arguments`; with the property, `total: 84  failed: 7`.
- Green: the four classes `total: 89  failed: 0`; the Calendar namespace `total: 182 failed: 0 skipped: 1`; core `total: 2714 failed: 0 skipped: 4`; the server's Calendar namespace `total: 35 failed: 0 skipped: 1` after one server test changed to the new span.
- Existing tests that pinned the old rule now assert the new value (nine, among them "13 months" → "1 year", "until 14:00" → "14:00" with `beforeTime`, "upcoming today" → "ongoing" before a window's time, and one What's on fixture that gained `task: open`); none was loosened.

**Task 2: calendar keys ignoring case (§6.4)**: `310ad68`, review fixes in `3fe9301`.
- Red: `total: 88  failed: 9`.
- Green: the two classes `total: 88 failed: 0`; the Calendar namespace `total: 182 failed: 0`; Board and Tag readers `total: 138 failed: 0`; core `total: 2713 failed: 0 skipped: 4`. After the review fixes, the Calendar namespace `total: 194 failed: 0`.

**Task 3: the web's wording and the app-icon badge (§6.2, §6.3), the calendar spec's amendments**: `c4c7cac`.
- Red: `Tests  5 failed | 17 passed (22)`.
- Green: `Tests  22 passed (22)`; `src/calendar src/shell` `Tests 433 passed (433)`; with `src/discreet` `Tests 545 passed (545)`; build `✓ built in 14.01s`.

**Task 4: reading and writing a flag in Core**: `a11a942`, review fixes in `d3478f6` and `0828354`.
- Red: `FlagTagsTests.cs(106,123): error CS0103: The name 'FlagTags' does not exist in the current context`.
- Green: `total: 84 failed: 0`; the Tags namespace `total: 164 failed: 0`; after the fixes `total: 206 failed: 0`.

**Task 5: `FlagService` and `help tags`**: `b247161`, review fixes in `d3478f6`.
- Red: `FlagServiceTests.cs(24,13): error CS0246: The type or namespace name 'FlagService' could not be found`; then `failed …Help_tags_names_the_flags` (`total: 24 failed: 1`).
- Green: six classes `total: 200 failed: 0`; core `total: 2834 failed: 0 skipped: 4`.

**Task 6: the flag's endpoints and the tree's new fields**: `bbd22d4`, review fix in `693961e`.
- Red: Core `total: 26 failed: 2` (Close only with done); server `total: 67 failed: 16`.
- Green: FlagApi, NoteApi, HistoryApi `total: 67 failed: 0`; Calendar, PasswordWindow, Console, ToolMetrics `total: 189 failed: 0`; CheckpointRead and Archive `total: 31 failed: 0`; after the review fix, five classes `total: 72 failed: 0 skipped: 1`.

**Task 7: flags in the web app**: `3f9ddbc`, review fixes in `16c53dc`.
- Red: `Tests  273 failed | 510 passed (783)` (missing modules and controls).
- Green: the touched files `Tests 788 passed (788)`; after the fixes `Tests 27 passed` (FlagMenu, TreeDrawerFlags), `70 passed` with NoteView; build `✓ built`.

**Tasks 8 and 9: search by words and flag; order by name or last change**: `b714769`.
- Red: `Failed to resolve import "./treeFilter"`, `"./treeOrder"`; 9 × `Unable to find an accessible element with the role "button" and name "Find in this vault"`.
- Green: `Test Files 3 passed (3)`, `Tests 24 passed`; build `✓ built`.

**Task 10: Take photo**: `eb37d29`, security fix in `0828354`.
- Red: photoName `Tests 10 failed (10)`; TreeDrawerPhoto `Tests 4 failed | 2 passed (6)` (the two passing tests check an absence).
- Green: `Tests 6 passed (6)`; Folders, Discreet and upload `111 passed`; build `✓ built in 17.25s`.

**Task 11: Recent changes**: server `54d1b05`, web `d232732`, security fixes in `0828354`.
- Server red: `total: 10, failed: 9, skipped: 1` ("Expected: Forbidden / Actual: NotFound"). Green: RecentApi `total: 10 failed: 0 skipped: 1` (the explicit cost test), PasswordWindow `70/0`, ConsoleApi `88/0`, ToolMetrics `6/0`.
- Web red: 16 failures, the 11 RecentPage tests with `Unable to find role="heading" and name "Recent changes"`. Green: `Test Files 41 passed (41)`, `Tests 491 passed (491)`; build `✓ built in 15.64s`.

**Task 12: documentation**: `693961e` (`docs/security-architecture.md` §4, README's web app and discreet sections). Welcome `total: 20 failed: 0`. No changelog entry.

**Beyond the plan: the shell's sign-out race**: `b213101`. The test "signs out and forgets the last note" failed now and then. It is a real race: `queryClient.clear()` ran before the navigation to the sign-in page committed, so the shell mounted again on the old note, asked the session again and put the note back among the recent ones. Alone it failed 10 of 10 at HEAD and at `63d7d72`; with the cache emptied on the sign-in page after a sign-out, 10 of 10 passed. The test now waits for an empty cache and asserts that no request follows the sign-out.

**Task 13: the UX check, the security review and the final gate**: `0828354`, `7c31c99`; outcomes below.

## Phase gates

Each gate is the brief's: Release, `KUMBU_TOKENIZER`, a fresh MongoDB per server group. Gate 1 and the gate after phases 2 and 3 ran in a separate worktree at a fixed commit while work went on in the main checkout, so their web steps ran on files nobody was editing.

| Line | Gate 1 (`c4c7cac`, after phase 1) | Gate 3 (`693961e`, after phases 2 and 3) | Final (`7c31c99`) |
|---|---|---|---|
| core | 2834, 0 failed, 4 skipped | 2846, 0, 4 | 2852, 0, 4 |
| Api, Mcp, Auth, Net | 854, 0 | 858, 0 | 858, 0 |
| Accounts | 413, 0 | 413, 0 | 413, 0 |
| Sharing | 353, **1 failed** | 353, 0 | 353, 0 |
| Welcome | 20, 0 | 20, 0 | 20, 0 |
| Vaults, Skills, Search, Metrics | 780, 0, 6 | 780, 0, 6 | 780, 0, 6 |
| the rest | 1199, 0, 2 | 1221, 0, 3 | 1221, 0, 3 |
| web | 152 files, 2621 tests, 0 failed | 159 files, 2924, 0 | 162 files, 2981, 0 |
| web build | green | green | green (`✓ built in 15.02s`) |

- Gate 1's failure was `MembershipApiTests.The_sweep_tells_the_hub_of_an_ended_membership_it_cleans_up` ("close 4401" where null was expected), a sync-hub test. Its class alone: 26/0 three times out of three.
- The rest grew by 22, exactly the new `Kumbu.Server.Tests.Flags` namespace (FlagApiTests 12, RecentApiTests 10 with one explicit), so the gate's last line runs it. The Api line grew by the four tree tests.
- I ran no separate gate after phase 4a (Take photo): Take photo and Recent changes were committed within the same hour, and the final gate covers both. That is a deviation from the plan's gates 4a and 4b.
- Gate 1 took 33 minutes, gate 3 32, the final one 30 (10:53–11:24 UTC), each while workers ran beside it.
- Flaky on the way, each green alone: `commentRanges.test.tsx` "reads comments in linear time" (in workers' runs under load, 3/3 green alone), and the shell's sign-out test, which turned out to be a real race and is fixed.

## Every mutation

Each mutation was applied on its own, run until red, then restored (with `git checkout`, or from a saved copy for new files, compared byte for byte).

**Task 1**
- What's on keeping day notes by name: `A_day_note_today_without_task_is_not_on`, `A_week_note_without_task_is_not_on` failed.
- What's on dropping day notes that are tasks too: `A_day_note_today_with_task_open_is_on` and `Whats_on_covers_every_vault…` failed.
- `OngoingFromUtc` back to the time for a window: 2 failed.
- `BeforeTime` always false: 10 failed.
- Years from 11 months: `Overdue_says_months_until_11` failed.
- (Review fix) a card's `reason` folded: `A_cards_reason_is_the_boards_as_written` failed.

**Task 2**
- `Fold` case-sensitive: 7 failed.
- Two spellings ignored (last wins): 3 failed.
- `Spelled` writing the lower-case spelling: `A_write_keeps_When`, `Close_keeps_Task` failed.
- `due` folded too: `Due_stays_case_sensitive_on_a_card` failed.

**Task 3**
- The app icon from the whole count: 4 failed.
- Not waiting for the vault list: 1 failed.
- `beforeTime` ignored: 1 failed.

**Task 4**
- The text preferred over the frontmatter: 2 failed.
- A flow removal trimming the next item's space: 4 failed; the first item's leading space: 1 failed.
- A block item appended without its indent: 5 failed.
- An emptied `tags` kept as `[]`: 10 failed.
- `flag/maybe` counted: 5 failed.
- Always appending instead of replacing in place: 10 failed.
- Comparing without `Normalise` and case: 3 failed.

**Task 5**
- The refusal removed: `A_read_only_member_can_not_flag` failed.
- Close as a second write: `Mark_done_closes_the_task_in_the_same_write` and `Undo_takes_both_back` failed.
- `Attempts = 1`: `A_write_in_between_is_retried` failed.
- The text check removed: `A_flag_in_the_text_is_left_alone` failed.
- (Review fixes) no line-ending repair: 2 failed; the `\r` guard dropped: 1 failed; the unchanged check before the read-back: 1 failed; the BOM refusal removed: 3 failed; the parse check returning its input: 1 failed; a comment after empty `tags` dropped: 1 failed; comments skipped inside a block: 1 failed; an empty block dropped: 1 failed.
- (Security fix) the other keys' check forced to pass: 6 failed.

**Task 6**
- Close accepted with any flag: both `Close_comes_only_with_the_done_flag` cases failed.
- The vault check after the path parse: nothing failed — an equivalent mutation, since the parse touches no store; calling the service before `NoteInAsync` failed `A_hidden_discreet_vault_answers_as_an_unknown_one` and 5 more.
- A folder's time from its direct children only: 2 failed.
- `Refuse` dropped from the service: `A_read_only_member_sees_the_flag_and_can_not_set_it` failed.

**Task 7**
- The question asked for every ✓: "no question for a card or a note that isn't a task" failed.
- Undo without `reopen`: 1 failed.
- The menu offered read-only (header, ⋯, tree): 1 failed each.
- `editorSettled` skipped: 2 failed.
- A save without the tree refresh: 1 failed.
- A palette yellow put back below 3:1: 4 failed.
- (Review fixes) the old wording: 3 failed; no `["tags"]` refresh: 1 failed; choices not disabled while reading: 1 failed; the row's label override back: 3 failed; no focus move: 1 failed; no discreet subscription: 1 failed; the `["flag"]` refresh back: 1 failed.

**Tasks 8 and 9**
- OR instead of AND: 6 failed.
- Folders not opened: 6 failed.
- The flag ignored: 3 failed.
- Files above folders: 8 failed.
- The order not stored: 2 failed.

**Task 10**
- Offered without a coarse pointer: 5 failed.
- Offered read-only: 1 failed.
- No " 2": 2 failed (the second after its test was fixed to take a JPEG).
- Dropped files not started: 2 failed.
- (Security fix) the hidden-vault check removed: 1 failed; no clearing on a vault change: 1 failed.

**Task 11**
- The archive kept: `Leaves_out_the_archive_and_the_trash` failed.
- `skip` ignored: `Filters_by_words_flag_and_vault` failed.
- Session-only dropped: `A_key_is_refused` failed.
- A hidden vault's name added to `vaults`: `A_hidden_discreet_vault_is_absent_chips_included` failed.
- Chips off by default: 1 failed; `skip` not sent: 1 failed; `hideAgain` without the reset: 1 failed; `hideAgain` removing instead of resetting: 1 failed; `leftElsewhere` without `recent`: 1 failed; reveal without the refresh: 1 failed; `?skip=Home` in the address: 1 failed.
- (Security fixes) the flag read on output replaced by null: 2 failed; the filters not cleared on a discreet change (three variants): 1 failed each; the badge not waiting for the vault list: 1 failed.

**The sign-out race**: the strengthened test failed 3 of 3 against the old app code.

**The UX fixes** (`7c31c99`): every place showing its own message: 15 failed; the message not hidden under the question: 1 failed; the other tabs' listener ignoring "hidden": 2 failed; no posting: 1 failed; Recent changes not emptying its cache: 1 failed; the chips wrapping again: 1 failed; a field back at 44 px, the native ✕ back, Clear without focus, Clear not clearing: 1 failed each; Recent changes back to an icon: 1 failed; the chips' group named "Flag": 5 failed. One survived: a constant message key (it only restarts the 8-second timer).

## Reading cost of Recent changes, 5000 notes

Measured over the server with `RecentApiTests.Recent_changes_for_5000_notes_over_the_server` (explicit): 5000 notes of about 2 KB in 50 folders, written through `EncryptedNoteStore`; cold after a day's advance, `VaultCatalog.Sweep()` and a new sign-in, so the request decrypts the vault from MongoDB; then warm. Times in ms, cold / warm / warm, two runs per build, while other agents were working on the machine:

| Request | Debug 1 | Debug 2 | Release 1 | Release 2 |
|---|---|---|---|---|
| Recent changes, no filter | 1151 / 68 / 75 | 972 / 57 / 63 | 1305 / 112 / 110 | 1166 / 93 / 87 |
| Recent changes, `words=folder 7 note` | 521 / 51 / 164 | 423 / 24 / 107 | 607 / 46 / 42 | 721 / 39 / 122 |
| The tree, `/api/vaults/Work/tree` | 837 / 104 / 79 | 732 / 97 / 59 | 1185 / 132 / 82 | 973 / 141 / 67 |

Cold is about a second, the cost of opening the vault from MongoDB (the calendar run measured What's on at 459–508 ms cold on an idle machine); warm, which is what a person sees once a vault is open, is 25–165 ms. Not far above a second, so no index; after the security review, Recent changes reads a note's flag only for the rows it returns when no flag filter is set. Run it with `tests/Kumbu.Server.Tests/bin/Release/net10.0/Kumbu.Server.Tests -method "*Recent_changes_for_5000_notes_over_the_server" -explicit only -showLiveOutput`.

## The UX check and what I did with it

A fresh subagent ran the server against the test MongoDB on 27018, with the web build served through `ASPNETCORE_WEBROOT` from a copy, never a real host. It walked the brief's path with Playwright and Chromium at 1280 and 375 px (touch), in light and dark, as Alice (`Home` discreet, `Work`) and as Bob, a read-only member of `Work`, at `d232732`.

**Fixed** (`7c31c99`):
1. **High:** each place offering the flag menu kept its own message, so a stale "Flagged Red · Undo" could sit over the newest one and its Undo took back the wrong write. Now one shared message; a newer write replaces it, and Undo always takes back the latest.
2. **High:** on a phone that message covered the buttons of "Also mark it done in the calendar?". It is hidden while the question is open.
3. **High (privacy):** a Recent changes page (and What's on, an older gap) left open in one tab kept showing a discreet vault hidden in another tab, since only the notes shell hears the sync socket. Hiding now tells the browser's other tabs over a `BroadcastChannel` carrying only "hidden", after the server confirmed it; each tab runs its own hide. Recent changes also empties its cache on any discreet change, so no earlier row shows for a moment. A hide on another device still reaches those pages when the session is next read (on focus).
4. At 375 px the nine flag chips took three rows; they now scroll on one.
5. The "Find by name" fields are 48 px; Recent changes' field got a 48 px Clear in place of the browser's tiny ✕.
6. Recent changes was an unlabelled clock icon; it now says "Recent changes", on a row of its own at the top of the tree.
7. The chips' group and the header's flag were both labelled "Flag"; the chips are now "Find by flag".

**Recorded, not changed** (for Chris):
- Recent changes shows writers as Activity does: the Welcome notes read "seed", and Bob sees Alice's name in quotes (`WriterLabels.Quoted`), where the spec's example reads "Alice". Changing Activity's labels is out of scope.
- Take photo ends with the upload's confirmation, "0 notes and 1 file uploaded, 0 replaced, 0 files skipped" with Done, and a second photo takes four taps. The spec asks for "the upload's usual confirmation"; a "Photo saved in Meetings · Take another" toast would serve the three-whiteboards case better.
- Menu rows ("Flag", "Take photo") are 44 px like every existing menu row; tree rows and their ⋯ are 44 px; the calendar's Later is 41 px wide.
- The properties box repeats the flag as `tags  #flag/blue` (the calendar run's point).
- The light scheme's yellow reads as ochre (labelled).

**What worked:** the eight flags and No flag at 48 px with icon and name; only the `tags` item changing in the note; the tick's question and Mark done writing `task: closed`, `closed` and `flag/done` in one go, with one exact Undo; the text flag's sentence; the tree search ("pump cert"), the flag chips, "Nothing called that here." and Escape; Last changed with `Pump/` first and folders above files; Recent changes at `/recent` with path, who, when and flags, its vault chips, words and flag filters and "Nothing matches that."; Take photo offered on the phone only, three photos landing as `Photo 2026-11-14 103249.jpg` and the like; Bob seeing flags with nothing to set and no Take photo; with `Home` hidden, nothing of it in Recent changes, its chips or the tree; no horizontal overflow at 375 px; no console errors beyond the expected 404s right after hiding.

## The security review and what I did with it

A fresh subagent got the spec's anti-goals and security boundary and `git diff 63d7d72...d232732`, and none of my reasoning. It answered the brief's six questions.

**No hole found** on:
- rights: `FlagService` asks `Refuse` before any read, so a read-only member, a read-only key or a connection without rights gets 403; the vault is checked first everywhere; Take photo is the existing `PUT /api/files` with its checks, offered only to writers;
- discreet vaults on the server: Recent changes, the tree and the flag filter are built from the session store's vault list, so a hidden vault is absent, with no difference in errors, timing or chips;
- stored content: no new collection, index or log; browser storage gains only `kumbu.treeOrder`;
- the untouched areas (`Auth/`, `CallerMiddleware`, `Crypto/`, `Dav/`, `Security.cs`, sharing and roles, sync, OAuth, the board's semantics);
- input validation (`limit`, `flag`, `skip`, `words`, `path`, `calendar`).

**Confirmed, fixed** (`0828354`):
- C1 (low): the flag editor found `tags:` by a line pattern that could match a line inside another key's multi-line single-quoted value, so "No flag" could change that value (only for someone who may write the note; the web app never sends it). The editor now refuses any result in which a key other than `tags` reads differently.

**Plausible, fixed** (`0828354`):
- P1 (low): right after a reveal the app icon could count the newly shown discreet vault until the vault list arrived. It now waits for the vault list.
- P2 (low): a photo returning from the camera after its vault was hidden opened an upload sheet naming the hidden vault. It is now dropped.
- P4 (low): Recent changes' filters could keep a hidden vault's name in memory. They are cleared when discreet vaults are hidden or shown.
- P3 (low, cost): Recent changes read every note's flag even without a flag filter; it now reads it only for the rows it returns.

**Recorded, not changed:**
- The tree is read again after every save and now parses each note's tags (cost, no escalation).
- Recent changes and What's on answer 503 while one of the caller's vaults is being re-encrypted.
- The flag endpoints work for keys and connections, as the calendar's do; only Recent changes is session-only.

## Decisions made without Chris

Each was settled with the brief's rules: break nothing, the spirit of the application, no new hole, the spec.

1. **Recent changes on the server** (open point 1). Options: Activity's changeset rows, `history` on the root, or each file's current state. Activity and `history` answer "which changesets", several rows per file; the spec wants one line per file as it is now. Chosen: each current note (`ListNotesUnderAsync` + `GetAsync`, the in-memory working set of `EncryptedNoteStore`, as `/api/changes` does) and attachment (`StoreAttachments.UnderAsync`), per vault the session's store lists, with its last write and writer, rendered by Activity's `WriterLabels`. No new store, no index. Session-only, as Activity. No rate limit: it reads the working set as What's on does, not the history that Activity's limit guards (a recorded cost point below). Paging by a growing `limit` (50, up to 500) with `more`, so Show more never skips a file that changed meanwhile.
2. **The tree's new fields** (open point 2). The flag is read with `Flags.Of`, which uses `TagReader` (frontmatter first, then text); a note's time is its stored last write; **a folder's time is computed on the server** (the newest write below it, folder markers included), since the client doesn't see markers. The checkpoint tree keeps its old shape (no times there).
3. **The flag write's endpoint and shape** (open point 3). `GET` and `POST /api/vaults/{vault}/flag`, following `CalendarApi`: the vault checked first, the role in `FlagService` (`Refuse` before any read), compare-and-swap, 409 on a conflict, 403 for a read-only member. `canWrite` is the same `Refuse` check, so a button is offered only where the write passes. Mark done is `calendar: "close"` on the same POST, refused unless the flag is `done`; Undo is `calendar: "reopen"` with the previous flag. Web types written by hand in `web/src/flags/api.ts`; `schema.d.ts` not regenerated.
4. **The flag editor's forms.** It edits flow lists, block lists, the string form and empty `tags`, keeps quoting, comments and line endings, and replaces a flag in place (so Undo is exact). It **refuses** what it can't change without risk (maps, nested lists, block scalars, flow lists over several lines, escapes, a byte order mark, comment lines between block items, a result that wouldn't parse or would change another key) with "I can't change this note's tags; edit them in the note". Options were a YAML round-trip (would rewrite the frontmatter) or editing more forms (more risk); refusing is the honest choice.
5. **A spelling written twice.** A calendar `set` writes the note's first spelling and removes the others, so Fix… can repair a note with `when:` and `When:`; reading still lists it under Dates I couldn't read. On a card, `reason` stays the board's (read as written), since `BoardReader` reads it exactly.
6. **Undo's spelling.** Undo after replacing `flag/RED` writes `flag/red`: exact for flags written as Kumbu² writes them.
7. **The tick's question** is asked only when the server says the note is an open to-do and no card (`openTask`).
8. **The tree refreshes after every save** (amendment 4), so Last changed and the flag follow ordinary edits; the cost is one tree read per save (see the cost section).
9. **The order switch is remembered on the device** (`kumbu.treeOrder`), as the gallery's order is. The spec says "as the calendar's grid is", but the grid is kept per user on the server; the device-local precedent is the gallery.
10. **Recent changes' words** match the path with its vault and without `.md`; attachments keep their extension; the tree's search matches the path within the vault without `.md`.
11. **The app-icon badge** is set only once the vault list is loaded and not being fetched, and never counts a discreet vault's share; if the vault list can't be read, the icon is left as it was.
12. **Take photo** names by local time like the archive's stamps, maps only JPEG, PNG, GIF and WebP to their extensions and lets anything else (HEIC) meet the upload's honest refusal; a photo coming back after its vault was hidden is dropped.
13. **The sign-out race** (beyond the plan): fixed in the app rather than loosening the test, since it was a real double request and a refill of the cache after sign-out.
14. **Wording of the messages after a flag write** (the spec gives none): "Flagged Red · Undo", "Ticked · Undo", "Ticked and marked done · Undo", "Marked important · Undo", "Flag removed · Undo".
15. **Left as they are, from the UX check:** Recent changes shows writers as Activity does (the Welcome notes' "seed", a member's name in quotes, `WriterLabels.Quoted`), since changing Activity's labels is out of scope; Take photo ends with the upload's usual confirmation, as the spec says; menu rows stay 44 px like every existing menu row.
16. **Telling other tabs of a hide** (from the UX check). Options: leave pages outside the notes shell (Recent changes, What's on) to learn of a hide on the next session read (on focus), or tell the browser's other tabs. Chosen: a `BroadcastChannel` that carries only the word "hidden", posted once the server confirmed the hide; each tab runs its own hide. It names no vault and adds no stored state.

## Deviations, surprises and open points for Chris

**Deviations from the plan:**
- One combined review per task or pair of tasks instead of two.
- `NoteInAsync` became `internal` too, not only `ReadEntryAsync`; `FrontmatterWrites.Same` became an `internal` method so the flag editor can compare other keys (behaviour unchanged; Board and Calendar `294/294`).
- Recent changes lists notes per vault rather than from the root, so a vault switched off is never read.
- The badge waits while the vault list is fetching, rather than comparing update times (that rule would never update the icon after a badge-only refresh).
- The flag chips moved into `web/src/flags/FlagChips.tsx`, shared by the tree and Recent changes.
- The sign-out fix (`b213101`) is outside the plan.

**Surprises:**
- The baseline gate took 27 minutes, not 55.
- The shell's sign-out test hid a real race (above).
- Every user's Skills vault appears among Recent changes' chips, even when empty.
- The REST note PUT turns CRLF into LF, so the server tests can't round-trip a CRLF note; CRLF is covered in Core.

**Open points for Chris:**
- Recent changes is cold at about a second for 5000 notes (warm 25–165 ms); the tree costs about the same, and is now asked again after every save. If that is too much on large vaults, the next step would be refreshing the tree only when a save changes the tags or the name, not an index.
- Recent changes and What's on answer 503 while any one of the caller's vaults is being re-encrypted (both read every vault).
- The properties box under the header still shows `tags  #flag/blue` beside the header's "Blue flag" (the calendar run's point about our fields in that box).
- In the light scheme the yellow flag reads as ochre; it is labelled.
- `schema.d.ts` is still not regenerated (the calendar run's open item).
- No spec outside this topic looked wrong. The spec was not changed; the calendar spec carries the amendments of spec §6, marked "amended 2026-11-14".

## Hand-test checklist

Each step says where to look and what passes. Run it on staging, or through Playwright (the UX check's scripts were in the run's scratchpad, `ux/`). Use a light and a dark scheme, a desktop width and a phone width of 375 px with touch. Alice has `Home` (discreet) and `Work`; Bob is a read-only member of `Work`.

**Flags (§2)**
1. As Alice, open `/Work/Pump/Certificate.md`, then ⋯ → **Flag**. Passes if eight choices and **No flag** show, each with its icon and name, and the white flag is outlined.
2. Choose **Red**. Passes if the header shows "Red flag", the tree shows a red flag after "Certificate", "Flagged Red · Undo" appears, and the note's frontmatter `tags` gained `flag/red` with nothing else changed (compare the history's previous version).
3. Tap the header's flag and choose **Blue**. Passes if a single message "Flagged Blue · Undo" shows; **Undo** puts Red back.
4. Choose **No flag**. Passes if the header shows nothing extra and the note is byte for byte as before step 2 (History → compare).
5. Give the note `when: <today>` and `task: open`, then choose **Done** in the flag menu. Passes if "Also mark it done in the calendar?" asks with **Mark done** and **Just the flag**, and on the phone both buttons can be tapped.
6. Tap **Mark done**. Passes if the note has `flag/done`, `task: closed` and `closed: <today>` in one new version, and the calendar line says it is closed. **Undo** gives both back.
7. Write `#flag/important` in the text of another note and open its Flag menu. Passes if it says "This flag is written in the note's text (#flag/important); change it there." and offers nothing.
8. Sign in as Bob and open the same note in `Work`. Passes if the flag shows in the header and the tree, and there is no Flag item, no flag menu and no Take photo anywhere.
9. In a terminal agent connected to Alice's vaults, run `tags flag` and `rg --tag flag/red`. Passes if the flagged notes are listed, and `help tags` names the eight flags.

**The explorer (§3)**
10. In the tree, tap the search, type "pump cert". Passes if only Pump › Certificate and its folder remain, opened.
11. Tap the red flag chip. Passes if only red-flagged notes remain; **Any flag** shows every flagged one; a search with no match says "Nothing called that here." followed by **Search the contents…**, which opens the search with the words.
12. Press Escape (or ✕). Passes if the full tree is back.
13. Switch to **Last changed**. Passes if the folder with the latest change anywhere below it is at the top, folders above files; reload the page: the choice is kept on this device, for every vault.

**Recent changes (§4)**
14. Tap **Recent changes** at the top of the tree. Passes if the address is `/recent` (no vault), and rows read like "Certificate · Work/Pump · Alice · 2 minutes ago" with flag icons, newest first.
15. Tap the `Home` chip off and on, type "pump cert", tap a flag chip. Passes if the rows narrow as asked; **Show more** adds the next ones; a row opens its note, an image or PDF opens in the lightbox.
16. Hide the discreet vaults (the pill), in this tab and once from a second tab. Passes if Recent changes, its chips and the tree show nothing of `Home` at once; show them again with the PIN: `Home` is back.
17. As Bob, open Recent changes. Passes if `Work`'s rows show with who and when, and nothing of Alice's own vaults.

**Take photo (§5)**
18. On a phone (375 px, touch), as Alice, open the folder menu of `Work/Meetings/`. Passes if **Take photo** is beside **Upload here**; at a desktop width it is not offered; as Bob it is not offered.
19. Take three photos. Passes if `Photo <today's date> <time>.jpg` and two more land in `Work/Meetings/` with the upload's confirmation, a name taken getting " 2".

**The calendar's fixes (§6)**
20. Tap **Today** in a vault's calendar. Passes if What's on and the badge don't count today's day note; give it `task: open`: now they do.
21. Give a note `when: <today>T14:00` and `until: <today>T15:00`. Before 14:00 What's on says "today · 14:00"; between 14:00 and 15:00 "ongoing · until 15:00".
22. A to-do overdue by more than a year says "overdue · 1 year" (three years: "3 years").
23. Write `When:` instead of `when:` in a note: it is in the calendar. Write both: it is under "Dates I couldn't read"; Fix… repairs it and keeps `When:`.
24. With `Home` shown, give it an entry that is on; install the app. Passes if the app icon's badge doesn't count `Home`'s share, while the badge inside the app does.

## The pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/198 (draft until this report was committed, then marked ready). Not merged, tagged or deployed; the version is unchanged.
