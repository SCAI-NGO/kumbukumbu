# Overnighter report: Skills, a portable capability

Date: 2026-11-08. Brief: [2026-11-08-skills-brief.md](2026-11-08-skills-brief.md). Spec: [2026-11-08-skills-design.md](../specs/2026-11-08-skills-design.md). Plan: [2026-11-08-skills.md](../plans/2026-11-08-skills.md).

**Pull request:** PENDING: PR link (draft, base `feature/webdav`; Chris retargets it to `master` once #152 is merged).

**Status of this draft.** The coordinator fills the places marked PENDING. Tasks 0 to 44 are done and none was skipped. Task 45 (the security review) is done, its fixes are pending. Task 46 (the final gate) is PENDING. Task 47 (hand-off) is this report and the pull request.

**In short.**

- Branch `feature/skills`, 79 commits on top of `898cb9c` (3 for the plan and its review, 76 for the work), 215 files changed, 21,780 insertions, 442 deletions.
- Gates 1 to 4 passed. The last full figures (Gate 4, at `19f0c19`): .NET 4995 tests, 4988 passed, 1 failed, 6 skipped; the failure is a load-flaky test that passed 3 of 3 alone. Web 112 files, 1829 tests; web build green; pdf 8 files, 146 tests; litmus exit 0.
- The security review found 0 Critical, 4 Important and 6 Minor findings. A fix wave for 1, 2, 3, 4, 6, 7 and 10 is dispatched; its results are not in this draft.
- Nothing is merged, no tag, no version change.

## 1. Environment

- Cloud container, Linux (Ubuntu), 4 cores.
- **.NET SDK 10.0.403** (preinstalled). `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Installed by hand for the run** (nothing added to the project):
  - Node **26.13.0** into `/opt/node26`, first on `PATH` (the preinstalled one was 22.23.4);
  - pnpm **10.32.1** with `npm -g`;
  - **litmus 0.13** with `apt`.
- **Docker**: `dockerd` was not running at the start; it was started by hand (`nohup dockerd`). The shell's hard `nofile` limit is 20,000 and cannot be raised.
- **MongoDB: the file-handle workaround.**
  - The first Task 0 .NET run used an own `mongo:7.0` container with `--ulimit nofile=20000`. After about 3 minutes mongod crashed with WiredTiger `errno 24` (too many open files), and 93 tests failed, all with Mongo connection errors.
  - The fix, used for every later run: mongod started with `--wiredTigerEngineConfigString "file_manager=(close_idle_time=30,close_scan_interval=10,close_handle_minimum=250)"`, so idle files are closed. The container is `kumbu-test-mongo` (replica set `rs0`, host port 27018), with `KUMBU_TEST_MONGO=mongodb://127.0.0.1:27018/?replicaSet=rs0&directConnection=true` and `TESTCONTAINERS_RYUK_DISABLED=true`. Every gate started from a fresh container. The plan's Task 0 and gate steps carry the same command.
  - litmus ran against a second container (`kumbu-litmus-mongo`, port 27019) and the built `Kumbu.Server.dll`, as `.github/workflows/ci.yml` does.
- **Skills (the tool kind):** the superpowers skills were available and used. The run followed subagent-driven development (section 2).
- **Models:** the coordinator and most implementers ran on the Opus model; part of the implementers ran on a Sonnet model (see "Commit trailers" in section 9). The security review was a fresh read-only subagent on the most capable model.

## 2. The process

Subagent-driven development, from the plan:

- **A fresh implementer per task**: red test first (a few tasks did not observe red, listed in section 9), green, commit, then every mutation on its own, restored with `git checkout`.
- **Two fresh reviewers per task**, in parallel: one for spec compliance, one for code quality. The skill asks for one task reviewer; the brief asks for two, and the ledger records that ruling.
- **Scoped re-reviews** of a fix diff only, after each fix round. Fix rounds were limited to 5 per task; no task needed more than 2 (Task 36).
- **Findings were triaged**: Critical and Important go to a fix round; Minor findings are written to the ledger (`.superpowers/sdd/2026-11-08-skills/progress.md`) as "minor (deferred)", then read again by the security review.
- **Fix rounds happened** for Tasks 3, 7, 8, 9, 10, 11, 14, 17 (with the Gate 1 failures), 21, 22, 28, 31, 36 (two rounds), 37, 40 (a second, for the marketplace switch), 42 and 44. A cleanup round followed Task 39 (Minors only) and a follow-up Task 30 (the `.zip` link). Every other task was clean at first review.
- **Parallel implementers** on disjoint files for Tasks 14 and 16, and Tasks 28, 29 and 30. Each committed only its own paths.
- **Gates** after each phase: a fresh Mongo container, the whole .NET suite in Release as CI does, the web and pdf suites and builds, litmus. Failing tests were run alone 3 times to tell flaky from broken.
- **The coordinator** did all pushes with an explicit branch check, after the detached-HEAD incident (section 9).

## 3. The plan review

A fresh subagent reviewed the spec, the brief and the first draft of the plan. It made 22 findings; **all 22 were accepted** and folded into the plan (commits `2afc01f`, `f6fa1f6`, `7fec6c3`). The ledger records that this review replaced the pre-flight conflict scan.

| # | Finding, and what the plan did | Tasks |
|---|---|---|
| 1 | Archives were cached for an hour per commit: only the listing is cached per commit; archives sit in a byte-capped LRU (200 MB, at most 4); GitHub fetches limited per user | 36, 38 |
| 2 | Commits and repository names were not validated enough: every commit, also the resolved one, must match `^[0-9a-f]{40}$`; owner and repo through `RepoName.Parse`; URLs only from validated, escaped segments | 35, 38 |
| 3 | The manifest's `name` could steer the `.zip` root, file name and a copy's default name: all come from the folder's name; `Content-Disposition` with `filename*`; `../evil` tested | 21, 29, 31, 38 |
| 4 | Archive reading had no bounds: decompressed bytes counted, an entry over `AttachmentFormat.MaxBytes` refused before allocating, every entry counts toward 5000, GitHub JSON capped at 1 MB, a 2 GB tar header tested | 35, 36 |
| 5 | Copying text files out of a skill location was only refused for moves: refused in `TreeWriter.CopyAsync` too; the store's last check covers restores; a WebDAV COPY test | 4, 5 |
| 6 | (a) `FollowAsync` returned early for a record without publications, so skill state would not follow a move; (b) removing `SKILL.md` or moving a shared skill out by any writer must withdraw the link; (c) a published note moved into a skill place is withdrawn, older ones stay and cannot be updated | 8, 27 |
| 7 | The update check could trust a frontmatter: a sealed copy of each copy's origin is kept in the vault's record; a disagreeing frontmatter reads as "origin edited" and nothing is fetched | 31, 38, 40, 41 |
| 8 | Old sync clients would receive text files: only with `text=1` | 6 |
| 9 | A shared vault's skill could pass as one's own: "shared by" on every skill of a shared vault; a folder name that breaks the name rules is quoted; Unicode Cc and Cf characters are stripped from names and descriptions | 13, 17 |
| 10 | The real HTTP handlers went untested: the `github` and `cimd` named clients are built from a service collection of their own and sent to `https://localhost:1/`; the handler's flags are read through `IHttpMessageHandlerFactory` | 33, 35 |
| 11 | `skills find` ranking: one search by meaning over the root scope, hits split by vault before ranking; anchor `SectionSearch.cs:187-202` | 15 |
| 12 | A second link for a skill: none while one exists; Withdraw only; Share anew afterwards with a warning (an open point for Chris) | 28 |
| 13 | Decision 8's side effect (a folder named like a text file inside a skill folder) written down; `Activity.cs:183` among `KindOf`'s callers | 1 |
| 14 | The plain-text viewer moved into the web task of Phase 1 | 7 |
| 15 | Images in `SKILL.md` on `/k` show as their alt text only | 30 |
| 16 | `VaultCatalog.NormaliseVaultName` made internal and reused; the alias rename's re-keying refusal caught like `VaultBusyException` | 10 |
| 17 | The handshake build counter is a singleton service; the use of `HttpContext.Items` is commented | 17 |
| 18 | One request resolves the commit (`commits/HEAD` with `Accept: application/vnd.github.sha`), validated | 35 |
| 19 | Targets may be any vault the user may write; a member's sealed state goes into that vault's record | 21, 31, 38 |
| 20 | Every command written out in full; security tests with full bodies; no "same as above" | many |
| 21 | Large tasks split as proposed; tasks renumbered; phases and gates kept | plan |
| 22 | Anchor fixes (`vite.config.ts:55`), the litmus gate starts the built dll so the pid is the server's; `KUMBU_GITHUB_TOKEN_FILE` only documented for a `compose.override.yaml` | 30, 34, gates |

Coordinator rulings on the review (from the notes): 6b withdraws the link and does not refuse; 6c leaves pre-existing note publications live, not updatable, with an open-items row; 7 keeps a sealed origin copy in the vault record, a mismatch reads "origin edited" and fetches nothing; 12 allows no second link while one exists; 19 allows member targets with the sealed state in that vault's record.

## 4. Task 0: planned against measured

Measured at `898cb9c`, before any change.

| | Planned (WebDAV Gate 7 at `8f2a3ae`, measured again at `898cb9c`) | Measured |
|---|---|---|
| .NET total | 4407 | 4407 (Server project 10 min 32 s, Core 5.6 s) |
| .NET succeeded | 4401 | 4401 |
| .NET failed | 0 | 0 |
| .NET skipped | 6 (4 need the model and a TensorX key, 2 skip as root) | 6 |
| Web | 100 files, 1732 tests, build green | 100 files, 1732 tests passed, build green |
| pdf | 8 files, 146 tests, build green | 8 files, 146 tests, build green |

The figures equal the known state. The first .NET attempt crashed on the Mongo file-handle limit (section 1); the figure above is from the rerun with the workaround. The coordinator's notes do not record a litmus run at Task 0 (litmus ran at each gate; section 6).

## 5. Per task: commits and test output

"Tests" gives the counts from the task report, taken from the implementer's own runs (Debug unless stated). The filter classes are in the reports under `.superpowers/sdd/2026-11-08-skills/`. Tasks 19, 25, 32 and 43 are the phase gates (section 6), Tasks 45 to 47 are in sections 7 and 9.

### Phase 1: Core

| Task | Commit(s); fix | Tests |
|---|---|---|
| 1 Skill paths and the text kind | `927cc04` | Core: 57 in the filtered classes; whole Core project 1658 total, 1656 passed, 2 skipped. Coordinator's server run after it: 2775 total, 2771 passed, 0 failed, 4 skipped |
| 2 The vault kind | `04bdcb5` | `SkillsVaultKindTests` 4/4; `UserVaultTests`, `VaultRenameTests`, `RekeyTests` 120/120 |
| 3 Text files in the core writers and readers | `81ad1a5`; fix `ab9fe1b` | `TextAttachmentTests` 9/9, with neighbours 115 passed; after the fix 166 total, 0 failed |
| 4 Text files through API and WebDAV | `1b730ec` | `TextFileChannelTests` + `DavSkillTextTests` 12/12; regression classes 206/206 |
| 5 Moving and copying text files out | `6aa4a5f` | `SkillFolderMoveTests` and `FolderMoveTests` 17 passed; DAV and store classes 17 passed after the extra tests |
| 6 Text files and sync clients | `085d55a` | `SyncApiTests` 20 total, 0 failed; `runImport.test.ts` 29 passed |
| 7 Text files in the web app | `f234e95`; fix `2eaf4e9` | web 101 files, 1754 tests, then (after the fix) 102 files, 1755; build green |
| 8 The vault Skills stays fixed; skill places not published | `c32ee30`; fix `1818831` | 118 total, 0 failed; web 102 files, 1757; after the fix 79 total, 0 failed |
| 9 Every user gets a vault Skills | `4447726`, `44684b1`; fix `7d49474` | `SkillsVaultCreationTests` 7, then 9; full server project 2814 total, 2810 passed, 0 failed, 4 skipped (81 failures before the vault-list pins were adjusted); after the fix 13/13 with `DfServerTests` |
| 10 The name clash and its notice | `4459e8c`, `4ac8ed4`; fix `0034fdd` | `SkillsVaultClashTests` 12/12; related classes 64/64 and 108/108; after the fix 139/139; web 102 files, 1757 |
| 11 `SKILL.md`: rules, origin fields, hash | `48129ae`, `9fc6471`; fix `c5f0cb2` | 23 total; after the fix 141/141 |
| 12 System skills and `create-skill` | `97afe67` | `SystemSkillsTests` 5/5; Release build 0 warnings, 0 errors |
| 13 The skill directory | `fecb961` | `SkillDirectoryTests` 25/25; with neighbours 51/51 |
| 14 The `skills` command | `ea3a274`; fix `3447d79` | Core 71/71, server 32/32; after the fix Core 101/101, `SkillsShellTests` 4/4 |
| 15 `skills find` | `e63f920` | Core 45/45; `SkillsShellTests` 4/4; search classes 91/91 |
| 16 Instructions per key and connection | `4d213cd` | 105 total, 0 failed; web 102 files, 1757, build green |
| 17 MCP instructions per connection | `f0db281`; fix `8216abe` | Core 33/33 and 206/206; server 57/57; after the fix 220/220; web 103 files, 1767 |
| 18 Skills preselected; the rename notice | `82af5d8`, `65d1ae7`, `ecb24ea` | web 1766 passed; build green |

### Phase 2: The Skills screen

| Task | Commit(s); fix | Tests |
|---|---|---|
| 20 Skills API: list and find | `f584cdc`, `20f73de` | `SkillsApiTests` 12/12; with neighbours 47 passed; build green |
| 21 Copy to… and Move to… | `0f5f2d0`; fix `71052a1` | `SkillCopyTests` 15/15; with neighbours 112/112; web 103 files, 1767; after the fix 141/141 and `SkillCopyTests` 20/20 |
| 22 The Skills screen, Installed tab | `4ac75a1`; fix `75b822d` | web 105 files, 1776; after the fix 1781; build green |
| 23 The Try tab | `98aed59`, `c66ade2` | web 106 files, 1789; build green |
| 24 The instructions setting in Access | `e251252` | web 105 files, 1785; build green |

### Phase 3: Sharing by link

| Task | Commit(s); fix | Tests |
|---|---|---|
| 26 Skill publications | `7277209` | `SkillPublicationTests` 11/11; neighbours 126/126 and 24/24 |
| 27 Skill links follow and withdraw | `360133d`, `22660bb` | `SkillLinkWithdrawalTests` 12/12; neighbours 140/140 |
| 28 Sharing from the Skills screen | `70d85ec`; fix `372a969` | 133 total, 0 failed; web 107 files, 1794; after the fix 148 and 76 total, 0 failed; web 109 files, 1806 |
| 29 The `/k` endpoint, route and `.zip` | `3326be8` | 69/69; committed tree in a clean worktree 177/177 |
| 30 The `/k` page | `c283390`, `8155217`; follow-up `3049069`, `0c2e65b` | `SkillPageApiTests` 35/35; web `src/skillpage` 11 passed; whole web 109 files, 1805, then 1809 after the follow-up; build green, `k.html` present |
| 31 Add to my skills | `0944ea7`, `3147740`; fix `7c71a4f` | `AddSkillTests` 11/11; related 136/136; web 110 files, 1812; after the fix 40 total, web 1813 |
| Gate 2 registry fix | `c52cb4b` | `PasswordWindowTests` 70/70 |

### Phase 4: Marketplaces

| Task | Commit(s); fix | Tests |
|---|---|---|
| 33 The shared address helper | `2375ec7` | 160 total, 0 failed |
| 34 Skills options and configuration | `be65122` | 81 total, 0 failed |
| 35 The GitHub client: requests | `4454a87` | 102 total, 0 failed; 12/12 for the factory change |
| 36 The GitHub client: archives, bounds, caches | `7c43d75`, `42c103a`; fixes `34a43d1`, `9cb5eeb` | 45 total; round 1: 57 (93 with the index tests); round 2: 68 (111 with the index tests). Red before round 1: 7 of 27 failed; before round 2: 9 of 39 failed |
| 37 The marketplace index | `e65b9d2`, `d1f76a1`; fix `2cb5db8` | 36 total; after the fix 43/43 |
| 38 Marketplaces API and install | `3c0cb3c`, `9b3f2b6` | 22/22; with neighbours 217/217; web 110 files, 1813 |
| 39 The Marketplaces tab | `25079ee`; cleanup `75e1280` | web 6 passed in the file; whole 111 files, 1819; build green |
| 40 The update check | `05aa027`; fixes `d784fe5`, `877b7bc` | 14 total; related 296/296; after round 1: `SkillUpdateCheckTests` 16/16, `SkillsShellTests` 5/5, `PasswordWindowTests` 70/70; after round 2: 18/18, `SkillUpdateTakeTests` 9/9, `MarketplaceApiTests` 22/22 |
| 41 Review, take and skip | `d7cc0d9` | 9 of 9 red; green 95/95 with the check and window tests (93/93 in a clean worktree) |
| 42 Updates in the web app | `19f0c19`; fix `2a7c753` | web 112 files, 1827; after the fix 1829; build green |
| Gate 4 and the switch | (Task 40 round 2) `877b7bc` | see Task 40 |

### Phase 5: Documentation and review

| Task | Commit(s); fix | Tests |
|---|---|---|
| 44 Documentation | `a5ba3b8`; fix `67720ab` | `WelcomeTextTests` red 2 failed of 10; green with neighbours 30/30; after the fix 20/20 |
| 45 Security review | none (read-only) | report: `.superpowers/sdd/2026-11-08-skills/task-45-security-review.md` |
| 46 Final gate | PENDING | PENDING |
| 47 Hand-off | this report, the pull request, an open-items row | PENDING |

## 6. Phase gates

| Gate | At | .NET (total / passed / failed / skipped) | Web | pdf | litmus |
|---|---|---|---|---|---|
| 1, run 1 | `f0db281` | 4650 / 4640 / 4 / 6 | 103 files, 1766, build green | 8 files, 146, build green | exit 0 |
| 1, rerun | `8216abe` | 4660 / 4653 / 1 / 6 | 103 files, 1767, build green | 8 files, 146, build green | exit 0 (at `f0db281`; the fix touched no DAV code) |
| 2 | `c66ade2` | 4692 / 4685 / 1 / 6 | 106 files, 1789, build green | 8 files, 146, build green | exit 0 |
| 3 | `3147740` (web and pdf against the tree including `7c71a4f`) | 4855 / 4849 / 0 / 6 | 110 files, 1813, build green | 8 files, 146, build green | exit 0 |
| 4 | `19f0c19` | 4995 / 4988 / 1 / 6 | 112 files, 1829, build green | 8 files, 146, build green | exit 0 |
| 5 (final, Task 46) | PENDING | PENDING | PENDING | PENDING | PENDING |

Every Release build had 0 errors and 0 warnings. litmus exit 0 means its failures are exactly the 21 in `tools/litmus/expected-failures.txt`.

What failed at the gates:

- **Gate 1, run 1**: four failures, all real and fixed in `8216abe` (Task 17's fix round):
  - `MembershipApiTests.Last_use_is_the_use_on_this_vault_not_the_keys_global_one` (deterministic 3 of 3): the handshake's skill listing stamped `usedAt` on shared vaults. Fix: `EncryptedNoteStore.CountsUse`, switched off around the listing.
  - `PasswordWindowTests` registry: the three new endpoints (instructions on keys and connections, dismissing the notice) needed a classification.
  - `ConsoleApiTests`: `skills` was not classified for the console; `skills copy` is refused there.
  - `ToolMetricsTests`: `skills` joins `KnownCommands`.
- **Gate 1 rerun**: one failure, `RekeyTransitionTests.A_run_cancelled_in_its_transaction_completes_the_transition(inside-finish)`, a 1 min 01 s timeout under load (an implementer was running tests at the same time). The class alone ran 3 times: 12/12 passed each time. Load-flaky, not a regression.
- **Gate 2**: `PasswordWindowTests` registry for the four `/api/skills` endpoints; fixed in `c52cb4b` (70/70). A full rerun was left to Gate 3, because the only change was a test registry entry.
- **Gate 4**: `PairingTests.A_device_name_gets_the_same_answer…(dev)` failed once; the class alone 3 times: 55/55 each time. Load-flaky (Task 40 and 44 implementers were running tests).
- **Gate 4 network check**: `github.com` is named only as `api.github.com` and `codeload.github.com` in `GitHubClient.cs`, plus a doc comment in `KumbuOptions`; `PasswordPolicy.cs`'s older source URL is a comment, no request.

## 7. The security review

Task 45: one fresh subagent on the most capable model, read-only, scope `git diff 898cb9c HEAD` at `a5ba3b8` with `877b7bc` and `67720ab` on top. It checked each spec §2 anti-goal, each spec §5 point, the brief's "things that matter" and `docs/security-architecture.md` §4 "Skills", with probes in a scratch worktree (since removed). Full report: `.superpowers/sdd/2026-11-08-skills/task-45-security-review.md`.

Result: **0 Critical, 4 Important, 6 Minor.** The coordinator ruled on each; a fix wave for 1, 2, 3, 4, 6, 7 and 10 was dispatched.

| # | Severity | Finding | What became of it |
|---|---|---|---|
| 1 | Important | `GET /api/skills/zip` bypasses the download ticket, the member-export event and every size bound (a remembered session could zip notes moved into a skill folder without the password; a member's export left no event; unbounded memory). The same unbounded source read feeds Copy and Move | Fix wave dispatched: ticket and password-window rule as the files ZIP, member export recorded, 200 files / 10 MB limits before reading, the same pre-read limit in `SourceAsync`. PENDING: fix wave results |
| 2 | Important | A `kumbu-source` written into frontmatter without a sealed origin is shown as "from github:…" in `skills ls`, `find`, the handshake list and the Skills screen, so a forged provenance looks verified | Fix wave dispatched: show "from" only when the sealed origin agrees, otherwise "claims source …, not verified"; correct the security-architecture paragraph. PENDING: fix wave results |
| 3 | Important | "Last changed by" and "another user" look at `SKILL.md` only; a member's change to the files the skill sends the agent to goes unmarked | Fix wave dispatched: compute over every file of the folder; "changed since taken" for copies. PENDING: fix wave results |
| 4 | Important | No server-wide bound on archive downloads: one user could drive gigabytes into memory with many commits (preview goes straight to codeload without resolving a commit) | Fix wave dispatched: server-wide concurrency and byte budget, one download per user in flight, cancel detached downloads, accept only commits this server resolved for that repository. PENDING: fix wave results |
| 5 | Minor | An installed copy's `github:owner/repo@commit` does not prove the commit belongs to the repository (a fork's commit under the parent's name) | Closed by finding 4's fix (only server-resolved commits accepted). Ruling in the ledger |
| 6 | Minor | `SkillCopier.Inside` and the update taker's joins do not refuse `..`, `.` or empty segments; `SkillZip.Plain` lets `C:` and control characters through. Not reachable today, since all inputs are sanitised upstream | Fix wave dispatched: the guard `SkillsCommand.Inside` has, plus a "strictly below the folder" assertion; `Plain` refuses `:` and Cc/Cf. PENDING: fix wave results |
| 7 | Minor | Take update skips the install limits, and a failed Take leaves a mixed folder that still reads "current" | Fix wave dispatched: install limits in `UpstreamAsync` (409 shown in Review); put back or mark "update incomplete". PENDING: fix wave results |
| 8 | Minor | Every MCP handshake touches every vault the credential reaches, which keeps their keys in memory | Accepted as specified (spec §3.6: "unlocks the connection's vaults at initialisation, as its first tool call would"). Ruling in the ledger. Cost if wrong: vault keys stay in memory for clients that reconnect often |
| 9 | Minor | The Add-to-my-skills code stays in the tab's `sessionStorage` if she leaves `/skills/add` without Cancel or Add | Parked: clearing it on unmount would break the sign-in hand-over; the tab's `sessionStorage` dies with the tab. Ruling in the ledger |
| 10 | Minor (privacy) | A shared skill's images keep EXIF/GPS metadata (verbatim by spec §3.7) | Not a code fix without a spec change; recorded in `open-items.md` for Chris. The fix wave adds a note in the Share dialog where a skill holds JPEG/PNG/WebP files. PENDING: fix wave results |

Everything else the review checked held: connection visibility, agents cannot bring in, share, move across vaults or update (the routes are session-only; `skills copy` copies system skills only), no change without the user's review, the fetch rules (two hosts, https, no redirects, public addresses only, the token to `api.github.com` only, the tar guard and caps), the share code leaking nowhere, text files only in skill folders and never rendered, path traversal at the sources, migration grants, `/k` isolation, re-keying races on the sealed record, and the password-window classification (except finding 1). The encryption scheme and slot derivations are unchanged.

The review also triaged the ledger's deferred Minors: those it called security-relevant are covered by findings 1, 4, 6, 7, 8 and 10; the rest are test strength, duplication, messages and UI polish.

### PENDING: fix wave results

PENDING: the coordinator fills this subsection. For each of findings 1, 2, 3, 4, 6, 7 and 10: the commit(s), the test that failed first, the test output, the mutations of the fix, and the re-review. Also the re-run of the documentation changes for `docs/security-architecture.md` §4 (findings 1, 2, 4) and the final gate's figures (Task 46).

## 8. Every mutation

Each mutation was applied alone, the named test class run, then the file restored with `git checkout --`. "Survived" marks a mutation that no test caught at first; what the task did about it follows. Test names are given as in the reports.

### Task 1 (`SkillPathsTests`, `AttachmentFormatTests`)

1. `_skills` matched case-insensitively: `FolderOf_finds_the_skill_folder("/Work/_Skills/x/a.py")` fails.
2. `_skills` accepted below the vault root: `FolderOf_finds_the_skill_folder("/Work/notes/_skills/x/a.py")` fails.
3. Text admitted at a location's root (`LocationOf` instead of `IsInSkillFolder`): `Elsewhere_nothing_changes("/Skills/run.py")` and `A_text_file_can_be_renamed_only_within_skill_folders` fail.
4. NUL check dropped: `Text_is_utf8_without_nul` fails.
5. Extension-less names all text: `Elsewhere_nothing_changes("/Work/_skills/scripts")` fails.

### Task 2 (`SkillsVaultKindTests`)

1. `BsonIgnoreIfNull` dropped: `A_vault_created_as_skills_carries_the_kind_in_its_document` fails.
2. Loading ignores `doc.Kind`: `The_kind_survives_loading_the_vault_again` fails.
3. `SkillsVaultAsync` by name: `SkillsVaultAsync_names_the_marked_vault_only` fails.

### Task 3 (`TextAttachmentTests`)

1. Mark check dropped: `An_unmarked_vault_named_Skills_keeps_no_text` fails.
2. `Refuse` removed in `AttachmentWriter`: `Write_refuses_a_read_only_vault` fails.
3. `NotText` text branch off: `Edit_points_a_text_file_to_write` fails.

The fix round (`ab9fe1b`) was red-first: `Write_errors_name_write_not_upload`, `A_long_text_file_is_cut_by_the_output_limit_never_outlined`, `Cat_marks_a_text_file_another_user_wrote_last` failed before it. No mutation list was recorded for the fix.

### Task 4 (`DavSkillTextTests`, `TextFileChannelTests`)

1. `ForName` back in `DavWrites.cs:59`: 3 `DavSkillTextTests` fail (415 instead of 201).
2. `.html` served as `text/html` in `FileApi.ServeAsync`: `Markup_in__skills_is_served_as_plain_text(index.html)` fails.
3. Store check disabled: `The_store_refuses_a_text_file_outside_a_skill_folder` fails.

### Task 5 (`SkillFolderMoveTests`, `DavSkillTextTests`, `TextFileChannelTests`)

1. Name only the first file (`.Take(1)`): `Moving__skills_itself…`, `Six_or_more…`, `A_skill_folder_moved_out…` fail.
2. Folder-move check skipped: the same three fail.
3. Check skipped in `TreeWriter.CopyAsync`: **survived** (a single file is already refused by `RenameProblem`). Test `Copy_of_a_folder_with_a_script_out_of__skills_is_refused_naming_it` added; it fails under the mutation.
4. "and N more" cut dropped: `Six_or_more_files_are_named_five_and_counted` fails.
5. Store check without the moves part: `The_store_refuses_to_move_a_text_file_out_of_a_skill_folder` fails.
6. Store check without the restores part: `The_store_refuses_to_restore_a_text_file_to_a_place_outside_a_skill_folder` fails.

### Task 6 (`SyncApiTests`)

1. `text` ignored (`withText = true`): `Old_clients_never_see_text_files` fails.
2. Changed-path filter dropped (current files only): `Old_clients_never_see_text_files` fails at the deletion assertion.

### Task 7 (web)

1. `_skills` matched case-insensitively: `isSkillTextFile(Work, _Skills/x/a.py) is false` fails.
2. Place check dropped (a fixed "Skills" vault): "skips a script picked elsewhere" and "skips a script without a place" fail. A first variant (place ignored, vault "Work") only failed "sends a script picked into a skill folder" and the 15 MB test.
3. `dangerouslySetInnerHTML`: "shows an HTML file as its text, never rendered" and the SVG test fail.

### Task 8 (`SkillsVaultFixedTests`, `SkillFolderPublishTests`)

1. Delete guard dropped (NoteApi): `The_vault_Skills_cant_be_deleted` fails.
2. `RequireNotInSkillPlace` dropped from `PublishAsync`: `A_note_in__skills_isnt_published_by_the_web_app` and `…_by_sh` fail.
3. Skills ids dropped from `SetVaultsAsync`'s `PublishByVaultId`: `May_publish_isnt_given_for_Skills` fails.
4. Invitation guard: dropping either guard alone does **not** fail (the two are redundant by design and answer the same 409); dropping both fails `The_vault_Skills_cant_be_shared_with_members`. No test change.
5. Withdrawal in `FollowAsync` disabled: `A_published_note_moved_into__skills_is_withdrawn` fails.

Fix round (`1818831`): with `vault.Kind == VaultKinds.Skills ||` removed from `InSkillPlace`, `A_note_in_the_vault_Skills_isnt_published_by_the_web_app` and `…_by_sh` fail; with the check removed from `UpdateAsync`, `An_old_publication_in__skills_cant_be_updated` fails; `A_published_note_doesnt_take_an_image_from__skills` failed before the `MayCopy` fix (a real leak, section 9).

### Task 9 (`SkillsVaultCreationTests`)

1. Semaphore and the restart on `VaultNameTakenException` dropped: **survived** all 3 runs with the brief's tests (the slot's unique name index already keeps one vault). Two tests added in `44684b1`; they then failed every time (`Calls_at_once_for_one_user_create_once_and_none_fails`: "Expected: 1 Actual: 3"; `Another_process_that_wins_the_name_counts_as_done`). Semaphore alone fails the first; restart alone fails the second. Re-checked with the 250 ms wait of the fix round, 3 runs.
2. `EnsureAsync` rethrows: `Sign_in_never_fails_when_creating_Skills_fails` fails (500).
3. `GET /api/session` hook skipped: `A_kept_session_gets_it_on_GET_session` fails.

### Task 10 (`SkillsVaultClashTests`, `VaultRenameTests`)

1. Clash compared case-sensitively: the Theory fails in all 4 spellings.
2. `catalog.RenameVaultAsync` called directly: `The_rename_drops_open_pairing_codes` fails.
3. `skillsAt` set before the rename: `A_vault_being_rekeyed_is_left_for_next_time` and `An_alias_of_a_vault_being_rekeyed_is_left_for_next_time` fail.
4. `skillsAt` set when the alias rename is refused: the alias test fails earlier than expected.
5. Notice stored in the clear: 10 tests fail, but `Nothing_names_the_old_vault_in_the_clear` **survived** (the stored JSON shows the field as base64). Strengthened in `4ac8ed4` to decode the bytes; it then fails.

Fix round (`0034fdd`), eight mutations:

- a. `NoticeAsync` without the broad catch: `A_notice_that_cant_be_read_is_no_notice_and_fails_nothing` fails.
- b. Neither catch: that test and `A_notice_that_doesnt_open_is_no_notice` fail.
- c. Notice written after the rename: `A_rename_whose_answer_is_lost_after_it_landed_still_leaves_the_notice` and `A_refused_retry_keeps_the_notice_of_the_attempt_before` fail.
- d. A refusal unsets the notice: `A_refused_retry_keeps_the_notice_of_the_attempt_before` fails.
- e. A refusal leaves the new notice: both re-keying tests fail.
- f. No `hub.AliasRenamed`: `The_sockets_on_the_renamed_alias_name_it_so` fails.
- g. No `hub.VaultRenamed` after a known rename: `The_sockets_on_the_renamed_vault_are_told` fails.
- h. Helper filter without the exact-`ArgumentException` exclusion: `A_slot_not_found_answers_not_found_and_takes_nothing_along` fails.

### Task 11 (`SkillManifestTests`, `SkillHashTests`)

1. Uppercase allowed: `Bad_names_are_named("Write-As")` fails.
2. Folder check skipped: `Bad_names_are_named("other")` fails.
3. No length prefix in the hash: **survived** the brief's test (the 0 separator already disambiguates). `A_file_boundary_is_not_ambiguous` added; it fails, with the pinned test.
4. Sort dropped: `The_hash_is_order_independent` fails.
5. `WithOrigin` rewrites frontmatter: both `Origin_fields_*` tests fail.

### Task 12 (`SystemSkillsTests`)

1. `EmbeddedResource` dropped: `Create_skill_is_built_in` (and two text tests) fail.
2. 1208-character description: `Every_system_skill_follows_the_rules` fails.

### Task 13 (`SkillDirectoryTests`)

1. Marked vault found by name: `An_unmarked_vault_named_Skills_holds_no_skills` fails.
2. `ByOther` dropped from `Line`: `A_skill_of_another_member_says_so` fails.
3. "shared by" only with `ByOther`: `A_shared_vaults_skill_says_so_even_when_he_last_changed_it` fails.
4. Quoting skipped: `A_name_that_breaks_the_rules_is_quoted` and `Control_and_format_characters_are_stripped` fail.
5. Strip only Cc: `Control_and_format_characters_are_stripped` fails.
6. A broken manifest throws: the no-frontmatter, bad-yaml and tab-metadata rows fail (and 4 other tests).

### Task 14 (`SkillsCommandTests`)

1. `copy` takes a user's skill: `Copy_refuses_a_users_skill` fails.
2. `caller.Refuse` skipped: `Copy_refuses_a_read_only_vault` fails.
3. `install` answering "not available": `No_subcommand_brings_in_shares_moves_or_updates(install)` and `An_unknown_subcommand_points_to_the_Skills_screen` fail.

Fix round (`3447d79`): no `Clean` in `check` fails `Check_cleans_what_a_manifest_says`; `Inside` without the `.`/`..` check fails `Cat_by_name_stays_in_the_skills_folder`; `copy` refusing `system:` fails `A_built_in_skill_has_an_address_of_its_own`.

### Task 15 (`SkillSearchTests`, `SkillsCommandTests`)

1. `HeadWeight` 2 to 1: `Name_and_description_outrank_the_body`, `The_score_is_the_fused_rank`, `Find_prints_each_hit_on_a_line_with_its_score` fail.
2. One meaning list over all vaults by raw cosine: `Each_vaults_hits_rank_on_their_own` fails.
3. Hits of other notes kept: `A_skill_in_no_list_isnt_returned` fails.

### Task 16 (`InstructionsModeTests`)

1. Written into sealed meta: `Hint_only_is_stored_in_the_clear` and `List_removes_the_field` fail.
2. Device key accepted: `A_device_key_has_no_such_setting` fails.
3. Password window added to the key route: `It_needs_no_password_window` fails.

### Task 17 (`HandshakeInstructionsTests`, `SkillInstructionsTests`)

1. Built for every request (id check dropped): `A_tool_call_builds_nothing` fails ("Expected: 2, Actual: 5").
2. Match only `initialize`: `Server_discover_carries_the_section` and four more fail (the SDK 2.3.0 client opens with `server/discover`).
3. Mode ignored: `Hint_only_sends_no_list` fails.
4. `MaxEntries` dropped: `Fifty_entries_then_how_many_more` fails ("Expected: 50, Actual: 56").

Fix round (`8216abe`): the old catch `e is not OperationCanceledException` fails the timeout test (`McpProtocolException`); no timeout fails it after 60 s ("Initialization timed out"). The `CountsUse` fix was red-first (`Initialising_doesnt_move_a_vaults_last_use` failed with a non-null `usedAt`).

### Task 18 (web)

1. Preselect by name: "preselects by the mark, not the name" fails.
2. "write" preselected: 4 key tests fail.
3. Skipping the touched guard: **survived** (the refetch returned identical data). Test strengthened in `ecb24ea` (a changing list, wait for it); it then fails.

### Task 20 (`SkillsApiTests`)

1. Revoked key counted in `WithoutSkills`: `Names_keys_and_connections_without_Skills` fails.
2. `as` ignored: `Find_as_a_key_sees_only_its_vaults` fails.
3. Device keys counted: `Names_keys_and_connections_without_Skills` fails.
4. `CountsUse` left true: **survived**; no test can fail, because a session store never stamps a key's slot. The test `Listing_as_a_credential_leaves_its_last_use_alone` stays as a pin, not a discriminator.

### Task 21 (`SkillCopyTests`)

1. Cleanup on failure skipped: `The_quota_applies` fails.
2. Source removal skipped: `Move_leaves_the_source_in_its_trash` fails.
3. Code left in the source record on move: `A_link_copys_code_goes_along` fails.
4. `NameProblem` skipped: `A_name_that_escapes_is_400("../evil")`, `("a/b")` and `A_name_that_breaks_the_note_names_is_400` fail.

Fix round (`71052a1`): the gate test `A_record_change_runs_under_the_vaults_gate` and `A_record_change_that_keeps_conflicting_is_409` were red before the fix; `A_record_change_during_a_re_keying_is_refused` is a guard that passed from the start.

### Task 22 (web)

1. First vault preselected: the `TargetDialog` preselect and name-taken tests fail; the Skills test "copies a skill to Skills by default" **did not fail** at first and was strengthened (an Archive vault before Skills).
2. Move offered for system skills: "offers no Move for a system skill" fails (the fixture's system skill needed `canWrite: true` to bite; strengthened).

### Task 23 (web)

1. `as` dropped from the find call: "asks as a chosen connection" fails.

### Task 24 (web)

1. Setting shown for devices: "shows no instructions setting for a device key" fails.
2. "list" sent: both "sets a key/connection to Hint only" tests fail.

### Task 26 (`SkillPublicationTests`)

1. Fair copy over `SKILL.md`: `A_skill_publication_holds_every_file_verbatim` fails.
2. (a) `OpenPublicationAsync` kind check removed: **survived** (the skill's copy is sealed to `publication:{l}:skill`, so opening it as a note's copy fails authentication anyway). Accepted by both reviewers as defence in depth. (b) Kind check removed and the copy opened under its own context: `The_reader_endpoint_doesnt_open_a_skill` fails.
3. A second link allowed: `A_second_link_isnt_made_while_one_exists` fails.
4. Mark check skipped: `A_skill_with_an_unpublishable_file_cant_be_shared` fails.

### Task 27 (`SkillLinkWithdrawalTests`)

1. Withdraw only for the web app's own deletes: `An_agents_rm_of_SKILL_md_withdraws_the_link`, `A_devices_removal_withdraws_the_link`, `A_dav_delete_of_the_folder_withdraws_the_link` fail.
2. Refuse instead of withdrawing: first only the agent and DAV tests failed (the owner's device was already allowed). `A_members_removal_withdraws_the_link` added in `22660bb`; then 3 of 12 fail.
3. Path update skipped: `Renaming_the_folder_follows_it` fails.
4. Mark check skipped: `Marking_a_file_added_after_sharing_withdraws_the_link` fails (the brief's test marks a file the copy already held, which cannot catch it; second test added).
5. Old early return kept: `A_renamed_copy_keeps_its_sealed_entries_and_a_trashed_one_drops_them` and `A_copy_moved_out_of_its_place_drops_its_sealed_entries` fail.

### Task 28 (`SkillSharingApiTests`, web)

1. Link in the list view: `Sharing_answers_the_link_once` fails.
2. Move refusal dropped: `A_shared_skill_cant_be_moved` fails.
3. Share offered for a shared skill (web): "shows the link once and never again" and "offers no second link while one exists" fail.

Fix round (`372a969`): withdraw endpoint not recording: `A_withdrawn_skill_says_so_until_it_is_shared_anew` fails; `FollowAsync` not recording and a mark not recording: `A_link_withdrawn_by_a_change_or_a_mark_counts_too` fails; a new share not clearing: both tests fail.

### Task 29 (`SkillPageApiTests`)

1. Cookie named `kumbu_reader`: `The_code_opens_the_skill_and_sets_its_cookie` fails.
2. `/api/k` out of `ReaderPaths.Is`: `The_page_and_api_get_the_readers_headers` fails.
3. Zip root and file name from the manifest's name: `A_manifest_name_cant_steer_the_zip` fails.
4. Copy header check skipped: `The_zip_needs_the_copy_header` fails.

### Task 30 (`SkillPage.test`, `isolation.test`, `SkillPageApiTests`, `Skills.test`)

1. `import "../api/client"` in `SkillPage.tsx`: `isolation.test.ts > imports nothing of the app` fails.
2. `img` rendered as a real `<img>`: "shows images as their alt text and fetches none" fails.
3. `SkillZip` guard without the backslash check: 2 cases (`scripts\run.py`, `a\b`) fail.
4. `SkillZip` guard without `..`: 3 cases fail.

Follow-up (`3049069`, `0c2e65b`): the Installed link using `s.path ?? s.name`: "offers each skill as a .zip" fails; a remark plugin turning `html` nodes into text: "renders no raw HTML from SKILL.md" fails; anchor not appended: "downloads the zip with the copy id" fails; unmount cleanup removed: "revokes the zip's URL when the page goes" fails; blob URL created outside the try: 3 tests fail.

### Task 31 (`AddSkillTests`, `SkillCopyTests`)

1. The code written into `kumbu-source`: `Dave_adds_…_without_the_code` and `The_code_is_kept_sealed_…` fail. The failure message first printed the code; the tests were changed to `AssertNoCode` in `3147740` and the mutation rerun.
2. Hash computed after `WithOrigin`: `Dave_adds_…` fails on the hash.
3. Sealed `SkillCode` skipped: `The_code_is_kept_sealed_…`, `A_taken_name_is_409`, `A_members_writable_vault_…` fail.
4. `NameProblem` skipped in `AddAsync`: `A_name_that_escapes_is_400("../evil")` and `("a/b")` fail (and the write was accepted, section 9).

Fix round (`7c71a4f`): the check deleted from `TargetOf`: 11 tests fail.

### Task 33 (`PublicAddressesTests`)

1. 10/8 accepted: `Same_answers_as_the_oauth_fetch` fails on four rows.
2. `ConnectAsync` connecting to any address: `The_oauth_client_still_refuses_loopback` fails.
3. `UseCookies = true` in the cimd handler: `The_oauth_handler_follows_no_redirects_and_takes_no_cookies_or_proxy` fails.

### Task 34 (`KumbuOptionsValidatorTests`)

1. File check `IsNullOrEmpty` to `is not null`: `Empty_values_from_compose_count_as_unset` fails.
2. File content appended to the failure message: `No_failure_names_the_token`, `An_empty_token_file_is_refused`, `An_unreadable_token_file_names_its_path` fail.

### Task 35 (`GitHubClientTests`)

1. `ConnectCallback` dropped: `The_real_handler_refuses_loopback` and `The_real_handler_follows_no_redirects_and_takes_no_proxy_or_cookies` fail.
2. `UseProxy = true`: the second of those fails.
3. 3xx accepted as success: `A_redirect_isnt_followed` fails.
4. Upper-case or short commits accepted: `A_commit_that_isnt_40_lower_hex_is_refused("0123")`, the upper-case row and `An_archive_uri_needs_a_valid_commit("abc")` fail.
5. 1 MB cap skipped: `An_api_answer_over_1_MB_is_refused` fails.
6. Only the counting stream skipped: the same test fails ("too-large" expected, "bad-answer" actual).
7. `%` allowed in the repo pattern: `Repo_names_are_checked("a/b%2F..", False)` fails.

### Task 36 (`GitHubArchiveTests`)

Original (`7c43d75`, `42c103a`):

1. Decompressed cap raised to 200 MB: `More_than_100_MB_decompressed_is_refused` fails.
2. `new byte[length]` allocated before the check: `A_2_GB_entry_header_is_refused_before_allocating` fails.
3. Only files counted toward 5000: `More_than_5000_entries_is_refused_counting_every_kind` fails.
4. Symbolic links kept: `Links_and_escaping_names_are_skipped` fails.
5. Evicting by count only: `The_cache_holds_at_most_four_archives_and_200_MB` fails.
6. Token sent to codeload: `The_archive_comes_from_codeload_at_the_commit_without_the_token` fails.
7. The archive's timeout not mapped: `A_download_that_hangs_is_given_up_as_unavailable` fails.
8. No timeout on API requests: **at first hung instead of failing**; the hanging tests now wait with a 30 s real-time bound (`42c103a`), and `An_api_answer_that_hangs_is_given_up_as_unavailable` fails with `TimeoutException`.

Round 1 (`34a43d1`, the tar guard): guard off: the four 1 GB metadata cases, checksum and both count tests fail; top folder unchecked: `Links_and_escaping_names_are_skipped`; only file headers counted: the 5000 test and `Metadata_headers_count_toward_5000`; metadata limit off: the four 1 GB cases; checksum unchecked: `malformed(checksum)`; PAX size not followed: `A_pax_size_is_followed_past_the_entry`; exception mapping narrowed: `malformed(sparse)`. The original mutations were re-run on the new code.

Round 2 (`9cb5eeb`): `x` allowed after `L`/`K`: the `L x file` and `K x file` cases fail; `L` allowed after `x`: `malformed(k-then-l)` fails (the `x L file` probe is refused by `TarReader` itself, so it does not tell this one apart); metadata counted toward 5000: `Metadata_headers_dont_count_toward_5000`; `g` anywhere: `malformed(g-not-first)`; magic unchecked: `malformed(x-then-gnu)`, `malformed(l-then-posix)`; any entry type after metadata: `malformed(x-then-fifo)`; metadata then end allowed: `malformed(x-then-end)`.

### Task 37 (`MarketplaceIndexTests`)

1. `MaxDepth = 7`: `Without_marketplace_json_every_SKILL_md_up_to_six_levels` and the `IsSkillPath` Theory fail.
2. A non-local source treated as local: **survived** (the only non-local source was an `https://` one, which fails validation anyway). Test strengthened in `d1f76a1` with a bare relative source; it then fails.
3. `IsSkillPath` accepts `..`: the Theory and `A_source_that_escapes_isnt_installable` fail.

Fix round (`2cb5db8`): catch only `JsonException`: the four lone-surrogate cases fail; folder-name check off: `A_skill_whose_folder_name_Kumbu_cant_keep_isnt_installable`; first case-twin always wins: `A_case_twin_of_SKILL_md_doesnt_hide_it`; plugin name not cleaned: `Names_sources_…_cleaned_and_bounded`.

### Task 38 (`MarketplaceApiTests`)

1. Branch resolved again at install: `Install_takes_the_commit_it_was_given` fails.
2. Fetch on add: `Adding_a_marketplace_fetches_nothing` and `Nothing_is_fetched_at_startup` fail.
3. `Commits.IsValid` skipped: **survived** (the client's `ArchiveUri` throws an `ArgumentException`, which also ends as 400 with nothing fetched). Theory strengthened in `9b3f2b6` to assert the route's own sentence; the `commit=main`, `commit=0123` and upper-case rows then fail.
4. Per-user limit skipped: `Too_many_fetches_by_one_user_answer_429_github_busy` fails.
5. Switch ignored in browse: `Switched_off_hides_and_fetches_nothing` fails.

### Task 39 (web)

1. "master" sent instead of the commit: "installs after showing repository, path, commit, files and the script notice" fails.
2. Tab shown whenever the answer arrived: "hides the tab when switched off" fails.

### Task 40 (`SkillUpdateCheckTests`, `SkillsShellTests`)

1. Compare commits only: `A_newer_commit_with_the_same_files_is_current`, `A_skipped_update_is_shown_as_skipped`, `A_vanished_skill_folder_is_gone` fail.
2. (a) Source read from the frontmatter, origin-edited check dropped: `An_edited_origin_is_shown_and_never_fetched` fails. (b) Also checking skills with a frontmatter source but no sealed origin: `A_source_only_the_frontmatter_names_is_never_fetched` fails.

Fix round 1 (`d784fe5`): a token taken for every repository: `A_check_of_cached_commits_or_links_alone_takes_no_token` fails; no token taken: `A_check_takes_a_token_per_repository…`, `A_check_counts_against_the_users_GitHub_limit` and the cached test fail; `HasUpdate` ignoring origin edited: core `An_edited_origin_is_shown_and_offers_no_update` and server `An_origin_edited_after_a_check_is_shown_and_offers_no_update` fail.

Fix round 2 (`877b7bc`): the switch check dropped in `SkillUpdates`, or in `SkillUpdateTaker`: each fails `With_marketplaces_off_a_GitHub_copy_is_never_fetched`.

### Task 41 (`SkillUpdateTakeTests`), run in a worktree of `d7cc0d9`

1. Review diffed against the source's previous files: `Review_shows_the_users_own_edits_too` (and incidentally `A_link_copy_…`) fails.
2. Hash check skipped: `Take_refuses_a_source_that_moved_on` fails (Conflict expected, OK actual).
3. Sealed origin left as it was: `Take_rewrites_the_origin_fields_and_the_sealed_origin` and `A_link_copy_takes_the_updated_publication` fail.

### Task 42 (web)

1. Take sends hash "": "reviews every file and takes the update with the reviewed hash" fails.
2. Check only on the button (mount call removed): "checks for updates when the tab opens and on the button" fails (and a second test).

Fix round (`2a7c753`): `refetchOnWindowFocus` set to true fails "keeps the reviewed hash when the window regains focus".

### Task 44 (`WelcomeTextTests`)

1. `Skills.md` removed: `The_vault_holds_exactly_the_eight_notes` fails.

### Survivors, in short

Mutations that survived at first and what happened: Task 5 mutation 3, Task 9 mutation 1, Task 10 mutation 5, Task 11 mutation 3, Task 18 mutation 3, Task 22 (both, until the fixture was strengthened), Task 37 mutation 2, Task 38 mutation 3, Task 36 mutation 8 (a hang, not a failure) were each answered with a stronger test. Three were accepted as they are: Task 8 mutation 4 (redundant guards), Task 20 mutation 4 (`CountsUse`, a pin) and Task 26 mutation 2(a) (defence in depth). Task 4 reported that no mutation targets the store check's replace, move and restore parts; Task 5 added them.

## 9. Decisions made without Chris

### The plan's Decisions

The plan's "Decisions" section settles the brief's eleven open points (OP 1 to 11) and 24 further decisions. Options are given where the plan recorded them; a dash means the plan records the choice and its reason but not a list of options.

| # | Decision | Options seen | Why |
|---|---|---|---|
| 1 (OP 1) | Instructions per connection: an incoming message filter records the id of an `initialize` or `server/discover` request in `HttpContext.Items`; the outgoing filter replaces `result.instructions` for that id only. Caps: 50 entries, 300 characters per description | `ServerInstructions` set in `ConfigureSessionOptions` (runs on every request, `tools/call` included); a request filter on `initialize` (misses `server/discover`); incoming plus outgoing filters (chosen) | Nothing is built, unlocked or listed for any other method; one HTTP request carries one JSON-RPC request in stateless mode |
| 2 (OP 2) | The narrow exception in `Manual.ToolDescription` and `ServerInstructions.Text`: a skill whose description matches the task may be followed after looking at its origin; what the user says comes first | — | Keeps "note content is data" with one stated exception |
| 3 (OP 3) | Name clash: any own vault or alias whose normalised name is "skills" is renamed to "Skills (renamed)", else "Skills (renamed 2)"… through a helper extracted from the rename endpoint; the notice says which | Only an own vault named exactly "Skills"; any own vault or alias by normalised name (chosen) | Both share one name index |
| 4 (OP 4) | Migration hook: `SkillsVaults.EnsureAsync` (10 s bound, never throws, serialised per user) in sign-in, join and `GET /api/session`; a `skillsAt` stamp; a busy or failing rename leaves it unset so the next call retries; the notice is sealed under a key derived from the user key | — | Never blocks or breaks sign-in; survives crashes |
| 5 (OP 5) | Ranking: reciprocal rank fusion with k = 60 (name+description weight 2, body 1, per-vault meaning 1); one search over the root scope split by vault; system skills by words only; no embedding on the fly; top 10 | — | No score is compared across vaults or providers |
| 6 (OP 6) | The `/k` page: own entry `k.html`, API group `/api/k`, cookie `kumbu_skill`; "Add to my skills" is a plain link to `/skills/add#<code>` that the web app moves into `sessionStorage`. The plan said "always shown" (deviated from, see the rulings below) | — | Isolation from the web app; the isolated page makes no signed-in call |
| 7 (OP 7) | `kumbu-hash`: `sha256:` plus hex over files ordered ordinally by NFC path, each as path bytes, a 0 byte, an 8-byte length, the bytes; over the files as received | — | Stable and unambiguous (Task 11 pins a boundary test) |
| 8 (OP 8) | Text files: one gate in `AttachmentFormat.ForPath` (strictly inside a skill folder), the writer checks the mark, the store backstops. Extension-less files limited to `LICENSE`, `LICENCE`, `COPYING`, `NOTICE`, `AUTHORS`, `CHANGELOG`, `README`, `Makefile`, `Dockerfile`. **Deviates from spec §3.3** | Any extension-less name (the spec) | An arbitrary extension-less name is also how a folder is named; it would make `IsAttachment` true for folders and break `rm`, `mv` and WebDAV lookups. Side effect remains (a folder named `config.json/` inside a skill) and is in `open-items.md` |
| 9 (OP 9) | litmus at every phase gate, started as CI does | — | CI parity |
| 10 (OP 10) | `Kumbu:Skills:GitHubTokenFile` beside `GitHubToken`; both set stops the server; empty counts as unset; `compose.yaml` passes only `KUMBU_GITHUB_TOKEN` | — | Same shape as `Kumbu:Mongo:PasswordFile` |
| 11 (OP 11) | Preselection in the new-key and new-connection dialogs only; the consent page shows Skills like any vault | — | Smallest surface |
| 12 | `kind` is a plain field on the vault document | — | Spec §4 |
| 13 | Sealed Skills state lives as optional fields on `PublishingRecord` (`SkillCodes`, `Origins`, `Marketplaces`, `Skipped`, `Updates`, later `Withdrawn`) | A new sealed collection; optional fields (chosen) | Re-keying, deletion, storage figures and transactions then cover it without touching `VaultCatalog.Rekey` |
| 14 | A skill is a direct subfolder of `/Skills/` or `/<vault>/_skills/`; text files only inside a skill folder; a missing or broken `SKILL.md` is a warning | — | — |
| 15 | One link per skill, shown once; no "Copy link" later. After withdrawing, Share makes a new link with a warning | Offer "Copy link again" as note links do | The brief forbids the code in any response but the creating one. **Open point for Chris** |
| 16 | `kumbu-source` of a link copy: `link:` plus the publication's lookup id in hex | — | The code itself must not be stored in the clear |
| 17 | Instructions setting: field `instructions` on keys and connections; `PUT …/instructions`, session only, no password window; device keys answer 400 | — | A preference about a text grants no right |
| 18 | The dev token sees the system skills and is in list mode | — | — |
| 19 | Origin in every listing: folder path, "shared by <owner>", "last changed by <label>", ", another user", "from <source>", ", origin edited"; names quoted when they break the rules; Cc/Cf stripped | — | No listing line can imitate another origin |
| 20 | `write` writes text files in skill folders; `edit` and `replace` keep refusing them | — | — |
| 21 | Cross-vault Copy and Move write through the session's writers (rights, quota, history); a move then trashes the source; a failed write removes what it wrote | — | — |
| 22 | `skills ls` shows "update available (Screen)" from the last web check; `sh` fetches nothing | — | Agents never reach the network |
| 23 | GitHub endpoints: `commits/HEAD` with the `sha` media type, then `codeload…/tar.gz/{commit}`; token to `api.github.com` only; validated, escaped segments | — | Two hosts only |
| 24 | Marketplace layouts: a `marketplace.json` that does not parse falls back to the plain layout with a note; only `./` sources are local | — | — |
| 25 | The hash at install covers the files that will be stored | — | — |
| 26 | Text files are served as `text/plain; charset=utf-8` whatever their extension | — | `.html` and `.svg` never render |
| 27 | Tests pinning a new user's vault list get Skills in their expectation, each named in Task 9's commit | Bend behaviour to keep them | Rule: no behaviour bent for a test |
| 28 | The Obsidian plugin gets no text files unless it asks with `text=1` | — | Whether it should is an open point |
| 29 | Skill publication limits are the marketplace limits (200 files, 10 MB) | — | — |
| 30 | Caching and limits: commits 10 minutes, listings an hour per commit, archives in an LRU (4, 200 MB); 30 GitHub fetches a minute per user, 429 `github-busy` | — | Plan review finding 1 |
| 31 | A shared skill's folder changed by any writer withdraws its link instead of refusing the write | Refuse such writes; withdraw (chosen) | Less exposure and no existing write path breaks |
| 32 | A published note moved into a skill place is withdrawn; those already there stay live until changed or moved, and cannot be updated | — | Break nothing |
| 33 | Sealed origin per copy; a disagreeing frontmatter reads "origin edited" and nothing is fetched | Trust the frontmatter; trust a sealed copy (chosen) | An agent cannot point the server at a repository by editing a file |
| 34 | Targets of a copy may be any vault the user may write; the sealed state goes into that vault's record | — | Same trust as the files |
| 35 | Text files reach sync clients only with `text=1` | — | Old clients never see them |

### Rulings made during the run (ledger)

| Ruling | Why | Cost if wrong |
|---|---|---|
| The pre-flight conflict scan is replaced by the independent plan review (22 findings, all folded in) | The plan was just reviewed against spec, brief and code | An intra-plan conflict surfaces only in task review |
| Two fresh reviewers per task (spec, quality) in parallel instead of the skill's single reviewer | The brief asks for it | — |
| Task 4: the store's last check throws `InvalidOperationException` (probably a 500) | It is a backstop behind channel gates that answer 4xx; fail-closed is acceptable | An odd 500 for a case no channel reaches |
| Task 4 into Task 5: the store's check on move and restore is untested, so a store-level test is added in Task 5 | Both reviewers flagged it | A bypass via move survives unnoticed until the final review |
| "`ForPath` assumes the name Skills" stands as designed | Spec §3.1: the vault cannot be renamed, a clashing vault is renamed at migration; the writer checks the mark and the store backstops | An unmarked "Skills" vault before migration gets refusals (fail closed) |
| Tasks 6 and 7 batched to one implementer, reviewed per commit | Two small adjacent tasks | A combined fix loop |
| Task 13: the weak per-caller visibility test in core is accepted; real-store pins carried to Task 14 | Enforcement is the per-caller `EncryptedNoteStore` | A visibility gap surfaces only at Task 17 or 20 |
| Tasks 14 and 16 run in parallel on disjoint files | Time | A build break from the other's half-done edit |
| Task 15: ranking tested against fakes only; the real index is exercised by Task 20's server-level find test | — | Find by meaning silently drops to words only on the real store |
| Task 26: mutation 2(a) survives by defence in depth (a separate seal context) | Both reviewers accepted | One unpinned layer |
| Skills copy and move endpoints need a session but no password window (registry `c52cb4b`) | Spec §1 and §5: a move across a trust boundary is a deliberate, confirmed step in the web app; the window guards credentials, rights and bulk export, not content writes | An unattended remembered session can copy a skill between the user's own vaults without the password |
| Skill share and update require the password window; list and withdraw stay open | Precedent of note publications | One extra password prompt |
| `/k` "Add to my skills" after a reload: first ruled to keep the code in `history.state`, **revised** to hide Add after a reload and keep the code nowhere | Rule 3: the secret in fewer places | A visitor who reloads must reopen the link. **Deviates from the plan's "always shown"** |
| Task 36: accept the deviations (links and `K` alone after metadata; format matching); then park "GNU `K`-then-`L` long links and POSIX fifos are refused" | The guard has no desync; codeload (`git archive`) never writes such entries | A hand-made archive with such entries is refused whole (not reachable through codeload) |
| Task 38: the per-user GitHub limit counts cached requests; a comment fix carried to Task 40 | Safer side | A busy user meets the 30 a minute limit sooner |
| Skip is offered from the Review dialog (it needs the reviewed hash); the check's response stays without a hash | Spec §3.10 lists Review, Take and Skip together | One extra click to skip |
| The marketplace switch must also stop update checks, Review and Take for `github:` copies (fix `877b7bc`) | Spec §3.9 "fetches nothing"; rule 3 | None |
| GPS and EXIF in images of a skill link stay verbatim (spec §3.7) and go to open-items for Chris | Stripping would be a spec change | A shared skill's photo carries its location |
| 18 commits by Sonnet-model implementers carry "Co-Authored-By: Claude Sonnet 5.5" instead of the brief's Opus line (24 by the final count) | Accurate attribution; no force-push is allowed | A cosmetic trailer mismatch |
| Security finding 8 (the handshake touches every reachable vault) accepted as specified | Spec §3.6 | Vault keys stay in memory for clients that reconnect often |
| Security finding 9 (the code stays in `sessionStorage`) parked | Clearing on unmount breaks the sign-in hand-over; the storage dies with the tab | The code stays in that tab until it closes |
| Security finding 5 closed by finding 4's fix | Only server-resolved commits are accepted | — |

## 10. Deviations, surprises, tasks left, open points

### Tasks left

None skipped. Tasks 0 to 44 are done. Task 45 (the review) is done, its fixes are pending (section 7). Task 46 (the final gate) is PENDING. Task 47 is this report and the pull request.

### Deviations from the spec and the plan

- **Extension-less text files** limited to nine names (Decision 8; spec §3.3).
- **`/k` offers Add to my skills only until the page is reloaded** (plan Decision 6 said always shown).
- **The tar guard refuses a whole archive** with a header shape codeload never writes, where the spec speaks of skipping links.
- **Review is offered only to someone who can write** (Task 42): Take writes.
- **Code against Decision 19**: the skills list in the MCP instructions does not mark ", origin edited" (`skills ls`, `skills find` and the web list do).
- **`SkillSearch` does its own rank fusion** with the shared constant `RankFusionK`, because `SectionSearch.FuseByRank` is private and typed to chunks (Task 15).
- **A skill's name is always the folder's name** (Task 13): a manifest name is valid only when it equals the folder's, so a manifest cannot give its skill another skill's name.
- **Task 8**: the brief's regex `/^_skills(\/|$)/` never matches an API path; `/^\/[^/]+\/_skills(\/|$)/` is used. A wrong password on the Skills vault's delete answers 409, not 401.
- **Task 21**: `PublicationStore.ChangeAsync` added; `SkillCopier` takes an optional logger.
- **Task 27 added `Updates` and `Skipped`** to `PublishingRecord` early (planned for Task 40).
- **Task 44**: the brief's "line references off by 4" for `EncryptedNoteStore` was wrong; they had been stale since Publish v1. Every reference was remapped by content.
- **Briefed tests used `alice.CreateAsync("/Skills/…")`**, which `RestClient` prefixes with the client's own vault. Tasks 17, 21, 26, 27, 28, 29 and 31 write with `PUT /api/notes/<path>` instead. Several briefed DAV tests also needed an `MKCOL` of the parents first (Tasks 4 and 5).
- **Red first was not observed** in Task 1 (the compile-failure step was skipped; mutations offset it) and in Task 22 (implementation written before the first run).

### Surprises and real defects the reviews found

- **Task 36, Critical (found in review)**: a PAX or GNU metadata header declaring 1 GiB made the tar reader allocate about 1,073,770,000 bytes. Measured, then fixed with a header-shape guard (`TarGuard`); a second round closed a desync (`L`/`K` then `x`). Compatibility with `git archive`, GNU and POSIX tar was probed.
- **Task 8**: an image in `_skills/` embedded by a published note was copied into the publication (`MayCopy`); found by a reviewer's question, fixed with a failing test first.
- **Task 10**: a notice that could not be read (anything other than a `CryptographicException`) made `GET /api/session` and sign-in fail; fixed to best effort. The notice is now sealed before the rename.
- **Task 14**: `skills cat write-as-alice/../../Work/plan.md` reached `/Work/plan.md` through `Cat`'s path parsing; fixed with a `..` refusal.
- **Task 31**: under mutation, `../evil` and `a/b` were accepted as folder names (`NameProblem` was the only guard); `TargetOf` now refuses them itself. The security review (finding 6) asked for the same guard in the update taker and `SkillCopier.Inside`.
- **Task 28**: the "share again" warning was first held in browser memory in one tab; replaced in the fix round by a durable `Withdrawn` list in the sealed record.
- **Task 9**: 81 existing tests pinned a new user's vault list; all were adjusted (Decision 27), none by bending behaviour.
- **Task 17 / Gate 1**: the handshake's listing stamped a shared vault's `usedAt` (`CountsUse` fix), and four registries needed `skills` added.
- **The security review's four Important findings** were all in code reviewed clean at task level: a route misclassified as "deliberately open", a provenance label shown from untrusted text, a "last changed by" computed from one file, and a missing concurrency bound.
- **Concurrent implementers** shared one working tree. Several verified their commits in temporary worktrees (removed afterwards) and staged by blob (`git update-index --cacheinfo`) to commit only their own hunks. No work was lost.

### The detached-HEAD incident and its recovery

An implementer ran a checkout of a commit after `20f73de`, so HEAD was detached. Twelve commits (`0f5f2d0` to `3326be8`, Tasks 21 to 29) landed on the detached HEAD, and a plain `git push` pushed nothing. The Task 29 implementer noticed ("HEAD detached from 20f73de") and reported it without touching branches. Recovery at about 11:00: `git branch -f feature/skills HEAD`, `git symbolic-ref HEAD refs/heads/feature/skills`, then the push of `20f73de..3326be8`. No force-push was used and no commit was lost. `common.md` (the implementers' rules) now forbids detaching, and the coordinator pushes with an explicit branch check.

### Load-flaky tests seen at gates

- `RekeyTransitionTests.A_run_cancelled_in_its_transaction_completes_the_transition(inside-finish)`, Gate 1 rerun: 1 min 01 s timeout while implementers ran tests; the class alone, 3 times: 12/12 each.
- `PairingTests.A_device_name_gets_the_same_answer…(dev)`, Gate 4: the class alone, 3 times: 55/55 each.

Both are recorded as load-flaky, not regressions. If either shows again at Gate 5, PENDING: Task 46.

### Commit trailers

The brief asks for the Opus trailer. 24 of the 79 commits, made by Sonnet-model implementers following the session's attribution reminder, carry "Co-Authored-By: Claude Sonnet 5.5" (`04bdcb5`, `81ad1a5`, `ab9fe1b`, `1b730ec`, `6aa4a5f`, `48129ae`, `9fc6471`, `c5f0cb2`, `4d213cd`, `82af5d8`, `65d1ae7`, `ecb24ea`, `f584cdc`, `20f73de`, `4ac75a1`, `75b822d`, `c66ade2`, `e251252`, `98aed59`, `be65122`, `25079ee`, `75e1280`, `19f0c19`, `2a7c753`). The other 55 carry the Opus line. The ledger counted 18 when it ruled; the count above is from `git log`. Changing them would need a force-push, which the brief forbids.

### Open points for Chris

From the rows this branch added to `docs/superpowers/open-items.md` (`git diff 898cb9c HEAD`):

1. **Obsidian plugin**: the versions endpoint leaves text files out unless asked with `text=1`. Ask for them once the plugin can keep the text files of skill folders, or it will not know them as files.
2. **Note publications already in a skill place** before the upgrade stay live until changed or moved; they can no longer be updated, only withdrawn. Withdraw them by hand if a skill folder was published as a note.
3. **A Copy to… or Move to… that fails part way** sends the files it wrote to the target vault's trash for 30 days, where they count towards the owner's storage (for a member's copy, the owner's trash and history). `skills copy` of a system skill leaves what it wrote. Decide whether such leftovers should be removed for good.
4. **"Copy link again" for skill links.** Note links have it; skills keep the code to the one answer that creates it, so the Shared tab offers only Update and Withdraw, and a lost link means withdrawing and sharing anew (copies made from the old link then lose their updates). Decide whether skill links should be copyable again.
5. **Extension-less text files** (deviation from spec §3.3): only nine names stay in a skill folder, and a folder named like a text file (`config.json/`) reads as a file's path. Decide whether that needs more.
6. **`/k` Add to my skills** is offered only until the page is reloaded (deviation from plan Decision 6).
7. **The archive guard** refuses a whole marketplace archive with a header shape codeload never writes, where the design speaks of skipping links.
8. **Images keep EXIF and GPS** in a shared skill, unlike a published note's. Decide whether a skill's images should be stripped, or the Share dialog say so (the security review's finding 10).
9. **The MCP instructions list does not mark ", origin edited"** (plan Decision 19), while the other listings do.

Also for Chris, not in `open-items.md`: security findings 8 (handshake keeps vault keys in memory) and 9 (the code in `sessionStorage` until the tab closes) were accepted or parked on the grounds given in section 9; and the plan's open point 12 (no second link while one exists).

A row linking the pull request in `docs/superpowers/open-items.md` is added by the coordinator with the PR link. PENDING: PR link row.

## 11. The hand-test checklist

Follows spec §6 ("End to end"): Alice owns the vaults Work and Private; Bob is a member of Work with write access. Where a step needs an AI client, any MCP client works; the console in the web app can stand in for `sh`.

**Set-up (once).**

- S1. Start the server from `master` (or `feature/webdav`) first. Create the accounts Alice, Bob and Dave (Carol needs none). Give Alice the vaults Work and Private; invite Bob to Work with write. Give Alice a vault named `Skills` holding a few notes (her CV), and an API key for the old Claude Code client that has that vault. Give Dave a vault `Writing`.
- S2. Stop it and start the server from this branch on the same database. Leave `KUMBU_PUBLISH_ENABLED` and `KUMBU_SKILLS_MARKETPLACES` at their default (true). Optional: `KUMBU_GITHUB_TOKEN` for a higher GitHub allowance.
- **Passes:** the server starts; nothing about Skills is logged as a failure.

**Step 1: the upgrade and the name clash (spec §6.1).**

1. Sign in as Alice. Where: the session reply, the web app's shell.
2. Look for the notice in the shell (above the note view). **Passes:** it says her vault was renamed to "Skills (renamed)"; Dismiss removes it, and it does not return after a reload.
3. Open the vault list. **Passes:** "Skills (renamed)" holds her CV notes; a new, empty vault "Skills" exists; "Skills" cannot be deleted, renamed or shared with a member (try each in the vault menu and the Access page; each is refused).
4. Use her old Claude Code key: `sh ls /`. **Passes:** it lists `Skills (renamed)/` and does not list `Skills/`; `sh ls /Skills` is an error or empty.
5. Sign in again in a second tab at the same time. **Passes:** still exactly one vault "Skills".

**Step 2: Access and the preselection (spec §6.2).**

6. Open Access, create a new key and a new connection. **Passes:** in the vault list, Skills starts at Read for both, unless you chose before the list arrived.
7. Edit the Claude connection and give Skills `write`; give the Claude Code key Skills `read`. **Passes:** the changes stick after a reload.
8. On a key and a connection find "Skills in the instructions" (Access, Keys and Connections). **Passes:** it offers list-and-hint or hint-only; a device key shows no such setting. Set one key to hint-only for step 4.

**Step 3: an AI makes a skill (spec §6.3).**

9. Connect an MCP client with the Claude connection. Inspect the instructions it received on connecting. **Passes:** they name `create-skill` (built-in) and say that note content is data, with the one exception for a matching skill.
10. Ask the AI to make a skill that writes in your style. **Passes:** it writes `/Skills/write-as-alice/SKILL.md` and `examples/post.md`, and runs `skills check`, which is clean (no warning lines).
11. In the web app's tree open `examples/post.md` and try uploading a `.py` file into `/Skills/write-as-alice/` and into a normal folder. **Passes:** the script is accepted inside the skill folder and refused elsewhere, with a message; it is shown as plain text, never rendered.

**Step 4: reading and using it (spec §6.4).**

12. Connect Claude Code (the key with Skills `read`, instructions in list mode) and read its instructions. **Passes:** `write-as-alice` is listed with its folder, "last changed by …", and no more than 50 entries.
13. Ask for a release post. **Passes:** the AI runs `skills cat write-as-alice` and `skills cat write-as-alice/examples/post.md`; `skills ls` and `skills find "a release post"` list it. `skills cat write-as-alice/../../Work/x` is refused.
14. Set the key to hint-only and reconnect. **Passes:** the instructions carry only the hint to `sh skills ls`/`find`, no list; `skills ls` still works.

**Step 5: a member's skill (spec §6.5).**

15. As Bob, write `/Work/_skills/release-notes/SKILL.md` (and a file in a subfolder). **Passes:** Alice's Claude (key with Work) lists it as "shared by Alice" if Work is hers, with "last changed by Bob".
16. As Bob, edit only `references/steps.md` of one of Alice's skills in Work. **Passes (after the security fix wave):** the skill is marked as changed by another user, not only when `SKILL.md` changes. PENDING: fix wave results.
17. Use a key that has only Private. **Passes:** it neither lists nor reads `release-notes` (`skills ls`, `skills find`, `skills cat` all come back empty or as errors).
18. Open the Skills screen, Try tab, choose "as seen by" the Private-only key. **Passes:** the results leave the skill out.

**Step 6: install from a marketplace (spec §6.6).**

19. Skills screen, Marketplaces tab: add a public marketplace repository (the README's example, or any repository with `SKILL.md` folders). **Passes:** adding fetches nothing; Browse fetches once.
20. Choose a skill that holds scripts, Install…. **Passes:** the preview shows repository, path, a 12-character commit, files with sizes, skipped files, and a notice that it contains scripts; the target dialog defaults to Skills; a taken name asks for another.
21. Open the installed copy's `SKILL.md`. **Passes:** the frontmatter records `kumbu-source` as `github:<repo>/<path>@<commit>` and a `kumbu-hash`.
22. Edit that frontmatter's `kumbu-source` by hand to another repository. **Passes:** the skill reads ", origin edited" and no update is offered or fetched.
23. In a skill you wrote yourself add `kumbu-source: github:evil/x@<40 hex>` by hand. **Passes (after the security fix wave):** every listing shows it as unverified or as origin edited, never as "from github:…". PENDING: fix wave results.

**Step 7: update, review, take (spec §6.7).**

24. A week later, or after the upstream changes (or edit one of the installed copy's files and run the check): Skills screen, Installed tab opens, which checks once; press "Check for updates". **Passes:** the copy shows "Update available"; `sh skills ls` says "update available (Skills screen)" and nothing is fetched by `sh`.
25. Make a local change to one of the copy's files first. Press Review. **Passes:** repository, path and commit lead; every file shows its change and hunks as text, including the line you changed yourself.
26. Take. **Passes:** the files and the origin fields are replaced; the copy reads current. Try Skip on another update: it hides that version.
27. Change the upstream between Review and Take (or take twice). **Passes:** 409 "moved on", with Review again; Take and Skip stay disabled until the review reloads.
28. Switch marketplaces off (`KUMBU_SKILLS_MARKETPLACES=false`), restart. **Passes:** the Marketplaces tab is gone; check, Review and Take for a `github:` copy answer "marketplaces off" and the server makes no request; link copies still update.

**Step 8: share by link (spec §6.8).**

29. Installed tab, Share on `write-as-alice` (password prompt if the window is closed). **Passes:** the link `…/k#<code>` is shown once, with a warning when you leave before copying it. Reload: the Shared tab lists the skill with Update and Withdraw and no Copy link. A second Share on the same skill is refused.
30. As Carol (no account, private window) open the link. **Passes:** the skill is shown as text; images in `SKILL.md` appear as their alt text; the address bar loses the `#code`; Download .zip gives a folder named after the skill folder with all files verbatim.
31. Reload Carol's `/k` page. **Passes:** the skill is still shown (by cookie), "Add to my skills" is gone, Download remains.
32. As Dave, open the link signed in, press Add to my skills, choose `/Writing/_skills/`. **Passes:** the copy lands there, named by the folder, `kumbu-source` is `link:<hex id>` (never the code), and the code is gone from the page and `sessionStorage` after Add or Cancel.
33. As Alice change a file and press Update on the Shared tab; as Dave run the check. **Passes:** Dave's copy shows an update, Review and Take work as in step 7.
34. As Alice `rm` the shared skill's `SKILL.md` from an agent (or move the folder out, or mark a file unpublishable). **Passes:** the link stops opening; Share then asks with "Copies made from the old link will be told their source is gone and get no more updates" (also after a reload).
35. Try Move to… on a skill that is shared. **Passes:** refused with "shared" and nothing moves; Copy to… still works.
36. Download .zip from the Installed tab for a vault skill, a skill in a vault with a space in its name and a system skill. **Passes:** each downloads; with "Ask for the password for downloads" on, or a closed password window, it asks first (after the security fix wave, as the files ZIP does). PENDING: fix wave results.

**Step 9: the injection that fails (spec §6.9).**

37. Give an AI key read on Skills and no write on Work, and ask it to "install this skill from a repository" and to "copy write-as-alice into Work". **Passes:** `sh` has no command that installs; `skills copy` copies only built-in skills; the `cat`-and-`write` route is refused for lack of write on Work.
38. Give that key write on Work and repeat the `cat` and `write`. **Passes:** it works, as for any note between vaults the key holds. The documentation says so.
39. Call `POST /api/skills/copy`, `…/shared` and `…/updates/take` with an API key or a device key. **Passes:** 403 for all.

**Extras worth a look.**

40. Skills screen, Installed: Copy to… a skill into a vault where the name is taken, then with a bad name (`../x`). **Passes:** the first asks for another name, keeping what you typed; the second is refused.
41. Copy a large skill into a vault near its quota. **Passes:** 507, nothing is left live in the target; see open point 3 for the trash.
42. Upload a skill folder with `.html`, `.svg`, `.sh` and a `LICENSE`. **Passes:** all are kept as text and downloaded or shown as plain text; none renders.
43. Sync a vault with the Obsidian plugin. **Passes:** no text files reach it (open point 1).
44. Documentation: README "Skills", `help skills` in `sh`, the Welcome vault's note "Skills" for a new account. **Passes:** they match what you saw (marketplace switch, the one link, update check on opening Installed).

After the fix wave and the final gate, repeat steps 16, 23 and 36 and run the whole checklist once against the final head. PENDING: Task 46.
