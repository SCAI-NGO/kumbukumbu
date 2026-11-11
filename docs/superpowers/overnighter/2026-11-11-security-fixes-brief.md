# Overnighter brief: the fixes of the 0.4.0 security audit

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. Last night's security audit of 0.4.0 found ten findings of Medium or higher that are still open. All of them must be fixed before 0.4.0 is released, and Chris would like to release this afternoon. Fixes that are right and proven count for more than fixes that are many: a fix that can't pass its review is dropped and reported, and that is a fine outcome.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, decisions included, and everything after it is yours, from the plan to the pull requests. The decision rules below tell you how to settle what the spec leaves open.

## Two repositories, and what goes where

The repository you start in (`SCAI-NGO/kumbukumbu`) is **public**. The findings' details are kept in the **private** repository `SCAI-NGO/kumbukumbu-security`, and so are the spec, the plan and every fix record: how to exploit a finding, its proof, its exact places. None of these may reach the public repository before the release: not a branch name, commit message, test name, comment, plan, report or pull request. This brief is public too, so it names the findings only by their ids (S-01 …).

- **Public**, branch `fix/0-4-0-security` (from `master` at `08c4be3`): the code of the fixes, each with a regression test worded as an ordinary behaviour test (what the code must do, never how it could be abused), under a neutral commit message that says what the code now does; public docs, which may describe the new behaviour but not the weakness; the public report; and one pull request.
- **Private**, branch `fixes/0-4-0` (it exists, holding the spec; it sits on top of `audit/0-4-0`, whose pull request #1 is still open): the plan, the proof tests, and each fix's record (commit, red and green output, mutations, reviews, hand test). Plus one pull request.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the public repository's root, if your session didn't load it (everything you need from it is repeated here)
3. **The spec, in the private repository:** `specs/2026-11-11-0.4.0-fixes-design.md` on the branch `fixes/0-4-0`. Chris reviewed and approved it; please don't edit it. It has Chris's decisions with his reasons, the anti-goals, the constraints and what is left to you.
4. **The findings:** `audits/2026-11-10-0.4.0.md` on the same branch, with each finding's mechanism, proof, the proposed fix and its tests, and the section "Fixes", which shows how the last run recorded each fix. The proof tests are in `audits/2026-11-10-0.4.0/`, and so are the patches the spec names.
5. `docs/security-architecture.md` (public): the design's promises.
6. Where a finding touches it, the design spec of its area under `docs/superpowers/specs/` (public); the private spec §5 names them.

## Where you run

- The session starts on the public repository's default branch, so first run `git fetch origin fix/0-4-0-security && git checkout fix/0-4-0-security`. Then clone the private repository beside it (`git clone https://github.com/SCAI-NGO/kumbukumbu-security`) and check out `fixes/0-4-0`. **Push only to these two branches.** If you can't reach the private repository at all, see "Open points" 1.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`. Node 26, which may have to be installed by hand (the last run put Node 26.12.1 in `/opt/node26`). pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout, run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - The environment has a hard file limit below what Testcontainers' `mongo` asks for. Use a container of your own (commands below). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
  - The last run's second server group (Accounts, Sharing, Welcome) killed its MongoDB at the 20,000-file limit. It ran that group namespace by namespace (Accounts, Sharing, Welcome separately); please do the same.
  - Parallel agents need a MongoDB each, on their own port.
- The 4 skipped tests need the real embedding model and a TensorX key; skipping them is expected. Running as root can skip 2 more. CI sets `KUMBU_TOKENIZER` (below); without it more tests skip.
- **Never test against a real host**: not staging, not production, not GitHub's API beyond what a test fixture stubs. Chromium for Playwright may load `setContent` pages with network requests aborted.
- If superpowers skills are not available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## Open points for the run

All **decide**: settle them with the rules below and log each in the report.

1. **The private repository is unreachable** (it can't be cloned, or can't be pushed to): fix nothing. Then write in the public report and PR only that this happened, and put your findings about the setup in your final message. Without the spec you can't work.
2. **The order.** The spec §5 suggests: S-01, S-02 and S-10 first, then S-13 and S-03, S-04, S-05, S-08, S-07, and S-11 last. Fixes may be built in parallel in separate worktrees, on local branches, and cherry-picked onto `fix/0-4-0-security` one at a time, each only after its fix review has confirmed it. That worked well last time.
3. **A fix whose review fails** is dropped while unpushed and recorded with the verdict in the private record. Don't bend its scope to get it through. If time remains, a second, different attempt is fine; a third is not.
4. **If time runs short:** stop fixing after the last green gate. The order of priority is: reports and both pull requests with every finding's state; the High fixes (S-01, S-02, S-13); the Medium fixes.
5. **Docs.** Where a fix changes what `docs/security-architecture.md` or `README.md` says (the spec says which), update them in neutral words, describing the behaviour. An earlier design spec gets at most the one pointer line the spec names; the rest stay as they are.

## Planning

1. Write the plan with `superpowers:writing-plans` **in the private repository**, as `plans/2026-11-11-0.4.0-fixes.md` on `fixes/0-4-0`. It names the findings, so it must not be in the public repository.
2. It starts with **Task 0: baseline**: run the full gate on `fix/0-4-0-security` before any change, and record the exact counts and the exact failures. Known state of `master` at `08c4be3` (the audit run's final gate on the same code; CI green on `08c4be3`):
   - **core:** 2163 total, 3 skipped, 0 failed
   - **server:** 836 + 413 + 353 + 20 + 768 + 1132 = 3522 total, 7 skipped, 0 failed (by group, the second group split into its three namespaces)
   - **web:** 128 files, 2242 tests, build green
   - **PDF sidecar:** 9 files, 198 tests

   **Known flaky under load**, each green when its class runs alone: `DavShareTests.The_expiry_sweep_retires_expired_shares`, `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault`, `DavParkingTests.Every_junk_pattern_is_parked_and_never_reaches_mongodb`, the zip tests of `web/src/skillpage/SkillPage.test.tsx`, and now and then `AccountApiTests`, `RekeyTransitionTests`, `PairingTests` and the sync-hub tests. If one fails, run its class alone three times, record the result, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), and the exact command for each test step with the expected pass or fail. Then a red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan to the private branch.

## How to work

- **Each fix, subagent-driven** (`superpowers:subagent-driven-development`):
  1. A fresh subagent writes the behaviour test, sees it fail and fixes the code. It commits locally, then runs every mutation individually: break the code, watch the test fail, restore with `git checkout -- <file>`.
  2. Then a spec-compliance review and a code-quality review, each by a fresh subagent. For a one-rule change, one reviewer for both is fine.
  3. Then the **fix review** of the spec §4, by a further fresh subagent. It gets the finding, the diff and the code, not the fixer's reasoning. It answers: does the fix close the weakness for every attacker, does it open a new one, does it change anything visible beyond the fault and the spec's decisions?
  4. Only after the fix review confirms: cherry-pick onto `fix/0-4-0-security`, run the gate, push, and write the record to the private branch.
- **Subagents** never merge, push or switch branches. You commit and push.
- **Commit messages:** imperative and free-form, no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended. **On the public branch, no finding id, no "security", no "fix S-…"**: say what the code now does.
- **Keep the public tree clean.** Confidential notes and proof tests live in the private clone, never in the public working tree. Stage files by name, never `git add -A`, `git add .` or `commit -a`. Before every public push, run `git diff origin/fix/0-4-0-security --stat` and check that only the files you mean are in it.
- **Timing tests**, where the spec asks for them, use `Task.Run(...).WaitAsync(TimeSpan, ct)` (xUnit1031/1051 are errors) and sizes that stay fast on CI.
- **Where the spec says output must not change**, prove it with a cross-check of the old code against the new on many random inputs; the cross-check is temporary and not committed, its result goes into the private record.
- Web tests, one file: `pnpm -C web test --run <file>`; all: `pnpm -C web test --run`. **The web's CI gate is `pnpm -C web build`** (it runs the type check). Core tests, one class: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ClassName"`; server tests likewise with `tests/Kumbu.Server.Tests`.
- **The tokenizer**, so that the token-bound tests run as in CI (`ci.yml`, "Fetch the tokenizer"):
  ```sh
  mkdir -p "$HOME/.cache/kumbu"
  curl -fsSL --retry 3 --retry-all-errors -o "$HOME/.cache/kumbu/tokenizer.json" https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v2.0/resolve/95c2741480856aa9666782eb4afe11959938017f/tokenizer.json
  echo "f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec  $HOME/.cache/kumbu/tokenizer.json" | sha256sum -c -
  export KUMBU_TOKENIZER="$HOME/.cache/kumbu/tokenizer.json"
  ```
- **The test MongoDB**, fresh for every full run:
  ```sh
  docker rm -fv kumbu-test-mongo 2>/dev/null
  docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all
  sleep 5 && docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate()'
  export KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"
  ```
- **The full gate**, as CI runs it (`.github/workflows/ci.yml`), after each server fix and at the end:
  ```sh
  dotnet restore --locked-mode
  dotnet build --configuration Release --no-restore
  dotnet test --project tests/Kumbu.Core.Tests --configuration Release --no-build
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Api --filter-namespace Kumbu.Server.Tests.Mcp --filter-namespace Kumbu.Server.Tests.Auth --filter-namespace Kumbu.Server.Tests.Net
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Accounts
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Sharing
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Welcome
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Vaults --filter-namespace Kumbu.Server.Tests.Skills --filter-namespace Kumbu.Server.Tests.Search --filter-namespace Kumbu.Server.Tests.Metrics
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-not-namespace Kumbu.Server.Tests.Api --filter-not-namespace Kumbu.Server.Tests.Mcp --filter-not-namespace Kumbu.Server.Tests.Auth --filter-not-namespace Kumbu.Server.Tests.Net --filter-not-namespace Kumbu.Server.Tests.Accounts --filter-not-namespace Kumbu.Server.Tests.Sharing --filter-not-namespace Kumbu.Server.Tests.Welcome --filter-not-namespace Kumbu.Server.Tests.Vaults --filter-not-namespace Kumbu.Server.Tests.Skills --filter-not-namespace Kumbu.Server.Tests.Search --filter-not-namespace Kumbu.Server.Tests.Metrics
  pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
  pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
  ```
  The server lines together are the whole server suite; start a fresh `kumbu-test-mongo` before each group if one dies. After a web-only fix, the web lines are enough; the final gate runs everything.
- **CI's fourth job, `litmus`** (WebDAV compliance against a running server, `.github/workflows/ci.yml` job `litmus`), is part of the gate after every change under `src/Kumbu.Server/Dav/` and in the final gate. Run it as that job does: `apt-get install -y litmus` (with `sudo` if you aren't root), a MongoDB on 27017 as a replica set (the `kumbu-test-mongo` above will do, with a fresh database), `dotnet restore --locked-mode src/Kumbu.Server && dotnet build --configuration Release --no-restore src/Kumbu.Server`, then with the job's `env:` block exported (`ASPNETCORE_ENVIRONMENT=Development`, `ASPNETCORE_URLS=http://127.0.0.1:8080`, `Kumbu__PublicUrl=http://127.0.0.1:8080`, `Kumbu__Mongo__ConnectionString` pointing at it, `Kumbu__Mongo__Database=litmus`, `Kumbu__Search__Enabled=false`, `Kumbu__Metrics__Enabled=false`) the job's "Start the server" step (a random `KUMBU_ADMIN_PASSWORD`, `KUMBU_INVITATION` from `dotnet run --no-build --configuration Release --project src/Kumbu.Server -- bootstrap-admin`, the server in the background, waiting on `/healthz`), and `tools/litmus/run.sh http://127.0.0.1:8080 litmus`. Expected failures are in `tools/litmus/expected-failures.txt`. Should litmus not run here, say so in the report and check the job on the pull request with `gh pr checks`.
- Only `dotnet build`, `dotnet test`, `pnpm test`, `pnpm build` and litmus output count as evidence.
- **Style:** British English in code, comments, docs, commits and user-visible text. Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **The repository names no organisation, customer or employer**: not in code, docs, tests or examples. Use Alice, Bob, Dana, `Home`, `Work`, `Tagebuch`.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. A fix that would change what a user sees beyond the fault and the spec's decisions is not made: report it.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture; no new dependencies. Where Obsidian's and Kumbu²'s behaviour differ, Kumbu²'s semantics come first.
3. **Opens no new security hole, and gives nothing away.** Never weaken authentication, authorisation, validation or secret handling. Nothing about an unreleased weakness on a public branch. Outside discreet mode, nothing may differ between users with and without a PIN or discreet vaults.
4. **True to the spec.** If you hesitate over whether something lies within a decision Chris made, it doesn't: report it, don't build it.

