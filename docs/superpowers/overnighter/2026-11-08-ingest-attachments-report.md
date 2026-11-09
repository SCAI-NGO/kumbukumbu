# Overnighter report: ingesting attachments

Branch `feature/ingest-attachments`, based on `feature/skills` (`3af5397`, plus the spec commit `d868ae7`). Draft pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/174. Nothing was merged, tagged or deployed, and the version is unchanged.

**Result:** all five phases of the plan are done: the sidecar routes and `IAttachmentPixels` methods, `pdfimages` in `sh`, the archive in `sh`, the archive in the web app, and the system skill `ingest-attachments` with help, README and welcome note. A security review of the whole branch ran at the end; its findings are fixed or recorded below. **Final gate** (full CI steps, in a worktree): at `de66eab` web 114 files, 1854 tests, build green; pdf 9 files, 198 tests, build green; .NET total 5329, succeeded 5322, **failed 1** (`PasswordWindowTests.Every_session_only_endpoint_is_guarded_or_deliberately_open`: the folder trash, made session-only by the security fix, was in neither list). Fixed in `111faba` (named among the deliberately open endpoints: vault content under the write right, like deleting a note); the .NET rerun at `111faba`: total 5329, succeeded 5323, failed 0, skipped 6 (4 model/key, 2 as root). GitHub CI on the pull request at `111faba`: build, Web app, PDF sidecar and WebDAV (litmus) all green.

## 1. Environment

