# Overnighter report: the findings of the pre.4 tests

Pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/184 (draft, against `master`; not merged, tagged or deployed; version unchanged). Branch `fix/pre4-findings`, on `master` at `fc3edeb`. Spec `docs/superpowers/specs/2026-11-10-pre4-findings-design.md` (unchanged), plan `docs/superpowers/plans/2026-11-10-pre4-findings.md`.

All four sections are done: §2 F9, §3 F10, §4 F7, §5 F8. None are left. The security boundary is not touched: no file under `src/Kumbu.Server` changed; the only server-side file is `src/Kumbu.Core/Search/SectionSearch.cs` (F8), which the brief allows.

## 1. Environment

- **Docker.** `docker info` said "Cannot connect": the setup script's `dockerd` wasn't running (a stale `/var/run/docker.pid`). I removed the pid file and started `dockerd` by hand.
- **Node and pnpm.** The image had Node 22 and pnpm 10.28.0. I installed Node 26.13.0 with `n` (into `/usr/local/bin`) and `npm install -g pnpm@10.32.1`.
- **.NET.** SDK 10.0.403 was there (`global.json` asks for 10.0.100, roll forward). `DOTNET_CLI_TELEMETRY_OPTOUT=1`. I fetched arctic-m's `tokenizer.json` at the commit and SHA-256 `ci.yml` pins and set `KUMBU_TOKENIZER`, as CI does.
- **MongoDB.** My own `kumbu-test-mongo` (`mongo:7.0`, replica set, `--ulimit nofile=20000:20000`), `TESTCONTAINERS_RYUK_DISABLED=true`.
  - **Surprise:** the session's hard limit is 20000 open files, and `dotnet test --solution Kumbu.slnx` in one go killed MongoDB twice ("Too many open files", WiredTiger panic) — once in the full run, once in CI's `accounts` shard. Those two aborted runs are not counted.
  - **What worked:** a script (`scratchpad/bin/shards.sh`) that runs the core tests, then the server tests namespace by namespace, each against a fresh MongoDB started with `--wiredTigerEngineConfigString "file_manager=(close_idle_time=5,close_scan_interval=2,close_handle_minimum=200)"`. Every namespace passed that way. CI's own split is unchanged.
- **Process.** The superpowers skills were available. I used `writing-plans` for the plan and emulated subagent-driven development with the Agent tool: a fresh implementer per task (red, green, commit, every mutation alone), then a fresh spec-compliance reviewer and a fresh code-quality reviewer per task, and a fresh whole-branch reviewer at the end. Task 4 (F8, C# only) ran in parallel in its own git worktree and was cherry-picked after F7, so the commits stay in the order of value. The review fixes I made myself, test first, each with its mutation checked.
- **Root cause first.** Before the plan I wrote the reproduction (`web/src/discreet/offlineEnd.test.tsx`) against the unchanged code and switched each candidate on and off by hand (section 4). That took about 40 minutes and is why the plan could name the causes.

## 2. The plan review

A fresh subagent reviewed the plan with the spec, the brief and the tests I handed over. Blocking findings, all folded in (`f0858f9`):

1. Task 1's M3 was an equivalent mutant (dropping `!s.conflict` from the `online` effect changes nothing, as `run` returns on a conflict): the effect became `if (s.failed) …` and M3 "drop the `s.failed` check", with a test that a pending keystroke keeps its 800 ms.
2. `gone` was never cleared but by a new user: a vault made ordinary would stay blank. `setFromVaults` now drops from `gone` every vault listed as ordinary (test 10).
3. Tests 7, 8 and Step 2's were not in the handed files, and `shownAgain` didn't exist: the plan says how to extend the fake.
4. F10 test 4 never got a 404 (the read was decided before the end): `ahead: 2_000`.
5. Task 2's mutation expectations contradicted Step 3: settled from measured runs.

Important ones folded in: `networkMode: "always"` also changes a task box's tick offline — logged as a decision with a test; the fake's `open-note` is a no-op while hidden, as the server's; `watchScreen` records `innerHTML` (labels, titles) and attributes; a test for the end by a `hidden` frame; the guard narrowed to a gone vault; the server classes that assert snippets named in Task 4. Minor: anchors, absolute paths, the open-items row with the report.

## 3. Task 0: planned vs. measured

| | Planned (brief) | Measured before any change (`b7ad689`) |
|---|---|---|
| Web | 125 files, 2192 tests, all passing, build green | **125 files, 2192 tests, all passing; build green** |
| Server and core | all passing in CI, 4 skipped, about 5,500 | **5669 tests: 5659 passed, 0 failed, 10 skipped** (core 2148 with 3 skipped; server 3521 with 7 skipped) |

The server numbers come from the namespace-by-namespace runs (section 1); the server's code is the same at every commit of this branch, so the runs made while Task 1 (web only) was already committed count as the baseline. The 10 skips: the model and TensorX tests, and 2 because the run is as root. No flaky class showed up.

## 4. The causes, ranked, and how each was settled

Reproduction: `offlineEnd.test.tsx`'s fake server behaves as the real one around the end (`CallerMiddleware.cs`, `Sessions.cs`, `LateSave.cs`): the marker from `open-note` (only while shown), one late save of exactly that note within 5 minutes, put back once if it fails, a late save's 409 with the status only, 404 for everything else of the vault, the note API's 404 sentence with the path. The browser goes offline through the network, `navigator.onLine` and the `offline`/`online` events (react-query's `onlineManager` hears the events). On the unchanged code it reproduced staging exactly: saves `[…, "… off0"]`; the route stayed on the note; the header said "Tagebuch / Heute"; the main said "You have no vault named Tagebuch." and "This note was moved or removed elsewhere.".

