# Overnighter report: a read-only console

Branch `feature/read-only-console`, from `master` at `c38664e` (Release v0.3.0). On top of the spec, the plan and the brief (`85fafd2`, `a1d3c86`) come 16 work commits up to `c1130ad`, then this report. Every commit was pushed to `feature/read-only-console` and mirrored to the session's branch `claude/read-only-console-overnighter-5gw5op` (the session's harness named that branch; the brief names the feature branch, so both carry the same commits).

Nothing was merged, tagged, released or deployed. No version file was touched, nor `CallerMiddleware.cs`, `Manual.cs`, `Help.cs`'s text, the tool descriptions or the shell's sentences.

**Pull request:** https://github.com/SCAI-NGO/kumbukumbu/pull/139 (draft).

---

## 1. Summary

All seven tasks are built, reviewed and pushed, and both phase gates and the final verification are green, apart from one flake explained in §4.

| Phase | Tasks | Gate |
|---|---|---|
| A, the server | 1 endpoint and read-only rule, 2 `cd` and the history limit | green at `175549f` |
| B, the web app | 3 tokeniser, 4 relative paths, 5 the page | green at `6394969`, apart from one flaky account test (§4) |
| C, documents and the end | 6 docs, hand test, security review; 7 the end | green at `c1130ad` |

**Final verification**, as CI runs it, with a fresh MongoDB. The `dotnet` stages ran at `21b3ed2`; between that commit and `c1130ad` no .NET file changed (`git diff --stat 21b3ed2 c1130ad` touches only `paths.ts`, `paths.test.ts` and the security document). The web and sidecar stages ran at `c1130ad`.

| Step | Result |
|---|---|
| `dotnet restore --locked-mode` | exit 0 |
| `dotnet build --configuration Release --no-restore` | exit 0 |
| `dotnet test --configuration Release --no-build` | total **3471**, succeeded **3465**, skipped **6**, failed **0** (7m 56s) |
| `pnpm -C web install --frozen-lockfile && pnpm -C web test --run` | **88 files, 1567 tests passed** |
| `pnpm -C web build` | `✓ built in 6.50s` |
| `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build` | **6 files, 78 tests passed**; build exit 0 |

On top of the baseline (3408 / 85 files and 1368 tests / 78), that makes 63 more .NET tests and 199 more web tests:
- .NET: the new `ConsoleApiTests` and `ConsoleFixtureTests`, and extended `PasswordWindowTests` and `LabelGuardTests`;
- web: 3 new files (`tokenise`, `paths`, `ConsolePage`) and new rows in `MobileShell`.

The 6 skips are the 4 the brief expects (`UnigramTokenizerTests.MatchesHuggingFaceOnArcticL`, `…OnTheRealVocabulary`, `OpenAiCompatibleDriverTests.TensorX_ranks_by_meaning`, `SearchIndexTests.The_real_model_ranks_by_meaning`). The other 2 skip because the tests ran as root: `SeedScannerTests.Skips_unreadable_files_with_a_warning` and `ModelFilesTests.AReadOnlyModelFolderTurnsSearchByMeaningOffInsteadOfStoppingTheServer`.

## 2. Environment

- **Machine:** a cloud container: Linux 6.18, 4 cores, 252 GB disk with a 30 GB allowance. Run as root.
- **.NET:** SDK 10.0.403 was preinstalled; `global.json` asks for 10.0.100 with `latestFeature`, so it rolls forward. `DOTNET_CLI_TELEMETRY_OPTOUT=1` was already set.
- **Node:** Node 22.23.4 was preinstalled at `/opt/node22` and came first in `PATH`.
  - **Installed:** Node 26.13.0 from nodejs.org, its SHA-256 checked against `SHASUMS256.txt`, into `/usr/local`.
  - **Moved:** `/opt/node22` to `/opt/node22.disabled`, so that 26 is found.
  - **pnpm:** 10.28.0 was there; I installed 10.32.1 with `npm install -g pnpm@10.32.1`.
