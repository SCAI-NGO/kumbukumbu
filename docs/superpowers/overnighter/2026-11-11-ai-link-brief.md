# Overnighter brief: AI links for published notes

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. Its owners can publish a note behind a secret link, and more and more readers read through an AI, whose fetch tool can't open that link (the code is in the fragment, and the page needs scripts). This run builds a short-lived link a reader makes on the reader page, which an AI fetches as plain Markdown, and, beside it, an honest answer for skill link copies while publishing is switched off. It is the first feature of 0.5.0.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, decisions included, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository's root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-11-ai-link-design.md` (reviewed and approved by Chris; please don't change it)
4. The publish design it amends: `docs/superpowers/specs/2026-11-05-publish-design.md`, above all §2, §3.12, §3.13 and §4
5. The code it builds on: `src/Kumbu.Server/Publishing/` (`ReaderApi.cs`, `PublicationStore.cs`, `Publications.cs`, `ShareCode.cs`), `AccountLimits` in `src/Kumbu.Server/Api/Security.cs`, `web/src/reader/` and `web/s.html`; for §5 `src/Kumbu.Server/Skills/SkillUpdates.cs` and `SkillUpdateTaker.cs`
6. `docs/security-architecture.md`: the promises the design keeps, and the places §6 of the spec names

## Where you run

- Branch `feature/ai-link`. The session starts on the default branch, so first run `git fetch origin feature/ai-link && git checkout feature/ai-link`. Everything you push to that branch survives, everything else does not. **Push only to `feature/ai-link`.**
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`. Node 26, which may have to be installed by hand (an earlier run put Node 26.12.1 in `/opt/node26`). pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout, run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - The environment has a hard file limit below what Testcontainers' `mongo` asks for. Use a container of your own (commands below). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
  - The second server group (Accounts, Sharing, Welcome) killed its MongoDB at the 20,000-file limit in earlier runs; run it namespace by namespace, as the gate below does.
  - Parallel agents need a MongoDB each, on their own port. Earlier runs had test runs killed for lack of memory when several agents ran server tests at once: run full server groups one at a time.
- The 4 skipped tests need the real embedding model and a TensorX key; skipping them is expected. Running as root can skip 2 more. Without `KUMBU_TOKENIZER` (below) more tests skip.
- **Never test against a real host**: not staging, not production. Chromium for Playwright may load `setContent` pages with network requests aborted.
- If superpowers skills are not available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## Open points for the run

All **decide**: settle them with the rules below and log each in the report.

