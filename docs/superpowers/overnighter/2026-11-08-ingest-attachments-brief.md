# Overnighter brief: ingesting attachments

Hi, thank you for taking this one on! Kumbu² is a Markdown second brain served over MCP, written in C# (.NET 10) with a React web app, on MongoDB, with a Node sidecar (`pdf/`) for all PDF and image work. Tonight's work lets a user's AI turn PDFs and images into proper notes without losing anything: a new `sh` command `pdfimages` that stores a PDF's pictures (embedded images and rendered regions such as vector diagrams) as image attachments, a per-vault **archive** (`/<vault>/_archive/`, the `archive` command, `--archive` options, an Archive view in the web app) that keeps a source out of the way after ingesting, and a system skill `ingest-attachments` that ties both together. Chris calls this one of the game changers Kumbu² needs, so care counts for more than speed, and an honest partial result is a good result.

Nobody is available to answer questions during this run. That is intended: the spec has been reviewed by Chris, and everything after it is yours, from the plan to the pull request. The decision rules below tell you how to settle what the spec leaves open.

## What to read, in this order

1. This brief
2. `CLAUDE.md` in the repository root, if your session didn't load it (everything you need from it is repeated here)
3. Spec: `docs/superpowers/specs/2026-11-08-ingest-attachments-design.md`, reviewed and approved by Chris. Please don't edit it.
4. For context, as needed:
   - `docs/superpowers/specs/2026-11-08-skills-design.md`, §3.11: system skills, which this branch builds on. The existing system skill `src/Kumbu.Core/Skills/System/create-skill/SKILL.md` shows the voice and length a skill text should have.
   - `src/Kumbu.Core/Sh/Commands/PdfCommand.cs` and `pdf/src/` (`analyse.ts`, `classify.ts`, `render.ts`, `encode.ts`, `pdfjs.ts`, `server.ts`): how PDFs are read, how regions are found, how pages and crops are rendered and encoded today.
   - `src/Kumbu.Core/Attachments/AttachmentWriter.cs`, `src/Kumbu.Core/Sh/Commands/Mv.cs`, `src/Kumbu.Core/Attachments/FolderMarker.cs`: the writer every stored file goes through, the mover `archive` builds on, and the precedent for leaving a reserved name out of listings.
   - `README.md`

## Where you run