- **Docker:** Docker 29.9.2 was running; `mongo:7.0` was pulled.
- **MongoDB:** I used the brief's external replica set from the start rather than Testcontainers: `--ulimit nofile=20000:20000` and the WiredTiger file-manager settings, `rs.initiate`, and `KUMBU_TEST_MONGO=mongodb://localhost:27017/?replicaSet=rs0&directConnection=true`. Every full run got a fresh container (`docker rm -fv kumbu-test-mongo` first), from a small script in the session's scratch folder. Ryuk was never needed.
- **GitHub:** the `gh` CLI wasn't available; the pull request was opened through the GitHub connector.

## 3. Task 0: planned against measured

| | Planned | Measured |
|---|---|---|
| Setup and first build and tests | about 15 minutes | about 16 minutes: session start 21:48, tools ready 21:51, full verification 21:51:32–22:04:24 (12m52s; `dotnet test` 9m13s) |
| `dotnet test` | total 3408, succeeded 3404, skipped 4, failed 0 | total **3408**, succeeded **3402**, skipped **6**, failed **0**. The 2 extra skips are the root-only ones, as the brief foresaw |
| Web | 85 files, 1368 tests | **85 files, 1368 passed** |
| Sidecar | 6 files, 78 tests | **6 files, 78 passed** |
| Builds | succeed | succeed |

The whole run, from start to the draft pull request, took about two hours (21:48–23:50).

## 4. Per task: commits and real test output

Each task went the same way: a fresh implementer subagent did red, green, commit and every mutation; then a spec-compliance reviewer and a code-quality reviewer, each its own subagent; then fix rounds where they found something; then I pushed. The per-task reports with full output are in the run's scratch folder; the essentials are here.

### Task 1: the endpoint and the read-only rule

**Commit:** `14b66aa` "Add the console's endpoint, which refuses what writes".

**Red:** `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleApiTests"`

```
ConsoleApiTests.cs(231,112): error CS0103: The name 'ConsoleApi' does not exist in the current context
ConsoleApiTests.cs(330,26): error CS0246: The type or namespace name 'ConsolePartView' could not be found
Build failed with exit code: 1.
```

**Green:** `… --filter-class "*ConsoleApiTests" --filter-class "*PasswordWindowTests" --filter-class "*ToolMetricsTests"`

```
Test run summary: Passed!
  total: 115
  failed: 0
  succeeded: 115
  skipped: 0
```

`dotnet build Kumbu.slnx` gave 0 warnings. **Reviews:** spec ✅, quality Approved; no Critical or Important findings.

### Task 2: `cd`, and the history limit

**Commits:**
- `e5bf198` "Let the console change directory, and count history against the limit"
- `920ab0f` "Test that cd refuses a note or file with notes below its path": strengthening after mutation (a) survived
- `175549f` "Make sure the label guard's console calls are answered": review fix round

**Red:** `… --filter-class "*ConsoleApiTests" --filter-class "*LabelGuardTests"`

```
failed …Cd_on_a_busy_vault_is_503  Expected: ServiceUnavailable  Actual: BadRequest
failed …Cd_takes_folders(path: "/", directory: "/")  Expected: OK  Actual: BadRequest
failed …History_takes_a_permit  Expected: TooManyRequests  Actual: OK
  total: 62
  failed: 16
  succeeded: 46
```

**Green:** same command

```
Test run summary: Passed!
  total: 63
  failed: 0
  succeeded: 63
  skipped: 0
```

The fix round's `--filter-class "*LabelGuardTests"` gave total 3, failed 0.

**Reviews:**
- spec ✅, quality Approved.
- One Minor raised to a fix: the label guard ignored the console's status, so it could have passed without the marker ever reaching the console. Re-review: ADDRESSED.
- A ruling from me added `Help_names_a_refused_command`: `help rm` works while `rm --help` is refused (spec §5); no task had asked for that test.

