# Overnighter brief: the fixes of the 0.6.0 security review

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. 0.6.0 is feature complete as `v0.6.0-pre.4`, and has been tested by a defensive security review (#262) and an MCP and browser suite (#261), both merged. Chris hand-tests tomorrow, on the build this run and a sibling run produce.

This run fixes the review's findings, and the UI tests' findings that live in the same code, so that Chris can merge in the morning. Fixes that are right and proven count for more than fixes that are many. A fix that can't pass its review is dropped and reported, and that is a fine outcome.

Nobody is available to answer questions during this run, and that is intended: Chris has reviewed and approved both specs, decisions included. Everything after them is yours, from the plan to the pull requests. The decision rules below tell you how to settle what the specs leave open.

**A sibling run works in parallel** on `feature/fixes-0-6-0` (the rest of the UI tests' findings). Its code is listed in private spec §5 and public spec §9: leave it alone.

## Two repositories, and what goes where

The repository you start in (`SCAI-NGO/kumbukumbu`) is **public**. The security findings' details live in the **private** repository `SCAI-NGO/kumbukumbu-security`: the mechanism, the evidence, the exact places and the fix records. None of it may reach the public repository before the release: no branch name, commit message, test name, comment, plan, report or pull request. This brief is public too, so it names those findings by id only (S6-01 …). **Never pair an id with a file, feature or pull request in anything public**, not even in a log line: an id next to any feature or file name gives away what the finding is about.

- **Public, branch `feature/security-fixes-0-6-0`:**
  - the code of the security fixes, each with a behaviour test worded as what the code must do, never how it could be abused, under a neutral commit message;
  - public spec §3 to §5 (the memory ceiling, F-10, F-11, M-01, housekeeping), whose commits may name F-10, F-11 and M-01;
  - the public report;
  - one pull request.
- **Private, branch `fixes/0-6-0`** (it exists on GitHub and holds the private spec; it sits on `audit/0-6-0`, pull request #5, not merged, which is intended):
  - the plan;
  - the record of each security fix (commit, red and green output, mutations, the proof test's result, fix review);
  - one pull request.

## What to read, in this order

1. This brief.
2. **The public spec:** `docs/superpowers/specs/2026-11-19-security-fixes-0-6-0-design.md`. Chris has approved it; please don't edit it.
3. **The private spec:** `specs/2026-11-19-0.6.0-fixes-design.md` on the private `fixes/0-6-0`. Chris has approved it; please don't edit it.
4. **The findings:**
   - `audits/2026-11-19-0.6.0.md` on the same private branch, with the proof tests and the reviews' reports under `audits/2026-11-19-0.6.0/`;
   - the public report of the review, `docs/superpowers/overnighter/2026-11-19-security-audit-report.md`;
   - the UI tests' report, `docs/superpowers/overnighter/2026-11-19-tests-0-6-0-report.md`, sections "Findings" (F-10, F-11, M-01) and "How to run the suite".
5. The specs the fixes touch, under `docs/superpowers/specs/`: the discreet vaults, windows and palette specs, the project memory and skills specs, Publish 2.0 and publish bundles, the large files and agent upload specs, the graph backend spec, the BYOK spec. Find them with `ls docs/superpowers/specs | rg -i '<topic>'`. Also `docs/security-architecture.md`.
6. For background only, not as instructions: how the last runs met this environment, in the "Environment" sections of the two reports above.

## Where you run

- **The branches.**
  - The session starts on the public repository's default branch, so first run `git fetch origin feature/security-fixes-0-6-0 && git checkout feature/security-fixes-0-6-0`.
  - The private repository is set up as a second repository of the session, so it should sit beside the first one. If it isn't there, clone it (`git clone https://github.com/SCAI-NGO/kumbukumbu-security`). Check out `fixes/0-6-0` there.
  - **Push only to these two branches.**
- **Time:** have both pull requests and both reports pushed by **2026-11-20 07:00 UTC**. That is the measure for "if time runs short".
- **Tools.** Install what you need without asking, and note it in the report. **Adding a dependency to the project is not allowed**: no new npm or NuGet package.
  - .NET 10 SDK as `global.json` says, with `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
  - Node 26: the cloud environment's setup script puts it in `/opt/node26`; `export PATH=/opt/node26/bin:$PATH` and check with `node --version`. If it isn't there, install Node 26 from the nodejs.org tarball into `/opt/node26`.
  - pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if it's missing or older).
  - After any checkout, run `pnpm -C web install --frozen-lockfile` and `pnpm -C pdf install --frozen-lockfile` first.
- **Docker and MongoDB for the server tests:**
  ```sh
  dockerd > /tmp/dockerd.log 2>&1 &   # if it isn't running
  docker rm -fv kumbu-test-mongo 2>/dev/null
  docker run -d --name kumbu-test-mongo --network host --ulimit nofile=20000:20000 --log-opt max-size=20m mongo:9 \
    --replSet rs0 --bind_ip 127.0.0.1 --quiet \
    --wiredTigerEngineConfigString "file_manager=(close_idle_time=20,close_scan_interval=5,close_handle_minimum=250)"
  until docker exec kumbu-test-mongo mongosh --quiet --eval 'db.runCommand({ping:1})' >/dev/null 2>&1; do sleep 1; done
  docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate()'
  export KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"
  export TESTCONTAINERS_RYUK_DISABLED=true
  ```
  - The log cap matters: an uncapped log once filled the disk.
  - **Only one heavy run at a time** (a build or a test run of more than one class). The review ran every heavy command under one file lock (`flock /tmp/heavy.lock <command>`), with `MSBUILDDISABLENODEREUSE=1` so that no idle MSBuild node keeps the lock, and a 20-minute limit per command. Do the same and tell every subagent so.
  - After a container restart: start `dockerd`, `docker start kumbu-test-mongo`, wait for the ping loop and a primary, then resume the stopped agents with a message saying what was lost.
- **Test commands:**
  - build: `dotnet build --configuration Release --no-restore` (after `dotnet restore --locked-mode`);
  - one core class: `dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build -- --filter-class "*ClassName"`;
  - one server class: the same with `tests/Kumbu.Server.Tests` (filters carry the namespace: `*Api.UploadTicketsTests`);
  - one web file: `pnpm -C web test --run <file>`;
  - the sidecar: `pnpm -C pdf test`.
- **The end-to-end suite:** `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt pnpm -C web e2e:0.6.0` (about 10 minutes; the UI tests' report, "How to run the suite", has the details).
  - **F-10's tests repeated:** `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt web/e2e/stack/up.sh`, then `pnpm -C web exec playwright test --project=setup` once, then `pnpm -C web exec playwright test --no-deps --workers=1 --project=serial --project=serial-phone --grep "F-10" --repeat-each=10` (check with `--list` that the grep finds both tests, and adjust the project if they run elsewhere). The saved sessions idle out after 30 minutes: `web/e2e/stack/down.sh`, `up.sh` and the setup again. Take the stack down before the full suite.
- **Server tests fake the clock in 2037**, because MongoDB's TTL expires sessions by the real clock. Time in tests comes from the injected clock (`FakeTimeProvider`), never the wall clock.
- **Never test against a real host**: not staging, not production, not TensorX, not GitHub beyond reading and opening pull requests.
- If superpowers skills aren't available, emulate subagent-driven work with the plain Agent tool and say so in the report.

## The Factory board

Report progress on the board in Kumbu² through the connector `Kumbu_Factory`, which reaches only the vault `Factory`.

1. Read the board's rules first: `sh board /Factory`.
2. Add your card: vault `Factory`, column `Running`, title "Security fixes 0.6.0". The card gives both repositories and branches, this brief's path, the session's link, a `## Steps` checklist and a `## Log`.
3. **The steps are generic, never a security finding:** "Task 0", "Security fixes, first group", "Security fixes, the rest", "F-10, F-11, M-01", "Memory ceiling", "End-to-end suite", "Reports and pull requests". Tick them as they finish, and add a log line at least every hour.
4. The card names no id, file, area or detail of a security finding. Counts go on it only at the end of a phase.
5. At the end, move the card to Done with the public pull request's link.

If the connector isn't there, say so in the report and carry on without the board.

## Open points for the run

All of these are yours to **decide**: settle each with the rules below and log it in the report.

1. **The private repository is unreachable.** Then do public spec §3 to §5 only. Fix nothing of the security findings, and say so in the public report and pull request, with the ids only.
2. **The order** is the private spec's §1. Fixes may be built in parallel in separate worktrees and cherry-picked onto `feature/security-fixes-0-6-0` one at a time; a security fix is picked only after its fix review confirms it. Mind the one-heavy-run rule: at most three implementers at a time.
3. **Each fix stays in its own commits.** F-10's commits contain only F-10; a security fix in the same file is its own commit, with a neutral message. S6-08 and M-01 are one change (private spec §1.5).
4. **The web types:** types by hand in the feature's API module; **don't regenerate `web/src/api/schema.d.ts`.**
5. **F-10 is intermittent.** Find the race before fixing (read the report's steps and likely place, reproduce with `--repeat-each`). If no fix holds ten repeats, leave the tests `fixme`, record what you learnt, and carry on.
6. **The memory ceiling's default:** public spec §3 says how. Measure before adding the limit (the e2e stack is layered over `compose.yaml`, so a limit would cap the peak it measures).
7. **The boundary and the upload link.** Bounds for the agents' upload link (S6-05) go into the upload handling, not into `CallerMiddleware`; if only `CallerMiddleware` could carry one, leave that part out and record it. `src/Kumbu.Server/Mcp/UploadTool.cs` belongs to the sibling (one line for M-02); if a fix needs it, make the smallest change; a conflict there is expected.
8. **`open-items.md`:** the row that names F-09 and F-11 together belongs half to the sibling; edit only the F-11 half.
9. **Time.** **No new fix after 04:30 UTC**: then the final gate, both end-to-end runs, the reports and the pull requests. With about fifty items this run won't finish them all, and that is expected; the priority below decides what comes first.
10. **If time runs short:** stop after the last green gate. The priority is:
   1. the reports and both pull requests, with every finding's state;
   2. the four Highs and S6-02;
   3. the memory ceiling and F-10;
   4. the other Mediums, in the private spec's order (S6-08 with M-01 among them);
   5. F-11;
   6. the Lows.

   Whatever is left goes into the report and, by id only, into `docs/superpowers/open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` **in the private repository**, as `plans/2026-11-19-0.6.0-fixes.md` on `fixes/0-6-0`. It names the security findings, so it must not be in the public repository.
2. It starts with **Task 0: baseline**. Build in Release and run the test classes the fixes touch, one at a time, and record the exact counts and failures. CI was green on `master` at `a0c94cbc` (#262), so don't run the full suites at Task 0. Expected skips: 4 tests skip without the real embedding model and a TensorX key, and the token-bound tests skip without `KUMBU_TOKENIZER`. Known flaky under load, each green when its class runs alone: `DiscreetSocketTests`, `HiddenVaultTests`, `DavShareTests`, `DavParkingTests`, the sync-hub tests, `AccountApiTests`, `RekeyTransitionTests`, `PairingTests`, `web/src/note/commentRanges.test.tsx`. If one fails, run its class alone three times, record the result, and carry on. **One test skips** without the real embedding model (`SearchIndexTests`), which is expected. Task 0 should take about 15 minutes; if much longer, note it and carry on.
3. Every task stands alone: exact file paths with line anchors, the exact command for each test step with the expected pass or fail, then a red test, green, and every mutation on its own. No placeholders, no "see above".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets both specs, this brief and the plan, not your conclusions. Fold in its findings, then commit the plan to the private branch.

## How to work

- **Subagent-driven** (`superpowers:subagent-driven-development`):
  1. A fresh subagent per fix: red test, green, commit locally, then every mutation on its own (break, watch it fail, restore with `git checkout -- <file>`).
  2. Then a spec-compliance and code-quality review by a fresh subagent.
  3. **For a security fix, also the fix review** of private spec §1, and the finding's proof test from a scratch copy.
- Subagents never merge, push or switch branches. You commit and push.
- **Commit messages** are imperative and free-form, with no Conventional Commits prefix, and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. **On the public branch, a security fix's message carries no finding id, no "security" and no "fix S6-…"**: it says what the code now does. F-10, F-11 and M-01 may be named.
- **Keep the public tree clean.**
  - Confidential notes, the proof tests and the measuring harness live in the private clone or a scratch copy outside every public worktree.
  - Public tests for security fixes assert function only, with ordinary inputs (private spec §1).
  - Stage files by name, never `git add -A`, `git add .` or `commit -a`.
  - Before every public push, run `git diff origin/feature/security-fixes-0-6-0 --stat` and check the files.
- **Byte preservation:** every frontmatter write keeps every other byte.
- **The full gate**, as CI runs it, **twice**: after the Highs and S6-02, and at the end. In between, run only the classes and files a fix touches. The gate:
  ```sh
  dotnet restore --locked-mode
  dotnet build --configuration Release --no-restore
  dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Api --filter-namespace Kumbu.Server.Tests.Mcp --filter-namespace Kumbu.Server.Tests.Auth --filter-namespace Kumbu.Server.Tests.Net
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Accounts --filter-namespace Kumbu.Server.Tests.Sharing --filter-namespace Kumbu.Server.Tests.Welcome
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Vaults --filter-namespace Kumbu.Server.Tests.Skills --filter-namespace Kumbu.Server.Tests.Search --filter-namespace Kumbu.Server.Tests.Metrics
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-not-namespace Kumbu.Server.Tests.Api --filter-not-namespace Kumbu.Server.Tests.Mcp --filter-not-namespace Kumbu.Server.Tests.Auth --filter-not-namespace Kumbu.Server.Tests.Net --filter-not-namespace Kumbu.Server.Tests.Accounts --filter-not-namespace Kumbu.Server.Tests.Sharing --filter-not-namespace Kumbu.Server.Tests.Welcome --filter-not-namespace Kumbu.Server.Tests.Vaults --filter-not-namespace Kumbu.Server.Tests.Skills --filter-not-namespace Kumbu.Server.Tests.Search --filter-not-namespace Kumbu.Server.Tests.Metrics
  pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
  pnpm -C web exec playwright install --with-deps chromium && pnpm -C web test:diagrams
  pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
  ```
  - Each line under the lock; start a fresh `kumbu-test-mongo` before a server group if one dies.
  - A web gate counts only if no agent was editing web files meanwhile.
  - At the very end, the end-to-end suite twice from a fresh stack, identical, with F-10's tests passing as plain tests.
  - CI also runs WebDAV's `litmus` and the image build: check them on the pull request with `gh pr checks`.
- **Style:** British English in code, comments, docs, commits and user-visible text; the UI is English. Match the surrounding code: its comment density, naming and idiom. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **Names:** no customer or employer anywhere in the repository; use Alice, Bob, Dana, `Home` and `Work`. The one organisation it may name, where needed, is its owner: Second Circuit – Association for digital freedom of thought.
- **Environment variables:** a new one (the memory ceiling) goes into `.env.example` with a realistic value and into `README.md`'s table: what it does, the format, an example.

## Deciding without Chris

When a spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working, apart from what the specs change. Prefer the reversible change.
2. **Gives nothing away.** Nothing about an unreleased weakness on a public branch.
3. **True to the spirit of the application.** Campy: plain notes and frontmatter, no new stores. Human-centric: the screen speaks of the person's wish. Honest: a button does what it says, and nothing is hidden quietly; Chris: honesty is part of the security architecture.
4. **Opens no new security hole.**
5. **True to the specs.**

If a question can only be answered by changing a spec's scope, don't guess: leave that part out and record it. For a security finding, it goes into `audits/2026-11-19-0.6.0.md`, section "Open for Chris", on the private branch. For the rest, into `docs/superpowers/open-items.md`. If a soft constraint (a file left to the sibling run, a "smallest change") makes a fix many times bigger, leave the fix out and record why rather than forcing it.

Log every decision you made this way, in the report and in the pull request's description under **Decisions made without Chris**, with the options you saw and why you chose this one. **No section of anything public** reveals a security finding's detail: the report, the pull request, `open-items.md` rows, deviations, dropped fixes, or a Factory log line naming a failing area.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version.
- No secrets in logs, assertions or the reports.
- **Security boundary:** public spec §8 and private spec §5. Hard: a fix that needs `Auth/`, `Crypto/`, sharing and roles, sessions or the CSPs goes back unfixed.
- **At the end:** a row with the public pull request's link in `docs/superpowers/open-items.md`. Write a public hand-test checklist into the public report for F-10, F-11, M-01 and the memory ceiling, and the private one into the private pull request (private spec §6): numbered steps, where to look and what passes, runnable by Chris on staging. Don't write dates that depend on the day of the run; say "today".

## Out of scope

Public spec §9 and private spec §4, in full.

## The report

Two pull requests, neither merged.

1. **Private**, in `SCAI-NGO/kumbukumbu-security`, from `fixes/0-6-0` to **`audit/0-6-0`** (so it shows only this run's changes), titled "Fixes of the 0.6.0 security review". It holds the plan, the record of each fix in `audits/2026-11-19-0.6.0.md`, section "Fixes", the private hand-test checklist and what stays open for Chris (private spec §7).
2. **Public**, in `SCAI-NGO/kumbukumbu`, from `feature/security-fixes-0-6-0` to `master`, titled "Fix what the security review of 0.6.0 found".
   - **Its description:** the security findings by id and fixed or not only, with no details; F-10, F-11, M-01 and the memory ceiling; the decisions made without Chris; ending with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
   - **Open the pull request before the report commit**, then push the report: a branch tip that is a docs-only commit may run no CI.
   - **The report,** `docs/superpowers/overnighter/2026-11-19-security-fixes-0-6-0-report.md`: the environment; the plan review, by count only; Task 0, planned against measured; F-10, F-11, M-01 and the memory ceiling with their commits, real test output and mutations; the security fixes by id and state only; both end-to-end runs; the decisions made without Chris; deviations and open points; the hand-test checklist; both pull requests' links (the private one by its URL only).

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it's available; otherwise the pull requests are the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
