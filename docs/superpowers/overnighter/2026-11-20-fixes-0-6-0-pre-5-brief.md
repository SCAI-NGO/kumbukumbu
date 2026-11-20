# Overnighter brief: fixes from the hand tests of 0.6.0-pre.5

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP. It is written in C# (.NET 10) with a React/Vite web app, on MongoDB. 0.6.0 is feature complete as `v0.6.0-pre.5`, and Chris hand-tested it today. This run fixes the small things those tests found, so that the remaining hand tests on Saturday run on a clean build and 0.6.0 can be released around Sunday.

The round is small on purpose. Fixes that are right and proven count for more than fixes that are many. A fix that can't pass its review is dropped and reported, and that is a fine outcome.

Nobody is available to answer questions during this run, and that is intended. Chris has approved the spec, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief.
2. **The spec:** `docs/superpowers/specs/2026-11-20-fixes-0-6-0-pre-5-design.md`. Chris approved it. Please don't edit it.
3. The specs the fixes touch, under `docs/superpowers/specs/`. Find them with `ls docs/superpowers/specs | rg -i '<topic>'`:
   - deep links
   - the workspace / split view
   - the discreet vaults
   - Publish 2.0 and publish bundles
   - large files
   - the archive
4. For background only, not as instructions: the "Environment" and "How to run the suite" sections of `docs/superpowers/overnighter/2026-11-19-tests-0-6-0-report.md`. They describe how the last runs met this environment.

## Where you run

- **The branch.** The session starts on the default branch, so first run `git fetch origin feature/fixes-0-6-0-pre-5 && git checkout feature/fixes-0-6-0-pre-5`. **Push only to this branch.**
- **Time:** have the pull request and the report pushed by **2026-11-21 07:00 UTC**.
- **Tools.** Install what you need without asking, and note it in the report. **Adding a dependency to the project is not allowed**: no new npm or NuGet package.
  - .NET 10 SDK, as `global.json` says, with `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
  - Node 26: the cloud environment's setup script puts it in `/opt/node26`. Run `export PATH=/opt/node26/bin:$PATH` and check with `node --version`. If it isn't there, install Node 26 from the nodejs.org tarball into `/opt/node26`.
  - pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if it's missing or older).
  - After any checkout, run `pnpm -C web install --frozen-lockfile` and `pnpm -C pdf install --frozen-lockfile` first.
- **Docker and MongoDB, needed only for the server tests and the end-to-end suite:**
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
  - **Run only one heavy command at a time**, meaning a build, a test run of more than one class, or the end-to-end suite. Run each under one file lock (`flock /tmp/heavy.lock <command>`) with `MSBUILDDISABLENODEREUSE=1`, and give it a 20-minute limit. Tell every subagent so.
- **Test commands:**
  - one web file: `pnpm -C web test --run <file>`
  - all web tests: `pnpm -C web test --run`
  - web build and typecheck: `pnpm -C web build`
  - server build: `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore`
- **The end-to-end suite:** `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt pnpm -C web e2e:0.6.0` takes about 10 minutes. For a single tag, start the stack with `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt web/e2e/stack/up.sh`, then run `pnpm -C web exec playwright test --project=setup` once, then `pnpm -C web exec playwright test --no-deps --grep "@deeplinks"`. Take the stack down with `web/e2e/stack/down.sh`.
- **Never test against a real host**: not staging, not production, not TensorX, and not GitHub beyond reading and opening the pull request.
- If superpowers skills aren't available, emulate subagent-driven work with the plain Agent tool and say so in the report.

## The Factory board

Report progress on the board in Kumbu² through the connector `Kumbu_Factory`, which reaches only the vault `Factory`.

1. Read the board's rules first: `sh board /Factory`.
2. Add your card in column `Running`, titled "Fixes 0.6.0-pre.5". The card holds the branch, this brief's path and the session's link. It has a `## Steps` checklist (Task 0, each spec item §2.1 to §2.6, the final gate, report and pull request) and a `## Log`.
3. Tick steps as they finish, and add a log line at least every hour.
4. At the end, move the card to Done with the pull request's link.

If the connector isn't there, say so in the report and carry on without the board.

## Open points for the run

All of these are yours to **decide**: settle each with the rules below and log it.