**F9**

1. **Confirmed, the root cause.** `useSaveNote` was a mutation on react-query's default `networkMode: "online"`: offline it pauses before sending, with the text it had then (` off0`). `useAutosave` keeps it as `inFlight`, later keystrokes only set `again`, and `flush` — hence `saveAhead` and the end's `hideAgain` → `saveOpen` — waits for it until the browser is online; `hold` is never reached. Checked at the end + 2 s: `hiding: true`, `revealed: true`, one mutation `pending` with `isPaused: true` and content ending ` off0`. Online again, the paused save is the one late save, the save of the latest text gets 404. With only `networkMode: "always"`, the example saves the latest text once.
2. **Confirmed, part of 1** (spec 1, second half): later edits wait behind the paused save.
3. **Confirmed as the mechanism, not a cause** (spec 5): the second save after the end is no late save to the server (404); the fix puts the latest text into the one late save.
4. **Ruled out** (spec 2): after the stale save the editor holds the version it returned; the next save got 404, not 409.
5. **Ruled out as it stands** (spec 3): `hold` takes `unsaved()` (current text, last saved version); it was simply never reached.
6. **Ruled out** (spec 4): once fixed, only the held draft retries after the end (the editor is gone, and since `7d40ac6` it saves nothing more).
7. **New, confirmed:** online again before the end with no further keystroke, a failed save waited for `saveAhead` or the end; now `useAutosave` retries on `online` (also when the `online` came while a save was in flight that then failed — from the review).
8. **Infrastructure:** mutations have no retries (default 0); `navigator.onLine` alone doesn't pause react-query, which is why the older offline tests never met the pause.
9. **Not this fault, recorded:** a note opened while offline is never told to the server (`useTellOpenNote` marks it told though the PUT failed): no marker, the late save gets 404. Open item.
10. **Not fixable on the client, recorded:** a save that arrives after the end while more was typed takes the late save; the rest gets 404. Likewise a save whose answer was lost leaves an old version, and a late save's 409 carries nothing to recover from. Open item.

**F10**

