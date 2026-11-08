# Ingesting attachments: pdfimages, the archive and a system skill — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user's AI turn PDFs and images into notes without losing anything: `pdfimages` stores a PDF's embedded images and rendered regions as image attachments, a per-vault archive (`/<vault>/_archive/`, `archive`, `--archive`, an Archive view) keeps a source out of the way, and the system skill `ingest-attachments` ties both together.

**Architecture:** The PDF sidecar (`pdf/`) gains three extraction routes served by a pool of its own with one worker: `/images/list` (one page's images with box, pixel size, hash and whether it is small), `/images/image` (one embedded image by page and painting number, decoded by pdf.js, turned as painted, scaled to 8 MP, PNG else JPEG 90–60) and `/images/region` (one rendered region at 300 dpi, capped at 8 MP, PNG or JPEG as asked). `IPdfSidecar` and `IAttachmentPixels` gain the matching methods, checked as pictures are today. `pdfimages` in `Kumbu.Core/Sh/Commands/` plans every name first, refuses on any clash or past 500 images, then writes image by image through `AttachmentWriter`, within a wall-clock budget. The archive is a static helper `Kumbu.Core.Notes.ArchiveFolder` (like `FolderMarker`) used by each command that hides it; `archive` builds the timestamped target and calls `NoteMover`. The web app gets the summary from the tree response, a new `GET /api/vaults/{vault}/archive`, a folder-trash endpoint and an Archive view.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB, xUnit v3 on Microsoft.Testing.Platform; Node 26 sidecar with pdf.js (`pdfjs-dist`) and `@napi-rs/canvas`, vitest, pdf-lib (dev only, test PDFs); React + Vite + Vitest. No new NuGet or npm package; `node:crypto` (built in) hashes pixels.

**Spec:** `docs/superpowers/specs/2026-11-08-ingest-attachments-design.md` (approved; do not edit). **Brief:** `docs/superpowers/overnighter/2026-11-08-ingest-attachments-brief.md`; its "Open points for the run" 1–24 are *OP n* below.

## Plan review

A fresh subagent reviewed spec, brief and the draft against the code (pdf.js 6.4.299 source included); all 27 findings were accepted. **Where a ruling below and a task's text disagree, the ruling wins**; each implementer gets the rulings for its task.

1. Tasks 7 and 12 also update `web/src/console/console-fixture.json` ("refused", checked by `ConsoleFixtureTests.Refused_is_the_servers`), the refused sentence in `help.ts`'s `helpBlock` and its assertions in `web/src/console/ConsolePage.test.tsx` (≈`:275`, `:307`); run `--filter-class "*ConsoleFixtureTests"` and `pnpm -C web test --run src/console`.
2. Decision 3 revised: extraction jobs keep `verbosity: 1` only with `console.log`/`console.warn`/`console.error` replaced *inside the job's worker* by a counter that matches exactly `Image exceeded maximum allowed size and was removed.` and swallows every message; a test asserts nothing reaches the console. No pdf.js warning reaches the log.
3. Both `extractImage` and `renderRegion` also cap the long edge at 16,384 px (`ImageHeader.MaxSide`), saying "scaled down"; a 20000×50 fixture tests it. `PdfSidecar`'s checks use `LongEdge: 16_384`.
4. Decision 4 revised: budget 20 s (`PdfImages.Budget`, init-settable), listing stops at half of it and only listed pages are written; writing stops at a page boundary once the budget is spent, with a hard ceiling of 50 s: if that is hit mid-page, the output says exactly what was stored of page K and tells to run page K again into another folder or remove those files first. "go on with pages K-" names the first page not (fully) written.
5. Task 1 pdf.js specifics: decoded images resolve asynchronously, so read them with the callback form `objs.get(id, cb)` wrapped in a Promise (`commonObjs` for ids starting with the document's `g_` prefix, `page.objs` otherwise); `paintInlineImageXObject` carries its image in its arguments; `paintInlineImageXObjectGroup` is one RGBA atlas plus a `map` of `{x, y, w, h, transform}` to cut out per entry; `paintImageMaskXObjectGroup` entries carry their own `data`; masks have no `kind`, their data is packed 1-bit with a 0 bit painted (Decode applied).
6. Fixtures and tests for 1-bit grey (kind 1), raw 8-bit DeviceRGB, `paintImageXObjectRepeat` (the same XObject 3+ times in a row), 10+ masks (group/repeat) and 10+ small inline images (atlas): colours and numbering right.
7. An image pdf.js fails to decode (`null` data) is listed with `hash: null` and `failed: true`; the C# check allows it; `pdfimages` names it as skipped ("could not be decoded").
8. Boxes are clipped to the page in the sidecar as `classify`'s `clip` does; an image wholly off the page is left out of the list (and not numbered); tested.
9. Memory: list hashes pdf.js's decoded `data` with kind, width, height, quarter turn, flip and mask colour (no conversion); extract converts and turns in one pass into one buffer, scales, and drops references.
10. Task 13's folder trash calls the existing `TreeWriter.RemoveAsync` (`TreeWriter.cs:31-58`), no new method; `PublishedNoteException` maps to 403 and is tested; `.`/`..` segments and `\` validated as `MoveAsync` does.
11. Filters in `rg`, `tags` and `ls` go after the empty-result checks, so a vault whose notes are all archived answers empty, not "No such file or directory"; tested.
12. Test classes: `SemsearchPdfTests` and `Search/SearchTests` (not `*SemsearchTests`), `CheckpointTests` (not `CheckpointCommandsTests`), `HelpTests` is in `Kumbu.Core.Tests`; every command names its `--project`.
13. Task 14: an archived folder's **Open** expands it inside the Archive view, listing its files each with Open; a file's Open uses the existing note/attachment route.
14. `NoteMover` gains `internal TimeProvider Time => time;` in Task 4; `pdfimages` and `archive` take it from there. Budgets measure with `time.GetTimestamp()`/`GetElapsedTime`; tests advance the `FakeTimeProvider` in the fake (or `AutoAdvanceAmount`) so `Task.Delay(…, time)` completes.
15. Task 3 adds `PixelsProblem.Busy` (its `Kind` is `Unavailable`) and maps `busy` to it for the new methods; `too_large` maps to `TooLarge` with its own sentences for the new methods; tested.
16. A quota stop returns `ShellResult.Error(partial lines + sentence) with { Kind = ShellErrorKind.Refused, Code = e.Owner ? Refusals.Quota : null }`, " See `df`." only for the owner; writer refusals keep their own `Kind`.
17. The archive figure of account storage is computed with `StorageQuota`'s count or cached per owner for its `ShownFor` time, owned vaults only; the 100 % sentence (`fullSentence`) names the archive too.
18. Registries go into the task that registers a command: `KnownCommands`, `Refused`, the console fixture, `help.ts`, `Manual.ToolDescription` and `HelpTests` for `pdfimages` in Task 4, for `archive` in Task 9; Tasks 7 and 12 keep the help texts, topic and the server tests.
19. A JPEG fallback flattens onto white first (Skia writes transparent as black); PNG keeps alpha; tested.
20. Task 13 adds a test in `CheckpointReadApiTests` that a checkpoint's tree shows `_archive/`, so mutation (a) fails.
21. `analyse.ts`'s analysis must not change: a separate walker (or a callback) in `extract.ts`; `analyse.test.ts` and `classify.test.ts` pass unchanged.
22. Task 14's restore checks for a note, file or folder at the target, and a 409 from `/api/move` reopens the "Restore as…" dialog; tested.
23. Archive items: the outermost stamped ancestor wins (nothing inside a stamped folder is an item of its own); `.vaultkeep` is never an item; tested.
24. `originalWidth`/`originalHeight` and "(scaled down from W×H)" are in the turned orientation.
25. Task 16's red test asserts `SystemSkills.Find("ingest-attachments")` is not null and checks the content (root links with `%20`, crop only drawing regions, the 5 % note); also run `SkillsCommandTests`, `SkillDirectoryTests`, `SystemSkillsTests`, `HandshakeInstructionsTests`.
26. Wording: the time-zone check is Task 17; web test file `web/src/shell/TreeDrawerArchive.test.tsx`; `TagsCommandTests` usage assertion is `:109`.
27. `pdfimages`'s help says duplicates are found within one call; a follow-up call stores a repeated image again.

## Global constraints

- No new NuGet or npm package. Test PDFs are generated in `pdf/test/fixtures.ts` with pdf-lib, never committed.
- British English in code, comments, docs, commits. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`. Match the surrounding idiom and comment density.
- No organisation, customer or employer names; examples use Alice, Bob, Carol, `Work`, `Private`, a pump manual, wild flowers.
- Images are decoded and encoded anew, never the PDF's raw bytes; every stored file goes through `AttachmentWriter.WriteAsync`; moves go through `NoteMover`.
- Archive hiding is never access control: no permission check reads `ArchiveFolder`.
- No note or attachment content, no file name, in a log.
- Time through the injected `TimeProvider`; Core tests use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` (already referenced by the test projects) with `SetLocalTimeZone`.
- `web/src/api/schema.d.ts` is edited by hand.
- Commit after each task, imperative free-form message ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Subagents never push, merge or switch branches; the coordinator pushes `feature/ingest-attachments`.
- Server tests need `KUMBU_TEST_MONGO=mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true` with the container `kumbu-test-mongo` (`docker rm -fv kumbu-test-mongo`, then `docker run -d --name kumbu-test-mongo --ulimit nofile=20000:20000 -p 27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all` and `rs.initiate` with host `127.0.0.1:27017`). Node 26 is at `/opt/node26/bin` (prefix `PATH`).
- Test filter: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ClassName"` (or `tests/Kumbu.Server.Tests`); sidecar one file: `pnpm -C pdf exec vitest run test/<file>.test.ts`; web one file: `pnpm -C web test --run <file>`.
- Mutations: after the commit, apply each listed mutation alone, run the named test, see it fail, restore with `git checkout -- <file>`, and report each.

## Decisions (settling the open points)

1. **OP 1** Anchors: the code wins over any line number given here; every task re-reads its anchors before editing.
2. **OP 2, 3** Embedded images are read from `page.objs` / `commonObjs` after `getOperatorList({ annotationMode: AnnotationMode.DISABLE })`. Kinds: `1` GRAYSCALE_1BPP (packed, 1 = white unless the decode says otherwise as pdf.js has already applied it), `2` RGB_24BPP, `3` RGBA_32BPP, converted to RGBA `ImageData`. Where pdf.js hands a `bitmap` instead (not in Node), the image is drawn from it. `paintImageMaskXObject` (and its group/repeat forms) is stored as the mask in the current fill colour on transparent; `paintSolidColorImageMask` is not an image and is not counted. The image is turned and flipped as the CTM maps its unit square onto the page (quarter turns only: the displayed matrix's dominant axes; a skewed or arbitrarily rotated image is stored axis-aligned in its nearest quarter turn), and its pixel size is reported after turning. PNG keeps alpha.
3. **OP 4** Extraction opens the PDF with `verbosity: 1` (warnings) and, inside the job's own fresh worker thread, counts pdf.js warnings whose text contains `maxImageSize` or "exceeded maximum allowed" for the page being listed; `/images/list` answers that count as `over`. If the count turns out unreliable in the test with `hugeImagePage`, the help says such images are left out and `pdf --crop` shows them, and `over` is dropped.
4. **OP 5** One page per `/images/list` request. One image per `/images/image` request; that re-decodes the page's images once per stored image, which is acceptable because small images are skipped by default and duplicates are fetched once (logged as a decision; batching several images in one answer would need a framed body). Extraction routes have their own limit `EXTRACT_MS = 30_000` per request. `pdfimages` has a wall-clock budget of 45 s (`PdfImages.Budget`, init-settable for tests like `Rg.RegionBudget`): listing stops at the budget, writing stops only at a page boundary once the budget is spent, and the output ends "stored N images; go on with pages K-". `busy` from the sidecar is retried up to 3 times, 1 s apart (through `Task.Delay` with the injected `TimeProvider`).
5. **OP 6** `createSidecar(pool, limits, extract?)`: a second `Pool({ workers: 1, queue: 8 })` runs the three extraction routes; `pdf` and `img` routes keep the existing pool. `maxConnections` counts both capacities.
6. **OP 7** Crop targets: `.png` asks the region route for PNG; if the PNG would pass 15 MB the route answers `too_large` and `pdfimages` says "pdfimages: the region is over 15 MB as PNG; name the target .jpg". `.jpg`/`.jpeg` asks for JPEG at 90 down to 60 (else `too_large`). Any other target extension, a folder target (ending `/`) or a page range with `--crop`: the usage line. `--rotate` without `--crop`: refused.
7. **OP 8** Paths start with `/`, pages `N`, `N-M` or `N-` (`PageRange.TryParse`); the folder target must end in `/` else the usage line; `--list` and `--all` anywhere (`Options.TryParse`).
8. **OP 9** `n` counts every image the page paints, in painting order (decorations and duplicates included, solid-colour masks excluded), so names are stable with or without `--all`; a duplicate is stored once, named after its first page and number, its line listing every page.
9. **OP 10** The 500 cap counts images to store (after decoration and duplicates) and is checked after listing, before any write; the message names the range that fits ("pdfimages: 812 images in pages 1-200, more than 500 in one call; give a page range, e.g. 1-117").
10. **OP 11** `pdfimages` catches `Kumbu.Core.Notes.QuotaExceededException` around each write, stops, prints the lines stored so far, the files not stored, and the exception's sentence. An `AttachmentWriter` error is quoted after `pdfimages:` (its own text starts `upload:`).
11. Size cap: 8 MP = 8,000,000 pixels; the scale is `min(1, sqrt(8e6 / (w*h)))`, sides floored, at least 1. The output line adds "(scaled down from W×H)".
12. **OP 12–15, 17, 20–22**: settled in Phases 3 and 4 below.
13. **OP 18** The timestamp goes before the format's extension: `NAME YYYY-MM-DD HHmmss.EXT` for a file (extension as in the name, `.jpeg` stays `.jpeg`), `NAME YYYY-MM-DD HHmmss` for a note `.md` (`v1.2 notes 2026-11-08 213005.md`) and for a folder. On a clash within the second: ` -2`, ` -3` … is appended after the time (`report 2026-11-08 213005-2.pdf`).
14. **OP 19** `compose.yaml` gains `TZ: ${TZ:-UTC}` for `kumbu`, `.env.example` and the README a line; whether the `aspnet` image honours `TZ` is checked in Task 17 and recorded.
15. **OP 23, 24**: Phase 5.

---

## Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1:** Start a fresh `kumbu-test-mongo` (Global constraints) and run, recording counts and failures:
  `dotnet restore --locked-mode && dotnet build --configuration Release --no-restore && dotnet test --configuration Release --no-build`;
  `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`;
  `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`.
- [ ] **Step 2:** Compare with the known state at `3af5397`: .NET total 5037, succeeded 5033, failed 0, skipped 4 (6 skipped as root); web 112 files, 1834 tests; pdf 8 files, 146 tests. A failing test class is run alone three times and recorded as flaky or known.

---

## Phase 1: sidecar routes and `IAttachmentPixels`

### Task 1: Extract the images of a page in the sidecar (`pdf/src/extract.ts`)

**Files:**
- Create: `pdf/src/extract.ts`
- Modify: `pdf/src/encode.ts` (add `encodeStored`), `pdf/src/pdfjs.ts:26-50` (`openPdf(bytes, { warnings? })`), `pdf/src/analyse.ts:112-186` (export the CTM walk so both use it)
- Modify: `pdf/test/fixtures.ts` (new fixtures)
- Test: `pdf/test/extract.test.ts`

Behaviour:
- `export const MAX_STORED_PIXELS = 8_000_000`, `export const STORED_DPI = 300`.
- `listImages(pdf, page): Promise<PageImages>` where `PageImages = { page, pageCount, width, height, over, images: ImageInfo[] }` and `ImageInfo = { n, width, height, x, y, w, h, small, hash }`: `n` from 1 in painting order over every image paint operation of the walk (XObject, inline, image masks, the repeat and group forms each count one per placement), excluding `paintSolidColorImageMask`; `width`/`height` the decoded pixel size after the quarter turn; `x,y,w,h` its box in displayed points rounded to 0.1 (as `analyse.ts` boxes); `small` when `w*h < MIN_FIGURE * pageWidth*pageHeight` (`classify.ts:36`); `hash` = sha256 hex (`node:crypto`) of width, height and the RGBA pixels after turning and (for masks) colouring. `no_such_page` for a page outside the PDF, as `render.ts:20`.
- `extractImage(pdf, page, n): Promise<Rendered & { originalWidth, originalHeight }>`: the image `n` of that page as above, scaled to fit 8 MP (aspect kept, `steps.ts` `pixels` floor), encoded by `encodeStored(canvas, MAX_BODY_STORED)` with `MAX_BODY_STORED = 15 * 1024 * 1024` (= `AttachmentFormat.MaxBytes`): PNG if it fits, else JPEG at 90, 85, 80, 75, 70, 65, 60, else `PdfError("The image is over 15 MB even as JPEG at quality 60.", "too_large")`. `no_such_image` (`PdfError`, new code, 422) for an `n` past the page's images.
- `renderRegion(pdf, page, crop, rotate, format: "png" | "jpeg")`: like `renderPage` (`render.ts`) but scale `min(300/72, sqrt(8e6/(w*h)))`; PNG only for `png` (over 15 MB: `too_large`), JPEG 90→60 for `jpeg` (none fits: `too_large`). Reports `pageWidth`, `pageHeight`, `rotate`.
- Image masks: drawn in the fill colour current at their paint (track `setFillRGBColor`, as `measureDrawing` tracks `fill`; a pattern or unknown fill draws black) onto transparent.
- Warnings (Decision 3): `openPdf(bytes, { warnings: true })` sets `verbosity: 1`; the count of pdf.js's oversize warnings while building the page's operator list is `over`.

Fixtures to add in `pdf/test/fixtures.ts` (pdf-lib):
- `twoImagesPage()`: one page, a 400×300 photo-like PNG (noise from `noiseCanvas`) at 72,72 drawn 300×225 pt, then a 200×200 JPEG at 72,400 drawn 200×200 pt.
- `logoOnEveryPage(pages = 3)`: the same 120×60 PNG drawn at 40,20 (60×30 pt — above 1 % of A4? 1800 pt² vs 5010: below, so small) and a large 600×400 PNG on page 2 only.
- `cornerImagePage(rotation: 0|90|180|270, flip?: boolean)`: a 4-colour corner image (as `cornersPage`'s colours) drawn with a CTM turning it, so the test can read the corners after extraction.
- `maskPage()`: an image mask (stencil) painted in red.
- `transparentImagePage()`: a PNG with an alpha channel.
- `bigImagePage()`: a 4000×3000 noise image (12 MP) drawn full page.
- `vectorDrawingPage()`: a chart drawn with paths only (reuse `CHART_BARS`).
- reuse `hugeImagePage()` (over 50 MP) for `over`.

Steps:
- [ ] **Step 1: Red.** Write `pdf/test/extract.test.ts` with tests: lists two images in painting order with boxes and sizes; `small` true for the logo, false for the photo; the same logo on three pages has the same hash, the photo another; corner colours after extraction match the page for each of 0/90/180/270 and a flip, and width/height swap for 90/270; a mask comes out red on transparent (alpha 0 at a background pixel); a transparent PNG keeps alpha; the 12 MP image is stored at ≤ 8,000,000 px with aspect kept and `originalWidth` 4000; `n` past the end is `no_such_image`; page past the end is `no_such_page`; `hugeImagePage` lists 0 images and `over` 1; `renderRegion` of `vectorDrawingPage` at 300 dpi gives `round(w*300/72)` px wide, PNG; a region whose 300 dpi size passes 8 MP is capped; `format: "jpeg"` gives JPEG bytes (`FF D8 FF`). Run `pnpm -C pdf exec vitest run test/extract.test.ts`: FAIL (module missing).
- [ ] **Step 2: Green.** Implement; run the same: PASS. Run `pnpm -C pdf test`: PASS (type check and suite).
- [ ] **Step 3: Commit** "Extract a PDF's images and render regions for storing in the sidecar".
- [ ] **Step 4: Mutations** (each alone, `pnpm -C pdf exec vitest run test/extract.test.ts`, must fail): (a) skip the quarter turn; (b) drop the 8 MP scale; (c) count `paintSolidColorImageMask` as an image; (d) hash without width/height and pixels (constant); (e) flatten alpha onto white; (f) `small` threshold 0.

### Task 2: Extraction routes and their pool

**Files:**
- Modify: `pdf/src/server.ts` (routes, `STATUS` gains `no_such_image: 422`, `EXTRACT_MS = 30_000`, `createSidecar(pool, limits, extract = pool)`, main starts `new Pool({ workers: 1, queue: 8 })` for extraction, `maxConnections = pool.capacity + extract.capacity + 16` when distinct), `pdf/src/pool.ts` (`Job` gains `{ kind: "list"; bytes; page }`, `{ kind: "extract"; bytes; page; n }`, `{ kind: "region"; bytes; page; crop; rotate; format }`), `pdf/src/worker.ts` (runs them; data transferred as for `page`)
- Test: `pdf/test/server.test.ts`

Routes (POST, PDF body):
- `/images/list?page=N` → JSON `PageImages`.
- `/images/image?page=N&n=K` → the image; headers `Content-Type`, `X-Kumbu-Width`, `X-Kumbu-Height`, `X-Kumbu-Original-Width`, `X-Kumbu-Original-Height`.
- `/images/region?page=N&crop=x,y,w,h&format=png|jpeg[&rotate=D]` → the image; headers as `/render/page` (`X-Kumbu-Page-Width/Height`, `X-Kumbu-Rotate` when turned). `crop` is required; `format` must be `png` or `jpeg` (`bad_request`).

Steps:
- [ ] **Step 1: Red.** In `pdf/test/server.test.ts` add: each route answers for `twoImagesPage`; `/images/image` headers match the PNG header's size; `no_such_image` is 422; missing `crop` or a bad `format` on `/images/region` is 400 `bad_request`; extraction runs on the extract pool: with an extract pool of `workers: 1, queue: 0` already holding a job, a second extraction answers 503 `busy` while `/render/page` on the main pool still answers 200. Run `pnpm -C pdf exec vitest run test/server.test.ts`: FAIL.
- [ ] **Step 2: Green.** Implement; same command PASS; `pnpm -C pdf test` PASS; `pnpm -C pdf build` PASS.
- [ ] **Step 3: Commit** "Serve a PDF's images and regions for storing from a pool of one worker".
- [ ] **Step 4: Mutations:** (a) route extraction through the main pool (busy test fails); (b) drop `no_such_image` from `STATUS` (expect 500, test fails); (c) accept a missing `crop`.

### Task 3: `IPdfSidecar` and `IAttachmentPixels` methods

**Files:**
- Modify: `src/Kumbu.Core/Attachments/IAttachmentPixels.cs:85-103` — add records and methods:
  - `public sealed record PdfImageInfo(int N, int Width, int Height, double X, double Y, double W, double H, bool Small, string Hash);`
  - `public sealed record PdfPageImages(int Page, int PageCount, double Width, double Height, int Over, IReadOnlyList<PdfImageInfo> Images);`
  - `Task<PdfPageImages> PdfImagesAsync(VaultPath path, AttachmentState state, int page, CancellationToken ct);`
  - `Task<Picture> PdfImageAsync(VaultPath path, AttachmentState state, int page, int n, CancellationToken ct);` (`OriginalWidth/Height` = pixels before scaling)
  - `Task<Picture> PdfRegionAsync(VaultPath path, AttachmentState state, int page, Crop crop, int rotate, bool jpeg, CancellationToken ct);` (`OriginalWidth/Height` = page size in points)
  - `PixelsProblem` gains `NoSuchImage`.
- Modify: `src/Kumbu.Server/Attachments/PdfSidecar.cs` — `IPdfSidecar` gains `ListImagesAsync`, `ExtractImageAsync`, `RenderRegionAsync`; `Codes` gains `["no_such_image"] = UnprocessableEntity`; responses checked: list JSON bounded (1 MiB), page equals the asked one, `PageCount ≥ page`, each `N` 1..count ascending, sizes > 0, boxes finite ≥ 0, hash `^[0-9a-f]{64}$`, at most 10,000 images; images through `PictureAsync`-like checks with `StepLimits(LongEdge: 16_384, Pixels: 8_000_000, Bytes: 15 MB)` (`ImageHeader.Read` must agree with the headers, pixels ≤ 8 MP + w + h); a region asked as PNG must come back `image/png`, as JPEG `image/jpeg`. Timeout: `RenderTimeout`-like `ExtractTimeout = 30 s + 5 s`.
- Modify: `src/Kumbu.Server/Attachments/AttachmentPixels.cs` — the three methods, through `BytesAsync` and `MeasuredAsync` (kind `Page` for region, a new `RenderKind.Extract` if the metrics enum allows it cleanly, else `Page`), errors through `Refused` with `no_such_image` → `PixelsProblem.NoSuchImage`, `too_large` → `TooLarge`, `busy`/unavailable → `Unavailable`.
- Modify: `tests/Kumbu.Server.Tests/Attachments/FakePdfSidecar.cs` (the new methods: canned images per page, a `PageImages` func, counts), `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs` `FakePixels` and every other `IAttachmentPixels` fake in the tests (`grep -rn ": IAttachmentPixels" tests`): `throw new NotSupportedException()`.
- Test: `tests/Kumbu.Server.Tests/Attachments/PdfSidecarTests.cs` (HTTP handler stub as the file's other tests do), `AttachmentPixelsTests.cs`.

Steps:
- [ ] **Step 1: Red.** Tests: a valid list parses; a list for another page, a non-hex hash, a negative box, `N` not ascending each give `SidecarException` `failed`; an image whose header size differs from `X-Kumbu-Width` is refused; an image over 8 MP is refused; a region asked as PNG answered as JPEG is refused; 422 `no_such_image` maps to `PixelsProblem.NoSuchImage` in `AttachmentPixels`. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*PdfSidecarTests" --filter-class "*AttachmentPixelsTests"`: FAIL (build errors count as red).
- [ ] **Step 2: Green.** Implement; same command PASS; `dotnet build Kumbu.slnx` without warnings.
- [ ] **Step 3: Commit** "Ask the sidecar for a PDF's images and regions to store".
- [ ] **Step 4: Mutations:** (a) skip the hash format check; (b) skip the 8 MP check; (c) map `no_such_image` to `Failed`.

### Phase 1 gate

- [ ] Full verification (Global constraints, as CI): `dotnet restore --locked-mode; dotnet build --configuration Release --no-restore; dotnet test --configuration Release --no-build`; web install/test/build; pdf install/test/build. Counts recorded against Task 0. Push.

---

## Phase 2: `pdfimages` in `sh`

### Task 4: `pdfimages --list` and argument parsing

**Files:**
- Create: `src/Kumbu.Core/Sh/Commands/PdfImages.cs` — `public sealed class PdfImages(INoteReader notes, IAttachmentPixels pixels, AttachmentWriter files, TimeProvider time) : ICommand` (writer from `writer.ForAttachments()`; when `files` is null in `Shell`, the command is not registered, as `Mkdir`).
- Modify: `src/Kumbu.Core/Sh/Shell.cs:50` — register after `PdfCommand` (only when `pixels` and `files` are not null).
- Test: `tests/Kumbu.Core.Tests/Sh/PdfImagesTests.cs` with its own `FakePixels` (pages → `PdfPageImages`, `(page,n)` → `Picture`, region → `Picture`, a queue of exceptions to throw, a call log).

Parsing (Decisions 6, 7): `Spec = [new(null, "list"), new(null, "all"), new(null, "crop", TakesValue: true), new(null, "rotate", TakesValue: true)]`. Usage `pdfimages <pdf> [pages] <folder>/ | <pdf> <page> --crop x,y,w,h <file.png> | --list <pdf> [pages]`. The source must exist as a PDF attachment (`StoreAttachments.GetAsync`, kind PDF) else `pdfimages: <path>: No such file` / `is not a PDF`.

`--list` output: a header `<path>: N images on pages A-B (K small, --all lists them)` then one line per image `p4  #1  2400×1600  at 80,300,435,290  [small] [same as p1 #1]`; small ones only with `--all`. Pages listed one request each (Decision 4).

Steps:
- [ ] **Step 1: Red.** Tests: usage for no operands, for a folder without `/`, for `--crop` with a range, for `--crop` with a folder target, for `--rotate` without `--crop`; `--list` lists images with boxes and marks the duplicate; `--list` leaves small ones out and counts them, `--all` includes them; `--list` anywhere in the line; a missing PDF and a note path are refused; a page past the end says the page count. Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*PdfImagesTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; PASS.
- [ ] **Step 3: Commit** "List a PDF's images with pdfimages --list".
- [ ] **Step 4: Mutations:** (a) accept a folder without `/`; (b) drop the small filter; (c) let `--rotate` pass without `--crop`.

### Task 5: `pdfimages` stores embedded images

**Files:** `src/Kumbu.Core/Sh/Commands/PdfImages.cs`; test `tests/Kumbu.Core.Tests/Sh/PdfImagesTests.cs` (with a real `AttachmentWriter` over the in-memory store the Core shell tests use; find the helper the `MkdirTests`/attachment write tests use).

Behaviour (spec §3.1, Decisions 4, 8–11):
- List the pages (budget), drop small ones unless `--all`, keep the first of each hash, building `name = "<pdf name without extension>-p<page>-<n>"` and target `<folder>/<name>.png`.
- Over 500 to store: refuse with the range that fits; nothing written.
- Before writing: for every planned name, `<name>.png`, `<name>.jpg` existing (note or attachment) → nothing written, output `pdfimages: these exist already: …; choose another folder, or remove them first`.
- Folder created when missing (by the first write: attachment writes create folders implicitly; confirm, else `files.MakeFolderAsync`).
- Each image: `pixels.PdfImageAsync`; `too_large` → named in the output as skipped ("p7 #1 skipped: over 15 MB even as JPEG"); JPEG → extension `.jpg`; write `files.WriteAsync(path, data, (w,h), null, ct)`; an error result → stop, print stored lines and `pdfimages: <writer's message>`; `QuotaExceededException` → stop, print stored lines, the files not stored, and the sentence (with " See `df`." for the owner, as `ToolResult` does).
- Output exactly as spec §3.1: `stored 3 images in /Work/Manual/images/ (2 left out as smaller than 1 % of their page, --all keeps them)` then lines `<path>  p1[, p9]  1200×400  88 KB  at 72,40,451,150[ (scaled down from 4000×3000)][ (JPEG: PNG over 15 MB)]`, sizes by `AttachmentText.Size`; `page N: 1 image over 50 MP left out (look with pdf --crop)` per page with `Over > 0`; budget → last line `stored N images; go on with pages K-`.
- `busy` (`PixelsProblem.Unavailable` whose message is the sidecar's busy sentence — add `PixelsProblem.Busy` in Task 3 if needed) retried 3× 1 s through `time` (`Task.Delay(TimeSpan, TimeProvider, ct)`).

Steps:
- [ ] **Step 1: Red.** Tests: stores two images with the exact output lines; a duplicate on pages 1–3 stored once with `p1, p2, p3`; small ones left out and counted, `--all` keeps them under the same numbers; JPEG answer named `.jpg` with "(JPEG: PNG over 15 MB)"; scaled-down note; an existing target refuses and writes nothing (store unchanged); 501 to store refuses with a range and writes nothing; quota midway: first write succeeds, second throws → output has the first line, names the rest not stored, ends with the sentence; a writer refusal (read-only caller) quoted after `pdfimages:`; `over` line; budget: a `FakeTimeProvider` advanced by the fake per call stops at a page boundary with "go on with pages 3-"; busy then success retries. Run the filter: FAIL.
- [ ] **Step 2: Green.** PASS.
- [ ] **Step 3: Commit** "Store a PDF's images as attachments with pdfimages".
- [ ] **Step 4: Mutations:** (a) skip the existing-name check; (b) check the 500 cap after writing; (c) no dedup; (d) rethrow the quota exception; (e) stop mid-page on the budget.

### Task 6: `pdfimages --crop`

**Files:** `src/Kumbu.Core/Sh/Commands/PdfImages.cs`; test `PdfImagesTests.cs`.

Behaviour: one page, `Crop.TryParse`, `Rotation.TryParse` as `pdf`; target extension `.png` → PNG, `.jpg`/`.jpeg` → JPEG; anything else → usage; existing target → refused; `TooLarge` for PNG → "pdfimages: the region is over 15 MB as PNG; name the target .jpg"; `BadCrop` → the sidecar's sentence after `pdfimages:`. Output `stored /Work/Pump manual/images/wiring diagram.png  p12  1958×1583  412 KB  at 60,120,470,380`.

Steps:
- [ ] **Step 1: Red.** Tests for each of the above. FAIL.
- [ ] **Step 2: Green.** PASS.
- [ ] **Step 3: Commit** "Store a rendered region of a PDF page with pdfimages --crop".
- [ ] **Step 4: Mutations:** (a) accept `.gif` target; (b) ask PNG for a `.jpg` target.

### Task 7: Registries, help and a server test through MCP

**Files:**
- `src/Kumbu.Server/Metrics/KumbuMetrics.cs:519-523` (`"pdfimages"` after `"pdf"`), `src/Kumbu.Server/Api/ConsoleApi.cs:43` `Refused` gains `"pdfimages"`, `web/src/console/help.ts` (the refused/help list as it is structured), `src/Kumbu.Core/Sh/Manual.cs` (`ToolDescription` mentions `pdfimages`; the `attachments` topic gains a paragraph), `PdfCommand.Help` gains "To store pictures as images: pdfimages (see 'help pdfimages').", the `HelpTests` list.
- Help text of `pdfimages` (short, literal, `Manual.cs` voice): the three forms, 8 MP, PNG/JPEG rule, small ones and `--all`, duplicates once, never overwrites, 500 per call, regions in points as `pdf info` gives them at 300 dpi, how to go on after the budget, "a writing command: needs write access to the folder".
- Test: `tests/Kumbu.Server.Tests/Mcp/` a test that runs `pdfimages` through the MCP `sh` tool with `FakePdfSidecar` returning a real small PNG and stores it (attachment exists, `ls` shows it), and that the web console refuses `pdfimages --list` (`ConsoleApiTests`).

Steps:
- [ ] **Step 1: Red.** `Every_shell_command_is_classified` fails once `pdfimages` is registered; add the MCP test and console refusal test. FAIL.
- [ ] **Step 2: Green.** PASS: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ConsoleApiTests" --filter-class "*HelpTests"` plus the new class; `pnpm -C web test --run src/console`.
- [ ] **Step 3: Commit** "Name pdfimages in the help, the metrics and the console's refusals".
- [ ] **Step 4: Mutations:** (a) take `pdfimages` out of `Refused`; (b) drop it from `KnownCommands`.

### Phase 2 gate

- [ ] Full verification as in the Phase 1 gate. Push.

---

## Phase 3: the archive in `sh`

Shared helper (spec §8, precedent `FolderMarker`): `src/Kumbu.Core/Notes/ArchiveFolder.cs`
```csharp
public static class ArchiveFolder
{
    public const string Name = "_archive";
    /// <summary>The path is the archive of its vault or lies below it: exactly /<vault>/_archive/…</summary>
    public static bool Contains(VaultPath path) => path.Segments.Count >= 2 && path.Segments[1] == Name;
    public static VaultPath Of(string vault) => VaultPath.Root.Child(vault).Child(Name);
    /// <summary>Whether a listing of <paramref name="scope"/> shows archived paths: asked with --archive, or the scope itself inside the archive.</summary>
    public static bool Shown(VaultPath scope, bool asked) => asked || Contains(scope);
}
```
The name compares ordinally (paths are case-sensitive in Kumbu²); `/Work/Inbox/_archive/` is an ordinary folder. No permission check uses it.

### Task 8: `ArchiveFolder`, skill discovery and link preference

**Files:**
- Create: `src/Kumbu.Core/Notes/ArchiveFolder.cs` (above).
- Modify: `src/Kumbu.Core/Skills/SkillDirectory.cs:94-118` — in the vault Skills (location = vault root) a top-level folder `_archive` is not a skill.
- Modify: `src/Kumbu.Core/Links/LinkResolver.cs:112-135` (`Find`) — after the folder-part preferences and before "1 candidate", when candidates hold some outside the archive and some inside, keep only those outside (`!ArchiveFolder.Contains(c)`); apply the same to attachment candidates (`:94-99` path). Exact links (`/…`, `./`, `../`) are unchanged.
- Tests: `tests/Kumbu.Core.Tests/Notes/ArchiveFolderTests.cs`, `tests/Kumbu.Core.Tests/Links/LinkResolverTests.cs` (or the existing resolver test file: `grep -rln "class LinkResolverTests" tests`), the existing `SkillDirectory` test file.

Steps:
- [ ] **Step 1: Red.** `Contains` true for `/Work/_archive`, `/Work/_archive/Inbox/a.md`; false for `/Work`, `/Work/Inbox/_archive/a.md`, `/Work/_archived/a.md`, root. `[[Overview]]` with `/Work/Overview.md` and `/Work/_archive/Overview 2026-11-08 213005.md`… (same file name needed:) with `/Work/Projects/Overview.md` and `/Work/_archive/Projects/Overview.md` resolves to the living one, not ambiguous; with only archived candidates (two) it stays ambiguous, with one it resolves into the archive; `![[shot.png]]` likewise. A `_archive/` folder in the vault Skills holding a `SKILL.md` is not listed as a skill. Run `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*ArchiveFolderTests" --filter-class "*LinkResolverTests" --filter-class "*SkillDirectoryTests"`: FAIL.
- [ ] **Step 2: Green.** PASS.
- [ ] **Step 3: Commit** "Prefer links outside a vault's archive and skip it in the vault Skills".
- [ ] **Step 4: Mutations:** (a) `Contains` on any segment; (b) remove the preference; (c) remove the skill skip.

### Task 9: `archive <path>`

**Files:**
- Create: `src/Kumbu.Core/Sh/Commands/ArchiveCommand.cs` — `ArchiveCommand(INoteReader notes, NoteMover mover, TimeProvider time)`; `Shell` needs the `TimeProvider`: add `NoteMover.Time` (`internal TimeProvider Time => time;`) or pass it through `Shell`'s constructor as an optional `TimeProvider? time = null` defaulting to `TimeProvider.System` (choose the former: no call site changes).
- Modify: `src/Kumbu.Core/Sh/Shell.cs` — register after `Mv`.
- Test: `tests/Kumbu.Core.Tests/Sh/ArchiveCommandTests.cs` with `FakeTimeProvider` at `2026-11-08 20:30:05Z` and `SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"))` → stamp `2026-11-08 213005`.

Behaviour (spec §3.2, Decision 13): `archive <path>` (one operand; a folder with trailing `/`). Target: `ArchiveFolder.Of(vault)` + the source's folder segments below the vault + stamped name. `Stamp(name, kind)`: for an attachment the extension of `AttachmentFormat.ForName(name)` (the matched suffix, case kept as in the name) stays at the end; for a note `.md` stays; for a folder none. Clash: `-2`, `-3` … after the time until free (note, attachment or folder at that path). Then `mover.MoveAsync(source, target, rewriteLinks: true, ct)` or `MoveFolderAsync`; output is the mover's (`moved … to …`, notes changed). Refusals (each with "to remove it, use rm"): a path in the archive or the archive itself ("already in the archive"), a vault root, anything in the vault Skills (`notes.SkillsVaultAsync`), anything in a skill folder (`SkillPaths.IsInSkillFolder` or a `_skills` segment at position 1). No such path: `archive: <path>: No such file or directory`. A folder named without `/`: as `mv` ("Is a directory (to archive the folder, write …/)").

Steps:
- [ ] **Step 1: Red.** Tests: a PDF → `/Work/_archive/Inbox/report 2026-11-08 213005.pdf` and a note linking `[report](/Inbox/report.pdf)` now links into the archive; a note `v1.2 notes.md` → `v1.2 notes 2026-11-08 213005.md`; `.jpeg` stays `.jpeg`; a folder → `Manual 2026-11-08 213005/` with contents; a second archive in the same second gets `-2`; refusals for `/Work/_archive/x.md`, `/Work/_archive/`, `/Work/`, `/Skills/foo/SKILL.md`, `/Work/_skills/foo/SKILL.md`; a reader is refused by the mover's permission check; history follows (`history` on the new path shows the old writes). Run `--filter-class "*ArchiveCommandTests"`: FAIL.
- [ ] **Step 2: Green.** PASS.
- [ ] **Step 3: Commit** "Move a note, an attachment or a folder into the vault's archive with archive".
- [ ] **Step 4: Mutations:** (a) UTC instead of local time; (b) no clash suffix; (c) drop the skill refusal.

### Task 10: `--archive` on `ls`, `tags`, `backlinks`

**Files:** `src/Kumbu.Core/Sh/Commands/Ls.cs:35` (replace `CheckOptions` by `Options.TryParse` with `Spec = [new(null, "archive")]`; at a vault's root without `--archive` leave the `_archive/` entry out; usage `ls [--archive] [path]`), `TagsCommand.cs:38` (`tags [--archive] [tag] [path]`; `ReadScopeAsync` gains `bool archive` and filters notes in archives unless shown — rg `--files --tag` passes its own flag), `Backlinks.cs:33` (`backlinks [--archive] <path>`; sources in archives left out unless `--archive` or the target itself is archived, OP 17). Help texts gain one `--archive` line each and `ls`'s help says: "_archive/ is the vault's archive (see 'help archive'); hiding it is about attention, not access: whoever may read the vault may read it."
- Tests update on purpose (OP 16): `tests/Kumbu.Core.Tests/Sh/TagsCommandTests.cs:108` (`tags [--archive] [tag] [path]`), `HelpTests`/`ShellTests` where they quote `ls [path]` (`ShellTests.cs` `ManualMentionsCommands`) — `Manual.ToolDescription` lines change with them. `du` keeps its usage: it is not hidden (spec), so `DfTests.cs:149-153` stay; a new test asserts `du /Work` lists `_archive/` as its own line.
- Checkpoints (OP 13): the readers run on a checkpoint view through the same classes, so `ls --checkpoint cp /Work` hides the archive and takes `--archive`; one test in `CheckpointCommandsTests` (or the existing checkpoint reader tests) asserts it.
- Test: `tests/Kumbu.Core.Tests/Sh/ArchiveHiddenTests.cs` (like `FolderMarkerHiddenTests.cs`).

Steps:
- [ ] **Step 1: Red.** `ls /Work` omits `_archive/`, `ls --archive /Work` and `ls /Work/_archive/` show it/its contents; `ls /Work/Inbox/_archive/`-style ordinary folders always show; `tags` counts no archived note unless `--archive` or the scope is in the archive; `backlinks /Work/Overview.md` leaves an archived linking note out, `--archive` includes it, a target inside the archive lists archived sources; checkpoint reader as above; `du /Work` shows `_archive/`. Run `--filter-class "*ArchiveHiddenTests" --filter-class "*TagsCommandTests" --filter-class "*HelpTests" --filter-class "*ShellTests"`: FAIL.
- [ ] **Step 2: Green.** PASS.
- [ ] **Step 3: Commit** "Leave the archive out of ls, tags and backlinks unless asked".
- [ ] **Step 4: Mutations:** (a) ignore `--archive` in `ls`; (b) treat a scope inside the archive as not asked in `tags`; (c) let `backlinks` ignore the target-inside rule.

### Task 11: `--archive` on `rg` and `semsearch`

**Files:**
- `src/Kumbu.Core/Sh/Commands/Rg.cs:36-44` (Spec gains `new(null, "archive")`), `:192-193` (notes and PDFs below the path filtered unless shown; the filtered PDFs feed the scan hint `:248-256`, the name hint `:275-289` takes the filtered `searched`), `:575-607` (`--files`, with and without `--tag`). A single path inside the archive works as given.
- `src/Kumbu.Core/Sh/Commands/Semsearch.cs:22` (Spec `--archive`), `:93-107` (notes filtered), and `src/Kumbu.Core/Search/SectionSearch.cs:54` `RankAsync(..., bool archive = false)` → `WithPages` (`:176-191`) skips a PDF hit in an archive unless `ArchiveFolder.Shown(scope, archive)` (OP 15).
- Help: one `--archive` line each.
- Test: `tests/Kumbu.Core.Tests/Sh/ArchiveHiddenTests.cs` (rg), `tests/Kumbu.Core.Tests/Search/` semsearch tests with the fake index the existing semsearch tests use (`grep -rln "class SemsearchTests" tests`).

Steps:
- [ ] **Step 1: Red.** `rg pump /Work` skips an archived note and an archived PDF's text, `--archive` finds both, `rg pump /Work/_archive/` finds them; `rg --files /Work` and `rg --files --tag t /Work` skip archived notes; the name hint names no archived note; the scan hint names no archived PDF; `semsearch` leaves out an archived note and an archived PDF's page hit; `--archive` and a scope inside the archive include them. Run `--filter-class "*ArchiveHiddenTests" --filter-class "*SemsearchTests" --filter-class "*RgTests"`: FAIL.
- [ ] **Step 2: Green.** PASS.
- [ ] **Step 3: Commit** "Leave the archive out of rg and semsearch unless asked".
- [ ] **Step 4: Mutations:** (a) filter notes but not PDFs in `rg`; (b) leave `WithPages` unfiltered; (c) ignore `--archive` in `--files`.

### Task 12: `archive` in the registries and help

**Files:** `KumbuMetrics.KnownCommands` (`"archive"` after `"mv"`), `ConsoleApi.Refused` gains `"archive"`, `web/src/console/help.ts:6-11` refused list gains `pdfimages` and `archive` (if Task 7 did not), `Manual.ToolDescription` lines for `archive` and the changed `ls`, `tags`, `backlinks`, `rg`, `semsearch` usages, `Help.cs` topic `["archive"]` (what is hidden where, `--archive`, a path inside counts as asking, restoring with `mv`, not access control, time zone of the stamp), `HelpTests.Commands` gains `archive` (and `pdfimages`).
- Server test: `archive` through MCP `sh` with links following; console refuses `archive`.

Steps:
- [ ] **Step 1: Red.** `Every_shell_command_is_classified` and `HelpTests` fail. Add the server tests. FAIL.
- [ ] **Step 2: Green.** PASS (`--filter-class "*ConsoleApiTests" --filter-class "*HelpTests"` and the new server class).
- [ ] **Step 3: Commit** "Name archive in the help, the metrics and the console's refusals".
- [ ] **Step 4: Mutations:** (a) take `archive` out of `Refused`.

### Phase 3 gate

- [ ] Full verification. Push.

---

## Phase 4: the archive in the web app

### Task 13: Tree, archive listing and figures in the API

**Files:**
- `src/Kumbu.Server/Api/NoteApi.cs:90` `TreeView(TreeFolder Root, int Damaged, ArchiveSummary? Archive = null)` with `ArchiveSummary(int Files, long Bytes)`; `TreeAsync` (`:413-420`) filters notes and attachments in `ArchiveFolder.Of(vault)` before `TreeOf` (OP 12; `TreeOf` unchanged, checkpoint trees keep everything) and sets `Archive` when the archive holds any file other than `.vaultkeep` (bytes: notes' sizes plus attachments' sizes as `du` counts them, `Du.cs:84-85`).
- New `GET /api/vaults/{vault}/archive` → `ArchiveView(IReadOnlyList<ArchiveItem> Items, bool CanWrite)`; `ArchiveItem(string Path, string Name, string OriginalPath, DateTime? ArchivedAt, bool Folder, int Files, long Bytes, string Kind)`. An item: a file or folder directly whose name carries the stamp (`… YYYY-MM-DD HHmmss[-N][.ext]`, parsed by a Core helper `ArchiveFolder.TryParseStamp(name, out original, out local)`) anywhere below `_archive/`; any other file below `_archive/` outside a stamped folder is an item of its own, `ArchivedAt` from its last write (OP 20). Stamped items newest first, the others after them by `ArchivedAt` newest first. `ArchivedAt` from the stamp is server-local time converted to UTC with `TimeProvider.LocalTimeZone`. Read access to the vault (as the tree); `CanWrite` from the caller's level.
- New `POST /api/folders/trash` `{ "path": "/Work/_archive/Inbox/Manual 2026-11-08 213005/" }` → `TreeWriter` trash of the folder with `caller.RefuseFolder("rm", folder)` (spec §3.2, brief's allowed exception); a new `TreeWriter.TrashFolderAsync(VaultPath folder, CancellationToken)` building one batch with `TrashAsync` and committing it, message as `RemoveAsync`. Only folders (path ends with `/`); a vault root refused.
- `GET /api/search` gains `archive` (bool): notes in archives filtered unless `archive=true` or the `path` lies inside one (OP 15).
- Account storage (OP 22): `AccountStorageView(long Used, long? Limit, long Archive = 0)` with the archive bytes across the owner's vaults (the `du` measure), computed in `AccountApi.StorageAsync` from the owned vaults' archives.
- `web/src/api/schema.d.ts` by hand for all of the above.
- Tests: `tests/Kumbu.Server.Tests/Api/ArchiveApiTests.cs`.

Steps:
- [ ] **Step 1: Red.** The tree leaves `_archive` out and carries `archive: { files, bytes }`; none when only a `.vaultkeep`; the listing's items, order, original paths, `archivedAt` from the stamp in a Berlin `FakeTimeProvider` zone; a hand-made file is its own item after the stamped ones; a reader gets `canWrite: false` and `403` on folder trash; folder trash moves every file to the trash and `rm`'s structural rule refuses a folder with `Board.md` for a writer; search leaves an archived note out unless `archive=true`; account storage carries `archive`. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ArchiveApiTests"`: FAIL.
- [ ] **Step 2: Green.** PASS; also `--filter-class "*NoteApiTests" --filter-class "*AccountApiTests"` PASS.
- [ ] **Step 3: Commit** "Give the web app the archive's figures and items, and a folder trash".
- [ ] **Step 4: Mutations:** (a) filter in `TreeOf` (a checkpoint-tree test fails); (b) skip `RefuseFolder`; (c) count `.vaultkeep`.

### Task 14: The tree entry and the Archive view

**Files:** `web/src/shell/TreeDrawer.tsx` (a dimmed row at the bottom of the vault's tree, `Archive · 23 files · 412 MB`, muted text and icon colours as existing muted rows, opening the sheet), `web/src/shell/ArchiveSheet.tsx` (new, built like `TrashSheet.tsx`: items newest first, original path, archived date, size; **Open** (note or attachment route; a folder opens the tree at it), **Restore** (move back without the stamp via `/api/move`; first checks the target with the tree/HEAD the app uses, and on a taken name opens a "Restore as…" dialog with the name prefilled, OP 21), **Delete** (file: the existing delete; folder: `POST /api/folders/trash`), buttons only when `canWrite`), `web/src/shell/MobileShell.tsx:147,352-360` (`kind: "archive"`), `web/src/api/archive.ts` (queries, invalidating `["tree", vault]` and `["account","storage"]`).
- Tests: `web/src/shell/ArchiveSheet.test.tsx`, `TreeDrawer` test for the entry.

Steps:
- [ ] **Step 1: Red.** Entry shown with counts and opens the sheet; absent without archive; sheet lists items newest first; Restore calls move with the original path; a taken name opens the dialog; Delete on a folder calls folder trash; a reader sees no buttons. `pnpm -C web test --run src/shell/ArchiveSheet.test.tsx src/shell/TreeDrawer.test.tsx`: FAIL.
- [ ] **Step 2: Green.** PASS; `pnpm -C web build` PASS.
- [ ] **Step 3: Commit** "Show the archive in the vault's tree with a view to restore or delete".
- [ ] **Step 4: Mutations:** (a) restore without the existence check; (b) show buttons to readers.

### Task 15: Search checkbox and storage notice

**Files:** `web/src/search/SearchSheet.tsx`, `web/src/search/useSearch.ts:31` (query `archive: true` when checked, key includes it), `web/src/shell/StorageNotice.tsx` (names the archive along with the trash when `archive ≥ 0.1 * used`: "Empty the trash or the archive, or ask your admin for more."), tests next to them.

Steps:
- [ ] **Step 1: Red.** Checkbox **Include archive** sends `archive=true`; unchecked sends none; notice names the archive at 10 %, not at 9 %. `pnpm -C web test --run src/search src/shell/StorageNotice`: FAIL.
- [ ] **Step 2: Green.** PASS; `pnpm -C web build` PASS.
- [ ] **Step 3: Commit** "Let the search include the archive and name it in the storage notice".
- [ ] **Step 4: Mutations:** (a) threshold 0; (b) always send `archive`.

### Phase 4 gate

- [ ] Full verification; check the tree entry and the Archive view at phone width in light and dark mode (Playwright screenshot against `pnpm -C web dev` with mocked API, or note in the report why not). Push.

---

## Phase 5: the system skill, documentation, security review

### Task 16: The system skill `ingest-attachments`

**Files:** `src/Kumbu.Core/Skills/System/ingest-attachments/SKILL.md` (embedded as `create-skill` is: check the `.csproj` `EmbeddedResource` glob and the system-skill registry, `grep -rn "create-skill" src`), tests in the existing system-skill test file (`skills check` passes; listed in the instructions).
Content: spec §3.3 steps 1–8, in `create-skill`'s voice and length; links from the vault's root without the vault name, spaces as `%20` (`![Wiring diagram](/Pump%20manual/images/wiring%20diagram.png)`), crop only `drawing` regions, `pdf info` lists regions only from 5 % — look at rendered pages for smaller drawings (OP 23).
Test: a note written as the skill says (in vault `Work`, at `/Work/Pump manual/Overview.md`, embedding `/Pump%20manual/images/wiring%20diagram.png` with the attachment present) resolves (`links` shows it, no broken link).

Steps: red (system skill missing, `skills check` test fails) → green → commit "Add the system skill ingest-attachments" → mutation: an underscore in the name fails `skills check`.

### Task 17: Help, README, welcome note, time zone

**Files:** `README.md` (pdfimages, the archive — hidden where, not access control, the stamp's time zone `TZ` — the new system skill), `compose.yaml` (`TZ: ${TZ:-UTC}` for `kumbu`), `.env.example` (`# TZ=Europe/Berlin` with a line), `src/Kumbu.Server/Welcome/Notes/Skills.md` (the two-sentence example), `docs/superpowers/open-items.md` if `aspnet` lacks tzdata (check: `docker run --rm -e TZ=Europe/Berlin mcr.microsoft.com/dotnet/aspnet:10.0 date` or the digest in `Dockerfile`; if no network, record as open item). Tests: `WelcomeTextTests` stay green.

Steps: edit → `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*WelcomeTextTests"` PASS → commit "Describe pdfimages, the archive and ingesting in the README and the welcome notes".

### Task 18: Security review

A fresh subagent reviews the whole branch (`git diff 3af5397...HEAD`) against spec §5 and the brief's boundary list; every finding goes to the report, fixed (with a test) or not, with the reason.

### Final gate and report

- [ ] Full verification; litmus optional (say whether run).
- [ ] Draft pull request against `feature/skills`, titled "Ingesting attachments: pdfimages, the archive and a system skill", body with **Decisions made without Chris** and "a security review applies", ending with the generated-with line.
- [ ] Row in `docs/superpowers/open-items.md`; `docs/superpowers/overnighter/2026-11-08-ingest-attachments-report.md` as the brief lists. Push.
- [ ] **Do not merge, tag or deploy; don't change the version.**