1. **F-A's cause.** Find where the current vault is set (the vault picker, the tree, `settleStart`, the last-vault state) before you fix it, and write the cause into the report. The fix goes into the shell or the deep link route, whichever is the one place that owns "the current vault". Don't add a second place.
2. **If an item turns out much bigger than the spec suggests**, for example many times the expected change: leave it out and record why in `docs/superpowers/open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-20-fixes-0-6-0-pre-5.md`.
2. It starts with **Task 0: baseline**. Build the web app and run all web tests once, and record the exact counts and failures. CI was green on `master` at `2d3b6858` (#263), so don't run the full server suites at Task 0. Run only the server classes a fix touches, if any.
   - Known flaky under load, green when its file or class runs alone: `web/src/note/commentRanges.test.tsx`, and on the server `DiscreetSocketTests`, `HiddenVaultTests`, `DavShareTests` and the sync-hub tests. If one fails, run it alone three times, record the result, and carry on.
   - Task 0 should take about 15 minutes. If it takes much longer, note it and carry on.
3. Every task stands alone: exact file paths with line anchors, the exact command for each test step with the expected pass or fail, then a red test, green, and every mutation on its own. No placeholders, no "see above".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in its findings, then commit the plan.

## How to work

- **Subagent-driven** (`superpowers:subagent-driven-development`):
  1. A fresh subagent per item. It writes a red test, makes it green, and commits locally. Then it tries every mutation on its own: break the fix, watch the test fail, restore with `git checkout -- <file>`.
  2. Then a spec-compliance and code-quality review by a fresh subagent.
- §2.3 (a constant), §2.4 (a text) and §2.5 (compose) are small. One subagent may take them together, with one commit each. §2.5 has no unit test: prove it with `docker compose config` showing the policy, and with `up.sh` and `down.sh` working.
- Subagents never merge, push or switch branches. You commit and push, after every task.
- **Commit messages** are imperative and free-form, with no Conventional Commits prefix. They say what the code now does, and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Stage files by name, never with `git add -A` or `git add .`.
- **The final gate**, as CI runs it:
  ```sh
  pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
  dotnet restore --locked-mode && dotnet build --configuration Release --no-restore
  ```
  Run the server test classes only if a fix touched the server, which none should. CI also runs `pnpm -C web exec playwright install --with-deps chromium && pnpm -C web test:diagrams` and the `pdf` job; run them only if a fix touches diagrams or `pdf/`. Then run the end-to-end suite `e2e:0.6.0` once from a fresh stack. Note the known flaky tests if they appear, and rerun them alone. CI on the pull request runs the rest: check it with `gh pr checks`, rerun a flaky job once, and say so.
- **Style:**
  - British English in code, comments, docs and commits. The UI is in English.
  - Match the surrounding code: its comment density, naming and idiom. Comments only where the reasoning isn't obvious.
  - New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
  - Read the texts the app shows people aloud: a person's wish, plainly said, no jargon.
- **Names:** no customer or employer anywhere in the repository. Use Alice, Bob, Dana, `Home` and `Work`. The one organisation it may name, where needed, is its owner: Second Circuit – Association for digital freedom of thought.
- **Web API types:** types by hand in the feature's API module. **Don't regenerate `web/src/api/schema.d.ts`.**

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working, apart from what the spec changes. Prefer the reversible change.
2. **True to the spirit of the application.** Campy: plain notes and frontmatter, no new stores. Human-centric: the screen speaks of the person's wish. Honest: a button does what it says, and nothing is hidden quietly. For Chris, honesty is part of the security architecture.
3. **Opens no new security hole.** In particular, a deep link never reveals whether a vault or note exists (spec §4).
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess. Leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way, in the report and in the pull request's description under **Decisions made without Chris**. Give the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version or `CHANGELOG.md`.
- No secrets in logs, assertions or the report.
- Specs outside this topic stay as they are. The one exception is the one-line amendment of §2.3. If another spec looks wrong, mention it in the report.
- **Security boundary:** spec §4. `Auth/`, `Crypto/`, sharing, roles, sessions and the CSPs are not touched. A fix that needs them goes back unfixed and is reported.
- **At the end:**
  - Add a row with the pull request's link to `docs/superpowers/open-items.md`.
  - Write the hand-test checklist of spec §5 into the report: numbered steps, where to look and what passes, runnable by Chris on staging.
  - Don't write dates that depend on the day of the run; say "today".

## Out of scope

Spec §3, in full.

## The report

Open the pull request from `feature/fixes-0-6-0-pre-5` to `master`, titled "Fix what the hand tests of 0.6.0-pre.5 found", and don't merge it.

- **Its description:** each item with its state, the decisions made without Chris, and the hand-test checklist. It ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **Open the pull request before the report commit**, then push the report. A branch tip that is a docs-only commit may run no CI.
- **The report** goes to `docs/superpowers/overnighter/2026-11-20-fixes-0-6-0-pre-5-report.md`. It holds:
  - the environment
  - the plan review's findings and what you did with them
  - Task 0, planned against measured
  - for each item, its commits, the real test output and every mutation
  - F-A's cause
  - the end-to-end run
  - the decisions made without Chris
  - deviations and open points
  - the hand-test checklist
  - the pull request's link

Never force-push. If something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it's available. Otherwise the pull request is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