1. **New, confirmed, the root cause.** The editor's navigation guard (`MarkdownEditor.tsx`, `useBlocker`) blocked every navigation while text was unresolved — offline always — so `hideAgain`'s `navigate` was blocked, and `shouldBlock` even put the discreet path back as `currentPath` and `mode: "write"` after `forgetVault`. With only the guard letting the hide pass, the tab goes to `/notes/Home/a.md`.
2. **Confirmed, contributing:** between the mode's reset and the commit of the navigation (a transition) the shell rendered once more with the old route — header and title named the note — online too (step 2.9), seen with the screen watcher.
3. **Confirmed, contributing** (spec 1): the note's read, asked again after `removeQueries` or on reconnect, answers 404 with the path, rendered while the view stays.
4. **Ruled out** (spec 2): `vaultOfRoute(pathname)` named the discreet vault and `navigate` was called.
5. **Ruled out** (spec 3): `followSession` returns while `hiding`; one run.
6. **Ruled out as a cause** (spec 4): the view kept its text because its route never changed.
7. **Found by the reviews, fixed:** sheets and dialogs the shell and the docked sidebar keep in their own state (History, Move, Delete, Publish, folder, vault sheets; Rename/Delete/Leave vault; Share, members, connect, export, embeddings) outlived the end; other 404 texts (an embed, the history, a restore, a task's box) named the path.

## 5. The final gate

As CI, at `244b48a` (every section in), the server tests by namespace (section 1):

| Step | Result |
|---|---|
| `dotnet restore --locked-mode` | ok |
| `dotnet build --configuration Release --no-restore` | Build succeeded, 0 warnings |
| Core, Release | 2153 total, 0 failed, 3 skipped |
| Server, Release, by namespace | 3521 total, 0 failed, 7 skipped (Accounts 413, Api 697, Attachments 264, Auth 34, Dav 280, Discreet 143, Mcp 63, Metrics 45, Net 42, Notes 26/1 skipped, OAuth 185, Publishing 149, Search 92/6 skipped, Sharing 353, Skills 339, Startup 85, Vaults 291, Welcome 20; the root namespace holds none) |
| `pnpm -C web install --frozen-lockfile && pnpm -C web test --run` | 128 files, 2224 tests, all passing |
| `pnpm -C web build` | built |

After the whole-branch review's commit `dc33e22` (web, and a `using` in `SectionSearch.cs`): Release build 0 warnings; core 2153, 0 failed, 3 skipped; web 128 files, 2226 tests, all passing; build green. The server's code is unchanged since the baseline.

## 6. The tasks

Commits, in order (each pushed; the web gate `pnpm -C web test --run` and `pnpm -C web build` green after each):

| Commit | What |
|---|---|
| `7d058a8`, `f0858f9` | The plan, and the plan review folded in |
| `8a1a7c5` | Task 1, F9: `networkMode: "always"` for the note's save; `useAutosave` retries a failed save on `online` |
| `c090005` | Task 2, F10: the guard lets the hide pass; `gone`; the shell shows nothing of a gone vault; a discreet 404 without its path |
| `48c1ddc` | Review of Task 1: an `online` during a save that then fails; a gone note keeps "Not saved"; the offline tests always reset the browser |
| `7d40ac6` | Review of Task 2: the guard waits for the mode's reset (a vault hidden and shown again is in `gone` all along); past it the editor saves nothing more; refusal and shared banner name nothing |
| `7e35845` | Spec review of Task 2: the shell's sheets close with a hidden vault; `useErrorTextFor` for every 404 of a discreet note (embed, history, restore, task box) |
| `f482fc6` | Task 3, F7: `useNotePublication`; `mayPublish` from the list |
| `796fa62` | Review of the follow-ups: the docked sidebar's vault dialogs close too; a gone note's status after an `online` |
| `9914213` | Reviews of Task 3: a just withdrawn note isn't asked again |
| `244b48a` | Task 4, F8: the snippet's line (with the review's FormKC fix and test) |
| `dc33e22` | Whole-branch review: a hidden vault's note reached later says there is no such note; the active vault's sidebar dialogs close; one `goneVault` helper |