If a question can only be answered by changing the spec's scope, don't guess: leave that part out, record it in the private record, and add a row with the finding's id only to `docs/superpowers/open-items.md` (public).

Log every decision you made this way: in the private record with its details, and in the public report and pull request under **Decisions made without Chris**, as far as it reveals nothing of a finding. Each decision gets the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version: Chris does the rollout himself.
- No secrets in logs, assertions or the reports. Test passwords and keys made by fixtures are fine.
- **Security boundary:** the private spec §4 says what the fixes may change and what stays untouched, files included. A fix that needs more is dropped and reported.
- Specs outside this topic stay as they are (apart from the one line of point 5); if one looks wrong, mention it in the report.
- **At the end:**
  - in `docs/superpowers/open-items.md`, mark the three rows of 2026-11-11 about the audit's fix overnighter and Chris's decisions as done or still open, by id only, and add a row with the public pull request's link;
  - the hand-test checklist for each fix goes to the private record. Write it as numbered steps, saying where to look and what passes, runnable on staging by Chris or through Playwright, with one browser context per test user.

## Out of scope

- L-01 and L-02 (they go to the "0.5.0 preparations"), the sidecar's per-page memory (an open item), the history caveat of write shares.
- Everything else in `docs/superpowers/open-items.md`.
- Dependency upgrades.

