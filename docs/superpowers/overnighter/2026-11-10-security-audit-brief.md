# Overnighter brief: the security audit of 0.4.0

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. 0.4.0 is feature complete and hand-tested: WebDAV, skills, ingesting attachments, structural chunking, Obsidian parity and discreet vaults. Before it is released we want an independent security audit of everything since 0.3.2, rated on one scale, with what can be fixed safely fixed tonight. Finding the real weaknesses counts for more than a long list; a finding you can prove is worth ten suspicions.

Nobody is available to answer questions during this run. That is intended: the spec has been reviewed by Chris, and everything after it is yours, from the plan to the pull requests. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-10-security-audit-0-4-0-design.md`, reviewed and approved by Chris. Please don't edit it.
4. `docs/security-architecture.md`: the threat model (§2), the slots (§3), who can do what (§4), sessions, keys, WebDAV shares, connections (§6), web hardening (§7). The design promises you audit against.
5. Per area, its design spec under `docs/superpowers/specs/`: `2026-11-07-webdav-design.md`, `2026-11-08-skills-design.md`, `2026-11-08-ingest-attachments-design.md`, `2026-11-09-structural-chunking-design.md`, `2026-11-09-obsidian-parity-quick-wins-design.md`, `2026-11-09-discreet-vaults-design.md`, and the security reviews in the matching reports under `docs/superpowers/overnighter/` (what earlier reviews already found and settled).

## Where you run

- **Two repositories.** Kumbu² (`SCAI-NGO/kumbukumbu`, public), branch `audit/0-4-0-security`, based on `master` at `9864ff7` (the same code as `v0.4.0-pre.5`). The session starts on the default branch, so first run `git fetch origin audit/0-4-0-security && git checkout audit/0-4-0-security`. And the private `SCAI-NGO/kumbukumbu-security`, for the confidential findings: it may be cloned beside the first one; if not, clone it (`git clone https://github.com/SCAI-NGO/kumbukumbu-security`). There, create the branch `audit/0-4-0` from `master` and push to it. Push only to these two branches. If you cannot reach the private repository at all, see "Open points" 1.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages, scanners) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", check whether the setup script's daemon is still running, and start it again if you can.
  - Earlier runs met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own with `--ulimit nofile=20000:20000`, named `kumbu-test-mongo`, `mongo:7.0` as a replica set (`--replSet rs0 --bind_ip_all`, then `rs.initiate()`), and `KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"`. Give every full run a fresh one (`docker rm -fv kumbu-test-mongo` first). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine. The last run had to run the server tests namespace by namespace (`--filter-namespace`), as CI does in four jobs; that is fine.
- The 4 skipped tests need the real embedding model and a TensorX key; skipping them is expected. Running as root can skip 2 more. CI also sets `KUMBU_TOKENIZER` (see `.github/workflows/ci.yml`); without it more tests skip locally, so compare counts with that in mind.
- The PDF sidecar (`pdfimages`, `pdftoppm`) is a separate container in production (`docs/security-architecture.md` "The PDF sidecar"); installing `poppler-utils` locally to try a malicious PDF is fine.
- **Never test against a real host**: not staging, not production, not GitHub's API beyond what a test fixture stubs. Everything runs against the test server, the test MongoDB and stubs.
- If superpowers skills are not available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## Open points for the run

All **decide**: settle them with the rules below and log each in the report.