**Task 1 (F9), `8a1a7c5`.** Red, before any change (4 files): `Tests 5 failed | 100 passed (105)` — offlineEnd "the spec's example…" (received `[FIRST, "Liebes Tagebuch. Morgens. off0"]`); "online again before the end…" (received `[FIRST, "… off0", "… off0 off1 off2 off3 off4"]`, a stale save before the latest); useAutosave "a save that failed is tried again…" (`expected … to have a length of 2 but got 1`); MarkdownEditor "offline, a save fails at once, says Not saved…" (`Unable to find role="alert" and name "Not saved"`); NoteView "offline, a task's box ticked fails at once…" (`Unable to find role="alert"`). offlineEnd 3 and 4 and "online again saves nothing early" passed (guards). Green: `Tests 105 passed (105)`; full web `Test Files 126 passed (126)`, `Tests 2200 passed (2200)`; build `✓ built`.

**Task 2 (F10), `c090005`.** Red (3 files, Task 1 in place): `7 failed, 31 passed (38)` — F10 1, 5 and 9 (`expected '/notes/Tagebuch/Heute.md' to be '/notes/Home/a.md'`), F10 2 (the screen named the note after the end), F10 7 (`Unable to find … There is no such note …`), state.test 10 (`expected true to be false`); 3, 4, 6, 8 and the ordinary note's guard test passed (guards). Green: 38/38; full web `Test Files 127 passed (127)`, `Tests 2211 passed (2211)`; build green.

**Task 3 (F7), `f482fc6`.** Red: `5 failed, 72 passed` — the bar's "asks for no publication of its own for an unpublished note" and "… while the vault's list hasn't answered" (`expected +0 to be 1`: the list wasn't asked), "gives a member the bar of a note the shared vault's list names" (`expected +0 to be 1`: the second trap), the shell's "offers Publish… for an unpublished note … and asks no publication of the unpublished one" (`expected 2 to be +0`), "asks the vault's list again after publishing from the menu" (`expected 3 to be 1`). Green: 77/77; full web `Test Files 128 passed (128)`, `Tests 2222 passed (2222)`; build green. Two existing assertions changed (a member's bar now asks the shared vault's list): `NoteView.test.tsx`'s delete dialog test (`not.toContain(list)` → `toEqual([list, per-note GET])`, its claim that the dialog asks nothing more unchanged) and `TreeDrawerPublishing.test.tsx`'s member count (0 → 1 list request; no marks still asserted). The spec reviewer judged neither weakened.

**Task 4 (F8), `244b48a`.** Red (SearchTests): `2 of 63 failed` — `Snippet_prefers_the_line_with_the_whole_query` (Actual `| E228 | Pumpe 28 | Druck zu niedrig |`), `Snippet_prefers_the_line_with_the_most_terms` (Actual `Pumpe Pumpe Pumpe`). Green: SearchTests 63/0/0; core 2153 total, 0 failed, 3 skipped; server classes EarlierSearchApi, NoteApi, HistoryApi, RenamedOwner, VaultRename: 160 total, 0 failed.

