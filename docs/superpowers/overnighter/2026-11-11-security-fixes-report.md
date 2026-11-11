# Overnighter report: the fixes of the 0.4.0 audit

Run of 2026-11-11 on the brief `docs/superpowers/overnighter/2026-11-11-security-fixes-brief.md` and the private spec of the fixes (approved by Chris). Branch `fix/0-4-0-security`, based on `master` at `08c4be3` plus the brief (`83727bc`). Nothing was merged, tagged or deployed, and the version is unchanged.

This report is public. The confidential findings are named here only by id, severity and whether they are fixed now; their details, the plan, every fix's record (commit, red and green output, mutations, reviews) and the hand tests are in the private repository's pull request (link at the end).

## 1. Environment

- A cloud container with 4 cores and 15 GB of memory, shared by every agent of the run.
- .NET SDK 10.0.403 (`global.json`: 10.0.100, roll forward), `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Started or installed by hand:** the Docker daemon (its socket was there, the daemon wasn't running; started with `dockerd`); Node 26.12.1 in `/opt/node26` (the image had Node 22.23.4); pnpm 10.32.1; `litmus` was already installed.
- arctic-m's tokenizer, fetched as CI does and checked against its SHA-256; `KUMBU_TOKENIZER` set.
- Test MongoDB `mongo:7.0` as a replica set with `--ulimit nofile=20000:20000`, a fresh container per server group; every agent that ran server tests had its own on its own port. `TESTCONTAINERS_RYUK_DISABLED=true`.
- Superpowers skills were available; the work was subagent-driven: a fresh agent per fix, per spec-and-quality review and per fix review, each fix in its own git worktree and local branch, cherry-picked onto this branch only after its fix review confirmed it.
- Nothing ran against a real host. Every request went to the in-process test server, the test MongoDB or a local server for litmus; Chromium loaded only `setContent` pages with network requests aborted.

## 2. The plan and its review

The plan was written with `superpowers:writing-plans` (in the private repository, since it names the findings) and reviewed by a fresh agent that got the spec, the brief and the plan. It found four points that had to change and about a dozen that should, all folded in before the plan was committed: an arithmetic overflow in a proposed bound, a shared test data file placed where the web build couldn't read it, two places where the plan assumed data or a code path the code doesn't have, and corrections of test paths, namespaces, where the docs to update live, and test set-up details (memory tests in a non-parallel collection, among others). The details are in the private plan.

Deviation: the first three implementers started while the plan review ran; its points for them were sent to them before they finished.

## 3. Task 0: planned and measured

| Suite | Planned (brief) | Measured on `83727bc` |
|---|---|---|
| Build | green | 0 warnings, 0 errors |
| Core | 2163 total, 3 skipped, 0 failed | 2163 total, 3 skipped, 0 failed |
| Server | 836 + 413 + 353 + 20 + 768 + 1132 = 3522, 7 skipped, 0 failed | 836 + 413 + 353 + 20 + 768 + 1132 = 3522, 7 skipped, 1 failed under load (below) |
| Web | 128 files, 2242 tests, build green | 128 files, 2242 passed, build ✓ 10.39 s |
| PDF sidecar | 9 files, 198 tests | 9 files, 198 passed |

- `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault` (`HiddenVaultTests.cs:342`) failed under load; the class alone, three times on its own MongoDB: 11/11 each time.
- Task 0 took about 10 minutes of setup and 32 minutes of gate.

## 4. The findings and their state

| Id | Severity | Fixed now |
|---|---|---|
| S-01 | High | yes |
| S-02 | High | **no** |
| S-03 | Medium | yes |
| S-04 | Medium | yes |
| S-05 | Medium | yes, one case left as an open item |
| S-06 | Medium | yes (in the audit run) |
| S-07 | Medium | yes |
| S-08 | Medium | yes |
| S-09 | Medium | yes (in the audit run) |
| S-10 | Medium | yes |
| S-11 | Medium | **no** |
| S-12 | Medium | yes (in the audit run) |
| S-13 | High (pre-existing) | yes |

Fixed in this run: S-01, S-03, S-04, S-05, S-07, S-08, S-10, S-13. Not fixed: S-02 and S-11. For each of them two attempts were made; each attempt's fix review found it not good enough, so both were dropped unpushed, as the brief requires (no third attempt). Both attempts of each are kept in the private repository for Chris, with the reviews saying what is left. Two other fixes passed their fix review only on their second attempt.

Every fix carries a behaviour test that was red before and green after, its mutations turned it red again, and each had a spec-and-quality review and a fresh fix review before it was cherry-picked here.

## 5. The final gate

On `6b4daf7` (every fix and the open items; this report's commit changes only this file):

Run as CI runs it, 10:36–10:57 UTC, a fresh test MongoDB per group (`/tmp/claude-0/logs/final-*.log`, not kept):

| Suite | Result |
|---|---|
| Build | `dotnet restore --locked-mode`, `dotnet build --configuration Release --no-restore`: 0 warnings, 0 errors |
| Core | total 2168, succeeded 2165, skipped 3, failed 0 |
| Server: Api, Mcp, Auth, Net | total 843, succeeded 842, failed 1 under load (below) |
| Server: Accounts | total 413, succeeded 413 |
| Server: Sharing | total 353, succeeded 353 |
| Server: Welcome | total 20, succeeded 20 |
| Server: Vaults, Skills, Search, Metrics | total 777, succeeded 771, skipped 6 |
| Server: the rest | total 1143, succeeded 1142, skipped 1 |
| Web | 129 files, 2263 passed; `pnpm -C web build` ✓ built in 4.36 s |
| PDF sidecar | 9 files, 198 passed; build green |
| litmus (as CI's job: port 8080, fresh database) | "litmus: every failure is an expected one (21 listed)"; locks: 41 run, 28 passed, 13 failed, all in `tools/litmus/expected-failures.txt` |

Server in all: 843 + 413 + 353 + 20 + 777 + 1143 = 3549 tests, 7 skipped (the 4 that need the model and a TensorX key, and 3 more as before). The one failure under load: `SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once` (`SyncHubReviewTests.cs:366`, `ArgumentOutOfRangeException`; the sync-hub tests are in the brief's list of tests that are flaky under load). The class alone, three times on its own MongoDB: 11/11, 11/11, 11/11.

CI on the pull request (all nine checks, `WebDAV (litmus)` among them) was green on every pushed head; its result on this report's commit is in the pull request.

## 6. Decisions made without Chris

1. **Order and parallelism.** The fixes were built in parallel worktrees (two to four at a time) and cherry-picked one by one after their fix reviews, roughly in the spec's suggested order. *Options:* strictly sequential, or parallel. *Why:* the spec allows it, and it left time for second attempts.
2. **Gates for several fixes at once.** After the first fixes, the full gate ran on two to four confirmed fixes together rather than after each one. *Options:* a gate per fix (about 35 minutes each here), or per batch. *Why:* time; every fix was tested on its own branch first, and a batch gate that found a failure (one did: a test listing every session-only endpoint) was rerun after the fix.
3. **A second attempt wherever a fix review failed** and time allowed (brief open point 3), each by the implementer of the first, against the fix review's findings, followed by a fresh fix review.
4. **One reviewed fix applied without new reviews.** The spec says its patch was reviewed and needs only to be applied, its test re-run and the gate run; its diff is the patch line for line.
5. **A case of one fix left out** rather than touching code the spec keeps off limits; recorded in the private record and as an id-only open item. *Options:* change the protected code, or leave the case as it is now (strictly better than before). *Why:* the spec's constraints come first.
6. **An earlier design spec left as it is.** `docs/superpowers/specs/2026-11-07-webdav-design.md` (§3.10) still describes the quota's earlier leeway; the private spec names no pointer line for it, so it stays as a record of its time (brief: specs outside the topic stay as they are). The one pointer line the private spec names went into the discreet-vaults design.
7. **Public wording.** Where a fix's docs or comments first described a remaining limit in a way that could help someone abuse it, they were reworded to describe the behaviour only; the limit is in the private record.
8. **A test helper gained a parameter** (`RestClient` can send without following redirects), since the test host's redirect handler buffers a body to its declared length, which made a memory test measure the client. No new package.

## 7. Deviations, surprises and open points for Chris

- **S-02 and S-11 stay open for 0.4.0.** Both need a decision: the private pull request has both attempts of each, what the reviews found, and what is left (for one, a narrow change; for the other, three rendering details). Neither is on this branch.
- **Memory:** twice, test runs were killed by the kernel for lack of memory while several agents ran tests at once; the groups were rerun alone. The test MongoDB's 20,000-file limit held when the second server group ran namespace by namespace.
- **Flaky under load** (each green when its class ran alone, three times): `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault` (in almost every gate), `DavShareTests.The_expiry_sweep_retires_expired_shares`, and once `OwnerInactiveTests.Indexing_stops` (`OwnerInactiveTests.cs:156`, an embedding still present: the indexing worker's timing), which isn't in the brief's list yet.
- **Smaller things the reviews noted, outside the fixes:** `src/Kumbu.Server/Dav/DavXml.cs:64` returns its 64 KiB pooled buffer without clearing it; `ArrayPool<byte>.Shared` pools arrays up to 1 GiB since .NET 6 (an older note says 1 MiB); the outline and a note's name from its heading keep their own reading of `%%` comments.
- Details of what each fix's reviews left for Chris (residuals, visible side effects) are in the private record.

## 8. Pull requests

- Public: https://github.com/SCAI-NGO/kumbukumbu/pull/186 (this branch, not merged).
- Private: https://github.com/SCAI-NGO/kumbukumbu-security/pull/2 (plan, records and hand tests; on top of #1, not merged).
