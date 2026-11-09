# Overnighter brief: structural chunking

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React web app, on MongoDB, with a Node sidecar (`pdf/`) for PDF work. This run replaces search's character-based chunking with chunking that follows the Markdown structure. Sections and tables stay whole, length is counted in meaningful words, the model's 512-token limit becomes a hard ceiling instead of a silent cut, PDF pages are split instead of cut off, and `semsearch` shows each hit's whole text. It is the second half of ingesting attachments: what an AI turns from PDFs into notes has to come out again through search. It ships in 0.4.0, planned for Sunday 2026-11-15, so a sound, honest partial result beats a rushed whole one.

Nobody is available to answer questions during this run. That is intended: Chris has reviewed and approved the spec, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-09-structural-chunking-design.md`, reviewed and approved by Chris. Please don't edit it.
4. For context, as needed:
   - `src/Kumbu.Core/Search/Chunker.cs`: today's chunker, which you replace.
   - `src/Kumbu.Core/Search/SectionSearch.cs`: ranking, `Locate` (matches hits to chunks by start line), `WithPages` (best cosine per PDF page), `Exact` (rg grouped by chunk), snippets.
   - `src/Kumbu.Server/Search/SearchIndex.cs`: indexing, `IsCurrent`, hash reuse of vectors, PDF pages as chunks (`:196-216`).
   - `src/Kumbu.Server/Attachments/PdfAnalyses.cs` (`SearchTextAsync`): a PDF's stored page text.
   - `src/Kumbu.Core/Search/UnigramTokenizer.cs`, `src/Kumbu.Server/Search/OnnxEmbedder.cs`, `src/Kumbu.Server/Search/ModelFiles.cs`, and the model download (`rg -n "ModelFiles" src/Kumbu.Server`).
   - `src/Kumbu.Core/Sh/Commands/Semsearch.cs` and `src/Kumbu.Core/Sh/Manual.cs`: the command and the voice of help texts.
   - `src/Kumbu.Core/Sections/Outline.cs` and `src/Kumbu.Core/Sh/Lines.cs`: headings, frontmatter and fenced code already parsed.
   - `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs:559-636` (`SearchAsync`): where stored embeddings are read, `Pending` is counted (`:608`, `:619`, by `e.Version != note.Version`), PDF pages are dropped by version (`:628`) and `SemanticHit`s are built (`:620`, `:635`).
   - `src/Kumbu.Core/Sh/OutputLimit.cs` (24,000 characters, applied in `Shell.cs:151`) and `PdfCommand`'s `Reserve`: the output budget `semsearch` must respect.
   - `web/src/console/paths.ts:64-72` (the console's semsearch entry and its options that take a value) and `web/src/console/console-fixture.json`, checked by `tests/Kumbu.Server.Tests/Api/ConsoleFixtureTests.cs`.
   - `docs/concept.md` §6: search as designed.

## Where you run

- Branch `feature/structural-chunking`. The session starts on the default branch, so first run `git fetch origin feature/structural-chunking && git checkout feature/structural-chunking`. Everything you push to that branch survives, everything else does not.
- **The branch is based on `ingest-attachments`** (pull request #176, not merged yet: the cherry-pick of ingesting attachments onto `master`). Build on what is there. Don't merge `master` or `ingest-attachments` into the branch. If they move during the run, the pull request will show it.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new NuGet or npm package.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward). `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`. Node 26, which may have to be installed by hand. pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout, first run `pnpm -C web install --frozen-lockfile` and `pnpm -C pdf install --frozen-lockfile`: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", check whether the setup script's daemon is still running, and start it again if you can.
  - If Docker can't work at all, use an external replica-set MongoDB: install `mongod`, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` (see `ExternalMongo` in `TestImages.cs`).
  - Earlier runs met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own, as a replica set:
    ```sh
    docker rm -fv kumbu-test-mongo
    docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 127.0.0.1:27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all
    for i in $(seq 1 30); do docker exec kumbu-test-mongo mongosh --quiet --eval 'try { rs.status().ok } catch (e) { rs.initiate({_id: "rs0", members: [{_id: 0, host: "127.0.0.1:27017"}]}).ok }' && break; sleep 1; done
    export KUMBU_TEST_MONGO='mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true'
    ```
    Check the image tag against `tests/Kumbu.Server.Tests/TestImages.cs` and use the one it names.
  - Give every full run a fresh container, with `docker rm -fv kumbu-test-mongo` first: mongod runs out of file handles over many runs, and without `-v` anonymous volumes fill the disk.
  - If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- **Network access to huggingface.co** is needed once, to fetch the tokenizer for the tests (spec §3.2). If it is blocked, note it in the report, build the download with its pinned hash anyway, and let the token-ceiling tests skip with a clear reason instead of failing. Chris checks them on his machine.
