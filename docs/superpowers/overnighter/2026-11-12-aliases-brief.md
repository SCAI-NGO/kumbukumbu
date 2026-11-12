# Overnighter brief: `aliases` in the front matter

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React/Vite web app, on MongoDB. Many of its users come from Obsidian, whose notes can carry other names in their front matter (`aliases: [Kumbu, K²]`), so that `[[Kumbu]]` leads to `Kumbukumbu.md`. In Kumbu² those links show as "not found" today. This run makes them work everywhere links resolve, and lets unlinked mentions find a note by its aliases too. It is a small, well-bounded piece of work: the last Obsidian parity item of 0.5.0.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, decisions included, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. Spec: `docs/superpowers/specs/2026-11-12-aliases-design.md` (reviewed and approved by Chris; please don't change it)
3. The code it builds on: `src/Kumbu.Core/Links/LinkResolver.cs` (the one resolver), `NoteLinks.cs`, `Mentions.cs`, `WikiLink.cs`, `NoteLink.cs`; `src/Kumbu.Core/Board/Frontmatter.cs` (the YAML reader to reuse); `src/Kumbu.Core/Sh/Commands/LinksCommand.cs`; and every place that constructs a `LinkResolver`: `rg -n "LinkResolver\(" src` (today: `Notes/Replacer.cs:119`, `Notes/NoteMover.cs:317-318`, `Notes/NoteWriter.cs:304, 385, 386`, `Notes/Checkpoints.cs:465-466` (written as `new Links.LinkResolver`, and it calls `NoteMover.Rewrites` at `:455`), `Attachments/AttachmentWriter.cs:222`, `Links/NoteLinks.cs`, `Sh/Commands/LinksCommand.cs:104`, `src/Kumbu.Server/Publishing/Publications.cs:761`)
4. `docs/superpowers/specs/2026-11-12-mermaid-and-parity-design.md` §5 (unlinked mentions, built last night) for how mentions work and are shown

## Where you run

- Branch `feature/aliases`. The session starts on the default branch, so first run `git fetch origin feature/aliases && git checkout feature/aliases`. Everything you push to that branch survives, everything else does not. **Push only to `feature/aliases`.**
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new npm or NuGet package (YamlDotNet is already there).
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`. Node 26, which may have to be installed by hand (an earlier run put Node 26.12.1 in `/opt/node26`). pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout, run `pnpm -C web install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`); earlier runs found `dockerd` not running and started it by hand (`dockerd &`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - The environment has a hard file limit below what Testcontainers' `mongo` asks for. Use a container of your own (commands below). If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
  - The second server group (Accounts, Sharing, Welcome) killed its MongoDB at the 20,000-file limit in earlier runs; run it namespace by namespace, as the gate below does.
  - Parallel agents need a MongoDB each, on their own port. Earlier runs had test runs killed for lack of memory when several agents ran server tests at once: run full server groups one at a time.
- The skipped tests need the real embedding model and a TensorX key; skipping them is expected. Running as root can skip 2 more. Without `KUMBU_TOKENIZER` (below) more tests skip.
- **Never test against a real host**: not staging, not production.
- If superpowers skills are not available, please emulate subagent-driven work with the plain Agent tool and say so in the report.

## Open points for the run

Points 1 to 6 are **decide**: settle them with the rules below and log each in the report. Points 7 and 8 come from the review of spec and brief and are binding. Point 9 is **leave out**.

1. **Where aliases come from for each caller.** Several callers build the resolver from paths only (`ListNotesUnderAsync`); aliases need the notes' content. Where a caller already reads the notes (`ReadNotesUnderAsync`), pass their aliases from that; where it doesn't, read them, or give the resolver an alias source — your choice, but no new stored data, no new collection or index, no cache that can go stale. Parse each note's front matter once per resolver, not once per link. `NoteLinks.OfAsync` runs for every note the web app opens and every `cat`, so **pre-filter cheaply**: call `Frontmatter.Read` only for a note whose front matter block (`Outline.FrontmatterEnd > 0`) contains the text `aliases`; the behaviour is the same, the YAML parser runs rarely.
2. **The shape of the resolver change**: an optional aliases argument to the constructor (keeping today's calls compiling) and a flag on `LinkResolution` (e.g. `ViaAlias`) are a natural fit; the spec only requires the behaviour of §3. Keep the `LinkKind` values as they are, so every `switch` on them stays right.
3. **Name clash with the existing `Alias`.** `WikiLink.Alias` already means a link's display text (`[[x|shown]]`). Name the new concept so the two can't be confused in code (e.g. `NoteAliases`, `aliasesOf`), and say "display text" in comments where the old one is meant if you touch them.
4. **The REST answer:** don't add the alias flag to `LinkView`. The web app draws whatever the server resolves (`web/src/note/wikiLinks.ts:57`), so the API, `schema.d.ts` and the web app stay as they are.
5. **Publications and attachments:** leave `Publications.cs` and `AttachmentWriter.cs` untouched. The publication resolver (`:761`) is used only to find attachments for embeds, fair copies turn note wikilinks into plain text (`FairCopies.cs:841-862`), and an alias never resolves to an attachment, so neither needs aliases. Instead, one test shows that a published note containing `[[Kumbu]]` gives the same fair copy as before. The spec's lines on the reader page then hold trivially; say so in the report.
6. **Smaller settled details:**
   - Only the exact key `aliases` counts; `aliases:` with no value (null) means none.
   - The alias step in `LinkResolver.Resolve` sits after the note lookup and **before** the "looks like a file" fallback (`LinkResolver.cs:107-109`), only for non-exact targets without `/`. `[[Kumbu.md]]` names a file and doesn't match by alias.
   - ` (alias)` appears only in `links` output, after a block subpath too (`… /Work/Projects/Kumbukumbu.md#^id (alias)`); `LinksCommand.Describe` is also used by `Cat.cs:257`, whose output stays as it is.
   - Unlinked mentions: apply the three-character minimum per term, not to the title alone (`NoteLinks.cs:65` returns early today, so a note `K²` with alias `Kumbu` would find nothing). Merge the marks of all terms per line; where marks overlap, keep the longest.
   - README: the one line goes next to "Links resolve like in Obsidian…" (`README.md:45`).
7. **`mv` and `restore` (binding).** `NoteMover.Rewrites` (`NoteMover.cs:313-344`) builds its `after` resolver from paths passed through `map`. Parse the aliases inside `Rewrites` from its `notes` argument (it has the content) and key them through `map` for `after`. Otherwise an alias link resolves to a path that no longer exists and gets rewritten, which is the spec's anti-goal. This also covers `Checkpoints.cs:455`. For the resolvers at `Checkpoints.cs:465-466`, `before` takes aliases from the current notes, and `after` from the restored content where the code has it; where it doesn't, keep it name-only and record that. Spec §3.4's "reported like the name collisions today" means **rewritten as today** by `NewTarget` (`NoteMover.cs:347-371`): the name form if unique, else the vault path. Tests: move a note with aliases into another folder, and archive one whose alias a living note shares.
8. **The creation hint (binding).** It exists only in `NoteWriter.WriteAsync` (`NoteWriter.cs:126`, `NewlyAmbiguousAsync`), that is sh and MCP `write`. The REST write discards the output (`NoteApi.cs:613-624`), and upload has no message field. So the hint goes only there. `before` leaves out the created note **and its aliases** (`NoteWriter.cs:386` removes only the path today). The new "now leads to … (before: … by its alias)" lines form their own `; note: …` clause next to the existing one, with the same cap of `AmbiguousShown` (5).
9. **Leave out, record in `docs/superpowers/open-items.md`** (each a question for Chris):
   - the creation hint for upload and the web app's write (needs new API fields and UI);
   - `rm`'s wording when a removed note's links now lead to another note by alias. Today it says they "now lead nowhere" (`NoteWriter.cs:320-324`), which becomes untrue. Proposal for Chris: "now lead to X by its alias".

   Keep `rm`'s text as it is in this run.
10. **If time runs short:** stop after the last green gate. Priority: resolution in `links`/`backlinks`/REST (§3.1–3.3), `mv` and `restore` keeping alias links (§3.4, point 7), unlinked mentions (§4), the creation hint (§3.4, point 8), the fair-copy test, docs. Whatever is left goes into the report and `open-items.md`.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-12-aliases.md`.
2. It starts with **Task 0: baseline**: run the full gate on `feature/aliases` before any change and record the exact counts and the exact failures. Known state of `master` at `f23d4ae` (Mermaid and parity merged; CI green):
   - measured locally on `f23d4ae` on 2026-11-12 (Debug, `dotnet test --solution Kumbu.slnx`, without `KUMBU_TOKENIZER` and the model): core + server **5,966 total, 5,886 passed, 80 skipped, 0 failed**. The 80 skips are the token-bound tests without the tokenizer plus the model and TensorX tests; with `KUMBU_TOKENIZER` set, as in CI, expect only the 5 to 7 model/TensorX skips.
   - **web:** 141 files, 2,503 tests passed; `pnpm -C web build` green (the chunk-size warning is known and accepted)
   - Mermaid's run (`docs/superpowers/overnighter/2026-11-12-mermaid-and-parity-report.md`) ended with core 2,384 total, 3 skipped; it is the latest per-group reference for the gate below.

   **Known flaky under load**, each green when its class runs alone: `HiddenVaultTests.The_console_shows_nothing_of_a_hidden_vault`, `DavShareTests.The_expiry_sweep_retires_expired_shares`, `DavParkingTests.Every_junk_pattern_is_parked_and_never_reaches_mongodb`, `OwnerInactiveTests.Indexing_stops`, `AttachmentPixelsTests`, `DiscreetSocketTests`, `SyncHubReviewTests.Signing_out_closes_the_tabs_socket_at_once` and other sync-hub tests, the zip tests of `web/src/skillpage/SkillPage.test.tsx`, `web/src/note/commentRanges.test.tsx` under high load, and now and then `AccountApiTests`, `RekeyTransitionTests`, `PairingTests`. If one fails, run its class alone three times, record the result, and carry on.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. The final task repeats: do not merge.
5. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: red test, green, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`).
- Subagent-driven (`superpowers:subagent-driven-development`): one fresh subagent per task, then a spec-compliance review and a code-quality review. Subagents never merge, push or switch branches; you commit and push.
- **A security review** of the finished work by a fresh subagent, before the final gate: it gets the spec's §1 (anti-goals and security boundary) and §3.3 and the diff, not your reasoning, and answers whether an alias can be read from outside the resolving note's vault or past the reader's permission (shared vaults' roles, discreet vaults, publications), whether a link can now lead somewhere other than today without the spec saying so, and whether malformed front matter can throw into a request. That includes exceptions other than `YamlException` (`Frontmatter.cs:34` catches only that one) and the cost of YAML anchor expansion in `Frontmatter`'s `Convert` (`:97-103`), now that it runs on more paths. Fix what it confirms; record the rest, without widening the scope.
- Commit and push after every task. **Commit messages:** imperative and free-form, no Conventional Commits prefix (e.g. "Resolve wikilinks by a note's aliases"), ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended. Stage files by name, never `git add -A`.
- Core tests, one class: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ClassName"`; server tests likewise with `tests/Kumbu.Server.Tests`. Web tests, one file: `pnpm -C web test --run <file>`; all: `pnpm -C web test --run`. **The web's CI gate is `pnpm -C web build`** (it runs the type check).
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
  The server lines together are the whole server suite; start a fresh `kumbu-test-mongo` before each group if one dies. The PDF sidecar (`pdf/`) isn't touched; CI checks it on the pull request. Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence. CI also runs a WebDAV compliance job (`litmus`); this work doesn't touch `src/Kumbu.Server/Dav/`, so check it on the pull request with `gh pr checks` rather than locally.
