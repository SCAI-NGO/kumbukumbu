# Overnighter report: the findings of the pre.3 tests

Pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/182 (draft, against `master`; not merged, tagged or deployed; version unchanged). Branch `fix/pre3-findings`, on `master` at `6a7a4c7`. Spec `docs/superpowers/specs/2026-11-09-pre3-findings-design.md` (unchanged), plan `docs/superpowers/plans/2026-11-09-pre3-findings.md`.

All six sections are done: §4 F3, §2 F1, §5 F4, §3 F2, §7 F6, §6 F5. None are left. The security boundary is not touched: the only server-side file changed is a Welcome note's text (and its test).

## 1. Environment

- **Docker.** The setup script's `dockerd` wasn't running (`Cannot connect`, a stale `/var/run/docker.pid`). I removed the pid file and started `dockerd` by hand; it came up at once.
- **Node and pnpm.** The image had Node 22 and pnpm 10.28.0.
  - I installed Node 26.13.0 from nodejs.org into `/opt/node26`, after checking it against the published SHA-256.
  - Then `npm install -g pnpm@10.32.1`.
- **.NET.** SDK 10.0.403 was already there (`global.json` asks for 10.0.100, roll forward). `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **MongoDB for the server tests.** I used my own container `kumbu-test-mongo`, `mongo:7.0` as a replica set, with `--ulimit nofile=20000:20000`, as the brief says. Testcontainers' Ryuk was disabled.
  - **Surprise:** with the limit at 20000, `mongod` still ran out of file handles about 8 minutes into the first baseline run (WiredTiger `EMFILE`, then `fassert`, and the container exited). The 20000 is the daemon's own hard limit, and the session can't raise it (`ulimit -Hn` is refused, and runc refuses a higher `--ulimit`).
  - **What worked:** `--wiredTigerEngineConfigString "file_manager=(close_idle_time=5,close_scan_interval=5,close_handle_minimum=200)"`, which makes WiredTiger close idle handles quickly. The open handles then stayed at about 200 to 6000, and both full runs passed.
  - The aborted first run is not counted. Its 5 failures (`AttachmentStoreTests`, `EmbeddingApiTests`) were all connection errors from the crash.
- **Superpowers skills.** They were listed, but this session followed the brief's process with the plain Agent tool. There was a fresh subagent per task (red, green, commit, mutations) and a fresh read-only reviewer per task.
  - **Deviation:** each reviewer did the spec-compliance review and the code-quality review together, not as two separate agents. Tasks 4 and 5 (texts only) shared one reviewer.
  - I did Task 5 (a README sentence and one comment) myself instead of through a subagent; a fresh reviewer then reviewed it.

## 2. The plan review

A fresh subagent reviewed the plan against the spec, the brief and the code. It had 14 findings: 4 must-fix, 7 should-fix, 3 nits. All were folded in before the plan was committed (`fb8c4ba`):

- **must-fix:**
  - `PinDialog.test.tsx` renders without a router, so a `<Link>` would break every test in it: wrap `renderDialog` in a `MemoryRouter`.
  - Decide on `onSettings` and test that the phone drawer closes too.
  - Task 6 named the wrong harness (`nameFromHeading.test.ts` holds pure functions only). The right ones are `MarkdownEditor.test.tsx`'s `gardenApi` and `MoveDialog.test.tsx`'s `api()`.
  - Check for **any** request to the old path, not only note and publication GETs.
- **should-fix:**
  - The cause's wording on retries: a publication 404 maps to `null` and isn't retried.
  - An undefined variable in Task 3's snippet (now `next`).
  - Make the pending test concrete (`MenuItem.disabled` exists).
  - There was no scroll test to copy: stub `scrollIntoView`.
  - The Welcome label must be bold, as the notes write it.
  - Replace placeholder mutations in Task 4.
  - Task 1's setup (`activeVault` isn't persisted; render at `/`).
- **nits:**
  - Line anchors.
  - Confirmation that the anti-goals hold.
  - Log the README "Marking." edit as a decision.

## 3. Task 0: planned vs. measured

| | Planned (brief, CI on 2026-11-09) | Measured here, before any change (`6a7a4c7` + plan) |
|---|---|---|
| Server | 5524 tests, all passing, 4 skipped, about 14 min | **5669 tests: 5605 passed, 0 failed, 64 skipped**, 16 min 29 s |
| Web | 120 files, about 2170 tests, all passing | **124 files, 2171 tests, all passing** |
| Build | green | green |

Of the 64 skips:

- 54 need `KUMBU_TOKENIZER` (arctic-m's `tokenizer.json`).
- 4 need `KUMBU_MODEL_DIR` with the model, and 3 more need the arctic-l or arctic-m tokenizer.
- 1 needs `KUMBU_TEST_TENSORX_KEY_FILE`.
- 2 skip because the run is as root.

CI provides the tokenizer, which explains the difference in the skip count. The difference in the total (5669 vs 5524) I can't explain from here: it may be how CI's four namespace jobs count. Worth a look at the next CI run of this branch.

- No flaky class showed up (no `AccountApiTests`, `RekeyTransitionTests`, `PairingTests` or sync-hub failures).
- Task 0 took about 35 minutes instead of 15: the Docker daemon, Node 26, and the MongoDB crash with its rerun.

## 4. The tasks

Each task got a red test first, then green, a gate (`pnpm -C web test --run` and `pnpm -C web build`), a commit and a push. Every mutation was applied alone, run, and restored with `git checkout -- <file>`, and the file was green again after each.

### Task 1, §4 F3: `7aabb50` "Start a new user in Welcome, not in Skills"

- **Change:** `web/src/shell/MobileShell.tsx`. When no vault is active, the effect picks the first vault with `kind !== "skills"`, else the first vault.
- **Tests:** three in `MobileShell.test.tsx`, "the first vault":
  - "is the first vault that isn't the Skills vault, though Skills sorts first"
  - "is the Skills vault when there is no other"
  - "leaves a vault the user chose"
- **Red:** 1 failed, 58 passed (`expected 'Skills' to be 'Welcome'`).
- **Green:** the file 59/59; web 124 files, 2174 tests; build green.
- **Mutations:**
  - (a) back to `vaults.data[0]`: the Welcome test fails.
  - (b) no fallback: the Skills-alone test fails.
  - (c) without `!activeVault &&`: the chosen-vault test fails, with 19 others in the file (20 of 59).
- **Review:** no findings. One wording nit on the comment, left as it is.

### Task 2, §2 F1: `9978747` "Point More vaults… to where the PIN is set", and `d361689` (review follow-up)

- **Changes:**
  - `PinDialog.tsx`: the fixed line "The PIN is set in Settings → Discreet vaults.", directly under "4 to 6 digits" and above the error, linking to `/settings#discreet-vaults`. The link calls the new optional `onSettings`, else `onClose`.
  - `TreeDrawer.tsx`: `onSettings` closes the dialog and the drawer (`setOverlay("none")`).
  - `Settings.tsx`: the section has `id="discreet-vaults"` and the hash-scroll effect copied from `InstallSection`.