- The 4 skipped tests need the real embedding model and a TensorX key. Neither is available here, and skipping them is expected. Running as root can skip 2 more.
- If superpowers skills are not available, emulate subagent-driven development with the plain Agent tool and say so in the report.

## Open points for the run

Settled by Chris on 2026-11-09 and written into the spec: when the tokenizer is downloaded (only with an embedding provider active; tests and CI use a pre-fetched file through an environment variable, test hosts never download; `tests/Kumbu.Server.Tests/KumbuAppFactory.cs:64` turns search off for all app-factory tests), and how `semsearch` fits into the output limit (spec §3.7). The rest are **decide**: settle them with the rules below and log each in the report. Where a reading is given, it is the recommended one.

1. **Which tokenizer bounds the chunks.** arctic-m and arctic-l ship different `tokenizer.json` files (`ModelFiles.cs:26,31`, different SHA-256). Recommended: chunk boundaries always use **arctic-m's** tokenizer (`Snowflake/snowflake-arctic-embed-m-v2.0` at the pinned revision, `tokenizer.json`, 17,083,009 bytes, SHA-256 `f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec`), whatever model or provider embeds. Then the boundaries don't change when the admin switches models. Reuse the file if the arctic-m model download already has it.
2. **How the tokenizer reaches Core.** `Chunker` lives in `Kumbu.Core` and runs at search time too (`SectionSearch.RankAsync`, `Exact`, `EarlierSearch`, `NoteApi`, `Semsearch`). Give it the bound through a small seam (a token counter passed in or set once at start-up), and keep every caller on the same bound as indexing, so `Locate` still finds the chunk by start line.
3. **The fallback bound** while the tokenizer is missing (spec §3.2): pick limits from measurements with the real tokenizer on dense fixtures (German prose, a numeric table, code, a long URL list). Write the measured worst case into the code's comment. `UnigramTokenizer.Encode` (`:111`) truncates at `maxLength`, so add a count that doesn't truncate. The indexer waits for the tokenizer and uses the fallback only after a failed download (spec §3.2).
3a. **Search-time cost.** `SectionSearch.RankAsync` (`:59`), `Exact` and `EarlierSearch` chunk every note in scope per query. Tokenise only where a chunk's word or character count comes near the bound, and measure a search over 1,000 notes before and after. Put the numbers in the report.
4. **Scheme version.** Add it next to the note's version in `NoteEmbeddings` (records at `UnlockedVault.cs:642` `EmbeddingsPayload`, `:645` `EmbeddedChunk`, `:649` `NoteEmbeddings`; sealing at `:265-280`), so that `IsCurrent` sees it and `EncryptedNoteStore.SearchAsync` counts notes of an older scheme as **pending**: the footer's count must go down as re-indexing proceeds. Embeddings stored before this change count as stale and are re-chunked. Hash reuse (`SearchIndex.cs:237`) keeps unchanged texts' vectors. PDFs of an older scheme **stay searchable by page** until re-indexed ("breaks nothing"), and they must not vanish silently from search. README: one line saying that after the update, vaults on an external provider send their text once more.
5. **PDF chunk key.** One splitter in Core splits a page's text, used by indexing and by `semsearch`'s display, on the same uncut text, so the core shown is the one that matched. `SearchTextAsync`'s `maxChars` cut (`SearchIndex.cs:205`) goes. `EmbeddedChunk.StartLine` holds the page number today, and `SemanticHit(Path, StartLine)` is all a hit carries. Choose an encoding of page plus position that keeps old data readable or re-indexes it through the scheme version. Prefer extending the stored record over overloading `StartLine` arithmetically, if it stays simple.
6. **Merged chunks and `cat`.** A merged chunk's address is the deepest shared heading (spec §4). Check what `semsearch`'s footer and help tell the AI to do with it, so `cat "path#Address"` or the line range leads to the right text.
7. **`rg`** (`SectionSearch.Exact`, spec §6): if grouping by the new chunks makes `rg`'s output worse for a single match, keep `rg` on plain sections and say so.
8. **The web app's search** uses the same `SectionSearch` (`rg -n "SectionSearch" src/Kumbu.Server/Api`). Its result list must keep working. It does not need the rolls. Any change to its API types goes into `web/src/api/schema.d.ts` by hand.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-09-structural-chunking.md`.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures, against the known state below.
   Known state on `ingest-attachments` at `9e919a7` (the base of this branch, which adds only docs), measured on Chris' machine on 2026-11-09:
   - **.NET** (`dotnet test --solution Kumbu.slnx`): total 5333, succeeded 5327, failed 2, skipped 4. Server alone: total 3339, failed 2, skipped 2.
   - The 2 failures are known flaky tests, also listed in `docs/superpowers/open-items.md` (row of 2026-11-07): `PairingTests.A_device_name_gets_the_same_answer_for_an_open_and_an_unknown_code` and `MembershipApiTests.Last_use_is_the_use_on_this_vault_not_the_keys_global_one`. `AttachmentPixelsTests.A_stored_analysis_and_its_text_are_read_without_the_sidecar` is a third one. Chris is fixing them on #176 in parallel: **don't fix them here**. If one fails, run its class alone three times, record the result, and carry on.
   - **CI** on #176: every job green (build, web app, PDF sidecar, WebDAV litmus, both images). Web and pdf suites were not measured locally.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. **Order by value**, so a short run still leaves something whole:
   1. tokenizer download plus the token bound and its fallback, with the CI step (spec §3.2);
   2. the structural chunker for notes: words, blocks, merging, tables, the token ceiling (spec §3.1-3.3, §3.5);
   3. scheme version and re-indexing;
   4. PDF pages split by words, with the new chunk key (spec §3.6);
   5. rolls and the `semsearch` output: default, `-C`, `-l`, help (spec §3.4, §3.7);
   6. the end-to-end tests, README and help texts.

   Put a phase gate after each.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push.
