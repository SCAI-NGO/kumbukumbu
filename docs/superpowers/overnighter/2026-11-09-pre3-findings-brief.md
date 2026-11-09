# Overnighter brief: the findings of the pre.3 tests

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. Tonight's work is small and about understanding: the evening's tests of `v0.4.0-pre.3` found nothing broken, but the maintainer himself got stuck setting up discreet vaults, and a new user lands in the wrong vault. Six findings, web app and texts only. They go into 0.4.0, due Monday 2026-11-16, so care and a clean pull request count for more than speed.

Nobody is available to answer questions during this run. That is intended: the spec has been reviewed by Chris, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-09-pre3-findings-design.md`, reviewed and approved by Chris. Please don't edit it.
4. For context, as needed: `README.md` "Discreet vaults" (around line 376); `docs/superpowers/specs/2026-11-09-discreet-vaults-design.md`; `web/src/discreet/` (`PinDialog.tsx`, `SetPinDialog.tsx`, `DiscreetPill.tsx` with `useRevealed`, `api.ts`, `hideAgain.ts`); `web/src/shell/TreeDrawer.tsx` (`vaultItems` around `:518`); `web/src/shell/MobileShell.tsx` (`:237`, and the heading rename's `setFollow` around `:210`); `web/src/routes/Settings.tsx` (the Discreet vaults section and its switches); `src/Kumbu.Server/Welcome/Notes/*.md` and `tests/Kumbu.Server.Tests/Welcome/`.

## Where you run

- Branch `fix/pre3-findings`, based on `master` at `6a7a4c7`. The session starts on the default branch, so first run `git fetch origin fix/pre3-findings && git checkout fix/pre3-findings`. Everything you push to that branch survives, everything else does not. Don't merge `master` into it.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", check whether the setup script's daemon is still running, and start it again if you can.
  - Earlier runs met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own with `--ulimit nofile=20000:20000`, named `kumbu-test-mongo`, `mongo:7.0` as a replica set (`--replSet rs0 --bind_ip_all`, then `rs.initiate()`), and `KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"`. Give every full run a fresh one (`docker rm -fv kumbu-test-mongo` first). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped tests need the real embedding model and a TensorX key; skipping them is expected. Running as root can skip 2 more.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool and say so in the report.

## Open points for the run

From an independent review of spec and brief against the code at `6a7a4c7`. All **decide**: settle them with the rules below and log each in the report. Where a reading is given, it is the recommended one.

1. **F1, the link (§2).** `Section` in `web/src/routes/Settings.tsx` already takes an `id` (`:60`), and `InstallSection` has the hash-scroll effect for `/settings#install` (`:752-757`, linked from `InstallHint.tsx:70`): copy that pattern for `id="discreet-vaults"`. The PIN dialog opens from inside `TreeDrawer`, which on a phone is itself a modal: the link also closes the drawer (`setOverlay("none")`, as the Settings link in the account row does, `TreeDrawer.tsx:364`). Put the line directly under "4 to 6 digits", above the place the error appears, so it never moves.
2. **F2, the Welcome notes (§3).** `WelcomeTextTests` pins no exact texts but some rules: the eight file names, each starting with `# Title`; `[[Start here]]` in every note but Start here; wikilink subpaths must be real headings (changing a heading can break a `#subpath` link elsewhere); no vendor names; nothing like "rename … vault". Add "More vaults…" and "Settings → Discreet vaults" to `The_menu_names_quoted_are_the_shared_labels` if the test works that way. Read all eight notes first and put the section where a new user would look for privacy or the vault menu. Seeded texts reach only new accounts: the hand-test step needs a fresh user.
3. **F3 (§4).** The fix is at `MobileShell.tsx:237` only; Welcome is created during the join, so it is in the first vault list. `hideAgain.ts:166`'s fallback can also pick Skills first: leave it ("nothing else changes") and log it.
4. **F4 (§5).** `vaultItems` acts on the active vault only; tell Skills apart with `active?.kind === "skills"` (`active` around `TreeDrawer.tsx:445`) and read the mark from `active.discreet`. Revealed state: `useRevealed` from `DiscreetPill.tsx`. Make the call with `useMarkDiscreet()` (`web/src/discreet/api.ts`), which invalidates `["vaults"]`, as Settings does; disable the entry while it is pending. Place it just before "Delete vault…" / "Leave this vault…", icons `Eye` / `EyeOff` from lucide. The confirmation: `useCopied` (`web/src/shell/useCopied.tsx`) is an `aria-live="polite"` div with "Copied" / "Couldn't copy" hard-coded and a 3 s timer; give it a general way to show a text, or add a small sibling with the same look, without breaking `useCopied.test.tsx` and `TreeDrawerAttachments.test.tsx`. A failure shows as `<FormError>` like the drawer's other vault actions (`createBoard.isError`, `TreeDrawer.tsx:569-578`). Marking a vault whose note is open is already handled (`neutralWindow.ts:141-145`).
5. **F5, the cause (§6).** Both renames go through `moveNote` (`web/src/api/notes.ts:304-311`); `MoveDialog` isn't quiet either. `moveNote` removes the `note`, `links`, `backlinks` and `history` queries for `from`, then invalidates `note` and `publication`; but the navigation comes only afterwards (`moved()` plus `follow`, `nameFromHeading.ts:187-188`, or `MoveDialog`'s onSuccess), and until then the old path's observers are still mounted (`NoteView key={path}`, `usePublication(path)` in `MobileShell`). So the removed `["note", from]` is rebuilt and fetched (the 404), and `["publication", from]` (`publications.ts:50`) is only invalidated (the repeated GETs, with retries). Removing a query that an observer still watches makes it fetch again. Recommended: inside `moveNote`, keep the `from` keys out of the invalidation and add `publication` to what is dropped, and drop them only once the URL and editor state have moved on. Fix both renames; the test is "no request for the old path" for both.
6. **The test list (§9).** You can't write to Chris' Kumbu²: the hand-test checklist goes into the report.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-09-pre3-findings.md`.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures. Known state on `master` at `6a7a4c7`, from CI on 2026-11-09 (the server tests split by namespace across four jobs): **5524 tests in total, all passing, 4 skipped** (`dotnet test --solution Kumbu.slnx` locally runs them all in one go, about 14 minutes); **web 120 files, about 2170 tests, all passing, build green**. `AccountApiTests`, `RekeyTransitionTests`, `PairingTests` and the sync-hub tests have failed now and then under load: if Task 0 shows one, run its class alone three times, record whether it is flaky, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. **Order by value:** (1) §4 F3; (2) §2 F1; (3) §5 F4; (4) §3 F2; (5) §7 F6; (6) §6 F5. Each its own commit; a gate (web tests and build) after each.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push.
- Commit and push after every task. Commit messages imperative and free-form ("Start a new user in Welcome, not in Skills"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- Web tests, one file: `pnpm -C web test --run <file>`; all: `pnpm -C web test --run`. **The web's CI gate is `pnpm -C web build`** (it runs the type check). Server tests, one class: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`.
- Full verification at the final gate, as CI does (`.github/workflows/ci.yml`): `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **If the night runs short**, stop at the last green gate, push, and say in the report which sections are left.
- **Style:** British English in code, comments, docs, commits and user-visible text. Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`. Menu labels and texts exactly as the spec writes them.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples. Use Alice, Bob, Dana, `Home`, `Tagebuch`.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture; no new dependencies. Where Obsidian's and Kumbu²'s behaviour differ, Kumbu²'s semantics come first.
3. **Opens no new security hole, and gives nothing away.** Outside discreet mode nothing may differ between users with and without a PIN or discreet vaults.
4. **True to the spec.** Chris' guiding wish here: **what confused him will confuse users; make the way obvious without magic.**

If a question can only be answered by changing the spec's scope, do not guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the pull request description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself. One pull request is the result.
- No secrets in logs, assertions or the report.
- **Security boundary: not touched.** Web app and texts only; the server's API, the PIN check, the single answer and the rate limit stay as they are. Welcome note texts are server files but only text. If something seems to need a server change, leave it out and record it.
- At the end: a row with the pull request's link in `docs/superpowers/open-items.md`, and in the report a hand-test checklist with numbered steps, where to look and what passes, including the spec's example as a whole. Don't change anything else outside the plan's files.

## Out of scope

- Everything spec §1 lists as a non-goal.
- The Obsidian plugin (another repository).

## The report

First open a pull request against `master` titled "The findings of the pre.3 tests", whose body lists the decisions made without Chris, says that the security boundary is not touched, and ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Then, as the last commit, `docs/superpowers/overnighter/2026-11-09-pre3-findings-report.md`, with: the environment; the plan review findings and what you did with them; Task 0 planned vs. measured; for each task the commit and the real test output; every mutation individually; the decisions made without Chris; deviations, surprises, sections left if any, open points for Chris; the hand-test checklist; the pull request's link, together with a row linking it in `docs/superpowers/open-items.md`.

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