- Branch `feature/ingest-attachments`. The session starts on the default branch, so first run `git fetch origin feature/ingest-attachments && git checkout feature/ingest-attachments`. Everything you push to that branch survives, everything else does not.
- **The branch is based on `feature/skills`** (draft pull request #153, itself based on `feature/webdav`, #152; neither is merged: they wait for the 0.3.2 release). Build on what is there: system skills, the quota (every write passes it), `df`/`du`, WebDAV. Don't merge `master`, `feature/skills` or `feature/webdav` into the branch; if they move during the night, the pull request will show it.
- The cloud environment may lack tools. Installing what you need (SDKs, CLIs, packages) is fine; please do it without asking and note it in the report. **Adding a dependency to the project is not**: no new NuGet or npm package. pdf.js (`pdfjs-dist`) and `@napi-rs/canvas` in `pdf/` can decode embedded images and encode PNG and JPEG; `pdf-lib` is a devDependency of `pdf/` for generating test PDFs only.
- Task 0 (setup plus first build and tests) should take about **15 minutes**. If it takes much longer, something is off: please note it and carry on if you can.
- .NET 10 SDK as `global.json` says (roll forward); `DOTNET_CLI_TELEMETRY_OPTOUT` should be `1`; Node 26, which may have to be installed by hand; pnpm 10.32.1 (`npm install -g pnpm@10.32.1` if missing). After any pull or checkout run `pnpm -C web install --frozen-lockfile` and `pnpm -C pdf install --frozen-lockfile` first: stale `node_modules` make tests and build fail.
- **Docker** test containers are available and allowed, set up by the environment's setup script (a `dockerd` with `socat`). The server tests start MongoDB through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs`).
  - If `docker info` says "Cannot connect", please check whether the setup script's daemon is still running, and start it again if you can.
  - If Docker can't work at all, use an external replica-set MongoDB: install `mongod`, start it as a single-node replica set, and set `KUMBU_TEST_MONGO` (see `ExternalMongo` in `TestImages.cs`).
  - Earlier runs met a hard file limit below what Testcontainers' `mongo` asks for. What worked was a container of your own with `--ulimit nofile=20000:20000`, named `kumbu-test-mongo`, as a replica set, and `KUMBU_TEST_MONGO` pointing at it.
  - Give every full run a fresh container, with `docker rm -fv kumbu-test-mongo` first: mongod runs out of file handles over many runs, and without `-v` anonymous volumes fill the disk.
  - If Ryuk gets in the way, `TESTCONTAINERS_RYUK_DISABLED=true` is fine.
- The 4 skipped tests need the real embedding model and a TensorX key. Neither is available here, and skipping them is expected. Running as root can skip 2 more.
- If superpowers skills are not available, please emulate subagent-driven development with the plain Agent tool and say so in the report.

## Open points for the run

An independent review of spec and brief against the code found these. Five were spec changes and Chris settled them in the spec (the 8 MP cap and one extraction worker, `_archive` skipped in the vault Skills, no archiving of skills, links preferring what is outside the archive, deleting archived folders from the web). The rest are **decide**: settle them with the rules below and log each in the report. Where a reading is given, it is the recommended one.

1. **Corrected anchors.** `measureDrawing` is `pdf/src/analyse.ts:112-186` (image operations `:159-182`), `MAX_IMAGE_PIXELS` is `pdf/src/pdfjs.ts:14` (passed at `:45`, `verbosity: 0` at `:36`), the interface is `IAttachmentPixels.cs:85-103`. The spec now uses these; trust the code over any line number.
2. **Decoded images in Node.** After `getOperatorList`, `page.objs.get(id)` (`commonObjs` for document-wide ids) holds `{width, height, kind, data}`, not a bitmap (no `OffscreenCanvas` in Node). Convert each `kind` (1-bit grey packed, RGB, RGBA) to `ImageData`. Stencil masks (`paintImageMaskXObject`) are stored rendered in their fill colour on transparent; `paintSolidColorImageMask` is never an image (though `analyse.ts:162` counts it); images in annotations stay out, as in the analysis (`AnnotationMode.DISABLE`, `analyse.ts:82`). PNG keeps alpha: no flattening onto white.
3. **Orientation.** Apply the CTM's quarter turns and flips (`analyse.ts:123`) so an image looks as it does on the page; report the size after turning.
4. **Over 50 MP.** pdf.js drops such an image with only a warning. For the extraction routes, turn warnings on and count that warning per page for the output line the spec gives; if that proves unreliable, say in the help that such images are left out and `pdf --crop` shows them.
5. **Time and cost.** `RENDER_MS` is 10 s (`pdf/src/server.ts:86`) and pdf.js decodes every image of a page to build its operator list. List one page per request (or a limit growing per page like `analysisLimit`, `server.ts:90`); avoid decoding a page again for each of its images where you can (e.g. one request returns the next image of a page in turn, or a page's images one after another in one worker). Give the command a wall-clock budget like `rg`'s regions (`src/Kumbu.Core/Sh/Commands/Rg.cs:33`), stopping cleanly with "stored …; go on with pages N-". Treat `busy` from the sidecar (`PdfSidecar.cs:56`) with a short retry.
6. **One worker.** Extraction runs one at a time in the sidecar (a queue or a dedicated worker in `pdf/src/pool.ts`), so two calls never decode 50 MP images in parallel; `pdf` and `img` keep their pool.
7. **Crop targets.** `.jpg`/`.jpeg` target: JPEG at 90 down to 60; `.png` target that would have to become JPEG: refused, as the spec says; a folder target or a page range with `--crop`: refused with the usage line. `--rotate` only with `--crop`.
8. **Parsing.** Paths start with `/`, pages are `N`, `N-M` or `N-` as in `pdf` (`PdfCommand.cs:38`). The target folder must end in `/` (as `mv`, `Mv.cs:61-63`), otherwise the usage line. `--list` and `--all` may stand anywhere (`Options.TryParse`).
9. **Numbering.** `n` counts every image painted on the page in painting order, decorations and duplicates included, so names don't change with `--all`; a duplicate is named after its first page.
10. **The 500 cap** counts after leaving out decoration and duplicates and is checked before writing anything, naming a page range that fits.
11. **Quota midway.** `QuotaExceededException` is thrown and would replace the whole output in `ToolResult.GuardAsync` (`src/Kumbu.Server/Mcp/ToolResult.cs:94-98`). `pdfimages` catches `Kumbu.Core.Notes.QuotaExceededException` itself, prints the partial list and the quota sentence. Errors from `AttachmentWriter` (prefixed `upload:`, `AttachmentWriter.cs:20`) are quoted after `pdfimages:`.
12. **The web tree** filters in `TreeAsync` (`src/Kumbu.Server/Api/NoteApi.cs:413-419`), not in `TreeOf`, which checkpoint trees share. Gallery (`GalleryPage.tsx:122`) and link completion (`LinkCompletion.tsx:17`) follow the tree. Web tags and the backlinks panel stay unchanged.
13. **Checkpoints.** A checkpoint captures and restores the archive like any folder; readers under `--checkpoint` hide it the same way and take `--archive` too.
14. **`rg`** filters the PDFs below the path (`Rg.cs:193`), so the scan hint (`Rg.cs:237,244-253`) and the name hint both leave the archive out.
15. **`semsearch`**: filter the note list and the PDF page hits in `WithPages` (`SectionSearch.cs:176-188`); there is no top-K to refill (`:63` asks with `int.MaxValue`). The web search needs only the notes filtered plus a new query parameter for **Include archive** (`schema.d.ts` by hand).
16. **`ls`, `tags`, `du`** take no options today (`CommandArgs.CheckOptions`, `Ls.cs:35`, `TagsCommand.cs:38`). Introducing them changes usage lines that `ShellTests.cs:139`, `TagsCommandTests.cs:108` and `DfTests.cs:152-153` assert: update those tests on purpose and say so.
17. **`backlinks`** on a target inside `_archive/` lists archived sources too.
18. **Timestamp placement**: before the format's extension from `AttachmentFormat` (`v1.2 notes 2026-11-08 213005.md`, `.jpeg` stays `.jpeg`). Spaces are allowed, `HHmmss` avoids `:` (`NoteNames.cs:25-45`).
19. **Time zone.** `compose.yaml` passes no `TZ` to the `kumbu` service: add `TZ: ${TZ:-UTC}` there and to `.env.example` and the README. Check whether the `aspnet` image honours `TZ=Europe/Berlin` (tzdata); if it does not, record it in `docs/superpowers/open-items.md` rather than adding packages. Tests use a `TimeProvider` with a fixed local zone.
20. **Archive figures for the web.** Add an `archive` summary to the tree response (files excluding `.vaultkeep`, bytes as `du` counts them) and `GET /api/vaults/{vault}/archive` listing the items. An item is every file or folder whose name carries the timestamp; any other file below `_archive/` outside such a folder is an item of its own, dated from its last write or move, sorted after the timestamped ones.
21. **Restore onto a taken name.** The move API moves *into* an existing folder (`NoteApi.cs:560-562`); the Archive view checks first whether the target exists and opens the "another name" dialog.
22. **The storage notice** is account-wide and shown from 90 % (`StorageNotice.tsx:5,13-24`). Add the archive's bytes (the `du` measure, across the owner's vaults) to the account storage figures and name the archive when it is at least 10 % of `used`.
23. **The skill text**: Markdown links from the vault's root without the vault's name (`/Manual/images/…`), spaces as `%20` (`Mv.cs:26-27`, `LinkResolver.cs:77-84`); crop only `drawing` regions (`classify.ts:14`; `image` regions come from `pdfimages` itself); `pdf info` lists regions only on pages with at least 5 % figures, so look at the rendered pages for smaller drawings. Add a test that an embed written as the skill says resolves.
24. **Welcome note**: the paragraph goes into `src/Kumbu.Server/Welcome/Notes/Skills.md`; keep `WelcomeTextTests` green.

## Planning

1. Write the plan with `superpowers:writing-plans` to `docs/superpowers/plans/2026-11-08-ingest-attachments.md`.
2. It starts with **Task 0: baseline**: run the full suites before any change and record the exact counts and the exact failures, against the known state below.
   Known state on `feature/skills` at `3af5397` (the base of this branch; it adds only docs), measured on the planning machine on 2026-11-08 with `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run && pnpm -C web build`, `pnpm -C pdf test && pnpm -C pdf build`: **.NET total 5037, succeeded 5033, failed 0, skipped 4**; **web 112 files, 1834 tests, all passing, build green**; **pdf 8 files, 146 tests, build green**. The skills run's last gate measured 5031 succeeded and 6 skipped (2 more skip as root).
   `AttachmentMetricsTests.Turned_pages_count_for_pdf`, `RekeyTransitionTests` and `PairingTests` have failed now and then under load in earlier runs: if Task 0 shows a failure, run that test class alone three times, record whether it is flaky, and carry on; a test that fails every time on the untouched branch is a known failure for the report, not something to fix.
3. Every task stands alone: exact file paths (with line anchors where it modifies code), the exact command for each test step with the expected pass or fail, red test, green, then mutations. No placeholders, no "see above", no "similar to Task N".
4. **Order by value**, as spec §7 says, so a short night still leaves something whole: (1) the sidecar routes and `IAttachmentPixels`; (2) `pdfimages` in `sh` with its registries; (3) the archive in `sh`: `archive`, `--archive` on every command the spec names, the PDF page hits of `semsearch`; (4) the archive in the web app: tree entry, Archive view, search checkbox, storage notice; (5) the system skill `ingest-attachments`, help texts, README, welcome note, and the security review. Put a phase gate after each.
5. The final task repeats: do not merge.
6. Have the plan reviewed by a fresh subagent that gets the spec, this brief and the plan, not your conclusions. Fold in the findings, then commit the plan.

## How to work

- Task by task as in the plan: a fresh subagent does red test, green, commit, then every mutation individually (break, watch it fail, restore with `git checkout -- <file>`). Then a spec-compliance review and a code-quality review, each by a fresh subagent.
- Subagents never merge, push or switch branches. You commit and push.
- Commit and push after every task. Commit messages imperative and free-form ("Store a PDF's images as attachments"), no Conventional Commits prefix, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The commit author is Claude, which is intended.
- The test filter is Microsoft.Testing.Platform's: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"`, repeatable. `FullyQualifiedName~` doesn't work here, and a glob in mid-name selects nothing. Core: `--project tests/Kumbu.Core.Tests`. Web: `pnpm -C web test --run <file>`. Sidecar, one file: `pnpm -C pdf exec vitest run test/<file>.test.ts`; `pnpm -C pdf test` (type check plus the whole suite) at the gates.
- Full verification at each phase gate, as CI does (`.github/workflows/ci.yml`): `dotnet restore --locked-mode`; `dotnet build --configuration Release --no-restore`; `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`. CI's litmus job (the WebDAV compliance suite, `.github/workflows/ci.yml:61-118`) is optional here, since this work does not touch WebDAV: run it at the final gate if you can follow those steps, otherwise say so in the report.
- **The web's CI gate is `pnpm -C web build`:** it runs the type check, which `pnpm test` does not.
- Only `dotnet build`, `dotnet test`, `pnpm test` and `pnpm build` output count as evidence.
- **Test PDFs are generated, never committed**: the sidecar's tests build them with pdf-lib in `pdf/test/fixtures.ts` (photo, chart, logo, scan, huge image pages are there); add what you need there (a logo on every page, a page with two images, a vector drawing). The C# side uses the existing fakes (`FakePixels` in `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs`, `FakePdfSidecar` in `tests/Kumbu.Server.Tests/Attachments/`).
- Time-dependent behaviour (the archive's timestamp) goes through the `TimeProvider` the code already injects, so tests don't sleep and don't depend on the machine's time zone.
- **A new `sh` command joins every registry**: `Shell.cs` (insertion order is the help order), `KumbuMetrics.KnownCommands`, `ConsoleApi`'s refused list (the test `Every_shell_command_is_classified` checks it), `web/src/console/help.ts`, `Manual.ToolDescription`, `HelpTests`. A new option joins the command's `Spec` array and its `Help` text.
- **If the night runs short**, stop at the last green phase gate rather than half-way through a task, push, and say in the report which tasks are left.
- **Style:** British English in code, comments, docs, commits (colour, behaviour, initialise, licence as the noun). Match the surrounding code's naming, idiom and comment density; comments only where the reasoning isn't obvious. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- **`web/src/api/schema.d.ts` is edited by hand here**, as generated output would have it.
- **The repository names no organisation, customer or employer**: code, docs, tests, examples, test data, the skill's text. Use Alice, Bob, Carol, `Work`, `Private`, a pump manual, wild flowers from the garden.
- The texts an AI reads (`pdfimages`'s and `archive`'s help, the `--archive` lines, `ingest-attachments`'s `SKILL.md`) are short, literal and in the voice of the existing `src/Kumbu.Core/Sh/Manual.cs` and `create-skill`. Read them before writing. `skills check` on the new system skill must pass; its name is `ingest-attachments` (hyphen: the Agent Skills name rules allow no underscore).
- The web app's Archive view and tree entry follow the app's existing screens and components; dimmed means the existing muted text and icon colours, in light and dark mode. Check them at phone width.

## Deciding without Chris

When the spec leaves something open, or something unforeseen comes up, choose the option that satisfies these, in this order:

1. **Breaks nothing.** Existing behaviour, data and interfaces keep working; prefer the reversible change. A vault without `_archive/` behaves exactly as before; `pdf`'s output and options stay as they are.
2. **True to the spirit of the application.** Follow its existing patterns, style and architecture: PDF work happens in the sidecar, every stored file goes through `AttachmentWriter`, moves go through `NoteMover`, reserved names are left out of listings as `.vaultkeep` is; no new dependencies.
3. **Opens no new security hole.** Never weaken authentication, authorisation, validation or secret handling. When unsure, the reading that writes less, keeps the sidecar's limits and refuses in the read-only console is the right one.
4. **True to the spec.** Chris' guiding wish for this work: **no information may be lost** in an ingest. Where a choice trades fidelity against size or convenience (resolution, skipping an image, a lossy encoding), lean to fidelity within the limits the spec sets.

If a question can only be answered by changing the spec's scope, do not guess: leave that part out and record it in `docs/superpowers/open-items.md`.

Log every decision you made this way. Put it in the report and in the pull request description under **Decisions made without Chris**, each with the options you saw and why you chose this one.

## A few things that matter to us

- Please do not merge, tag or deploy, and don't change the version: rollout is something Chris does himself. One draft pull request is the result.
- No secrets in logs, assertions or the report. Note and attachment contents never reach a log either.
- **Archive hiding is not access control.** Never make it one, and never let it change who may read or write what.
- **Images are decoded and encoded anew**, never stored as the PDF's raw bytes; every stored image passes `AttachmentWriter`'s magic-byte check.
- **Security boundary: touched, narrowly.** A new writing `sh` command fed by untrusted PDFs through the sidecar, new sidecar routes, a new move command, query-time filters in search. A late task has a fresh subagent review the whole branch against spec §5; every finding goes in the report, fixed or not, and the pull request says a security review applies. **Not touched:** encryption, accounts, sessions, sharing and roles, OAuth, pairing, WebDAV, Obsidian sync, the export, publishing, `CallerMiddleware`, the sidecar's `mem_limit`. **Allowed exceptions, and only these:** the skills machinery gains one system skill and skips `_archive` in the vault Skills (`SkillDirectory`); `compose.yaml` gains `TZ` for the `kumbu` service; link resolution prefers candidates outside `_archive/`; a new endpoint puts a folder in the trash with `rm`'s rules.
- At the end: a row with the pull request's link in `docs/superpowers/open-items.md`, and in the report a hand-test checklist with numbered steps, where to look and what passes. Chris tests it on staging together with WebDAV and skills, so include the end-to-end example of spec §4 with a real PDF, the four-photos case of spec §3.3, archiving, restoring from the Archive view, and `--archive` in `ls`, `rg` and `semsearch`. Don't change anything else outside the plan's files.

## Out of scope

- Everything spec §2 lists as a non-goal: other formats, server-side OCR, the PDF's raw image bytes, ingesting on upload or from the web app, an expiring archive or its own quota, hiding the archive from WebDAV, sync, export, publishing or sharing, archiving from the web app's tree.
- Changing the Obsidian plugin (another repository). If the archive needs anything from it, record it in `docs/superpowers/open-items.md`.
- Merging `master`, `feature/skills` or `feature/webdav` into the branch.

## The report

First open a **draft** pull request with base **`feature/skills`** (not `master`: Chris retargets it once #153 is merged), titled "Ingesting attachments: pdfimages, the archive and a system skill", whose body lists the decisions made without Chris and says that a security review applies, and ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Then, as the last commit, `docs/superpowers/overnighter/2026-11-08-ingest-attachments-report.md`, with:

- the environment
- the plan review findings and what you did with them
- Task 0 planned vs. measured
- for each task the commit and the real test output
- every mutation individually
- the security review's findings and what became of each
- the decisions made without Chris
- deviations, surprises, tasks left if any, open points for Chris
- the hand-test checklist
- the pull request's link, together with a row linking it in `docs/superpowers/open-items.md`

Never force-push: if something has to come out, use `git revert`.

## When you are done or stuck

Send a short message via `PushNotification` if it is available; otherwise the pull request is the signal and the report says so. If you get stuck, push what you have, write down in the report where and why, and stop there. A clear account of being stuck is worth more than a guess. Good luck, and thank you!
