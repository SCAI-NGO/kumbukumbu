# Overnighter brief: a read-only console

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# with a React web app, on MongoDB. Tonight's work adds a page to the web app where a signed-in person types what an agent would send to the `sh` tool and reads the answer. It makes hand tests possible without an LLM client, and gives power users a fast way to read their vaults. It looks small, and its one hard promise is that nothing typed there can change a vault.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it
3. Spec: `docs/superpowers/specs/2026-11-02-console-design.md`, reviewed and approved. Please don't edit it.
4. Plan: `docs/superpowers/plans/2026-11-02-read-only-console.md`; start with Task 0

## Where you run

- Branch `feature/read-only-console`: everything you push there survives, everything else does not.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report.
- Task 0 (setup plus first build and tests) should take about **15 minutes**; an earlier run here needed about 25, with the Docker notes below. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (10.0.100, roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if it is missing).
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`mongo:7.0`, see `tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", please check whether the setup script's daemon is still running, and start it again if you can.
  - If Docker can't work at all, use an external replica-set MongoDB: install `mongod` 7.0, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` (see `ExternalMongo` in `TestImages.cs`).
  - An earlier run here met a hard file limit below what Testcontainers' `mongo` asks for. What worked: `docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all --wiredTigerEngineConfigString "file_manager=(close_idle_time=5,close_scan_interval=2,close_handle_minimum=250)"`, then `docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate({_id:"rs0",members:[{_id:0,host:"localhost:27017"}]})'` and `export KUMBU_TEST_MONGO="mongodb://localhost:27017/?replicaSet=rs0&directConnection=true"`.
  - Give every full run a fresh container, with `docker rm -fv kumbu-test-mongo` first: mongod runs out of file handles over many runs, and without `-v` anonymous volumes fill the disk.
  - If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped tests need the real embedding model and a TensorX key. Neither is available here, and skipping them is expected. Running as root can skip 2 more.
- **A partial dry run only.** On the planning machine the web tests and build, the Core tests and the server's build ran at the baseline. It has no Docker, so the server's tests didn't run, and the plan's new code has never been compiled. If a signature doesn't fit, fix the obvious thing and say so in the report.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by its own subagent. Then you, the controller, push.
- Subagents never merge, push or switch branches.
- The test filter is Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`, repeatable. `FullyQualifiedName~` doesn't work here, a glob in mid-name gives "Zero tests ran", and "Zero tests ran" is a failure.
- Commit and push after every task. Commit messages imperative and free-form ("Let the console refuse what writes"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Full verification at the end of each phase, whatever it touched, as CI does: `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`.
- **The web's CI gate is `pnpm -C web build`:** it runs the type check, which `pnpm test` does not. Run `pnpm -C web install --frozen-lockfile` first, since stale `node_modules` make tests and build fail.
- Known failures before you start: **none**. Expected: `dotnet test` total 3408, 3404 succeeded, 4 skipped, 0 failed; web 85 files, 1368 tests; sidecar 6 files, 78 tests.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **Style:** British English in code, comments, docs, commits (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **`web/src/api/schema.d.ts` is edited by hand here**, as generated output would have it.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples, test data. Use Alice, Bob, `Gartenarbeit`, `Work`.
- **Update `docs/security-architecture.md`** as the plan's Task 6 says. It cites file and line, so please keep those true where you touch them.
- **Uncertainty is welcome.** When spec and plan leave something open, decide it, say so in a commit message, and list it in the report. If something seems wrong, build the safer reading, write down why, and carry on. Where plan and spec differ, the spec's decisions win over the plan's details.

These decisions are the maintainer's, and final; please don't reopen them: the console only reads; no `file` command, no board changes, no `rm`, `mv`, `mkdir`, `restore`, `mkcp` or `rmcp`; no `connect <key>`; history in memory only; a two-line prompt; `rg` without a path searches the working directory. The reason, in the maintainer's words: it should be a helpful tool, not one with which someone who doesn't know their way around ruins everything.

## A few things that matter to us

- **Please do not merge, tag, release or deploy.** No version change (`version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj`, `web/package.json`), no pushes to `master`, and leave `gitops-*`/`argocd-*` and devices alone: a rollout is something Chris does together with a person.
- No secrets in logs, assertions, test data or the report. **No command and no output in logs, and nothing of the console in browser storage.**
- Specs outside this topic stay as they are, and so does this one; if one looks wrong, please mention it in the report.
- **Agents see no change:** `Manual.cs`, `Help.cs`'s text, the tool descriptions, the shell's hints and error sentences stay byte for byte. The extraction in `ToolResult.cs` leaves MCP's behaviour as it is.
- **Security boundary:** this work **touches** the REST API (`src/Kumbu.Server/Api/`, a new session-only endpoint) and `docs/security-architecture.md`, so **the repository's security-review expectation applies**: Task 6 has a fresh subagent review the diff, every finding goes in the report, and the pull request says that a review applies. **Not touched, on purpose:** `src/Kumbu.Server/Auth/CallerMiddleware.cs` (the session cookie stays refused on `/mcp`), keys, slots, encryption, sessions, OAuth, sharing.
- **The plan's order is deliberate.** Phase A (the server) comes first: a stuck web part still leaves the read-only rule built and tested.
- **At the end, in this order** (the plan's final task): push, the draft pull request, the report and the row in `docs/superpowers/open-items.md` with its link, push.

## Out of scope

- Any way of writing through the console, and `connect <key>`.
- Tab completion, globbing, redirection, persistent history, a layout made for phones.
- Changes to what agents see, or to the shell's behaviour.
- Any version change or release.

## The report

First push, and open a **draft** pull request from `feature/read-only-console` to `master`, its body ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. **Do not merge it.** If no pull request can be opened, use the compare URL `master...feature/read-only-console` of this repository's `origin` as the link.

Then the last commit, pushed: `docs/superpowers/overnighter/2026-11-02-read-only-console-report.md`, and the pull request's link added to `docs/superpowers/open-items.md`. The report holds:

- the environment;
- Task 0 planned against measured;
- per task the commit and the real test output;
- every mutation individually;
- deviations and the calls you made;
- surprises;
- the security review's findings, fixed or not;
- open points for Chris, with **what he must check by hand** first: `docs/test-playbook-console.md`, above all `img` and `pdf` with real attachments, and a phone;
- the PR link.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the PR is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Getting through Phase A with everything green and honestly reported is already a good night's work.