1. **Where the token documents live** and how the new collection is wired into vault and account deletion (`ContentCollections` in `src/Kumbu.Server/Vaults/VaultCatalog.cs`) and into the hooks where a publication ends (`DeleteCopyAsync`, `DeleteCopiesOfVaultsAsync` in `PublicationStore.cs`). The spec leaves it to you; whatever you choose, a test must show that every way a publication ends kills its tokens.
2. **The owner check.** A token must die with the owner's deactivation. The reader endpoint checks the owner's state when a code is opened; the token needs the same check on every fetch. How the owner is found (sealed beside the key, or through the publication document) is yours; the vault id is already in the clear on publication documents (publish design §4), the owner must not become so.
3. **`/s/t/` and the reader paths.** Today `/s/t/…` falls through to `index.html`. Add it to `ReaderPaths` (reader headers, left out of the HTTP metrics) and make sure the SPA fallback never serves it.
4. **The L-02 state in the Skills screen.** How "publishing is switched off" appears in `SkillUpdateState` and the web is yours, within the spec's §5 sentence.
5. **Settled before the start** (from the review of spec and brief; these are binding, not open):
   - **Images in the Markdown.** "The filtered Markdown exactly as stored" means: byte for byte, except image references. The stored copy names its images `f/<n>` (`FairCopies.FilePrefix`, `src/Kumbu.Core/Publishing/FairCopies.cs:23`). Only an `f/<n>` whose index is in the copy's files is rewritten, to `<base>/s/t/<token>/files/<n>`, with its media type beside it. A reference to an index not in the copy is left out, as `web/src/reader/Reader.tsx:48` leaves it out. Build `<base>` with `Publications.BaseUrl(http, options)` (`Publications.cs:86`), never by hand.
   - **Own secrets, own space.** The token gets HKDF labels of its own (e.g. `kumbu/v1/ai-link-lookup`, `kumbu/v1/ai-link-key`), never `ShareCode`'s, and its documents live in a collection of their own, not in `publications`. Test: a share code in `/s/t/` and a token in `POST /api/s` open nothing.
   - **Expiry in code.** The TTL index only tidies up (as `VaultCatalog.cs:585` does it). Every fetch checks `expiresAt` against the injected `TimeProvider` in the same atomic update that increments the count (filter `expiresAt > now` and `count < ceiling`). Tests use `FakeTimeProvider`, already referenced (`tests/Kumbu.Server.Tests/Kumbu.Server.Tests.csproj:15`; examples `Api/ChangesetIdleTests.cs`, `Skills/SkillUpdateCheckTests.cs`).
   - **Not counted against the owner's storage.** `ContentCollections` also feeds the storage figures (`Admin.BytesByVaultAsync`, `Accounts/Admin.cs:210-230`). Anonymous readers make token documents, so they must never count towards the owner's quota: delete the token collection with the vault (a `vault` field, deleted beside `ContentCollections` in vault and account deletion), but keep it out of the storage figures. Test: making tokens doesn't change the owner's storage figure.
   - **No SPA fallback under `/s/t/`.** A `MapGet` doesn't match HEAD, so `MapFallbackToFile` (`Program.cs:351`) would answer HEAD or POST with `index.html`. Add `MapFallback("/s/t/{**path}", …)` with the plain-text 404 sentence, as `Program.cs:348` does for `/assets/`. HEAD answers as GET does, without a body, and counts against the ceiling like a GET. Test GET, HEAD and POST on a valid and an unknown token.
   - **L-02's state.** In `SkillUpdates.cs:159-170` a non-null state counts as checked, and null is left out of what `CheckAsync` returns, so neither fits. Add a state value of its own (e.g. `publishing-off`), excluded from `checkedPaths` the way null is, so the stored update state stays, and render the spec's sentence for it in `web/src/skills/Installed.tsx`. Review and Take refuse as `MarketplaceApi.SwitchedOff()` does (`MarketplaceApi.cs:52`, 404 with a type of its own), with the spec's sentence; reuse the type `publishing-off` (`ReaderApi.OffType`), since it is the same fact about the server.
   - **The `/s/t/` answers.** Publishing off: `404`, plain text, the off sentence. A network that `ReadersBlocked` blocks (for wrong share codes) is refused at `/s/t/` too, as `OpenAsync` refuses it (`ReaderApi.cs:113-114`), with the plain-text 404. The ceiling's "plus 5 per image" uses the copy served at the moment of the request (so it follows an Update).
   - **Making a token:** `POST /api/s/ai-links` under the cookie's path (`ReaderApi.CookiePath`), with `X-Kumbu-Copy`, checked as `FileAsync` checks it; the answer is JSON with the link and its expiry, `Cache-Control: no-store`.
   - **Logs.** The hosting log line that would carry the path ("Request starting …") is kept out only by `builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning)` (`Program.cs:42`). Don't bypass it; the log-capture test runs with the app's real logging filters, as `tests/Kumbu.Server.Tests/Publishing/ReaderApiTests.cs:61-80` and `:703` do with `CapturedLog`. security-architecture §7 adds that an operator who lowers that level gets the token in the server's log, beside a proxy's access log.
   - **The owner check** on every fetch means an owner reactivated within the hour revives the token, as it revives the ordinary link. That is intended: no delete-on-deactivation hook in `Accounts/`.
   - **What is in the clear** (spec §6, security-architecture §5.6) for the token documents: lookup id, `vault`, publication, expiry, count.
