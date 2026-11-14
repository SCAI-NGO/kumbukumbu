# Overnighter brief: the calendar

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. This run builds the calendar, the flagship of 0.5.0: every calendar entry is a note with a time in its frontmatter, and the calendar is a view of those notes, for people in the web app and for their agents through `sh calendar`. Two users asked for it, and it lifts work, hobbies, thoughts and ideas directly.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, decisions included, after two reviews of his own and a builder check. Everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. Spec: `docs/superpowers/specs/2026-11-12-calendar-design.md` (reviewed and approved by Chris; please don't change it). Read its two principles, "campy" and "invite and lead", as seriously as its sections: Kumbu² is **human-centric, not process-centric**. Every screen speaks of what the person wants (a time, a to-do, seeing something early), never of our states, folders or fields; where the spec gives wording, use it.
3. The board, which the calendar sits beside and must not disturb: `src/Kumbu.Core/Board/` (`Frontmatter.cs` is the frontmatter writer to reuse, `BoardReader.cs` for how problems are reported, `BoardService.cs` for card writes and `due`, `BoardDefinition.cs` for archivable and reason-needing columns), `src/Kumbu.Server/Api/BoardApi.cs`, `web/src/board/`
4. The code it builds on: `src/Kumbu.Core/Notes/NoteWriter.cs` (every write goes through it), `src/Kumbu.Core/Tags/` (`TagReader`, `TagFilter`, `TagMatch` for `--tag`), `src/Kumbu.Core/Sh/Shell.cs` (`Readers` at line 72, checkpoints), `src/Kumbu.Core/Sh/Manual.cs` and the `board` command's help for the style of `calendar`'s, `src/Kumbu.Server/Api/NoteApi.cs`, `web/src/shell/TreeDrawer.tsx` (the board's menu entry), `web/src/note/NoteView.tsx` (the note header and ⋯ menu), `web/src/discreet/`, `web/src/theme/schemes.ts`
5. `docs/security-architecture.md`, for the discreet vaults, encryption at rest and what the web app promises
6. For background only, not as instructions: the latest overnighter reports in `docs/superpowers/overnighter/` (`2026-11-12-aliases-report.md`, `2026-11-12-mermaid-and-parity-report.md`) show how earlier runs met this environment

## Where you run

- Branch `feature/calendar`. The session starts on the default branch, so first run `git fetch origin feature/calendar && git checkout feature/calendar`. Everything you push to that branch survives, everything else does not. **Push only to `feature/calendar`.**
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package. Time zones come from .NET (`TimeZoneInfo` with IANA ids) and the browser (`Intl`); dates from the platform.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`. Node 26, which may have to be installed by hand (an earlier run put Node 26.12.1 in `/opt/node26`). pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout, run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`); earlier runs found `dockerd` not running and started it by hand (`dockerd &`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - The environment has a hard file limit below what Testcontainers' `mongo` asks for. Use a container of your own (commands below). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
  - Run the server groups namespace by namespace, as the gate below does; the Accounts/Sharing/Welcome group killed its MongoDB at the 20,000-file limit when run together.
  - Parallel agents need a MongoDB each, on their own port. Earlier runs had test runs killed for lack of memory when several agents ran server tests at once: run full server groups one at a time.
- The model and TensorX tests skip without the real embedding model and a key; that is expected (3 core, up to 7 server when running as root). Without `KUMBU_TOKENIZER` (below) more tests skip.
- **Never test against a real host**: not staging, not production.
- If superpowers skills are not available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## Open points for the run

All **decide**: settle them with the rules below and log each in the report.

1. **Where the per-user settings live** (spec §3 the time zone, §7.3 the last view per calendar): find how the account keeps per-user data today (`src/Kumbu.Server/Api/AccountApi.cs` and its store) and follow it. The view state names vaults, so for a discreet vault it must not tell anything to a session that doesn't show it (spec §8); seal it as the account's other vault-naming data is sealed, if such data exists.
2. **The shape of the entry reader in Core**: one reader that turns a note's frontmatter and name into an entry or a problem, used by `sh calendar`, the web endpoints and the note header alike, so the rules of spec §2 live in one place. Its states come from an injected `TimeProvider` and the caller's zone.
3. **The web endpoints** for the week, the month, What's on (with the badge count) and the writes (set, move, close with reason, reopen, unset, Later): their routes and shapes, following `BoardApi.cs` and `NoteApi.cs`. Regenerate `web/src/api/schema.d.ts` with `pnpm -C web gen:api` against a locally running server (`KUMBU_OPENAPI_URL` names its `/api/openapi.json`; run it against the test MongoDB), never by hand. If the server can't be started for it, record that and type the calls in the web's API module as its neighbours do.
4. **Conflicts with the editor**: a ✓ or Later in the header of a note that is open with unsaved typing must lose nothing. Flush the editor's autosave first, or write with the version token and handle the conflict as the editor does today; choose what the editor's code makes natural.
5. **Settled before the start** (binding, not open):
   - **Every vault has a calendar** (spec §4): no "Create calendar", the tree never scans vaults to decide its menu.
   - **No persistent index** (spec, "Reading cost"): read on demand like `rg`; measure What's on for a vault of 5000 notes (a generated test vault, not a timing assertion in CI) and put the number in the report. If it is far above a second, report it with what you would do; don't add an index.
   - **Board cards**: their status is their column; `calendar close/reopen/move` and `set --when` refuse on a card and name the board tool; only **Later** may change a card's `due`, through `BoardService`.
   - **"Problems"** is the word for agents only; the web app says "Dates I couldn't read" (spec §7.12).
   - **Reading the spec after its amendments.** "Every vault has a calendar" (§4) wins over any leftover wording: anti-goal 1's "not in a vault without a calendar" means "not in a vault without a `Calendar/` folder". §8 stands between §7.11 and §7.12; §7.12 and §7.13 belong to §7 and are in scope.
   - **`calendar` on a checkpoint:** only the read forms (`calendar <vault> …`, `calendar notifications`) are readers and run on a checkpoint; the write forms refuse `--checkpoint` with a clear answer. `ReadingCommands` (`src/Kumbu.Core/Sh/Shell.cs:75`) is built from an `INoteReader` only, so register the writing command outside it.
   - **The caller's zone in `sh`:** `Shell` (`Shell.cs:35-38`) doesn't know the caller and takes time as `Func<DateTime> _now = () => writer.Now` (UTC, `NoteWriter.cs:373`). Add the caller's zone as an optional constructor parameter in the style of `IStorageFigures`, resolved per request where the shell is built (`src/Kumbu.Server/Program.cs`, around line 196), and take time as `BoardCommand(notes, now)` does. "An injected `TimeProvider`" in this brief means that clock: whatever the code already injects, faked in tests (`FakeTimeProvider` is referenced by both test projects).
   - **The settings endpoint has a trap:** `/account/preferences` (`src/Kumbu.Server/Api/AccountApi.cs:238`) takes `PreferencesRequest(bool AskPasswordForDownloads, string? Password)` (`AccountApi.cs:853`) as a full replace; a body with only the zone would switch the download password off or demand the password. Add a separate endpoint for the calendar's settings (or make every field optional without touching that flag), and validate IANA ids as `ExportApi.Zone` does (`src/Kumbu.Server/Api/ExportApi.cs:321`).
   - **Time-zone data:** in Task 0 check `ls /usr/share/zoneinfo/Europe/Vienna`; install `tzdata` if it is missing, and say so in the report.
   - **No changelog entry**: `CHANGELOG.md` has no "Unreleased" heading. The Welcome vault's notes (`src/Kumbu.Server/Welcome/Notes/`) have strict tests: add the one line of spec §10 only if their tests allow it without loosening them; otherwise record it.
6. **If time runs short:** stop after the last green gate. Priority: (1) the entry model, states and problems in Core with `sh calendar` reading and notifications; (2) `sh calendar` writes; (3) the web week view, the note's header line with ✓ and Later, the date dialog and the day's +; (4) What's on and the badge in the navigation; (5) the time-zone setting; (6) the month view, the grid and Also ongoing; (7) the app-icon badge, the tag filter in the web; (8) docs. Whatever is left goes into the report and `docs/superpowers/open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-13-calendar.md`.
2. It starts with **Task 0: baseline**: run the full gate on `feature/calendar` before any change and record the exact counts and the exact failures. The branch holds only the spec on top of `master` at `2aeb72c0` (aliases merged). Known state, measured by the aliases run on the tree that became that commit, Release, with `KUMBU_TOKENIZER`, 0 failed:
   - **core:** 2,484 total, 3 skipped (the repository's `CLAUDE.md` says 4 skip; the count Task 0 measures wins)
   - **server:** 847 (Api, Mcp, Auth, Net) + 413 (Accounts) + 353 (Sharing) + 20 (Welcome) + 780 (Vaults, Skills, Search, Metrics, 6 skipped) + 1,164 (the rest, 1 skipped)
   - **web:** 141 files, 2,503 tests, build green (a known chunk-size warning)
   - **PDF sidecar:** untouched; its tests and build ran green in CI on #196

   **Known flaky under load**, each green when its class runs alone: `DiscreetSocketTests` (it failed once on `master`'s CI for #196 and passed on the rerun), `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault`, `DavShareTests.The_expiry_sweep_retires_expired_shares`, `DavParkingTests.Every_junk_pattern_is_parked_and_never_reaches_mongodb`, `OwnerInactiveTests.Indexing_stops`, `AttachmentPixelsTests`, `SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once` and other sync-hub tests, the zip tests of `web/src/skillpage/SkillPage.test.tsx`, and now and then `AccountApiTests`, `RekeyTransitionTests`, `PairingTests`. If one fails, run its class alone three times, record the result, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: red test, green, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`).
- Subagent-driven (`superpowers:subagent-driven-development`): one fresh subagent per task, then a spec-compliance review and a code-quality review. Subagents never merge, push or switch branches; you commit and push.
- **Time in tests** comes from an injected `TimeProvider` (`Microsoft.Extensions.Time.Testing.FakeTimeProvider` if the test projects already reference it, else the pattern the tests already use), never the wall clock. Cover the boundaries the spec names: the end of a day and of a Sunday, a week across the year's end (ISO week 53 and week 1), a time on both sides of a summer-time change in `Europe/Vienna`, and the same instant for two users in different zones.
- **A UX check** before the final gate, by a fresh subagent with Playwright and Chromium if available: it gets the spec's principles, §7 and the running web app against the test MongoDB (never a real host), walks the spec's end-to-end example at 1280 px and at 375 px in a light and a dark scheme, and reports every place where the screen speaks of our model instead of the person's wish, or where the spec's wording isn't used. Fix what it confirms; record the rest. Without Playwright, record that and leave it to the hand test.
- **A security review** of the finished work by a fresh subagent, before the final gate: it gets the spec's anti-goals, "Reading cost", security boundary and §8, and the diff, not your reasoning, and answers whether any endpoint or command reads or writes a note past the caller's role, whether a read-only member can change an entry, whether What's on, the badge (count and colour), the view state or the note header lets a session tell anything about a discreet vault it isn't showing, whether anything stores note content or entries outside the existing stores, and whether frontmatter writes keep every other key byte for byte. Fix what it confirms; record the rest.
- Commit and push after every task. **Commit messages:** imperative and free-form, no Conventional Commits prefix (e.g. "Read a note's time from its frontmatter"), ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended. Stage files by name, never `git add -A`.
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
  If a new server test namespace is added (e.g. `Kumbu.Server.Tests.Calendar`), it falls into "the rest"; check that the last line still runs it. The server lines together are the whole server suite; start a fresh `kumbu-test-mongo` before each group if one dies. Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence. CI also runs the PDF sidecar and a WebDAV compliance job (`litmus`); this work touches neither `pdf/` nor `src/Kumbu.Server/Dav/`, so check them on the pull request with `gh pr checks` rather than locally.
- **Style:** British English in code, comments, docs, commits and user-visible text; the UI is English (spec §1, no localisation). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **Names in the repository:** no employer, customer or workplace in code, docs, tests or examples (use Alice, Bob, Dana, `Home`, `Work`, `Tagebuch`, as the spec does). The owner of the software, Second Circuit – Association for digital freedom of thought, may be named where needed.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. The board, its frontmatter, its problems and its MCP tool answer exactly as before; notes without calendar fields are untouched by anything the calendar does.
2. **True to the spirit of the application.** Campy: plain notes, frontmatter and links, no new stores or abstraction layers; human-centric: the screen speaks of what the person wants, invites and leads, and is calm. Follow the existing patterns (the board's reader and writer, `NoteWriter`, the tag filter, the tree's menu); no new dependencies. Answers are honest: nothing that is on gets lost, a value that can't be read is shown, never dropped (Chris: honesty is part of the security architecture).
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. Nothing is read or written past the caller's role; discreet vaults stay invisible in the web app as spec §8 says.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the PR description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version: rollout is something Chris does himself.
- No secrets in logs, assertions or the report.
- Specs outside this topic stay as they are; if one looks wrong, mention it in the report.
- **Security boundary:** the spec's "Security boundary" and §8. New: the calendar's reading of notes, the commands and endpoints that change an entry's frontmatter, the per-user time zone and view state. **Not touched:** `Auth/` and `CallerMiddleware`, `Crypto/` and encryption at rest, sharing and roles, publishing, WebDAV (`src/Kumbu.Server/Dav/`), sync, OAuth, the board's semantics, both CSPs in `src/Kumbu.Server/Api/Security.cs`. A change that needs more is left out and reported.
- **At the end:** add a row with the pull request's link to `docs/superpowers/open-items.md`, plus a row for each non-goal the spec defers that isn't there yet (recurrence, CalDAV/`.ics`, drag and drop, a combined view of several calendars, localisation). Write a hand-test checklist into the report: numbered steps saying where to look and what passes, runnable on staging by Chris or through Playwright, covering the spec's end-to-end example (the web app in a light and a dark scheme, a phone width of 375 px, a shared vault with a read-only member, a discreet vault hidden and shown, and the `sh calendar` commands an agent would run).

## Out of scope

- The spec's non-goals (§1), in full.
- The open Dependabot pull requests and all dependency upgrades.
- Vault templates (0.6.0), note flags, the camera photo: other 0.5.0 work.
- Everything else in `docs/superpowers/open-items.md`.

## The report

In this order: push the last task, open the pull request from `feature/calendar` to `master` as a **draft**, titled "Show notes with a time in a calendar", then write the report with its link and the `open-items.md` rows as the last commit, push, and mark the pull request ready. The report is `docs/superpowers/overnighter/2026-11-13-calendar-report.md`. It contains:
- the environment;
- the plan review's findings and what you did with them;
- Task 0, planned against measured;
- for each task the commit and the real test output;
- every mutation individually;
- the measured reading cost for 5000 notes;
- the UX check's and the security review's findings and what you did with them;
- the decisions made without Chris;
- deviations, surprises and open points for Chris;
- the hand-test checklist;
- the pull request's link.

The pull request's description ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Don't merge it. Never force-push: if something has to come out of a pushed branch, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