1. **The private repository is unreachable.** Then write the confidential findings nowhere public: put them in full into your final message in this session (Chris can read it), and say in the public report and PR only that this happened, with the ids and severities. Fix nothing of Medium or higher in that case. If you can clone but not push, or push but not open the pull request, treat it as unreachable for fixing Mediums, but keep what you did push. If `audit/0-4-0` already exists there, check it out instead of creating it. Lows and F11 may be fixed in every case.
2. **The order of the areas.** By risk: E (discreet vaults: sessions and a PIN), A (WebDAV: a new credential, HTTP Basic, XML), B (skills: an anonymous page, outbound HTTP, archives), C (attachments: processes on untrusted files), F (rendering: XSS), G (cross-cutting), D (search). Parallel agents are welcome; give each one area with its files, the spec's attacker list and the classes of spec §4 step 2.
3. **Where a proof test lives while it is red.** Write and run it in your working copy; never commit a red proof test of a Medium or higher to the public branch. Its code goes to `audits/2026-11-10-0.4.0/` in the private repository (as a file you could drop into `tests/…` to run).
4. **Is it a finding or a design decision?** Some behaviour is a decision an earlier spec made on purpose (for example: agents, WebDAV and sync see discreet vaults; the PIN hides, it doesn't encrypt; the late save takes one save). A behaviour a spec decided is no finding unless it breaks a promise of `docs/security-architecture.md` or the spec itself; if you think the decision is unsafe, report it as a finding with "needs a decision from Chris" and fix nothing.
5. **Duplicates of earlier reviews.** The overnighter reports of #152, #153 and #179 contain security reviews. A finding they already list as accepted is no new finding; one they list as fixed but which isn't, is.
6. **F11** is decided (spec §6). Its place: wherever the web app shows a note's 404 (find the texts with `rg -n "There is no note at" src web/src`; the server makes the text, `NO_SUCH_NOTE` is in `web/src/discreet/errorText.ts:9`). Decide whether the vault is in the user's list from the vaults query the app already has, add no request.
7. **A dependency with a known vulnerability** is a finding, rated by whether Kumbu² reaches the vulnerable code. The upgrade itself is not done here (spec §8).

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-10-security-audit.md`. The plan names the areas and how each is audited; it can't name findings it doesn't know yet, so its fix phase is a procedure (spec §6 and §5), applied per finding.
1a. **The public plan names no finding.** It describes the areas, the method and the fix procedure; the per-finding fix tasks (with their tests and mutations) go into the private repository, next to the confidential findings, for Mediums. Lows' fix tasks may be in the public plan once the Low has passed its adversarial check.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures. Known state on `master` at `9864ff7`: **web 128 files, 2226 tests, all passing, build green** (measured locally on 2026-11-10); **core 2153 tests, 3 skipped; server 3521 tests, 7 skipped, 0 failed** (from `docs/superpowers/overnighter/2026-11-10-pre4-findings-report.md` §1 and §5, run namespace by namespace; CI green on `9864ff7`); the PDF sidecar `pdf/` passing in CI. `AccountApiTests`, `RekeyTransitionTests`, `PairingTests` and the sync-hub tests have failed now and then under load: if Task 0 shows one, run its class alone three times, record whether it is flaky, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. **Phases:** (1) audit, every area, findings with proof and severity; (2) adversarial check of every Medium-or-higher finding and of every Low; (3) the confidential findings written and pushed to the private repository, its pull request opened; (4) F11; (5) fixes per spec §6, Low and Medium, each its own task, commit and gate, each Medium fix with its own review before it is pushed; (6) reports and pull requests. Nothing of phase 4 or 5 starts before phase 3 is pushed. In phases 1–3 the only public commit is the plan.
4a. **If time runs short:** stop fixing after the last green gate. The audit, the confidential findings and the reports come first, then F11, then Medium fixes, then Low fixes.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- The audit: one fresh subagent per area, then for each Medium-or-higher finding a second fresh subagent that tries to disprove it (spec §4 step 4). Each fix: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`); then a spec-compliance review and a code-quality review, each by a fresh subagent; for a Medium also the fix review of spec §6.
- Subagents never merge, push or switch branches. You commit and push.
- Commit and push after every task, with one exception: a Medium fix is pushed only after its fix review confirms it (spec §6); a failed one is dropped while still unpushed (`git reset --hard HEAD~1`), never reverted in public. Commit messages imperative and free-form ("Leave the framework's version out of WebDAV answers"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended. **On the public branch, a Medium fix's message, test names and comments say what the code must do, never how it could be abused** (spec §5).
- **Keep the public tree clean.** Confidential notes and red proof tests live in the private clone, never in the public working tree. Stage files by name, never `git add -A`, `git add .` or `commit -a`. Before every public push, run `git diff origin/audit/0-4-0-security --stat` and check that only the files you mean are in it.
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
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Accounts --filter-namespace Kumbu.Server.Tests.Sharing --filter-namespace Kumbu.Server.Tests.Welcome
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-namespace Kumbu.Server.Tests.Vaults --filter-namespace Kumbu.Server.Tests.Skills --filter-namespace Kumbu.Server.Tests.Search --filter-namespace Kumbu.Server.Tests.Metrics
  dotnet test --project tests/Kumbu.Server.Tests --configuration Release --no-build -- --filter-not-namespace Kumbu.Server.Tests.Api --filter-not-namespace Kumbu.Server.Tests.Mcp --filter-not-namespace Kumbu.Server.Tests.Auth --filter-not-namespace Kumbu.Server.Tests.Net --filter-not-namespace Kumbu.Server.Tests.Accounts --filter-not-namespace Kumbu.Server.Tests.Sharing --filter-not-namespace Kumbu.Server.Tests.Welcome --filter-not-namespace Kumbu.Server.Tests.Vaults --filter-not-namespace Kumbu.Server.Tests.Skills --filter-not-namespace Kumbu.Server.Tests.Search --filter-not-namespace Kumbu.Server.Tests.Metrics
  pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
  pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
  ```
  The four server groups together are the whole server suite; a fresh `kumbu-test-mongo` before each group if one dies. After a web-only fix the web lines are enough; the final gate runs everything.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence of a fix; a finding's proof may also be a precise trace through the code (spec §4 step 3).
- **Style:** British English in code, comments, docs, commits and user-visible text. Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples. Use Alice, Bob, Dana, `Home`, `Work`, `Tagebuch`.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. When a fix would change something a user sees beyond the fault, don't fix it: report it.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture; no new dependencies. Where Obsidian's and Kumbu²'s behaviour differ, Kumbu²'s semantics come first.
3. **Opens no new security hole, and gives nothing away.** Never weaken authentication, authorisation, validation or secret handling. Nothing about an unreleased weakness on a public branch. Outside discreet mode nothing may differ between users with and without a PIN or discreet vaults.
4. **True to the spec.** Rate up when in doubt; fix only when sure. If you hesitate over whether a fix needs a decision, or whether it lies inside spec §6's limits, it does need one and it doesn't: report it, don't fix it.

If a question can only be answered by changing the spec's scope, do not guess: leave that part out and record it in `docs/superpowers/open-items.md` (public, so without details of a weakness: name the finding's id only).

Log every decision you made this way. Put it in the report and in the pull request description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself.
- No secrets in logs, assertions or the reports. Test passwords and keys made by fixtures are fine.
- **Security boundary** (spec §9): the audit reads everything. What you may change: the fixes of spec §6 and F11; in the private repository only `audits/`. **Not touched:** authentication, sessions and the password window, the PIN check and its rate limit, cryptography and the slots, sharing and roles, the late-save rule, WebDAV's authentication, the CSP's policy (unless a Low finding is a missing header outside it), the version; and these files, whatever the finding: `src/Kumbu.Server/Auth/CallerMiddleware.cs`, `src/Kumbu.Server/Accounts/Sessions.cs`, `src/Kumbu.Server/Accounts/DavTokens.cs`, `src/Kumbu.Server/Accounts/DiscreetVaults.cs`, `src/Kumbu.Server/Api/LateSave.cs`, `src/Kumbu.Server/Sharing/`, and anything that derives keys or slots. **Allowed** is adding a missing check or filter that only narrows what a caller sees or may do, without changing how a credential is verified or a role is granted. A fix that needs more goes back to the findings, unfixed.
- Specs outside this topic stay as they are; if one looks wrong, mention it in the report.
- At the end: a row with the public pull request's link in `docs/superpowers/open-items.md` (no details of a weakness), and in the public report a hand-test checklist for the fixes a user can see (F11 at least), with numbered steps, where to look and what passes.

## Out of scope

- Everything spec §8 lists.
- The leftovers in `docs/superpowers/open-items.md`, unless one turns out to be a security finding.

## The report

Two pull requests, neither merged:

1. **Private**, in `SCAI-NGO/kumbukumbu-security`, from `audit/0-4-0` to `master`, titled "Security audit of 0.4.0": `audits/2026-11-10-0.4.0.md` with every finding of Medium or higher (spec §5's fields, the adversarial verdict, the fix and its review where there is one), and the proof tests under `audits/2026-11-10-0.4.0/`. Push it as soon as phase 3 is done, then update it as fixes land.
2. **Public**, in `SCAI-NGO/kumbukumbu`, from `audit/0-4-0-security` to `master`, titled "Security audit of 0.4.0: fixes", whose body gives the counts per severity (not per area), the Low findings with their fixes, F11, the confidential findings by id, severity and fixed-or-not only (no area, commit or test name, and Medium fixes not listed as fixes), the decisions made without Chris (none that reveal a confidential finding), the security boundary, and ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Then, as the last commit, `docs/superpowers/overnighter/2026-11-10-security-audit-report.md`, with: the environment; the plan review findings and what you did with them; Task 0 planned vs. measured; what was audited per area and how (the method, not what was found); the counts per severity; every Low finding in full; for each Low fix and F11 the commit, the real test output and every mutation individually (for Medium fixes all of this goes to the private file); the decisions made without Chris; deviations, surprises, open points for Chris; the hand-test checklist; both pull requests' links, together with a row linking the public one in `docs/superpowers/open-items.md`. Link the private pull request by its URL only; it opens for Chris alone.

If the audit finds nothing of Medium or higher, say so plainly in both places; the private file then says "none" and why you trust that.

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull requests are the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
