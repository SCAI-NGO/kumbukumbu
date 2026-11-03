# Attachment wishes: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (or, if it is unavailable, emulate it with the plain Agent tool). One fresh subagent per task, then a spec-compliance review and a code-quality review, each by its own subagent. Subagents do a task's red, green, commit and mutations; they never merge, push or switch branches. The controller pushes. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Give clients `--rotate` on `img` and `pdf`, `--frame N` on `img`, `rg --regions` for where matches lie in a PDF, and attempt regions that include a figure's labels and legend, shipping that last item only if it measures reliable enough.

**Architecture:** All new parsing stays in the PDF sidecar (`pdf/`, Node, pdf.js, `@napi-rs/canvas`, new: `omggif`). The server (`src/Kumbu.Server`) passes validated parameters, checks what comes back, and stores nothing new. The commands (`src/Kumbu.Core/Sh/Commands/`) own the sentences. Task 14 alone may touch the stored analysis, in commits that can be reverted.

**Tech stack:** .NET 10, xUnit v3, Testcontainers; Node 26, TypeScript, Vitest, pdf-lib in tests; uv with matplotlib, openpyxl and LibreOffice Calc for the corpus of Task 14.

**Spec:** `docs/superpowers/specs/2026-11-02-attachment-wishes-design.md` (binding; "spec §n" below). The maintainer accepted its four `ASSUMPTION:` marks: with `--rotate`, `--crop` counts in the turned picture; `rg --regions` asks the sidecar on demand and stores no boxes; GIFs through `omggif`, licence checked and version pinned; "reliable enough" means held-out at least 90 % overall, 80 % per generator, 70 % per layout.

**No dry run:** the planning machine has no dotnet, node or docker, so nothing here has been executed. Baseline numbers come from CI. Every anchor was read from the tree; every expected count of new tests is a floor, not a measurement.

## Global constraints