- **Installed by hand for the run** (nothing added to the project):
  - Node **26.13.0** into `/opt/node26` (checksum checked against nodejs.org's `SHASUMS256.txt`); the image had 22.23.4. `PATH` was prefixed per command: making it permanent in the shell profile was refused by the session's safety classifier, so every script and agent set it itself.
  - pnpm **10.32.1** with `npm -g`.
- **.NET** 10.0.403 (global.json rolls forward from 10.0.100). `DOTNET_CLI_TELEMETRY_OPTOUT=1`.
- **Docker:** `dockerd` was not running; started by hand (`nohup dockerd`). The shell's hard `nofile` limit is 20,000 and cannot be raised.
- **MongoDB:** own container `kumbu-test-mongo` (`mongo:7.0`, `--ulimit nofile=20000:20000`, replica set `rs0`, port 27017), `KUMBU_TEST_MONGO=mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true`, `TESTCONTAINERS_RYUK_DISABLED=true`. The first .NET run crashed mongod after a few minutes with WiredTiger `errno 24` (too many open files), as the skills run had seen; every later run started mongod with `--wiredTigerEngineConfigString "file_manager=(close_idle_time=15,close_scan_interval=5,close_handle_minimum=250)"`.
- **Process:** superpowers skills were available. Subagent-driven development: a fresh implementer per task (red, green, commit, every mutation alone, restored with `git checkout`), then reviews by fresh subagents, then a fix wave where reviews found something. Deviations from the brief's process are in section 7.
- **Litmus** (the WebDAV compliance suite) was not run locally; GitHub's CI ran it on the pull request at `111faba` and it passed, with `build`, `Web app` and `PDF sidecar`.

## 2. The plan and its review

The plan is `docs/superpowers/plans/2026-11-08-ingest-attachments.md` (Task 0 plus 18 tasks in five phases with gates). A fresh subagent reviewed spec, brief and draft against the code, pdf.js 6.4.299's source included. **All 27 findings were accepted** and written into the plan as rulings that override task text; the most important:

- the console fixture and its web assertions belong to the registry tasks (1);
- pdf.js warnings may quote the file, so extraction jobs swallow the console and count only the exact oversize sentence (2);
- a long-edge cap of 16,384 px beside the 8 MP cap (3);
- a time budget of 20 s with listing in its first half and a hard ceiling of 50 s (4);
- how pdf.js really hands over image data (callback `objs.get`, inline images, atlases, masks) (5), the fixtures for every image kind (6), failed decodes (7), boxes clipped to the page (8), memory (9);
- the folder trash reuses `TreeWriter.RemoveAsync` (10); archive filters after the empty-result checks (11); registries in the task that registers a command (18); and more (see the plan).

## 3. Task 0: baseline

| | Planned (known state at `3af5397`) | Measured (`d868ae7`, clean worktree) |
|---|---|---|
| .NET | total 5037, succeeded 5033, failed 0, skipped 4 (6 as root) | total 5037, succeeded 5031, failed 0, skipped 6 (4 model/key, 2 as root) |
| web | 112 files, 1834 tests, build green | 112 files, 1834 tests, build green |
| pdf | 8 files, 146 tests, build green | 8 files, 146 tests, build green |

Task 0 took far longer than 15 minutes: the first .NET run died with mongod (above); a second run, started while implementers were already running in parallel, hung for about 45 minutes after a `RekeyTransitionTests` time-out and a `PairingTests` failure (both known as flaky under load) and was stopped. The measured row is a third run in a clean worktree at `d868ae7` with `-- --timeout 50m`; it passed without a failure (server tests 17 min under load).

## 4. Tasks, commits and test output

Test counts are the implementers' own `dotnet test` / `pnpm test` summary lines. "Mutations" lists each mutation applied alone, the test that failed, and the restore (`git checkout -- <file>`); every listed mutation failed its test.

### Phase 1: sidecar routes and `IAttachmentPixels`

**Task 1** `d9ae0cf` Extract a PDF's images and render regions for storing in the sidecar; fixes `0a3835e`.
- Red: `vitest run test/extract.test.ts` "1 failed, no tests" (module missing). Green: 34/34; `pnpm -C pdf test` 9 files, 180 tests; build green. After fixes: 39/39, suite 195.
- Mutations: no quarter turn (turn tests 90/180/270/mirrored fail); no 8 MP scale; solid-colour mask counted; constant hash; alpha flattened; `small` threshold 0; console not swallowed; off-page kept; 16,384 cap removed; no white flatten for JPEG; atlas offsets ignored; (fix wave) positive-size check removed; `declared` raw; mask padding removed; walk limit removed; hash cache removed (needed a larger fixture to fail by time-out); `setTransform` q/r swapped; turn ignored in rendering.
- Found: pdf.js's `getOperatorList` never yields the optimised repeat/group/atlas operators (it uses the `NullOptimizer` for the OPLIST intent, `pdf.worker.mjs:11429`); the walker handles them anyway, tested by rewriting real operator lists into the optimised forms.
- Memory, measured (maxRSS): extracting one 7000×7000 RGB image fell from ~980 MB to ~840 MB; the source-size canvas alone costs ~440 MB. Listing a page with four such images reaches ~1.3 GB because pdf.js decodes every image of the page while building the operator list (see open points).

**Task 2** `22751b5` Serve a PDF's images and regions for storing from a pool of one worker; fix `a796118`.
- Red: `vitest run test/server.test.ts` 9 failed / 32. Green: 32/32 (33 after the fix); suite 189; build green.
- Mutations: extraction through the main pool (busy test fails); `no_such_image` dropped from `STATUS`; missing `crop` accepted; `n` parsed with `Number()`; (fix) the list route given the render limit (time-out test fails).

**Task 3** `d80d0f2` Ask the sidecar for a PDF's images and regions to store; fixes `5b04d74`.
- Red: build error CS0246 (`PdfPageImages` missing). Green: `PdfSidecarTests` + `AttachmentPixelsTests` 159/159; after fixes, with `*MetricsTests`, 196/196. `dotnet build` 0 warnings.
- Mutations: hash format check skipped (3 rows fail); 8 MP check skipped (3 tests); `no_such_image` mapped to `Failed`; (fixes) list limit back to 1 MiB; box edge check removed; edge check without tolerance; time-out without the queue; extraction pictures in the delivered-image metrics; busy counted as failure; list not timed; `too_large` arm unrestricted.

**Phase 1 gate:** merged into the gate after Phase 3 (section 7).

### Phase 2: `pdfimages` in `sh`

**Task 4** `8a71840` List a PDF's images with pdfimages --list.
- Red: Core 22 failed / 41; Server 2 failed / 110 (`Every_shell_command_is_classified`, `Known_commands_are_the_shells`). Green: Core 68/68 (141/141 with `PdfCommandTests`); Server 110/110; web console 209/209.
- Mutations: folder without `/` accepted; small filter dropped; `--rotate` without `--crop`; `pdfimages` out of `Refused`; out of `KnownCommands`; listing to the full budget instead of half.

**Task 5** `b76e45d` Store a PDF's images as attachments with pdfimages; `ecf2eef` (Task 4 review folded in).
- Red: 21 failed / 43. Green: 43/43, with HelpTests 61/61; after the review commit 63/63 and `AttachmentPixelsTests` 23/23.
- Mutations: existing-name check skipped; 500 cap after writing; no dedup; quota not caught; budget stopping mid-page; page-count fallback on any failure; `no_such_page` mapping removed (server); `files is null` guard half dropped.

**Task 6** `bb6eca3` Store a rendered region of a PDF page with pdfimages --crop; `9004383` (Task 5 review folded in); `6bce848` (Task 6 review folded in).
- Red: 13 failed / 76; review fold 5 failed / 62; second fold 2 failed / 65. Green: 76/76; 80/80; 187/187 with Help, PdfCommand and Shell tests.
- Mutations: `.gif` target accepted; PNG asked for a `.jpg` target; writing over an existing target; "not listed" line dropped; over lines dropped in a stop; small count over all pages; over lines over all pages; no up-front refusal; crop `WriterRefused` return dropped; "stored only in part" dropped; "go on with" past the last page.

**Task 7** `964d2ed` Point to pdfimages from pdf and the attachments help, and test it through MCP.
- Red: `PdfCommandTests` 1 failed. Green: Core 184/184; Server (`PdfImagesToolTests`, `ConsoleApiTests`, `HandshakeInstructionsTests`, `ConsoleFixtureTests`) 100/100.
- Mutation: `pdfimages` out of `ConsoleApi.Refused` (`Every_shell_command_is_classified` fails).

**Phase 2 gate:** merged into the gate after Phase 3.

### Phase 3: the archive in `sh`

**Task 8** `adce605` Prefer links outside a vault's archive and skip it in the vault Skills; fixes `3716b44`, `de147b4`.
- Red: 7 failed / 106. Green: 106/106; wider 362/362. Fix: red 5 failed / 102, green 102/102 (Links and Notes namespaces 326/326).
- Mutations: `Contains` on any segment; preference removed; skill skip removed; (fix) filter moved above the folder preferences.
- `de147b4` is the coordinator's: the fix wave had removed the exact-link guard as "never changing a result", but without it an exact link could resolve to a path differing in case; the guard stays.

**Task 9** `76d7f24` Move a note, an attachment or a folder into the vault's archive with archive; fixes `2bcd48d`.
- Red: 22 failed / 22. Green: 67/67; related 157/157; Server 81/81; web console 209/209. Fix: Core 252/252.
- Mutations: UTC instead of local time (8 tests); no clash suffix; skill refusal dropped; (fix) old command text with the new mover (4 tests); both exact refusals removed; folder-branch refusal removed; "mv:" back in the mover's text.

**Task 10** `61a8d1a` Leave the archive out of ls, tags and backlinks unless asked.
- Red: 13 failures. Green: 341/341; whole Core project 1904 total, 1902 succeeded, 2 skipped; Server 113/113.
- Mutations: `ls` ignoring `--archive`; `tags` treating a scope inside the archive as not asked; `backlinks` without the target-inside rule.
- Usage lines changed on purpose (OP 16): `TagsCommandTests.Help_tags_and_the_command_list`, `ShellTests.ManualMentionsCommands`, `McpClientTests` (also quoted `ls [path]`). `du` keeps its usage; a test asserts it lists `/Work/_archive/` as its own line.

**Task 11** `9e134dd` Leave the archive out of rg and semsearch unless asked; `7fbf379` (Task 10 review: say when only archived notes would have answered); fixes `6dc5b4d`.
- Red: 9 failed / 49 and 3 / 77; follow-up 6 failed / 43. Green: 371/371; follow-up 378/378; fixes 261/261 and Server 33/33.
- Mutations: PDFs unfiltered in rg; `WithPages` unfiltered; `--archive` ignored in `--files` and with `--tag`; Narrow's archived branch off; hidden count 0; and the fix wave's twelve (name hint without `--archive`, no `Exact` check, no cap, no time-out catch, no cancellation, every hint keeping its lead, `-l`/`-c` dropped, paths dropped, old Manual line, `--tag` ignored, `--files --tag` paths dropped, scope dropped).

**Task 12** `9c495e7` Explain the archive in a help topic and test archive through MCP.
- Red: 5 failed / 58. Green: Core 97/97; Server 96/96.
- Mutations: UTC stamp; `rewriteLinks: false`; `ls` always showing the archive; `help archive` returning the topic alone.

**Gate after Phases 1–3** (full CI steps in a worktree at `9c495e7`): .NET total 5299, succeeded 5293, failed 0, skipped 6; web 112 files, 1834 tests, build green; pdf 9 files, 195 tests, build green.

### Phase 4: the archive in the web app

**Task 13** `6a1f783` Give the web app the archive's figures and items, and a folder trash; fixes `c755707`; security fix `4a40443`.
- Red: Core build error CS0117; Server 16 failed / 25. Green: Core 28/28; Server 25/25; related 397/397; web 1834 and build green. Fixes: red 1 failed / 4, green 74/74 and Core 78/78. Security fix: red 6 failed, green 97/97.
- Mutations: filter in `TreeOf` (the new checkpoint test fails); `RefuseFolder` skipped; `.vaultkeep` counted; marker as loose item; innermost stamp winning; (fixes) `canWrite` always true; no DST check; `RequireVaultAsync` removed.

**Task 14** `ac8a75a` Show the archive in the vault's tree with a view to restore or delete; fixes `1b67e77`.
- Red: 2 failed / 3 (module missing). Green: 14/14; full web 114 files, 1848 tests; build green. Fixes: red 9 failed, green 331/331, full 1854/1854, build green.
- Mutations: restore without the existence check (4 tests); buttons for readers.
- Visual check (Playwright, 390×844, light and dark, API mocked): the dimmed row reads as secondary in both themes; the sheet's cards match the Trash sheet; long names truncate; nothing overflows (`scrollWidth` = 390).

**Task 15** `2237a79` Let the search include the archive and name it in the storage notice.
- Red: Server 2 failed / 19; web 3 failed / 78. Green: web 78/78, full 1836, build green; Server 44/44.
- Mutations: threshold 0; always sending `archive`; ignoring a path inside the archive (server).

### Phase 5: skill, documentation, security review

**Task 16** `e400391` Add the system skill ingest-attachments; security fix `de66eab`.
- Red: 6 failed. Green: Core 112/112; Server 168/168 and 188/188.
- Mutation: `name: ingest_attachments` (rules test and three others fail).

**Task 17** `076f7bc` Describe pdfimages, the archive and ingesting in the README and the welcome notes.
- `WelcomeTextTests` + `MongoOptionsTests` 24/24. The `aspnet` runtime image honours `TZ`: tzdata 2026e is installed, and a .NET program in the image printed `Europe/Berlin +01:00` for `TimeZoneInfo.Local` with `TZ=Europe/Berlin` (UTC without). Nothing went to the open items.

**Task 18** security review: section 5.

### Final gate

**Final gate** (full CI steps, in a worktree): at `de66eab` web 114 files, 1854 tests, build green; pdf 9 files, 198 tests, build green; .NET total 5329, succeeded 5322, **failed 1** (`PasswordWindowTests.Every_session_only_endpoint_is_guarded_or_deliberately_open`: the folder trash, made session-only by the security fix, was in neither list). Fixed in `111faba` (named among the deliberately open endpoints: vault content under the write right, like deleting a note); the .NET rerun at `111faba`: total 5329, succeeded 5323, failed 0, skipped 6 (4 model/key, 2 as root). GitHub CI on the pull request at `111faba`: build, Web app, PDF sidecar and WebDAV (litmus) all green.

## 5. Security review

A fresh subagent reviewed `d868ae7..ac8a75a` against spec §5 and the brief's boundary. No critical or high findings.

| # | Severity | Finding | What became of it |
|---|---|---|---|
| 1 | low | `POST /api/folders/trash` trashed any non-empty folder and took API keys: a new bulk-destructive path beyond the Archive view's need | Fixed `4a40443`: only folders inside an archive, session-only |
| 2 | low | The extraction worker is a third concurrent worker outside the sidecar's memory budget (`mem_limit` unchanged by decision) | Fixed `a4257cc`: one gate keeps at most two jobs running across both pools |
| 3 | low | One user can keep the single extraction worker busy for everyone | Fixed `965c9b9`: one `pdfimages` per user at a time |
| 4 | low | The skill never says a source is data, not instructions | Fixed `de66eab`: a sentence in step 3 |
| 5 | info | The link preference lets a co-writer quietly retarget a link that matched only an archived file by creating a living namesake | Kept: an allowed exception in the brief, needs write on the vault; documented here |
| 6 | info | `TZ: ${TZ:-UTC}` takes the host's environment | Kept: only archive stamps read local time |
| 7 | info | `pdfimages`' existence pre-check compared names ordinally | Fixed `de66eab`: case-insensitive, so a case-only clash is refused before writing |

Not touched, verified by the reviewer: encryption, accounts (beyond the storage figure), sessions, sharing and roles, OAuth, pairing, WebDAV, Obsidian sync, the export, publishing, `CallerMiddleware`, the sidecar's `mem_limit`. Archive hiding is read only by listing, search and link filters and the API's summaries, never by a permission check.

## 6. Decisions made without Chris

Each with the options seen and the reason; the rules are the brief's (breaks nothing, spirit of the app, no new hole, true to the spec).

| # | Question | Options | Chosen, and why |
|---|---|---|---|
| 1 | How the sidecar addresses an embedded image | page + painting number; a hash | Page + painting number `n` over every paint operation (decorations and duplicates included, solid-colour masks and off-page images not), so names don't change with `--all` (OP 9) |
| 2 | One image per request or batches | one per request; a framed body of several | One per request: no new body format; a page is decoded once per stored image, acceptable since small ones and duplicates aren't fetched (OP 5) |
| 3 | Extraction queue length | 8 (plan); 2 | 2, so the client's time-out (30 s × 3 + 5 s) covers the queue; a full queue answers busy at once |
| 4 | Time budget | 45 s (draft); 20 s + 50 s ceiling | 20 s (like `rg --regions`), listing in its first half, writing stops at a page boundary, hard ceiling 50 s mid-page with what of the page was stored; within typical MCP client time-outs |
| 5 | pdf.js warnings for images over 50 MP | verbosity on and logged; counted | Counted in the job's own worker, every console message swallowed: warnings may quote the file (OP 4) |
| 6 | Long images | 8 MP only; also 16,384 px a side | Both: the server refuses a side over 16,384 (`ImageHeader.MaxSide`); "scaled down from" says so |
| 7 | Images pdf.js can't decode, odd declared sizes | fail the page; list as failed | Listed `failed` with no hash, named "skipped: could not be decoded"; the other images of the page are stored |
| 8 | Images in hidden layers, masks under a transparent fill | leave out; store | Stored, as the analysis counts them: an ingest loses nothing (fidelity) |
| 9 | A transparent image that must become JPEG | black (Skia's default); white | Flattened onto white; PNG keeps alpha |
| 10 | Order of notes on an output line | after the box (plan); as the spec's example | As the spec: "(scaled down from …)" after the pixel size, "(JPEG: PNG over 15 MB)" after the bytes |
| 11 | Images not stored after a stop | file names; page and number | "not stored: p2 #1": the extension is known only once fetched; consistent with `--list` |
| 12 | A turned crop's output | "turned 90°"; "at … of the page turned 270°" | The latter: the numbers count in the turned page, and this says so |
| 13 | Existing-name check | ordinal; ignoring case | Ignoring case (security review): a case-only clash is refused before anything is written |
| 14 | A reader running `pdfimages` | refused at the first write; up front | Up front, before listing or rendering (review): no sidecar work for a call that can only be refused |
| 15 | Concurrent `pdfimages` of one user | allowed; one at a time | One per account (security review), Kind Unavailable: the call is right and only has to wait |
| 16 | Sidecar memory with a third worker | raise `mem_limit`; keep two jobs at a time | A gate keeps at most two jobs across both pools; `mem_limit` stays (spec), its comment updated |
| 17 | Links from an archived note | prefer living candidates everywhere; only for living sources | Only for living sources: an archived note keeps leading to its archived siblings, and archiving a folder rewrites none of its inner links |
| 18 | Exact links (`/…`) and case | drop the guard; keep it | Kept: an exact link never resolves to a path that differs in case |
| 19 | `archive` on a vault root | point to rm; own text | Own text ("a vault can't be archived, only what is in it"): rm doesn't remove vaults |
| 20 | The mover's messages for `archive` | rewrite "mv:" in the text; a command parameter | A command parameter on internal `NoteMover` overloads, and an exact mode that never moves into a folder that appeared at the target |
| 21 | `archive` on a folder's `.vaultkeep` | explicit message; as everywhere | As everywhere in the shell: markers can't be typed (`TryParsePath`) |
| 22 | `help archive` (a command and a topic) | topic only; command only; both | Both: the command's help, then the topic; `archive --help` points to the topic |
| 23 | `du` | gain `--archive`; unchanged | Unchanged: it is not hidden (spec) and lists `_archive/` as its own line; its usage line and tests stay |
| 24 | Hints when only archived notes would have answered | silence; a hint | A runnable hint (`backlinks`, `tags`, `rg --tag`, `rg`'s no-match, `semsearch`): "nothing found" must not read as "nothing there"; capped and cancellable |
| 25 | Folder trash endpoint scope | any folder, keys too; archived folders, session only | Archived folders, session only (security review): it exists for the Archive view; `rm` has no `-r` |
| 26 | Restore onto a taken name | trust the tree; check fresh and move exactly | A fresh tree check, then `/api/move` with a new optional `exact` flag, so a folder appearing meanwhile answers 409 and the dialog opens |
| 27 | Archive item dates | stamp only; stamp or last write | The stamp (server time → UTC); other files below `_archive/` are items of their own dated by their last write, after the stamped ones; a stamp in a DST gap has no date |
| 28 | Account storage figure for the archive | compute per request; cache | Cached per owner for the notice's interval, owned vaults only, invalidated with the recount |
| 29 | "Include archive" control | checkbox; switch | A switch, like the search sheet's only other control |
| 30 | Storage notice wording at 100 % | always name the archive; from 10 % | From 10 % of used space, the same rule as at 90 %, and in the refused-write sentence |
| 31 | Deleting from the Archive view | at once; confirm | Confirm (review): a folder goes to the trash with all its files, and the trash restores them one by one |
| 32 | Contents of an archived folder in the view | new endpoint; existing versions listing | The existing versions listing filtered by prefix, as the import does: no new endpoint |
| 33 | Metrics | count extraction as page renders; own kind | Own kind `extract` for durations and failures; not in the delivered-image histograms (their buckets are for the shell's pictures); busy is no failure |
| 34 | Time zone | add tzdata; check | Checked: the `aspnet` image honours `TZ`; nothing added |

## 7. Deviations, surprises, open points

**Deviations from the brief's process**
- Several tasks ran in parallel, some in git worktrees whose commits were cherry-picked onto the branch (Tasks 8–13, 15, fix waves); that is why some commit hashes in reports differ from the branch's.
- Some reviews were one fresh reviewer covering both lenses (spec and quality) in two sections, instead of two reviewers: Tasks 2, 4, 5, 6, 8, 9, 10, 11, 12+13, 14. Tasks 1 and 3 had two reviewers each. Task 16's skill had no separate review; the coordinator read it and the security review covered it. Task 17 (docs) had none.
- Review findings were sometimes folded into the next task's implementer rather than a separate fix agent (Tasks 4→5, 5→6, 6→7, 10→11).
- The Phase 1 and Phase 2 gates were merged into one full gate after Phase 3, run in a worktree while work went on.
- Plan tasks 7 and 12 were reduced by ruling 18 (their registries moved to Tasks 4 and 9).
- The plan's Phase 4 gate check of the web screens was done by the Task 14 reviewer with Playwright against mocked APIs.

**Surprises**
- pdf.js's `getOperatorList` never produces the optimised image operators; `analyse.ts`'s handling of them is also dead code in practice.
- A page's operator list decodes every image of the page at once; the spec's "image by image" memory assumption does not hold for pages with several large images.

**Open points for Chris** (also rows in `docs/superpowers/open-items.md`)
1. Sidecar memory: one page painting several images near 50 MP can take a worker past 1 GB (pdf.js decodes all of a page's images to build its operator list), the same exposure `pdf`'s analysis has today. The gate keeps two jobs at a time, `mem_limit` is unchanged. Decide whether extraction should get a lower per-image cap or a per-page budget.
2. The sidecar does not drop a queued job whose client has gone away; with one extraction worker and a queue of two, abandoned jobs can delay the next `pdfimages` by up to a minute and a half.
3. Deleting a living note now lets `[[Name]]` links fall back to an archived namesake instead of becoming broken, as the spec says ("only when every candidate is archived"); `links --check` does not flag it.
4. Duplicates are found within one `pdfimages` call; a follow-up call ("go on with pages K-") stores a repeated image (a logo) again.
5. `pdfimages`' error stops part way: `VaultUnavailableException` and `VaultReadOnlyException` are server types Core cannot catch, so such a failure midway replaces the partial output with the error (quota is caught).
6. The Obsidian plugin needs nothing for the archive (it sees `_archive/` as an ordinary folder, as intended).

## 8. Hand-test checklist (staging)

Prepare: a staging server on this branch's image, `TZ=Europe/Berlin` in `.env`, a vault `Work`, an MCP client (Claude) connected, a real PDF manual with photos, vector diagrams and a logo on every page (call it `Pump manual.pdf`), and four photos of wild flowers.

1. **End to end (spec §4).** Upload `Pump manual.pdf` into `/Work/Inbox/` (WebDAV or the web app). Ask Claude: "Make notes of the pump manual in Inbox." Pass: it reads `ingest-attachments`, runs `pdf info`, asks once whether a folder or one note, and where.
2. Answer "folder". Pass: `pdfimages "/Work/Inbox/Pump manual.pdf" "/Work/Pump manual/images/"` stores the photos and the logo once (its line lists every page), says how many small icons were left out, and lists each file with its rectangle; no file over 8 MP (`ls /Work/Pump manual/images/` shows sizes).
3. Pass: for the diagrams from `pdf info`, `pdfimages … <page> --crop x,y,w,h ".../images/<name>.png"` stores sharp PNGs named after what they show.
4. Pass: `/Work/Pump manual/` holds an overview and chapter notes; every picture shows in the web app's reader (embeds resolve), each followed by a description; a wiring diagram followed by a table; a `Source:` line linking the PDF.
5. Pass: Claude asks keep/archive/delete once and recommends archive. Answer archive. `archive "/Work/Inbox/Pump manual.pdf"` answers `moved … to /Work/_archive/Inbox/Pump manual 2026-…-… HHmmss.pdf` with Berlin time, and names the notes whose links it rewrote; the Source links open the archived PDF.
6. **`--archive` in sh** (through Claude or the MCP inspector): `ls /Work` has no `_archive/`; `ls --archive /Work` has it; `ls /Work/_archive/Inbox/` lists the PDF. `rg -i pressure /Work` finds the chapter note but not the PDF's pages; `rg --archive -i pressure /Work` finds both. `semsearch "maximum pressure of the pump"` finds the chapter note and no PDF page; with `--archive` the PDF pages appear too.
7. **Web tree.** Pass: the tree of `Work` shows `Pump manual/` and, dimmed at the bottom, `Archive · 1 file · 8 MB` (size as the PDF); check on a phone in light and dark mode.
8. **Archive view.** Tap the entry. Pass: the PDF listed with original folder `Work / Inbox`, the archived date and size; Open shows it.
9. **Restore.** Tap Restore. Pass: the PDF is back at `/Work/Inbox/Pump manual.pdf` (no stamp), the entry disappears from the tree, the Source links point back to Inbox. Archive it again (step 5), then upload another file named `Pump manual.pdf` into Inbox, and Restore: pass: a "Restore as…" dialog asks for another name.
10. **Delete.** Archive a folder (`archive /Work/Pump manual/`, then restore it afterwards if wanted), open the Archive view, Delete the folder: pass: a confirmation names the folder and its file count; after it, the files are in the trash (Trash sheet) and the entry's counts drop.
11. **Search.** In the web search, look for a word only the archived PDF or an archived note holds: pass: nothing; switch on **Include archive**: pass: found.
12. **Storage notice** (if the account has a limit): with the archive at 10 % or more of the used space and use above 90 %, pass: the notice says "Empty the trash or the archive, or ask your admin for more."
13. **Four photos (spec §3.3).** Upload four photos of a wild flower into `/Work/Inbox/`. Ask: "Make one note of these four photos of wild flowers, and look up what you know about the plant." Pass: one note embedding all four, the information of each written out, one question "Do you want to add a description or notes of your own?", the plant named with what comes from where, then the keep/archive/delete question once for all four.
14. **Refusals.** In the web app's console: `pdfimages --list "/Work/_archive/Inbox/…pdf"` and `archive /Work/x.md` are refused as writing commands. A read-only key's agent running `pdfimages` is refused before anything is rendered.
15. **Readers.** As a member with read access to `Work`: the Archive view lists items without Restore/Delete.

## 9. Pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/174, with a row in `docs/superpowers/open-items.md`.