**The review fixes** were each made test first; red and green below:
- `48c1ddc`: useAutosave "online again while a save is in flight, and that save then fails…" red (`expected [ 'start a' ] to deeply equal [ 'start a', 'start ab' ]`), then green; three files 78/78; full web `Test Files 127 passed (127)`, `Tests 2212 passed (2212)`; build green.
- `7d40ac6`: offlineEnd "✕ while offline…" red (`expected 6 to be 5`: the editor's unmount sent the held text again) and "hidden, shown again and hidden once more…" red (`expected '/notes/Home/a.md' to be '/notes/Tagebuch/Heute.md'`: the guard let the user's navigation pass before the hide had the text); green; full web `Test Files 127 passed (127)`, `Tests 2213 passed (2213)`; build green.
- `7e35845`: offlineEnd "a sheet open on the note at the end (its history) closes with it" red (`expect(element).not.toBeInTheDocument()`), errorText.test red (module missing); full web `Test Files 128 passed (128)`, `Tests 2216 passed (2216)`; build green.
- `796fa62`: "a dialog of the docked sidebar's open on the vault at the end (Rename vault) closes with it" red, useAutosave "a note gone while its save was in flight says Not saved…" red; full web `Test Files 128 passed (128)`, `Tests 2224 passed (2224)`; build green.
- `9914213`: PublicationBar "asks before unpublishing" red (`expected "spy" to not be called at all, but actually been called 1 times`: the withdrawn note's publication asked again, a 404); full web 2224 passed; build green.
- `dc33e22`: "the active vault's dialog of the docked sidebar (Share) closes at the end…" and "a note of the hidden vault reached again later (a bookmark) says there is no such note…" (each red under its mutation below); full web `Test Files 128 passed (128)`, `Tests 2226 passed (2226)`; build green; SearchTests 63/0.


## 7. Mutations

Each applied alone, run, and restored with `git checkout -- <file>` (or the saved copy); the tree was clean after each.

| Task | Mutation | Caught by |
|---|---|---|
| 1 | M1 no `networkMode: "always"` | offlineEnd example and "online again before the end", MarkdownEditor offline test, NoteView task box |
| 1 | M2 no `online` effect | offlineEnd "online again before the end", useAutosave "tried again when online", MarkdownEditor offline test |
| 1 | M3 the `online` effect without its `s.failed` check | useAutosave "online again saves nothing early" |
| 1 (review) | no `s.online = true` while in flight | useAutosave "online again while a save is in flight…" |
| 1 (review) | no `!s.stopped` in the completion | useAutosave "a note gone while its save was in flight…" |
| 2 | M1 the guard's bypass removed | F10 1, 5, 9 |
| 2 | M2 `TopBar path={path}`, `main` without `gone` | F10 2, 5, 9 |
| 2 | M3 `gone` not set in `hideAgain` | F10 1, 2, 5, 9 |
| 2 | M4 `useGoneWithVaults` without `!names.has` | F10 8 |
| 2 | M5 `errorText` again for the discreet 404 | F10 7 |
| 2 | M6 `setFromVaults` leaves `gone` alone | state.test 10 |
| 2 (review) | the guard without `stop()` | "✕ while offline" (one PUT more) |
| 2 (review) | the guard without `!names.has` | "hidden, shown again and hidden once more" |
| 2 (review) | the shell doesn't close the History sheet | "a sheet open on the note at the end…" |
| 2 (review) | `useErrorTextFor` without `gone` | errorText.test "shown or hidden again" |
| 2 (review) | the sidebar doesn't close Rename vault | "a dialog of the docked sidebar's … (Rename vault)" |
| 2 (final) | the sidebar doesn't close Share | "the active vault's dialog … (Share)" |
| 2 (final) | the gone place without its sentence | "a note of the hidden vault reached again later…" |
| 3 | M1 the per-note read asked regardless | the bar's two no-request tests, both new shell tests, the tree's "marks are unknown" |
| 3 | M2 `mayPublish` from `publication.data === null` again | both new shell tests, the tree's "marks are unknown" |
| 3 | M3 the per-note read while the list is undefined | as M1, with "while the vault's list hasn't answered" |
| 3 (review) | withdraw invalidates the note's publication again | "asks before unpublishing" |
| 4 | M1 the first line with any term | whole_query, most_terms |
| 4 | M2 terms counted with repetition | most_terms (Actual `Pumpe Pumpe Pumpe`) |
| 4 | M3 the last line among equals | first_line_among_equals, row_says_something_else |
| 4 (review) | the whole query compared without FormKC | whole_query (the decomposed line) |


## 8. Decisions made without Chris

1. **The root causes established before the plan, by experiment, not by subagents.** Options: let the plan's first task find them; find them first. Chose first: the brief puts the cause before any fix, and a plan that names the causes lets each task carry a precise red test. The experiments' code was reverted; only the tests went into Task 1.
2. **`networkMode: "always"` for every save of a note, not only the editor's.** Options: an option for the editor only; all. Chose all: a task box ticked offline would otherwise be replayed later with an old version, and after a reveal's end as the one late save. Visible change: a tick offline now says the server can't be reached at once (tested) instead of waiting.
3. **Retrying a failed save on `online`.** The spec asks "saved when the browser is online again"; the other choice was waiting for the next keystroke, `saveAhead` or the end. Chose the listener; nothing failed, nothing is sent early.
4. **The editor's guard yields to the hide only for a note of a vault being hidden, and only once the mode is reset; then the editor saves nothing more.** Options: yield while `hiding` at all (would let an ordinary note's or the user's own navigation pass before the text is held); yield and keep the unmount save (sends the held text twice). Chose the narrow form (rule 1 and "the user's text is sacred").
5. **`gone`: the vaults this tab hid again, in memory until the session ends, yielding to `names` and to a vault listed as ordinary.** Options: no memory (a view rendering once more after the reset names the note, seen online too); clearing it at the end of the hide (the transition's last render comes later). Chose the memory: never shown, never stored; it holds names the tab already had, as the held draft holds a path. Nothing changes for a user without discreet vaults (both sets stay empty).
6. **What the gone place shows.** Empty while the hide runs; afterwards (a bookmark of a hidden note) "There is no such note (if it was removed, it is in the trash)." Options: blank (no explanation), the note view (its 404 names the path). Chose the sentence.
7. **A discreet note's 404 without its path, everywhere, also while shown** ("There is no such note (if it was removed, it is in the trash).") The spec says "never renders … any other text with the path"; a revealed vault's genuinely missing note loses its path in the sentence (the title still shows it, as the user is looking at that vault).
8. **Sheets and dialogs of a hidden vault close at the hide** (the shell's and the docked sidebar's), and the sidebar's dialogs of the active vault too. Options: leave them (they name the vault; the export one would go on with the ordinary vault). Chose closing.
9. **F7: while the vault's list fails, no bar.** Options: fall back to the per-note read on a failed list (a 404 again when unpublished). Chose no bar, as the spec says "wait for it"; recorded.
10. **F7: a withdrawn note's publication is set to none instead of asked again.**
11. **F8: equal weight per distinct term, as the spec says**, though the quality review suggested weighting rare terms (which would pick row 51 in the Dichtung case). Recorded for Chris. The reviewer's "stop at a line with every term" I didn't take: a later line with the whole query must still win. The FormKC comparison I took (a note written decomposed).
12. **✕ while offline left as it is** (brief point 6); described in section 8 and recorded.
13. **Server tests by namespace** instead of `dotnet test --solution` in one go (the file limit); every namespace ran.


## 9. Deviations, surprises, open points

- **Surprise: F10's cause was not among the spec's candidates.** The editor's own navigation guard blocked the hide.
- **Surprise: the online case (step 2.9) also showed the note once more after the reset** (a transition's render), never noticed because it is brief.
- **Changed existing tests:** two assertions about a member's requests (Task 3, judged not weakened); `hideAgain.test.tsx` resets `gone` in its `afterEach`, and a comment there was reworded.
- **Not done, recorded in open items:** the late-save cases a client can't fix; a note opened offline is never told to the server; ✕ offline; the header during a slow hide; F8's weighting and long lines; F7's failing list and the folder mark's re-reads.
- **✕ while offline, what happens today** (brief point 6, unchanged): before this branch the hide hung on the paused save with the note on screen. Now ✕ holds the draft (its save fails at once), the hide's POST fails (`unsent`), the tab goes to the ordinary note and names nothing. Back online, the held draft's retry and the session read both start on the `online` event; the hide is sent again only after the session read answers. So the held PUT usually reaches the server first and, the reveal still running there, is saved as an ordinary save; if the hide gets there first the marker is gone and the draft gets 404.
- **Text typed in the moment between the hide's save and its navigation** (the ✕'s round trip) isn't kept; after ✕ the server would refuse it anyway.
- **Open points for Chris:** the late-save rule taking one save (whether a later save within the window should win); ✕ offline; F8's weighting.


## 10. Hand-test checklist

On staging with a pre-release of this branch; Chromium with DevTools (Network: Offline; Console open). Alice with a PIN and a discreet vault `Tagebuch` holding `Heute.md`, and an ordinary vault `Home` with a note.

1. **The spec's example (2.9 offline).** Reveal for 5 minutes; open `/Tagebuch/Heute.md`, Write; type a word. *Pass:* saved (the history has a new version).
2. Two minutes before the end set Network to Offline; type a word every few seconds until past the end. *Look at* the editor while typing: "Not saved — retry" may show. *Pass:* the text stays.
3. The end passes (the pill runs out). *Pass:* the note closes, the app shows the last ordinary note (or the vault overview), and nothing on screen — header, title, sidebar, tab title, any toast — says `Tagebuch` or `Heute`. No dialog or sheet is left open.
4. Three minutes after the end set Network back to Online. *Pass:* within a few seconds one `PUT /api/notes/Tagebuch/Heute.md` answers 200 (Network tab); nothing on screen names the note.
5. Reveal again and open `Heute.md`: *Pass:* everything up to the last word typed in the tunnel is there; its history has exactly one new version for the tunnel text.
6. **Offline past the window.** As 1–3, but stay offline until 6 minutes after the end, then online. *Pass:* no successful PUT of the note; the tunnel text is not in `Heute.md`; the screen named nothing throughout.
7. **Online again before the end.** Offline 2 minutes before the end, type, online 1 minute before the end, stop typing. *Pass:* one PUT right after going online with the latest text; at the end the note closes as in 3.
8. **Step 2.9 online.** Type until the end online. *Pass:* the note closes, the last ordinary note is shown, the text saved once, nothing names the note even for a moment (watch the header as the pill runs out).
9. **✕ while offline** with text unsaved. *Pass:* the tab goes to the ordinary note and names nothing. Then online: *note* whether the text is saved (today it usually is; see the report, open item).
10. **A sheet at the end.** In Read mode on `Heute.md`, open Note actions → History; let the time run out. *Pass:* the sheet closes with the note. On a desktop-wide window, open the sidebar's vault menu → Rename vault… (or Share…) on `Tagebuch` and let the time run out: *Pass:* the dialog closes.
11. **A bookmark after the hide.** After a hide, open `/notes/Tagebuch/Heute.md` by URL (it shows `/discreet` or a pasted link). *Pass:* "There is no such note (if it was removed, it is in the trash)." and no path in it.
12. **A task box offline.** In an ordinary note with `- [ ]`, go offline and tick it. *Pass:* a notice that the server can't be reached at once; online again, nothing is sent by itself.
13. **F7: publications.** As the owner, open an unpublished note with the Console and Network open. *Pass:* no `GET /api/publications/…` for it and no 404 in the console. Open a published note: *Pass:* the bar (green or yellow), Copy link works, Update copy when older, Unpublish. Unpublish it: *Pass:* the bar goes, no 404. Publish from the note's menu: *Pass:* the bar shows. As a member of a shared vault, open a published note: *Pass:* "Published by the vault's owner"; an unpublished one: no bar, no 404. With publishing switched off on the server: *Pass:* the published note's bar still offers Unpublish.
14. **F8: the 60-row table.** A note with a table of 60 rows like `| E2nn | Pumpe nn | Druck zu niedrig |`; search "Pumpe 51 Druck". *Pass:* the hit's snippet is the row `| E251 | Pumpe 51 | Druck zu niedrig |`, not the section's first row. Change row 51 to "Dichtung undicht" and search again: *Pass:* the snippet is the first row of that hit's section holding both "Pumpe" and "Druck" (the tie the spec pins; see open items). The earlier-versions search shows the same lines.


## 11. The pull request

[https://github.com/SCAI-NGO/kumbukumbu/pull/184](https://github.com/SCAI-NGO/kumbukumbu/pull/184) — and a row in `docs/superpowers/open-items.md` linking it.
