# Overnighter brief: vault sharing ("collab", 0.3.0)

Hi, thank you for taking this one on! Tonight's work is vault sharing for Kumbu², a Markdown second brain served over MCP. An owner shares a vault with people she trusts, as read, write or maintain, and can change or withdraw that at any moment. Their agents then work on it like on their own vaults. It is the heart of release 0.3.0. The design has been through two reviews today, one of them a security review, so the hard thinking is done. What remains is careful building, and that is exactly what we would love your help with.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root (language, style, build and test commands)
3. Requirements: `concepts/collab-concept.md`, especially §3.4, §7, §7.1, §8, §9, §9a and §14
4. Design: `docs/superpowers/specs/2026-10-31-collab-design.md`. This is the document you build from. Where it and the concept differ, the design wins. §12 gives the phases and §13 the security review's findings with their resolutions.
5. `docs/security-architecture.md`, the system as built. You update it phase by phase (design §13, L10).

There is **no plan yet: writing it is part of Task 0** (below). Please start with Task 0.

## Task 0: baseline, then the plan

1. **Environment.** These must work:
   - `dotnet --version`: the SDK must satisfy `global.json` (10.0.100, roll forward)
   - `echo $DOTNET_CLI_TELEMETRY_OPTOUT`: expected `1`
   - `node --version`: 26
   - `pnpm --version`: 10.32.1 (`npm install -g pnpm@10.32.1` if it is missing)
   - `docker info`
2. **Baseline,** measured locally today on the commit this branch starts from:
   - `dotnet test --solution Kumbu.slnx`: **2703 passed, 4 skipped, 0 failed**, about 1 min 45 s. The 4 skipped need the embedding model and a TensorX key, and skipping them is expected.
   - `pnpm -C web install --frozen-lockfile`, then `pnpm -C web test --run`: **74 files, 1128 tests passed**
   - `pnpm -C web build`: succeeds
3. **Time budget:** setup plus the first build and test run should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
4. **The plan.** Write it to `docs/superpowers/plans/2026-10-31-collab-plan.md`. Use superpowers:writing-plans if it is available, otherwise follow its usual shape. Please follow these rules:
   - one section per phase of design §12, in that order;
   - small tasks, each with its red test, the change to make it green, and the mutations you will try;
   - the expected test counts after each phase (estimates; the report gives measured against estimated);
   - the files each task touches.

   Commit and push the plan before you write any code.

## Where you run

- Branch `feature/collab`: everything you push there survives, everything else does not. For this run it **takes the place of the long-lived `v0.3.0` branch** of design §12: one pull request for all phases, opened as a **draft**, never merged until 0.3.0 is complete. Where this brief and the design differ on scope or process, this brief wins.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report.
- **Docker** is set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`mongo:7.0`, see `tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", please check whether the setup script's daemon is still running and start it again if you can.
  - If Docker can't work at all, the tests can use an external replica-set MongoDB instead: install `mongod` 7.0, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` to its connection string (see `ExternalMongo` in `TestImages.cs`). Please say in the report which way you went.
  - Containers don't trust the network proxy's CA by themselves. MongoDB doesn't need the internet, so this shouldn't matter. If a container does need HTTPS, mount `/root/.ccr/ca-bundle.crt` as `/etc/ssl/certs/ca-certificates.crt` (read-only).
  - If Ryuk (Testcontainers' cleanup container) gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped search tests need the real embedding model (`KUMBU_MODEL_DIR`) and a TensorX key. Neither is available here, and skipping them is expected.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool.

## How to work

- **Phase by phase, task by task as in your plan:** red test, green, **commit** (locally), then every mutation individually: break the code, watch the test fail, restore with `git checkout -- <file>` (which returns it to the green commit). Then push.
- **Commit and push after every task.** Commit messages are imperative and free-form ("Refuse structural files below maintain"), with no Conventional Commits prefix.
- **A phase ends with all tests green:** `dotnet test --solution Kumbu.slnx`, and `pnpm -C web test --run` plus `pnpm -C web build` once the phase touched the web app. If you have to stop, please stop at a phase boundary with everything green, or at least at a task boundary with everything green.
- **Evidence:** only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output counts.
- **Style:**
  - British English everywhere (colour, behaviour, authorise).
  - Match the surrounding code's naming, idiom and comment density. Comments only where the reasoning isn't obvious.
  - New source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later` where their siblings do.
- **The repository names no organisation, customer or employer.** That covers code, docs, test data and examples: use neutral names such as Alice, Bob, Carol and "Work".
- **Security architecture:** update `docs/security-architecture.md` in the phase that builds each part, as design §13 (L10) lists.
- **Uncertainty is welcome.** When the design leaves something to the builder, decide it, say so in a short comment or commit message, and list it in the report. If the design seems wrong somewhere, build the safer reading, write down why, and carry on.

## A few things that matter to us

- **Please don't merge, tag, release or deploy.** No version bumps (`version.txt`, `Kumbu.Server.csproj`, `web/package.json`) and no pushes to `master`. A rollout is something Chris does together with a person.
- No secrets in logs, assertions, test data or the report.
- Specs outside this topic stay as they are. If one looks wrong, please mention it in the report.
- These are the design's security rules. Please treat each as a hard requirement with its own test:
  - membership checked per request before a vault is loaded (§3.1);
  - membership writes bound to vault and caller (§6.0);
  - re-keys wrap only to pinned keys (§5.4);
  - members' writes store no names (§8.1);
  - the marker test (§8.2).

## Out of scope

- **The Obsidian plugin** (`kumbu-obsidian`, a separate repository): its 0.2.0 (design §10.2) is a separate run. Here, build the server side of that contract: refusal `code`s (§3.5), the `plugin` field on pairing redemption, and refusing to pair a shared vault without it.
- Live co-typing, which is an anti-goal; messages between users; owner transfer; quotas; renaming one's own vaults; the ML-KEM hybrid (§5.2, stated as a limit only).
- Releases, tags, version numbers, deployment and the images.

## The report

The last commit adds `docs/superpowers/overnighter/2026-10-31-collab-overnighter-report.md` with:

- the environment (what you installed, how Docker and MongoDB ran);
- Task 0 planned against measured (counts and time);
- per task the commit and the real test output (counts);
- every mutation individually;
- deviations from design or plan, and the calls you made;
- surprises;
- open points for Chris;
- the PR link.

Then open a **draft** pull request from `feature/collab` to `master`. Please don't merge it. If no pull request can be opened (no `gh`, no authorisation), put the compare URL `https://github.com/SCAI-NGO/kumbukumbu/compare/master...feature/collab` in the report and in `open-items.md` instead, and say so. Title "Vault sharing (collab) for 0.3.0"; the description ends with the Claude Code attribution as usual. Finally add a row with the PR link to `docs/superpowers/open-items.md` and push.

## When you are done or stuck

Send a short message via `PushNotification` if it is available. Otherwise the PR is the signal, and the report says so.

If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Getting through only some of the phases with everything green and honestly reported is a good night's work. Thank you!