- **Style:** British English in code, comments, docs, commits and user-visible text. Match the surrounding code's naming, idiom and comment density (the XML doc comments in `src/Kumbu.Core/Links/` explain the rules; extend `LinkResolver`'s summary with the alias step). Comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **Names in the repository:** no employer, customer or workplace in code, docs, tests or examples (use Alice, Bob, Carol, `Home`, `Work`). The owner of the software, Second Circuit – Association for digital freedom of thought, may be named where needed.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. Every link that resolves today resolves to the same place afterwards (spec §3.2 step 1 is unchanged); `links`, `backlinks`, `mv` and the web app answer exactly as before wherever no alias is involved.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture (the one `LinkResolver`, `Frontmatter.Read`, `NoteLinks`, `Mentions`); no new dependencies. Answers are honest: an ambiguity is said as one, a link that changed its target is said, never hidden (Chris: honesty is part of the security architecture).
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. Nothing is read outside the note's vault or past the caller's role; a publication sees only its own notes.
4. **True to the spec.**

If a question can only be answered by changing the spec's scope, don't guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the PR description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please don't merge, tag or deploy, and don't change the version: rollout is something Chris does himself.
- No secrets in logs, assertions or the report.
- Specs outside this topic stay as they are (the Mermaid spec's line on aliases is superseded by this spec, as it says; leave that file alone); if one looks wrong, mention it in the report.
- **Security boundary:** the spec's §1. New: reading `aliases` from front matter inside the resolver's existing note set. **Not touched:** `Auth/` and `CallerMiddleware`, `Crypto/`, sharing and roles, discreet vaults, publishing's semantics and `FairCopies.cs`, WebDAV, sync, OAuth, both CSPs in `src/Kumbu.Server/Api/Security.cs`. A change that needs more is left out and reported.
- **No changelog entry:** `CHANGELOG.md` has no "Unreleased" heading. Leave the Welcome notes (`src/Kumbu.Server/Welcome/Notes/`) alone.
- **At the end:** add a row with the pull request's link to `docs/superpowers/open-items.md`, and one row for "Aliases in the web app's link completion" (later, spec §1). Write a hand-test checklist into the report: numbered steps saying where to look and what passes, runnable on staging by Chris or through Playwright, covering the spec's example from end to end (web app, a phone width of 375 px, the reader page of a published note, and the `sh` commands an agent would run).

## Out of scope

- The spec's non-goals (§1), in full.
- Dependency upgrades of any kind, and the open Dependabot pull requests.
- Everything else in `docs/superpowers/open-items.md`.

## The report

In this order: push the last task, open the pull request from `feature/aliases` to `master` as a **draft**, titled "Resolve links by the aliases in a note's front matter", then write the report with its link and the `open-items.md` rows as the last commit, push, and mark the pull request ready. The report is `docs/superpowers/overnighter/2026-11-12-aliases-report.md`. It contains:
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