**Phase A gate** at `175549f`: `dotnet test` total 3468, succeeded 3462, skipped 6, failed 0; web 85 files and 1368 tests; sidecar 6 and 78; builds OK.

### Task 3: the tokeniser

**Commits:**
- `c101036` "Add the console's tokeniser, checked against the shell's"
- `ae2c453` and `72fdad9`: strengthening after mutation (d) survived

**Red:**
- `pnpm -C web test --run src/console`: `Test Files 1 failed (1)`, as the import of `./tokenise` failed.
- `--filter-class "*ConsoleFixtureTests"` passed at once (total 3, failed 0), as the plan foresaw. Its red came from mutation (b).

**Green:** web 48 passed; `ConsoleFixtureTests` 3 of 3; `pnpm -C web build` OK.

**Reviews:** spec ✅, quality Approved.

### Task 4: relative paths

**Commits:**
- `a057903` "Let the console turn relative paths into absolute ones"
- `cfaff1e` "Leave a line with an unknown command as typed": review fix round

**Red:** `pnpm -C web test --run src/console/paths`: `Error: Failed to resolve import "./paths"`.

**Green:** paths 99 passed; `src/console` 147 passed; build OK. After the fix round, `src/console` 150 passed.

**Reviews:**
- spec ❌, one Important: a pipeline with an unknown or refused stage was still partly rewritten, against spec §7. It was fixed; re-review ADDRESSED.
- quality Approved.

### Task 5: the page

**Commits:**
- `6577841` "Add the console page"
- `87c8df0` "Tidy the console page after review": eight review minors folded in
- `d4d3549` "Show only raster images in the console": security review finding

**Red:** `pnpm -C web test --run src/console/ConsolePage src/shell/MobileShell`

```
FAIL  src/console/ConsolePage.test.tsx  Error: Failed to resolve import "./store"
FAIL  src/shell/MobileShell.test.tsx > … Unable to find an accessible element with the role "link" and name "Console"
     Tests  2 failed | 46 passed (48)
```

**Green:**

| Run | Result |
|---|---|
| Focused | 89 passed |
| `src/console` | 191 passed |
| Build | OK |
| Whole web suite | 88 files, 1560 passed |
| After the fix rounds, `src/console` + MobileShell | 239 passed |
| After `d4d3549`, `src/console` | 197 passed |

**Reviews:**
- spec ✅, quality Approved; both raised only Minor points.
- Both reviewers flagged the controller race in `submit`, so I folded it and seven other cheap minors into one round (§6). Re-review: all 8 ADDRESSED.

**Phase B gate** at `6394969`:
- `dotnet test`: total 3471, succeeded 3464, skipped 6, **failed 1**: `AccountApiTests.Recovery_revokes_every_api_key_and_replaces_the_used_recovery_key` (`AccountApiTests.cs:991`: `Expected: OK`, `Actual: Conflict`, on the second `/api/recover` right after the first). This branch touches nothing in accounts or recovery.
- I reran `AccountApiTests` alone (Release, no build) three times: 70 of 70 each time.
- At that moment the security reviewer was running tests against the same MongoDB.
- In the final verification it passed.
- I take it for a flake under load that predates this branch (§8).
- Web 88 files and 1560 tests; sidecar 6 and 78; builds OK.

### Task 6: documents, hand test, security review

**Commits:**
- `6394969` "Document the console, with its hand test"
- `21b3ed2` "Make the console's hand test easier to follow": review fix round

**Reviews:**
- spec ❌ and quality "needs fixes": the phone was called part H instead of part I; the sidecar-down step was ambiguous; part K didn't say how to make Bob and the share; one pass criterion was soft. All fixed, together with the security review's two documentation points; re-review ADDRESSED.
- **Security review:** see §7.

### Task 7: the end, and the final review

- **Final whole-branch review** (most capable model): "With fixes". One Important: line references to `ConsolePage.test.tsx` in the security document were stale, moved by `d4d3549`. One Minor worth taking: `semsearch /Work` alone lost the shell's own "missing query" error.
- **Final fix wave:**
  - `c810ef7` "Keep semsearch's own error for a path without a query": red, `paths` 103 tests with 1 failed; green, 103 passed; the mutation fails again; `src/console` 198 passed; build OK.
  - `c1130ad` "Point the security document at the console's tests again".
