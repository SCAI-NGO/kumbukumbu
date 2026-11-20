# Report: fixes from the hand tests of 0.6.0-pre.5

Unattended run on `feature/fixes-0-6-0-pre-5`, from the brief `docs/superpowers/overnighter/2026-11-20-fixes-0-6-0-pre-5-brief.md` and the approved spec `docs/superpowers/specs/2026-11-20-fixes-0-6-0-pre-5-design.md`; plan `docs/superpowers/plans/2026-11-20-fixes-0-6-0-pre-5.md`. Pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/266 (not merged).

All nine items of spec §2 are fixed and proven. Nothing was dropped.

## Environment

- Claude Code cloud session, Linux. Node 26.13.1 from `/opt/node26`. pnpm upgraded from 10.28.0 to 10.32.1 (`npm install -g pnpm@10.32.1`), the only thing installed. .NET SDK 10.0.404 (`global.json` rolls forward from 10.0.100), Docker 29.10.0 (`dockerd` started by hand).
- No MongoDB container: no server test class ran, because no task touched the server.
- Superpowers skills were available. The work ran subagent-driven (`superpowers:subagent-driven-development`): a fresh implementer and a fresh reviewer per task, a plan review before the work, and a final whole-branch review. Implementers and reviewers ran on Opus for F-A and F-C and on Sonnet for the small items. The plan review and the final review ran on Opus.
- **The Factory board:** the `Kumbu_Factory` connector isn't available in this session, so there is no card. Progress is in this report and the pull request.
- **Heavy lock:** the server build left a `VBCSCompiler` (Roslyn's compiler server) running, and it had inherited the `/tmp/heavy.lock` descriptor from `flock`. The first end-to-end start waited on it for two minutes until `dotnet build-server shutdown` ended it. Next time, run `dotnet build-server shutdown` after a build, or build with `-p:UseSharedCompilation=false`.
- **The end-to-end suite writes screenshots into the last test run's folder** (`docs/superpowers/overnighter/2026-11-19-tests-0-6-0/*.png`, through `shot()`). They were restored with `git checkout`, not committed.

## The plan review

A fresh Opus subagent got the brief, the spec and the plan, not my conclusions. It confirmed F-A's cause with a scratch Vitest run outside the repository: a fresh load of `/n/<Work's id>/HT/Alpha.md` ended with Welcome, and in-app navigation already ended with Work. It found 0 critical, 4 important and 17 minor issues. All were folded into the plan before it was committed (`b41eb71`).

- **Important:**
  1. In jsdom the tree is the phone's drawer, mounted only while it is open, so F-A's unit test opens it first. It also asserts the picker (`Switch vault: Work`) and uses a fake API with distinct vault ids.
  2. A push after every task, and the board: added; the board isn't reachable (see above).
  3. The 200 ms test lives in `web/src/shell/split.test.tsx` and uses fake timers, so the 199/200 ms boundary isn't flaky.
  4. F-C's `hashchange` listener has a cleanup and no once-guard, and a StrictMode case was added.
- **Minor, taken over:**
  - The in-app regression case of F-A is marked as one that passes before the fix.
  - F-A's end-to-end test asserts that the default vault isn't the link's vault, and asserts both picker and tree.
  - F-C has no element-id filter (it guards a case that can't occur), and gains an empty-hash case and a "Loading …" case.
  - The commit order around the mutations was corrected.
  - The amendment's wording on line 93 was settled.
  - The expected `compose config` output was corrected: `pdf` already restarts.
  - The report notes spec §2.5 asks for were added.
  - The bundle decision for the publication bar is logged.
  - Download and failed-copy cases for the recovery key were added.
  - Heavy-lock and staging lines were added.
  - The diagram check was put right.

## Task 0: planned against measured

| | Planned | Measured |
|---|---|---|
| Time | about 15 minutes | 3 minutes 10 seconds (21:28:55 to 21:32:05 UTC) |
| `pnpm -C web build` | green | green |
| `pnpm -C web test --run` | all pass | 206 files, 4150 tests passed, 0 failed, 0 skipped |
| Server classes | only those a fix touches | none: no fix touches the server |

The known flaky `commentRanges.test.tsx` passed, so it needed no reruns.

## The items

### §2.1 F-A: a deep link leaves the tree on the wrong vault

Commits:

- `bf12139` Keep the deep link's vault in the tree and the picker (`web/src/shell/MobileShell.tsx`, `web/src/routes/DeepLink.test.tsx`)
- `07ed255` Test that a deep link switches the tree and the picker to its vault (`web/e2e/deeplinks/deeplinks.spec.ts`)

**F-A's cause.** "The current vault" is `activeVault` in the editor store. Three things set it:

- `openNote` sets it to the opened note's vault (`web/src/state/editor.ts:301`).
- The vault picker sets it (`VaultSwitcher.tsx:66`).
- The shell's default-vault effect sets it (`web/src/shell/MobileShell.tsx:374-377`).

`settleStart` and the last-note state (`shell/lastNote.ts`) play no part when the address names a note, because `kept` needs `!path` (`MobileShell.tsx:221`).

`DeepLink.tsx` loads the vault list (`useVaults`) to resolve the link, then `<Navigate>`s to `/notes/Work/HT/Alpha.md`. The shell then mounts with the vault list already cached, and two effects run in the same commit:

1. The effect at `MobileShell.tsx:340-347` opens the URL's note, and the store says Work.
2. The default-vault effect then runs with the `activeVault` it captured at render, still `null`, and sets the first vault that isn't Skills: Welcome.

A fresh load of `/notes/Work/…` didn't show this: the list wasn't cached at the shell's first render, and by the time it came, the store already said Work. The phone uses the same shell. After sign-in the link goes through `DeepLink` too (`SignIn.tsx:55`). So one cause covers all three of the spec's situations.

**The fix**, in the shell, the one place that owns the default: the effect reads `useEditor.getState().activeVault`, not the value captured at render. No second place was added. `DeepLink.tsx` is unchanged, so spec §4 holds: the shell navigates only to a note it found in a vault of the reader's list, and every failure is the same "Not here".

**Tests.** `DeepLink.test.tsx` has a new block with its own fake API: Skills `vs`, Welcome `vw`, and Work `vk` with `/Work/HT/Alpha.md`.

- RED, before the fix:
  ```
  × a deep link shows its vault in the tree and the picker, not the one shown before 55ms
  AssertionError: expected 'Welcome' to be 'Work' // Object.is equality
   ❯ src/routes/DeepLink.test.tsx:166:46
        Tests  1 failed | 14 passed (15)
  ```
- The regression case "from a note of the vault shown before, too" (in-app navigation) passed before the fix, as the plan review had measured.
- GREEN: `Tests 15 passed (15)`. Neighbours `MobileShell.test.tsx`, `DeepLink.test.tsx` and `lastNote.test.tsx`: `Test Files 3 passed (3)`, `Tests 112 passed (112)`.
- The drawer assertions (the `h2`, `Switch vault: Work`, `Alpha` with `aria-current="page"`) were seen only green. In red, the test stops at the vault assertion before it reaches them.

**Mutations**, each alone and restored:

1. The condition reads the render's `activeVault` again: the new test fails, expected "Work", received "Welcome".
2. The `!activeVault` check is dropped: both new tests fail.

**End-to-end test:** "deeplinks: a link into another vault switches the picker and the tree to it @deeplinks @phone", `deeplinks.spec.ts:142`.

- It asserts that the first vault that isn't Skills is not Work. Alice opens a Welcome note, then `page.goto`s a Work note's link as a fresh load.
- It checks the picker and the marked row: in the docked tree on the desktop, and in the drawer on the phone.
- It passed on both: `✓ 137 [desktop] … (532ms)` and `✓ 246 [phone] … (742ms)`.
- It was never run before the fix, so it was never seen red. It follows the fresh-load path that had the bug.

**Left as they are** (they behave the same for `/notes/…` links, which is what the spec asks):

- A deep link followed in the app while the right pane has the focus: `openNote(path, "read", "left")` takes the other-pane branch, which doesn't set the vault.
- A deep link to the note that is already open on the left, while the tree shows another vault.

### §2.2 F-C: the reader keeps "This code doesn't open a published note"

Commit `057944d` Open the code of a changed address in the reader without a reload (`web/src/reader/Reader.tsx`, `Reader.test.tsx`).

**The fix.** A `hashchange` listener in an effect of its own, removed in its cleanup.

- It ignores an empty hash and the copy's own forms: `^#\/\d+$`, `^#kn-\d+(\/.*)?$` and `^#n\d+$`.
- Any other hash it treats as a code: it takes the code out of the address with `replaceState`, sets the state to loading, which drops the copy shown and the wrong-code flag, and reads the code as the first load does.
- The first-load effect is unchanged. The `latest` counter still drops a late answer.

**Tests.** Four new cases:

- a wrong code, then the right one;
- in-copy hashes and an empty hash post nothing;
- the old copy goes while the new code loads, and `replaceState` runs before the POST;
- one POST under StrictMode, and none after an unmount.

RED, before the fix:

```
× opens the code of a changed address after a wrong one
× drops the copy shown while the new code is read
× sends one request for a changed address under StrictMode
Tests  3 failed | 30 passed (33)
```

GREEN: `Tests 33 passed (33)`.

**Mutations**, each alone and restored:

1. `replaceState` dropped: killed (`expected '#K7QM-…' to be ''`).
2. The in-copy filter dropped: killed (`expected 2 to be 1`).
3. The loading state dropped: killed ("Loading …" not found).
4. The empty-hash check dropped: killed.
5. The cleanup's removal dropped: killed (`expected 2 times, but got 3 times`).

On mutation 5: the plan expected two POSTs under StrictMode. That can't happen, because the first listener empties the hash before the second one runs. So the implementer added an unmount-then-change step to the StrictMode case.

### §2.3 Click-to-Write wait: 250 ms → 200 ms

Commits:

- `7ff2496` Start Write mode 200 ms after a plain click in the other pane
- `83e3949` Type the 200 ms test's right pane: a fix round, see below
- `e1af1ab` Amend the workspace spec's click wait to 200 ms

**The change.** `web/src/note/NoteView.tsx:79` is now `DOUBLE_CLICK_MS = 200`. `PREVIEW_PAUSE_MS` stays at 250. No test had named 250 ms for this wait.

**Test.** A new test in `web/src/shell/split.test.tsx` uses fake timers: 199 ms after a plain click the pane still reads, and 1 ms later it writes.

- RED: `expected 'read' to be 'write'` (1 failed, 56 passed). GREEN: 57 passed.

**Mutations:**

- 250: fails.
- 150: fails at the 199 ms assertion.

**The fix round.** The first version used `panes.right.mode` on a nullable field. Vitest doesn't typecheck, so it passed. `tsc --noEmit`, which `pnpm -C web build` runs, failed with `TS2531` at `split.test.tsx:768,770`. The Task 3 review missed it, and Task 4's implementer noticed it. The implementer was resumed and wrote `panes.right!.mode`; I checked `tsc --noEmit` clean myself.

**The amendment.** On `workspace-design.md:93`, "waits a quarter of a second" is now "waits 200 ms (amended: 200 ms, decided by Chris in the hand tests of 2026-11-20, because 250 ms felt slow)". The line's later "well within a quarter of a second" stays. Line 109 stays.

### §2.4 The PIN dialog names Settings → Security

Commit `996bdc5` Name Settings → Security in the PIN dialog.

**The change.** The link text at `PinDialog.tsx:87` is now "Settings → Security → Discreet vaults". The paragraph reads "The PIN is set in Settings → Security → Discreet vaults.".

**Tests.** Updated in `PinDialog.test.tsx` (lines 195, 196, 204, 216 and 221) and `MobileShell.test.tsx:1253`.

- RED: 3 failed, 15 passed. GREEN: PinDialog with MobileShell, 101 passed.

**Mutation:** the old text put back: the same 3 tests fail.

**Not changed:** the Welcome vault's note `src/Kumbu.Server/Welcome/Notes/Security and privacy.md:24`, "Set a PIN in Settings → **Discreet vaults**". It is server content outside the spec's list and is guarded by `WelcomeTextTests`. It is recorded in `open-items.md`.

### §2.5 `restart: unless-stopped` for the server

Commit `88e5a54` Restart the server after the kernel ends it at its memory limit (`compose.yaml`, `README.md`).

**The change.** `restart: unless-stopped` sits under `mem_limit`. The comment above it now says that Docker starts the server again. `README.md:566` no longer says the policy comes only with `compose.traefik.yaml`.

**Proof:**

- `docker compose -f compose.yaml config`: `restart: unless-stopped` under `kumbu` and `pdf` (which had it already), none under `mongo`.
- The end-to-end stack's `compose config` shows the same.
- While the suite ran: `/kumbu-e2e-kumbu-1 policy=unless-stopped restarts=0`. The server didn't restart during the run.
- After `down.sh`: `docker ps -a --filter label=com.docker.compose.project=kumbu-e2e` listed no container. The policy keeps nothing alive after `down.sh`.

**What changes for the end-to-end suite** (spec §2.5): a server ending at its memory limit during the suite now restarts quietly instead of failing loudly. The tests that run while it is down fail, but the suite goes on against a fresh process. `docker inspect -f '{{.RestartCount}}' kumbu-e2e-kumbu-1` while the stack is up shows whether it happened.

### §2.6.1 Start's publication bar

Commit `f865829` Say in Start's publication bar that a linked note or file changed.

**The change.** "Published copy is older: a linked note or file changed 10:15" shows when Start's own `lastWriteAt` isn't after `snapshotAt`. Otherwise the text is today's.

- The time format and the same-day rule come from `olderText`, as a shared `changedWhen`.
- `NoteView.tsx:671` already passes `lastWriteAt`.
- No server change and no new fetch.

**Tests.** RED: 4 of 29 failed (the linked text, another day, no `changedAt`, equal times). GREEN: 29 passed.

**Mutations:**

- `<=` → `<`: the equal-times case fails.
- The `lastWriteAt` condition dropped: 2 tests fail.

### §2.6.2 The recovery key's line

Commit `6f30f02` Say why the recovery key's Continue waits.

**The change.** "Download or copy the key first." shows until the key is downloaded or copied. Continue points at it with `aria-describedby` while it shows. The checkbox rule is unchanged.

**Tests.** RED: 2 failed, 9 passed. The Download case passed in red too, so it only proves something under the mutation. GREEN: `RecoveryKeyReveal` 11 passed, `RevealLocks` 8 passed.

**Mutation:** the line always shown: 2 tests fail.

### §2.6.3 The Info panel of a file over 15 MB

Commit `87d2844` Tell in the viewer's Info that a file over 15 MB isn't read.

**The change.** For a PDF or an image over `MAX_ATTACHMENT_BYTES` (imported from `upload/planUpload.ts`, where `Thumbnail.tsx` gets it), the panel's last line says "Larger than 15 MB: Kumbu² keeps it, but doesn't search its text or show a thumbnail."

- The panel is now a wrapper `div` holding the `dl` (`aria-label="Info"` kept) and the line, since a `dl` may hold only `dt`/`dd` groups.

**Tests.** RED: 2 failed, 21 passed. GREEN: 23 passed.

**Mutations:**

- `>=`: the cases at exactly the limit fail.
- The kind check dropped: the "other kind" case fails. Info is reachable for a plain file, so that case was added.

### §2.6.4 The archive's count

Commits:

- `2f25865` Count the archive's notes and files as items in the tree
- `3ea1013` Count the archive's items in the README too: from the final review, `README.md:427` still showed "Archive · 23 files · 412 MB"

**The change.** "Archive · 1 item · 8 B", and "… items …" for more.

**Tests.** RED: 2 failed, 1 passed. GREEN: 3 passed.

**Mutation:** the singular "file" put back: fails.

## The final gate

- `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`: `Test Files 206 passed (206)`, `Tests 4170 passed (4170)`, build `✓ built`. That is 20 tests more than the baseline.
- `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore`: `Build succeeded. 0 Warning(s) 0 Error(s)`.
- No file of `src/`, `pdf/`, the diagrams or Mermaid changed. So no server class, `test:diagrams` or pdf job ran here; CI runs them.
- The final whole-branch review (Opus) said "ready to merge". It found no critical or important issue. Its one recommendation, the README label, was taken (`3ea1013`).

## The end-to-end run

`E2E_PROXY_CA=/root/.ccr/ca-bundle.crt pnpm -C web e2e:0.6.0` ran once, from a fresh stack (`down.sh` first), 22:05 to 22:16 UTC.

- **Parallel phase:** 259 passed, 6 skipped, 2 failed (3.8 minutes).
- **Serial phase:** 41 passed, 1 skipped (5.3 minutes).
- The new deep-link test passed on desktop and phone.

Both failures are in the MCP project and are pre-existing. Neither is about this branch:

1. `e2e/mcp/blame.spec.ts:362`, "help blame starts as §6 prints it": the test expects `blame [--lines] <path>\n  Who wrote what:`.
2. `e2e/mcp/upload.spec.ts:208`, the `upload` tool's description: the test expects "The write tool writes notes, SVGs and a skill's text files". The server says "Notes and a skill's text files are written with the write tool, an SVG with either tool".

Both texts come from #264 ("Make the help, the tool descriptions and the console say what the code does"), which changed `src/Kumbu.Core/Sh/Manual.cs` and is on `master`. This branch changes nothing under `src/`.

They aren't flaky (the text differs every time), so they weren't rerun alone. They also weren't run on the base itself, to keep within the run's time. They are recorded in `open-items.md` for the next run: the end-to-end tests follow #264's texts. No known flaky test appeared.

## Decisions made without Chris

1. **Where F-A is fixed: in the shell's default-vault effect** (`MobileShell.tsx:376`).
   - Options: set the vault in `DeepLink.tsx` before `<Navigate>`, or in the shell.
   - The brief asks for the one place that owns "the current vault". The shell picks the default, and a second place in `DeepLink` would have been one more owner.
2. **Which hashes are codes in F-C.**
   - Options: also skip any hash naming an element on the page, or skip only the copy's own three forms.
   - The reader renders no anchor that changes the hash (`Reader.tsx:180-195`), so the extra check would guard a case that can't occur (CLAUDE.md).
3. **The publication bar's new text holds for any published note, not only for bundles.**
   - Options: only when `depth > 0`, or always.
   - For a single note whose own text is unchanged, only an embedded file can make the copy older, and "a linked note or file changed" is true there too.
4. **The day in that text.** I read "with the day as today, when it isn't the same day" as the bar's existing rule: the day shows when the change isn't on the snapshot's day.
5. **The amendment's form.** Line 93's phrase now says 200 ms and carries the spec's sentence in brackets, so the change stays one line and doesn't contradict itself. The later "a second click comes well within a quarter of a second" stays: it is still true and the brief keeps the amendment to one line.
6. **HTML and accessibility details the spec leaves open.**
   - The Info line sits below the `dl`, in a wrapper.
   - The recovery key's line is Continue's `aria-describedby`, so the disabled button says why it waits.
7. **The Welcome note's "Settings → Discreet vaults" stays as it is,** because it is server content outside the spec's list. It is recorded in `open-items.md`.
8. **The README's archive example follows the new label** (`3ea1013`). It is docs only, and the old text would have contradicted the app.
9. **The end-to-end limit was 40 minutes instead of 20,** because `run.sh` builds the images before the suite. The suite took 11 minutes.

## Deviations and open points

- **Process:**
  - Implementers committed before trying mutations, so that `git checkout -- <file>` restores the fix instead of losing it. The plan's order (mutate, then commit) was corrected for this.
  - The Factory board wasn't reachable (no `Kumbu_Factory` connector).
- **Never seen red:**
  - The end-to-end test of F-A.
  - The F-A drawer assertions.
  - The recovery key's Download case, which passed in red too and is meaningful only under the mutation.
- **The second "quarter of a second"** on `workspace-design.md:93` may want a word from Chris.
- **For `open-items.md`:**
  - the Welcome note's text;
  - the two MCP end-to-end tests that still expect the texts before #264.
- **Another spec:** `docs/superpowers/specs/2026-11-09-pre3-findings-design.md:25,35,40` and `2026-11-09-discreet-vaults-design.md:133` name "Settings → Discreet vaults". They record their time and were left as they are.

## Hand tests for Chris

On staging, once it runs a build of this branch (or of `master` after the merge), signed in as yourself in a desktop browser at 1280 px or wider, and on the phone.

1. **F-A, desktop.** Open a note of Welcome, so the tree shows Welcome. Copy the link of `Work/HT/Alpha.md` (the ⋯ menu's Copy link, or an agent's answer), paste it into the address bar of the same tab and press Enter. **Passes:** Alpha opens, the vault picker at the top of the tree says Work, the tree shows Work's folders with `HT` open and `Alpha` marked.
2. **F-A, phone.** As step 1 on the phone: from a Welcome note, open the same `/n/…` link. Open the tree (the title or Files). **Passes:** the drawer says Work and marks `Alpha`.
3. **F-A, after sign-in.** Sign out, open the same `/n/…` link, sign in. **Passes:** as in step 1, the tree and the picker on Work.
4. **F-C.** In a fresh tab, open `…/p#WRONG`. **Passes:** "This code doesn't open a published note." Then paste a right link (`…/p#<code>`) into the same tab and press Enter. **Passes:** the published note appears without a reload, and the address bar no longer shows the code.
5. **The 200 ms wait.** Open two notes side by side (from 1280 px). Click into the right pane's text a few times, one plain click each, with the left one focused in between. **Passes:** Write mode starts after each click, a little sooner than before. Then double-click a word in the pane that isn't writing. **Passes:** the word is selected, Write mode doesn't start.
6. **The PIN dialog.** Open More vaults… in the vault menu. **Passes:** under the PIN field: "The PIN is set in Settings → Security → Discreet vaults.", the link goes to Settings → Security, at Discreet vaults.
7. **Start's publication bar.** With a bundle published from Start that links Alpha, change Alpha (one word), then open Start. **Passes:** the yellow bar says "Published copy is older: a linked note or file changed HH:MM" (with the day if the change isn't on the snapshot's day). Change Start itself. **Passes:** the bar says "Published copy is older than this note. Published … · changed …", as before.
8. **The recovery key's line.** Create a vault (or regenerate a recovery key) until the recovery key is shown. **Passes:** near Continue, "Download or copy the key first."; Continue greyed out. Download or copy the key. **Passes:** the line goes; Continue waits for the box to be ticked, then works.
9. **The Info panel of the 16 MB PDF.** Open the 16 MB PDF in the viewer and press Info. **Passes:** the last line says "Larger than 15 MB: Kumbu² keeps it, but doesn't search its text or show a thumbnail." Open a small PDF's Info. **Passes:** no such line.
10. **The archive's count.** In a vault whose archive holds one archived note, look at the bottom of the tree. **Passes:** "Archive · 1 item · 8 B" (the size as it is), and "… items …" for more than one.

## The pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/266, from `feature/fixes-0-6-0-pre-5` to `master`. Not merged, tagged or deployed. The version and `CHANGELOG.md` are unchanged.
