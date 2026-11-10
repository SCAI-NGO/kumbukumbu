# The findings of the pre.4 tests: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Text typed offline around the end of a reveal is saved once, with the latest text (F9); once a reveal has ended nothing on screen names the discreet note (F10); no 404 for an unpublished note (F7); a search hit's snippet is the line that matched best (F8).

**Architecture:** Web app only, plus `SectionSearch.Snippet` in `Kumbu.Core`. F9: the note's save no longer waits for react-query's idea of being online, and the editor tries a failed save again when the browser is online. F10: the editor's navigation guard lets the hide's navigation pass, and the shell shows nothing of a vault this tab hid again while the route still names it; a 404 in a shown discreet vault shows no path. F7: the per-note publication is asked only for a note the vault's list names. F8: the snippet picks the whole query, else the most distinct terms, else the first line among equals.

**Tech stack:** React 18, TanStack Query 5.105, React Router 6 (data router, `v7_startTransition`), Zustand, Vitest 2 with fake timers, Testing Library; C# .NET 10, xUnit (Microsoft.Testing.Platform).

**Spec:** `docs/superpowers/specs/2026-11-10-pre4-findings-design.md` (approved; do not edit). Brief: `docs/superpowers/overnighter/2026-11-10-pre4-findings-brief.md`.

## Global constraints

- British English in code, comments, docs, commits and user-visible text. Comments in the discreet code explain their reasons in full sentences; elsewhere only where the reasoning isn't obvious. New files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- No new npm or NuGet package. No server change: not the API or its answers, not `LateSave.cs` or the late-save window, not the PIN check, the session, encryption, accounts, sharing, WebDAV, sync, the CSP.
- The repository names no organisation, customer or employer; test data uses Alice, Bob, Dana, `Home`, `Tagebuch`.
- Step 2.9 online (typing until the end, the note closes, the app goes back to the last ordinary note, everything saved once) must stay as it is.
- Once a reveal has ended, nothing on screen names a hidden vault or a note in it. Outside discreet mode nothing may differ between users with and without a PIN or discreet vaults.
- Commit messages imperative and free-form, no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Subagents never merge, push or switch branches.
- Commands: one web test file `pnpm -C web test --run <file relative to web/>`; all `pnpm -C web test --run`; the web's CI gate `pnpm -C web build`. Core tests of one class: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*SearchTests"`. Before web tests after a checkout: `pnpm -C web install --frozen-lockfile`. The environment needs `source /tmp/claude-0/-home-user-kumbukumbu/1091e662-678d-533b-9c29-7bc341ebe8b4/scratchpad/env.sh` (Node 26 on the `PATH`).

## The causes, ranked (established before this plan, by experiment)

A reproduction (`web/src/discreet/offlineEnd.test.tsx`, Task 1 and 2) runs the spec's example against a fake server that behaves as the real one around the end (late-save marker from `open-note`, one late save within 5 minutes, put back once on failure, 404 otherwise, the note API's 404 sentence with the path). Unchanged code reproduces staging exactly: saves `[…, "… off0"]`; the route stays on `/notes/Tagebuch/Heute.md`, the header says "Tagebuch / Heute", the main says "You have no vault named Tagebuch." / "This note was moved or removed elsewhere.". Each candidate was then switched on or off alone:

**F9**