- **Scoped re-review:** both ADDRESSED, no new breakage.
- **`git diff master...HEAD --stat`:** no version file; `CallerMiddleware.cs` untouched; under `src/Kumbu.Server/Mcp/` only `ToolResult.cs`; under `src/Kumbu.Core/Sh/` only `Shell.cs` (gains `HasCommand`). 29 files, 2971 insertions and 11 deletions, the spec, plan and brief included.

## 5. Every mutation

Each mutation was made on its own, the task's command run, and the file restored with `git checkout -- <file>` with `git status` clean afterwards.

| Task | Mutation | Result |
|---|---|---|
| 1 a | `mkcp` added to `Allowed` | 5 failed: `Every_shell_command_is_classified`, `The_lists_are_disjoint`, 3 rows of `Every_refused_command_stays_refused` |
| 1 b | only the first stage checked | 6 failed: the `cat … \| <cmd>` row of each refused command |
| 1 c | `SessionOnlyMetadata` removed | 2 failed: `PasswordWindowTests.Every_session_only_endpoint_is_guarded_or_deliberately_open`, `An_api_key_is_refused` |
| 1 d | `rm` moved to `Allowed` | 9 failed: all 7 `rm` rows, `A_read_only_member_reads_a_shared_vault`, `The_lists_are_disjoint` |
| 1 e | `HasCommand` always true | 3 failed: `Lines_the_shell_answers_itself`, `Errors_say_what_mcp_says` for `grep x` and for `ls \| foo` |
| 1 f | size attribute removed | 1 failed: `The_body_is_limited` |
| 1 g | busy catch removed | 1 failed: `A_busy_vault_is_503` |
| 2 a | no attachment check in `cd` | **survived first** (62/62). `UnderAsync`'s prefix and the write rules make the state unreachable except through a trash restore. Strengthened with `A_note_or_file_with_notes_below_its_path_is_not_a_folder` (`920ab0f`); then 1 failed |
| 2 b | a kept folder's marker not counted | 2 failed: the `Cd_takes_folders` rows for `/Work/Drafts` and its normalised spelling |
| 2 c | only `history` in the first stage counted | 1 failed: `History_takes_a_permit` |
| 2 fix | the label guard's console calls posted to `/api/consol` | 1 failed: `No_label_carries_user_input`, `Expected: OK Actual: NotFound` |
| 3 a | a backslash in double quotes escapes everything | web row `reads 'echo "a\nb"' …` failed |
| 3 b | one fixture expectation changed | the web row and C# `Lines_parse_as_the_fixture_says` failed |
| 3 c | `rm` added to the fixture's `allowed` | `Allowed_is_the_servers` failed |
| 3 c2 | `rm` removed from `refused` | `Refused_is_the_servers` failed |
| 3 d | `quote` forgets to escape `"` | **survived** with the brief's words: none holds both `'` and `"`. Strengthened with `it's "x" \ y` (`ae2c453`); a variant (escape `"` but not `\`) also survived, so `it's \` was added (`72fdad9`); both now fail |
| 4 a | option values taken as operands | 14 failed, among them `head -n 5 a.md`, `tail -n +3 a.md`, `cat --version v1 a.md`, `history --since 2h a.md`, `board --column Doing` |
| 4 b | defaults added right of a pipe | 3 failed: `cat a.md \| rg foo`, `cat a.md \| rg foo \| head`, `ls \| rg foo` |
| 4 c | semsearch's last word always a path | 2 failed: `semsearch how do plants grow`, `semsearch Tomaten plan.md` |
| 4 d | `sort` removed from the table | 2 failed: the agreement test, `cat a.md \| sort b.md` |
| 4 fix | the unknown-stage check reverted | 6 failed: `rm a.md`, `mv a b`, `unknown a`, `cat a.md \| rm x`, `ls \| unknown`, `ls \| toString` |
| 5 a | `dangerouslySetInnerHTML` for output | "shows a note's HTML as text" failed |
| 5 b | `persist` to `sessionStorage` | "browser storage > keeps no command, path or output" failed |
| 5 c | no generation check | 4 failed: the 401 test, and forgetting everything on sign-out, on the session ending and on another user signing in. Run again after the 20 ms sleep became a deterministic flush: the same 4 |
| 5 d | no reset on sign-out (no subscription) | the same 4 failed |
| 5 e | input not disabled while running | "posts once for two quick Enters and waits with the input disabled" failed |
| 5 f | the typed line sent instead of the resolved one | "sends the line with its paths made absolute, and shows what it ran" failed |
| 5 fix 2 | any `image/*` allowed again | the `image/svg+xml` row failed |
| final | the semsearch guard reverted | the `semsearch /Work` row failed |