- Branch `feature/attachment-wishes`. Pushing to it is expected after every task, even if the environment assigned another branch. **Do not merge.** One **draft** pull request to `master`. No tag, no version change (`version.txt`, `src/Kumbu.Server/Kumbu.Server.csproj`, `web/package.json`, `pdf/package.json`'s `version`), no push to `master`.
- British English in code, comments, docs, sentences and commit messages. `// SPDX-License-Identifier: AGPL-3.0-or-later` on new C# and TypeScript files, `# SPDX-License-Identifier: AGPL-3.0-or-later` on new Python ones. Match the surrounding naming, idiom and comment density; comments only where the reasoning isn't obvious.
- No organisation, customer or employer named anywhere, including tests and generated files. Use `Work`, `Papers`, `Demo`, Alice, Bob.
- Commit messages imperative and free-form ("Turn pictures in the sidecar"), no Conventional Commits prefix, ending with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. The tasks spell it out.
- `web/src/api/schema.d.ts`: no task should need it, as no REST shape changes. If one does, edit it by hand exactly as generated output would have it.
- Sentences and formats in the spec are not open. If one proves wrong in the hands-on test, change it and say why in `docs/notes-attachment-wishes-hands-on.md`.
- Line numbers refer to `836354d`, the commit that holds the spec. Earlier tasks move lines: if a number has drifted, search for the code the task names.
- "Zero tests ran" is a failure of the command, not red. A compile error counts as red only for a type or member the task creates.
- A **pin** is a new test that passes at red because it guards what must not change. Each task names its pins; every other new test must fail at red. A mutation, not the red step, proves a pin can fail.
- Test filters are Microsoft.Testing.Platform's: `--filter-class "*Name"`, repeatable, ORed. `FullyQualifiedName~` doesn't work here, and a glob in mid-name selects nothing.
- Narrow filters miss things: `PasswordWindowTests` covers every session endpoint and sits in no narrow filter, and `DashboardTests`, `LabelGuardTests` and `HelpTests` break on additions elsewhere. So every phase ends with the full suites.
- Exact lists of help texts and metrics have tests: `tests/Kumbu.Core.Tests/Sh/HelpTests.cs`, `tests/Kumbu.Server.Tests/Metrics/LabelGuardTests.cs` (through `KumbuMetrics.AllowedLabels`), and `tests/Kumbu.Server.Tests/Metrics/DashboardTests.cs:48` `Every_metric_the_server_defines_has_a_panel`. A new metric needs its `AllowedLabels` entry and a dashboard panel in the same task.

## Decisions this plan adds, where the spec is silent or can't hold

The report should list these, so the maintainer can overrule them.

1. **Text boxes are clipped to the page in the sidecar (Task 9).** Spec §3.3 has the server refuse numbers below 0, but text may lie partly off the displayed page (a CropBox, a margin overrun). Without clipping, one such item would refuse the whole answer. An item wholly outside has no entry.
2. **`end` is clamped to the trimmed text (Task 9).** `text` is `trimEnd`ed, so a last item ending in spaces would have `end` past the text, which the server refuses.
3. **GIF tests are built with `omggif`'s `GifWriter`, not `GifEncoder` (Task 5).** Spec §8 names `GifEncoder`, but its `GifDisposal` enum (`Keep = 0`, `Background = 1`, `Previous = 2`, `@napi-rs/canvas` 1.0.13 `index.d.ts`) isn't the file's disposal values 0, 2 and 3, and it quantises colours. `GifWriter` writes the given values exactly. The existing first-frame test stays on `GifEncoder`.
4. **`@types/omggif` 1.0.5 as a dev dependency (Task 5).** `omggif` ships no types, and `pnpm test` type-checks.
5. **The licence text of `omggif` comes from the header of `omggif.js` (Task 5):** the 1.0.10 tarball has no `LICENSE` file.
6. **`no_such_frame` is a request mistake (Task 6):** `KumbuMetrics.IsRequestMistake` (`src/Kumbu.Server/Metrics/KumbuMetrics.cs:387`) must name it, or it counts as a failed render.
7. **A frame asked of a file that isn't a GIF, at the sidecar, is `not_image` (Task 5).** The command refuses this first; this is the backstop. A frame with no palette is also `not_image`: `omggif` would otherwise read colours from the file's header.
8. **Any failure of `TextBoxesAsync` gets the spec's "No regions" notice, and no further PDF is asked in that search (Task 11).** Spec §3.4 names only "away, busy or too slow", but a malformed answer must not lose matches either, and asking 8 times with the sidecar away could take 80 s. **Slow successes have a budget too:** 8 × 10 s is possible, and the shell has no overall timeout, so past 20 s in all no further PDF is asked, with the same notice.
9. **"pdf /Work/paper.pdf 60" in the page-limit notice (spec §3.4) is the first page with matches that got no region (Task 11).**
10. **`pdf info` regions may change by a point (Task 8).** `Crop.Enclosing` rounds outward, so `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs:386`, y 20.4 and h 40, changes from `20,31,40` to `20,31,41`. Change it deliberately and say so in the commit.
11. **The numbers in the spec's `crop=` examples (§3.2, §3.7) don't follow its own rule (§3.3 point 4)** when computed from its JSON example. The rule wins: an item at 74.2,300.1,210.3,11.0 grows by 3.3 and gives `crop=70,296,218,19`. Tests assert the rule; the hands-on note quotes the real outputs.
12. **pdf-lib figures are made by a Node script (Task 14).** `make_figure_pdfs.py` (Python) can't call pdf-lib itself, so it calls `pnpm -C pdf eval:figures`.
13. **`/text-boxes` takes strictly ascending, distinct pages (Task 9),** so "pages as asked" can be checked by plain comparison.
14. **The eval reads a per-figure manifest (Task 14).** Spec §4.5 reads identity from the text alone, so a figure whose tokens fragment or vanish would pass "complete" with nothing to check. The generators write the expected tokens; a missing one fails the figure. Text falls into four classes: token, word list, `Figure N`, unidentified.
15. **Page types and tight regions are compared on seeds 1 to 5 and the fixtures, not on the held-out seeds (Task 14):** those are what `baseline.json` holds, from before the rule. The "no body line in a `with labels` region" check runs on the held-out controls as well.
16. **A rotation fallback, if needed, stays at the size of the step (Task 1),** with no full-size canvas: the memory limit of the composing path would otherwise lose 200 MB to it, and so would any 50 MP image.

### What was corrected in the spec

These corrections came from an independent review, were checked against the code, and are in the spec in this worktree:

- §1.5: the help's conversions round W and H down. An A4 page prints as 842 pt but measures 841.89, so a converted region with y = 0 failed by 0.11 pt.
- §2.2: the composing path is capped at 16 MP (`too_large`), skips `loadImage`, keeps one copy buffer and wraps its buffer in `ImageData`. At 50 MP one worker could use some 850 MB, against `mem_limit: 1536m`.
- §2.2: a frame outside the canvas has its own sentence, so a test can tell the check from a failed allocation.
- §2.2: `console.log` is silenced while decoding, rather than noted in a comment: the warning fires per frame, about 1.4 million lines from a 16 MiB GIF.
- §2.3: a sentence for a canvas over 16 MP.

## Review focus

1. An opaque image turned and scaled must not come back "transparent": anti-aliased edges would add a false note and flatten it. Pinned in Task 1.
2. Text partly or wholly outside the displayed page (CropBox, off-page text) must lose only that item's region, not the whole PDF's. Pinned in Task 9.
3. A failure part way through a PDF's text, or from `TextBoxesAsync`, with `--regions` must not lose lines already held. Pinned in Task 11.
4. A truncated GIF, a GIF with no palette, and a `--frame` on one: an honest refusal, no crash and no colours read from the header. Pinned in Task 5.
5. A page whose last text item ends in spaces, and a match in the last line of a page: offsets stay inside the text and the region is right. Pinned in Tasks 9 and 11.

---

## Task 0: baseline

- [ ] **Tools.** Run `dotnet --version`; expect 10.0.x (`global.json`: 10.0.100, roll forward). Run `echo $DOTNET_CLI_TELEMETRY_OPTOUT`; expect `1`. Run `node --version`; expect v26. Run `pnpm --version`; expect 10.32.1 (`npm install -g pnpm@10.32.1` if missing). Run `docker info`; expect it to work. If Docker can't run, set `KUMBU_TEST_MONGO` to a `mongod` 7.0 replica set (`ExternalMongo` in `tests/Kumbu.Server.Tests/TestImages.cs`); if MongoDB runs out of file handles, `--ulimit nofile=20000:20000` and `--wiredTigerEngineConfigString "file_manager=(close_idle_time=5,close_scan_interval=2,close_handle_minimum=250)"`. Install what is missing without asking, and note it.
- [ ] **Branch.** Run `git status -sb`; expect `## feature/attachment-wishes` and a clean tree. Run `git diff --stat 8a7c64e HEAD -- src tests web pdf tools monitoring`; expect no output: the branch's own commits hold documents only, so CI's numbers on `8a7c64e` apply.
- [ ] **The CI numbers, if you want to confirm them.** Run `gh run list --repo SCAI-NGO/kumbukumbu --branch master --workflow CI --limit 1`, then `gh run view 41511547330 --repo SCAI-NGO/kumbukumbu --log`. Taken on 2026-11-02 from that run, on `8a7c64e`.
- [ ] **Run the full verification, each line:**

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

**Expected, from CI on `8a7c64e`:** `dotnet test`: **total 3264, succeeded 3260, skipped 4, failed 0**. Web: **84 files, 1346 tests passed**. Sidecar: **6 files, 78 tests passed**. All builds succeed. **Known failures: none.** The 4 skipped need the embedding model and a TensorX key; running as root can skip 2 more; note that. The type check of the web app runs in `pnpm -C web build`, and `pnpm -C web test --run` doesn't give it; `pnpm -C pdf test` does include the sidecar's. These counts were not measured in a dry run: record what you measure, and any difference.

- [ ] **Time budget:** setup plus first build and tests about **15 minutes** (the last run needed about 25). If much longer, note it and carry on if you can.

## Phase 1: `--rotate`

### Task 1: The sidecar turns pictures (spec §1.2, §1.3)

**Files:** `pdf/src/image.ts:16,31-40`, `pdf/src/render.ts:14,21-31`, `pdf/src/pool.ts:12-15`, `pdf/src/worker.ts:23`, `pdf/src/server.ts:87-100,172-181`, `pdf/test/fixtures.ts`, `pdf/test/image.test.ts`, `pdf/test/render.test.ts`, `pdf/test/server.test.ts`.

**Produces:** `renderImage(img: Uint8Array, t: Target, crop?: Crop, rotate = 0)`, `renderPage(pdf: Uint8Array, page: number, t: Target, crop?: Crop, rotate = 0)`; `Job` kinds `page` and `image` gain `rotate: number`; query `rotate=90|180|270`; header `X-Kumbu-Rotate` only on an answer that was turned.

- [ ] **Red.** Add a fixture `cornersPage()` to `pdf/test/fixtures.ts`: an A4 page with four squares of 100 pt in four colours in its corners. Add these tests:
  - `image.test.ts`: `turns an image by 90, 180 and 270 degrees clockwise, exact to the pixel` (a 3 × 2 image with six different colours, at `SMALL`, every pixel asserted); `swaps the sides for 90 and 270, and keeps them for 180`; `counts a crop in the turned image`; `refuses a crop outside the turned image that fits the unturned one` (`bad_crop`); `turns on top of the EXIF orientation` (the `exif(6)` helper at `image.test.ts:16`); `reports the size before the turn as the original`; `keeps an opaque image opaque when it is turned and scaled` (an opaque 1000 × 700 PNG, each angle, at `SMALL`: `transparent` is false).
  - `render.test.ts`: `turns a page by each angle` (`cornersPage`, the colour in each corner after a clockwise turn); `turns a page with its own /Rotate on top of it` (`rotatedPage`, the red box where 90 plus the turn puts it); `turns a page with a CropBox` (`cropBoxPage`); `counts a crop in the turned page`; `reports the page's size without the turn`; `the conversions in the help hit the same colour` (a region around the `cornersPage` square at each corner, converted with the formula of spec §1.5, cropped with `rotate`: the middle of the picture has that colour, for each angle); `a converted region with y = 0 fits a page of fractional size` (a 595.28 × 841.89 page, W and H rounded down as the help says, each angle: no `bad_crop`).
  - `server.test.ts`: `takes rotate on both render routes and answers X-Kumbu-Rotate`; `answers no X-Kumbu-Rotate without a turn`; `refuses rotate other than 90, 180 or 270` (`0`, `45`, `-90`, `90.0`, `abc`: status 400, code `bad_request`).
- [ ] Run `pnpm -C pdf exec vitest run test/image.test.ts test/render.test.ts test/server.test.ts`. **Expected:** a non-zero exit, with the new tests failing on their assertions (the extra argument is ignored for now) and the old ones passing where they ran. **Pins, which pass at red:** `reports the size before the turn as the original`, `keeps an opaque image opaque when it is turned and scaled`, `reports the page's size without the turn`, `answers no X-Kumbu-Rotate without a turn`.
- [ ] **Green.**
  - `pdf/src/render.ts`: per spec §1.3. `shown = p.getViewport({ scale: 1, rotation: (p.rotate + rotate) % 360 })` for the check and the fit, the same `rotation` in the scaled viewport at `:28`, and `pageWidth`/`pageHeight` from `p.getViewport({ scale: 1 })`. Return `rotate` in the result.
  - `pdf/src/image.ts`: keep today's `drawImage` line untouched for `rotate === 0`. Otherwise the turned size is `[oh, ow]` for 90 and 270. Set the scale and offset for the region, then apply an integer matrix, which is what keeps it exact: `ctx.transform(0, 1, -1, 0, oh, 0)` for 90, `(-1, 0, 0, -1, ow, oh)` for 180, `(0, -1, 1, 0, 0, ow)` for 270, then `drawImage(image, 0, 0)`. If the opaque test fails on anti-aliased edges, don't add a full-size canvas: convert the region to the unturned frame, draw it with today's `drawImage` into a canvas of the output size with its sides swapped, then turn that canvas into the final one by the integer matrix at scale 1. It costs no more than the step's size. `originalWidth`/`originalHeight` stay `ow`/`oh`. Return `rotate`.
  - `pdf/src/server.ts`: a helper `turn(q)`: absent gives 0; else only the strings `90`, `180`, `270`; else `bad("rotate must be 90, 180 or 270.")`. Called before the slot is reserved, like `target` and `crop`. Add `"X-Kumbu-Rotate": r.rotate` to the headers only when `r.rotate !== 0`.
  - `pdf/src/pool.ts:14-15`, `pdf/src/worker.ts:23`: pass `rotate` along.
- [ ] Run `pnpm -C pdf exec vitest run test/image.test.ts test/render.test.ts test/server.test.ts`. **Expected:** exit 0, 0 failed.
- [ ] Run `pnpm -C pdf test`. **Expected:** exit 0: type check clean, at least 78 + 17 tests passed.
- [ ] Commit: `git add pdf/src/image.ts pdf/src/render.ts pdf/src/pool.ts pdf/src/worker.ts pdf/src/server.ts pdf/test/fixtures.ts pdf/test/image.test.ts pdf/test/render.test.ts pdf/test/server.test.ts && git commit -m "Turn pictures in the sidecar" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `pnpm -C pdf exec vitest run test/image.test.ts test/render.test.ts test/server.test.ts`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. `image.ts`: swap the matrices of 90 and 270. Expected to fail: `turns an image by 90, 180 and 270 degrees clockwise, exact to the pixel`.
  1b. `image.ts`: report the turned size as the original. Expected to fail: `reports the size before the turn as the original`.
  1c. `image.ts`: draw with a matrix offset by half a pixel, so edges blend. Expected to fail: `keeps an opaque image opaque when it is turned and scaled`.
  2. `image.ts`: check the crop against the unturned size. Expected to fail: `refuses a crop outside the turned image that fits the unturned one`.
  3. `render.ts`: `rotation: rotate` without `p.rotate`. Expected to fail: `turns a page with its own /Rotate on top of it`.
  4. `render.ts`: report `pageWidth` from the turned viewport. Expected to fail: `reports the page's size without the turn`.
  5. `server.ts`: accept any integer for `rotate`. Expected to fail: `refuses rotate other than 90, 180 or 270`.
  6. `server.ts`: leave out `X-Kumbu-Rotate`. Expected to fail: `takes rotate on both render routes and answers X-Kumbu-Rotate`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 2: The server asks for a turn and checks the echo (spec §1.3)

**Files:** new `src/Kumbu.Core/Attachments/Rotation.cs`; `src/Kumbu.Core/Attachments/IAttachmentPixels.cs:63,66`; `src/Kumbu.Server/Attachments/PdfSidecar.cs:21,24,105-115,212-234`; `src/Kumbu.Server/Attachments/AttachmentPixels.cs:20-31`; call sites `src/Kumbu.Core/Sh/Commands/PdfCommand.cs:404,478,511`, `src/Kumbu.Core/Sh/Commands/Img.cs:84` (pass 0 for now). **Implementers that follow:** `tests/Kumbu.Server.Tests/Attachments/FakePdfSidecar.cs:49,59`, and `RefusingSidecar` at `tests/Kumbu.Server.Tests/Attachments/AttachmentPixelsTests.cs:118`, which the spec doesn't list; `FakePixels` at `tests/Kumbu.Core.Tests/Sh/ImgTests.cs:17`, `PdfCommandTests.cs:18`, `RgPdfTests.cs:14`, `SemsearchPdfTests.cs:21`. **Calls in tests:** `AttachmentPixelsTests.cs:46,47,68,84,85,107`, `PdfSidecarTests.cs:82,83,103,104,178,305,317,333,346,367,377`. Find them all with `rg -n "RenderPageAsync|RenderImageAsync|\.PageAsync\(|\.ImageAsync\(" src tests`. **Test:** new `tests/Kumbu.Core.Tests/Attachments/RotationTests.cs`, `tests/Kumbu.Server.Tests/Attachments/PdfSidecarTests.cs`, `AttachmentPixelsTests.cs`.

**Produces:**
- `public static class Rotation { public static bool TryParse(string text, out int degrees); public static bool SwapsSides(int degrees); }`
- `IAttachmentPixels.ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, int rotate, CancellationToken ct)`, `PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, int rotate, CancellationToken ct)`
- `IPdfSidecar.RenderPageAsync(ReadOnlyMemory<byte> pdf, int page, StepLimits target, Crop? crop, int rotate, CancellationToken ct)`, `RenderImageAsync(ReadOnlyMemory<byte> image, StepLimits target, Crop? crop, int rotate, CancellationToken ct)`
- `FakePdfSidecar` gains `List<int> PageRotations` and `List<int> ImageRotations`, and swaps its original size in the rendered size for 90 and 270. Keep the existing `PageRequests` and `ImageRequests` tuples as they are: tests compare them.

- [ ] **Red.**
  - `RotationTests`: `Only_the_three_angles_parse` (a theory: `90`, `180`, `270` parse; `0`, `45`, `-90`, `+90`, `090`, `90.0`, ` 90`, empty don't); `Only_a_quarter_turn_swaps_the_sides`.
  - `PdfSidecarTests`: `A_turn_goes_into_the_query`; `No_turn_leaves_the_query_as_it_was`; `An_answer_without_the_echo_is_refused` (asked 90, no header: `SidecarException` with code `failed` and the message `The image isn't turned as asked`); `An_answer_turned_otherwise_is_refused` (asked 90, header 180); `An_answer_turned_unasked_is_refused` (asked nothing, header 90); `A_malformed_echo_is_refused` (`90.0`, `abc`); `A_matching_echo_is_accepted`, on both routes.
  - `AttachmentPixelsTests`: `A_picture_not_turned_as_asked_couldnt_be_rendered` (a sidecar throwing that exception: the message is `<path> couldn't be rendered`).
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*RotationTests"`. **Expected:** non-zero, compile error for `Rotation`, the type this task creates.
- [ ] **Green.** The parser per the signature above (compare with the three exact strings). `PdfSidecar.Query` (`:113-115`) appends `&rotate=` when it isn't 0. `PictureAsync` (`:212-234`) takes `int rotate`: read `X-Kumbu-Rotate`, absent counts as 0, anything that isn't the requested value's plain string throws `Invalid("The image isn't turned as asked")`. Pass `rotate` from `AttachmentPixels`. Commands pass 0. Update every implementer and call above.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*RotationTests" --filter-class "*ImgTests" --filter-class "*PdfCommandTests" --filter-class "*RgPdfTests" --filter-class "*SemsearchPdfTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*AttachmentPixelsTests" --filter-class "*PdfToolTests" --filter-class "*ImgToolTests"`. **Expected:** exit 0, 0 failed.
- [ ] Commit: `git add src/Kumbu.Core/Attachments/Rotation.cs src/Kumbu.Core/Attachments/IAttachmentPixels.cs src/Kumbu.Server/Attachments/PdfSidecar.cs src/Kumbu.Server/Attachments/AttachmentPixels.cs src/Kumbu.Core/Sh/Commands/PdfCommand.cs src/Kumbu.Core/Sh/Commands/Img.cs tests/Kumbu.Core.Tests tests/Kumbu.Server.Tests/Attachments && git commit -m "Let the server ask for a turn and check that it happened" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests"`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. `PdfSidecar.cs`: skip the echo check. Expected to fail: `An_answer_without_the_echo_is_refused`.
  2. `PdfSidecar.cs`: treat an absent echo as good. Expected to fail: `An_answer_without_the_echo_is_refused`.
  3. `PdfSidecar.cs`: check the echo only when a turn was asked. Expected to fail: `An_answer_turned_unasked_is_refused`.
  4. `PdfSidecar.cs`: leave `&rotate=` out of the query. Expected to fail: `A_turn_goes_into_the_query`.
  5. `Rotation.cs`: accept `0`; for this one run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*RotationTests"`. Expected to fail: `Only_the_three_angles_parse`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 3: `img --rotate` (spec §1.1, §1.2, §1.4, §1.5, §1.6, §7)

**Files:** `src/Kumbu.Core/Sh/Commands/Img.cs:11,15,20,27-29,52-56,75-79,84,97-101`; `src/Kumbu.Core/Sh/PictureText.cs:24-29,35-47,49-60`; `src/Kumbu.Core/Attachments/IAttachmentPixels.cs:43,55`; `src/Kumbu.Server/Metrics/KumbuMetrics.cs` (a `Counter` beside `ImgCrops` at `:339`, the switch at `:398-420`, the `Label` at `:439-448`, pre-created labels near `:70`, `AllowedLabels` near `:596`); `monitoring/grafana/dashboards/kumbu.json` (the Attachments row; `kumbu_img_calls_total` at about `:1206`). **Test:** `tests/Kumbu.Core.Tests/Sh/ImgTests.cs`, `tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs`.

**Consumes:** `Rotation.TryParse`, `Rotation.SwapsSides`; `ImageAsync(path, state, step, crop, rotate, ct)` (Task 2).

**Produces:**
- `public enum TurnCommand { Img, Pdf }`, `public sealed record PictureTurned(TurnCommand Command) : AttachmentUse;`, `ImgRefusal.BadRotate`
- `PictureText.For(string head, string original, SizeStep step, Crop? crop, Picture picture, int rotate = 0)`, `PictureText.ForImage(string head, string original, SizeStep step, Crop? crop, Picture picture, bool gif, int rotate = 0)`, `internal static string PictureText.Turned(int rotate, double width, double height, string unit)` giving `, turned 90° clockwise to 842×595 pt`
- Metric `kumbu_pictures_turned_total{command=img|pdf}`, counted once per delivered picture.

- [ ] **Red.** In `ImgTests` (give its `FakePixels` a `List<int> Rotations` and a `List<AttachmentUse> Used`, and keep `Requests` as it is):
  - `Rotate_goes_to_the_pixels`; `The_last_rotate_wins`; `Rotate_goes_with_size_and_crop_in_any_order`.
  - `A_bad_rotate_is_refused_with_its_sentence` (a theory: `45`, `0`, `-90`; exactly `img: --rotate turns clockwise by 90, 180 or 270 degrees, not '45' (leave it out for no turn)`, and `Used` holds `ImgRefused(BadRotate)`); `A_bad_rotate_is_refused_before_the_path_is_opened` (a missing path gets the rotate sentence).
  - `The_text_block_names_the_turn`: a 3000×2000 image, `--rotate 90`, a picture of 894×1341: the first line is exactly `/Work/Scans/sheet.jpg (version <v>): image (JPEG), original 3000×2000 px, turned 90° clockwise to 2000×3000 px; medium: 894×1341 px PNG, 4 B` as `AttachmentText.Size` prints it; `Reduced to 45 %` follows; then `With --rotate 90, --crop counts from the top left of this turned image, in its pixels.`; then the "If no image follows" line, in that order.
  - `A_half_turn_keeps_the_sizes` (`turned 180° clockwise to 3000×2000 px`, and "Reduced to" against 3000).
  - `A_crop_says_it_is_of_the_turned_image` (`; crop 0,0,300,200 of the turned image;`).
  - `A_crop_outside_the_turned_image_is_refused_without_a_round_trip`: a 2000×3000 image, `--rotate 90 --crop 0,2500,800,600`: exactly `img: the crop 0,2500,800,600 lies outside the image as turned by --rotate 90 (3000×2000 px)`, and no request. The size in brackets is the **turned** size.
  - `A_crop_inside_the_turned_image_passes_though_outside_the_stored_one`: 2000×3000, `--rotate 90 --crop 2500,0,400,400`: one request.
  - `Without_rotate_the_text_block_is_as_it_was`: no `turned`, no `With --rotate` line.
  - `The_help_names_rotate` (`--rotate D`, `[--rotate 90|180|270]`, and the `Usage` line).
  - `AttachmentMetricsTests`: `Turned_pictures_are_counted_by_command` (`kumbu_pictures_turned_total{command="img"}` grows by one); `A_refused_rotate_has_its_own_reason` (`reason="bad_rotate"`).
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ImgTests"`. **Expected:** non-zero: compile errors only for `ImgRefusal.BadRotate` and `PictureTurned`, or, once those exist, the new tests failing. **Pin, which passes at red:** `Without_rotate_the_text_block_is_as_it_was`.
- [ ] **Green.**
  - `Img.cs`: add the option to `Spec` at `:11`; the help and `Usage` per spec §1.5, without `[--frame N]`, which Task 7 adds. Parse right after `--size` (`:52-56`). The check before the round trip (`:75-79`) compares with the turned size and uses the spec's sentence only when turned; today's sentence otherwise. Count `PictureTurned(TurnCommand.Img)` beside `ImgCalled` at `:97`.
  - `PictureText.cs`: `share` at `:38` divides by `crop?.Width ?? (Rotation.SwapsSides(rotate) ? picture.OriginalHeight : picture.OriginalWidth)`. `Block` writes `of the turned image` or `of the turned page` after the crop, and adds the `With --rotate` line last among the notes. The original text from the caller gets `Turned(…)` appended.
  - `KumbuMetrics.cs`: the counter, the `case PictureTurned`, its `Label`, `ImgRefusal.BadRotate => "bad_rotate"` (the fallback at `:447` would call it `unavailable`), pre-created labels, and `Metric("kumbu_pictures_turned_total", ("command", Values<TurnCommand>(Label)))`.
  - `kumbu.json`: a panel in the Attachments row, with an id no other panel has and a `description` as its neighbours have.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ImgTests" --filter-class "*HelpTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*AttachmentMetricsTests" --filter-class "*LabelGuardTests" --filter-class "*DashboardTests" --filter-class "*ImgToolTests"`. **Expected:** exit 0, 0 failed.
- [ ] Commit: `git add src/Kumbu.Core/Sh/Commands/Img.cs src/Kumbu.Core/Sh/PictureText.cs src/Kumbu.Core/Attachments/IAttachmentPixels.cs src/Kumbu.Server/Metrics/KumbuMetrics.cs monitoring/grafana/dashboards/kumbu.json tests/Kumbu.Core.Tests/Sh/ImgTests.cs tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs && git commit -m "Let img turn a picture" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ImgTests"`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. `PictureText.cs`: always divide by `OriginalWidth`. Expected to fail: `The_text_block_names_the_turn`.
  2. `Img.cs`: check the crop against the unturned size. Expected to fail: `A_crop_outside_the_turned_image_is_refused_without_a_round_trip`.
  3. `Img.cs`: parse `--rotate` after the path is opened. Expected to fail: `A_bad_rotate_is_refused_before_the_path_is_opened`.
  4. `PictureText.cs`: drop the `With --rotate` line. Expected to fail: `The_text_block_names_the_turn`.
  5. `KumbuMetrics.cs`: remove the `bad_rotate` label; for this one run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*AttachmentMetricsTests"`. Expected to fail: `A_refused_rotate_has_its_own_reason`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 4: `pdf --rotate` (spec §1.1, §1.2, §1.4, §1.5, §1.6)

**Files:** `src/Kumbu.Core/Sh/Commands/PdfCommand.cs:25,31,36,58,111-114,121-125,140-145,170-173,404,426,448,454-463,470-473,478,488,511,521`; `src/Kumbu.Core/Sh/PictureText.cs:24-29` if Task 3 left anything for pages. **Test:** `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs`, `tests/Kumbu.Server.Tests/Attachments/PdfToolTests.cs`, `tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs`.

**Consumes:** `Rotation.TryParse`, `Rotation.SwapsSides`; `PageAsync(path, state, page, step, crop, rotate, ct)` (Task 2); `PictureText.For(head, original, step, crop, picture, rotate)`, `PictureText.Turned`, `PictureTurned(TurnCommand.Pdf)` (Task 3).

- [ ] **Red.** In `PdfCommandTests` (give its `FakePixels` a `List<int> Rotations`):
  - `Rotate_goes_to_the_pixels_for_every_page_of_a_range`; `The_last_rotate_wins`.
  - `A_bad_rotate_is_refused` (exactly `pdf: --rotate turns clockwise by 90, 180 or 270 degrees, not '45' (leave it out for no turn)`).
  - `Rotate_without_pictures_is_refused` (exactly `pdf: --rotate turns the pictures of a page; add --images, --images=all or --crop`).
  - `Info_refuses_rotate` (the existing sentence: `pdf: info takes a path and nothing else, e.g. pdf info /Work/a.pdf`).
  - `The_text_block_names_the_turn`, from spec §1.6: `…, page 7 of 15 (image): page 595×842 pt, turned 90° clockwise to 842×595 pt; medium: 1303×920 px …`, then `With --rotate 90, --crop counts from the top left of this turned page, in its points.`, then the "If no image follows" line.
  - `A_crop_says_it_is_of_the_turned_page` (`; crop 646,491,162,65 of the turned page;`).
  - `A_crop_outside_the_turned_page_is_refused_without_a_round_trip`: exactly `pdf: the crop 700,500,300,200 lies outside page 7 as turned by --rotate 90 (842×595 pt)`; `A_crop_inside_the_turned_page_passes_though_outside_the_page`.
  - `Continue_with_keeps_the_turn` (exactly `… Continue with: pdf /Work/a.pdf 5-20 --images=all --rotate 90 ---`), and `Continue_with_names_turn_before_size` (`--images --rotate 270 --size large`).
  - `Without_an_analysis_the_turn_still_goes_along` (through `WithoutAnalysisAsync`, `:511`), and its `Stop` line keeps it.
  - `Rotate_leaves_the_page_text_as_it_is`; `Without_rotate_the_text_block_is_as_it_was`; `The_help_names_rotate_and_its_conversions` (`x,y,w,h on a page W×H, with W and H rounded`, `down, is H-y-h,x,h,w when turned by 90` and the rest of spec §1.5).
  - `PdfToolTests`: `Rotate_passes_through_to_the_sidecar` (through MCP, `FakePdfSidecar.PageRotations` holds 90). `AttachmentMetricsTests`: `Turned_pages_count_for_pdf` (`command="pdf"`).
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*PdfCommandTests"`. **Expected:** non-zero, with the new tests failing (`pdf: unrecognized option '--rotate'` for most) and no old one. **Pin, which passes at red:** `Without_rotate_the_text_block_is_as_it_was`.
- [ ] **Green.** Add the option to `Spec` (`:25`), `parsed.Has("rotate")` to the check at `:111`, parse after `--size` (`:121-125`). The value is checked before the path is opened; then, still before it, `rotate != 0 && show == Show.Text && crop is null` is refused. `Call` (`:170`) gains `int Rotate`. Pass it at `:404,478,511`. `PageSize` (`:448`) gets the turned part from `PictureText.Turned`. `Stop` (`:454-463`) writes `flag`, then ` --rotate N`, then `size`. `CropAsync` (`:470-473`) compares with the turned size and uses the spec's sentence only when turned. `CountPicture` (`:183`) counts `PictureTurned(TurnCommand.Pdf)` when turned. Help and `Usage` per spec §1.5.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*PdfCommandTests" --filter-class "*HelpTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfToolTests" --filter-class "*AttachmentMetricsTests" --filter-class "*LabelGuardTests" --filter-class "*DashboardTests"`. **Expected:** exit 0, 0 failed.
- [ ] Commit: `git add src/Kumbu.Core/Sh/Commands/PdfCommand.cs src/Kumbu.Core/Sh/PictureText.cs tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs tests/Kumbu.Server.Tests/Attachments/PdfToolTests.cs tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs && git commit -m "Let pdf turn the pages it shows" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*PdfCommandTests"`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Drop `--rotate` from the `Stop` line. Expected to fail: `Continue_with_keeps_the_turn`.
  2. Drop `parsed.Has("rotate")` from the `info` check. Expected to fail: `Info_refuses_rotate`.
  3. Remove the refusal without pictures. Expected to fail: `Rotate_without_pictures_is_refused`.
  4. `CropAsync`: compare with the unturned size. Expected to fail: `A_crop_outside_the_turned_page_is_refused_without_a_round_trip`.
  5. `WithoutAnalysisAsync`: pass 0. Expected to fail: `Without_an_analysis_the_turn_still_goes_along`.
- [ ] Reviews.
- [ ] **Phase gate: the full verification, each line:**

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

**Expected:** exit 0 for each; `dotnet test` failed 0, skipped 4, total above 3264; web 1346 tests; sidecar above 78.

- [ ] The controller runs `git push -u origin feature/attachment-wishes`.

## Phase 2: `--frame`

### Task 5: The sidecar composes a GIF's frames (spec §2.1, §2.2)

**Files:** `pdf/package.json`, `pdf/pnpm-lock.yaml`, `THIRD-PARTY-NOTICES.md`; `pdf/src/image.ts:16,24-31`; `pdf/src/pool.ts:15`; `pdf/src/worker.ts:23`; `pdf/src/server.ts:23-33,94-100`; `pdf/test/fixtures.ts`, `pdf/test/image.test.ts`, `pdf/test/server.test.ts`.

**Produces:** `renderImage(img: Uint8Array, t: Target, crop?: Crop, rotate = 0, frame?: number)`; `RenderedImage` gains `frame?: number`, `frames?: number`; `export function frames(bytes: Uint8Array): number | undefined`; code `no_such_frame` (422); query `frame`; headers `X-Kumbu-Frame`, `X-Kumbu-Frames`.

- [ ] **Check the licence and pin the version.** Run `pnpm -C pdf view omggif@1.0.10 license`; expect `MIT`. Run `pnpm -C pdf view omggif@1.0.10 dependencies`; expect no output. (The npm registry said on 2026-11-02: latest 1.0.10, MIT, no dependencies, published 2019-07-16, integrity `sha512-LMJTtvgc/nugXj0Vcrrs68Mn2D1r0zf630VNtqtpI1FEO7e+O9FP4gqs9AcnBaSEeoHIPm28u6qgPR0oyEpGSw==`.) If the licence isn't MIT, stop this task and Tasks 6 and 7, write why in the report, and go on with Task 8. Then run `pnpm -C pdf add --save-exact omggif@1.0.10` and `pnpm -C pdf add --save-dev --save-exact @types/omggif@1.0.5`. Run `rg -n '"omggif"' pdf/package.json`; expect `"omggif": "1.0.10"`. Run `rg -n "omggif@1.0.10" pdf/pnpm-lock.yaml`; expect a hit whose integrity is the one above. Add a section `## omggif` to `THIRD-PARTY-NOTICES.md`: used in the PDF sidecar's image, from the npm package `omggif` 1.0.10 (`pdf/package.json`), `GifReader` only, source https://github.com/deanm/omggif, licence MIT, with the text and copyright line from the header of `omggif.js` (the package has no `LICENSE` file).
- [ ] **Red.** Add to `pdf/test/fixtures.ts` a helper `gifOf(width, height, frames)` that builds a GIF with `GifWriter` from `omggif`, with exact palettes, offsets, `disposal` and `transparent`, and a helper `gifWithFrameRect(canvasW, canvasH, frameW, frameH)` that writes the bytes by hand. Add to `image.test.ts`:
  - `composes frame 2 over frame 1 with a partial rectangle`; `clears a frame's rectangle for disposal 2`; `restores what lay under a frame for disposal 3` (three frames; at frame 3 the area of frame 2 shows frame 1); `lets what lies under show through a transparent index`; `shows the last frame before its own disposal` (frame 2 with disposal 2, asked for as frame 2, is visible).
  - `shows frame 1 through the composing path as the plain still shows it`; `delivers transparency left by composing on white, and says so`.
  - `turns and crops a frame`.
  - `refuses a frame past the end with no_such_frame`; `refuses a frame rectangle past the canvas before decoding it` (canvas 10×10, frame 60,000×60,000: `not_image` **with the message `A frame of the GIF lies outside its canvas.`**, since a failed 3.6 GB allocation would also map to `not_image`); `refuses a frame past the canvas's width` (canvas 10×10, frame at x 5, 6×1: that message) and `refuses a frame past the canvas's height` (at y 5, 1×6: that message), since the first fails on both axes; `refuses to compose a GIF over 16 megapixels` (a hand-made header of 5000×4000: `too_large`; the plain path still takes it as far as today); `logs nothing while decoding a frame with a short stream` (a hand-made GIF whose image data ends early, with `vi.spyOn(console, "log")`: never called, and `console.log` is restored afterwards); `refuses a frame with no palette` (`not_image`); `refuses a frame of a file that is no GIF` (`not_image`); `refuses a frame of a truncated GIF` (`not_image`).
  - `frames counts 3 for a GIF of three, 1 for a still GIF, and nothing for a PNG`; `frames gives nothing, and doesn't throw, for a truncated GIF`.
  - `renderImage reports the frames of a plain GIF`, and `a plain GIF still comes through Skia` (the existing `delivers the first frame of an animated GIF` at `image.test.ts:97` stays as it is).
  - In `server.test.ts`: `takes frame and answers X-Kumbu-Frame and X-Kumbu-Frames`; `answers no_such_frame with 422`; `refuses frame 0, 1000001 and 1-3` (400, `bad_request`); `answers no X-Kumbu-Frames for a PNG`.
- [ ] Run `pnpm -C pdf exec vitest run test/image.test.ts test/server.test.ts`. **Expected:** a non-zero exit, with the new tests failing on their assertions. **Pins, which pass at red:** `shows frame 1 through the composing path as the plain still shows it`, `a plain GIF still comes through Skia`, `answers no X-Kumbu-Frames for a PNG`.
- [ ] **Green.** In `image.ts`: `import { GifReader } from "omggif"` (if Node reports no such named export, `import omggif from "omggif"` and take it from there). `frames(bytes)` per spec §2.2: `undefined` for a non-GIF, for a reader that throws, and for zero frames. With `frame` set, `renderImage` doesn't call `loadImage` at all: after the header checks, a file that is no GIF is `not_image`, and `export const MAX_FRAME_PIXELS = 16_000_000` over the canvas is `new PdfError("The GIF is too large for a frame: at most 16 megapixels.", "too_large")`. Add a private `composeFrame(reader, n)`: a `Uint8ClampedArray` of `width × height × 4` of zeros, and one copy buffer of that size, made the first time a frame's disposal is 3 and reused after; for i from 0 to n−1: refuse a frame with `x + width` past the canvas or `y + height` past it, `not_image` with the sentence `A frame of the GIF lies outside its canvas.`, **before** decoding it, and one with `palette_offset === null`, `not_image`; `decodeAndBlitFrameRGBA(i, buffer)`; then, if i < n−1, apply its disposal: 2 clears its rectangle, 3 restores the copy, anything else keeps. Wrap the buffer without copying, `new ImageData(buffer, width, height)`, and `putImageData` into a canvas that takes the place of `image` at `:31`; if `@napi-rs/canvas`'s constructor copies after all, say so in the report. By estimate, not measured: about 255 MB a worker without a copy and 320 MB with one (buffer, copy, a 16 MB index stream per frame at `omggif.js:599`, which assumes the collector keeps up, canvas, and about 45 MB for step canvases, `getImageData`, `onWhite` and encoding). With two workers, 288 MiB of bodies, `GifReader`'s frame objects from tiny frames (about 180 MB of heap each) and the runtime (about 150 MB), the sidecar comes to about 1.3 to 1.45 GiB. That is close to `mem_limit: 1536m`, not clearly below it: Task 13 measures it. Reader errors are `not_image`, and a `PdfError` passes unchanged. Past the end is `new PdfError(..., "no_such_frame")`. Silence `console.log` while decoding, in `try`/`finally` inside this synchronous function, with a comment: `omggif` warns once per decoded frame on a short stream, which a 16 MiB GIF could turn into over a million lines. `server.ts`: `no_such_frame: 422` in `STATUS`, `q.has("frame") ? integer(q, "frame", 1, 1_000_000) : undefined`, the headers only when known. `pool.ts:15`, `worker.ts:23`: pass `frame`.
- [ ] Run `pnpm -C pdf exec vitest run test/image.test.ts test/server.test.ts`. **Expected:** exit 0, 0 failed.
- [ ] Run `pnpm -C pdf test`. **Expected:** exit 0: type check clean.
- [ ] Commit: `git add pdf/package.json pdf/pnpm-lock.yaml THIRD-PARTY-NOTICES.md pdf/src/image.ts pdf/src/pool.ts pdf/src/worker.ts pdf/src/server.ts pdf/test/fixtures.ts pdf/test/image.test.ts pdf/test/server.test.ts && git commit -m "Compose the frames of a GIF in the sidecar" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `pnpm -C pdf exec vitest run test/image.test.ts test/server.test.ts`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Remove the frame rectangle check. Expected to fail: `refuses a frame rectangle past the canvas before decoding it`, on its message, whatever the allocation does.
  1b. Check only the width. Expected to fail: `refuses a frame past the canvas's height`.
  1c. Check only the height. Expected to fail: `refuses a frame past the canvas's width`.
  2. Ignore disposal 2. Expected to fail: `clears a frame's rectangle for disposal 2`.
  3. Ignore disposal 3. Expected to fail: `restores what lay under a frame for disposal 3`.
  4. Blit only frame N. Expected to fail: `composes frame 2 over frame 1 with a partial rectangle`.
  5. Apply frame N's own disposal too. Expected to fail: `shows the last frame before its own disposal`.
  6. Remove the palette check. Expected to fail: `refuses a frame with no palette`.
  6b. Remove the 16 MP cap. Expected to fail: `refuses to compose a GIF over 16 megapixels`.
  6c. Remove the silencing of `console.log`. Expected to fail: `logs nothing while decoding a frame with a short stream`.
  7. `no_such_frame` as `failed`. Expected to fail: `answers no_such_frame with 422`.
  8. `frames` rethrows. Expected to fail: `frames gives nothing, and doesn't throw, for a truncated GIF`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 6: The server carries frames (spec §2.2, §2.3)

**Files:** `src/Kumbu.Core/Attachments/IAttachmentPixels.cs:10,17,25-30,43,63`; `src/Kumbu.Server/Attachments/PdfSidecar.cs:24,77-89,109-111,212-234`; `src/Kumbu.Server/Attachments/AttachmentPixels.cs:20-25,109-124`; `src/Kumbu.Server/Metrics/KumbuMetrics.cs:387,439-448`; `src/Kumbu.Core/Sh/Commands/Img.cs:84,88-93`. **Implementers:** `tests/Kumbu.Server.Tests/Attachments/FakePdfSidecar.cs:59`, `RefusingSidecar` at `AttachmentPixelsTests.cs:118`, `FakePixels` at `tests/Kumbu.Core.Tests/Sh/ImgTests.cs:17`, `PdfCommandTests.cs:18`, `RgPdfTests.cs:14`, `SemsearchPdfTests.cs:21`. **Test:** `tests/Kumbu.Server.Tests/Attachments/PdfSidecarTests.cs`, `AttachmentPixelsTests.cs`, `tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs`.

**Consumes:** the sidecar's `frame`, `X-Kumbu-Frame`, `X-Kumbu-Frames` and `no_such_frame` (Task 5).

**Produces:** `Picture` gains a last parameter `int? Frames = null`; `PixelsProblem.BadFrame`; `ImgRefusal.BadFrame`; `IAttachmentPixels.ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, int rotate, int? frame, CancellationToken ct)`; `IPdfSidecar.RenderImageAsync(ReadOnlyMemory<byte> image, StepLimits target, Crop? crop, int rotate, int? frame, CancellationToken ct)`; `FakePdfSidecar` gains `List<int?> ImageFrames`, and a `Func<int?, int?>? FramesOf`.

- [ ] **Red.**
  - `PdfSidecarTests`: `A_frame_goes_into_the_query`; `No_frame_leaves_the_query_as_it_was`; `An_answer_without_the_frame_echo_is_refused`; `An_answer_with_a_frame_unasked_is_refused`; `The_count_of_frames_reaches_the_picture`; `A_missing_count_of_frames_is_null`; `A_malformed_count_of_frames_is_refused` (`0`, `-1`, `3.5`, `abc`); `A_frame_past_the_count_is_refused`; `No_such_frame_is_known_with_its_status` (422 with that code gives `SidecarException` code `no_such_frame`; 500 with it gives `failed`).
  - `AttachmentPixelsTests`: `A_frame_past_the_end_says_so` (exact message `<path> has no frame 7; img without --frame says how many it has`, `PixelsProblem.BadFrame`); `Too_long_with_a_frame_says_finding_it` (`finding frame 7 of <path> took too long`); `Too_long_without_a_frame_is_as_it_was`; `A_canvas_too_large_for_a_frame_says_so` (`too_large` with a frame: exact message `<path> is too large for --frame (at most 16 megapixels); img without --frame shows its first frame`, `PixelsProblem.TooLarge`).
  - `AttachmentMetricsTests`: `A_frame_past_the_end_isnt_a_failed_render` (`kumbu_render_failures_total` doesn't grow); `A_bad_frame_has_its_own_reason` (`reason="bad_frame"`).
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*AttachmentPixelsTests"`. **Expected:** non-zero: compile errors only for the members this task creates (`BadFrame`, `Frames`, the `frame` parameter).
- [ ] **Green.** `Codes` (`:77-89`) gains `["no_such_frame"] = HttpStatusCode.UnprocessableEntity`. `Query` appends `&frame=`. `PictureAsync` checks the echo as for the turn (`Invalid("The image isn't the frame asked for")`), reads `X-Kumbu-Frames` as a whole number above 0 (absent gives null, anything else `Invalid`), and refuses a frame above the count. `Refused` (`AttachmentPixels.cs:109-124`) takes `int? frame`: `"no_such_frame"` gives the sentence, `Timeout` with a frame gives `finding frame {frame} of {path} took too long`, `"too_large"` with a frame gives the sentence of spec §2.3 and the problem `TooLarge`, and `"no_such_frame"`'s problem is `BadFrame`. `PixelsUnavailableException.Kind` (`IAttachmentPixels.cs:25-30`): `BadFrame` is `Invalid`. `IsRequestMistake` (`KumbuMetrics.cs:387`) names `no_such_frame`. `Label(ImgRefusal)` (`:439-448`): `BadFrame => "bad_frame"`. `Img.cs:88-93`: `BadFrame => ImgRefusal.BadFrame`. `Img.cs:84` passes null for now. Update every implementer above.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*AttachmentPixelsTests" --filter-class "*AttachmentMetricsTests" --filter-class "*LabelGuardTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ImgTests" --filter-class "*PdfCommandTests" --filter-class "*RgPdfTests" --filter-class "*SemsearchPdfTests"`. **Expected:** exit 0, 0 failed.
- [ ] Commit: `git add src/Kumbu.Core/Attachments/IAttachmentPixels.cs src/Kumbu.Core/Sh/Commands/Img.cs src/Kumbu.Server/Attachments/PdfSidecar.cs src/Kumbu.Server/Attachments/AttachmentPixels.cs src/Kumbu.Server/Metrics/KumbuMetrics.cs tests/Kumbu.Core.Tests/Sh tests/Kumbu.Server.Tests/Attachments tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs && git commit -m "Carry GIF frames from the sidecar to the commands" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*AttachmentPixelsTests" --filter-class "*AttachmentMetricsTests"`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Skip the frame echo. Expected to fail: `An_answer_without_the_frame_echo_is_refused`.
  2. Accept a count of 0. Expected to fail: `A_malformed_count_of_frames_is_refused`.
  3. Remove `no_such_frame` from `Codes`. Expected to fail: `No_such_frame_is_known_with_its_status`.
  4. Remove it from `IsRequestMistake`. Expected to fail: `A_frame_past_the_end_isnt_a_failed_render`.
  5. Remove `bad_frame` from the label. Expected to fail: `A_bad_frame_has_its_own_reason`.
  6. Give a timeout with a frame today's sentence. Expected to fail: `Too_long_with_a_frame_says_finding_it`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 7: `img --frame` (spec §2.1, §2.3, §2.4, §2.5, §7)

**Files:** `src/Kumbu.Core/Sh/Commands/Img.cs:11,15,20,29-31,52-63,69-72,84,97-101`; `src/Kumbu.Core/Sh/PictureText.cs:35-47`; `src/Kumbu.Core/Attachments/IAttachmentPixels.cs:55`; `src/Kumbu.Server/Metrics/KumbuMetrics.cs` (the counter beside `:339`, the switch `:398-420`, `AllowedLabels` near `:596`); `monitoring/grafana/dashboards/kumbu.json`. **Test:** `tests/Kumbu.Core.Tests/Sh/ImgTests.cs`, `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs`, `tests/Kumbu.Server.Tests/Attachments/ImgToolTests.cs`, `tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs`.

**Consumes:** `ImageAsync(path, state, step, crop, rotate, frame, ct)`, `Picture.Frames`, `ImgRefusal.BadFrame`, `PixelsProblem.BadFrame` (Task 6).

**Produces:** `public sealed record ImgFrameShown : AttachmentUse;`; metric `kumbu_img_frames_total`; `PictureText.ForImage(string head, string original, SizeStep step, Crop? crop, Picture picture, bool gif, int rotate = 0, int? frame = null)`.

- [ ] **Red.** In `ImgTests`:
  - `A_plain_gif_of_three_frames_says_so` (exactly `An animated GIF of 3 frames, shown as a still: frame 1. --frame N shows another.`); `A_plain_gif_of_one_frame_has_no_note`; `A_plain_gif_of_unknown_count_says_todays_sentence` (the existing `A GIF is shown as a still: if it is animated, only its first frame.`; the assertion at `ImgTests.cs:178` stays valid as is).
  - `A_frame_goes_to_the_pixels`; `The_head_names_the_frame_of_the_count` (exactly `/Work/Clips/animated.gif (version <v>), frame 2 of 3: image (GIF), original 600×400 px; medium: 600×400 px PNG, …`, and no note line); `The_head_names_the_frame_with_the_count_unknown` (`, frame 2:`).
  - `A_bad_frame_is_refused_with_its_sentence` (a theory: `1-3`, `0`, `-1`, `2.5`, `1000001`: exactly `img: --frame is one frame, counting from 1, not '1-3'`, counting `BadFrame`), before the path is opened.
  - `A_frame_of_anything_but_a_gif_is_refused` (exactly `img: --frame is for GIFs; /Work/Screens/shot.png is a PNG`, counting `BadFrame`, no request).
  - `A_frame_past_the_end_says_so` (exactly `img: /Work/Clips/moving.gif has no frame 7; img without --frame says how many it has`, from a `Fail` with `PixelsProblem.BadFrame`).
  - `Frame_goes_with_rotate_crop_and_size`.
  - `The_help_names_frame` (`--frame N`, `A GIF is shown as a still, its first frame unless --frame says otherwise.`).
  - `PdfCommandTests`: `Pdf_has_no_frame` (exactly `pdf: unrecognized option '--frame'`).
  - `ImgToolTests`: `Frame_passes_through_to_the_sidecar`. `AttachmentMetricsTests`: `Frames_shown_are_counted`.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ImgTests" --filter-class "*PdfCommandTests"`. **Expected:** non-zero, with the new `ImgTests` failing. **Pins, which pass at red:** `Pdf_has_no_frame`, `A_plain_gif_of_unknown_count_says_todays_sentence`.
- [ ] **Green.** The option in `Spec` (`:11`). Parse after `--rotate`, before the path: `int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 1_000_000`. After the PDF refusal (`:69-72`): `frame is not null && state.MediaType != "image/gif"` gets the GIF sentence with `AttachmentText.FormatName(state)`. The head at `:99` gets `, frame N of M` or `, frame N`. `PictureText.ForImage` (`:44`) follows the table of spec §2.3. Help per spec §2.4, and add `[--frame N]` to the help's first line and to `Usage`. The metric: counter, `case`, `Metric("kumbu_img_frames_total")`, a panel in the Attachments row.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ImgTests" --filter-class "*PdfCommandTests" --filter-class "*HelpTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ImgToolTests" --filter-class "*AttachmentMetricsTests" --filter-class "*LabelGuardTests" --filter-class "*DashboardTests"`. **Expected:** exit 0, 0 failed.
- [ ] Commit: `git add src/Kumbu.Core/Sh/Commands/Img.cs src/Kumbu.Core/Sh/PictureText.cs src/Kumbu.Core/Attachments/IAttachmentPixels.cs src/Kumbu.Server/Metrics/KumbuMetrics.cs monitoring/grafana/dashboards/kumbu.json tests/Kumbu.Core.Tests/Sh/ImgTests.cs tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs tests/Kumbu.Server.Tests/Attachments/ImgToolTests.cs tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs && git commit -m "Let img show any frame of a GIF" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ImgTests"`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Drop the not-a-GIF check. Expected to fail: `A_frame_of_anything_but_a_gif_is_refused`.
  2. Allow frame 0. Expected to fail: `A_bad_frame_is_refused_with_its_sentence`.
  3. Show the "animated" note for one frame too. Expected to fail: `A_plain_gif_of_one_frame_has_no_note`.
  4. Keep the note with `--frame`. Expected to fail: `The_head_names_the_frame_of_the_count`.
- [ ] Reviews.
- [ ] **Phase gate: the full verification, each line:**

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

**Expected:** exit 0 for each; `dotnet test` failed 0, skipped 4; web 1346 tests; sidecar passing.

- [ ] The controller runs `git push -u origin feature/attachment-wishes`.

## Phase 3: `rg --regions`

### Task 8: `Crop.Enclosing`, and `pdf info` regions that fit the page (spec §3.3)

**Files:** `src/Kumbu.Core/Attachments/Crop.cs`; `src/Kumbu.Core/Sh/Commands/PdfCommand.cs:275,281`. **Test:** `tests/Kumbu.Core.Tests/Attachments/CropTests.cs`, `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs:386`.

**Produces:** `public static Crop? Crop.Enclosing(double x, double y, double width, double height, double pageWidth, double pageHeight)`: x and y rounded down, the far edges rounded up, then cut at the page's size rounded down; null if nothing is left.

- [ ] **Red.**
  - `CropTests`: `Enclosing_rounds_outward` (10.4, 20.6, 30, 40 on 595×842 gives `10,20,31,41`: its far edge 40.4 rounds differently to nearest, so it tells the two apart); `Enclosing_stays_within_the_page` (100.6, 10, 494.7, 20 on 595.3×842 gives `100,10,495,20`, and x + w = 595); `Enclosing_keeps_whole_numbers` (72, 300, 451, 260 stays); `Enclosing_of_a_box_past_the_edge_is_null` (595.1, 10, 5, 5 on 595.3×842).
  - `PdfCommandTests`: `Info_gives_a_figure_flush_with_the_edge_a_region_ready_for_crop` (a region 100.6, 10, 494.7, 20 on a page 595.3 wide: `drawing 100,10,495,20`, and `--crop 100,10,495,20` on that page is accepted).
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*CropTests"`. **Expected:** non-zero, compile error for `Crop.Enclosing`, which this task creates.
- [ ] **Green.** `Crop.Enclosing`. `InfoLine` (`PdfCommand.cs:275`) prints `Crop.Enclosing(r.X, r.Y, r.Width, r.Height, page.Width, page.Height)` through its `ToString`, leaving out a region that gives null. `Point` (`:281`) stays for page sizes (`:448`). `PdfCommandTests.cs:386` changes on purpose: y 20.4, h 40 now gives `{10 * i},20,31,41`.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*CropTests" --filter-class "*PdfCommandTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfToolTests"`. **Expected:** exit 0, 0 failed (`:67` holds whole numbers).
- [ ] Commit: `git add src/Kumbu.Core/Attachments/Crop.cs src/Kumbu.Core/Sh/Commands/PdfCommand.cs tests/Kumbu.Core.Tests/Attachments/CropTests.cs tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs && git commit -m "Keep pdf info's regions within the page" -m "A region now rounds outward, so an expected height in PdfCommandTests changes on purpose." -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*CropTests" --filter-class "*PdfCommandTests"`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Don't cut at the page. Expected to fail: `Enclosing_stays_within_the_page`.
  2. Round the far edges to nearest. Expected to fail: `Enclosing_rounds_outward`.
  3. Cut at the page's size rounded up. Expected to fail: `Enclosing_stays_within_the_page`.
  4. `InfoLine` back to `Point`. Expected to fail: `Info_gives_a_figure_flush_with_the_edge_a_region_ready_for_crop`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 9: The sidecar's `/text-boxes` (spec §3.3)

**Files:** new `pdf/src/boxes.ts`, new `pdf/test/boxes.test.ts`; `pdf/src/analyse.ts:67-79` (uses the shared text function, keeps its own coverage boxes); `pdf/src/classify.ts:75` (export `clip`); `pdf/src/pool.ts:12-15`; `pdf/src/worker.ts:17-28`; `pdf/src/server.ts:79-101`; `pdf/test/fixtures.ts`, `pdf/test/server.test.ts`.

**Produces:**
- `export type ItemBox = { at: number; end: number; x: number; y: number; w: number; h: number }`
- `export type PageBoxes = { n: number; width: number; height: number; text: string; items: ItemBox[] }`
- `export const MAX_ITEMS = 20_000`
- `export function pageText(items: unknown[]): { text: string; spans: { item: TextItem; at: number; end: number }[] }`, shared with `analyse.ts`
- `export function itemBox(item: TextItem, viewport: PageViewport): Box | null`, per "A box, correctly" of spec §3.3 and clipped to the page
- `export function boxesOf(page: { n: number; width: number; height: number }, items: unknown[], viewport: PageViewport, maxItems = MAX_ITEMS): PageBoxes`
- `export async function textBoxes(bytes: Uint8Array, pages: number[]): Promise<{ pages: PageBoxes[] }>`
- `Job` kind `{ kind: "boxes"; bytes: Uint8Array; pages: number[] }`; `POST /text-boxes?pages=7,12`

- [ ] **Red.** Fixtures: `turnedTextPage()` (pdf-lib `drawText` with `rotate: degrees(90)`), `cropBoxTextPage()` (one line inside the CropBox, one across its edge, one wholly outside), `trailingSpacesPage()` (a last line `Total   `). In `boxes.test.ts`:
  - `gives a horizontal item its box in displayed points`; `gives text drawn at 90 degrees a tall, narrow box`; `counts boxes on a /Rotate page as displayed`.
  - `clips a box to the CropBox, and leaves out an item wholly outside`; `gives no number below zero on any fixture`.
  - `answers the very text analyse stores` (`textPage`, `chartPage`, `tablePage`, `threePages`, `rotatedPage`, `scanPage(true)`: equal per page to `analyse(…).pages[i].text`).
  - `keeps offsets inside the trimmed text, ascending` (`trailingSpacesPage`: every `end <= text.length`, every `at >= ` the previous `end`); `has no entry for an item of white space`; `has no box for an item with a zero vector` (`itemBox` with a transform of zeros).
  - `answers items: [] above the limit of items, with the text` (`boxesOf` with `maxItems` 2 on three fake items).
  - `refuses a page the PDF lacks`, `refuses what is not a PDF`, `refuses an encrypted PDF` (`no_such_page`, `not_pdf`, `encrypted`, from `encryptedPdf()`).
  - In `server.test.ts`: `answers /text-boxes as JSON for the pages asked`; `refuses more than 50 pages, none, duplicates, a falling order, and anything but whole numbers` (400, `bad_request`).
- [ ] Run `pnpm -C pdf exec vitest run test/boxes.test.ts test/server.test.ts`. **Expected:** a non-zero exit: `boxes.test.ts` can't find `../src/boxes.js`, the module this task creates, and the route tests get 404.
- [ ] **Green.** `boxes.ts` per the signatures. `pageText`: as `analyse.ts:68-79`: skip what has no `str`; append `str`, then `\n` after `hasEOL`; `trimEnd` last; a span for each item whose `str.trim() !== ""`, with `end` clamped to the trimmed length (and no span if nothing is left). `itemBox`: from `viewport.convertToViewportPoint` of the four corners, per the unit vectors of spec §3.3; a zero vector gives null; clip with `clip` from `classify.ts`, null if nothing is left; round to one decimal. Items with no box have no entry. In `analyse.ts`, take `text` from `pageText` and leave `textBoxes` at `:76-77` exactly as it is. In `server.ts`: `/text-boxes` under `ms.renderMs`, with a parser for `pages` that takes each part by the same `/^\d{1,16}$/` as `integer`, at most 50, strictly ascending. `worker.ts`: `case "boxes"`.
- [ ] Run `pnpm -C pdf exec vitest run test/boxes.test.ts test/server.test.ts test/analyse.test.ts test/classify.test.ts`. **Expected:** exit 0, 0 failed.
- [ ] Run `pnpm -C pdf test`. **Expected:** exit 0: type check clean.
- [ ] Commit: `git add pdf/src/boxes.ts pdf/src/analyse.ts pdf/src/classify.ts pdf/src/pool.ts pdf/src/worker.ts pdf/src/server.ts pdf/test/boxes.test.ts pdf/test/fixtures.ts pdf/test/server.test.ts && git commit -m "Let the sidecar say where a page's text lies" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `pnpm -C pdf exec vitest run test/boxes.test.ts test/server.test.ts`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Treat every item as horizontal (as `analyse.ts:76-77` does). Expected to fail: `gives text drawn at 90 degrees a tall, narrow box`.
  2. Don't clip. Expected to fail: `gives no number below zero on any fixture`.
  3. Don't clamp `end`. Expected to fail: `keeps offsets inside the trimmed text, ascending`.
  4. Give white space an entry. Expected to fail: `has no entry for an item of white space`.
  5. Build `text` differently from `analyse` (no `trimEnd`). Expected to fail: `answers the very text analyse stores`.
  6. Allow 51 pages. Expected to fail: `refuses more than 50 pages, none, duplicates, a falling order, and anything but whole numbers`.
  7. Remove the items limit. Expected to fail: `answers items: [] above the limit of items, with the text`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 10: The server's `TextBoxesAsync` (spec §3.3, §5)

**Files:** `src/Kumbu.Core/Attachments/IAttachmentPixels.cs` (the records, and the method beside `PageTextAsync`, `:72`); `src/Kumbu.Server/Attachments/PdfSidecar.cs:15-25,66-69,271-337`; `src/Kumbu.Server/Attachments/AttachmentPixels.cs:85-88`. **Implementers:** `tests/Kumbu.Server.Tests/Attachments/FakePdfSidecar.cs`, `RefusingSidecar` at `AttachmentPixelsTests.cs:118`, `FakePixels` at `tests/Kumbu.Core.Tests/Sh/ImgTests.cs:17`, `PdfCommandTests.cs:18`, `RgPdfTests.cs:14`, `SemsearchPdfTests.cs:21` (they throw `NotSupportedException` for now). **Test:** `tests/Kumbu.Server.Tests/Attachments/PdfSidecarTests.cs`, `AttachmentPixelsTests.cs`.

**Consumes:** `POST /text-boxes?pages=…` and its JSON (Task 9).

**Produces:**
- `public sealed record TextBox(int At, int End, double X, double Y, double Width, double Height);`
- `public sealed record PageTextBoxes(int Number, double Width, double Height, string Text, IReadOnlyList<TextBox> Items);`
- `IAttachmentPixels.TextBoxesAsync(VaultPath path, AttachmentState state, IReadOnlyList<int> pages, CancellationToken ct)` returning `Task<IReadOnlyList<PageTextBoxes>>`, throwing `PixelsUnavailableException` on failure
- `IPdfSidecar.TextBoxesAsync(ReadOnlyMemory<byte> pdf, IReadOnlyList<int> pages, CancellationToken ct)`
- `internal static PdfSidecar.ParseTextBoxes(byte[] body, IReadOnlyList<int> pages)`, `internal const int MaxTextBoxBytes = 16 * 1024 * 1024`
- `FakePdfSidecar.TextBoxes`: a `Func<IReadOnlyList<int>, IReadOnlyList<PageTextBoxes>>`, honouring `Down`

- [ ] **Red.**
  - `PdfSidecarTests`: `Text_boxes_are_asked_by_page_list` (the query is `pages=7,12`); `Text_boxes_parse`; `Malformed_text_boxes_are_refused`, a theory, each `SidecarException` with code `failed`: pages other than asked, in another order, `at` above `end`, `end` past the text, `at` below the previous `end`, a negative `x`, an infinite `w` (`1e999`), a missing `text`, a missing `items`, not JSON; `Text_boxes_over_16_MiB_are_refused`; `No_such_page_from_text_boxes_is_that_code`.
  - `AttachmentPixelsTests`: `Text_boxes_read_the_blob_with_the_requests_credential` (bytes reach the fake from the caller's vault, as `:46`); `Text_boxes_from_a_stopped_sidecar_are_unavailable` (`PixelsProblem.Unavailable`, the message `PDF rendering is unavailable right now`); `Text_boxes_store_nothing` (the stored analysis is byte-equal before and after).
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*AttachmentPixelsTests"`. **Expected:** non-zero: compile errors only for the members this task creates.
- [ ] **Green.** Per the signatures, and with the model of `ParseAnalysis` (`PdfSidecar.cs:280-337`): private JSON records with `N, Width, Height, Text, Items` and `At, End, X, Y, W, H`; its `Point` check for every number; `RenderTimeout`. `AttachmentPixels.TextBoxesAsync`: `BytesAsync` (`:85-88`), then the sidecar; a `SidecarException` goes through `Refused(e, "PDF rendering", path, null, null, null)`. No metrics here.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*AttachmentPixelsTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ImgTests" --filter-class "*PdfCommandTests" --filter-class "*RgPdfTests" --filter-class "*SemsearchPdfTests"`. **Expected:** exit 0, 0 failed.
- [ ] Commit: `git add src/Kumbu.Core/Attachments/IAttachmentPixels.cs src/Kumbu.Server/Attachments/PdfSidecar.cs src/Kumbu.Server/Attachments/AttachmentPixels.cs tests/Kumbu.Core.Tests/Sh tests/Kumbu.Server.Tests/Attachments && git commit -m "Ask the sidecar for text boxes, and check the answer" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*AttachmentPixelsTests"`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Drop the check that pages are as asked. Expected to fail: `Malformed_text_boxes_are_refused`.
  2. Drop the ascending check. Expected to fail: `Malformed_text_boxes_are_refused`.
  3. Drop the offsets-within-the-text check. Expected to fail: `Malformed_text_boxes_are_refused`.
  4. Drop the finite check. Expected to fail: `Malformed_text_boxes_are_refused`.
  5. Read without the size limit. Expected to fail: `Text_boxes_over_16_MiB_are_refused`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 11: `rg --regions` (spec §3.1, §3.2, §3.3, §3.4, §3.5, §3.6, §3.7, §7)

**Files:** `src/Kumbu.Core/Sh/Commands/Rg.cs:22-29,44-50,66-67,88-91,199-201,286-356,467-509`; `src/Kumbu.Core/Sh/Commands/PdfCommand.cs:63`; `src/Kumbu.Core/Attachments/IAttachmentPixels.cs:55`; `src/Kumbu.Server/Metrics/KumbuMetrics.cs` (a labelled counter near `:339`, the switch `:398-420`, `AllowedLabels` near `:596`, pre-created labels near `:70`); `monitoring/grafana/dashboards/kumbu.json`. **Test:** `tests/Kumbu.Core.Tests/Sh/RgPdfTests.cs`, `tests/Kumbu.Server.Tests/Attachments/PdfToolTests.cs`, `tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs`.

**Consumes:** `IAttachmentPixels.TextBoxesAsync`, `PageTextBoxes`, `TextBox`, `FakePdfSidecar.TextBoxes` (Task 10); `Crop.Enclosing` (Task 8).

**Produces:** `public enum RegionOutcome { Delivered, Limited, Unavailable }`, `public sealed record RegionsAsked(RegionOutcome Outcome) : AttachmentUse;`, one count per PDF with matches; metric `kumbu_rg_regions_total{outcome=delivered|limited|unavailable}`; constants `MaxRegionPdfs = 8`, `MaxRegionPages = 50`, `MaxRegionsPerLine = 3`; `internal TimeSpan RegionBudget { get; init; } = TimeSpan.FromSeconds(20);` on `Rg` (`Kumbu.Core.Tests` sees internals).

- [ ] **Red.** In `RgPdfTests` (give its `FakePixels` a `Func<string, IReadOnlyList<int>, IReadOnlyList<PageTextBoxes>>? Boxes`, a `List<(string Path, IReadOnlyList<int> Pages)> BoxRequests` and an `Exception? BoxesFail`; expected numbers below follow the rule of spec §3.3, point 4):
  - `A_match_gets_its_region_before_the_text`: stored text `Net revenue (after adjustments)`, one item `at 0, end 31, 74.2, 300.1, 210.3, 11.0`: exactly `/V/p.pdf:p7:crop=70,296,218,19: Net revenue (after adjustments)`.
  - `With_n_the_region_follows_the_line_number` (`/V/p.pdf:p7:12:crop=…: text`).
  - `Two_matches_in_a_line_give_two_regions`: items at 310, 507, 90, 10 and 470, 507, 90, 10: `crop=307,504,96,16;467,504,96,16`.
  - `Equal_boxes_in_a_line_count_once` (two matches in one item).
  - `At_most_three_regions_in_a_line`: five items: `crop=a;b;c;+2 more: …`.
  - `A_match_over_two_items_is_their_union`; `Small_text_grows_by_two_points` (100, 200, 50, 5 gives `98,198,54,9`); `A_region_stays_on_the_page`.
  - `A_match_in_the_last_line_of_a_page_is_found_by_its_offset` (several lines; the offset comes from adding up lengths).
  - `Context_lines_print_as_today` (`-C 1`: `/V/p.pdf:p7- a line`); `The_group_separator_stays`.
  - `A_note_line_prints_as_today` (`/V/plan.md:2:needle in a note`).
  - `A_zero_width_match_prints_as_today` (`rg --regions 'x*' …`); `A_match_only_in_white_space_prints_as_today`.
  - `A_forged_crop_in_the_text_stays_text`: stored text `crop=1,1,1,1: forged`, no region for it: printed as a plain line; with a region: `…:p1:crop=<real>: crop=1,1,1,1: forged`.
  - `The_refusals_name_what_they_refuse` (a theory: `-l`, `-c`, `-v`, `--files`, `--tag t`, `--version 1`, and right of a pipe: exactly `rg: --regions says where matches lie in PDFs; it doesn't go with -l, -c, -v, --files, --tag or --version, nor right of a pipe`).
  - `A_pipe_to_the_right_is_fine` (`rg --regions needle /V | head -1` holds `crop=`).
  - `Boxes_are_asked_once_a_pdf_for_the_pages_with_matches` (`BoxRequests` holds one entry, pages 7 and 12).
  - `More_than_eight_pdfs_get_no_regions_after_the_eighth` (exactly the notice `Regions only for the first 8 PDFs with matches; name a PDF to get its own.`, nine PDFs, all matches printed, 8 requests).
  - `More_than_fifty_pages_get_no_regions_after_the_fiftieth` (exactly `Regions for /V/p.pdf only on its first 50 pages with matches; use -m, or look at one page: pdf /V/p.pdf 51.`; a request of 50 pages).
  - `A_stopped_sidecar_loses_no_match` (exactly `No regions: PDF rendering is unavailable right now. The matches are all here.`; the lines print as today); `A_stopped_sidecar_is_asked_once_a_search` (three PDFs, one request); `Any_failure_of_the_boxes_loses_no_match` (`PixelsUnavailableException` with `Failed`).
  - `Text_that_differs_gets_no_region_on_that_page` (exactly `No regions on some pages of /V/p.pdf: its stored text comes from an older analysis. pdf info /V/p.pdf renews it.`; the other page keeps its region).
  - `A_failure_of_the_text_part_way_keeps_the_lines_held` (`TextFailsFrom`: the matches found before the failure print, with regions).
  - `The_notice_joins_the_note_about_other_writers` (with `WriterMarks`: both lines).
  - `Plain_rg_asks_for_no_boxes` (`BoxRequests` is empty, and the output of the existing `/V/p.pdf:p7: text` case is unchanged).
  - `Past_the_time_budget_no_further_pdf_is_asked`: `new Rg(_store, _pixels) { RegionBudget = TimeSpan.Zero }` over three PDFs: one request, all matches printed, and exactly the notice `No regions: PDF rendering is unavailable right now. The matches are all here.`.
  - `The_help_names_regions` (spec §3.6's text); and `help pdf` holds `rg --regions also says where on the page a match lies.`.
  - `PdfToolTests`: `Regions_work_through_mcp_with_the_callers_credential`. `AttachmentMetricsTests`: `Regions_are_counted_by_outcome` (each of the three).
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*RgPdfTests"`. **Expected:** non-zero: the new tests fail (`rg: unrecognized option '--regions'` for most), the old ones pass. **Pin, which passes at red:** `Plain_rg_asks_for_no_boxes`.
- [ ] **Green.**
  - `Rg.cs:22-29`: `new(null, "regions")`. The refusal goes before `:93`, next to `:88-91`.
  - `Search` gains `Spans(string line)`: `(int Start, int Length)` from `regex.EnumerateMatches(line)`, skipping zero length.
  - `AddPdfLinesAsync`: without `--regions` its path stays as it is. With it, at `:325-330`, hold `(page, lines, matches)` for this PDF, and add the held lines' size to `size` so that reading stops where it would have. After the reading loop, **also when the `catch` at `:339` was taken**, call `TextBoxesAsync` once for the first 50 pages with matches, unless this search is past 8 PDFs, has already failed, or, from its second PDF on, has spent `RegionBudget` by a `Stopwatch` started at its first call. Past the budget the PDF prints without regions, with the "No regions" notice; a call under way isn't cut, as its own limit bounds it. Then render the held pages in order, with `--` as at `:327`.
  - `search.Render` already passes the line number (`:491`), so the format callback can look regions up by `n - 1`.
  - For a page: if `page.Text != stored`, by ordinal comparison, it has no regions, and the PDF gets the notice. A line's offset is the sum of `length + 1` over the lines before it. A match's box is the union of the items with `at < end` and `end > start`. Grow by `max(2, 0.3 × short side)`. `Crop.Enclosing` with the page's `Width` and `Height`; null gives no region. Equal crops once. Three, then `;+N more`.
  - `Rg.cs:201`: join the notices with the writers' note by `\n`, each once. Count `RegionsAsked` once per PDF with matches.
  - Help per spec §3.6; `PdfCommand.cs:63` per spec §3.6.
  - The metric, its `AllowedLabels` entry, pre-created labels, a panel in the Attachments row.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*RgPdfTests" --filter-class "*RgTests" --filter-class "*PdfCommandTests" --filter-class "*HelpTests" --filter-class "*PipelineTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfToolTests" --filter-class "*AttachmentMetricsTests" --filter-class "*LabelGuardTests" --filter-class "*DashboardTests"`. **Expected:** exit 0, 0 failed.
- [ ] Commit: `git add src/Kumbu.Core/Sh/Commands/Rg.cs src/Kumbu.Core/Sh/Commands/PdfCommand.cs src/Kumbu.Core/Attachments/IAttachmentPixels.cs src/Kumbu.Server/Metrics/KumbuMetrics.cs monitoring/grafana/dashboards/kumbu.json tests/Kumbu.Core.Tests/Sh/RgPdfTests.cs tests/Kumbu.Server.Tests/Attachments/PdfToolTests.cs tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs && git commit -m "Let rg say where in a PDF its matches lie" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*RgPdfTests"`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Print the region after the text. Expected to fail: `A_match_gets_its_region_before_the_text`.
  2. Union per line rather than per match. Expected to fail: `Two_matches_in_a_line_give_two_regions`.
  3. Skip the text comparison. Expected to fail: `Text_that_differs_gets_no_region_on_that_page`.
  4. Drop the growth. Expected to fail: `A_match_gets_its_region_before_the_text`.
  5. Start each line's offset without the `+ 1` for the line end. Expected to fail: `A_match_in_the_last_line_of_a_page_is_found_by_its_offset`.
  6. Flush held lines only on success. Expected to fail: `A_failure_of_the_text_part_way_keeps_the_lines_held`.
  7. Keep asking after a failure. Expected to fail: `A_stopped_sidecar_is_asked_once_a_search`.
  7b. Ignore the time budget. Expected to fail: `Past_the_time_budget_no_further_pdf_is_asked`.
  8. Remove the limit of 8. Expected to fail: `More_than_eight_pdfs_get_no_regions_after_the_eighth`.
  9. Remove the limit of 50. Expected to fail: `More_than_fifty_pages_get_no_regions_after_the_fiftieth`.
  10. Remove the pipe refusal. Expected to fail: `The_refusals_name_what_they_refuse`.
- [ ] Reviews.
- [ ] **Phase gate: the full verification, each line:**

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

**Expected:** exit 0 for each; `dotnet test` failed 0, skipped 4; web 1346 tests; sidecar passing.

- [ ] The controller runs `git push -u origin feature/attachment-wishes`.

## Phase 4: what goes with it

### Task 12: Docs, and the playbook's new steps (spec §5, §7)

**Files:** `docs/concept.md:222,249-250` (§12); `docs/attachments-client-research.md:279`; `docs/security-architecture.md:54,632`; `docs/test-playbook-attachments.md:92` (step 6), `:101-112` (section C), `:131-142` (the results table); `tools/attachment-probes/make_probes.py:141,193-200,217`. `THIRD-PARTY-NOTICES.md` has `omggif` from Task 5, and `README.md` needs nothing.

There is no red test here: these are documents and a probe script. The check is the command below.

- [ ] Edit:
  - `docs/concept.md`: at `:222`, the release's content in a sentence; beside `:249`, `img <path> [--size S] [--rotate D] [--crop x,y,w,h] [--frame N]`; beside `:250`, `pdf`'s `--rotate`; and `rg --regions`.
  - `docs/attachments-client-research.md:279`: what was built and what was left out, and why. If Task 14 hasn't run yet, leave the sentence on regions with labels to Task 14.
  - `docs/security-architecture.md:54`: the sentence of spec §5: `--regions` sends files that a plain search wouldn't, on the caller's explicit request. `:632`: add `POST /text-boxes` to the routes, and that `omggif` decodes GIF frames in the worker under the same limits, with the frame rectangle checked first. Keep every file-and-line citation it makes true where you touch it.
  - `tools/attachment-probes/make_probes.py`: `:198` no longer says "no right answer yet", and the key asks for frame 2's colour. A new `sideways()`, after `rotated` at `:141`: `sideways.png`, a sheet stored on its side, no EXIF, a code in small type, called from `main` (`:217`).
  - `docs/test-playbook-attachments.md`, per spec §7: step 6 asks a question only frame 2 can answer; a new step for `sideways.png`: "read the code, then crop it, and say how many tries it took"; section C: "find this word with `rg --regions`, then crop it. Did the crop hit it?"; a row for each in the results table. The step on regions with labels belongs to Task 14. New steps get new numbers at the end of their section, without renumbering the old ones if the results table cites them; adjust the table's citations if you must renumber.
- [ ] Run `uv run tools/attachment-probes/make_probes.py "$HOME/probes-check" --seed 7`. **Expected:** exit 0; `ls "$HOME/probes-check/upload"` lists `sideways.png`; `rg -n "frame 2|Frame 2" "$HOME/probes-check/key"` finds the question's answer. `rm -rf "$HOME/probes-check"` afterwards: nothing generated is committed.
- [ ] Run `rg -n "no right answer yet" tools docs/test-playbook-attachments.md`. **Expected:** no hit.
- [ ] Commit: `git add docs/concept.md docs/attachments-client-research.md docs/security-architecture.md docs/test-playbook-attachments.md tools/attachment-probes/make_probes.py && git commit -m "Document what clients asked for, and add the playbook's steps" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutation:** in `make_probes.py`, leave `sideways` out of `main`; run `uv run tools/attachment-probes/make_probes.py "$HOME/probes-check" --seed 7`; `ls "$HOME/probes-check/upload"` lacks `sideways.png`. Restore with `git checkout -- tools/attachment-probes/make_probes.py` and `rm -rf "$HOME/probes-check"`.
- [ ] Reviews, then the controller runs `git push -u origin feature/attachment-wishes`.

### Task 13: Hands-on, through MCP (spec §8)

**Files:** new `docs/notes-attachment-wishes-hands-on.md`, in the form of `docs/notes-attachments-model-hands-on.md` (set-up, checks, "What I did and saw", found, not tested). Everything generated stays neutral and outside the repository, in `$HOME/wishes`.

No red test or mutations of its own: this task writes no code. A bug it finds gets a failing test first, a fix, and its mutation, in a commit of its own.

- [ ] **The stack.** Use `$KUMBU_TEST_MONGO` if set. Otherwise start a MongoDB: `docker rm -fv kumbu-test-mongo; docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all`, then, after a few seconds, `docker exec kumbu-test-mongo mongosh --quiet --eval 'rs.initiate({_id:"rs0",members:[{_id:0,host:"localhost:27017"}]})'`. **Expected:** `ok: 1` or `{ ok: 1 }`. Then, with a fresh token that goes in no log or document:

```sh
export KUMBU_DEV_TOKEN="$(openssl rand -hex 32)"
pnpm -C pdf build
PORT=8090 pnpm -C pdf start > "$HOME/kumbu-pdf.log" 2>&1 &
Kumbu__Mongo__ConnectionString="${KUMBU_TEST_MONGO:-mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true}" \
Kumbu__Mongo__Database=kumbu-handson Kumbu__Search__Enabled=false Kumbu__DevToken="$KUMBU_DEV_TOKEN" \
Kumbu__Pdf__Url=http://127.0.0.1:8090 ASPNETCORE_URLS=http://127.0.0.1:8099 \
  dotnet run --project src/Kumbu.Server --no-launch-profile > "$HOME/kumbu-server.log" 2>&1 &
until curl -fsS http://127.0.0.1:8099/healthz >/dev/null; do sleep 1; done
```

**Expected:** `healthz` answers, and `/Demo` is seeded. A call through MCP in the stateless form the earlier hands-on used:

```sh
sh_call() { jq -n --arg c "$1" '{jsonrpc:"2.0",id:1,method:"tools/call",params:{name:"sh",arguments:{command:$c}}}' \
  | curl -sS -H "Authorization: Bearer $KUMBU_DEV_TOKEN" -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
      --data-binary @- http://127.0.0.1:8099/mcp \
  | sed -e '/^event:/d' -e 's/^data: //'; }
sh_call 'ls /Demo'
```

**Expected:** a JSON answer listing the vault. If the server asks for `initialize` first, send it and keep the `Mcp-Session-Id` header. Print text blocks with `jq -r '.result.content[] | select(.type=="text") | .text'`. Decode images with `jq -r '.result.content[] | select(.type=="image") | .data' | base64 -d` and **look at each**.

- [ ] **Files.** Generate in `$HOME/wishes`, neutral: `sheet.jpg` (3000×2000, a code in small type, sideways), `mix.pdf` (15 pages, page 7 one full-page image of a sideways sheet), `animated.gif` (`uv run tools/attachment-probes/make_probes.py "$HOME/wishes/probes" --seed 11`, three frames of 600×400), `report.pdf` (23 pages, `Net revenue` on pages 7 and 12, page 12 in two columns of a table), `notes.md` with that phrase. Upload with `curl -sS -X PUT -H "Authorization: Bearer $KUMBU_DEV_TOKEN" --data-binary @<file> http://127.0.0.1:8099/api/files/Demo/<path>`. **Expected:** 201 each.
- [ ] **Each worked example of the spec, with its real output quoted** in the note: §1.6 (`pdf … 7 --images=all --rotate 90`; a crop read off the picture; `img … --rotate 90`), §2.5 (`img`, `--frame 2`, `--frame 7`), §3.7 (`rg --regions -i 'net revenue' /Demo/…`, then `pdf … --crop <its numbers>`: the crop must show the line). Also each refusal of spec §1.4, §2.3 and §3.1, one by one. Stop the sidecar (`fuser -k 8090/tcp`), run `rg --regions` again (**Expected:** the matches and the notice), and start it again. **Expected:** every sentence as the spec has it, or a change you make and explain in the note.
- [ ] **Checks in the note:** the test counts of the last phase gate, and a checklist for the maintainer: the client round of the playbook's new steps, which this run can't do, as it has neither the clients nor the private PDFs; and that both images must come from the same tag.
- [ ] Stop the stack: `fuser -k 8099/tcp; fuser -k 8090/tcp`. Then `rg -n "$KUMBU_DEV_TOKEN" docs/notes-attachment-wishes-hands-on.md`. **Expected:** no hit.
- [ ] **The memory of the composing path, measured.** Task 5's cap rests on an estimate close to the limit. Build the sidecar's image: `docker build --build-context root=. -t kumbu-pdf-mem pdf`. Run it as `compose.yaml` limits it: `docker run -d --name kumbu-pdf-mem --memory=1536m --memory-swap=1536m --cpus=1.5 -p 8091:8090 kumbu-pdf-mem`. Generate in `$HOME/wishes/mem` two GIFs of 4000×4000 (16 MP) under 16 MiB, each with several full-canvas frames with disposal 3 and then as many tiny frames as fit. Send two `POST /render/image?longEdge=2576&pixels=3700000&bytes=1048576&frame=<a late frame>` at once, with 16 more 16 MiB bodies queued behind them, and repeat for about a minute; 503 and 504 answers are fine. Read `docker exec kumbu-pdf-mem cat /sys/fs/cgroup/memory.peak` and `docker inspect -f '{{.State.OOMKilled}} {{.RestartCount}}' kumbu-pdf-mem`. **Expected:** `false 0`, and a peak below 1536 MiB. Record the peak in the note and the report. Then `docker rm -fv kumbu-pdf-mem`.
  - **On an OOM:** lower `MAX_FRAME_PIXELS`, measure again, and say so in the report and the pull request. Change the sentences with it: `pdf/src/image.ts`, `AttachmentPixels.Refused`, spec §2.2 and §2.3, and the tests naming 16 megapixels. Commit with `git add pdf/src/image.ts pdf/test/image.test.ts src/Kumbu.Server/Attachments/AttachmentPixels.cs tests/Kumbu.Server.Tests/Attachments/AttachmentPixelsTests.cs docs/superpowers/specs/2026-11-02-attachment-wishes-design.md && git commit -m "Lower the frame cap to what the sidecar's memory holds" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.
  - **If the image can't be built or Docker can't run:** try the built `pdf/dist` with its `node_modules` mounted in `node:26-bookworm-slim` under the same limits. If that can't run either, write "memory not measured" in the report and the pull request, as a hand check for the maintainer.
- [ ] If this finds a bug, fix it with a failing test first, in the file concerned, as a commit of its own, and run its test class.
- [ ] Commit: `git add docs/notes-attachment-wishes-hands-on.md && git commit -m "Note the hands-on test of the attachment wishes" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] The controller runs `git push -u origin feature/attachment-wishes`.

If the stack can't be started at all after a fair try, write that in the note and in the report, and go on: the tests stand.

## The attempt, last

### Task 14: Regions with a figure's labels (spec §4)

Attempted and honestly reported. If it misses the measure it stays out and everything else ships. Nothing before this task depends on it. **Budget: about 3 hours.** Past that, take the verdict on the constants you have rather than tuning further; if the corpus isn't there by then, the verdict is "not measured". Keep the corpus and the eval (stage A) in commits apart from the rule (stage B), so that the rule alone can be reverted.

**Files, stage A:** new `tools/attachment-probes/make_figure_pdfs.py`; new `pdf/eval/regions.ts`, `pdf/eval/figures.ts`; new `pdf/tsconfig.eval.json` (extends `tsconfig.json`, `rootDir` `.`, `outDir` `eval-dist`, includes `src` and `eval`); `pdf/package.json` scripts `"eval:regions": "tsc -p tsconfig.eval.json && node eval-dist/eval/regions.js"` and `"eval:figures": "tsc -p tsconfig.eval.json && node eval-dist/eval/figures.js"`; `pdf/tsconfig.test.json` (add `eval` to `include`, so it is type-checked); `pdf/.gitignore` (`eval-dist/`). `pdf/Dockerfile` copies only `src`, so none of this enters the image.

**Files, stage B:** `pdf/src/classify.ts:12-14,25,67-72`; `pdf/src/analyse.ts:76-77`; `pdf/test/classify.test.ts`, `pdf/test/analyse.test.ts:33`; `src/Kumbu.Server/Attachments/PdfAnalyses.cs:42,436-441`; `src/Kumbu.Core/Attachments/PdfAnalysis.cs:7`; `src/Kumbu.Server/Attachments/PdfSidecar.cs:277,320-325`; `src/Kumbu.Core/Sh/Commands/PdfCommand.cs:57-58,270-279`; `tests/Kumbu.Server.Tests/Attachments/PdfSidecarTests.cs`, `PdfAnalysisStoreTests.cs`; `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs`. The consistency test at `PdfSidecarTests.cs:209-216` checks that `RULES_VERSION` and `PdfAnalyses.Analyser` agree.

**Stage A: the corpus and the eval (these stay, whatever the verdict).**

- [ ] Install what is needed: `apt-get update && apt-get install -y --no-install-recommends libreoffice-calc fonts-liberation`. Run `soffice --version`. **Expected:** a version line. If `uv` is missing: `curl -LsSf https://astral.sh/uv/install.sh | sh`. If LibreOffice can't run after a fair try, the verdict is **"not measured"**: no stage B commit will exist, so go to stage D and skip its revert.
- [ ] Write `make_figure_pdfs.py <out> --seed N` per spec §4.5 (uv script header as in `make_probes.py:1-5`, with `matplotlib` and `openpyxl`): matplotlib with `pdf.fonttype 42`; `.xlsx` through openpyxl and `soffice --headless -env:UserInstallation=file://$HOME/.lo-eval --convert-to pdf`; pdf-lib by running `pnpm -C pdf eval:figures <out>/seed-N/pdf-lib --seed N`. The eight layouts and the controls of spec §4.5, tokens `zq<Letter><digit>` on every label, tick label, axis title and legend entry (Calc through a number format such as `"zqA3 "0`), body text from a word list with no tokens and no digits, captions starting `Figure N`. Nothing is committed but the scripts. Output per seed: `<out>/seed-N/<generator>/<layout>-<i>.pdf`, and beside each a manifest `<layout>-<i>.json` per figure: `{ "figure": 1, "token": "zqA3", "labels": ["zqA3 north", …], "minTickLabels": 4 }`, written by the generator from what it asked to be drawn, not read back from the PDF (a generator that can't know its tick count gives the fewest it can rely on). The words in labels come from a list of their own, which shares no word with the body's word list; the script asserts that.
- [ ] Write `pdf/eval/regions.ts`, run as `pnpm -C pdf eval:regions <out> --seeds 1-5`: `analyse` and `textBoxes` in-process on each PDF; the two conditions of spec §4.5 ("complete" at 90 % by area, "clean" by centre); judge by `labelled` where a region has one, by the tight box otherwise; identity from the text pdf.js gives. Every text item falls into one class: **token** (holds `zq<Letter><digit>`), **word list** (made wholly of the body's words), **`Figure N`** (a caption, counted for neither side), or **unidentified**. Unidentified items count for neither side and are reported, per generator, as a failure kind of their own. Labels are matched on the joined text of neighbouring items on one line, so that `zqA3 north` split in two is still its label, and each fragment counts as part of it. **A figure also fails "complete" if anything in its manifest is missing:** a label not found even so, or fewer items with its token than `minTickLabels`, so that tokens that fragment or vanish can't pass with nothing to check. A table per generator and per layout, and failures by kind; the bars 90 / 80 / 70 and at least 60 figures. For the controls of the seeds in `<out>/baseline.json` and the fixtures of `pdf/test/`, a comparison of page types and tight regions with that baseline, made by `--baseline`, whichever seeds are being judged; on the controls of the judged seeds, the check that no `with labels` region contains a body line.
- [ ] Add one fast Vitest, `pdf/test/eval.test.ts`: `judges a figure by token: complete and clean, and each way it can fail` (hand-made items, regions and manifests, including a token that is missing, a label split into fragments that still counts as whole and doesn't count against "clean", and an item of each of the four classes). Run `pnpm -C pdf exec vitest run test/eval.test.ts`. **Expected:** exit 0.
- [ ] Generate and measure the rule as it stands (no `labelled` yet), and record the numbers: `for s in 1 2 3 4 5; do uv run tools/attachment-probes/make_figure_pdfs.py "$HOME/fig" --seed $s; done`, then `pnpm -C pdf eval:regions "$HOME/fig" --seeds 1-5 --baseline`. **Expected:** exit 0, a table with at least 20 figures per generator, and the verdict "fails" on completeness: that the tight region alone leaves out the labels is the problem of spec item 4.
- [ ] Run `pnpm -C pdf test`. **Expected:** exit 0.
- [ ] Commit: `git add tools/attachment-probes/make_figure_pdfs.py pdf/eval pdf/tsconfig.eval.json pdf/tsconfig.test.json pdf/package.json pdf/.gitignore pdf/test/eval.test.ts && git commit -m "Measure regions with a corpus of generated figures" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`. The controller pushes: `git push -u origin feature/attachment-wishes`.

**Stage B: the rule.**

- [ ] **Red.** `classify.test.ts`: `a legend right of a chart joins its region`; `a chain follows a legend's second line`; `a long caption stops the growth`; `a label closer to another figure stays with that figure`; `a label inside another region stays there`; `a full page's image gains nothing`; `above 5,000 text boxes nothing grows`; `the same boxes give the same answer, in any order`; `no fixture changes its page type or tight region` (`textPage`, `chartPage`, `logoPage`, `scanPage`, `tablePage`, `whiteBackgroundPage`, `photoPage`, against literal expected values). `PdfSidecarTests`: `An_analysis_with_a_labelled_box_parses`; `A_malformed_labelled_box_is_refused`. `PdfAnalysisStoreTests`: `An_analysis_stored_before_labelled_regions_reads_without_them`; `A_labelled_box_survives_the_store`. `PdfCommandTests`: `Info_prints_a_region_with_labels` (exactly `… regions (pt, x,y,w,h): drawing 72,300,451,260 with labels 48,290,520,300; image 80,90,200,120`); `An_entry_without_labels_stays_as_today`; `The_help_says_what_with_labels_is`.
- [ ] Run `pnpm -C pdf exec vitest run test/classify.test.ts`. **Expected:** a non-zero exit, with the new growth tests failing. **Pins, which pass at red, as nothing grows yet:** `no fixture changes its page type or tight region`, `a full page's image gains nothing`, `above 5,000 text boxes nothing grows`, `the same boxes give the same answer, in any order`.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*PdfAnalysisStoreTests"`. **Expected:** non-zero: compile errors only for the `Labelled` member this stage creates. **Pin once it compiles:** `An_entry_without_labels_stays_as_today` (in `PdfCommandTests`).
- [ ] **Green.** The rule of spec §4.2, with `LABEL_GAP_PT` starting at 12: `PageMeasure.textBoxes` boxes gain an optional `chars`, kept through `within`; the small path boxes kept for candidates; `Region` gains `labelled?: Box`, rounded like the others and cut at the page. `analyse.ts:76-77` uses `itemBox` from `boxes.ts`. `RULES_VERSION = 2` (`classify.ts:25`), `PdfAnalyses.Analyser` `"pdfjs-dist@6.4.299+rules.2"` (`PdfAnalyses.cs:42`), `analyse.test.ts:33` to match. C#: `public sealed record PdfBox(double X, double Y, double Width, double Height);`, `PdfRegion(string Kind, double X, double Y, double Width, double Height, PdfBox? Labelled = null)`; `RegionJson` gains `BoxJson? Labelled`, checked with `Point` at `:320`; `RegionRecord` an optional field, null when absent from an older record. `InfoLine` prints ` with labels <Crop.Enclosing of the box>`; an entry with none stays as today. The help per spec §4.3.
- [ ] Run `pnpm -C pdf test`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*PdfAnalysisStoreTests" --filter-class "*AttachmentPixelsTests" --filter-class "*RekeyTests"`. **Expected:** exit 0, 0 failed.
- [ ] Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*PdfCommandTests" --filter-class "*HelpTests"`. **Expected:** exit 0, 0 failed.
- [ ] Commit, and note its hash for the report: `git add pdf/src/classify.ts pdf/src/analyse.ts pdf/test/classify.test.ts pdf/test/analyse.test.ts src/Kumbu.Server/Attachments/PdfAnalyses.cs src/Kumbu.Core/Attachments/PdfAnalysis.cs src/Kumbu.Server/Attachments/PdfSidecar.cs src/Kumbu.Core/Sh/Commands/PdfCommand.cs tests/Kumbu.Server.Tests/Attachments/PdfSidecarTests.cs tests/Kumbu.Server.Tests/Attachments/PdfAnalysisStoreTests.cs tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs && git commit -m "Try regions that take in a figure's labels and legend" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] **Mutations**, one at a time. After each, run `pnpm -C pdf exec vitest run test/classify.test.ts`, see at least one named test fail, and restore with `git checkout -- <file>`:
  1. Ignore blockers. Expected to fail: `a long caption stops the growth`.
  2. Don't chain. Expected to fail: `a chain follows a legend's second line`.
  3. Don't check "closer to another region". Expected to fail: `a label closer to another figure stays with that figure`.
  4. Grow a full page's image too. Expected to fail: `a full page's image gains nothing`.
  5. Remove the limit of 5,000. Expected to fail: `above 5,000 text boxes nothing grows`.
  6. Let growth replace the tight box. Expected to fail: `no fixture changes its page type or tight region`.

**Stage C: tune, then the verdict, once.**

- [ ] Tune on seeds 1 to 5 only: `pnpm -C pdf eval:regions "$HOME/fig" --seeds 1-5`, changing constants or the rule's shape, position only (spec §4.2), and committing each round (`git add pdf/src/classify.ts pdf/test/classify.test.ts && git commit -m "Tune the growth of labelled regions" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`). Stop when you can't improve it within the budget.
- [ ] Take the verdict **once** on held-out seeds: `for s in 101 102 103 104 105 106 107 108 109 110; do uv run tools/attachment-probes/make_figure_pdfs.py "$HOME/fig" --seed $s; done`, then `pnpm -C pdf eval:regions "$HOME/fig" --seeds 101-110`. **Ships if, and only if:** at least 60 figures; at least 90 % pass overall, 80 % for each generator, 70 % for each layout; on the controls of seeds 1 to 5 and the fixtures, no page type or tight region differs from `baseline.json`; and no `with labels` region contains a body line, on those or on the held-out controls.
- [ ] If it fails: **one** more round of tuning on seeds 1 to 5, and from their failures only: held-out data stays held out, so don't open the held-out failures to choose what to change. Then the verdict on seeds 201 to 210, by `for s in 201 202 203 204 205 206 207 208 209 210; do uv run tools/attachment-probes/make_figure_pdfs.py "$HOME/fig" --seed $s; done` and `pnpm -C pdf eval:regions "$HOME/fig" --seeds 201-210`. Report both. **Then it stops: no third look.** Don't change a bar, a seed set or the corpus's difficulty after seeing a held-out result.

**Stage D: act on the verdict, and write it down.**

- [ ] **If it ships:** add to `docs/test-playbook-attachments.md` the step "crop the large chart with its labels. Could you read the axes and the legend without another call?" and a row in the results table. Run the worked example of spec §4.4 through MCP if the stack from Task 13 can be started again, and quote it in the note. Add to `docs/attachments-client-research.md:279` that it was built.
- [ ] **If it doesn't, or wasn't measured:** if stage B never ran, skip the revert: there is nothing to name, and only the note below remains. Otherwise revert only stage B and C's commits: `git revert --no-edit <first stage B commit>^..<last stage C commit>`. **Expected:** `RULES_VERSION = 1` in `pdf/src/classify.ts`, `+rules.1` in `PdfAnalyses.cs:42`, and `PdfCommand.cs:57-58` with its present sentence; the script and the eval are still there. Check with `rg -n "RULES_VERSION = " pdf/src/classify.ts` and `rg -n "rules\." src/Kumbu.Server/Attachments/PdfAnalyses.cs`. Add to `docs/attachments-client-research.md:279` that it was left out, and why. Never force-push.
- [ ] In either case, in `docs/notes-attachment-wishes-hands-on.md`: the table per generator and layout, for both held-out rounds if there were two; the constants; the failures by kind; the attempt's commits; and **the verdict in one sentence**, which also goes into the pull request. If it ships, add: "the first real PDF that disagrees outweighs the table" (spec §4.5).
- [ ] **The full verification, each line:**

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

**Expected:** exit 0 for each; `dotnet test` failed 0, skipped 4; web 1346 tests; sidecar passing.

- [ ] Commit: `git add docs/notes-attachment-wishes-hands-on.md docs/attachments-client-research.md docs/test-playbook-attachments.md && git commit -m "Record the verdict on regions with labels" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`
- [ ] Reviews. The controller runs `git push -u origin feature/attachment-wishes`.

If you run out of time or get stuck anywhere in this task, revert what stage B and C committed (nothing, if stage B never ran), write "not measured" or what you did find in `docs/notes-attachment-wishes-hands-on.md`, say in `docs/attachments-client-research.md:279` that it was left out, and commit: `git add docs/notes-attachment-wishes-hands-on.md docs/attachments-client-research.md && git commit -m "Record that regions with labels were left out" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`. Then go to the final task, whose full verification covers this. Tasks 1 to 13 ship regardless.

## Final task: report and draft pull request

**Do not merge.** No version change, no tag.

- [ ] Run `git diff --stat origin/master -- version.txt src/Kumbu.Server/Kumbu.Server.csproj web/package.json`. **Expected:** no output.
- [ ] **The full verification, each line.** This covers phase 4 and a Task 14 that stopped early:

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build
pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build
```

**Expected:** exit 0 for each; `dotnet test` failed 0, skipped 4; web 1346 tests; sidecar passing. Fix what fails before going on.

- [ ] Run `git push -u origin feature/attachment-wishes`. **Expected:** the branch is up to date on origin.
- [ ] Open a **draft** pull request from `feature/attachment-wishes` to `master`: `gh pr create --repo SCAI-NGO/kumbukumbu --draft --base master --head feature/attachment-wishes --title "Attachment wishes: --rotate, --frame, rg --regions" --body-file <file>`. The body says what was built, the verdict of spec §4.5 in its one sentence, what to check by hand, that both images must come from the same tag, and it ends with the line `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. **Do not merge.** If no pull request can be opened, use the compare URL `https://github.com/SCAI-NGO/kumbukumbu/compare/master...feature/attachment-wishes` as the link below.
- [ ] Write `docs/superpowers/overnighter/2026-11-02-attachment-wishes-report.md`: the environment and what you installed; Task 0 planned against measured (time and counts); per task the commit and the real test output; every mutation individually, with whether it failed as expected; the decisions of this plan's list you kept or overruled, and your own calls; deviations; surprises; the verdict of spec §4.5 in one sentence, with both rounds if there were two; the measured memory peak of the composing path (Task 13), or "memory not measured"; what the maintainer must check by hand first (the playbook's new steps with real clients, real PDFs, both images from the same tag); the PR link.
- [ ] Add the row to `docs/superpowers/open-items.md`: `| 2026-11-02 | Attachment wishes for 0.3.5: draft pull request from the overnighter run; review, the verdict on regions with labels, hand checks named in the report | <PR link> |`.
- [ ] Commit and push both: `git add docs/superpowers/open-items.md docs/superpowers/overnighter/2026-11-02-attachment-wishes-report.md && git commit -m "Report on the attachment wishes run, and link its pull request" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`, then `git push -u origin feature/attachment-wishes`.
- [ ] Run `gh pr checks --repo SCAI-NGO/kumbukumbu feature/attachment-wishes` once CI has had time, and note the result in the pull request if it fails: fix what is yours. **Do not merge.**