1. **Confirmed, the root cause.** `useSaveNote` (`web/src/api/notes.ts:274`) is a mutation on react-query's default `networkMode: "online"`. Offline it pauses before sending, holding the text it had then (` off0`); `useAutosave.run` keeps it as `inFlight`, so every later keystroke only sets `again`. `flush`, hence `saveAhead` and the end's `hideAgain` → `saveOpen`, wait on it until the browser is online; `hold` is never reached (after the end + 2 s: `hiding: true`, `revealed: true`, one mutation `pending`, `isPaused: true`, content ending ` off0`). Online again, the paused save is the one late save; the save of the latest text that follows gets 404. With `networkMode: "always"` alone, the example saves the latest text once. (Spec candidate 1; brief lead.)
2. **Confirmed, part of 1.** "Later edits are not scheduled while that save is pending": they set `again` and wait behind the paused save.
3. **Confirmed as the mechanism of the loss, not a cause of its own** (spec candidate 5): the second save after the end is no late save in the server's eyes and gets 404; nothing on the client can change that rule (brief point 3), so the fix sends the latest text in the one late save.
4. **Ruled out** (spec candidate 2): after the stale save succeeds the editor holds the version it returned (`s.version = result.saved.version`); the next save gets 404 from the late-save rule, not 409.
5. **Ruled out as it stands** (spec candidate 3): `hold` takes `unsaved()`, the editor's current text and last saved version. It was never reached; once reached (fix 1) it holds the latest text.
6. **Ruled out** (spec candidate 4): only one retry fires after the end once fix 1 and F10's fix are in: the editor is gone (navigation), the held draft retries on `online`.
7. **New, confirmed, smaller:** online again *before* the end with no further keystroke, a failed save is retried only at `saveAhead` or the end; the spec wants it saved when the browser is online again. `useAutosave` listens for `online`.
8. **Infrastructure checked:** mutations have no retries (react-query default 0), so no second stale send; the `online` event reaches both react-query's `onlineManager` and `hold`; `navigator.onLine` alone doesn't pause react-query (it listens to the events), which is why the old offline tests never saw the pause.
9. **Not part of this fault, recorded:** `useTellOpenNote` marks the open note as told even when its `PUT open-note` fails (a note opened while offline): the server then has no marker and the late save gets 404. Open item.
10. **Not fixable on the client, recorded:** a save that *arrives* after the end while the user has typed more takes the late save, and the next save gets 404. Open item (server rule).

**F10**

1. **New, confirmed, the root cause.** The editor's navigation guard (`MarkdownEditor.tsx` `shouldBlock`, `useBlocker`) blocks every navigation while text is unresolved (failed, in flight, unsaved). Offline it is unresolved by definition, so `hideAgain`'s `navigate` is blocked; `shouldBlock` even puts the discreet path back as `currentPath` and `mode: "write"` after `forgetVault`. The route stays on the discreet note, its queries are dropped and asked again (404). With `shouldBlock` answering false while `useDiscreet.hiding`, the tab goes to `/notes/Home/a.md`.
2. **Confirmed, contributing:** between the mode's reset and the commit of the navigation (a transition) the shell renders once more with the old route: header and title name the note. Seen even online (step 2.9) with a screen watcher. The shell shows nothing of a vault the tab hid again.
3. **Confirmed, contributing** (spec candidate 1): the note's read asked again (after `removeQueries`, on reconnect or focus) answers 404 with the path in its sentence; while the view stays it is rendered.
4. **Ruled out** (spec candidate 2): `vaultOfRoute(pathname)` named the discreet vault and `navigate` was called; with only the guard changed, it navigates.
5. **Ruled out** (spec candidate 3): `followSession` returns while `hiding`; one run only (`running`).
6. **Ruled out as a cause** (spec candidate 4): the view kept its text because its route never changed (cause 1), not because of its `key`.

## Review focus

1. A note of an *ordinary* vault open in Write mode with a failed save, while a hide runs (✕ from the pill): the guard must still keep it (the hide doesn't navigate away from an ordinary note, and `mode: "read"` unmounts its editor, whose unmount save sends the text). Test in Task 2.
2. Offline with no discreet vault at all: a save fails at once, "Not saved — retry" shows, and online again the latest text is saved once. Test in Task 1 (`MarkdownEditor.test.tsx`).
3. A vault hidden again and shown again in the same tab: its notes must render normally again (`gone` yields to `names`). Test in Task 2.
4. A member's view of a shared vault's published note keeps its bar after F7. Test in Task 3.
5. A query with no term and a line with the whole query that comes after a line with more terms (F8). Tests in Task 4.

---

### Task 0: Baseline

- [ ] **Step 1:** `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`. Expected: 125 files, 2192 tests passing, build green.
- [ ] **Step 2:** Server and core. `dotnet test --solution Kumbu.slnx` in one go exhausts this environment's file limit (MongoDB dies with "Too many open files" at the session's hard limit of 20000), so run `scratchpad/bin/shards.sh <log dir>`: the core tests, then the server tests in CI's four shards, each against a fresh `kumbu-test-mongo`. Expected: everything passes; skips as CI's (the model and TensorX tests; root skips two more).
- [ ] **Step 3:** Record counts and failures in the report. A flaky class (`AccountApiTests`, `RekeyTransitionTests`, `PairingTests`, sync hub): run it alone three times and note it.