6. **If time runs short:** stop after the last green gate. Priority: the server side of the AI link with its tests and docs; then the web button; then §5 (L-02); then the amendments in other docs. Whatever is left goes into the report and `open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-11-ai-link.md`.
2. It starts with **Task 0: baseline**: run the full gate on `feature/ai-link` before any change and record the exact counts and the exact failures. Known state of `master` at `c42732c` (0.4.0 and its release notes) (CI green there):
   - **core:** 2,266 total, 3 skipped, 0 failed
   - **server:** 843 (Api, Mcp, Auth, Net) + 786 (Accounts, Sharing, Welcome together; 413 + 353 + 20 in the last split run) + 777 (Vaults, Skills, Search, Metrics) + 1,143 (the rest) = 3,549 total, 5 skipped in CI (the model and TensorX tests; locally up to 7), 0 failed
   - **web:** 130 files, 2,355 tests, build green
   - **PDF sidecar:** 9 files, 198 tests, build green

   **Known flaky under load**, each green when its class runs alone: `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault`, `DavShareTests.The_expiry_sweep_retires_expired_shares`, `DavParkingTests.Every_junk_pattern_is_parked_and_never_reaches_mongodb`, `OwnerInactiveTests.Indexing_stops`, `SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once` and other sync-hub tests, the zip tests of `web/src/skillpage/SkillPage.test.tsx`, and now and then `AccountApiTests`, `RekeyTransitionTests`, `PairingTests`. If one fails, run its class alone three times, record the result, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: red test, green, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`).
- Subagent-driven (`superpowers:subagent-driven-development`): one fresh subagent per task, then a spec-compliance review and a code-quality review. Subagents never merge, push or switch branches; you commit and push.
- **A security review** of the finished server side by a fresh subagent, before the final gate: it gets the spec's §2 anti-goals, §3.4, §3.5, §5c and the diff, not your reasoning, and answers whether a token can open more than its copy, outlive its publication, owner or hour, reach a log or metric, or let the reader page into the web app. Fix what it confirms; record the rest.
- Commit and push after every task. **Commit messages:** imperative and free-form, no Conventional Commits prefix (e.g. "Let a reader make a link an AI can fetch"), ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended. Stage files by name, never `git add -A`.
- **Timing and clock tests** use the server's existing time abstraction where there is one (look for `TimeProvider` in the publishing and account code), never real sleeps of an hour.
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

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. The ordinary link and the reader page behave exactly as before, apart from the new button.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture (the reader endpoint, `ShareCode`, `AccountLimits`, the sealed documents); no new dependencies. Answers are honest: a fact about the server is said as one, never disguised as "gone" (Chris: honesty is part of the security architecture).
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. A token opens one copy and its images, nothing else; it never reaches a log or a metric label; nothing about readers is stored beyond what the spec names.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the PR description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version: rollout is something Chris does himself.
- No secrets in logs, assertions or the report. Tokens and codes made by test fixtures are fine.
- Specs outside this topic stay as they are, apart from the amendments the spec's §6 names in the publish design and the skills design §3.10; if another looks wrong, mention it in the report.
- **Security boundary:** the spec's §5c. New: the `/s/t/` endpoints, the token documents and their derivation, the request that makes a token. **Not touched:** `Auth/` and `CallerMiddleware`, `Crypto/` and the slot model, `ShareCode`'s derivation and the reader cookie, the publication documents' format, `Kumbu.Core/Publishing/FairCopies.cs`, WebDAV, OAuth, the MCP surface. A change that needs more is left out and reported.
- **At the end:** in `docs/superpowers/open-items.md`, mark the row "Security audit of 0.4.0, L-02, for Chris" (2026-11-11) as settled by this work, and add a row with the pull request's link. Write a hand-test checklist into the report: numbered steps saying where to look and what passes, runnable on staging by Chris or through Playwright with one browser context per test user (the owner and a reader), and one step with `curl` as a stand-in for an AI's fetch tool.

## Out of scope

- The spec's §5b, in full.
- The open Dependabot pull requests and all dependency upgrades.
- Everything else in `docs/superpowers/open-items.md`.

## The report

In this order: push the last task, open the pull request from `feature/ai-link` to `master` as a **draft**, titled "Let readers make a link an AI can fetch", then write the report with its link and the `open-items.md` rows as the last commit, push, and mark the pull request ready. The report is `docs/superpowers/overnighter/2026-11-11-ai-link-report.md`. It contains:
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
