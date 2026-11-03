# Overnighter brief: renaming one's own vaults

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# with a React web app, on MongoDB. Members can already rename a vault shared with them, but owners can't rename their own; tonight's work adds that, and closes three small leftovers from the hand test of vault sharing. The rename looks like a small feature and touches something delicate: a vault's stored name sits in every stored path, so it stays for good, and everyone sees a display name instead. That is why the plan spends most of its care on proving the owner's paths.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it
3. Spec: `docs/superpowers/specs/2026-11-02-rename-own-vaults-design.md`, reviewed and approved. **The plan's section "Corrections to the spec" overrides it where they differ**: several sites the spec lists turned out to be something else when checked. Please don't edit the spec.
4. Plan: `docs/superpowers/plans/2026-11-02-rename-own-vaults.md`; start with Task 0

## Where you run

- Branch `feature/rename-own-vaults`: everything you push there survives, everything else does not.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report.
- Task 0 (setup plus first build and tests) should take about **15 minutes**; the last run here needed about 25, with the Docker notes below. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (10.0.100, roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if it is missing).
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`mongo:7.0`, see `tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", please check whether the setup script's daemon is still running, and start it again if you can.
  - If Docker can't work at all, use an external replica-set MongoDB: install `mongod` 7.0, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` (see `ExternalMongo` in `TestImages.cs`).
  - The last run here met a hard file limit below what Testcontainers' `mongo` asks for. What worked: `docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all --wiredTigerEngineConfigString "file_manager=(close_idle_time=5,close_scan_interval=2,close_handle_minimum=250)"`, then `docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate({_id:"rs0",members:[{_id:0,host:"localhost:27017"}]})'` and `export KUMBU_TEST_MONGO="mongodb://localhost:27017/?replicaSet=rs0&directConnection=true"`.
  - Give every full run a fresh container, with `docker rm -fv kumbu-test-mongo` first: mongod runs out of file handles over many runs, and without `-v` anonymous volumes fill the disk.
  - If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped tests need the real embedding model and a TensorX key. Neither is available here, and skipping them is expected. Running as root can skip 2 more.
- **Nothing was dry-run.** The machine this plan was written on has no dotnet, node or docker. The baseline numbers come from CI, and the plan's code has never been compiled. If a signature doesn't fit, fix the obvious thing and say so in the report.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by its own subagent. Then you, the controller, push.
- Subagents never merge, push or switch branches.
- The test filter is Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`, repeatable. `FullyQualifiedName~` doesn't work here, a glob in mid-name gives "Zero tests ran", and "Zero tests ran" is a failure.
- Commit and push after every task. Commit messages imperative and free-form ("Let owners rename their vaults"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Full verification at the end of each phase, whatever it touched, as CI does: `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`.
- **The web's CI gate is `pnpm -C web build`:** it runs the type check, which `pnpm test` does not, and a recent type error got through the tests alone. Run `pnpm -C web install --frozen-lockfile` first, since stale `node_modules` make tests and build fail.
- Known failures before you start: **none**. Expected: `dotnet test` total 3264, 3260 succeeded, 4 skipped, 0 failed; web 84 files, 1345 tests; sidecar 6 files, 78 tests.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **Style:** British English in code, comments, docs, commits (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`, where their siblings do.
- **`web/src/api/schema.d.ts` is edited by hand here**, as generated output would have it; the plan says where.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples, test data. Use Alice, Bob, Carol, `Gartenarbeit`, `Garten`, `Beet`, `Work`.
- **Update `docs/security-architecture.md`** as the plan's tasks say. It cites file and line, so please keep those true where you touch them.
- **Uncertainty is welcome.** When spec and plan leave something open, decide it, say so in a commit message, and list it in the report. If something seems wrong, build the safer reading, write down why, and carry on. The plan's line numbers are from `3eae059` and will drift as you work; the sense counts.

These decisions are the maintainer's, and final; please don't reopen them: no password for renaming; members aren't told; the old name is free at once; the re-encryption notice only for members whose credentials ended; devices pair again after a rename.

## A few things that matter to us

- **Please do not merge, tag, release or deploy.** No version change (`version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj`, `web/package.json`), no pushes to `master`, and leave `gitops-*`/`argocd-*` and devices alone: a rollout is something Chris does together with a person.
- No secrets in logs, assertions, test data or the report. **The new vault name must not appear in logs**; there is a test.
- Specs outside this topic stay as they are, and so does this one; if one looks wrong, please mention it in the report.
- **Security boundary:** this work **touches** the vault meta (it gains a sealed display name) and the slot name index (a rename updates the owner's user slot's `nameId`). So `docs/security-architecture.md` is updated (plan, Tasks 3 and 10) and **the repository's security-review expectation applies**: Task 10 has a fresh subagent review the diff, and every finding goes in the report; the pull request says that a review applies. **Not touched, on purpose:** pins, the vault keys and re-keying's key handling, `_loading`'s use in loads and runs (the rename only queues for it once, after its transaction, and not holding the vault's gate). So:
  - No new cryptographic primitives, and no new key: the display name is sealed as the name already is.
  - These earlier hard rules still hold, each with its test class, which is never weakened: `MembershipCheckTests`, `BoundMembershipWriteTests`, `PinTests`, `NoNamesTests`, `MarkerTests`.
- **The plan's order is deliberate.** Phase A (Leave entry, restore changeset, notice) doesn't depend on the rename, so a stuck rename still leaves those three done and pushed. In Phase B the case of a new vault under the old name comes first, by the maintainer's wish.
- **Please take care with `RestClient.Vault` in tests:** it keeps the old name after a rename, and beside a new vault of that name its helpers hit the wrong vault without a word. The plan explains it.
- **At the end, in this order** (the plan's final task): push, the draft pull request, the report and the row in `docs/superpowers/open-items.md` with its link, push.

## Out of scope

- Plugin 0.2.0: its own run in its own repository; the plugin isn't changed for this. "Connect Obsidian…" for members follows that run, as a small change here.
- Pins a former member can't forge and `_loading`: 0.3.1 (`docs/superpowers/open-items.md`), design and independent review first.
- A one-time notice to the owner's agents about a rename.
- Rewriting notes that spell out an absolute path to their own vault.
- Changing the sentence of the Settings entry on a re-encryption; please mention it in the report, as the plan's final task says.
- Any version change or release.

## The report

First push, and open a **draft** pull request from `feature/rename-own-vaults` to `master`, its body ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. **Do not merge it.** If no pull request can be opened, use the compare URL `master...feature/rename-own-vaults` of this repository's `origin` as the link.

Then the last commit, pushed: `docs/superpowers/overnighter/2026-11-02-rename-own-vaults-report.md`, and the pull request's link added to `docs/superpowers/open-items.md`. The report holds:

- the environment;
- Task 0 planned against measured, with the per-project test counts CI's log doesn't print;
- per task the commit and the real test output;
- every mutation individually;
- deviations and the calls you made;
- surprises, first among them each row of the owner's matrix (Task 7) that failed before a fix;
- the security review's findings, fixed or not;
- open points for Chris, with **what he must check by hand** first (a real Obsidian device after a rename, the old device key that still holds `Maintain` after pairing again, a second open tab on the old name's route, a member's open tab seeing the notice, the dialog on a phone);
- the PR link.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the PR is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Getting through Phase A with everything green and honestly reported is already a good night's work.