- Commit and push after every task. Commit messages imperative and free-form ("Count chunk lengths in words"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- The test filter is Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`, repeatable. `FullyQualifiedName~` doesn't work here, and a glob in mid-name selects nothing. Core: `--project tests/Kumbu.Core.Tests`. Web: `pnpm -C web test --run <file>`.
- Full verification at each phase gate, as CI does (`.github/workflows/ci.yml`): `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`. The litmus job is optional here, since WebDAV isn't touched.
- **The web's CI gate is `pnpm -C web build`:** it runs the type check, which `pnpm test` does not.
- **CI:** the new tokenizer step goes into `.github/workflows/ci.yml` with `actions/cache`, keyed by the SHA-256, and verifies the hash after the download. The tests find the file through an environment variable you choose and document.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **Test PDFs are generated, never committed**: the sidecar's tests build them with pdf-lib in `pdf/test/fixtures.ts`. The C# side uses the existing fakes (`FakePixels` in `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs`, `FakePdfSidecar` in `tests/Kumbu.Server.Tests/Attachments/`). The end-to-end test with the real model follows the existing skip pattern (`SearchIndexTests.The_real_model_ranks_by_meaning`, `KUMBU_MODEL_DIR`).
- **Fixtures for chunking** live as strings or files in the test project. Include a long German section, a table of over 200 rows (error codes and descriptions, no real product's list), nested headings with many short sections, fenced code, and a single line of 10,000 characters.
- **Style:** British English in code, comments, docs, commits (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples, test data. Use Alice, Bob, `Work`, a pump manual, wild flowers from the garden.
- The texts an AI reads (`semsearch`'s help and footer) are short, literal and in the voice of `src/Kumbu.Core/Sh/Manual.cs`. Read it before writing. If `semsearch`'s usage line changes, update the tests that assert it (`HelpTests`, `rg -n "semsearch \[" tests`) on purpose, and keep `web/src/console/paths.ts` and `console-fixture.json` in step (`-C` and `-l` take no value).
- If the run gets short, stop at the last green phase gate rather than half-way through a task, push, and say in the report which tasks are left.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. Stored embeddings either stay usable or are re-indexed through the scheme version, never silently wrong. `rg`'s results and the web search keep working.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture: search lives in `Kumbu.Core/Search`, indexing in `Kumbu.Server/Search`, model files are pinned by size and SHA-256 like `ModelFiles`; no new dependencies.
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. The tokenizer is a downloaded file: verify its hash before use, exactly as the model files are.
4. **True to the spec.** Chris' guiding wish: **nothing that is in a note or on a page may become unreachable by search** without the user being told. Where a choice trades completeness against size, lean to completeness within the token ceiling.

If a question can only be answered by changing the spec's scope, do not guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the pull request description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself. One draft pull request is the result.
- No secrets in logs, assertions or the report. Note contents never reach a log either.
- **Security boundary: not touched, apart from one download.** Not touched: encryption (embeddings stay sealed as today), accounts, sessions, sharing and roles, OAuth, pairing, WebDAV, Obsidian sync, the export, publishing, `CallerMiddleware`, the sidecar. The one exception is the new tokenizer download, which follows `ModelFiles`' pinning and verification exactly. Say in the pull request that this is the only new outside access.
- At the end: a row with the pull request's link in `docs/superpowers/open-items.md`, and in the report a hand-test checklist with numbered steps, where to look and what passes. Include: a long ingested note with a big table searched by a row near its end, a dense PDF page searched by its last paragraph, `semsearch` with and without `-C` and `-l`, re-indexing after the update (the footer's pending count going down), and a server with `KUMBU_SEARCH=false` and an external provider getting the tokenizer. Don't change anything outside the plan's files.

## Out of scope

- Everything spec §2 lists as a non-goal: another embedding model, another ranking, chunking by sentences or topics, one chunk per table row, PDF chunks across pages.
- The three flaky tests (Chris fixes them on #176).
- A separate repository for downloaded files (Chris decides that after 0.5.0).
- The Obsidian plugin (another repository).
- Merging `master` or `ingest-attachments` into the branch.

## The report

First open a **draft** pull request with base **`master`**, titled "Structural chunking: sections that stay whole, tables included". Its body:

- says it depends on #176 and shows #176's commit until that is merged;
- lists the decisions made without Chris;
- names the tokenizer download as the only new outside access;
- ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

Then, as the last commit, write `docs/superpowers/overnighter/2026-11-09-structural-chunking-report.md`, with:

- the environment
- the plan review findings and what you did with them
- Task 0 planned vs. measured
- for each task the commit and the real test output
- every mutation individually
- the decisions made without Chris
- the measured token counts behind the thresholds and the fallback
- deviations, surprises, tasks left if any, open points for Chris
- the hand-test checklist
- the pull request's link, together with a row linking it in `docs/superpowers/open-items.md`

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
