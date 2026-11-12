# Overnighter brief: Mermaid diagrams and three Obsidian parity items

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. Many of its users come from Obsidian, and closing the everyday gaps makes Kumbu² the place they work in. This run builds four of 0.5.0's features: Mermaid diagrams (a flagship of the release), embedded notes in `cat` for agents, block references `^id`, and unlinked mentions.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, decisions included, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. Spec: `docs/superpowers/specs/2026-11-12-mermaid-and-parity-design.md` (reviewed and approved by Chris; please don't change it)
3. The 0.4.0 parity spec, for how the last parity work was built: `docs/superpowers/specs/2026-11-09-obsidian-parity-quick-wins-design.md`
4. The code it builds on: `web/src/note/MarkdownView.tsx`, `web/src/note/math.ts` (lazy KaTeX), `web/src/note/wikiLinks.ts`, `web/src/note/NoteView.tsx`, `web/src/note/BacklinksList.tsx`, `web/src/reader/Reader.tsx`; `src/Kumbu.Core/Sh/Commands/Cat.cs`, `LinksCommand.cs`, `Backlinks.cs`, `src/Kumbu.Core/Links/`, `src/Kumbu.Core/Sections/`, `src/Kumbu.Core/Notes/NoteMover.cs`, `src/Kumbu.Server/Api/NoteApi.cs`
5. `docs/security-architecture.md`, for the CSPs and what the web app promises about note content

## Where you run