Not mutated: the controller race fix in `ConsolePage.tsx` (`87c8df0`, item 1) has no test. In jsdom every event re-renders before the next, so the race can't be reached through the page; the fix makes `submit` read the store, the same source `run`'s guard reads.

## 6. Deviations and the calls I made

These are decisions where the brief, the plan or the spec left something open, or where they didn't fit the code.

1. **Two branches.** The session's harness named `claude/read-only-console-overnighter-5gw5op`, the brief `feature/read-only-console`. Both started at `a1d3c86`. I pushed every commit to both, and the pull request goes from `feature/read-only-console`.
2. **Pushes before the reviews finished.** The container is ephemeral and only pushed work survives, so I pushed each task's commits right after the implementer reported, before its reviews came back. No pull request was open then; fixes came as follow-up commits.
3. **The route is `/api/console/` internally.** The plan's mapping (group `/api/console` plus `MapPost("")`) registers the route with a trailing slash, as `GET /api/connections/` already is. So `DeliberatelyOpen` holds `"POST /api/console/"`. Clients post to `/api/console`, which works, and `schema.d.ts` lists `/api/console`, as the generator writes `/api/connections`.
4. **Wordings the brief left open:**
   - 400 for another call: "The console takes the calls sh and cd."
   - 400 without a field: "Give a command line (command)." and "Give a path (path)."
   - A line with several refused commands names the first.
   - `cd`'s error repeats the path as sent; success answers the normalised path.
   - Local help texts, `pwd x` and `clear x` giving `too many arguments`, and "Stopped, as the page was left." for a command aborted by leaving.
5. **The history permit** is taken after the read-only refusal: a refused line takes none. A line that doesn't parse takes none. A line with `history` in a later stage, which the shell then refuses, takes one, as the plan says.
6. **The empty folder in the `cd` test** is made with `PUT …/.vaultkeep`: the REST API has no `POST` that makes a folder; `FolderMarkerApiTests` does the same.
7. **`cd` on a single segment** is decided by the caller's vault list alone. So `cd /Work` on a vault being re-encrypted answers 200, not 503; a deeper path answers 503. Spec §6 lets the next command say it.
8. **The tokeniser's** unsupported-operator errors are not ported: a refused line goes to the server unchanged (spec §7), so the shell's own sentence answers.
9. **`tags` and `semsearch` follow their C# code** where it is more exact than the spec's table:
   - `tags`: a word starting with `/` is the path wherever it stands (`TagsCommand.cs`), so `tags /Work x` stays as typed.
   - `semsearch`: a single word is always the query, so `semsearch ./plan.md` searches for "./plan.md". `semsearch /Work` alone stays as typed, so the shell gives its "missing query" error (final fix wave).
