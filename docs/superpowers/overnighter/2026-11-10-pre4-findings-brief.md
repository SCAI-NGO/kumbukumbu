# Overnighter brief: the findings of the pre.4 tests

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. This morning's Playwright tests of `v0.4.0-pre.4` found two faults in discreet vaults that block 0.4.0: text typed offline near the end of a reveal is lost (F9), and after that end the screen names the hidden note (F10). Two small things ride along (F7, F8). This is a day run: the maintainer is back at about 17:00 CET (16:00 UTC) to hand-test it, and the release may follow tomorrow. Finding the real cause counts for more than a quick patch.

Nobody is available to answer questions during this run. That is intended: the spec has been reviewed by Chris, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-10-pre4-findings-design.md`, reviewed and approved by Chris. Please don't edit it.
4. For context, as needed: `docs/superpowers/specs/2026-11-09-discreet-vaults-design.md` §3.8 (hiding again, the late save); `web/src/discreet/hideAgain.ts` (`hold`, `saveOpen`, `saveAhead`, `hideAgain`/`run`, `useDiscreetMode`, `followSession`) and its tests `hideAgain.test.tsx`; `web/src/discreet/neutralWindow.ts`; `web/src/state/editor.ts` (`beforeRead`, `unsaved`, `draft`); the editor's save: `web/src/note/useAutosave.ts`, `web/src/note/MarkdownEditor.tsx:35-53,138`, `useSaveNote` in `web/src/api/notes.ts:274`; `src/Kumbu.Server/Api/LateSave.cs` (read only: the server's late-save rule, not to be changed); `web/src/api/publications.ts` (`usePublications`, `usePublication`) and where `usePublication` is called; `src/Kumbu.Core/Search/SectionSearch.cs:137-160` and its tests.

## Where you run

- Branch `fix/pre4-findings`, based on `master` at `fc3edeb`. The session starts on the default branch, so first run `git fetch origin fix/pre4-findings && git checkout fix/pre4-findings`. Everything you push to that branch survives, everything else does not. Don't merge `master` into it.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", check whether the setup script's daemon is still running, and start it again if you can.
  - Earlier runs met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own with `--ulimit nofile=20000:20000`, named `kumbu-test-mongo`, `mongo:7.0` as a replica set (`--replSet rs0 --bind_ip_all`, then `rs.initiate()`), and `KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true"`. Give every full run a fresh one (`docker rm -fv kumbu-test-mongo` first). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped tests need the real embedding model and a TensorX key; skipping them is expected. Running as root can skip 2 more. CI also sets `KUMBU_TOKENIZER` (see `ci.yml`); without it more tests skip locally, so compare counts with that in mind.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool and say so in the report.

## Open points for the run

All **decide**: settle them with the rules below and log each in the report.

