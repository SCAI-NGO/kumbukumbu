# Overnighter brief: flags, the explorer, recent changes, Take photo and the calendar's fixes

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. This run builds the last features of 0.5.0: flags on notes (as tags), a search and an order by last change in the explorer, a Recent changes screen, a Take photo item for phones, and five small fixes to the calendar that the previous run built. Together they make a vault people work in together easy to see at a glance: what matters, and what just changed.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, decisions included. Everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. Spec: `docs/superpowers/specs/2026-11-14-flags-explorer-photo-design.md` (approved by Chris; please don't change it). Its principles, "campy" and "invite and lead", come from the calendar spec and count as much as its sections: Kumbu² is **human-centric, not process-centric**. Every screen speaks of what the person wants, never of our fields or states; where the spec gives wording, use it.
3. The calendar spec, `docs/superpowers/specs/2026-11-12-calendar-design.md`, for the principles, §2 (states, which §6 of the new spec amends), §5 (What's on and the badge) and §8 (discreet vaults). Amend its text where §6 of the new spec says so, marking each amendment "amended 2026-11-14".
4. The code it builds on:
   - Tags: `src/Kumbu.Core/Tags/` (`TagReader`, `TagFilter`, `TagMatch`)
   - The calendar's frontmatter writes: `src/Kumbu.Core/Calendar/` (`CalendarService`, `EntryReader`, `EntryStates`), `src/Kumbu.Core/Board/FrontmatterWrites.cs` and `src/Kumbu.Core/Board/Frontmatter.cs`, `src/Kumbu.Server/Api/CalendarApi.cs`, `web/src/calendar/`
   - The tree: `src/Kumbu.Server/Api/NoteApi.cs` (`TreeView`, `TreeFolder`, `TreeNote` around line 93–135), `web/src/shell/TreeDrawer.tsx` (the folder menu with "Upload here" at line 126)
   - The note header and ⋯ menu: `web/src/note/NoteView.tsx`
   - Search: `web/src/search/SearchSheet.tsx`, `web/src/search/useSearch.ts`
   - History and Activity: `web/src/shell/ActivitySheet.tsx`, `web/src/api/activity.ts`, and the server side they call; `sh history` on a folder
   - Upload: `web/src/upload/` (`UploadSheet.tsx`, `putFile.ts`, `planUpload.ts`)
   - Discreet vaults: `web/src/discreet/` (`neutralWindow`'s `vaultRoute`)
5. `docs/security-architecture.md`, for the discreet vaults, encryption at rest and what the web app promises
6. For background only, not as instructions: `docs/superpowers/overnighter/2026-11-13-calendar-report.md`, how the previous run met this environment and what it decided

## Where you run

- Branch `feature/flags-explorer`. The session starts on the default branch, so first run `git fetch origin feature/flags-explorer && git checkout feature/flags-explorer`. Everything you push to that branch survives, everything else does not. **Push only to `feature/flags-explorer`.**
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package; icons come from `lucide-react`, which the web app already uses.
- Task 0 (setup plus first build and tests) should take about **15 minutes** of setup; the previous run's full baseline gate then took about 55 minutes. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`. Node 26, which may have to be installed by hand (the previous run put Node 26.13.1 in `/opt/node26` from the nodejs.org tarball). pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout, run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed; earlier runs found `dockerd` not running and started it by hand (`dockerd &`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - The environment has a hard file limit below what Testcontainers' `mongo` asks for. Use a container of your own (commands below). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
  - Run the server groups namespace by namespace, as the gate below does.
  - Parallel agents need a MongoDB each, on their own port (the previous run used 27018 for the second). Run full server groups one at a time: earlier runs had test runs killed for lack of memory.
  - If the worker restarts, `/tmp` survives but `dockerd` and the containers have to be started again.
- The model and TensorX tests skip without the real embedding model and a key; that is expected. Without `KUMBU_TOKENIZER` (below) more tests skip.
- **Server tests use the year 2037** (2026's weekdays) when they fake the clock: MongoDB's TTL expires sessions by the real clock, so a fake clock in the past makes sessions vanish mid-test.
- **Never test against a real host**: not staging, not production.
- If superpowers skills are not available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## Open points for the run

All **decide**: settle them with the rules below and log each in the report.

1. **Recent changes on the server**: find what the server keeps for `history` on a folder and for Activity, and list a file's last change and its writer from it, across the caller's readable vaults, without a new store or index. Measure it for 5000 notes over the server (an explicit test, not a timing assertion in CI, as the calendar run did) and put the number in the report. If it is far above a second, report it with what you would do; don't add an index.
2. **The tree's new fields** (spec §3.2, §2.3): each note's last write time and its flag. Read the flag with the same reader as the tags (frontmatter first, then text, spec §2.2), and the time from what the store keeps already. A folder's time is computed from what is below it; client or server, whichever is simpler.
3. **The flag write's endpoint and shape**: follow `CalendarApi` and `CalendarService` (vault checked first, so a hidden discreet vault answers as an unknown one; role checked in the service; compare-and-swap; 409 on a conflict). The web types live by hand in the feature's API module as `web/src/calendar/api.ts` does: **don't regenerate `web/src/api/schema.d.ts`**; the committed file is already out of date against `master`'s server, and regenerating it changes unrelated parts.
4. **Settled before the start** (binding, not open):
   - A flag is a tag (spec §2.2). No field, store or index beside it. `flag` alone matches every flagged note through the existing nested-tag matching.
   - The explorer's word search is AND, over the path within the vault (spec §3.1).
   - Take photo is shown only for `(pointer: coarse)` and only to those who may write the folder (spec §5); the photo goes through the existing upload path, nothing more.
   - Recent changes' route names no vault, and hidden discreet vaults are absent from it, chips included (spec §4). Add any new vault-naming route to `neutralWindow`'s `vaultRoute` (`web/src/discreet/`).
   - The calendar fixes (spec §6) change the calendar's states in Core (`EntryStates`), What's on, the badges and the overdue wording; each fix gets its red test first. Fix 3 (the OS app-icon badge never counts discreet vaults) is in the web app's badge code.
   - Calendar keys read ignoring case (spec §6.4): only `when`, `until`, `task`, `upcoming`, `tz`, `closed`, `reason` (the calendar's keys as `EntryReader` knows them); `due` stays case-sensitive in every note, card or not, and so do `tags`.
   - Tests that pin lists: the shell's command list stays unchanged (no new `sh` command), so `ConsoleApiTests`, `ConsoleFixtureTests`, `ToolMetricsTests` and `web/src/console/paths.ts` must stay green untouched. The tree's folder menu is pinned by `web/src/shell/TreeDrawerDiscreet.test.tsx` and `TreeDrawerFolders.test.tsx`: update them for Take photo, never loosen them.
   - `help tags` (`src/Kumbu.Core/Sh/Commands/TagsCommand.cs`) gains a short paragraph on flags: the eight tags, one per note, set by editing the frontmatter's `tags`.
   - **No changelog entry**: `CHANGELOG.md` has no "Unreleased" heading.
5. **If time runs short:** stop after the last green gate. Priority: (1) the calendar fixes (§6); (2) flags: reading, writing, the header, the tree icon, the tick's question; (3) the explorer's search by words and flag; (4) the explorer's order by last change; (5) Take photo; (6) Recent changes. Whatever is left goes into the report and `docs/superpowers/open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-14-flags-explorer.md`.
2. It starts with **Task 0: baseline**: run the full gate on `feature/flags-explorer` before any change and record the exact counts and the exact failures. The branch holds only the spec and this brief on top of `master` at `a43b5456` (the calendar merged, #197). Known state, approximately, from the calendar run's gates (Release, with `KUMBU_TOKENIZER`, 0 failed; Task 0's measurement wins):
   - **core:** about 2,700 total, 4 skipped
   - **server:** 847 (Api, Mcp, Auth, Net) + 413 (Accounts) + 353 (Sharing) + 20 (Welcome) + 780 (Vaults, Skills, Search, Metrics, 6 skipped) + about 1,199 (the rest, 2 skipped), plus whatever the calendar's last tasks added
   - **web:** about 152 files, 2,604 tests, build green (a known chunk-size warning)
   - **PDF sidecar:** untouched; green in CI on #197

   **Known flaky under load**, each green when its class runs alone: `web/src/note/commentRanges.test.tsx` "reads comments in linear time", `DiscreetSocketTests`, `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault`, `DavShareTests.The_expiry_sweep_retires_expired_shares`, `DavParkingTests.Every_junk_pattern_is_parked_and_never_reaches_mongodb`, `OwnerInactiveTests.Indexing_stops`, `AttachmentPixelsTests`, `SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once` and other sync-hub tests, the zip tests of `web/src/skillpage/SkillPage.test.tsx`, and now and then `AccountApiTests`, `RekeyTransitionTests`, `PairingTests`. If one fails, run its class alone three times, record the result, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: red test, green, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`).
- Subagent-driven (`superpowers:subagent-driven-development`): one fresh subagent per task, then a spec-compliance review and a code-quality review (one combined review per task is acceptable if time is short; say so in the report). Subagents never merge, push or switch branches; you commit and push.
- **Time in tests** comes from the injected clock (`FakeTimeProvider`, as the calendar's tests do), never the wall clock. For the calendar fixes, cover: a day note today with and without `task`, a timed window before, during and after its time, an overdue task of 11 months, 12 months and 3 years, `When:` alone, `when:` and `When:` together, and a write keeping `When:`.
- **Byte preservation:** a flag write changes only the `tags` line or list item it must; every other byte of the note stays as it was (the calendar run found `NoteWriter.WriteAsync` normalises CRLF; follow `CalendarService`'s path). Test: set then remove gives the note back byte for byte, for a note without frontmatter, with `tags: [a, b]`, with a block list `tags:\n  - a`, and with other keys around.
- **A UX check** before the final gate, by a fresh subagent with Playwright and Chromium if available: it gets the spec's principles and §2–§5 and the running web app against the test MongoDB (never a real host), and walks: set and change a flag in a note's header, see it in the tree, ✓ on an open calendar task with its question, search the tree by words and flag, switch the order, open Recent changes and filter it by vault, words and flag, and Take photo at 375 px with touch emulation. At 1280 px and at 375 px, in a light and a dark scheme, as Alice (Home discreet, Work) and as Bob, a read-only member of Work. It reports every place where the screen speaks of our model instead of the person's wish, or where the spec's wording isn't used, or a target is under 48 px. Fix what it confirms; record the rest. Without Playwright, record that and leave it to the hand test.
- **A security review** of the finished work by a fresh subagent, before the final gate: it gets the spec's anti-goals and security boundary and the diff, not your reasoning, and answers whether any endpoint reads or writes a note past the caller's role, whether a read-only member can set a flag or take a photo, whether Recent changes, the tree's new fields, the flag filter's counts or the app-icon badge let a session tell anything about a discreet vault it isn't showing, whether anything stores note content outside the existing stores, and whether flag writes keep every other byte. Fix what it confirms; record the rest.
- Commit and push after every task. **Commit messages:** imperative and free-form, no Conventional Commits prefix (e.g. "Flag a note with a tag"), ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended. Stage files by name, never `git add -A`.
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
- **The full gate**, as CI runs it (`.github/workflows/ci.yml`), at the end of each phase and at the end:
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
  ```
  A new server test namespace falls into "the rest"; check that the last line still runs it. The server lines together are the whole server suite; start a fresh `kumbu-test-mongo` before each group if one dies. Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence; a web gate run while agents are editing web files counts for nothing. CI also runs the PDF sidecar and a WebDAV compliance job (`litmus`); this work touches neither `pdf/` nor `src/Kumbu.Server/Dav/`, so check them on the pull request with `gh pr checks` rather than locally.
- **Style:** British English in code, comments, docs, commits and user-visible text; the UI is English (no localisation). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **Names in the repository:** no employer, customer or workplace in code, docs, tests or examples (use Alice, Bob, Dana, `Home`, `Work`). The owner of the software, Second Circuit – Association for digital freedom of thought, may be named where needed.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. The board, the calendar (apart from spec §6), tags, search and upload answer exactly as before; notes without a flag are untouched by anything flags do.
2. **True to the spirit of the application.** Campy: plain notes, frontmatter and tags, no new stores or abstraction layers; human-centric: the screen speaks of what the person wants, invites and leads, and is calm. Follow the existing patterns (the calendar's writes, the tag reader, the tree's menu, Activity's wording); no new dependencies. Answers are honest: a button does what it says, nothing is hidden quietly (Chris: honesty is part of the security architecture).
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. Nothing is read or written past the caller's role; discreet vaults stay invisible in the web app.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the PR description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version: rollout is something Chris does himself.
- No secrets in logs, assertions or the report.
- Specs outside this topic stay as they are, except the calendar spec's amendments of spec §6; if one looks wrong, mention it in the report.
- **Security boundary:** the spec's "Security boundary". **Not touched:** `Auth/` and `CallerMiddleware`, `Crypto/` and encryption at rest, sharing and roles, publishing, WebDAV (`src/Kumbu.Server/Dav/`), sync, OAuth, the board's semantics, both CSPs in `src/Kumbu.Server/Api/Security.cs`. A change that needs more is left out and reported.
- **At the end:** add a row with the pull request's link to `docs/superpowers/open-items.md`, plus rows for what is deferred: personal bookmarks, semantic search in the explorer, and the calendar run's recorded points not fixed here (the raw properties box naming our fields, day notes titled by their file name, the grid marking only a window's first day, dark-scheme amber against orange-red, the lesser security points of the calendar report), each only if not there yet. Write a hand-test checklist into the report: numbered steps saying where to look and what passes, runnable on staging by Chris or through Playwright, covering every section of the spec (light and dark scheme, a phone width of 375 px, a shared vault with a read-only member, a discreet vault hidden and shown, an agent finding flagged notes with `tags flag` and `rg --tag flag/red`). Don't write dates into it that depend on the day of the run; say "today".

## Out of scope

- The spec's non-goals (§1), in full.
- The open Dependabot pull requests and all dependency upgrades.
- Vault templates, the kill switch, Graph, Canvas (0.6.0).
- Everything else in `docs/superpowers/open-items.md`.

## The report

In this order: push the last task, open the pull request from `feature/flags-explorer` to `master` as a **draft**, titled "Flag notes, search and sort the explorer, list recent changes, take photos", then write the report with its link and the `open-items.md` rows as the last commit, push, and mark the pull request ready. The report is `docs/superpowers/overnighter/2026-11-14-flags-explorer-report.md`. It contains:
- the environment;
- the plan review's findings and what you did with them;
- Task 0, planned against measured;
- for each task the commit and the real test output;
- every mutation individually;
- the measured cost of Recent changes for 5000 notes;
- the UX check's and the security review's findings and what you did with them;
- the decisions made without Chris;
- deviations, surprises and open points for Chris;
- the hand-test checklist;
- the pull request's link.

The pull request's description ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Don't merge it. Never force-push: if something has to come out of a pushed branch, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