- **Tests:**
  - `PinDialog.test.tsx` (`renderDialog` wrapped in a `MemoryRouter`; the 13 existing tests stayed green):
    - "says where the PIN is set, with a link to that section"
    - "keeps that line, unchanged, after a wrong PIN: below the digits, above the server's sentence". The line's `outerHTML` is identical before and after the 400.
    - "the link closes the dialog, or does what the caller asks instead"
  - `Settings.test.tsx`: "has an id to link to" and "is scrolled to when a link leads there".
  - `MobileShell.test.tsx`: "leads to where the PIN is set, leaving neither the dialog nor the drawer open".
- **Red:** all 6 new tests failed.
- **Green:** PinDialog 16/16, Settings 45/45, MobileShell 60/60; web 124 files, 2180 tests; build green.
- **Mutations:**
  - (a) remove the line: all 3 new PinDialog tests fail.
  - (b) `to="/settings"` without the hash: "says where the PIN is set…" fails.
  - (c) drop the `onClick`: "the link closes the dialog…" fails.
  - (d) move the error above the line: "keeps that line, unchanged…" fails.
  - (e) drop the section's `id`: "has an id to link to" fails.
  - (f) drop the scroll effect: "is scrolled to…" fails.
  - (g) drop `setOverlay("none")`: this one **survived at first**, because `/settings` lies outside the shell and the drawer unmounts anyway. The implementer added `expect(useEditor.getState().overlay).toBe("none")` and amended the commit before it was pushed; then (g) fails "leads to where the PIN is set…".
