# Overnighter brief: publish v1, one note behind a link

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# with a React web app, on MongoDB. Since today it is in daily use at work, and twice its owner wanted to send a colleague one note as a link instead of pasting its text into a chat. Tonight's work is that: the owner publishes one note as an encrypted snapshot behind a secret link, readable without an account, and nothing else of the vault goes along. It is the biggest overnighter so far and it touches crypto, credentials and an anonymous endpoint, so care counts for more than speed. An honest partial result is a good result.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it
3. Spec: `docs/superpowers/specs/2026-11-05-publish-design.md`, reviewed and approved. Please don't edit it.
4. Plan: `docs/superpowers/plans/2026-11-05-publish.md`; start with Task 0. Its table "Decisions this plan takes" settles what the spec leaves open: please follow it rather than reopening a choice.

## Where you run

- Branch `feature/publish`: everything you push there survives, everything else does not.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new NuGet or npm package (plan, global constraints).
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (10.0.100, roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing).
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", please check whether the setup script's daemon is still running, and start it again if you can.
  - If Docker can't work at all, use an external replica-set MongoDB: install `mongod`, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` (see `ExternalMongo` in `TestImages.cs`).
  - An earlier run met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own with `--ulimit nofile=20000:20000`, named `kumbu-test-mongo`, as a replica set, and `KUMBU_TEST_MONGO` pointing at it.
  - Give every full run a fresh container, with `docker rm -fv kumbu-test-mongo` first: mongod runs out of file handles over many runs, and without `-v` anonymous volumes fill the disk.
  - If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped tests need the real embedding model and a TensorX key. Neither is available here, and skipping them is expected. Running as root can skip 2 more.
- **Partial dry run only.** The planning machine had dotnet and node but no Docker: the server build, `Kumbu.Core.Tests`, the web tests and build and the sidecar ran there; the server tests did not, and none of the plan's code has been compiled. Trust the code over the plan's line numbers. If a task's premise turns out wrong, choose the reading closest to the spec and write it down.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push.
- The test filter is Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`, repeatable. `FullyQualifiedName~` doesn't work here, and a glob in mid-name selects nothing. Web: `pnpm -C web test --run <file>`.
- Commit and push after every task. Commit messages imperative and free-form ("Let the owner publish one note behind a link"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Full verification at each phase gate (after Tasks 3, 7, 10 and 13b, and in Task 17), as CI does: `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`.
- **The web's CI gate is `pnpm -C web build`:** it runs the type check, which `pnpm test` does not.
- Known failures before you start: **none**. Expected: `dotnet test` total 3727, 3723 succeeded, 4 skipped, 0 failed; web 88 test files, 1585 tests; sidecar 8 test files, 146 tests (CI run 41883724750 of `e8be7ad`). Known flaky under load, passing alone: see the plan's Task 0.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **If the night runs short**, stop at the last green phase gate rather than half-way through a task, push, and say in the report which tasks are left. The order of the plan is the order of value: the core (Tasks 1 to 10, 4a, 4b, 6a and 6b among them) is worth more than a polished UI on top of something unfinished.
- **Style:** British English in code, comments, docs, commits (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **`web/src/api/schema.d.ts` is edited by hand here**, as generated output would have it.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples, test data. Use Alice, Bob, Carol, `Work`, `Gartenarbeit`, `kumbu.example.org`.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself. One draft pull request is the result.
- No secrets in logs, assertions or the report. **A share code, its lookup id or a reader cookie never reaches a log**, not even at debug level.
- Specs outside this topic stay as they are; if one looks wrong, mention it in the report.
- **The guiding principle is the *forum internum*** (spec §1): the right to keep unfinished thoughts to oneself. When something is unclear, the reading that lets less leave the vault is the right one. Nothing the owner didn't choose leaves it: no other note, path, tag, comment, attachment metadata, vault name or owner name. The reader is never told whether the note has changed since the snapshot. For the same reason, **withdrawing and setting or lifting an unpublishable mark always work**, also with publishing switched off on the server (plan decision 20): they only ever reduce what is out there.
- **History is an audit log**: publishing, updating and withdrawing are recorded, nothing is hidden, duplicates included. Members see that a note is published, never when, how often or by whom.
- **Security boundary: touched, deliberately.** Crypto (share code, HKDF, a publication key in two slots), credentials (the "May publish" right sealed into slots and metas), an anonymous endpoint with its own cookie, a page strangers open on the web app's origin, and a hook in every removal and move. Task 16 has a fresh subagent review the whole branch against spec §5 and spec §2's anti-goals; every finding goes in the report, fixed or not, and the pull request says a security review applies. **Not touched:** `CallerMiddleware`'s refusal of the session cookie on `/mcp`, sessions and their password window (only used), pins, the vault keys' own handling in re-keying (one more collection is resealed, nothing else), `_loading`.
- At the end: a row with the pull request's link in `docs/superpowers/open-items.md`, and in the report a hand-test section with numbered steps, where to look and what passes: Chris tests from that list on Saturday. Don't change anything else outside the plan's files.

## Out of scope

- Everything spec §2 lists as a non-goal for v1: several notes under one code, linked notes taken along, open pages with slugs, OpenGraph, custom domains, expiry dates, short codes, greylisting, live pages, members publishing.
- PDFs in a publication (plan decision 1: they become plain text in v1).
- Pins, Options A and B, the owner lock-out that `PlantedUserSlotTests` documents, and the earlier reviews' open Low findings.
- The Obsidian plugin (another repository).
- Merging `master` into the branch: `origin/master` has moved past `e8be7ad` with #149, a web-only copy change; the pull request will show whether it conflicts.

## The report

First open a draft pull request to `master` (not merged), titled "Publish v1: one note behind a link", whose body lists Claude's calls in the spec (plan, after the decisions table) and says that a security review applies. Then, as the last commit, `docs/superpowers/overnighter/2026-11-05-publish-report.md`, with environment, Task 0 planned against measured, per task the commit and real test output, every mutation individually, the security review's findings and what became of each, deviations, surprises, tasks left if any, the hand-test section, open points for Chris, and the pull request's link, together with a row linking it in `docs/superpowers/open-items.md`. Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