10. **No default where it would be misread:** `rg` with no regex, `semsearch` with no query, and an option missing its value (`rg foo -m`). Such a stage stays as typed.
11. **A line whose any stage has a command the table doesn't know** goes out exactly as typed (review fix, spec §7).
12. **Page details:**
    - The welcome stays at the top; `clear` empties the commands below it.
    - The markers `> `, `→ ` and `! ` are `select-none` and `aria-hidden`, so a selected `→` line pastes as the bare command.
    - Errors carry a hidden "Error: " for screen readers.
    - Only `image/png`, `jpeg`, `gif` and `webp` become a `data:` image; anything else shows "(an image the console can't show)".
    - The page is `h-dvh`, with home-indicator padding as the app's sheets have.
13. **Review minors fixed beyond the loop's rule** (minors normally only go to the ledger): the label guard's status, and eight Task 5 points (the controller race, the server's real 429/503 sentences in the tests, `/Demo` → `/Work` in the help, `aria-hidden` markers, scroll after an image loads, safe-area padding, `||` for an empty `cd` answer, a deterministic flush instead of a sleep). They were cheap and touched what the hand test looks at.
14. **Kept beyond the brief:** two extra round-trip words in the tokeniser test, being the strengthening after mutation (d).
15. **The security document:** besides the console, it now says that `pdf` may store its derived PDF analysis, and that the shell's own metrics count console commands by name. Five stale references were corrected on the way: four in `PasswordWindowTests.cs`, and `NoteApi.cs:594` → `:653`.
16. **The `schema.d.ts` changes** are by hand, as the brief says.
17. **Commit trailers:** every commit has `Co-Authored-By: Claude Opus 5.5`; most also carry the session's `Claude-Session:` line, which the session's attribution asked for.

## 7. Security review

A fresh subagent got the diff `master...HEAD` at `6394969` and the plan's four questions, not my conclusions. It read the surrounding code and ran `ConsoleApiTests` (60/60).

**Answers:**
- **Can any line write?** No. The check is an allowlist over exactly the string the shell then runs, with the same parser and the same ordinal names. `"rm"`, `r\m`, `'r'm` and `rm --help` are refused. The allowed readers and their options, and `cd`, write nothing.
- **Logs or browser storage?** Nothing of a command or its output reaches a log or browser storage.
- **HTML?** Nothing is rendered as HTML.
- **Agents?** Nothing agents see changed.

**Overall:** no Critical, High or Medium finding.

| # | Severity | Finding | Done |
|---|---|---|---|
| 1 | Low | Only `history` is rate-limited. `diff --checkpoint` on a vault, `rg`/`tags`/`backlinks` on `/`, and `pdf` on a PDF not yet analysed run without a limit, as over MCP's `sh` | **Left for Chris.** Spec §5 limits only `history`; a permit for `diff --checkpoint` on a folder or vault, or a per-user console limit, would be a follow-up |
| 2 | Info | `pdf` can store the derived PDF analysis (`runIfPending: true`), as the background runner would | **Documented** in `docs/security-architecture.md` (§4, the console) |
| 3 | Info | `kumbu_sh_commands_total` and the command duration count console commands, names only | **Documented**; the MCP tool counter doesn't count them (tested) |
| 4 | Info | The 64 KiB limit is tested by its metadata only | **Left.** The plan says `TestServer` doesn't enforce it; the same attribute gives a real 413 on the upload endpoints |
| 5 | Info | The image check let `image/svg+xml` through | **Fixed** in `d4d3549`: raster only, with red, green and a mutation |
| 6 | Info | 500 scrollback entries holding images could take hundreds of MB in the tab | **Left for Chris** (spec §8 sets 500); dropping old images would be the fix |
| 7 | Info | Sign-out in one tab doesn't clear the console in another until it gets a 401, as for the rest of the app | **Left for Chris** |
| 8 | Info | JS `trim()` strips U+FEFF, .NET `Trim()` doesn't; only what the client shows can differ, the server re-checks what it gets | **Left**, not exploitable |
| 9 | Info | A `history` line takes a permit even when the shell then rejects the line | Intended and tested |

## 8. Surprises

- **Trash restore doesn't look below its path.** `AttachmentWriter.RestoreAsync`, and apparently the note restore, bring a note or file back over a path that now has notes below it, which writes otherwise refuse ("Is a directory"). This came up while strengthening Task 2's mutation (a), and the new test relies on it. If restore is fixed, that test needs another way to make the state. This predates the branch and wasn't changed.
- **`AccountApiTests.Recovery_revokes_every_api_key_and_replaces_the_used_recovery_key`** failed once in the Phase B gate with 409 instead of 200, on the second recovery right after the first. It passed alone three times and in the final run; it looks load-dependent.
- **`SearchSheet.test.tsx`** ("has the input and the Hybrid, Meaning and Exact chips at the bottom") failed once in a full web run under load, also on the unchanged tree; it passed alone and on rerun.
- **Two mutations survived the plan's own tests:** Task 2 (a) and Task 3 (d) (§5). Both tests were strengthened.
- **The pre-flight scan was right, but one test had no task:** spec §5's "`help rm` works" had no task's test, and was added in Task 2.
- **Stale references in the security document** that this branch didn't cause: `KumbuMetrics.cs:381–389` and `:439–491` in §7 Metrics, and `Program.cs:37–39` and `237, 239, 245` for `CallerMiddleware`, look a few lines off. Left alone.
- **Two remarks on the spec**, which wasn't edited:
  - §5 says a test classifies every command the shell registers. The test reads `help`'s list and `KumbuMetrics.KnownCommands`, because no `Shell` can be resolved outside a request, as the plan says. The runtime check refuses any unlisted command anyway.
  - §8's "the page scrolls to the newest answer unless the user scrolled up": each new command scrolls to the bottom again, so scrolling up holds only while a command runs or an image loads.
- **The session tooling:** a wait loop of mine matched its own command line with `pgrep` and never ended, so a second final run never started. The first run covered the final HEAD (§1), and I stopped the waiter.

## 9. Open points for Chris

**Check by hand first,** with `docs/test-playbook-console.md`:

1. **`img` and `pdf` with real attachments** (part G): a JPEG, a PNG and a PDF with a chart or a scanned page. Check that the image shows (the CSP and `data:` URLs), the page follows to the bottom after an image loads, and the sidecar-down sentences (step 30).
2. **A phone** (part I):
   - the input above the soft keyboard; on iOS Safari `h-dvh` may not shrink for it;
   - no zoom on focus, no autocapitalisation, the Go key;
   - the home-indicator padding;
   - long lines wrapping without sideways scroll.
3. **Scroll-follow, click-to-focus unless text is selected, and that `>` and `→` line up** in the monospaced font (part H). jsdom can't see any of these.
4. **Copying an error line:** the hidden "Error: " may come along (step 14). That is the one place where a copied line can differ from what an agent sees.
5. **Reload, sign-out and another user in the same tab,** and the browser's storage in dev tools (steps 39–42).
6. **A member of a shared vault** (part K).

**Decisions for you:**

7. A rate limit for `diff --checkpoint` and other whole-vault reads through the console (security §7, #1). The spec has only `history`.
8. Whether `cd /Work` on a vault being re-encrypted should answer 503 like deeper paths (§6, #7).
9. Scrollback memory with many images (security #6), and a sign-out signal across tabs (security #7).
10. The trash-restore gap (§8): a fix belongs in its own pull request.

**Smaller follow-ups the reviews left (minor):**
- `cd` repeats `ls`'s folder detection and lists everything below a path to test that it isn't empty (the same cost as `ls`).
- The path line isn't tied to the input with `aria-describedby`.
- The disabled input loses focus for a moment on each command; the plan asks for `disabled`.
- `tags sub x`, with the path typed first, becomes a runnable line instead of the shell's "too many arguments": the spec's "second operand", read literally.
- A few untested path rows (`a//b`, `rg -- foo a.md`).
- The `.md#` split is case-sensitive.
- The trailing-slash route name.

## 10. The pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/139, a **draft** from `feature/read-only-console` to `master`. Its description says that a security review applies and points here for the findings. It is not merged.