- **Review:** no must-fix or should-fix findings.
  - Fixed in `d361689`: the link's classes now match the app's other in-text links (`font-medium text-accent-text underline underline-offset-2`, as in `AgentBanner.tsx`), and the docblock mentions the line and `onSettings`. PinDialog 16/16 after the fix.
  - Left: a ctrl-click also closes the dialog (harmless, as the account row's links do), and a `cleanup()` in the middle of a test.

### Task 3, §5 F4: `31b8b01` "Offer Discreet and Not discreet in the vault menu while they are shown"

- **Changes:**
  - `TreeDrawer.tsx`: a new entry only when `revealed && active && active.kind !== "skills"`, just before "Delete vault…" / "Leave this vault…".
    - Label: **Not discreet** with the `Eye` icon for a marked vault, **Discreet** with `EyeOff` for an unmarked one.
    - It calls `useMarkDiscreet()` (the Settings switch's call), is disabled while pending, and says "Marked as discreet" / "No longer discreet" on success.
    - A `FormError` block shows a failure.
  - `useCopied.tsx`: a general `say(text)`; `copy` now uses it with its own two texts.
  - `README.md`: one sentence in "Marking.".
- **Tests:** a new file `TreeDrawerDiscreet.test.tsx` with 8 tests.
  - Two guards, not revealed, for a user without discreet vaults and one with them. Each compares the whole menu literally: New note here, New folder…, Upload here, Trash, Activity, Checkpoints, Create board, Gallery, Share…, Rename vault…, Connect Obsidian…, Export as ZIP…, Delete vault….
  - Discreet before Delete vault…, with its PUT and confirmation.
  - Not discreet, with its PUT and confirmation.
  - A shared vault, before Leave this vault….
  - Not for Skills.
  - A failure.
  - Disabled while pending.
- **Red:** 5 failed, 3 passed (the two guards and the Skills test pass before the change, as they should).
- **Green:** the new file plus `useCopied` and `TreeDrawerAttachments`, both unchanged: 19/19. Web 125 files, 2188 tests; build green.
- **Mutations:**
  - (a) without `revealed &&`: both guards fail.
  - (b) without the Skills condition: the Skills test fails.
  - (c) always `discreet: true`: the Not discreet test fails.
  - (d) without `say`: both confirmation tests and the pending test fail.
  - (e) without the `FormError`: the failure test fails.
  - (f) without `disabled`: the pending test fails.
- **Surprise:** while the discreet vaults are hidden, `useVaults` drops any vault marked discreet (`withoutDiscreet`, `api/notes.ts:137`). The "with discreet vaults" guard therefore lists a discreet `Tagebuch` beside `Work`.
- **Review:** no must-fix or should-fix findings. Four nits, left as they are:
  - The confirmation is lost if the drawer closes before the PUT returns. Settings shows none either.
  - The error stays until the next mark, as the drawer's other error blocks do.
  - One `waitFor` in the hidden case guards nothing.
  - The tests find the status line as the drawer's only live region.

### Task 4, §3 F2: `467598a` "Explain discreet vaults in the Welcome notes", and part of `abc8f5b` (review follow-up)

- **Change:** `Security and privacy.md` gets a `## Discreet vaults` section between "Keys and connections" and "The honest limits". Its text after the review:

  > A discreet vault isn't there in the web app until a PIN shows it: for when Kumbu² is open at a family table, at work or on the train, and even a vault's name would say too much.
  >
  > 1. Set a PIN in Settings → **Discreet vaults**: 4 to 6 digits, separate from your password.
  > 2. Open **More vaults…**, the last entry of the vault menu, and enter the PIN, with how long the vaults stay shown. An amber frame and a **Discreet** pill show that discreet mode is on; ✕ on the pill hides them again.
  > 3. While the amber frame is shown, mark a vault: the vault's ⋯ menu → **Discreet** (or **Not discreet**), or its switch in Settings.
  >
  > The PIN hides vaults in the web app; it doesn't encrypt them. Agents, synced devices, WebDAV, keys and connections see them as before.

- **Test:** `WelcomeTextTests.The_menu_names_quoted_are_the_shared_labels` now also asks for `"More vaults…"` and `"Settings → **Discreet vaults**"`.
- **Red:** 1 of 10 failed (`Not found: "More vaults…"`).
- **Green:** `WelcomeTextTests` 10/10, `WelcomeVaultTests` 10/10; web 125 files, 2188 tests; build green. After the review's wording changes, `WelcomeTextTests` was still 10/10.
- **Mutations:**
  - (a) "More vaults…" removed: the labels test fails.
  - (b) "Settings → Discreet vaults" without bold: the labels test fails.
  - (c) a vendor name in the text: `The_texts_are_ai_agnostic_outside_the_client_sections` fails.
  - (d) "Back to [[Start here]]" removed: `Every_text_starts_with_its_title_and_links_back_to_start_here` fails.
- **Review:** every statement was checked against the app and found true. Four wording nits were taken in `abc8f5b`:
  - a full opening sentence;
  - "show that discreet mode is on";
  - "hides vaults in the web app";
  - and, in the README, "A tab showing".

### Task 5, §7 F6: `1d450c9` "Say in the README where hiding the discreet vaults leads", and part of `abc8f5b`

- **Changes:**
  - `README.md` "Hiding again": "and the app returns to the last ordinary vault" becomes: "A tab showing a discreet note or view goes back to the last ordinary note it showed, or to the overview of the vaults if it showed none; an ordinary note on screen stays."
  - The doc comment of `hideAgain` (`hideAgain.ts:130-135`) said "goes back to the last ordinary vault" and was corrected.
- **Tests:** none, as spec §8 says. `hideAgain.test.tsx` 23/23; build green.
- **Mutations:** none (no code).
- **Review:** the sentence matches `hideAgain.ts:162-182` and the tests at `:166` and `:189`. The rewrapped comment line was too long; fixed in `abc8f5b`.

### Task 6, §6 F5: `e110f98` "Ask nothing more for a note's old name once it is renamed", and `1f02062` (review follow-up)

- **Cause, confirmed by the red tests and shared by both renames:**
  - `moveNote` removed the `note`, `links`, `backlinks` and `history` queries for `from`, then invalidated every `note` and `publication` query.
  - The navigation comes only afterwards (`moved()` plus `follow`, or `MoveDialog`'s `onSuccess`), and until then the old path's views (`NoteView`, `PublicationBar`'s `usePublication`, the version poll) are still mounted.
  - A query removed under a mounted observer is built again and fetched, and an active one that is invalidated is refetched.
  - `MoveDialog` was not quiet either.
- **Requests for the old path in red:**
  - From the heading: `GET /api/publications/Home/Garden/Untitled.md` and `GET /api/notes/Home/Garden/Untitled.md`.
  - Through `MoveDialog`: `GET /api/publications/Work/inbox.md` and `GET /api/notes/Work/inbox.md`.
  - The reviewer, running the MoveDialog test against the old `notes.ts`, also saw the backlinks, links and version GETs.
- **Fix** (`web/src/api/notes.ts`, `moveNote` only):
  - The old path's `note`, `links`, `backlinks`, `history`, `version` and `publication` queries are cancelled and left out of the invalidation. `version` was added after the review, in `1f02062`.
  - A new `dropWhenUnwatched` removes them at once if nothing watches them. Otherwise it removes each one when its last observer goes, through a query-cache listener that stops once all are gone.
  - So a later note at the old path still can't show the old text.
- **Tests:**
  - `MarkdownEditor.test.tsx`: "asks nothing more for the old name once the note is named".
  - `MoveDialog.test.tsx`: "asks nothing more for the open note's old name once it is renamed".
  - Both record every request and assert that, after the `POST /api/move`, none of any method names the old path, and that the new path's GET was sent. After unmounting, `["note", from]` is no longer cached.
- **Red:** both failed with the requests above.
- **Green:** the two files plus `nameFromHeading.test.ts` and `NoteView.test.tsx`: 4 files, 134 tests. Web 125 files, 2190 tests; build green. The same again after `1f02062`.
- **Mutations:**
  - (a) remove the old keys at once again: both tests fail, with the note and publication GETs.
  - (b) invalidate `publication` without the predicate: both fail, with the publication GET.
  - (c) never drop the old keys: both fail on the cache check, and so does the existing "opens a second new note and stays on it", which shows the old text.
- **Review:** the cause and the fix are right for TanStack Query 5.105: `cancelQueries` reverts without refetching, the `observerRemoved` and `removed` events behave as the fix expects, and other notes' queries are still invalidated.
  - Should-fix: `version` was missing. Fixed in `1f02062`; it isn't caught by a test, because the poll's 5 s interval lies outside the tests' window.
  - Nits: see §6.

## 5. Final gate (as CI)

- `dotnet restore --locked-mode`: ok.
- `dotnet build --configuration Release --no-restore`: 0 warnings, 0 errors.
- `dotnet test --configuration Release --no-build`, at `e110f98` (no server file changed after it), with a fresh `kumbu-test-mongo`: **5669 tests, 5605 passed, 0 failed, 64 skipped**, 16 min 11 s.
- `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`, at `1f02062`: **125 files, 2190 tests, all passing; build green**.

## 6. Decisions made without Chris

The same as in the pull request description:

1. **F1, the link closes the drawer too.** I saw two options: close only the dialog, or close the drawer as well. On a phone the drawer is itself a modal, so the link closes both, through an optional `onSettings` that falls back to `onClose`. This follows the account row's Settings link.
2. **F1, the link style.** The plan pointed at `InstallHint.tsx`, which has no in-text link. The link takes the app's other in-text links' classes (`AgentBanner.tsx`).
3. **F3, the scope.** The change also affects any session at `/` without an active vault whose first vault is Skills. That is within the spec, and a chosen vault stays chosen. `hideAgain.ts:166`'s fallback can also pick Skills first; I left it, because "nothing else changes".
4. **F4, the confirmation.** I chose a general `say(text)` in `useCopied` over a sibling component: one live region in the drawer, with the same look and timing.
5. **F4, the icons.** `EyeOff` for **Discreet** and `Eye` for **Not discreet**: each shows what selecting does.
6. **F4, the README "Marking." bullet** gained one sentence. That is beyond the spec's letter, but it keeps the README true.
7. **F2, placement and wording.**
   - The section is in "Security and privacy", where a new user looks for privacy. "Working by hand" names the vault menu only in passing.
   - "connections" was added to the list of who still sees discreet vaults, as the README and Settings say.
   - The single answer and the rate limit were left out to keep it short.
   - The Settings path is in bold, as the notes write the others.
8. **F5, when the old keys go.** I weighed three options: remove at once (the bug); defer by `setTimeout(0)`, which is fragile because the navigation runs under `v7_startTransition`; or remove once the last observer goes. A shorter `gcTime` doesn't work, because TanStack Query keeps the largest `gcTime` it has seen. I chose the last.
9. **F6, the comment.** `hideAgain`'s doc comment said otherwise than the code and was corrected, as the spec allows.
10. **A draft pull request**, as the earlier overnighter runs', so nothing is merged by accident.

## 7. Deviations, surprises, open points for Chris

- **Deviations:**
  - Spec compliance and code quality were reviewed by one fresh subagent per task rather than two, and Tasks 4 and 5 shared one.
  - I did Task 5 myself.
  - There are three follow-up commits from the reviews (`d361689`, `abc8f5b`, `1f02062`) beside the six section commits.
- **Surprises:**
  - The MongoDB file-handle crash and its workaround (§1).
  - The server's test total: 5669 here against 5524 in the brief (§3).
- **Open points** (rows in `docs/superpowers/open-items.md`):
  - **F5, a leftover.** If the caller never moves on after a rename, the listener for the old keys stays until those views unmount. That happens when the heading rename's `timed` gives up after the server has moved the note, or when no `follow` is set. Meanwhile the old view shows its cached text instead of the earlier 404. This is small and rare.
  - **A flaky test start.** Run alone with `-t`, two heading-rename tests in `MarkdownEditor.test.tsx` lose the first typed character: the new one, and the existing "names it when leaving the notes for Settings". In the full file they pass. The cause is in the shared `writeIn` helper: it should wait for the editor's `# ` before typing.
  - **A test title.** `hideAgain.test.tsx:189` is titled "shows the vault Work" but asserts `/` and "Your vaults". Spec §7 keeps the tests as they are.
  - **The hide fallback.** `hideAgain.ts:166` can pick Skills as the active vault after hiding, the same pattern F3 fixed for the start.

## 8. Hand-test checklist

Use staging with `v0.4.0-pre.3`'s successor built from this branch. The Welcome texts reach only accounts made after the update, so steps 1 and 4 need a **fresh user**.

1. **F3, the start vault.**
   - Do: create a new user (Dana) and sign in on a phone and on a laptop.
   - Look: the vault name in the sidebar or drawer header.
   - Passes: it reads `Welcome`, not `Skills`. Switch to Skills, reload: Skills stays chosen.
2. **F1, the PIN dialog line.**
   - Do: as Dana, with no PIN yet, open the vault menu → **More vaults…**.
   - Look: under the PIN field, below "4 to 6 digits".
   - Passes: "The PIN is set in Settings → Discreet vaults.", with the link. Enter `1234`, then Open: "You have no discreet vaults." appears **below** the line, and the line hasn't moved. As a user who has a PIN and discreet vaults, the same dialog looks identical.
3. **F1, the link.**
   - Do: in that dialog, tap **Settings → Discreet vaults**, on a phone (drawer open) and on a laptop.
   - Look: the page and the scroll position.
   - Passes: Settings opens scrolled to "Discreet vaults". Neither the dialog nor the drawer is left open, and Back returns to the notes.
4. **F2, the Welcome note.**
   - Do: as Dana, open `Welcome` → "Security and privacy".
   - Look: the section "Discreet vaults".
   - Passes: it is there, with the three steps and the sentence on agents and encryption, and it reads well in Read mode on a phone.
5. **F4, the menu outside discreet mode.**
   - Do: as a user with discreet vaults (hidden) and as one without, open a vault's ⋯ menu in the sidebar.
   - Look: the entries.
   - Passes: no "Discreet" or "Not discreet" entry, and the two menus are alike entry for entry.
6. **F4, marking from the menu.**
   - Do: enter discreet mode (More vaults…, the PIN, 10 minutes) and open the ⋯ menu of an ordinary vault `Tagebuch`.
   - Look: just above "Delete vault…".
   - Passes: **Discreet** (crossed-out eye). Choose it: "Marked as discreet" shows under the vault header for a few seconds, and `Tagebuch` stays shown, its menu now offering **Not discreet**. Settings → Discreet vaults shows its switch on.
7. **F4, the exceptions.**
   - Do: in discreet mode, open the ⋯ menu of `Skills`, and of a vault shared with you.
   - Passes: Skills has no entry. The shared vault has one, just above "Leave this vault…", and marking it changes nothing for its owner.
8. **F4, unmarking.**
   - Do: in discreet mode, on a marked vault, choose **Not discreet**.
   - Passes: "No longer discreet", and the vault stays after ✕.
9. **F6, where hiding leads.**
   - Do (a): in discreet mode, read an ordinary note, then open a discreet note and tap ✕.
   - Passes (a): the tab shows that ordinary note.
   - Do (b): sign in afresh, enter discreet mode before reading any ordinary note, open a discreet note, tap ✕.
   - Passes (b): "Your vaults".
   - Do (c): with an ordinary note on screen, tap ✕.
   - Passes (c): it stays. The README's "Hiding again" says the same.
10. **F5, the rename requests.**
    - Do: with the browser's network panel open (filter `Untitled`), make a new note, write `# Groceries`, switch to Read.
    - Look: the requests after `POST /api/move`.
    - Passes: the note is named `Groceries.md`, and no `GET /api/notes/…/Untitled.md` or `GET /api/publications/…/Untitled.md` follows the move. Then rename an open note through ⋯ → **Rename or move…**: no request for its old name follows.
    - Make a new note again: it is a blank `Untitled.md`, not the old text.
11. **The spec's example as a whole.**
    - Do:
      1. As a fresh Dana, land in `Welcome`.
      2. Read "Discreet vaults" in the Welcome notes.
      3. Open More vaults… and see the line with its link.
      4. Follow the link and set `4711`.
      5. Come back, open More vaults…, enter `4711` and pick 10 minutes.
      6. In the ⋯ menu of `Tagebuch`, choose **Discreet**.
      7. Tap ✕.
      8. Reveal again and open the same menu.
    - Passes: at step 5 the sidebar has its amber frame. At step 7 `Tagebuch` is gone. At step 8 the menu says **Not discreet**.

## 9. The pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/182, with a row in `docs/superpowers/open-items.md`. The session is subscribed to its activity.
