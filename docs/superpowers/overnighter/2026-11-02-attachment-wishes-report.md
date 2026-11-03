# Overnighter report: the attachment wishes

Branch `feature/attachment-wishes`, from `26c7a2e` to `5ce61a7`, plus this report: 43 commits (`git diff --stat 26c7a2e HEAD`: 57 files changed, 5485 insertions, 196 deletions). Draft pull request: <https://github.com/SCAI-NGO/kumbukumbu/pull/140>. Nothing was merged, tagged, released or deployed, and no version file was touched.

Sources: the brief, the plan and spec, the controller's ledger, one report per task (1 to 12, 13, 13b, 14a to 14d), the phase-gate logs and `git log`. Where a source gives no number, this report says "not recorded". Hashes are those on the branch; commits made in worktrees were cherry-picked and have new hashes (section 3 gives both).

**In short.**

- `img --rotate`, `pdf --rotate`, `img --frame` and `rg --regions` are built, reviewed, and green at every phase gate.
- Regions with labels were tried, measured on held-out figures, failed twice, and were reverted. The eval and the corpus stay (section 7).
- The memory measurement of the composing path (Task 13) found that the spec's remedy, a lower pixel cap, did not hold. The branch therefore adds a frame limit of 10,000, lowers the cap from 16 MP to 4 MP, and counts frames by omggif's own rules (section 8).
- Final whole-branch review: ready for maintainer review, with no critical or important findings. The security boundary of spec §5, the sidecar–server contracts and the complete revert of Task 14 stages B and C hold across the branch. Its 12 minor findings were fixed in one wave, `5ce61a7` ("Tidy the wording and two edges the final review found"), and a scoped re-review found all 12 addressed with no new breakage: the limit of 50 pages is per PDF, not per search; the regions metric counts PDFs; "delivered" means the sidecar answered; a stale line citation; the note's test count; quoted paths in rg's suggested commands; no scan from item 0 per match span; required x, y, w and h in a text box; a typo; and the help paragraph re-flowed. Its triage of the deferred minors: only the typo and the help wrap needed fixing before merge (both done); the rest are fine to defer, with vertical-writing fonts in text boxes named as a known limit for the maintainer.
- The signal at the end of the run is `PushNotification`; the pull request is the record.

---

## 1. Environment and what was installed

| Item | Setting |
|---|---|
| .NET SDK | 10.0.403 (`global.json` says 10.0.100, roll forward); already present |
| Node | 26.13.0, **installed by hand** at `/opt/node-v26.13.0-linux-x64`, with the image's Node 22 binary symlinked over |
| pnpm | 10.32.1, installed with `npm install -g` |
| Docker | 29.9.2. The `dockerd` was **restarted by hand** (`nohup dockerd`) after the container restart at about 00:16 UTC (section 6) |
| MongoDB | the brief's `mongo:7.0` replica-set container `kumbu-test-mongo`, with `--ulimit nofile=20000:20000` and the wiredTiger `file_manager` setting; recreated with `docker rm -fv` before each full run. `KUMBU_TEST_MONGO` pointed at it |
| Testcontainers | `TESTCONTAINERS_RYUK_DISABLED=true` |
| uv | 0.11.32, **already present** |
| LibreOffice | 24.2.7.2, **already present** (needed for the Calc figures) |
| User | the tests ran as root, which skips 2 more tests than the brief's 4 |

**The sidecar image could not be built.** `docker build --build-context root=. -t kumbu-pdf-mem pdf` failed with `npm error code SELF_SIGNED_CERT_IN_CHAIN … request to https://registry.npmjs.org/pnpm failed`: containers can't reach npm through the sandbox's proxy. The memory runs used the Dockerfile's base image `node:26-bookworm-slim` with the built `pdf/dist` and its `node_modules` mounted read-only, under the compose file's limits and hardening (section 8). Whether `docker build` of the shipped image works was therefore **not checked**.

Host cgroups are v1, so there is no `memory.peak`; the peak is `memory.max_usage_in_bytes`.

**Process.** One fresh implementer subagent per task, then a review subagent, with fix rounds where it found something. Task 14 stage A ran in a worktree (`/home/user/kumbu-stageA`, branch `work/stage-a`) in parallel with Task 11, and Task 13 ran in a worktree (`/home/user/kumbu-handson`, branch `work/handson`) in parallel with stage B. Both were cherry-picked onto the branch.

---

## 2. Task 0: planned against measured

| | Planned (CI on `8a7c64e`) | Measured on `26c7a2e` |
|---|---|---|
| Time | about 15 minutes | about 15 minutes: started 20:20 UTC, installs until 20:23, verification 20:23 to 20:35 (`dotnet test` itself 8 min 38 s) |
| `dotnet test` | total 3264, succeeded 3260, skipped 4, failed 0 | total **3408**, succeeded **3402**, skipped **6**, failed 0 |
| web | 84 files, 1346 tests | **85** files, **1368** tests |
| sidecar | 6 files, 78 tests | 6 files, 78 tests |
| builds | all succeed | all exit 0 |

**Why the numbers differ.**

- The brief says the branch's own commits hold documents only, so `git diff --stat 8a7c64e HEAD -- src tests web pdf tools monitoring` should print nothing. It does not: the branch also holds #136 (`cd97378`), which changes `src`, `tests` and `web`. That accounts for the extra 144 tests in `dotnet test` and the extra test file and 22 tests in the web suite. The ledger's ruling: the baseline is measured on `26c7a2e`, and CI's numbers are a reference only.
- The 2 extra skips are `SeedScannerTests.Skips_unreadable_files_with_a_warning` and `ModelFilesTests.AReadOnlyModelFolderTurnsSearchByMeaningOffInsteadOfStoppingTheServer`. Both skip themselves under root ("Running with privileges that read a mode-000 file anyway (root)"; "… write to a read-only folder anyway (root)"). The 4 expected skips are the model and TensorX tests.

Log: `verify-task0.log`.

### Phase gates

Each gate is the full verification: `dotnet restore --locked-mode`, Release build and test, then web install, test and build, then sidecar install, test and build.

| Gate | `dotnet test` (total / succeeded / skipped / failed) | web | sidecar | Builds |
|---|---|---|---|---|
| Task 0, `26c7a2e` | 3408 / 3402 / 6 / 0 | 85 files / 1368 | 6 / 78 | exit 0 |
| Phase 1, `5a55cfa` | 3473 / 3467 / 6 / 0 | 85 / 1368 | 6 / 95 | exit 0 |
| Phase 2, `1b2051a` | 3514 / 3508 / 6 / 0 | 85 / 1368 | 6 / 121 | exit 0 |
| Phase 3, `8c0395e`, first run | 3589 / 3582 / 6 / **1** | 85 / 1368 | 8 / 139 | exit 0 (dotnet exit 2) |
| Phase 3, `8c0395e`, rerun on a fresh mongo | 3589 / 3583 / 6 / 0 | 85 / 1368 | 8 / 139 | exit 0 |
| Final, `a00a28a` | 3589 / 3583 / 6 / 0 | 85 / 1368 | 8 / 146 | exit 0 |
| Final after the review's fixes, `5ce61a7` | 3595 / 3589 / 6 / 0 | 85 / 1368 | 8 / 146 | exit 0 |

`dotnet test` took 7 min 24 s (phase 3, first run), 7 min 43 s (rerun) and 8 min 51 s (final). The web count did not move: this branch touches no web code, and `web/src/api/schema.d.ts` is unchanged.

The one failure at the phase 3 gate is in section 6 (flaky `PairingTests`).

---

## 3. Per task: commits and test output

Commands in the filters are Microsoft.Testing.Platform's: `dotnet test --project tests/<Proj> -- --filter-class "*Name"`. Sidecar tests: `pnpm -C pdf exec vitest run …` or `pnpm -C pdf test` (type check, then tests). Red means the new tests failed for the right reason. A compile error counts as red only for a type or member the task creates, and a pin (a test of unchanged behaviour) passes at red.

### Task 1: the sidecar turns pictures

Commits `c2f3ad2` Turn pictures in the sidecar; `d4fdab5` Strengthen the test that a turned image stays opaque. Review clean.

