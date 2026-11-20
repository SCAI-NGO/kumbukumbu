# Report: the fixes of the 0.6.0 security review

The overnighter of 2026-11-19/20, from the brief `docs/superpowers/overnighter/2026-11-19-security-fixes-0-6-0-brief.md` and the public spec `docs/superpowers/specs/2026-11-19-security-fixes-0-6-0-design.md`. Pull requests: https://github.com/SCAI-NGO/kumbukumbu/pull/265 (this branch, `feature/security-fixes-0-6-0`) and the private one, https://github.com/SCAI-NGO/kumbukumbu-security/pull/6. Neither is merged.

## Environment

- **Machine:** 4 CPUs, 15 GB RAM, Linux 6.18, the clock in UTC. **The container restarted twice** (about 23:35 and 01:15 UTC): each time `dockerd` and `kumbu-test-mongo` were started again, the heavy lock cleared and the stopped agents resumed with a note of what was lost (their background builds and test runs). About an hour was lost in all; no commit was.
- **.NET SDK 10.0.404** (`global.json` asks 10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`. **Node 26.13.1** in `/opt/node26`; **pnpm** 10.28.0, installed 10.32.1. **Docker** was installed; `dockerd` wasn't running, so it was started; MongoDB 9 as the brief says, with the log cap.
- **One heavy run at a time** under `flock /tmp/heavy.lock`, `MSBUILDDISABLENODEREUSE=1`. A compiler server left by a build kept the lock's descriptor open twice (about 20 minutes each); from then on the builds ran with `UseSharedCompilation=false` and `DOTNET_CLI_USE_MSBUILD_SERVER=0`.
- **Superpowers:** loaded. The plan was written with `writing-plans`, the work ran subagent-driven: one fresh implementer per task in its own worktree (up to three or four at a time, their heavy runs taking turns on the lock), then one fresh reviewer, fix rounds that resumed the implementer and re-reviews by the same reviewer until the verdict was clean. I committed, cherry-picked onto this branch after the review and pushed; subagents never did. Where a review asked for one or two small text changes, I made them myself and said so (the commits are named below).
- **The Factory board:** the `Kumbu_Factory` connector was not attached to this session, so there is no card; this report is the account.
- **Nothing ran against a real host.** No dependency was added; `web/src/api/schema.d.ts` was not regenerated.

## The plan and its review

The plan is in the private repository (it names the security findings): four parts written in parallel, then one review by a fresh subagent that had both specs, the brief and the plan. **24 findings, 13 of them important**; all were taken, as a section of amendments that bind the tasks.

## Task 0: the baseline

- **Planned:** a Release build and the test classes the fixes touch, one at a time, about 15 minutes.
- **Measured:** the build in 1 min 15 s (0 warnings, 0 errors). The tasks touch 112 existing test classes, so they ran grouped per project (decision 2), from 22:13 to about 22:45 UTC, sharing the lock with the first implementers' builds:
  - Core (the classes of the list): 1,645 total, 1,645 passed.
  - Server (the classes of the list): 1,389 total, 1,381 passed, 0 failed, 8 skipped (6 `ChunkTokenizerTests` without `KUMBU_TOKENIZER`, `OpenAiCompatibleDriverTests.TensorX_ranks_by_meaning`, `SearchIndexTests.The_real_model_ranks_by_meaning`: expected).
  - Web, the whole Vitest suite: 205 files, 4,111 tests passed. Sidecar: 10 files, 211 tests passed.
- **The baseline end-to-end run** (`E2E_PROXY_CA=/root/.ccr/ca-bundle.crt pnpm -C web e2e:0.6.0`, before any change): 259 passed and 6 skipped in the first phase, 37 passed and 5 skipped in the second, exit 0. It is the measurement for the memory ceiling.

## The memory ceiling (public spec §3)

**Commit** `e9953d4` "Give the server's container a memory ceiling, KUMBU_MEM_LIMIT".

- **Measured** before the limit was added, on the baseline end-to-end run: `docker stats` of `kumbu-e2e-kumbu-1` sampled from 22:03 to 22:12 UTC, 134 samples about 4 s apart. **Peak: 501.8 MiB** (0.49 GiB).
- **The default:** 2 × (0.49 GiB + 1.5 GiB for the embedding model, which the e2e stack runs without) = 3.98, rounded up to **4g**; above the floor of 3g. Compose's `g` is GiB.
- `compose.yaml`: `mem_limit: ${KUMBU_MEM_LIMIT:-4g}` on the server, with a comment; the sidecar keeps its `1536m`. Checked with `docker compose config`: 4294967296 by default, 2147483648 with `KUMBU_MEM_LIMIT=2g`, the same through the e2e stack's layering.
- `README.md`: a "Good to know" item (what it does, the format, the default, when to raise it), the variable in the Compose list and a clause in "Without Compose"; `.env.example`: the variable with `KUMBU_MEM_LIMIT=8g`.
- No restart policy was added to `compose.yaml` (decision 3).
- **Under the limit:** both final end-to-end runs (below) ran with it; the server was not ended by it (`OOMKilled` false).

## F-10: a hide from elsewhere never leaves a discreet vault's note in the clear

**Commits** `9110a59` "Hide every discreet vault shown in the mode, also one a vault list read first left out (F-10)" and `a1b3e1c` "Write /discreet for a vault shown in the mode until it is hidden, also once a vault list read left it out (F-10)". F-10's commits hold F-10 only.

**Reproduced first.** A scratch copy with an unminified build and recording (the vault list, the session, the socket's frames, `history.pushState`/`replaceState`), the palette test without `fixme`, `--repeat-each=10` on `serial` and `serial-phone`: **4 failed, 16 passed**, each time with the address on `/notes/Home/Zahnarzt.md`. All four records show one sequence:

```
setFromVaults  2145  names ["Home"]
socket close   2473  code 1006          (no hidden frame reached the tab)
socket open    3482  again: true        (the reconnect reads session, vaults, … again)
setFromVaults  3508  names []           (the vault list's answer, without Home, comes first)
run            3518  names [], pathname /notes/Home/Zahnarzt.md
navigate       3520  to /Home/Zahnarzt.md   (with replace)
```

**The race it closes:** the tab's socket went without the `hidden` frame; on the reconnect the vault list's answer came before the session's and emptied the discreet names; the hide then took its names from the store, empty, so nothing counted as discreet: the "last ordinary note" was the hidden note itself, the neutral window wrote its address in the clear, and Recent got it back. **The fix:** the tab keeps `listed`, the discreet vaults the list has named since the mode was last reset (memory only; a vault listed as ordinary leaves it; a reset clears it), and the hide closes the names and `listed` together. The review then found the same gap one step earlier: a navigation to a note of the vault between that list read and the hide (a click in the tree, a rename followed) would still have reached the address bar and the history; the second commit lets the neutral window count `listed` too.

**Tests.**
- Red before the first commit (`state.test.ts`, `hideAgain.test.tsx`, `WindowHost.test.tsx`): 4 failed, 56 passed (`expected '/notes/Tagebuch/a.md' to be '/notes/Work/a.md'`; the location behind the window kept `Tagebuch`). Green: 60 of 60.
- Red before the second (`neutralWindow.test.tsx`): 1 failed, 24 passed (`expected '/notes/Tagebuch/a.md' to be '/discreet'`). Green: 25 of 25.
- Neighbours: `src/discreet/ src/windows/ src/palette/ src/recent/ src/shell/`: 54 files, 811 passed; `tsc` for the app and for `e2e` clean.
- **Mutations:** 6 of 6 caught (names alone in the hide; `listed` not filled; a vault listed as ordinary not leaving it; a reset keeping it; `discreetUrl` and `keepUsr` back to the names alone).
- **End to end:** both former `test.fixme` tests in `web/e2e/discreet/discreet-0-6-0.spec.ts` are plain tests. `--list --grep "F-10"`: 4 tests (both on `serial` and `serial-phone`). `pnpm -C web exec playwright test --no-deps --workers=1 --project=serial --project=serial-phone --grep "F-10" --repeat-each=10`: **40 passed (11.6 min)**, on the build with F-10's first commit and F-11. The second commit came after it; the two final end-to-end runs carry it (below).

## F-11: ✕ on a window whose note was hidden shows the vault list

**Commit** `4ee7b48` "Close a window over a note hidden again to the vault list (F-11)". The location behind a window whose note was hidden again is now `/` with `{ overview: true }` (the state the vault switcher's "the vaults" opens), not the last ordinary note, as the windows spec says (lines 135 and 168). Only the hide-again path changes.

- Red: `expected '/notes/Work/readme.md' to be '/'`, 1 failed, 19 passed. Green: 20 of 20. Neighbours (windows, MobileShell, VaultSwitcher, discreet): 258 passed.
- **Mutations:** 3 of 3 caught.
- **End to end:** the windows test's `fixme` is gone; it is one of the 40 repeats above, and `--grep "discreet-0-6-0|@windows"` once: 15 passed.

## M-01: `skills cat project-memory` reads the system skill

**Commit** `a627f83` "Let skills cat read the system skill by its name beside a vault's skill of that name (M-01)". Where a bare name matches more than one skill and a system skill has it, only the system skill, or her own `/Skills/<name>` that takes its place, is kept; a vault's `_skills` skill of that name is read by its path; `skills ls` lists both, unchanged. `SkillsCommandTests`: the test that pinned the ambiguity is replaced by `A_system_skills_name_reads_the_system_skill_beside_a_vaults_skill_of_that_name`; new `Her_own_skill_in_Skills_still_takes_the_system_skills_name_beside_a_vaults_skill_of_that_name` and `Two_vault_skills_of_one_name_stay_ambiguous`. The MCP test `memory: a _skills/project-memory doesn't replace the system skill` loses its `test.fail`. I reworded the README sentence the review pointed at before the push.

- Red: 2 failed, 38 passed (`'project-memory' names 2 skills`). Green: 40 of 40. Neighbours: SkillDirectory 43, SkillInstructions 14, CatEmbeds 30, SkillsShell 7, ConsoleApi 88, all green.
- **Mutations:** 3 of 3 caught.

## The security findings

By id and state only; their records are in the private pull request.

- **Fixed:** S6-01, S6-02, S6-03, S6-05, S6-06, S6-07, S6-08, S6-09, S6-10, S6-11, S6-12, S6-13, S6-14, S6-15, S6-16, S6-18, S6-20, S6-21; L6-13.
- **Not fixed in this run** (time): S6-04, S6-17, S6-19, S6-22, S6-23, S6-24; L6-01, L6-04, L6-05, L6-07, L6-08, L6-09, L6-10, L6-11, L6-12, L6-14, L6-16, L6-18, L6-19, L6-20, L6-23, L6-24, L6-26, L6-28.
- **Written for the release:** L6-22.
- **For 0.7.0, as agreed:** L6-02, L6-03, L6-06, L6-15, L6-17, L6-21, L6-25, L6-27.

Each fixed one: a behaviour test seen red, green, every mutation alone, a fix review by a fresh subagent, the finding's own proof test where it applies. No fix was dropped; four went back to their implementer at least once and passed on a later round.

## The gates

**Gate 1, after the Highs and S6-02** (02:25 to 03:00 UTC, at `77f8840`):
- restore, build: ok.
- Core: 3,771 total, 3,701 passed, 70 skipped, 0 failed.
- Server, Api/Mcp/Auth/Net: 1,027 total, 1,026 passed, 1 failed: `SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once`, one of the brief's known flaky classes under load. Alone, three times: 11 of 11 each time.
- Server, Accounts/Sharing/Welcome: 827 of 827. Vaults/Skills/Search/Metrics: 881 total, 869 passed, 12 skipped. The rest: 1,296 total, 1,293 passed, 3 skipped.
- Web: 205 files, 4,117 tests passed; build ok. Diagrams: 4 passed. Sidecar: 10 files, 211 tests passed; build ok.

**Gate 2, at the end** (04:01 to 04:25 UTC, at `03899eb`, the branch's last code commit):
- restore, build: ok.
- Core: 3,806 total, 3,736 passed, 70 skipped, 0 failed.
- Server, Api/Mcp/Auth/Net: 1,034 total, 1,033 passed, 1 failed: the same `SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once` as in gate 1. Alone, three times: 11 of 11 each time. CI's `build (api)` job on #265, which runs it, is green.
- Server, Accounts/Sharing/Welcome: 829 of 829. Vaults/Skills/Search/Metrics: 882 total, 870 passed, 12 skipped. The rest: 1,304 total, 1,301 passed, 3 skipped.
- Web: 206 files, 4,133 tests passed; build ok. Diagrams: 4 passed. Sidecar: 10 files, 211 tests passed; build ok.
- **CI on #265:** all nine checks green, WebDAV's `litmus` and both image builds included.

## The end-to-end runs

Each from a fresh stack (`E2E_PROXY_CA=/root/.ccr/ca-bundle.crt pnpm -C web e2e:0.6.0`, which brings the stack up from this checkout and takes it down with its volumes), on the branch's last code commit, with the memory ceiling in place:

| Run | Time (UTC) | First phase | Second phase (serial) | Exit | Server's peak |
|---|---|---|---|---|---|
| 1 | 04:25-04:36 | 260 passed, 5 skipped | 42 passed | 0 | 473.7 MiB |
| 2 | 04:36-04:47 | 259 passed, **1 failed**, 5 skipped | 42 passed | 1 | 477.7 MiB |
| 3 | 04:47-04:57 | 260 passed, 5 skipped | 42 passed | 0 | 509.3 MiB |

- **Runs 1 and 3 are identical**, and F-10's two tests pass in both as plain tests, on `serial` and `serial-phone` (`discreet-0-6-0 palette: … the address leaves the note (F-10)` and `discreet-0-6-0 windows: … ✕ shows the vault list (F-10, F-11)`), as does M-01's MCP test, no longer expected to fail. The sibling run's findings are still `test.fail` here and fail as expected.
- **Run 2's one failure:** `[phone] palette #1: Ctrl+P and the star open the palette …`: `toBeFocused` on the palette's field timed out, the call log showing `getByRole('region', { name: 'Install Kumbu² as an app' })` intercepting. The phone's install hint came up over the palette. Possible causes, ranked: (1) the install hint's timing on the phone, the UI tests' known open point "the install hint under windows on the phone"; (2) something of this run in the palette or the shell (this run changes neither the palette nor the hint; the test passed in the baseline and in runs 1 and 3); (3) load. Run 3, a further fresh run, passed it; the failure is recorded, not hidden (decision 10).
- **The ceiling held:** no out-of-memory event in any run (`docker events --filter event=oom` empty), and the peaks stay far below 4g. Across the four runs of the session the server peaked between 474 and 509 MiB.
- `SyncHubReviewTests`, the one red class of both gates, passed alone three times after the runs: 11 of 11 each time.

## Decisions made without Chris

1. **The Factory board:** the connector was not attached; the run went on without the card (the brief's rule for this case).
2. **Task 0 grouped per project:** 112 classes one at a time would have taken over an hour against the brief's 15 minutes; a red class would have been rerun alone three times (none was red).
3. **The memory ceiling:** measured on a baseline end-to-end run made before any change (the first end-to-end run of the session), `docker stats` every few seconds; no restart policy added to `compose.yaml`, which has none for the server today (breaks nothing: `docker compose up` in development stays as it was). Whether it should restart is open for Chris.
4. **F-11 straight after F-10**, before the Mediums, against the order of the private spec: F-10's windows test can pass only with F-11's behaviour.
5. **F-11 covers a vault hidden again.** Where ✕ lands after a vault is taken away under a window is unchanged; open for Chris.
6. **M-01 adds no "others of this name exist" line:** `skills ls` already lists both, and `cat`'s output stays byte for byte.
7. **Documents of a changed rule** are brought up to date at the release, as with earlier security fixes (gives nothing away); their text is ready in the private record.
8. **Order within the Mediums:** after the Highs, the smaller Mediums went before the larger ones so that more fit the night (true to the priority list: Highs first, then Mediums).
9. **Builds without a shared compiler:** a compiler server left by a build held the shared lock; switching it off cost a little build time and freed the lock.

10. **A third end-to-end run:** run 2 failed one phone test on the install hint (above); rather than call two different runs identical, a third fresh run was made, identical to run 1. All three are reported.

## Deviations and open points

- **The container restarted twice**; about an hour was lost, no commit.
- **Not done (time):** the Mediums and Lows listed above as not fixed; they keep their tasks in the plan, and `open-items.md` names them by id.
- **F-10:** why the socket closed with 1006, without the `hidden` frame, in all four failures is not settled. A guess, not checked: the server's `CloseAsync` (`src/Kumbu.Server/Sync/SyncHub.cs:921-937`) cancels a send under way. `Sync/` was outside this run; the client no longer depends on hearing the frame first.
- **F-11:** ✕ after a vault is taken away under a window (`leaveVaults`) is unchanged (decision 5).
- **M-01:** her own copy `/Work/_skills/project-memory` (after `skills copy project-memory /Work`) is still a start-up skill, but `skills cat project-memory` now gives the system skill; her copy is read by its path. That is what the decision says ("any vault's").
- **The memory ceiling:** whether the base `compose.yaml` should restart the server (decision 3).
- **The tests report's screenshots** were rewritten by every end-to-end run and restored each time; none is committed.

## Hand-test checklist (staging, on this pull request's build)

1. **F-10, the palette and Recent.** On the laptop, Settings › Vaults: mark Home discreet and show it; open a Home note (e.g. `/Home/Zahnarzt.md`), then an ordinary note in Work, then the Home note again. On the phone, hide the discreet vaults. *Look at* the laptop's address bar, Recent and the palette (type a word of the Home note). *Passes:* at once the address leaves the Home note (the last ordinary note's, or `/`); Recent and the palette name no Home note. Repeat three times, also with the laptop's network dropped for a moment just before the hide.
2. **F-10, the window.** As above, with Settings › Vaults open over the Home note when the phone hides. *Look at* the window and the shell behind it. *Passes:* nothing of Home shows, in the window or behind it.
3. **F-10, the history.** After step 1, press Back a few times on the laptop. *Passes:* no entry shows a Home note's address in the clear.
4. **F-11.** After step 2, close the window with ✕. *Passes:* the vault list ("Your vaults") shows, not the last ordinary note.
5. **M-01.** In a team vault Work, create `_skills/project-memory/SKILL.md` (any text). In the console: `skills cat project-memory`. *Passes:* the system skill's text. `skills cat /Work/_skills/project-memory` gives Work's; `skills ls` lists both. With her own `/Skills/project-memory`, `skills cat project-memory` gives hers.
6. **The memory ceiling.** On the server: `docker inspect -f '{{.HostConfig.Memory}}' <kumbu container>`. *Passes:* 4294967296 without `KUMBU_MEM_LIMIT`, the set value with it (e.g. `8g` → 8589934592). `docker stats` shows the limit beside the server.

## The pull requests

- Public: https://github.com/SCAI-NGO/kumbukumbu/pull/265
- Private: https://github.com/SCAI-NGO/kumbukumbu-security/pull/6