- Branch `feature/mermaid-parity`. The session starts on the default branch, so first run `git fetch origin feature/mermaid-parity && git checkout feature/mermaid-parity`. Everything you push to that branch survives, everything else does not. **Push only to `feature/mermaid-parity`.**
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**, with one exception: the web app may add `mermaid` (the current stable 11.x release, an exact version range as the other dependencies use, the lockfile committed). No other new npm or NuGet package.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`. Node 26, which may have to be installed by hand (an earlier run put Node 26.12.1 in `/opt/node26`). pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout, run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`); the last run found `dockerd` not running and started it by hand (`dockerd &`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - The environment has a hard file limit below what Testcontainers' `mongo` asks for. Use a container of your own (commands below). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
  - The second server group (Accounts, Sharing, Welcome) killed its MongoDB at the 20,000-file limit in earlier runs; run it namespace by namespace, as the gate below does.
  - Parallel agents need a MongoDB each, on their own port. Earlier runs had test runs killed for lack of memory when several agents ran server tests at once: run full server groups one at a time.
- The 4 skipped tests need the real embedding model and a TensorX key; skipping them is expected. Running as root can skip 2 more. Without `KUMBU_TOKENIZER` (below) more tests skip.
- **Never test against a real host**: not staging, not production. Chromium for Playwright may load `setContent` pages with network requests aborted.
- If superpowers skills are not available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## Open points for the run

All **decide**: settle them with the rules below and log each in the report.

1. **The shape of the block finders** (spec §4.1), one in Core and one in the web, and whether they share a cases file.
2. **Wide diagrams** (spec §2.2): lightbox or scrolling within the frame.
3. **The API for unlinked mentions** (spec §5.3): a parameter on the backlinks endpoint or an endpoint beside it; regenerate `web/src/api/schema.d.ts` with `pnpm -C web gen:api` against a locally running server (`KUMBU_OPENAPI_URL` names its `/api/openapi.json`; run it against the test MongoDB), never by hand. If the server can't be started for it, record that and type the call in the web's API module as its neighbours do.
4. **Mermaid under the reader page's CSP** (spec §2.3): find out; if it can't work without changing the CSP, the reader page shows the code block and you record it in `docs/superpowers/open-items.md`. Never change a CSP.
5. **Settled before the start** (from the review of spec and brief; binding, not open):
   - **The reader's isolation.** `web/src/reader/isolation.test.ts` lets the reader import only `react`, `react-dom/client`, `react-markdown`, `remark-gfm` and files in `web/src/reader/`. So the Mermaid component and the `^id` hiding live in `web/src/reader/`, the note view imports them from there, and the test's allowed set gains `"mermaid"` only. The component never imports `web/src/theme/*`; it takes "light or dark" as a prop.
   - **No service worker exists.** "KaTeX's way" (spec §2.2) is its lazy `import()` and its `manualChunks` exclusion in `web/vite.config.ts:67-71`: give `mermaid` the same, and check in the build output that neither the `main` nor the `s` entry imports mermaid statically. Mermaid's chunk will pass `chunkSizeWarningLimit` (`vite.config.ts:52`): leave the limit; the warning for that lazy chunk is accepted, say so in the report.
   - **Natural size.** With `useMaxWidth: true` (Mermaid's default) the SVG has `width="100%"` and falls to 300×150 inside an `<img>`. Set `useMaxWidth: false` for every diagram type, or set `width`/`height` from the `viewBox` before making the Blob; a test shows the image's natural width.
   - **Theme.** The app has many schemes; light or dark is `scheme.light` (`web/src/theme/schemes.ts:30`), applied as `data-scheme` (`web/src/theme/apply.ts:25`), with no change event: redraw on a change of `data-scheme` (a `MutationObserver` or the theme store, whichever the app already uses). The reader page sets no scheme: it follows `prefers-color-scheme`.
   - **Reader page under its CSP** (open point 4) passes only if the image shows the right boxes, text and colours **and** the console shows no CSP violation; an image that merely appears isn't enough.
   - **Block jumps and "open at a line" are new mechanisms**, not reuse: today `jump` (`NoteView.tsx:236`) only scrolls, `data-line` is on headings only (`MarkdownView.tsx:398-405`), and nothing reads a line from the URL. Build them: `data-line` on block elements of the note shown (not inside embedded notes), a navigation parameter for a block id and for a line, and the brief highlight. This is within the spec (§4.3, §5.3), not a scope change.
   - **`cat`'s appendix lives in `Shell`, not in `Cat`.** Standing alone is decided in `src/Kumbu.Core/Sh/Shell.cs:134` (`stages.Count == 1 && command is Cat && args.Count == 1`); `SkillsCommand.cs:116` calls `Cat` directly and gets no appendix; `OutlineIfTooLong` (`Cat.cs:113`) measures the note alone, and the appendix is added after it. With `--checkpoint` (`Shell.cs:220`) no appendix, as with `--version`.
   - **Line numbers** in the appendix are 1-based and count in the whole note, also for `cat "<path>#section"`, as `links` prints them (`LinksCommand.cs:86`); the block in spec §3 is an illustration (its "line 9" is line 10 in the example).
   - **`links` and blocks of the same note:** `[[#^id]]` says `this note` when the block exists and `not found: no block ^id in this note` when it doesn't. `#Heading` subpaths print as today, without the subpath.
   - **Moves keep subpaths already** (`WikiLink.WithTarget`, `WikiLink.cs:21`; `MarkdownLink.cs:31`): a test with `#^id` and `%5E` is enough, no code change unless it fails.
   - **No changelog entry:** `CHANGELOG.md` has no "Unreleased" heading. Leave the Welcome notes (`src/Kumbu.Server/Welcome/Notes/`) alone: none lists Markdown features, and their tests are strict.
6. **If time runs short:** stop after the last green gate. Priority: Mermaid in the note view; then block references; then embeds in `cat`; then unlinked mentions; then Mermaid on the reader page; then docs. Whatever is left goes into the report and `open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-12-mermaid-and-parity.md`.
2. It starts with **Task 0: baseline**: run the full gate on `feature/mermaid-parity` before any change and record the exact counts and the exact failures. Known state of `master` at `54e66fd` (the AI link merged; measured by its run on the same tree, CI green):
   - **core:** 2,266 total, 3 skipped, 0 failed
   - **server:** 843 (Api, Mcp, Auth, Net) + 413 (Accounts) + 353 (Sharing) + 20 (Welcome) + 780 (Vaults, Skills, Search, Metrics) + 1,163 (the rest) = 3,572 total, 5 skipped in CI (the model and TensorX tests; locally up to 7), 0 failed
   - **web:** 131 files, 2,361 tests, build green
   - **PDF sidecar:** 9 files, 198 tests, build green

   **Known flaky under load**, each green when its class runs alone: `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault`, `DavShareTests.The_expiry_sweep_retires_expired_shares`, `DavParkingTests.Every_junk_pattern_is_parked_and_never_reaches_mongodb`, `OwnerInactiveTests.Indexing_stops`, `AttachmentPixelsTests`, `DiscreetSocketTests`, `SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once` and other sync-hub tests, the zip tests of `web/src/skillpage/SkillPage.test.tsx`, and now and then `AccountApiTests`, `RekeyTransitionTests`, `PairingTests`. If one fails, run its class alone three times, record the result, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: red test, green, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`).
- Subagent-driven (`superpowers:subagent-driven-development`): one fresh subagent per task, then a spec-compliance review and a code-quality review. Subagents never merge, push or switch branches; you commit and push.
- **A security review** of the finished work by a fresh subagent, before the final gate: it gets the spec's §1 (anti-goals and security boundary), §2.2, §3 and §5.1 and the diff, not your reasoning, and answers whether a diagram's source can run anything in the page or reach the DOM as markup, whether either CSP changed, whether `cat`'s appendix, block lookups or unlinked mentions can read anything outside the note's vault or past the caller's permission, and whether `cat`'s own note text stays byte for byte. Fix what it confirms; record the rest.
- Commit and push after every task. **Commit messages:** imperative and free-form, no Conventional Commits prefix (e.g. "Draw Mermaid diagrams in the note view"), ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended. Stage files by name, never `git add -A`.
- **Mermaid in tests:** jsdom can't lay out SVG, so mock `mermaid` in Vitest where a real render is impossible; test the component's behaviour (image, alt text, Show source, the error line, the theme passed, lazy loading as `web/src/note/MarkdownView.mathLoading.test.tsx` does for KaTeX). If Playwright with Chromium is available, one real render of a small flowchart under the reader page's CSP (served by the test server or a `setContent` page with the policy as a meta tag) settles spec §2.3's question; record the result either way.
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
  pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
  ```
  The server lines together are the whole server suite; start a fresh `kumbu-test-mongo` before each group if one dies. Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence. CI also runs a WebDAV compliance job (`litmus`); this work doesn't touch `src/Kumbu.Server/Dav/`, so check it on the pull request with `gh pr checks` rather than locally.
- **Style:** British English in code, comments, docs, commits and user-visible text. Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New TypeScript and C# files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **Names in the repository:** no employer, customer or workplace in code, docs, tests or examples (use Alice, Bob, Dana, `Home`, `Work`, `Tagebuch`). The owner of the software, Second Circuit – Association for digital freedom of thought, may be named where the spec names it.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. `cat`, `links` and `backlinks` answer exactly as before wherever the spec adds nothing; the note view and the reader page render everything else as before.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture (KaTeX's lazy loading in `web/src/note/math.ts`, the link parsing in `src/Kumbu.Core/Links/`, `SectionLookup`, `NoteLinks`, the backlinks list); no new dependencies. Answers are honest: a fact about the server is said as one, never disguised as "gone" (Chris: honesty is part of the security architecture).
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. Nothing a note's author writes runs in the page; nothing is read outside the note's vault or past the caller's role.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the PR description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version: rollout is something Chris does himself.
- No secrets in logs, assertions or the report.
- Specs outside this topic stay as they are; if one looks wrong, mention it in the report.
- **Security boundary:** the spec's §1. New: Mermaid in the web app and the reader page, the `cat` appendix, block lookups, the unlinked-mentions query and its endpoint. **Not touched:** `Auth/` and `CallerMiddleware`, `Crypto/`, sharing and roles, discreet vaults, publishing's semantics and `FairCopies.cs`, WebDAV, sync, OAuth, both CSPs in `src/Kumbu.Server/Api/Security.cs`. A change that needs more is left out and reported.
- **At the end:** add a row with the pull request's link to `docs/superpowers/open-items.md`, and one row for "Copy link to block" and one for the "Link" button on unlinked mentions (both later, spec §1). Write a hand-test checklist into the report: numbered steps saying where to look and what passes, runnable on staging by Chris or through Playwright, covering the spec's example from end to end (web app in light and dark theme, a phone width of 375 px, the reader page of a published note, and `sh` commands an agent would run).

## Out of scope

- The spec's non-goals (§1), in full.
- The open Dependabot pull requests and all dependency upgrades other than adding `mermaid`.
- Everything else in `docs/superpowers/open-items.md`.

## The report

In this order: push the last task, open the pull request from `feature/mermaid-parity` to `master` as a **draft**, titled "Draw Mermaid diagrams, embed notes in cat, resolve block references and list unlinked mentions", then write the report with its link and the `open-items.md` rows as the last commit, push, and mark the pull request ready. The report is `docs/superpowers/overnighter/2026-11-12-mermaid-and-parity-report.md`. It contains:
- the environment;
- the plan review's findings and what you did with them;
- Task 0, planned against measured;
- for each task the commit and the real test output;
- every mutation individually;
- the security review's findings and what you did with them;
- the decisions made without Chris;
- deviations, surprises and open points for Chris;
- the hand-test checklist;
- the pull request's link.

The pull request's description ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Don't merge it. Never force-push: if something has to come out of a pushed branch, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal, and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
