# Overnighter brief: the follow-ups of the 0.7.0 fixes

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, a TypeScript data layer for the browser (`packages/eidolon-core`) and a Node PDF sidecar, on MongoDB. 0.7.0 brings an offline mode: the web app keeps chosen vaults sealed in the browser and syncs them back. Its fix run (#352) is merged into `master` (`a57d05af`); it left a short list of points that Chris decided today. This run builds them, so that `v0.7.0-pre.6` holds them.

It is a small run: about a dozen items, most of a few lines. Items that are right and proven count for more than many. An item that can't pass its review is dropped and reported, and that is a fine outcome.

Nobody is available to answer questions during this run, and that is intended: Chris has reviewed and approved both specs, decisions included. Everything after them is yours, from the plan to the pull requests. The decision rules below tell you how to settle what the specs leave open.

## Two repositories, and what goes where

The repository you start in (`SCAI-NGO/kumbukumbu`) is **public**. The security findings' details live in the **private** repository `SCAI-NGO/kumbukumbu-security`. None of them may reach the public repository before the release: no branch name, commit message, test name, comment, plan, report or pull request. This brief is public too, so it names those findings by id only. **Never pair an id with a file, feature or pull request in anything public**, not even in a log line.

- **Public, branch `feature/follow-ups-0-7-0`:** the code of each item, each with a behaviour test worded as what the code must do, never how it could be abused, under a neutral commit message; F-15's test (public spec §3), whose commit may say what it pins; the public report; one pull request to `master`.
- **Private, branch `fixes/0-7-0-follow-ups`** (on `fixes/0-7-0`, pull request #15, not merged, which is intended): the plan; the record of each item in `audits/2026-11-26-0.7.0.md`, new section "Follow-ups"; the investigation's answer; the refreshed patches; one pull request to `fixes/0-7-0`.

## What to read, in this order

1. This brief.
2. **The public spec:** `docs/superpowers/specs/2026-11-27-follow-ups-0-7-0-design.md`. Approved; please don't edit it.
3. **The private spec:** `specs/2026-11-27-0.7.0-follow-ups-design.md` on the private `fixes/0-7-0-follow-ups`. Approved; please don't edit it. It builds on `specs/2026-11-26-0.7.0-fixes-design.md` (§1, the policy, holds unchanged).
4. **The findings:** `audits/2026-11-26-0.7.0.md` on the same private branch, its sections on each id, "Fixes" and "Open for Chris"; the tests under `audits/2026-11-26-0.7.0/`.
5. **The last run's report:** `docs/superpowers/overnighter/2026-11-26-fixes-0-7-0-report.md` (environment, how the gates and the end-to-end suite ran).
6. The design the items touch: `docs/superpowers/specs/2026-11-22-offline-mode-design.md` (§4.4), the ADRs in `docs/adr/`, `docs/security-architecture.md`.

## Where you run

- **The branches.**
  - The session starts on the public repository's default branch, so first run `git fetch origin feature/follow-ups-0-7-0 && git checkout feature/follow-ups-0-7-0`.
  - The private repository is set up as a second repository of the session. If it isn't beside the first one, clone it (`git clone https://github.com/SCAI-NGO/kumbukumbu-security`) and check out `fixes/0-7-0-follow-ups` there.
  - **Push only to `feature/follow-ups-0-7-0` (public) and `fixes/0-7-0-follow-ups` (private).** Nothing to `maintenance/0.6` or any other branch: Chris releases the maintenance line himself.
- **Time:** have both pull requests and the reports pushed by **2026-11-28 05:00 UTC**. **No new item after 03:00 UTC**: then the final gate, the end-to-end run, the reports and the pull requests.
- **Tools.** Install what you need without asking, and note it in the report. **Adding a dependency to the project is not allowed**: no new npm or NuGet package and no upgrade.
  - .NET 10 SDK as `global.json` says, with `DOTNET_CLI_TELEMETRY_OPTOUT=1`, **`MSBUILDDISABLENODEREUSE=1`**, `DOTNET_CLI_USE_MSBUILD_SERVER=0` and `UseSharedCompilation=false` (as environment variables), and `-nodeReuse:false` on every `dotnet build`: a build server left running holds the lock (below) for everyone.
  - Node 26: the cloud environment's setup script puts it in `/opt/node26`; `export PATH=/opt/node26/bin:$PATH` and check with `node --version`. If it isn't there, install Node 26 from the nodejs.org tarball into `/opt/node26`.
  - pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if it's missing or older).
  - After any checkout, run `pnpm -C web install --frozen-lockfile` (the web app and the Eidolon core, one workspace with one `pnpm-lock.yaml` at the root). The PDF sidecar keeps its own: `pnpm -C pdf install --frozen-lockfile --ignore-workspace`, only for the gate.
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
  - **Only one heavy run at a time** (a build, or a test run of more than one class): every heavy command under one lock, `flock -o /tmp/heavy.lock timeout 20m <command>`. `-o` keeps an idle build server from inheriting the lock. Tell every subagent so, with the command.
- **Test commands:**
  - build: `dotnet build --configuration Release --no-restore -nodeReuse:false` (after `dotnet restore --locked-mode`);
  - one core class: `dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build -- --filter-class "Kumbu.Core.Tests.<Namespace>.<ClassName>"` (with the namespace; `--filter-class` and `--filter-namespace` don't combine in one call);
  - one server class: the same with `tests/Kumbu.Server.Tests`;
  - one web file: `pnpm -C web test --run <file>`;
  - the Eidolon core: `pnpm -C packages/eidolon-core test` and `pnpm -C packages/eidolon-core build` (its `tsc` keeps DOM types out);
  - the service worker's tests are `web/sw/*.test.ts`, run with the web app's.
- **The end-to-end suite** (Docker with buildx; else `E2E_STACK=dotnet`): `E2E_PROXY_CA=/root/.ccr/ca-bundle.crt web/e2e/stack/run.sh --grep "@smoke|@offline|@devices|@sw|@graph|@similar|@synthesis|@overview" --grep-invert "@search"` from the repository root. **Never `E2E_SEARCH=1`, `pnpm e2e:0.7.0` or its `:tensorx`**: they fetch the model from huggingface.co or need TensorX. Install the browsers first: `pnpm -C web exec playwright install --with-deps chromium chromium-headless-shell firefox`. `run.sh` passes `--pass-with-no-tests`: a run with no passed test counts as not run. It deletes `web/e2e/.results` when it exits; keep evidence from the console, and **keep the traces of a failing test** (`--trace retain-on-failure`, copied out before the script exits): `gaps #15` failed once with no trace kept. Test runs that call `shot()` overwrite PNGs under `docs/superpowers/overnighter/`: restore them with `git checkout` before committing.
- **Server tests fake the clock in 2037**, because MongoDB's TTL expires sessions by the real clock. Time in tests comes from the injected clock (`FakeTimeProvider`), never the wall clock.
- **Never test against a real host**: not staging, not production, not TensorX, not huggingface.co, not GitHub beyond reading and opening pull requests. The private spec's harness runs against a server you start yourself in the session.
- If superpowers skills aren't available, emulate subagent-driven work with the plain Agent tool and say so in the report.

## The Factory board

Report progress on the board in Kumbu² through the connector `Kumbu_Factory`, which reaches only the vault `Factory`. Read its rules first (`sh board /Factory`), add your card in `Running` titled "Follow-ups 0.7.0" with the branches, this brief's path, the session's link, a `## Steps` checklist and a `## Log`. **The steps are generic, never a security finding:** "Task 0", "Items", "Investigation", "Patches checked", "End-to-end suite", "Reports and pull requests". A log line at least every hour; no id, file, area or detail of a finding. At the end, move the card to Done with the public pull request's link. If the connector isn't there, say so in the report and carry on.

## Open points for the run

All of these are yours to **decide**: settle each with the rules below and log it in the report.

1. **The private repository is unreachable.** Then do public spec §3 only, and say so in the public report and pull request, with the ids only.
2. **The order:** the private spec's §2 top to bottom, with these first because others build on them or they take the longest: the harness (start it early, it runs beside the rest), then the investigation. Items may be built in parallel in separate worktrees, at most three implementers at a time, and cherry-picked one at a time after their review.
3. **An item whose fix turns out many times bigger than its row suggests** (it needs a new store, or breaks a parser elsewhere): leave it out, record why in "Open for Chris", carry on.
4. **New end-to-end tests** carry a tag inside the suite's `--grep` set (`@offline` for the offline ones) and a title that says what the app does.
5. **The web types:** types by hand in the feature's API module; **don't regenerate `web/src/api/schema.d.ts`.**
6. **ADRs:** a changed decision gets a new record in `docs/adr/` (`template.md`, listed in `docs/adr/README.md`) that supersedes the old one, never an edit of it. New records take the next free number on `master` **that is not 0026, 0027 or 0034 to 0041** (taken on `release/0.8.0`): check `docs/adr/` first; 0042 to 0044 are taken.
7. **The Eidolon core's sync part** (`src/engine`, `src/api`, `src/live`) is vendored by the Obsidian plugin: change it here only, and list every changed file of it in the report so the plugin's copy can follow after the release.

## Planning

1. Write the plan with `superpowers:writing-plans` **in the private repository**, as `plans/2026-11-27-0.7.0-follow-ups.md` on `fixes/0-7-0-follow-ups`. It names the findings, so it must not be in the public repository.
2. It starts with **Task 0: baseline**, about 15 minutes: build in Release, run the test classes and files the items touch, `pnpm -C packages/eidolon-core test` and `pnpm -C web test --run src/offline sw`; record the counts. CI was green on `master` at `a57d05af`. Expected skips: the tests bound to the embedding model or a TensorX key, and the token-bound tests without `KUMBU_TOKENIZER`. Known flaky under load, each green alone: `web/src/routes/DeepLink.test.tsx`, `web/src/shell/split.test.tsx`, the sync-hub tests (`SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once`), `RekeyTests`. If one fails, run it alone three times, record the result, carry on.
3. Every task stands alone: exact file paths with line anchors, the exact command for each test step with the expected pass or fail, then a red test, green, and every mutation on its own. No placeholders.
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets both specs, this brief and the plan, not your conclusions. Fold in its findings, then commit the plan to the private branch.

## How to work

- **Subagent-driven** (`superpowers:subagent-driven-development`): a fresh subagent per item: red test, green, commit locally, then every mutation on its own (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance and code-quality review by a fresh subagent, and for a security item the fix review of the private spec's policy. The fix review also checks the public commit message and test names against the publication rule.
- Subagents never merge, push or switch branches. You commit and push.
- **Commit messages** are imperative and free-form, with no Conventional Commits prefix, and end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. **On the public branch, a security item's message carries no finding id, no "security"**: it says what the code now does, in a subject alone or with a body that says no more than the subject.
- **Keep the public tree clean.** Confidential notes, proof tests and the harness live in the private clone or a scratch copy outside every public worktree. Stage files by name, never `git add -A`, `git add .` or `commit -a`. In every public text, security ids appear in numeric order only. Before every public push, `git diff origin/<branch> --stat`. **Before anything public is pushed at the end** (report, pull request description, `open-items.md`): a fresh reviewer gets the public texts and the private record's summary and checks that nothing points to a confidential finding.
- **Byte preservation:** every frontmatter write keeps every other byte.
- **The full gate**, as CI runs it, once at the end, each line under the lock:
  ```sh
  dotnet restore --locked-mode
  dotnet build --configuration Release --no-restore -nodeReuse:false
  dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Api --filter-namespace Kumbu.Server.Tests.Mcp --filter-namespace Kumbu.Server.Tests.Auth --filter-namespace Kumbu.Server.Tests.Net
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Accounts --filter-namespace Kumbu.Server.Tests.Sharing --filter-namespace Kumbu.Server.Tests.Welcome
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Vaults --filter-namespace Kumbu.Server.Tests.Skills --filter-namespace Kumbu.Server.Tests.Search --filter-namespace Kumbu.Server.Tests.Metrics
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-not-namespace Kumbu.Server.Tests.Api --filter-not-namespace Kumbu.Server.Tests.Mcp --filter-not-namespace Kumbu.Server.Tests.Auth --filter-not-namespace Kumbu.Server.Tests.Net --filter-not-namespace Kumbu.Server.Tests.Accounts --filter-not-namespace Kumbu.Server.Tests.Sharing --filter-not-namespace Kumbu.Server.Tests.Welcome --filter-not-namespace Kumbu.Server.Tests.Vaults --filter-not-namespace Kumbu.Server.Tests.Skills --filter-not-namespace Kumbu.Server.Tests.Search --filter-not-namespace Kumbu.Server.Tests.Metrics
  pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
  pnpm -C packages/eidolon-core test && pnpm -C packages/eidolon-core build
  pnpm -C web exec playwright install --with-deps chromium && pnpm -C web test:diagrams && pnpm -C web test:shell
  pnpm -C pdf install --frozen-lockfile --ignore-workspace && pnpm -C pdf test && pnpm -C pdf build
  ```
  Then the end-to-end suite from a fresh stack. CI also runs WebDAV's `litmus` and the image build: check them on the pull request with `gh pr checks`; rerun a job that fails once for no reason of the change (the browsers' apt install failed once on 2026-11-27), and say so.
- **Style:** British English in code, comments, docs, commits and user-visible text; the UI is English. Match the surrounding code: its comment density, naming and idiom. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **Names:** no customer or employer anywhere in the repository; use Alice, Bob, Carol, Dana, `Home` and `Work`. The one organisation it may name, where needed, is its owner: Second Circuit – Association for digital freedom of thought.
- **The changelog** of the version is kept by Chris in Kumbu²: write in the report one sentence per item that changes something for people (most don't; Add link accepting every name is one), which he takes over, in public words.

## Deciding without Chris

When a spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working, apart from what the specs change. Prefer the reversible change.
2. **Gives nothing away.** Nothing about an unreleased weakness on a public branch.
3. **True to the spirit of the application.** Campy: plain notes and frontmatter, no new stores. Human-centric. Honest: a button does what it says, and nothing is hidden quietly.
4. **Opens no new security hole.**
5. **True to the specs.**

If a question can only be answered by changing a spec's scope, don't guess: leave that part out and record it, for a security point in "Open for Chris" of the private findings file, for the rest in `docs/superpowers/open-items.md`. Log every decision you made this way in the report and in the pull request's description under **Decisions made without Chris**, with the options you saw and why you chose this one, without any security detail in public.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version.
- No secrets in logs, assertions or the reports.
- **Security boundary:** public spec §6. Hard: a change that needs what it marks "not touched", beyond what the private spec allows, goes back undone.
- **At the end:** a row with the public pull request's link in `docs/superpowers/open-items.md`. A hand-test checklist only for what a person can see change (numbered steps, where to look, what passes, runnable on staging; "today", never a date); the private one into the private pull request.

## Out of scope

Public spec §7 and private spec §3, in full.

## The report

Two pull requests, neither merged.

1. **Private**, in `SCAI-NGO/kumbukumbu-security`, from `fixes/0-7-0-follow-ups` to **`fixes/0-7-0`**, titled "Follow-ups of the 0.7.0 fixes": the plan, the record of each item in "Follow-ups", the investigation's answer, the harness's result, the refreshed patches, what stays open.
2. **Public**, in `SCAI-NGO/kumbukumbu`, from `feature/follow-ups-0-7-0` to `master`, titled "Follow up the fixes of 0.7.0".
   - **Its description:** the items by id and done or not only, F-15's test by what it pins, the decisions made without Chris; ending with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
   - **Open the pull request before the report commit**, then push the report: a branch tip that is a docs-only commit may run no CI.
   - **The report,** `docs/superpowers/overnighter/2026-11-27-follow-ups-0-7-0-report.md`: the environment; Task 0, planned against measured; F-15's test with its output and mutation; the security items by id and state only; the end-to-end run; the decisions made without Chris; deviations and open points; the pull requests' links (the private one by its URL only).

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it's available; otherwise the pull requests are the signal. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