1. **Root cause before fix (spec §2, §3).** The spec lists candidate causes for F9 and F10, seen from the code and not checked. Treat them as a starting list, not as the answer: add any you find, rank them, and confirm or rule out each with a test before changing code. Infrastructure counts too (the browser's `online` event, react-query's own offline handling and `networkMode`, retries, the server's late-save rule answering 404 for a second save). Write the ranked list with its verdicts into the report. A lead from the review, not checked: `useSaveNote` (`web/src/api/notes.ts:274`) is a react-query mutation on the default `networkMode: "online"`; offline it **pauses** instead of failing, with the content it had then (` off0`). Then `flush`, `saveOpen` and with them `hideAgain`'s `run` (`hideAgain.ts:152`) wait until the browser is online, and `hold` is never reached while offline; once online, the paused save is the one late save, the next save gets 404, and the held retry finds no late-save marker left. That fits the staging timeline (end at about 07:07:15, online at 07:07:51).
2. **Reproducing F9/F10 in tests.** Vitest with fake timers; take react-query offline too (`onlineManager.setOnline(false)` from `@tanstack/react-query`, or dispatch `offline`): stubbing `navigator.onLine` alone won't reproduce it, and no existing test does this yet; `fetch` stubbed per request (see `hideAgain.test.tsx` and `web/src/reader/Reader.test.tsx`'s `stubFetch` for patterns). The red test is the spec's example; let the stubbed server behave as the real one (`src/Kumbu.Server/Auth/CallerMiddleware.cs:159-216`, `Sessions.cs:300-366`, `Api/LateSave.cs`; find them with `rg -n LateSave src`): the late-save marker is set by `PUT /api/account/discreet/open-note` (`web/src/discreet/openNote.ts`); only a **timed** reveal has a late save; after the end the server takes a save of that note within 5 minutes and answers 404 otherwise; a late save that fails puts the marker back once (`LateSaveRetried`), so there is one second try; a late save's 409 carries only the status, no current text or version, so the client can't recover from it; `POST /api/account/discreet/hide` (✕) clears the marker; an ordinary save answers 409 with the current text and version for a stale version.
3. **The server's late-save rule takes one save.** If the cause turns out to need a second late save (or any server change), don't change the server: find a client-side way (save the latest text in that one late save), or leave that part out and record it in `docs/superpowers/open-items.md`.
4. **F7: what the vault's list carries.** `GET /api/vaults/{vault}/publications` gives `older`, `snapshotAt`, `changedAt`, but no link (`ListAsync` gets `linkBase: null`, `PublicationApi.cs:68`); members get it too, paths only. So the owner's **Copy link** still needs the per-note request: ask it only for a note the list names. Don't add the link to the list (a server change). Two traps: `MobileShell.tsx:264-265` derives `mayPublish` from `publication.data === null`, so with a gated query Publish would vanish from the menu; derive it from the list. And today the list is asked only for the user's own vaults (`MobileShell.tsx:267`, `TreeDrawer.tsx:470`, `NoteView.tsx:460`): a member's bar needs the list asked for a shared vault as well. While publishing is off, Unpublish still shows (`PublicationBar.tsx:22-29`).
5. **F8's tie-breaking.** Pin with tests: the whole query beats most terms; most distinct terms beat fewer; the first line among equals; no term at all gives the section's start as today. Keep `EarlierSearch` and both `NoteApi` callers working.
6. **✕ while offline: leave out.** ✕ clears the late-save marker, so whether a held draft is saved after ✕ offline depends on a race between its retry and the resent hide (`followSession`, `hideAgain.ts:272`). The spec doesn't settle which outcome is right: don't change that behaviour, describe what happens today in the report, and record the question in `docs/superpowers/open-items.md`. (The screen naming nothing after ✕ is in scope, spec §3.)
7. **The test list.** You can't write to Chris' Kumbu²: the hand-test checklist goes into the report.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-10-pre4-findings.md`.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures. Known state on `master` at `fc3edeb`: **web 125 files, 2192 tests, all passing, build green** (measured locally on 2026-11-10); **server and core: all passing in CI with 4 skipped** (about 5,500 tests; `dotnet test --solution Kumbu.slnx` locally runs them all in one go, about 14 minutes). `AccountApiTests`, `RekeyTransitionTests`, `PairingTests` and the sync-hub tests have failed now and then under load: if Task 0 shows one, run its class alone three times, record whether it is flaky, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. **Order by value:** (1) §2 F9 cause and fix; (2) §3 F10 cause and fix (they may share a cause: if so, one task with both tests); (3) §4 F7; (4) §5 F8. Each its own commit; a gate (web tests and build; for §5 also the core tests) after each.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push.
- Commit and push after every task. Commit messages imperative and free-form ("Save the latest text when the browser is back online"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- Web tests, one file: `pnpm -C web test --run <file>`; all: `pnpm -C web test --run`. **The web's CI gate is `pnpm -C web build`** (it runs the type check). Core tests, one class: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ClassName"`; server tests likewise with `tests/Kumbu.Server.Tests`.
- Full verification at the final gate, as CI does (`.github/workflows/ci.yml`): `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --solution Kumbu.slnx --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **The clock:** the maintainer is back at about 16:00 UTC. If time runs short, stop at the last green gate, push, open the pull request and say in the report which sections are left. F9 and F10 matter most; F7 and F8 may be left.
- **Style:** British English in code, comments, docs, commits and user-visible text. Match the surrounding code's naming, idiom and comment density (the discreet code explains its reasons in full sentences; keep that); comments only where the reasoning isn't obvious. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples. Use Alice, Bob, Dana, `Home`, `Tagebuch`.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. Step 2.9 online must stay as it is.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture; no new dependencies. Where Obsidian's and Kumbu²'s behaviour differ, Kumbu²'s semantics come first.
3. **Opens no new security hole, and gives nothing away.** Once a reveal has ended, nothing on screen names a hidden vault or a note in it. Outside discreet mode nothing may differ between users with and without a PIN or discreet vaults.
4. **True to the spec.** Chris' guiding wish here: **the user's text is sacred, and so is the discretion.**

If a question can only be answered by changing the spec's scope, do not guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the pull request description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself. One pull request is the result.
- No secrets in logs, assertions or the report.
- **Security boundary:** the web app and `src/Kumbu.Core/Search/SectionSearch.cs` only. **Not touched:** the server's API and its answers, `LateSave.cs` and the late-save window, the PIN check, the session, encryption, accounts, sharing, WebDAV, sync, the CSP. If something seems to need a server change, leave it out and record it.
- At the end: a row with the pull request's link in `docs/superpowers/open-items.md`, and in the report a hand-test checklist with numbered steps, where to look and what passes, including the spec's example as a whole. Don't change anything else outside the plan's files.

## Out of scope

- Everything spec §1 lists as a non-goal.
- The leftovers of the pre.3 findings in `docs/superpowers/open-items.md` (row of 2026-11-09), unless one turns out to be part of F9's or F10's cause.
- The Obsidian plugin (another repository).

## The report

First open a pull request against `master` titled "The findings of the pre.4 tests", whose body lists the ranked causes with their verdicts, the decisions made without Chris, says that the security boundary is not touched, and ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Then, as the last commit, `docs/superpowers/overnighter/2026-11-10-pre4-findings-report.md`, with: the environment; the plan review findings and what you did with them; Task 0 planned vs. measured; the ranked causes of F9 and F10 and how each was confirmed or ruled out; for each task the commit and the real test output; every mutation individually; the decisions made without Chris; deviations, surprises, sections left if any, open points for Chris; the hand-test checklist; the pull request's link, together with a row linking it in `docs/superpowers/open-items.md`.

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