- Red, `pnpm -C pdf exec vitest run test/image.test.ts test/render.test.ts test/server.test.ts`: `Tests  13 failed | 46 passed (59)`. The 4 pins passed. Failures were assertions, for example `90: expected [ 3, 2 ] to deeply equal [ 2, 3 ]`.
- Green, same command: `Test Files  3 passed (3)`, `Tests  59 passed (59)`. `pnpm -C pdf test`: 6 files, 95 tests.
- Decision 16 held: no fallback canvas was needed, because the opaque test passed straight away.
- `pdf/test/workers/stub.mjs` was changed outside the brief's file list (its `page` answer gained `rotate: 0`), because the `!== 0` header check would otherwise have thrown in the server-limits tests.
- Deferred minors: `turn()` in `image.ts` falls through to 270 for any other value (the server whitelists); `render.test.ts` relies on A4 portrait without saying so.

### Task 2: the server asks for a turn and checks it happened

Commits `349ad16` Let the server ask for a turn and check that it happened; `d15dc29` Make the angle test name every refused angle. Review clean.

- Red, `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*RotationTests"`: build failed, `CS0103 Rotation does not exist` (3 errors), the type the task creates. The server tests have no separate red: the echo check was written with the signature change.
- Green: Core (Rotation, Img, PdfCommand, RgPdf, SemsearchPdf) total 129, failed 0; Server (PdfSidecar, AttachmentPixels, PdfTool, ImgTool) total 72, failed 0.
- `RenamedOwnerTests.cs` was also updated (the brief's `git add` omitted it; commit amended before pushing).
- Deferred minors: `A_picture_not_turned_as_asked_couldnt_be_rendered` is weak (`RefusingSidecar` throws regardless); the echo check reads `FirstOrDefault` of `X-Kumbu-Rotate`, so a duplicated header is not refused.

### Task 3: `img --rotate`

Commit `898f61d` Let img turn a picture. Review clean.

- Red, `… --filter-class "*ImgTests"`: first compile errors only for `PictureTurned`, `TurnCommand`, `ImgRefusal.BadRotate`; after adding just those, `total: 47, failed: 15, succeeded: 32`. All 15 new non-pin tests failed (most with `img: unrecognized option '--rotate'`); the pin `Without_rotate_the_text_block_is_as_it_was` passed.
- Green: ImgTests + HelpTests `total 65, failed 0`; server AttachmentMetrics, LabelGuard, Dashboard, ImgTool `total 21, failed 0`; the whole Core suite `total 1403, failed 0, succeeded 1401, skipped 2`.
- Adds `kumbu_pictures_turned_total{command}`, the reason `bad_rotate`, and dashboard panel 49. The panel opens a new line in the Attachments row, so the rows below moved down by 8.
- Deferred minor: the page variant of `PictureText.Turned(…,"pt")` was untested until Task 4 (Task 4 covers it).

### Task 4: `pdf --rotate`

Commit `5a55cfa` Let pdf turn the pages it shows. Review clean.

- Red, `… --filter-class "*PdfCommandTests"`: `total: 68, failed: 20, succeeded: 48`. Every new test failed (mostly `pdf: unrecognized option '--rotate'`), no old test failed, the pin passed.
- Green: `… "*PdfCommandTests" "*HelpTests"` `total: 86, failed: 0`; server PdfTool, AttachmentMetrics, LabelGuard, Dashboard `total: 21, failed: 0`.
- Two deviations are in section 5: the help line-length guard 106 to 107, and the synopsis `[--rotate D]`.

### Task 5: the sidecar composes a GIF's frames

Commits `a6566e3` Compose the frames of a GIF in the sidecar; `7df7601` Refuse a frame of a GIF with no image in it as not an image (fix round 1). Review approved after one fix round.

- Licence and pin: `omggif@1.0.10`, MIT, no dependencies, integrity matching the brief; `@types/omggif@1.0.5`, MIT. The tarball has no `LICENSE`, so `THIRD-PARTY-NOTICES.md` carries lines 1 to 21 of `omggif.js` (checked with `diff`).
- `pnpm -C pdf view …` does not work in pnpm 10.32.1 (`ERR_PNPM_RECURSIVE_EXEC_FIRST_FAIL`), so the commands were run from `pdf/`.
- `@napi-rs/canvas`'s `new ImageData(buffer, …)` does not copy (checked: writing into the buffer afterwards showed in `.data`).
- Red: `Test Files 2 failed (2)`, `Tests 22 failed | 40 passed (62)`. The 3 pins passed.
- Green: `Tests 62 passed (62)`; `pnpm -C pdf test` 6 files, 120 tests.
- Fix round 1 (the reviewer's finding): a GIF cut after its palette gives zero frames without a throw, and `--frame 1` answered `no_such_frame` where the plain path says `not_image`. Red `Tests 1 failed | 62 passed (63)`; green 63; `pnpm -C pdf test` 121.
- Deferred: a crafted LZW stream may loop until the worker's 10 s kill (Task 13 could test it); the plain path builds `GifReader` after `loadImage` (Task 13 should measure it, and did). A colour index past the palette reads later file bytes; the reviewer judged this harmless (same file, sandbox).

### Task 6: the server carries frames

Commit `aa6cb24` Carry GIF frames from the sidecar to the commands (made as `59fb561`; the controller amended the trailer to the brief's before the push). Review clean.

- **No red run.** The implementer wrote code and tests in one pass; the mutations stand in for it.
- Green: Server PdfSidecar, AttachmentPixels, AttachmentMetrics, LabelGuard `total 97, failed 0`; Core Img, PdfCommand, RgPdf, SemsearchPdf `total 151, failed 0`; `dotnet build Kumbu.slnx` 0 warnings, 0 errors.
- Deferred: the `Img` mapping of `BadFrame` is untested end to end (Task 7 covers it); any 413 on a framed request reads as "canvas too large".

### Task 7: `img --frame`

Commit `1b2051a` Let img show any frame of a GIF. Review clean.

- Red (record `ImgFrameShown` declared only): `total 131, succeeded 116, failed 15` over ImgTests and PdfCommandTests. Two pins passed (`Pdf_has_no_frame`, `A_plain_gif_of_unknown_count_says_todays_sentence`).
- Green: Core `total 149, failed 0`; Server `total 26, failed 0`.
- Adds `kumbu_img_frames_total` and panel 50.
- Deferred: no space-separated `--frame -1` case; no Img-side test of the 16 MP sentence.

### Task 8: `Crop.Enclosing`

Commits `dcd551a` Keep pdf info's regions within the page; `4283ed4` Cut an enclosing region at the page's origin and count only regions that print (fix round 1). Review clean after the fix.

- Red: build failed, `CS0117 'Crop' does not contain a definition for 'Enclosing'`.
- Green: Core CropTests + PdfCommandTests `total 95, failed 0`; Server PdfToolTests `total 5, failed 0`. Fix round: red `98 total, 3 failed`, then Core 98/98 and PdfToolTests 5/5.
- As the plan's decision 10 says, the expected `20,31,40` became `20,31,41` on purpose.
- Fix round 1 (3 items): a negative origin is clamped instead of refused; the cap of 8 regions is applied after dropping nulls; a test for the null drop.

### Task 9: the sidecar's `/text-boxes`

Commits `276f344` Let the sidecar say where a page's text lies; `1d41cfd` Check that the sidecar's text loses trailing white space as the analysis's does. Review clean.

- Red, `pnpm -C pdf exec vitest run test/boxes.test.ts test/server.test.ts`: `Test Files 2 failed (2)`, `Tests 2 failed | 21 passed (23)` (module missing; route answers 404).
- Green: 4 files, 63 tests; `pnpm -C pdf test` 7 files, 136 tests, also after the second commit.
- Deferred: rounding to 0.1 lets `x + w` overshoot the page by up to 0.1, and a sliver may round to `w` 0 with an entry. Vertical-writing fonts: see section 9.

### Task 10: the server asks for text boxes and checks the answer

Commits `8ee547f` Ask the sidecar for text boxes, and check the answer; `3ac6f56` Check a text-boxes answer over the limit that is valid JSON; `042a4c8` Refuse null entries and missing offsets in sidecar answers, and guard the page list asked for (fix round 1). Review clean after the fix.

- Red: build failed on `CS0246 type PageTextBoxes could not be found`, the type the task creates.
- Green: Server `total 101, failed 0`; Core `total 169, failed 0`. After the fix round: red 11 failures, then green `113 total, failed 0`.
- Fix round 1 (3 items), which also closed a null page / null region hole in `ParseAnalysis` that predates the branch.
- Deferred: no `AttachmentPixels`-level `no_such_page` test for text boxes.

### Task 11: `rg --regions`

Commit `85812ed` Let rg say where in a PDF its matches lie. Review clean.

- Red: compile error `CS0117 'Rg' does not contain a definition for 'RegionBudget'` (a member the task creates). To see the others fail, the implementer reran with one property temporarily removed: `failed: 34, succeeded: 29` (28 old tests and the pin passed). Server: 2 failed.
- Green: `total 209, failed 0` (Core, five classes); `total 26, failed 0` (Server).
- Deferred: `Crop.Enclosing` may widen a crop by 1 pt at float edges (harmless); a "PDF's" typo in two summaries; the `help pdf` rewrap is ragged; refusal order when piped; one non-Pixels exception test case.

### Task 12: documents

Commit `edc5ff4` Document what clients asked for, and add the playbook's steps. Review clean. No red test (plan-mandated, docs).

- `uv run … --seed 7`: exit 0; upload lists `sideways.png`; key line "Colour of frame 2: **green**" found.
- Playbook steps 26 and 27 sit at the end of their sections, so the old numbers are unchanged (deliberately out of positional order). `docs/concept.md:105`, the metrics list, was not updated.
- Process deviation: see section 4, Task 12.

### Phase 3, the stage A cherry-picks

`0d15f64` Measure regions with a corpus of generated figures (made as `c4d7271`); `8c0395e` Check the baseline's controls on every run, and size the side-by-side charts as the playbook does (made as `204e4cc`).

- Built in the worktree with no product code. Seeds 1 to 5: 135 figures (45 per generator), `complete 0 %, clean 100 %`, which is the baseline.
- `pnpm -C pdf test`: 8 files, 137 tests, then 139 after the fix round.
- Fix round 1 (4 items): the baseline controls' body-line check on every run; the baseline guarded by `RULES_VERSION` and `--force`; the side-by-side size against 11 %; minors. Calc: 12 figures with no region (looked at in stage B).
- `eval.test.ts`: 1 test, then 3. Deliberate break with no listed mutation: `COMPLETE_AREA` 0.9 to 0.8 failed `judges a figure by token…` with `expected [] to deeply equal [ 'label outside' ]`.

### Task 13: hands-on, through MCP

A first attempt was lost to the container restart, with nothing committed. The second ran in `work/handson`. Commits, with both hashes:

| Branch hash | Made as | Subject |
|---|---|---|
| `ebb134c` | `710418c` | Put img's --frame after --crop in its synopsis, as the design has it |
| `4fa039b` | `d4797d7` | Note the hands-on test of the attachment wishes |
| `769e11c` | `7b3148a` | Note the measured memory of the composing path |
| `7437d79` | `9987dc0` | Refuse --frame on GIFs of more frames than the sidecar can hold |
| `aaeff3c` | `b776334` | Note the memory measured after the limit on frames |
| `a5a5301` | `dddb5c7` | Lower the frame cap to what the sidecar's memory holds |
| `de72f8b` | `4a79f25` | Count a GIF's frames by omggif's own rules for extensions |
| `891a2f0` | `4de23b0` | Note the memory measured at the 4 MP cap, and what a still image needs |
| `a00a28a` | (task 13b, on the branch) | Pin how the frame count reads an application extension |

- **The one bug found by hand** (`ebb134c`): `help img` and `Usage` had `[--frame N] [--crop x,y,w,h]`, but spec §1.5 and `docs/concept.md:249` have `[--crop x,y,w,h] [--frame N]` (Task 7 had inserted `--frame` in parse order). Red, ImgTests: `total 62, failed 2, succeeded 60` (`The_help_names_rotate`, `The_help_names_frame`); green with HelpTests `total 80, failed 0`.
- Every sentence of spec §1.4, §2.3, §3.1 and §3.4 matched as written. Not reachable by hand and covered by tests instead: the "older analysis" notice of §3.4, and a GIF whose frame count is unknown. No spec sentence changed.
- `notes.md` could not go in through `/api/files` (400: it is a note), so it went in through the MCP `write` tool.
- The output format for the `pdf` example is PNG where the spec's example says JPEG (the encoder's choice for that content).
- With the sidecar stopped, `rg --regions` gave "No regions: PDF rendering is unavailable right now. The matches are all here." above the three matches; `img` gave "image rendering is unavailable right now; try again later"; after the restart it worked again.
- The memory runs and the three rulings they led to are in section 8. The fix round after the review (the 500,000-frame probe, `4a79f25`) is in sections 4 and 6.
- **Task 13b** (`a00a28a`): one new test, "skips an application extension as omggif does, whatever its size or name" (three crafted GIFs of 20,000 frames). `pnpm -C pdf test`: 8 files, 146 tests.
- The note `docs/notes-attachment-wishes-hands-on.md` holds every worked example with real output, the refusals, the metrics seen (`kumbu_pictures_turned_total{command=img|pdf}`, `kumbu_img_frames_total`, `kumbu_rg_regions_total{outcome=delivered}`, `kumbu_img_refusals_total{reason=bad_rotate|bad_frame}`), and a checklist for the maintainer. A search of the note for the dev token found nothing.
- Cleanup: stack stopped, `kumbu-pdf-mem` removed, the token file deleted. The database `kumbu-handson` is left in `kumbu-test-mongo` (harmless).

### Task 14: regions with labels

| Stage | Commit(s) | What |
|---|---|---|
| A, corpus and eval | `0d15f64`, `8c0395e` | see above |
| B, the rule | `2b72e26` | seeds 1 to 5: 48.9 % (matplotlib 93.3, pdf-lib 53.3, Calc 0) |
| C, review fixes and tuning | `3b9dd06`, `b4de89a`, `0c6309a`, `8dfdddb` | seeds 1 to 5: 87.4 % |
| D, revert and record | `3416997`, `6c02b03`, `3e6e1b4`, `db33a9d`, `8f8e29d`, `1c68256` | the verdict below |

- Stage B was written by a second agent: the first was lost to the container restart with only uncommitted work. The WIP was reviewed and kept in its C# part and in the `analyse.ts` change. The rule itself was found short on one real defect: thin path boxes (axis lines, gridlines) were never candidates. Red `Tests  8 failed | 30 passed (38)`; green `pnpm -C pdf test` 8 files, 150 tests, and `dotnet … PdfSidecarTests PdfAnalysisStoreTests AttachmentPixelsTests RekeyTests` `total 208, failed 0`; Core `total 92, failed 0`.
- Stage B's review: **Needs fixes**. (1) nearness was measured to another chart's grown box; (2) an overlap take-in skipped the `takeChart` checks. Minors: a rule joining two charts accepted as a limit (pinned), characters reaching `classify` unpinned, the 5,000 boundary, `labelled` null shape. Routed into stage C as fix round 1, before any held-out seed was generated. Also deferred: the `withLabels` cost is unbounded in the number of regions (the pool time limit contains it, but the whole analysis would be lost).
- Stage C: fix round red `Tests  2 failed | 24 passed (26)`, C# red `total 19, failed 1`; green `pnpm -C pdf test` 156 tests after round 1 and 158 after round 2.
- Stage D (`1c68256`): the section "Regions with labels: the attempt" in `docs/notes-attachment-wishes-hands-on.md`, and one sentence in `docs/attachments-client-research.md`. The five reverts return `diff 8c0395e..HEAD` for the labels to empty: `RULES_VERSION` is 1, the analyser is `rules.1`, the help sentence is as before. The eval and the corpus scripts stay. A search for "with labels" in `src`, `docs` (outside `docs/superpowers`) and `README.md` finds only the note.

### The small follow-up: `a00a28a`

Task 13b, above. The review of Task 13's fix found that the polyglot test did not pin the "size not 11" rule nor `b[p+16]===0`; that was out of scope for the fix round and became this one small commit.

---

## 4. Every mutation, individually

Each mutation was applied alone, run against the tests named, and restored (`git checkout -- <file>`), with the tree clean afterwards. "Failed" is whether a named test failed as expected.

### Task 1 (`pnpm -C pdf exec vitest run test/image.test.ts test/render.test.ts test/server.test.ts`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | `image.ts`: swap the 90 and 270 matrices | yes | turns an image by 90, 180 and 270 exact to the pixel; crop in turned image; EXIF + turn |
| 1b | `originalWidth: tw, originalHeight: th` | yes | reports the size before the turn as the original; take-rotate on both routes |
| 1c | `ctx.translate(0.5, 0.5)` after the turn | **first try: the opaque test did not fail** | Skia does not anti-alias an image's edges, so a half image pixel at a down-scale (1000 × 700 at 0.756) is invisible. **Needed a strengthening commit** (`d4fdab5`: a 640 × 480 image, rendered at scale 1). Retry failed the opaque test and the exact-pixel test; a half-output-pixel variant failed the same two |
| 2 | `inside(region, ow, oh)` (unturned size) | yes (8) | refuses a crop outside the turned image that fits the unturned one |
| 3 | `render.ts`: `rotation = rotate` | yes | turns a page with its own /Rotate on top of it; two more |
| 4 | `pageWidth: round1(shown.width)` | yes | reports the page's size without the turn; three more |
| 5 | `server.ts`: accept any integer | yes | refuses rotate other than 90, 180 or 270 |
| 6 | drop the `X-Kumbu-Rotate` spread | yes | takes rotate on both render routes and answers `X-Kumbu-Rotate` |

### Task 2 (`PdfSidecarTests`, `RotationTests`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | echo check skipped | yes (5) | `An_answer_without_the_echo_is_refused` and four more |
| 2 | absent echo accepted | yes | `An_answer_without_the_echo_is_refused` |
| 3 | check only when a turn was asked | yes | `An_answer_turned_unasked_is_refused` |
| 4 | `&rotate=` left out | yes | `A_turn_goes_into_the_query` |
| 5 | `Rotation` accepts 0 | **first try: `Only_the_three_angles_parse` did not fail** | the refused values had been split into another test. **Needed a strengthening commit** (`d15dc29`: one theory with every angle). Retry failed for all 8 refused values |

### Task 3 (`ImgTests`; mutation 5 on `AttachmentMetricsTests`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | share always divided by `OriginalWidth` | yes | `The_text_block_names_the_turn` |
| 2 | crop checked against the unturned size | yes | both turned-crop tests |
| 3 | `--rotate` parsed after the attachment lookup | yes | `A_bad_rotate_is_refused_before_the_path_is_opened` |
| 4 | the `With --rotate` line dropped | yes | three tests |
| 5 | `bad_rotate` label removed | yes | `A_refused_rotate_has_its_own_reason` |

### Task 4 (`PdfCommandTests`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | `{turn}` removed from the `Stop` line | yes (3) | |
| 2 | `\|\| parsed.Has("rotate")` removed from the info check | yes (2) | |
| 3 | refusal without pictures removed | yes (3) | |
| 4 | `CropAsync` compares with the unturned size | yes (4) | |
| 5 | `WithoutAnalysisAsync` passes 0 | yes (1) | |

### Task 5 (`pnpm -C pdf exec vitest run test/image.test.ts test/server.test.ts`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | rectangle check removed | yes (3) | the run took several minutes: without the check omggif allocates a 3.6 GB index stream and decodes 3.6 × 10⁹ indices synchronously, which vitest's time-out cannot interrupt (in the sidecar the worker's 10 s would kill it) |
| 1b | width only | yes | past-the-canvas's-height test |
| 1c | height only | yes | past-the-canvas's-width test |
| 2 | disposal 2 ignored | yes | two tests |
| 3 | disposal 3 ignored | yes | |
| 4 | blit only frame N | yes | five tests |
| 5 | frame N's own disposal applied | yes | four tests |
| 6 | palette check removed | yes | `refuses a frame with no palette` |
| 6b | 16 MP cap removed | yes | `refuses to compose a GIF over 16 megapixels` |
| 6c | `console.log` not silenced | yes | `logs nothing while decoding a frame with a short stream` |
| 7 | `no_such_frame` dropped from `STATUS` | yes | `answers no_such_frame with 422` |
| 8 | `frames` rethrows | yes | three tests |
| fix | `if (count === 0) throw notImage();` removed | yes | `Tests 1 failed \| 62 passed (63)` |

### Task 6 (`PdfSidecarTests`, `AttachmentPixelsTests`, `AttachmentMetricsTests`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | frame echo check skipped | yes (6) | |
| 2 | count 0 accepted | yes | `A_malformed_count_of_frames_is_refused(frames: "0")` |
| 3 | `no_such_frame` removed from `Codes` | yes | the 422 case |
| 4 | removed from `IsRequestMistake` | yes (2) | |
| 5 | `bad_frame` label removed | yes | |
| 6 | Timeout with a frame gets today's sentence | yes | `Too_long_with_a_frame_says_finding_it` |

These mutations stood in for the red run that was skipped.

### Task 7 (`ImgTests`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | not-a-GIF check dropped | yes | first attempt (`if (false)`) did not compile; redone as `&& frame < 0` |
| 2 | frame 0 allowed | yes | |
| 3 | note for one frame too | yes | `A_plain_gif_of_one_frame_has_no_note` |
| 4 | note kept with `--frame` | yes (2) | |

### Task 8 (`CropTests`, `PdfCommandTests`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | no cut at the page | yes | |
| 2 | far edges to nearest | yes | |
| 3 | cut at the ceiling of the page | yes | |
| 4 | `InfoLine` back to rounding to nearest | yes | |
| fix 1 | no clamp | yes | both edge tests |
| fix 2 | null filter removed | yes | the new Info test |
| fix 3 | `Take` before the filter | yes | the new Info test |

### Task 9 (`pnpm -C pdf exec vitest run test/boxes.test.ts test/server.test.ts`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | every item treated as horizontal | yes | `gives text drawn at 90 degrees a tall, narrow box` |
| 2 | no clipping | yes (2) | |
| 3 | `end` not clamped | yes | |
| 4 | white space given an entry | yes | |
| 5 | `text` built differently from `analyse` (no `trimEnd`; own untrimmed text) | **first commit: only `keeps offsets…` failed, not `answers the very text analyse stores`** | pdf.js 6 never leaves trailing white space on these fixtures, and `analyse` shares `pageText`. **Needed a strengthening commit** (`1d41cfd`: made-up items ending in white space). After it both variants failed two tests |
| 6 | `MAX_BOX_PAGES = 51` | yes | |
| 7 | items limit removed | yes | |

### Task 10 (`PdfSidecarTests`, `AttachmentPixelsTests`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | pages-as-asked check dropped | yes | |
| 2 | ascending check dropped | yes | |
| 3 | end-within-text check dropped | yes | |
| 4 | finite check dropped | yes | |
| 5 | read without the size limit | **first attempt hung** on the endless stream (stopped, restored) | **Needed a strengthening commit** (`3ac6f56`: valid JSON padded past 16 MiB). With the limit raised to 64 MiB, `Text_boxes_over_16_MiB_are_refused` then failed |
| fix | null page; null item; `at`/`end` defaulting to 0; page-list guard removed; `ParseAnalysis` null page; null region | yes, each | one at a time, all five guard cases and the named cases failed |

### Task 11 (`RgPdfTests`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | region after the text | yes (19) | `A_match_gets_its_region_before_the_text` |
| 2 | union per line, not per match | yes (5) | |
| 3 | no text comparison | yes | `true` broke the build (`CS0162`, warnings are errors); the mutation compared `boxes.Text` with itself instead |
| 4 | no growth | yes (18) | |
| 5 | offset without `+ 1` | yes (2) | |
| 6 | held lines dropped in the catch | yes | `A_failure_of_the_text_part_way_keeps_the_lines_held` |
| 7 | keep asking after a failure | yes (2) | |
| 7b | budget ignored | yes | `Past_the_time_budget_no_further_pdf_is_asked` |
| 8 | no limit of 8 PDFs | yes | |
| 9 | no limit of 50 pages | yes | |
| 10 | no pipe refusal | yes | |

### Task 12

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | `sideways()` left out of `main` in `make_probes.py` | yes | the run's upload lacked `sideways.png`. **Process deviation:** the file was still uncommitted, so `git checkout --` discarded the implementer's uncommitted edits; they were restored from a copy made before the mutation (a stray `/mp.bak` was written to the filesystem root and removed), and the final file verified before the commit. The brief orders the mutation against the committed file |

### Task 13 (hands-on worktree)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| synopsis | `Usage` back to `[--rotate D] [--frame N] [--crop x,y,w,h]` | yes | `The_help_names_rotate`, `The_help_names_frame` (62 / 60) |
| frames 1 | `frames()` without `\|\| walked > MAX_FRAMES` | yes | "gives no count for a GIF of 10,001 frames, and refuses a frame of it" |
| frames 2 | the `--frame` path's `too_large` check removed | yes | same test |
| frames 3 | `if (++n > MAX_FRAMES) break;` to `++n;` | yes | "stops counting once past the limit" |
| frames 4 | server sentence back to the old one | yes | `A_GIF_too_large_for_a_frame_says_so` (20 / 19) |
| cap | cap back to 16,000,000 | yes | "refuses to compose a GIF over 4 megapixels" (1 failed, 46 passed) |
| walker | the `0x21` branch back to the generic walk | yes | "never counts fewer frames than omggif reads…" (1 failed, 47 passed) |

Also for Task 13: a fuzz of 300,000 GIFs found the walker never below omggif's count, and equal on the 88,046 that omggif can read.

### Task 13b (`pnpm -C pdf exec vitest run test/image.test.ts`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| a | `if (netscape) p += 17` | **first version of the test passed** | the crafted tail lacked the chain terminator, so the walk ran off the end and returned undefined. Fixed in the same test, before the commit, by an 18-byte tail (0, then the trailer). Then failed: "a size of 20" |
| b | drop `&& b[p + 16] === 0` | yes | "NETSCAPE2.0 with a byte at +16 that isn't 0" |
| d | always skip 17 | yes | "a size of 11 and another name" |
| (c) | whole netscape test replaced by `b[p] === 0x0b` | not run | vitest startup error from a `sed` artefact; not pursued |

### Task 14 stage A (`pnpm -C pdf exec vitest run test/eval.test.ts`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | `COMPLETE_AREA` 0.9 to 0.8 | yes | deliberate break (none was listed). File untracked, restored with `sed` and checked with `grep` |
| fix 1 | choose `regions[0]` instead of the nearest | yes | `gives each of two figures the region nearest its labels` |
| fix 2 | drop the outside-the-span check | yes | `judges a figure by token…` |

### Task 14 stage B (`pnpm -C pdf exec vitest run test/classify.test.ts`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| 1 | blockers ignored | yes | a long caption stops the growth. A first form (drop the check in `grow`) left a variable unread, TS stopped it at startup |
| 2 | no chaining | yes (4) | a first form left `changed` unread (TS6133), replaced |
| 3 | no "closer to another region" check | yes | |
| 4 | grow a full page's image too | yes | |
| 5 | limit of 5,000 removed | yes | |
| 6 | growth replaces the tight box | yes (8) | including `no fixture changes its page type or tight region` |
| 7 | no thin marks (own) | yes | the axis-lines test |
| 8 | refuse every overlap (own) | yes (2) | |

### Task 14 stage C (`classify.test.ts`)

| # | Mutation | Failed | Notes |
|---|---|---|---|
| review 1 | third-chart check in `grow` dropped | yes | only the defect-2 test |
| review 2 | grown boxes again in `takeLabel` | yes | only the defect-1 test |
| round 1 a | no blockers | yes | "a long caption stops the growth" |
| round 1 b | no thin marks | yes | the axis-lines test and "two charts with their own axes … stay apart" |
| round 1 c | current boxes instead of round-start | yes | "a label goes to the nearer chart" |
| round 2 a | no reach-across check | yes | the blocker pin |
| round 2 b | `ROW_GAP_PT` replaced by `LABEL_GAP_PT` | yes | "a long caption stops the growth" and the pin |
| round 2 c | the row's own centre blocking | yes | the row test |

---

## 5. Deviations and the calls made

### The plan's 16 decisions

None was overruled by the maintainer's text; each was kept. Two numbers inside the spec corrections the plan lists changed because of what was measured (section 8). The evidence:

| # | Decision | Outcome |
|---|---|---|
| 1 | Text boxes clipped to the page in the sidecar | **Kept** (Task 9; mutation 2). pdf.js already drops glyphs wholly outside the CropBox, so the clip is also tested directly (section 6) |
| 2 | `end` clamped to the trimmed text | **Kept** (Task 9; mutation 3, and the strengthening commit `1d41cfd`) |
| 3 | GIF tests built with `GifWriter`, not `GifEncoder` | **Kept** (Task 5). The first-frame test stays on `GifEncoder` |
| 4 | `@types/omggif` 1.0.5 as a dev dependency | **Kept** (Task 5) |
| 5 | Licence text from the header of `omggif.js` | **Kept** (Task 5; `diff` identical) |
| 6 | `no_such_frame` is a request mistake | **Kept** (Task 6; mutation 4) |
| 7 | A frame asked of a non-GIF, or of a frame with no palette, is `not_image` | **Kept** (Task 5; mutations 6, and the fix round 1 for zero frames) |
| 8 | Any `TextBoxesAsync` failure gets "No regions", no further PDF is asked; 20 s budget | **Kept** (Task 11; mutations 7 and 7b; hands-on with the sidecar stopped) |
| 9 | "pdf /Work/paper.pdf 60" names the first page with matches that got no region | **Kept** (Task 11 names the 51st page with matches; hands-on: "use -m, or look at one page: pdf /Demo/Long/long.pdf 51.") |
| 10 | `pdf info` regions may change by a point | **Kept** (Task 8; `20,31,40` to `20,31,41`, said so in the commit) |
| 11 | The spec's `crop=` examples don't follow its own rule; the rule wins | **Kept.** Tests assert the rule, and the hands-on note quotes the real outputs (`crop=68,321,162,19`) |
| 12 | pdf-lib figures made by a Node script | **Kept** (stage A: `make_figure_pdfs.py` calls `pnpm -C pdf eval:figures`) |
| 13 | `/text-boxes` takes strictly ascending, distinct pages | **Kept** (Task 9, and the client-side guard added in Task 10's fix round) |
| 14 | The eval reads a per-figure manifest | **Kept** (stage A; the manifest is per PDF with a `figures` array, since two figures can share a page) |
| 15 | Page types and tight regions compared on seeds 1 to 5 and the fixtures | **Kept.** The held-out runs say "Baseline (rules 1, seeds 1,2,3,4,5 and the fixtures): 168 pages compared"; the body-line check runs on the held-out controls as well |
| 16 | A rotation fallback stays at the size of the step, with no full-size canvas | **Kept.** No fallback was needed: the opaque test passed straight away (Task 1) |

Not among the 16, but recorded by the plan as a spec correction: §2.2's "the composing path is capped at 16 MP". **Overruled by what was measured**: the cap is now 4 MP (ruling 12 below).

### Every "Ruling:" line of the ledger, in order

Each states what it costs if wrong, as the ledger does.

1. **Task 0's diff-stat expectation is wrong.** The branch starts at `cd97378` (#136), which changes `src`, `tests` and `web`. The baseline is measured on `26c7a2e`, and CI's numbers are a reference. Costs nothing if wrong.
2. **Pushes go to `feature/attachment-wishes`**, as the brief demands, with a mirror to the session branch `claude/attachment-wishes-brief-19tz7t` at the end. Costs nothing if wrong. The mirror is pushed together with this report.
3. **Node 26.13.0 installed by hand** at `/opt/node-v26.13.0-linux-x64`, with the Node 22 binary symlinked over; pnpm 10.32.1 via `npm -g`. The ledger records no cost; it changes the machine only.
4. **README's metrics prose** (around line 446) gets the new metrics in Task 12. The spec said the README needs nothing (no new variable or service), but the maintainer's preference is an up-to-date README. Costs a few lines if wrong.
5. **`pdf` synopsis shows `[--rotate D]`, not `[--rotate 90|180|270]`** (the longer form makes the first line 111 characters); `PageSize` is unchanged because `PictureText.For` already adds the turn, which would otherwise print twice. Cost: cosmetic. The help line-length guard went from 106 to 107, since three lines of the spec's own §1.5 block are 107 characters.
6. **`Crop.Enclosing` clamps a negative origin to 0** (the brief says null only if nothing is left), so `rg`'s grown boxes at a margin keep their region. If wrong: a region starting at 0 is a fraction larger than the text.
7. **Task 14 stage A runs in a worktree, in parallel with Task 11**, and is cherry-picked afterwards (disjoint files, and the night's time budget). If wrong: a cherry-pick conflict in `pdf/package.json`. There was none.
8. **Stage B may let a figure's `with labels` box take in neighbouring drawing regions of the same chart** (gap at most `LABEL_GAP_PT`, not a full-page image, never crossing a blocker or a region closer to another chart); tight regions are unchanged. Grouped bar charts split into one tight region per group, which would cap a labels-only rule at about 47 to 60 % per generator, and spec §4.2 allows a position-only change of the rule's shape. If wrong: merged boxes might take in a neighbour chart, which the "clean" condition measures.
9. **`RegionOutcome` (Task 11):** `Delivered` is the sidecar answered (even if some pages differed), `Limited` is past 8 PDFs or 50 pages, `Unavailable` is any failure or past the budget. The spec is silent. If wrong: the metric's meaning.
10. **Task 13's hands-on runs in a worktree (`work/handson` at `8c0395e`), in parallel with stage B in the main tree**, so the hands-on exercises the shipped items without stage B's in-flight edits. If wrong: cherry-pick conflicts. There were none.
11. **A frame limit of 10,000 (`MAX_FRAMES`)**, from a cheap block walk before `GifReader` is built. Over it, the plain path's count is unknown (today's sentence) and `--frame` is refused as `too_large`, with the sentence "… is too large for --frame (at most 16 megapixels and 10,000 frames); img without --frame shows its first frame". The cap stayed at 16 MP at that point. Taken because the brief's security rule ("lowers if it doesn't hold") requires the composing path to hold, and a frame cap is the safe direction with no parser beyond a block walk. If wrong: GIFs of more than 10,000 frames lose `--frame` and the count note.
12. **`MAX_FRAME_PIXELS` lowered to 4 MP** (8 MP held with only 2 MiB of margin), and measured for 120 s, plus one sustained plain-path run on a large still image to see whether the residual climb predates this branch. If wrong: GIFs over 4 MP lose `--frame`.
13. **Stage C partly replaced review fix 1**: labels are measured to each chart's box at the start of the round, instead of to the tight boxes, because side-by-side axis titles went to the wrong chart. Moot after the revert.
14. **The plan and brief keep 16 MP as history** (plans are records); the spec, code, tests and docs say 4 MP. Costs nothing.

### Other calls that are not "Ruling:" lines

- **Stage D reverted all of stage B and C** (five reverts) rather than leaving a rule that failed its bar in the code, so the shipped analyser is `rules.1` as before. The brief says "only with an honest number"; the number was not good enough.
- **The tuning stopped after one allowed round.** Round 1 was tuned on seeds 1 to 5; held-out round 1 (seeds 101 to 110) failed; one more round was allowed, again chosen from seeds 1 to 5's failures only; held-out round 2 (seeds 201 to 210) failed; no third look, and no bar, seed set or corpus was changed.
- **Trailers.** The brief's trailer is `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (constraints file). Task 6's implementer used the session reminder's attribution instead; the controller amended it to the brief's before the push (`59fb561` to `aa6cb24`). The reverts have the brief's trailer.
- **Task 12's file `docs/security-architecture.md:632`** had drifted to 633; the citations were re-checked with `rg -n` and fixed where touched.
- **The review's "polyglot test" gap** in Task 13 (the test did not pin the "size not 11" rule nor `b[p+16]===0`) was out of scope for the fix round and became the one small commit `a00a28a`.

---

## 6. Surprises

1. **The brief's diff-stat expectation is wrong** (section 2): the branch contains #136, so the counts differ from CI's.
2. **A flaky test: `PairingTests`.** At the phase 3 gate the first run failed `PairingTests.A_code_redeems_while_its_session_lives_though_another_of_the_user_ended` (`Assert.NotNull` null at `PairingTests.cs:293`). The test is not touched by this branch; the class was 5/5 green on rerun, and a full rerun on a fresh mongo gave `3589 / 3583 / 6 / 0`. It did not recur at the final verification.
3. **pdf.js 6.4.299 already drops glyphs that lie wholly outside the CropBox**, and drops trailing spaces of drawn text. In `cropBoxTextPage`, "Wholly outside" never reaches the text content; "Across the left edge" comes out as "ss the left edge", starting 5.3 pt left of the CropBox. The clipping and the `end` clamp are therefore also tested directly with made-up items, and the clamp's mutation needed a strengthening commit (section 4, Task 9).
4. **Skia does not anti-alias an image's edges.** Task 1's premise that a half-pixel error would add a false "transparent" note did not hold; the opaque test now catches an error only where it reaches half an output pixel, and the exact-pixel test catches any offset at scale 1.
5. **`omggif`'s NETSCAPE fast path.** The Task 13 review found that `gifFrameCount` followed an application extension whose size is not 11 as a chain of sub-blocks, where omggif skips 17 bytes. A crafted 16.5 MB GIF hid 500,000 frames from the walker (probe: `walker 0, omggif 500000`), restoring the OOM. Fixed in `de72f8b` to omggif's own rules (the same probe then gives `walker 10001 omggif 500000`), pinned in `a00a28a`.
6. **Memory follows the number of frames, not the canvas** (section 8): omggif builds its frame list eagerly (about 120 MiB of RSS for around 650,000 1×1 frames in 16 MiB) and composing a late frame adds about 90 MiB of per-frame garbage.
7. **Memory climbs from round to round** under sustained load, even when every job is bounded. The same climb shows on today's plain path with a 49 MP still image (section 8), so it predates this branch.
8. **A container restart mid-run** (about 00:16 UTC). The Task 13 agent and the stage B agent were lost and the Docker daemon with them. `dockerd` was restarted by hand (`nohup dockerd`) and the mongo container recreated. Task 13 had committed nothing; stage B had uncommitted work in the main tree, saved as patches and reviewed. Evidence left by the first memory attempt: `kumbu-pdf-mem` with `State.OOMKilled=true`, `RestartCount=0`, exit 137 during the 16 MP frame test, which the second attempt reproduced.
9. **The spec's `crop=` examples don't follow its own rule** (plan decision 11): from its JSON example an item at 74.2,300.1,210.3,11.0 grows by 3.3 and gives `crop=70,296,218,19`. The rule wins and the hands-on note quotes the real outputs.
10. **Most "label" failures were not about labels.** Grouped bar charts fall apart into one tight region per group, and small Calc charts give no region at all under rules 1. The rule only widens existing regions, and tight regions may not change, so Calc's ceiling on seeds 1 to 5 was 33 of 45 (73.3 %) before any tuning, under the bar of 80 %. Stage A saw it coming; the held-out failures confirm it (section 7).
11. **Mutation runs that did not behave:** a mutation without the rectangle check ran for several minutes (Task 5); `git checkout --` discarded uncommitted work during Task 12's mutation (restored from a copy); `if (false)` and an unread variable broke compilation in Tasks 7 and 14b; an `Endless` stream hung the Task 10 mutation.
12. **`pnpm -C pdf view …` doesn't work** in pnpm 10.32.1 (`-C` is read as a recursive exec for `view`); the commands were run from `pdf/`.
13. **`notes.md` can't be put through `/api/files`** (400), so the hands-on used the MCP `write` tool.
14. **The sidecar image can't be built here** (containers can't reach npm), which is also why the memory runs used the base image with the built `dist` mounted.

---

## 7. The verdict on regions with labels

**Regions with labels are not reliable enough: on held-out figures they passed 84.4 % (seeds 101–110) and 83.3 % (seeds 201–210) against a bar of 90 %, with Calc at 65.6 % and 60.0 % against 80 %, so they are left out.**

The bars: 90 % overall, 80 % per generator, 70 % per layout, with the page types, tight regions and body lines held to the baseline. The baseline and the body-line check were met in both rounds; the overall bar, the per-generator bar and the per-layout bar were not. The held-out PDFs were run without `--verbose`, and the held-out failures were never opened.

### Held-out round 1: seeds 101 to 110, rules at `0c6309a`

270 figures; judged by labelled box 243, by tight box 1, with no region 26.

| generator | figures | pass | % |
|---|---|---|---|
| matplotlib | 90 | 86 | 95.6 % |
| calc | 90 | 59 | 65.6 % |
| pdf-lib | 90 | 83 | 92.2 % |
| all | 270 | 228 | 84.4 % |

| layout | figures | pass | % |
|---|---|---|---|
| legend-right | 30 | 28 | 93.3 % |
| legend-below | 30 | 29 | 96.7 % |
| side-by-side | 60 | 39 | 65.0 % |
| between-paragraphs | 30 | 27 | 90.0 % |
| two-columns | 30 | 20 | 66.7 % |
| long-caption | 30 | 28 | 93.3 % |
| turned-page | 30 | 29 | 96.7 % |
| turned-labels | 30 | 28 | 93.3 % |

Failures by kind (matplotlib / calc / pdf-lib / all): no region 0 / 23 / 3 / 26; label outside 3 / 2 / 6 / 11; other figure inside 1 / 10 / 2 / 13; the other kinds 0. The eval's own verdict line: `fails (complete 87.0 %, clean 95.2 % of figures)`.

### Held-out round 2: seeds 201 to 210, rules at `8dfdddb`

270 figures; judged by labelled box 239, by tight box 1, with no region 30.

| generator | figures | pass | % |
|---|---|---|---|
| matplotlib | 90 | 86 | 95.6 % |
| calc | 90 | 54 | 60.0 % |
| pdf-lib | 90 | 85 | 94.4 % |
| all | 270 | 225 | 83.3 % |

| layout | figures | pass | % |
|---|---|---|---|
| legend-right | 30 | 27 | 90.0 % |
| legend-below | 30 | 29 | 96.7 % |
| side-by-side | 60 | 36 | 60.0 % |
| between-paragraphs | 30 | 26 | 86.7 % |
| two-columns | 30 | 23 | 76.7 % |
| long-caption | 30 | 30 | 100.0 % |
| turned-page | 30 | 28 | 93.3 % |
| turned-labels | 30 | 26 | 86.7 % |

Failures by kind: no region 0 / 26 / 4 / 30; label outside 2 / 3 / 3 / 8; other figure inside 2 / 11 / 2 / 15; the other kinds 0. Eval verdict line: `fails (complete 86.7 %, clean 94.4 % of figures)`.

### Why

From the summary tables only. Most failures on both sets are "no region": figures on which rules 1 finds no region at all (26 and 30 figures, of which 23 and 26 are Calc). Tight regions may not change, so a rule that widens regions cannot reach them. On seeds 1 to 5, Calc's ceiling was 33 of 45 (73.3 %) for the same reason. "Other figure inside" (13 and 15, mostly Calc) matches the side-by-side case seen on seeds 1 to 5: a chart takes in the labels of a partner that has no region of its own. Calc limited by pages with no region under rules 1: 23 and 26 of the 90 held-out Calc figures.

On the seeds the tuning saw (1 to 5): 48.9 % (first version, `2b72e26`), 46.7 % (after the review fixes), 85.2 % (round 1, `0c6309a`), 87.4 % (round 2, `8dfdddb`). Constants at the end: `LABEL_CHARS` 30; `LABEL_GAP_PT` 40 (was 12); `ROW_GAP_PT` 3 (new); `MAX_LABELLED` 5,000.

This measures geometry on generated pages, not whether a model reads a chart better, and not the maintainer's real PDFs.

---

## 8. The measured memory peak of the composing path (Task 13)

Set-up: the sidecar in `node:26-bookworm-slim` with the built `dist`, `--memory=1536m --memory-swap=1536m --cpus=1.5` (limit 1536 MiB = 1610612736 B), read-only root, tmpfs `/tmp` of 64 MB, all capabilities dropped, no-new-privileges, user 1000. Load per round: two requests at once (`longEdge=2576&pixels=3700000&bytes=1048576&frame=F`), 0.3 s later 16 more, all with 16 MiB bodies; repeated for 60 s (120 s in the later runs). The peak is `memory.max_usage_in_bytes`, or, when the container died, the highest 0.5 s sample of `memory.usage_in_bytes`.

### The runs, verbatim from the task's log

First attempt at the spec's 16 MP, with a GIF of about 600,000 1×1 frames after a few full-canvas frames:

| Run | Result |
|---|---|
| 16 MP, frame = the last (617625 / 590994) | composing can't finish in 10 s at 1.5 CPUs: 18 × 504 in one round of 90 s. `memory.max_usage_in_bytes 1520140288 failcnt 0`; inspect `false 0` (running). Peak 1449.7 MiB, with no job reaching the canvas |
| 16 MP, frame 250000 (a single such request alone: 200 in 5.5 s) | log `504 10084ms, 504 10118ms, 504 19827ms, 504 19757ms, 200 28748ms, 504 29821ms, 504 38506ms, 504 39742ms, 504 48470ms`, then the container died. `OOMKilled` **`true 0`** (exit 137). Highest sample 1548881920 B (1477 MiB). Reproduces the earlier attempt (two 504 after 10.1 s, two 200 after 19.3 s, then OOMKilled) |
| 12 MP (a3464 / b3464, frame 250000) | log `200 18987ms, 504 19930ms, 200 28518ms, 504 30069ms`, then dead. **`true 0`** (exit 137). Highest sample 1572208640 B (1499 MiB) |
| 8 MP (a2828 / b2828, frame 250000) | log `200 9730ms, 200 9754ms, 200 18259ms, 200 18626ms`, then dead. **`true 0`** (exit 137). Highest sample 1573146624 B (1500 MiB) |
| 4 MP (a2000 / b2000, 657944 / 651488 frames, frame 250000) | log `200 17845ms, 200 18055ms, 200 26048ms, 200 26031ms`, then dead. **`true 0`** (exit 137). Highest sample 1608798208 B (1534 MiB) |

Controls:

| Run | Result |
|---|---|
| 16 MP, few frames (c1000.gif, 4000×4000, 1004 frames, 1.3 MB, last frame) | 54 × 200; `max_usage 1105334272 failcnt 0`; `false 0` |
| plain path (no frame), the 16 MiB GIFs | 108 × 200; `max_usage 1277190144 failcnt 0`; `false 0` |
| `MALLOC_ARENA_MAX=2`, 8 MP frame 250000 | 21 × 200, 16 × 504; `max_usage 1473540096 failcnt 0`; `false 0` |
| `MALLOC_ARENA_MAX=2`, 16 MP frame 250000 | 1 × 200, 17 × 504 (timeouts before the canvas, so not a fair test); `max_usage 1475440640 failcnt 0`; `false 0` |

Outside the container, one process (`node --max-old-space-size=384`, VmHWM): the small animated GIF 98 MiB; reading a 16 MiB GIF 60 MiB; `new GifReader` of a 657,944-frame GIF alone 183 MiB; `renderImage` of it, frame 5, 314 MiB, frame 250000, 405 MiB.

**Conclusion of the first measurement:** lowering `MAX_FRAME_PIXELS`, the spec's remedy, doesn't hold. It gets the sidecar OOM-killed at 16, 12, 8 and 4 MP alike. The ledger's ruling 11 (a frame limit) followed.

After the frame limit (`7437d79`, `MAX_FRAMES` 10,000):

| Run | Result |
|---|---|
| 600k-frame a.gif / b.gif with `--frame` | 504 × 413 (`{"error":"The GIF has too many frames for a frame: at most 10,000.","code":"too_large"}`), about 1 s each, 28 rounds. `max_usage 663519232 failcnt 0`; `false 0` |
| same GIFs, plain path | 144 × 200, 8 rounds. `max_usage 1021755392 failcnt 0` (before the walk: 1277190144); `false 0` |
| 16 MP, 10,000 frames (a10k / b10k), frames 9999 down, 60 s | 18 × 200 then killed. **`true 0`** (exit 137); highest sample 1554231296 |
| `MAX_FRAMES` 2,000 (dist only), a2k / b2k, 60 s | 36 × 200; `max_usage 1401872384 failcnt 0`; `false 0` |
| same, 120 s | 58 × 200 then killed. **`true 0`** (exit 137); highest sample 1507762176. So 2,000 only delayed the kill; `MAX_FRAMES` stays at 10,000 |
| probe, not committed: `MAX_FRAME_PIXELS` 8 MP, 10,000-frame 2828×2828 GIFs, 120 s | 126 × 200; `max_usage 1608753152 failcnt 0` (limit 1610612736); `false 0`. Survived by 2 MiB |

Memory climbs from round to round under sustained load, each job being a fresh worker thread, which suggests memory left by ended workers (allocator arenas or native canvas memory) rather than one job's size.

### The final result: 4 MP and 10,000 frames (`a5a5301`)

| Run | Result |
|---|---|
| 4 MP, 10,000 frames (a10k-4mp / b10k-4mp, 2000×2000, 4 resp. 6 full disposal-3 frames then 1×1, padded to 16 MiB), frames 9999 down, two at once + 16 queued, **120 s** | 180 × 200, 10 rounds, slowest 13.0 s. **`memory.max_usage_in_bytes 1410973696 failcnt 0`**; inspect `false 0` (exit 0, running); highest sample 1400709120. The container held, with about 190 MiB below the 1536 MiB limit |

Comparison on today's plain path, a 7000×7000 PNG (49 MP), 120 s:

| Run | Result |
|---|---|
| as made, 2.1 MB bodies | 216 × 200, 12 rounds. `max_usage 948813824 failcnt 0`; `false 0`; highest sample 890478592 |
| padded to 16 MiB with a private ancillary chunk, same load as the frame runs | 52 × 200, 2 × 504, 3 rounds. `max_usage 1413484544 failcnt 0`; `false 0`; highest sample 1335111680 |

So today's path with full queues reaches the same level as the 4 MP frame path (1410973696 against 1413484544): the climb and most of the level come from the pool and the queued bodies, not from this branch. The residual margin of about 190 MiB predates the branch and is an open point (section 9).

---

## 9. Open points for Chris

### What you must check by hand first

These need things the run didn't have: real clients, your real PDFs, and the release images.

1. **Playbook steps 26, 27 and 6, with real clients** (claude.ai, Claude Desktop, Claude Code, ChatGPT, Le Chat, Cursor, VS Code).
   - Step 26: the sideways sheet with a fresh code in small type. Does the model, after a first look, ask for `--rotate 90` by itself, read a crop off the turned picture, and quote the code exactly?
   - Step 27: `rg --regions`, then `pdf … --crop` with the numbers it gave. Does the crop show the line, and does the model widen it for the rest of a table row?
   - Step 6: the animated GIF. Does the model follow the note line to `--frame N`, and say which frame it saw (the playbook's key asks for frame 2's colour)?
2. **Real PDFs.** A PDF with a scanned page lying on its side: `pdf … N --images=all --rotate 90`, then a crop read off the picture; does it hit on the first try? And one of your private PDFs for `rg --regions`. The run had only generated PDFs and the demo set.
3. **Both images from the same tag.** `KUMBU_IMAGE` and `KUMBU_PDF_IMAGE` must come from the same tag. The server's echo (§1.3) refuses a picture from an older sidecar, but only the same tag gives the new options. Also check that the sidecar image builds (it couldn't be built in the run, section 1), and that the compose file's memory limit is what you want.
4. **The sidecar's memory headroom.** About 190 MiB remains of its 1536 MiB with full queues (16 MiB bodies, two workers). This **predates the branch**: today's plain path with a 49 MP still image padded to 16 MiB reaches 1413484544 B against the frame path's 1410973696 B. It held in every run, but the margin is small. Candidates, each a design change: fewer bodies in the queue; reusing workers, or limiting what an ended one leaves (`MALLOC_ARENA_MAX` was inconclusive here).

### To decide or look at

- **The frame cap changed from the spec's 16 MP to 4 MP, and a limit of 10,000 frames was added.** The sentence is now "… is too large for --frame (at most 4 megapixels and 10,000 frames); img without --frame shows its first frame". A 2000×2000 canvas fits; a 2560×1440 screen recording fits (3.69 MP). GIFs above either limit lose `--frame`, and over 10,000 frames also lose the "N frames" note on a plain `img`. The spec §2.2 and §2.3, `docs/security-architecture.md` and the code say 4 MP; the plan and brief keep 16 MP as history.
- **The 10 s compose limit for late frames.** The worker's 10 s kill still applies. In the first measurement a late frame of a huge canvas could not finish in 10 s at 1.5 CPUs (the 504 answers at 16 MP). At 4 MP and 10,000 frames the 120 s run had no 504, and the slowest round took 13.0 s with the queue; a slow machine could still see a 504 for a late frame of a large GIF.
- **Vertical-writing fonts on text boxes.** Boxes for text set in a font with `font.vertical` come out on the wrong side by the spec's formula. This is a spec-level matter found in Task 9's review; it is not fixed. The ledger marks it as spec-level, to report to you.
- **Regions with labels are left out.** The corpus, eval and scripts stay in the repository (`tools/attachment-probes/make_figure_pdfs.py`, `pnpm -C pdf eval:regions`). A first real PDF that disagrees would outweigh the table either way (the note says so too). If you want it again, the structural gap is Calc and other charts with no region at all under rules 1, which a rule that only widens regions can't reach.
- **Deferred minors from the ledger:**
  - Task 1: `turn()` falls through to 270 for any other value; `render.ts` passes an unvalidated rotate (the server whitelists it).
  - Task 2: the echo check reads `FirstOrDefault` of `X-Kumbu-Rotate`, so a duplicated header is not refused; `A_picture_not_turned_as_asked_couldnt_be_rendered` is weak.
  - Task 5: a crafted LZW stream may run until the worker's 10 s kill; a colour index past the palette reads later file bytes (same file, harmless).
  - Task 6: any 413 on a framed request reads as "canvas too large".
  - Task 7: no space-separated `--frame -1` case; no Img-side test of the size sentence.
  - Task 9: rounding to 0.1 lets `x + w` overshoot the page by up to 0.1, and a sliver may round to `w` 0 with an entry.
  - Task 10: no `AttachmentPixels`-level `no_such_page` test for text boxes.
  - Task 11: `Crop.Enclosing` may widen a crop by 1 pt at float edges; a "PDF's" typo in two summaries; `help pdf` rewrap is ragged; refusal order when piped.
  - Task 12: `docs/concept.md:105` (the metrics list) isn't updated; playbook steps 26 and 27 are out of positional order (deliberate).
  - Stage B: the `withLabels` cost was unbounded in the number of regions; moot with the revert.
  - Not covered by hand: the "older analysis" notice, and a GIF whose frame count is unknown (both covered by tests).
- **Tidy up after the run:** the database `kumbu-handson` is left in `kumbu-test-mongo`; the worktrees `/home/user/kumbu-stageA` and `/home/user/kumbu-handson` and their branches `work/stage-a` and `work/handson` are local only; the session branch `claude/attachment-wishes-brief-19tz7t` mirrors `feature/attachment-wishes`.
- **Final whole-branch review:** see the summary at the top; the review's fixes are in `5ce61a7`.

---

## 10. The pull request

<https://github.com/SCAI-NGO/kumbukumbu/pull/140> (a draft, opened at `a00a28a`; not merged).

Final whole-branch review: ready for maintainer review, with no critical or important findings. The security boundary of spec §5, the sidecar–server contracts and the complete revert of Task 14 stages B and C hold across the branch. Its 12 minor findings were fixed in one wave, `5ce61a7` ("Tidy the wording and two edges the final review found"), and a scoped re-review found all 12 addressed with no new breakage: the limit of 50 pages is per PDF, not per search; the regions metric counts PDFs; "delivered" means the sidecar answered; a stale line citation; the note's test count; quoted paths in rg's suggested commands; no scan from item 0 per match span; required x, y, w and h in a text box; a typo; and the help paragraph re-flowed. Its triage of the deferred minors: only the typo and the help wrap needed fixing before merge (both done); the rest are fine to defer, with vertical-writing fonts in text boxes named as a known limit for the maintainer.

If it arrives after this report was written, the result is recorded in the pull request's follow-up. The signal at the end of the run is `PushNotification`.