### Task 1: F9, the latest text is saved once

**Files:**
- Create: `web/src/discreet/offlineEnd.test.tsx` (the fake server, helpers and the F9 tests below)
- Modify: `web/src/api/notes.ts:274-296` (`useSaveNote`)
- Modify: `web/src/note/useAutosave.ts:197-199` (a new effect after the one there)
- Test: `web/src/note/useAutosave.test.ts`, `web/src/note/MarkdownEditor.test.tsx`

**Interfaces:**
- Produces: `offlineEnd.test.tsx` exports nothing; Task 2 appends a `describe` using its helpers `diary`, `typeInTunnel`, `gate`, `wait`, `PATH`, `FIRST`, `NAMES`, and the server's `gates`, `puts`, `saves`, `note`, `online`, `offline`.

- [ ] **Step 1: The reproduction and the F9 tests.** Create `web/src/discreet/offlineEnd.test.tsx` as committed on this branch's Task 1 commit (the controller hands the implementer the file; it was written and run against the unchanged code before this plan). What it pins:
  - `server(until, ahead)`: as described under "The causes"; `GET /api/notes/Tagebuch/Heute.md` answers ``There is no note at /Tagebuch/Heute.md (if it was removed, it is in the trash).`` once hidden; `gates.write` / `gates.read` hold back answers already decided; `puts()` counts the note's PUTs sent.
  - `browser(net)`: offline/online set the network, `navigator.onLine` and dispatch `offline`/`online` (which react-query's `onlineManager` hears); `afterEach` calls `onlineManager.setOnline(true)`.
  - Tests, in `describe("F9: text typed offline around the end of a reveal is saved")`:
    1. "the spec's example: offline two minutes before the end, online three minutes after it, saves the latest text once" — 5-minute reveal, ` Morgens.` saved, offline at end − 2 min, ` off0` … ` off29` every 3 s, past the end, online at end + 3 min: `saves` from `FIRST` on equal `[FIRST, FIRST + tunnel]`, the server's note holds `FIRST + tunnel`, still two after 10 more minutes.
    2. "online again before the end, saves the latest text at once, without waiting for another keystroke" — offline at end − 2 min, 5 words, online at end − 60 s: within 2 s `[FIRST, FIRST + tunnel]`, still two after 10 minutes.
    3. "the end while a save is in flight: what was typed meanwhile is the late save" — a save arriving before the end, answered after it; typed meanwhile: `[FIRST, FIRST spät, FIRST spät später]`, three after 10 minutes.
    4. "offline until six minutes past the end: the tunnel text is gone, as the late-save window is five" — the last save stays `FIRST`, the server's note too.
- [ ] **Step 2: Unit tests.**
  - `useAutosave.test.ts`, new `it("a save that failed is tried again when the browser is online again, with the latest text")`: change → save rejects (`new TypeError("Failed to fetch")`) → `status` `"error"`; change again without the timer running out is not needed: dispatch `window` `online` → one new call with the latest text and the version known; dispatched again after it succeeded → no further call. And `it("online again doesn't save after a conflict, nor when nothing failed")`: with a 409 answered, `online` makes no call; with everything saved, none either.
  - `MarkdownEditor.test.tsx`, new `it("offline, a save fails at once, says Not saved, and online again saves the latest text once")`: `onlineManager.setOnline(false)` plus a PUT handler that throws `TypeError` while offline; type `a`, wait 1 s → the "Not saved" alert, one PUT tried; type `b` → another PUT tried at once with `…ab` (no pause); go online (`onlineManager.setOnline(true)`, dispatch `online`, the handler answers) → exactly one more PUT, with the text ending `ab`, and the alert goes. `afterEach` gets `onlineManager.setOnline(true)`.
- [ ] **Step 3: Run them, expect red.** `pnpm -C web test --run src/discreet/offlineEnd.test.tsx src/note/useAutosave.test.ts src/note/MarkdownEditor.test.tsx`. Expected: offlineEnd tests 1 and 2 fail (1: ends `… off0`; 2: a stale save before the latest), 3 and 4 pass (guards); the useAutosave online test fails (no call); the MarkdownEditor offline test fails (no PUT tried while offline: the save is paused).
- [ ] **Step 4: `useSaveNote`.** Add `networkMode: "always"` to the mutation's options, with a comment saying why: a save sends the text it was started with; paused while the browser is offline it would send that text later (after the end of a reveal: as the one late save, with an old text), and everything after it would wait. Failing at once, the editor keeps the text and saves the latest.
- [ ] **Step 5: `useAutosave`.** A new effect: on `window` `online`, if `s.failed && !s.conflict && !s.stopped`, clear `failed` and `run()`. Cleans its listener up. Extend the hook's doc comment by a sentence ("A save that failed is tried again once the browser is online again.").
- [ ] **Step 6: Green.** Same command as Step 3. Expected: all pass. Then `pnpm -C web test --run && pnpm -C web build`: all pass, build green.
- [ ] **Step 7: Commit.** `git add web/src/discreet/offlineEnd.test.tsx web/src/api/notes.ts web/src/note/useAutosave.ts web/src/note/useAutosave.test.ts web/src/note/MarkdownEditor.test.tsx && git commit` with "Save the latest text when the browser is back online, not the one a paused save held".
- [ ] **Step 8: Mutations**, each alone, restored with `git checkout -- <file>`:
  - M1 remove `networkMode: "always"` → offlineEnd 1 and 2 and the MarkdownEditor offline test fail.
  - M2 remove the `online` effect → offlineEnd 2 and the useAutosave online test fail.
  - M3 in the `online` effect drop the `s.conflict` check → the conflict test fails.

### Task 2: F10, nothing on screen names the note after the end

**Files:**
- Modify: `web/src/note/MarkdownEditor.tsx:172-182` (`shouldBlock`)
- Modify: `web/src/discreet/state.ts` (store field `gone`, hook `useGoneWithVaults`, the session subscriber at the end)
- Modify: `web/src/discreet/hideAgain.ts:172` (before the `forgetVault` loop)
- Modify: `web/src/shell/MobileShell.tsx:136-137` (the hook), `:296` (`TopBar`), `:315` (`main`)
- Modify: `web/src/note/NoteView.tsx:288,367` (the note's error text)
- Test: `web/src/discreet/offlineEnd.test.tsx` (new `describe`), `web/src/discreet/hideAgain.test.tsx`

**Interfaces:**
- Consumes: Task 1's helpers in `offlineEnd.test.tsx`.
- Produces: in `web/src/discreet/state.ts`: `gone: ReadonlySet<string>` on `useDiscreet`'s state (initially empty; not cleared by `reset()`; cleared with `unsent` when the session's user changes), and `export const useGoneWithVaults = (path: string | null): boolean` — true when `path`'s vault is in `gone` and not in `names`.

- [ ] **Step 1: The F10 tests** in `offlineEnd.test.tsx`, `describe("F10: after the end, nothing on screen names the discreet note")`, with a helper `watchScreen({ fromNow = false } = {})` that records `document.body.textContent` at every DOM change (a `MutationObserver` on `body`, `childList`, `subtree`, `characterData`) once the tab's reveal has ended (`useDiscreet` `revealed` true → false), or from now with `fromNow`, and answers what it saw; and `namesNothing(seen)` = no text matching `/Tagebuch|Heute/`:
  1. "the spec's example: the note closes offline at the end, Home is shown, and nothing names it, also once online" — as Task 1's example; at end + 2 s the route is `/notes/Home/a.md`; no PUT of the note leaves the tab while offline after that; online at end + 3 min, `Einkaufen.` (the Home note) is shown; nothing seen names it; `currentPath` is `/Home/a.md`.
  2. "the end while a save is in flight, with the note's read answering 404 meanwhile" — the slow save of Task 1's test 3; the note's query invalidated while the hide waits (404); then the answer; route `/notes/Home/a.md`; nothing seen names it.
  3. "a 404 before the tab's end (the server's clock is ahead) shows no path, and the hide that follows names nothing" — Read mode, `ahead: 20_000`; at end − 10 s the note's query is refetched (404); nothing seen from then on contains "There is no note at"; route `/notes/Home/a.md`; nothing seen after the reveal's end names it.
  4. "a 404 that arrives after the hide names nothing" — Read mode; a read of the note under way at the end (`gates.read`), answered after the hide; nothing seen names it.
  5. "✕ while offline with text unsaved goes to Home and names nothing".
  6. "online all along (step 2.9): typing until the end, the note closes, Home is shown, everything is saved once" — a word every 5 s until the end; route `/notes/Home/a.md`; the server's note holds every word; no text saved twice; nothing seen names it.
  7. "a note that isn't there in a shown discreet vault: the 404 names no path" — add a handler `GET /api/notes/Tagebuch/*` answering the note API's 404 sentence for the requested path; navigate to `/notes/Tagebuch/Fehlt.md` while shown: the page says `There is no such note (if it was removed, it is in the trash).` and nothing contains "There is no note at".
  8. "shown again after a hide, the vault's notes render again" — hide with ✕ online, `server` shown again (a `shownAgain()` on the fake, as `hideAgain.test.tsx`'s), session and vaults read again; navigate to `/notes/Tagebuch/Heute.md`: its heading and text are shown.
- [ ] **Step 2: Review focus 1, in `hideAgain.test.tsx`:** `it("✕ leaves an ordinary note's unsaved text to its own guard")` — on `/notes/Work/a.md` in Write mode with a save failing (PUT answers 500), ✕: the route stays `/notes/Work/a.md`, and the text typed is sent again (one more PUT of `/api/notes/Work/a.md` with it) when the editor goes (the unmount save).
- [ ] **Step 3: Red.** `pnpm -C web test --run src/discreet/offlineEnd.test.tsx src/discreet/hideAgain.test.tsx`. Expected: F10 tests 1, 2, 4, 5, 6 and 7 fail (1, 5: the route stays on the note; 2, 4, 6: the old header seen after the end; 7: the path in the sentence); 3 and 8 and Step 2's test may pass already (guards). The F9 tests still pass.
- [ ] **Step 4: The guard.** In `shouldBlock`, answer false while `useDiscreet.getState().hiding`, with the reason in a comment: the hide has held what is unsaved (`hold` in `hideAgain.ts`), and the note goes with the vaults; blocking would keep the hidden note on screen (and put it back as the open note).
- [ ] **Step 5: `gone`.** In `state.ts` the field and the hook (Interfaces above; the vault of a path is `vaultOf` from `../api/paths`). Doc comment: memory only, never shown; it is what lets a view of a hidden vault that renders once more show nothing of it. In `hideAgain.ts` `run`, just before `for (const name of names) useEditor.getState().forgetVault(name);`: `useDiscreet.setState({ gone: new Set([...useDiscreet.getState().gone, ...names]) })`.
- [ ] **Step 6: The shell.** In `MobileShell`, right after `const path = …` (before any early return: it is a hook): `const gone = useGoneWithVaults(path);` with a comment ("the discreet note on its way out: the route still names it until the navigation is in"). `TopBar` gets `path={gone ? null : path}`; `main` renders `<div className="flex-1" />` when `gone`, else as today.
- [ ] **Step 7: The 404 in a shown discreet vault.** In `NoteView`, a constant `NO_SUCH_NOTE = "There is no such note (if it was removed, it is in the trash)."` and, for both places that render `errorText(note.error)`, that sentence instead when `note.error` is an `HttpError` with status 404 and `useDiscreet((s) => s.names.has(vaultOf(path)))`. Comment: the note API's 404 names the path, which mustn't be on screen for a discreet vault (spec §3).
- [ ] **Step 8: Green.** Same command as Step 3: all pass. Then `pnpm -C web test --run && pnpm -C web build`.
- [ ] **Step 9: Commit** "Let the hide's navigation pass the editor's guard, and show nothing of a vault hidden again".
- [ ] **Step 10: Mutations**, each alone:
  - M1 drop `|| useDiscreet.getState().hiding` from `shouldBlock` → F10 1 and 5 fail.
  - M2 `TopBar path={path}` and `main` without the `gone` branch → F10 2 and 5 fail (seen: the old header).
  - M3 don't set `gone` in `hideAgain.ts` → as M2.
  - M4 `useGoneWithVaults` without `!s.names.has(vault)` → F10 8 fails.
  - M5 `NoteView` renders `errorText` again for the discreet 404 → F10 7 fails.

### Task 3: F7, no request for an unpublished note's publication

**Files:**
- Modify: `web/src/api/publications.ts:44-61` (a new hook beside `usePublication`)
- Modify: `web/src/note/PublicationBar.tsx:29`
- Modify: `web/src/shell/MobileShell.tsx:262-268` (`publication`, `mayPublish`, `publications`)
- Test: `web/src/note/PublicationBar.test.tsx`, `web/src/shell/MobileShell.test.tsx` (or the test file that covers the shell's Publish item: `rg -n "Publish" web/src/shell/*.test.tsx`)

**Interfaces:**
- Produces: `export function useNotePublication(path: string, enabled = true): { data: PublicationView | null | undefined }` in `publications.ts` — asks `usePublications(vaultOf(path), enabled)`; asks `usePublication(path)` only while `enabled` and the list names `path`; `data` is `undefined` until the list is in (or while the listed note's own read is), `null` when the list doesn't name it (or its own read says 404), else the per-note answer (with `link` for the owner).

- [ ] **Step 1: Tests.**
  - PublicationBar: "an unpublished note asks for no publication of its own" (owner and member: no `GET /api/publications/{vault}/{path}` at all, no bar); "a published note's bar comes from its own read once the vault's list names it" (owner: Copy link, Update copy when `older`, Unpublish; the per-note GET made once); "a member's bar for a note the shared vault's list names" ("Published by the vault's owner"; the list of the shared vault asked); "switched off, a published note's bar still offers Unpublish". Keep every existing test passing; adapt their fake APIs to answer the vault's list where they don't yet.
  - Shell: "Publish is offered for an unpublished note of her own vault, not for a published one, and no per-note publication is asked for the unpublished one"; after publishing from the menu the list is asked again and the bar shows.
- [ ] **Step 2: Red.** `pnpm -C web test --run src/note/PublicationBar.test.tsx src/shell/MobileShell.test.tsx` — the "asks for no publication" tests fail.
- [ ] **Step 3: Implement** `useNotePublication`; `PublicationBar` uses it instead of `usePublication(path)`; in `MobileShell` drop the `usePublication` call and derive `mayPublish` from the list already asked there: `ownNote && publishEnabled === true && publications !== undefined && !publications.publications.some((p) => p.path === path) && publishableAsNote(noteVault, path ?? "")` (move the `publications` line above it).
- [ ] **Step 4: Green.** The same command, then `pnpm -C web test --run && pnpm -C web build`.
- [ ] **Step 5: Commit** "Ask for a note's publication only when the vault's list names it".
- [ ] **Step 6: Mutations:** M1 `useNotePublication` asks `usePublication(path, enabled)` regardless → the "asks for no publication" tests fail; M2 `mayPublish` back to `publication.data === null` (with the old call) → the shell test fails; M3 `data` null while the list is undefined → no assertion change expected? Then add one: while the list hasn't answered, no bar and no per-note request.

### Task 4: F8, the snippet is the line that matched best

**Files:**
- Modify: `src/Kumbu.Core/Search/SectionSearch.cs:144-156` (`Snippet(Chunk, string)`)
- Test: `tests/Kumbu.Core.Tests/Search/SearchTests.cs`

- [ ] **Step 1: Tests** (new `[Fact]`s; a 60-row table chunk built in the test: header `| Code | Teil | Fehler |`, separator, rows `| E{200+i} | Pumpe {i} | Druck zu niedrig |` for i = 1…60, as lines 47–74 would hold rows 28…55: build the chunk from rows 28 to 55):
  - `Snippet_prefers_the_line_with_the_whole_query`: query `Pumpe 51 Druck`… whole query appears in no row (`Pumpe 51 | Druck`), so add a prose line `Pumpe 51 Druck prüfen` after the rows: it wins over every row.
  - `Snippet_prefers_the_line_with_the_most_terms`: query `Pumpe 51 Druck` on the table alone: `| E251 | Pumpe 51 | Druck zu niedrig |` (three terms) wins over `| E228 | Pumpe 28 | Druck zu niedrig |` (two).
  - `Snippet_takes_the_most_terms_when_the_row_says_something_else`: row 51 reads `| E251 | Pumpe 51 | Dichtung undicht |`; query `Pumpe 51 Druck`: rows with `Pumpe` and `Druck` have two terms, row 51 has two (`pumpe`, `51`): the first line with two terms wins — `| E228 | Pumpe 28 | Druck zu niedrig |`. Pin exactly that line.
  - `Snippet_takes_the_first_line_among_equals`: two lines with one term each, the first wins.
  - `Snippet_without_any_term_is_the_start_of_the_section`: query `xyz` → `SectionSearch.Snippet(chunk)`.
  - The existing `Snippet_of_a_table_part_keeps_its_header_row` and the kettle test stay green unchanged.
- [ ] **Step 2: Red.** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*SearchTests"`: the most-terms test fails (`E228` instead of `E251`).
- [ ] **Step 3: Implement.** One pass over `Body(chunk)`'s trimmed non-empty lines: the first line containing the whole `query` (ordinal, ignoring case) is the snippet at once; otherwise keep the first line with the largest count of distinct `Bm25.QueryTerms(query)` among its `Bm25.Terms`; none above zero → `Snippet(chunk)`. Update the doc comment to say so.
- [ ] **Step 4: Green.** The same class; then the whole core suite `dotnet test --project tests/Kumbu.Core.Tests` and the search API tests `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*SearchApiTests"` (and `*EarlierSearch*` if present: `rg -l "EarlierSearch|earlier" tests`).
- [ ] **Step 5: Commit** "Show the line that matched best as a search hit's snippet".
- [ ] **Step 6: Mutations:** M1 return the first line with any term (old behaviour) → most-terms test fails; M2 count terms with repetition (`Count()` without `Distinct`) → a test with a line repeating one term (`Pumpe Pumpe Pumpe`) before row 51 fails — add that line to the most-terms test; M3 take the last line among equals → the first-among-equals test fails.

### Task 5: Final gate, open items, pull request, report

- [ ] **Step 1:** As CI: `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; the server tests by `scratchpad/bin/shards.sh <dir> --configuration Release --no-build` (the file limit, Task 0); `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`.
- [ ] **Step 2:** `docs/superpowers/open-items.md`: F9 cause 9 (open-note not told again after a failed PUT), F9 cause 10 (a save arriving after the end takes the late save), ✕ while offline (brief point 6: what happens today), and the pull request's row.
- [ ] **Step 3:** Pull request against `master`, "The findings of the pre.4 tests"; body: the ranked causes with verdicts, the decisions made without Chris, the security boundary untouched, ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] **Step 4:** The report `docs/superpowers/overnighter/2026-11-10-pre4-findings-report.md` as the last commit.
- [ ] **Do not merge, tag or deploy; don't change the version.**