## The report

Two pull requests, neither merged:

1. **Private**, in `SCAI-NGO/kumbukumbu-security`, from `fixes/0-4-0` to **`audit/0-4-0`** (so that GitHub retargets it to `master` once #1 is merged), titled "Fixes of the 0.4.0 audit". It holds the plan and the fix records: append each one to the section "Fixes" of `audits/2026-11-10-0.4.0.md`, in the form the S-06, S-09 and S-12 records use, and update the summary table's "Fixed here" column. Its body says that it sits on top of #1 and lists every finding with its state. Open it once the first fix is recorded, then update it as fixes land.
2. **Public**, in `SCAI-NGO/kumbukumbu`, from `fix/0-4-0-security` to `master`, titled "The fixes of the 0.4.0 audit". Open it as a **draft** after the first public push, with a short body that names no finding; write the findings' states into the body only in its final update, and mark it ready after the final gate (including CI's `litmus` job, `gh pr checks`). Its final body gives:
   - each confidential finding by id, its severity and whether it is fixed now (no area, mechanism or test name, and no mapping from commits to ids);
   - the decisions made without Chris, as far as they reveal nothing;
   - the gate's results;
   - and it ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

   As the last commit, add `docs/superpowers/overnighter/2026-11-11-security-fixes-report.md`, with:
   - the environment;
   - the plan review in general terms;
   - Task 0, planned against measured;
   - the ids fixed and not fixed;
   - the final gate's real output;
   - the decisions made without Chris;
   - deviations, surprises and open points for Chris;
   - both pull requests' links (the private one by URL only).

Never force-push: if something has to come out of a pushed branch, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull requests are the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
