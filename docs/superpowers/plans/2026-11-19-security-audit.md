# Plan: the security review of 0.6.0

Spec: `docs/superpowers/specs/2026-11-19-security-audit-0-6-0-design.md`. Brief: `docs/superpowers/overnighter/2026-11-19-security-audit-brief.md`. The code under review is `v0.6.0-pre.4` (`63d3ca46`) against `v0.5.0`.

This plan is public. It names areas and methods and **never a finding**: no id paired with an area, file or feature, no candidate, no count before a phase ends. Once committed it is not touched again.

## Rules that hold for every task

- **Blue team only** (spec §1): review and behaviour tests of our own code. Each test asserts what the code *should* do; its inputs are the ordinary untrusted inputs the spec lists. No exploit, no payload beyond those, no chain, no live host (not staging, production, TensorX, or GitHub beyond reading pull requests).
- **Nothing is fixed.** No product code, no version, no dependency changes; no merge, tag or deployment.
- **Public commits:** this plan now; `docs/superpowers/open-items.md` and the report in phase 5. Nothing else. Files are staged by name; before every public push `git diff origin/audit/0-6-0-security --stat` is checked.
- **Confidential material** (candidates, tests, reviewers' reports) lives only in the private clone, under `audits/2026-11-19-0.6.0/`, and in scratch worktrees that are never committed from.
- **Worktrees:** `/home/user/audit-scratch/<name>`, each `git worktree add … v0.6.0-pre.4`, one per reviewer. Subagents never commit, push, merge or switch branches; the controller does every commit and push.
- **One heavy run at a time** across all agents (two suites at once have killed the test container before): every `dotnet build`, `dotnet test`, `pnpm … test` and `pnpm … build` runs as `flock -w 3600 /home/user/audit-scratch/heavy.lock timeout 20m <command>`; a timeout is recorded, not retried blindly. One shared test MongoDB (`mongo:9`, replica set, capped log); reviewers run single test classes, never a suite, and name a class with its namespace (`*.Vaults.RekeyTests`), since a bare `*RekeyTests` runs more than it names. Builds run with `-nodeReuse:false -p:UseSharedCompilation=false`, so no build server stays in memory per worktree.
- **At most four area reviewers at a time**, in the brief's order. A worktree is removed once its reviewer's report is in the private clone.
- **After a container restart:** start `dockerd`, `docker start kumbu-test-mongo`, wait for the ping and a primary, then resume the stopped agents with a message saying what was lost; they `cd` into their own worktree and use `git -C <path>`.
- **Never force-push;** what has to come out is taken out with `git revert`. Commit messages are imperative and free-form and end with `Co-Authored-By: Claude Mythos 5.2 <noreply@anthropic.com>`.
- **Without the superpowers skills**, the work is done subagent-driven with the plain Agent tool, and the report says so. If `PushNotification` isn't available, the card and the pull requests are the signal.
- **Measurements** in Release, beside a control under the same load, with the load average stated. A measurement holds the lock for its whole run, control included.
- **Server tests fake the clock in 2037.**
- **No secrets** in logs, assertions or reports; fixture-made test passwords and keys are fine. Neutral names only (Alice, Bob, Carol, Dana, `Home`, `Work`).
- **The Factory card** carries phases and an hourly log line, and never an area, a file, a detail or per-area progress; ids, severities and counts only at the end of phases 2, 3 and 4 (the one exception is spec §6). If the connector isn't available, the report says so and the run carries on.
- **Deciding without Chris:** the brief's order (gives nothing away, defensive only, honest, true to the spec). Each such decision is logged for the report.
- **A question that only a change of the spec's scope could answer** is left out and recorded in `docs/superpowers/open-items.md` as the brief says.
- **Time** (ten hours from 16:57 UTC): phase 1 ends at start + 6 h whatever is unfinished; phase 4 is pushed by start + 8 h 30 min; the public report by start + 10 h. If time runs short: stop reviewing, write what there is, and name the unfinished areas in both reports.

## Task 0: set-up and baseline (about 20 minutes)

1. Read the board's rules with `sh board /Factory`; add the card "Security review 0.6.0" (vault `Factory`, column `Running`) with the repository and branch, the brief's path, the session's link, `## Steps` with the six phase names as the brief gives them, and `## Log`.
2. Tools: .NET SDK per `global.json`, Node 26 from `/opt/node26`, pnpm 10.32.1, Docker with the brief's `mongo:9` container. Record versions and anything installed.
3. Private clone beside the public one (clone it if it isn't there): branch `audit/0-6-0` from `master`. If the private repository can't be cloned or pushed to, here or in phase 4: nothing confidential is written anywhere public; the findings go in full into the session's final message; the public report says only that this happened, with ids and severities.
4. Scratch worktree `audit-run` at the tag; `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore`; `pnpm -C web install --frozen-lockfile`; `pnpm -C pdf install --frozen-lockfile` (if the native canvas binary can't be fetched, record it and go on without the sidecar's tests).
5. Baseline: the test classes changed in `v0.5.0..v0.6.0-pre.4` that belong to the areas, one class at a time, counts recorded:
   - core: `Attachments/*` (5 classes), `Graph/*` (6), `Notes/Authorship*`, `Notes/NoteWriterTests`, `Publishing/FairCopyTests`, `Sections/SectionAddressTests`, `Sh/BlameTests`, `CsvTests`, `HtmlTests`, `XmlTests`, `GlobTests`, `GraphCommandTests`, `LsTests`, `RgTests`, `PublishCommandTests`, `CatTests`, `Skills/*` (4);
   - server: `Api/AgentUploadApiTests`, `UploadTicketsTests`, `UploadApiTests`, `FileApiTests`, `FileDownloadTests`, `LargeFileTests`, `LargeFileRefusalTests`, `LargeFileMemoryTests`, `BlameApiTests`, `ByokApiTests`, `ConsoleApiTests`, `GraphApiTests`, `HistoryApiTests`, `SyncApiTests`, `ExportApiTests`, `Auth/CallerTests`, `Dav/*` (6), `Discreet/HiddenVaultTests`, `Mcp/DeepLinkTests`, `UploadToolTests`, `HandshakeInstructionsTests`, `Publishing/*` (5), `Search/Byok*` (3), `SearchIndexTests`, `Accounts/EmbeddingsKeysTests`, `FavouritesTests`, `Vaults/LargeBlobTests`, `RekeyTests`, `EncryptedNoteStoreTests`, `AttachmentStoreTests`, `Startup/KumbuOptionsValidatorTests`;
   - web: the folders `attachments`, `upload`, `data`, `reader`, `note`, `blame`, `palette`, `windows`, `discreet`, `console`, `routes`, `shell`;
   - the sidecar: `pnpm -C pdf test`.
   - also, by area: `Api/NoteApiTests`, `QuotaApiTests`, `Paths/FileOrFolderTests`, `VaultPathTests` (A); `Attachments/ImageMetadataTests`, `AttachmentPixelsTests`, `PdfSidecarTests`, `PdfToolTests`, `Skills/TextFileChannelTests` (C); `Vaults/AuthorshipStoreTests`, `Skills/SkillUpdateTakeTests`, `Sh/SkillsCommandTests` (E); `Api/PasswordWindowTests`, `Sh/ArchiveHiddenTests` (F); `Vaults/GraphStoreTests`, `LinkExtractStoreTests`, `Sh/CalendarCommandTests` (G); `Metrics/ServerMetricsTests`, `UserKeyRotationTests` (H).

   One start of the runner per class makes this longer than 20 minutes; the area reviews start beside it.

   A failing test is run alone three times, recorded, and the run goes on. The full suites are not run: nothing is changed, and CI was green on `63d3ca46`.

## Phase 1: the area reviews

One fresh reviewer per area, in its own worktree, in the brief's order of weight: B, A, D, F, E, C, H, G, I. Reviewers run in parallel as far as the machine allows; the heavy lock keeps their builds and tests apart.

Every reviewer gets: the spec and brief (paths), its area's row of spec §2 with the files below, the trust boundaries of spec §2 that touch the area, the classes of the 0.4.0 spec §4 step 2, the crossings of the brief's open point 3, and the rules above.

Each crossing has one owner, whose report says "checked" or "not reached" for it: a large file through each way out, and re-keying a vault with chunked files: A; an upload link while sharing changes: B; `blame` and history in a discreet vault and after a member leaves: E; a template or a CSV by a shared vault's writer: C; an SVG or HTML file through a deep link or a published bundle: D; the console's completion and the palette listing a hidden vault: F. Every reviewer:

1. **Reads** the area's feature specs (`docs/superpowers/specs/`, 2026-11-15 to 2026-11-19), the security review in each pull request's squash commit (`git log -1 --format=%b <sha>`), and the parts of `docs/security-architecture.md` at the tag that make the area's promises; and so knows what earlier reviews accepted and what they recorded as fixed.
2. **Models** the area: every entry point new or changed since 0.5.0 (endpoint, MCP tool, `sh` command, page, background job), the trust boundaries each touches, what the design promises.
3. **Reviews each entry point** against the classes: authentication and authorisation for every role and credential kind; path handling; injection; outbound requests; resource use on bounded oversized and nested inputs; races; information leaks (existence, error texts, timing, logs); cryptography where keys or slots are new; request forgery and origin checks; the CSP and response headers.
4. **Shows each candidate** by a behaviour test beside a positive control, or by a trace with `path:line`; rates it on the scale (impact × reachability, the higher level when in doubt, the doubt written down); checks it against earlier reviews (accepted: no finding; "fixed" but not fixed: new) and against the design decisions of spec §8 ("needs a decision from Chris" where one looks unsafe).
5. **Writes** its report to `audits/2026-11-19-0.6.0/reports/<area>.md` in the private clone and its tests under `audits/2026-11-19-0.6.0/<area>/` at their repository paths, with the outputs in `<area>/output/`. The report also lists what was checked and found sound, and what was not reached.

The areas (spec §2 gives the pull requests):

| # | Area | Files and places, beyond the spec's row |
|---|---|---|
| A | Files in and out | `src/Kumbu.Server/Api/FileApi.cs`, `UploadApi.cs`, `NoteApi.cs`, `ApiErrors.cs`, `SyncApi.cs`, `EntityTags.cs`; `src/Kumbu.Server/Vaults/` (`LargeBlobs.cs`, `BlobSweep.cs`, `UnlockedVault.cs`, `VaultCatalog.cs`, `VaultCatalog.Rekey.cs`, `Documents.cs`, `Credentials.cs`, `EncryptedNoteStore.cs`); `Program.cs`, `KumbuOptions.cs`, `KumbuOptionsValidator.cs`; `src/Kumbu.Core/Attachments/`, `Paths/FileOrFolder.cs`, `Sh/AttachmentText.cs`, `Sh/Commands/TextFiles.cs`; `src/Kumbu.Server/Dav/`; `Skills/SkillUpdateTaker.cs`; the export; `web/src/attachments/`, `web/src/upload/`, `web/src/data/`. Method: every way out (publish, export, ZIP, WebDAV, sync, `cat`, the reader, the file endpoint) header by header for every file type; the chunk format and its key against §3 and §5; limits and quota at each way in; re-keying a vault with chunked files; the sweep |
| B | Agents' uploads | `Mcp/UploadTool.cs`, `Api/AgentUploadApi.cs`, `Api/UploadTickets.cs`, `Auth/CallerMiddleware.cs`, `Auth/WrongKeyLimiter.cs`, `OAuthUrls`, the system skill `kumbu-binary-upload`, `KUMBU_PUBLIC_URL` and the proxy's trust. Method: the link's making, storing, finding, spending and ending; what the anonymous answers say; the rights it stores with, also when sharing or the credential changes while it lives; the HTTPS rule; the rewritten middleware's every route against 0.5.0's |
| C | Parsers and rendering | `Sh/Commands/Csv.cs`, `CsvParser.cs`, `Html.cs`, `CssSelection.cs`, `Xml.cs`, `TextFiles.cs`; `Attachments/SvgImage.cs`; `src/Kumbu.Server/Attachments/` (`ImageMetadata.cs`, `AttachmentPixels.cs`, `ImageHeader.cs`, `PdfSidecar.cs`, `PdfAnalysisRunner.cs`); `pdf/src/`; `web/src/note/` (templates, the properties box), `web/src/reader/` (Mermaid from a data URL). Method: the spec's ordinary untrusted inputs to each parser, asserting neutralised or bounded; how each parser's output reaches an agent and a browser; the sidecar's process boundary |
| D | Publish 2.0 and deep links | `src/Kumbu.Server/Publishing/`, `src/Kumbu.Core/Publishing/`, `Sh/Commands/PublishCommand.cs`, `Mcp/DeepLinks.cs`, `Mcp/ToolResult.cs`, `WriteTools.cs`, the "Not here" page, `web/src/reader/`, `web/src/router.tsx`. Method: what a bundle holds against what its maker may read, byte by byte for a note left out; the walk's bounds; updates and withdrawal; the anonymous answers; a deep link for every kind of caller without access |
| E | Sharing and who wrote what | `Notes/Authorship.cs`, `NoteHistory.cs`, `NoteWriter.cs`, `Sh/Commands/Blame.cs`, `Api/HistoryApi.cs`, the blame endpoint (in `Api/NoteApi.cs`), `web/src/blame/`, `Mcp/HandshakeInstructions.cs`, `src/Kumbu.Core/Skills/`, the system skills' texts, `Sync/SyncHub.cs`, `Sharing/VaultEvents.cs`. Method: `blame` and history for every role and credential kind, after a member leaves, in a discreet vault; what a writer's skill or message can make another member's agent do or see; the handshake text as untrusted-data carrier |
| F | Discreet vaults and the windows | `Accounts/Favourites.cs`, `UserDocuments.cs`, `Api/FavouritesApi.cs`, `Api/DiscreetApi.cs`, `web/src/palette/`, `web/src/windows/`, `web/src/discreet/`, `web/src/console/` (completion), `web/src/shell/`, `web/src/routes/`, `web/src/recent/`, `web/src/note/` (split view, `?beside=`, the side panel). Method: every new endpoint and view for a session with its discreet vaults hidden; the lifetime of passwords and recovery keys in the windows; what stays in the address, storage and caches after a hide |
| G | Resource use | `src/Kumbu.Core/Graph/`, `Api/GraphApi.cs`, `Sh/Commands/GraphCommand.cs`, `Sh/Glob.cs`, `Rg.cs`, `Sections/SectionAddress.cs`, `Cat.cs`, `Rm.cs`, `Mv.cs`, `Ls.cs`, `CalendarCommand.cs`, `Diffs/Myers.cs`, `src/Kumbu.Server/Search/SearchIndex.cs` (the queue). Method: cost against input size beside a control, cancellation, per-request and per-account bounds, what persists across a restart, what another user sees |
| H | BYOK and cross-cutting | `Search/Byok.cs`, `ByokDrivers.cs`, `IEmbeddingDriver.cs`, `OpenAiCompatibleDriver.cs`, `Api/EmbeddingsKeyApi.cs`, `Accounts/EmbeddingsKeys.cs`, `UserKeyRotation.cs`, `Vaults/Credentials.cs`, `VaultCatalog.Members.cs`, `Sharing/SharingApi.cs`, `Api/AccountApi.cs`, `web/src/routes/settings/`; `Api/ConsoleApi.cs` with every command new or changed since 0.5.0; `dotnet list package --vulnerable --include-transitive`, `pnpm -C web audit`, `pnpm -C pdf audit`; every log line, metric label and error text added since 0.5.0; `.github/`, `.dockerignore`, `compose.yaml`, the Dockerfiles. Method: the key's sealing and every place it is opened; where the key check may send a request (a stub stands in for the provider); the console compared by the vault's state with the same writes over MCP as controls |
| I | The 0.5.0 findings at this tag | every S5 and L5 finding from the private file `audits/2026-11-14-0.5.0.md` and the branches `fixes/0-5-0` and `fixes/0-5-0-rest`: is the fix in the tag, does it hold where 0.6.0 code sits beside it; the 0.5.0 audit's own tests re-run where they still apply. Results go to the private file only |

Changed files that no row above names go to these areas:

- **A:** `Sh/Commands/Df.cs`, `Paths/VaultPath.cs`, `Notes/TreeWriter.cs`, `NoteCreation.cs`, `StoredNote.cs`, `VersionToken.cs`, `INoteReader.cs`, `web/src/components/fileKindIcon.ts`.
- **C:** `Sh/Commands/Img.cs`, `PdfCommand.cs`, `PdfImages.cs`, `Sh/PictureText.cs`, `web/src/state/editor.ts` (templates; with F for the split view).
- **D:** `Links/LinkResolver.cs`, `NoteLinks.cs`, `web/src/routes/DeepLink.tsx` (the "Not here" page), `PublicationsSection.tsx`, `web/src/api/publications.ts`.
- **E:** `Sh/Commands/History.cs`, `SkillsCommand.cs`, `Notes/Activity.cs`, `Checkpoints.cs`, `CheckpointView.cs`, `Replacer.cs`, `Welcome/Notes/Skills.md`, `web/src/skills/`, `web/src/routes/Skills.tsx`, `web/src/hooks/useSyncSocket.ts`.
- **F:** `web/src/components/` (`PasswordPrompt.tsx`, `RecoveryKeyReveal.tsx`, `ConfirmTyped.tsx`, `shownOnce.tsx`, `Menu.tsx`, `ListFilter.tsx`, `PointMenu.tsx`), `web/src/hooks/` (`useDialog.ts`, `useSidePanel.ts`), `web/src/api/favourites.ts`, `web/src/search/SearchSheet.tsx`, `web/src/board/BoardPage.tsx`, `web/src/calendar/`, `Api/CalendarApi.cs`, `Sh/Commands/ArchiveCommand.cs`, `Notes/ArchiveFolder.cs`, `App.tsx`, `theme/apply.ts`; and wherever the graph is drawn in the web app, if it is.
- **G:** `Calendar/CalendarLayout.cs`, `CalendarService.cs`, `Sh/Commands/Semsearch.cs`, `Notes/NoteMover.cs`, `Tags/TagReader.cs`, `Board/BoardDefinition.cs`, `Mcp/BoardTool.cs`.
- **H:** `Metrics/KumbuMetrics.cs`, `Mcp/ShTool.cs`, `Sh/Shell.cs`, `ShellResult.cs`, `Manual.cs`, `Sh/Commands/Help.cs`, `web/src/api/client.ts`, `paths.ts`, `notes.ts`, `sharing.ts`, `.github/dependabot.yml`, both `packages.lock.json`, `Kumbu.Core.csproj`, `web/package.json`, `pdf/package.json`. A vulnerable dependency is rated by whether Kumbu² reaches the vulnerable code; a lookup that fails behind the proxy is recorded, not guessed.

## Phase 2: the second reviews

For every candidate of Medium or higher and every Low, a second fresh reviewer who gets the claim (title, place, the behaviour claimed, the test) and the code, not the first reviewer's reasoning, in a worktree of its own. It tries to reproduce and to disprove; it confirms, rates up or refutes; a Low is never rated down except to "refuted". Its report goes to `reports/<area>-second.md`. Where the two disagree on severity, a third reviewer settles it. A suspicion with something measurable gets the same check.

**A High or Critical the second review confirms** (spec §6), at once: write it in full into the private file, push, open the private pull request if it isn't open yet; send the notification "Security review 0.6.0: a High (or Critical) finding, S6-NN, details in the private pull request"; move the card to Waiting for Chris with the id, the severity and the spec's sentence; carry on.

## Phase 3: the severity check

One fresh reviewer measures every resource finding of Medium or higher against a control: what another user's ordinary request costs while the slow calls run, whether the cost persists (indexing, queues, a restart), whether it can be cancelled. Report: `reports/severity.md`. A remaining doubt towards High is written down with the measurement that would settle it.

## Phase 4: the confidential findings

1. Number the findings `S6-NN` and `L6-NN`; decide which Lows stay private (those that would point to a confidential finding).
2. Write `audits/2026-11-19-0.6.0.md` in the private clone: the summary table, the controller's decisions on severity, every Medium-or-higher finding with the fields of spec §4, the Lows kept private, the state of the 0.5.0 findings, what each area checked and found sound. If nothing is Medium or higher: "none", and why that is to be trusted.
3. Commit `audits/` by name, push `audit/0-6-0`, open the pull request "Security review of 0.6.0" to `master` there. Do not merge.
4. Card: the counts per severity.

## Phase 5: the public report

1. Write `docs/superpowers/overnighter/2026-11-19-security-audit-report.md`: the environment, the plan review, Task 0, per area what was reviewed and how (the method, not the findings), the counts, every public Low in full, the decisions made without Chris, deviations and open points, both pull requests' links (the private one by its URL only).
2. Have a fresh reviewer check the report, the pull request text, the commit message and the card's final text against the publication rule: it gets those texts and the private file's summary table, and says whether any public sentence, a Low included, points at a confidential finding.
3. Open the pull request "Security review of 0.6.0" to `master` on the plan commit, with a description that names nothing yet.
4. Add the row to `docs/superpowers/open-items.md` (that pull request's link, no detail; scope questions, if any, as the brief says). Commit the report and `open-items.md` in one commit, by name; check `git diff origin/audit/0-6-0-security --stat`; push.
5. Set the pull request's description: the counts per severity (not per area), the Lows in full, the confidential findings by id and severity only, **Decisions made without Chris**; it ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
6. Move the card to Done with the public link; send the notification.
7. **Do not merge, tag or deploy.**
