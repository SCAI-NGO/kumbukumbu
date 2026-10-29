# Attachments, Part 2: the Model Reads Attachments — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agents look at image attachments with `img` and read PDFs page by page with `pdf` (text by default, rendered pages on request, crops), PDF text is found by `rg` and `semsearch`, and every untrusted PDF or image is parsed and decoded in an isolated sidecar container.

**Architecture:** A new Node service in `pdf/` (pdf.js legacy build + `@napi-rs/canvas`, worker threads with time limits) analyses PDFs and renders pages, crops and images to a size step; it is stateless and reachable only from the server on an `internal` compose network. The server keeps the analysis sealed per blob in a chunked collection (`pdf_analyses`), queues analyses durably (`pdf_jobs`) and runs them while a vault is unlocked. The commands `img` and `pdf` live in Kumbu.Core like every other command and reach pixels and analyses through one Core interface, `IAttachmentPixels`, which the server implements; `ShellResult` gains ordered text and image parts that `ToolResult` turns into MCP content blocks.

**Tech Stack:** .NET 10, xUnit v3; Node 22, TypeScript, pnpm, `pdfjs-dist` 6.4.299 (legacy build), `@napi-rs/canvas` 1.0.13, Vitest, `pdf-lib` (tests only, fixtures); prometheus-net (from monitoring).

**Spec:** `docs/superpowers/specs/2026-10-28-attachments-model-reading-design.md` (part 1: `docs/superpowers/specs/2026-10-27-attachments-foundation-design.md` as built in #81/#84; monitoring: `docs/superpowers/specs/2026-10-28-monitoring-design.md`).

## Global Constraints

- British English in code, comments, docs and commits; SPDX headers (`// SPDX-License-Identifier: AGPL-3.0-or-later` in C# and TypeScript, `# SPDX…` in YAML/Dockerfile where siblings have it); no organisation, customer or employer names anywhere, including fixtures and examples.
- **Untrusted PDFs are never parsed, and untrusted images never decoded, in the process that holds vault keys.**
- Size steps (constants in one place, `src/Kumbu.Core/Attachments/SizeSteps.cs`, printed by `help img` and `help pdf`): `small` 768 px long edge, 0.4 MP (400,000 px), byte target 150 KB; `medium` (default) 1568 px, 1.2 MP (1,200,000 px), 400 KB; `large` 2576 px, 3.7 MP (3,700,000 px), 1 MB. KB = 1024 bytes.
- Images are scaled down to fit both limits keeping the aspect ratio, never enlarged, never refused for size. PDF pages and regions are rendered to fit the step, render scale capped at 600 dpi (600/72 per point).
- Encoding: PNG if the PNG fits the byte target; else JPEG at quality 85, lowered in steps (85, 80, 75, 70, 65, 60) until it fits; if 60 still doesn't fit it is sent as it is and the text block says its size. JPEG of a transparent image is drawn on white.
- Crop `--crop x,y,w,h`: images in the original's pixel coordinates, EXIF-oriented; PDF pages in PDF points of the page as displayed (rotation applied, the CropBox, top-left origin; 1 pt = 1/72 in). `--crop` with a single page only; implies a rendered image.
- At most 4 images per `img`/`pdf` call; text pages don't count.
- Page types: `image` — one image covers ≥ 85 % of the page (with or without a text layer); `mixed` — images plus vector figures cover ≥ 25 % of the page; `text` — otherwise, including an empty page.
- PDFs over 2,000 pages are analysed up to page 2,000 and say so.
- Header limits before decoding (part 1): 50 MP, 16,384 px on a side (`ImageHeader.MaxPixels`, `ImageHeader.MaxSide`).
- No metric label from user input (monitoring's label guard test must stay green with the new metrics).
- New NuGet packages: none expected. npm packages: only in `pdf/` (pnpm, lock file committed).
- Commit messages imperative, no prefix, ending with the session trailer lines.

## Decisions (left to the builder, each with its reason)

- **Images are decoded in the sidecar, not with ImageSharp** (deviates from the spec's "Library: ImageSharp"): part 1 dropped ImageSharp (`docs/concept.md` §12: current versions need a licence key to build, older ones have known vulnerabilities) and left the decoder open. The sidecar already has Skia (`@napi-rs/canvas`) behind the same isolation, limits and time-outs as the PDF parser, so decoding untrusted images there keeps them out of the key-holding process too: one pixel pipeline in one place. The server still checks the header (50 MP, 16,384 px) before sending, and checks what comes back by magic bytes and header.
- **Sidecar API:** `POST /analyse?maxPages=2000` (body: the PDF) → JSON; `POST /render/page?page=N&longEdge=&pixels=&bytes=[&crop=x,y,w,h]` (body: the PDF) → PNG or JPEG; `POST /render/image?longEdge=&pixels=&bytes=[&crop=x,y,w,h]` (body: the image) → PNG or JPEG; `GET /healthz`. Rendered answers carry `X-Kumbu-Width`, `X-Kumbu-Height` (delivered pixels) and, for pages, `X-Kumbu-Page-Width`, `X-Kumbu-Page-Height` (points as displayed); for images `X-Kumbu-Original-Width`, `X-Kumbu-Original-Height`. Errors: JSON `{ "error": "<sentence>", "code": "<code>" }` with 400 `bad_request`/`not_pdf`/`not_image`, 413 `too_large`, 422 `no_such_page`/`bad_crop`/`encrypted` (a password-protected PDF), 503 `busy`, 504 `timeout`, 500 `failed`. Plain HTTP with raw bodies: the smallest interface that carries bytes both ways.
- **Limits:** request bodies ≤ 16 MiB; analysis 30 s plus 0.5 s per page, at most 20 min (the worker reports the page count first and the pool extends the deadline: a scanned page costs about 0.3 s, because pdf.js decodes its image even for the analysis, and an ordinary scanned book must not time out for good); render 10 s per job; 2 workers, a queue of 16 (then 503); worker `resourceLimits.maxOldGenerationSizeMb` 512; a worker is terminated (not reused) after a time-out. Two workers keep a 2-CPU host responsive; a queue bounds memory.
- **Images inside a PDF:** pdf.js's `maxImageSize` is 50,000,000 pixels; a larger image is dropped (its page reads as `text`), so a small file can't make the parser allocate gigabytes.
- **Analysis rules as built (rules version 1):** path boxes per subpath (a table grid drawn as one path stays thin lines), clip-only paths ignored, pure-white fills count as paper; more than 20,000 path boxes on a page skip clustering (their union counts, one region).
- **Vector clustering:** each `constructPath`'s bounding box (pdf.js gives `minMax`) is transformed by the current transformation matrix into displayed points; boxes with a side under 2 pt are *thin* (rules, table borders, axis lines) and dropped; the rest are merged when their boxes, grown by 6 pt, overlap, until stable; a cluster is a figure if its area is ≥ 1 % of the page. Coverage is measured on a 200 × 200 grid over the page (union area, no double counting). Reason: cheap, deterministic, and good enough to tell a chart from body text; the metrics will show where it errs.
- **Analysis storage:** collection `pdf_analyses`: `{ vault, bid (the blob id), part, blob (sealed), at }`, unique `(vault, bid, part)`; part 0 is the header (analyser, page count, analysed pages, title, author, per-page type/size/rotation/coverages/regions — everything but text), parts 1… hold page text in chunks of at most 1 MiB of JSON. Sealed under context `pdfa:{vaultId}:{base64 bid}:{part}`. Jobs: `pdf_jobs` `{ vault, bid, at, tries }`, unique `(vault, bid)`, no content. Analyser version: `pdfjs-dist@6.4.299+rules.1`.
- **Page summary lives in part 0, not in the attachment record:** an analysis finishes after the upload's commit; writing the record then would bump its revision and wake sync clients for nothing. `cat` and `pdf info` read part 0 (one small document).
- **Text pages per `pdf` call:** at most 20, and fewer if the next page would push the text past `OutputLimit.MaxChars`; a continuation line names the next range.
- **`rg` on PDFs:** lines of each page's text; match lines print as `/V/paper.pdf:p7: …` (with `-n`: `/V/paper.pdf:p7:12: …`, the line within the page); context options work within a page; `-l`/`-c` per PDF; `--files` and `--tag` stay notes-only. PDFs are read after the notes, one at a time, from stored analyses only (never run on demand).
- **`semsearch`:** a PDF's pages are embedded as its chunks (`EmbeddedChunk.StartLine` = page number) under the PDF's path; meaning ranking includes them, `--words` (BM25) stays notes-only (documented); a PDF hit prints `path p7` and the start of the page text.

## Review Focus

1. A hostile or broken PDF (not a PDF, truncated, an endless content stream) must give a clear error within the time limit and leave the sidecar serving the next request — sidecar test with a stubbed slow job and a garbage body (Task 1/2).
2. `pdf` on a PDF whose analysis is pending while the sidecar is down: text says "not analysed yet", `--images=all` renders fail with "PDF rendering is unavailable right now", nothing throws to the MCP client — server test (Task 6).
3. A vault re-keyed or deleted, or a blob released, must take its analyses along (no sealed analysis under an old key, no orphan) — server tests (Task 4).
4. An image whose EXIF orientation is 6 or 8: crop coordinates and delivered size follow the oriented image — sidecar test (Task 2).
5. A pipeline with `img`/`pdf` anywhere is refused before anything runs — Core test (Task 3).

---

### Task 1: Sidecar: project, analysis and classification

**Files:**
- Create: `pdf/package.json`, `pdf/pnpm-lock.yaml`, `pdf/tsconfig.json`, `pdf/vitest.config.ts`, `pdf/.gitignore`, `pdf/src/classify.ts`, `pdf/src/analyse.ts`, `pdf/src/pdfjs.ts`, `pdf/test/fixtures.ts`, `pdf/test/classify.test.ts`, `pdf/test/analyse.test.ts`
- Modify: root `.gitignore` if it doesn't cover `pdf/node_modules`, `pdf/dist`

**Interfaces:**
- Produces `classify.ts` (pure):
  ```ts
  export type Box = { x: number; y: number; w: number; h: number }; // displayed points, top-left origin
  export type PageMeasure = { width: number; height: number; textBoxes: Box[]; imageBoxes: Box[]; pathBoxes: Box[] };
  export type Region = { kind: "image" | "drawing"; x: number; y: number; w: number; h: number };
  export type PageType = "text" | "mixed" | "image";
  export type Classified = { type: PageType; textCoverage: number; imageCoverage: number; vectorCoverage: number; images: number; regions: Region[] };
  export function classify(page: PageMeasure): Classified;
  export const RULES_VERSION = 1;
  ```
  Thresholds as named constants (`FULL_PAGE_IMAGE = 0.85`, `MIXED = 0.25`, `THIN_PT = 2`, `MERGE_PT = 6`, `MIN_FIGURE = 0.01`, `GRID = 200`). Coverages in 0…1, rounded to 3 decimals; regions rounded to 0.1 pt, sorted top to bottom, left to right; images below `MIN_FIGURE` are counted but not listed as regions.
- Produces `analyse.ts`:
  ```ts
  export type PageAnalysis = Classified & { n: number; width: number; height: number; rotation: number; chars: number; text: string };
  export type Analysis = { analyser: string; pageCount: number; analysedPages: number; title: string | null; author: string | null; pages: PageAnalysis[] };
  export async function analyse(bytes: Uint8Array, maxPages: number): Promise<Analysis>;
  export const ANALYSER: string; // `pdfjs-dist@${version}+rules.${RULES_VERSION}`
  ```
  `pdfjs.ts` opens documents with the hardening shared by every use: `isEvalSupported: false`, `enableXfa: false`, `disableFontFace: true`, `useSystemFonts: false`, `stopAtErrors: false`, `verbosity: 0`, `standardFontDataUrl` pointing into `pdfjs-dist/standard_fonts/`, no scripting. Text in reading order as `getTextContent()` gives it (items joined, `hasEOL` → newline); text boxes from each item's transform and width/height through `viewport.convertToViewportRectangle`; image boxes from the unit square under the CTM at `paintImageXObject`/`paintInlineImageXObject`/`paintImageMaskXObject`; path boxes from `constructPath`'s `minMax` under the CTM (track `save`/`restore`/`transform`). A non-PDF throws an error whose `code` is `not_pdf`.

- [ ] **Step 1: Scaffold** `pdf/` (Node ≥ 22.13, `"type": "module"`, scripts `build` = `tsc -p .`, `test` = `vitest run`, `start` = `node dist/server.js`; deps `pdfjs-dist@6.4.299`, `@napi-rs/canvas@1.0.13`; devDeps `typescript`, `vitest`, `@types/node`, `pdf-lib`). `pnpm -C pdf install`.
- [ ] **Step 2: Write the failing tests.** Fixtures generated in `test/fixtures.ts` with pdf-lib (and `@napi-rs/canvas` to make PNG bytes for embedded images): `textPage()` (40 lines of body text), `chartPage()` (a title, 12 filled bars and an axis line covering ≈ 35 % of an A4 page), `logoPage()` (body text and one 40 × 40 pt image), `scanPage(withInvisibleText)` (one image covering the whole page; optionally text drawn with rendering mode 3, `setTextRenderingMode(3)` via `pushOperators`), `rotatedPage()` (landscape page with `/Rotate 90` and a filled box at a known place), `emptyPage()`, `tablePage()` (text in a grid of thin lines).
  - `classify.test.ts` (pure, hand-built `PageMeasure`s): a 90 % image → `image`; two boxes of 15 % each → `mixed` with `vectorCoverage` ≈ 0.30; overlapping boxes are not counted twice; thin 0.5 pt rules alone → `text`, no regions; an empty page → `text` with coverages 0.
  - `analyse.test.ts`: text page → `text`, chars > 1000, text contains "Body text line 3"; chart page → `mixed`, `vectorCoverage` between 0.25 and 0.5, one `drawing` region around the bars (within 10 pt of their union box); logo page → `text`, `images` 1, `imageCoverage` < 0.01; scan page → `image` with and without the invisible text (chars 0 vs > 0); rotated page → `rotation` 90, width/height swapped (595 × 842 shown as 842 wide?) — assert `width > height` for a portrait MediaBox rotated by 90, and the box's region in displayed coordinates; empty → `text`, chars 0; table → `text`; `maxPages` 2 on a 3-page document → `pageCount` 3, `analysedPages` 2; garbage bytes → rejects with `code: "not_pdf"`.
- [ ] **Step 3: Run** `pnpm -C pdf test` — FAIL.
- [ ] **Step 4: Implement** `classify.ts`, `pdfjs.ts`, `analyse.ts`.
- [ ] **Step 5: Run** `pnpm -C pdf test` and `pnpm -C pdf build` — PASS, no warnings.
- [ ] **Step 6: Commit** — "Add the PDF sidecar's page analysis"

---

### Task 2: Sidecar: rendering, image scaling, workers, HTTP server, image

**Files:**
- Create: `pdf/src/steps.ts`, `pdf/src/encode.ts`, `pdf/src/render.ts`, `pdf/src/image.ts`, `pdf/src/exif.ts`, `pdf/src/pool.ts`, `pdf/src/worker.ts`, `pdf/src/server.ts`, `pdf/Dockerfile`, `pdf/.dockerignore`, `pdf/test/render.test.ts`, `pdf/test/image.test.ts`, `pdf/test/pool.test.ts`, `pdf/test/server.test.ts`

**Interfaces:**
- Consumes: Task 1's `analyse`, `pdfjs.ts`.
- Produces:
  ```ts
  export type Target = { longEdge: number; pixels: number; bytes: number };
  export type Crop = { x: number; y: number; w: number; h: number };
  export type Rendered = { data: Uint8Array; mediaType: "image/png" | "image/jpeg"; width: number; height: number };
  export function fit(w: number, h: number, t: Target, maxScale: number): number; // scale: min(longEdge/max(w,h), sqrt(pixels/(w*h)), maxScale)
  export async function encode(canvas: Canvas, bytes: number, opaque: boolean): Promise<Rendered>; // the encoding rule
  export async function renderPage(pdf: Uint8Array, page: number, t: Target, crop?: Crop): Promise<Rendered & { pageWidth: number; pageHeight: number }>;
  export async function renderImage(img: Uint8Array, t: Target, crop?: Crop): Promise<Rendered & { originalWidth: number; originalHeight: number }>;
  export class Pool { constructor(o: { workers: number; queue: number }); run<T>(job: Job, timeoutMs: number): Promise<T>; close(): Promise<void> }
  ```
  `renderPage`: `maxScale` = 600/72; for a crop, the viewport is offset so the region's top-left is at 0,0 and the canvas is the region's size at the scale (a region outside the page → `bad_crop`). `renderImage`: decode with `loadImage` (GIF: first frame), apply EXIF orientation (parse it in `exif.ts` if Skia doesn't apply it — the test decides), `maxScale` = 1 (never enlarge), crop in oriented pixel coordinates, refuse a header over 50 MP or 16,384 px a side before decoding (`too_large`). `server.ts`: `node:http`, port from `PORT` (default 8090), host `0.0.0.0`, body limit 16 MiB (413 before reading further), routes and errors as in the plan's decisions; each job runs in the pool; a time-out terminates the worker and answers 504. No request logging of content or parameters beyond method, route, status and duration.
  Dockerfile: multi-stage on `node:22-bookworm-slim` pinned by digest, `corepack`/pnpm, `pnpm install --frozen-lockfile`, `pnpm build`, prune to production deps, run as `node` (uid 1000), `ENV NODE_ENV=production PORT=8090`, `EXPOSE 8090`, `HEALTHCHECK` via `node -e` fetch of `/healthz`, `CMD ["node","dist/server.js"]`.

- [ ] **Step 1: Write the failing tests**
  - `render.test.ts`: chart page at `medium` → long edge 1568 px or less (A4: 1109 × 1568), PNG (line art fits 400 KB); `small` → ≤ 768 px long edge and ≤ 400,000 px; a crop `70,400,460,300` at `large` renders a canvas of 460:300 aspect at ≤ 600 dpi (≤ 3834 × 2500 → capped by the step: ≤ 2576 long edge); a region beyond the page → `bad_crop`; page 9 of 3 → `no_such_page`; a photo-like page (random noise image) at `small` → JPEG ≤ 150 KB or the smallest JPEG with its size; `encode` chooses PNG when it fits and JPEG otherwise.
  - `image.test.ts`: a 4000 × 3000 PNG at `medium` → 1449 × 1087 or smaller (budget 1.2 MP: scale √(1.2e6/1.2e7)), never enlarged (a 300 × 200 image stays 300 × 200); a JPEG with EXIF orientation 6 (stored 400 × 200) → original 200 × 400, crop `0,0,200,100` returns the top of the displayed image (assert colour of a marker painted in the stored image's corner that orientation 6 moves to the top-left); a PNG header claiming 20,000 × 20,000 → `too_large` without decoding; garbage → `not_image`.
  - `pool.test.ts`: a job that never ends is rejected with `timeout` after its limit and the next job succeeds; more than `workers + queue` jobs → `busy`.
  - `server.test.ts` (server on port 0): `/healthz` 200; `/analyse` with the chart fixture → JSON with `pages[1].type` set; `/analyse` with garbage → 400 `not_pdf`; a body over 16 MiB → 413; `/render/page` → `image/png` with the `X-Kumbu-*` headers; unknown route → 404.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `pnpm -C pdf test`, `pnpm -C pdf build`, `docker build -t kumbukumbu-pdf:dev pdf` (pull the base via `mirror.gcr.io/library/node:…` and retag if Docker Hub rate-limits; the Dockerfile itself names `node:22-bookworm-slim@sha256:…`), then `docker run --rm -d -p 127.0.0.1:18090:8090 --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges kumbukumbu-pdf:dev` and `curl -s --data-binary @<chart fixture> 'localhost:18090/render/page?page=2&longEdge=1568&pixels=1200000&bytes=409600' -o /tmp/x.png -D -` → 200 with the headers. Stop it.
- [ ] **Step 5: Commit** — "Render pages, crops and images in the PDF sidecar"

---

### Task 3: Core: image parts in shell results, size steps, crops, and the pixel interface

**Files:**
- Create: `src/Kumbu.Core/Sh/ShellPart.cs`, `src/Kumbu.Core/Attachments/SizeSteps.cs`, `src/Kumbu.Core/Attachments/Crop.cs`, `src/Kumbu.Core/Attachments/IAttachmentPixels.cs`, `src/Kumbu.Core/Attachments/PdfAnalysis.cs`
- Modify: `src/Kumbu.Core/Sh/ShellResult.cs`, `src/Kumbu.Core/Sh/ICommand.cs`, `src/Kumbu.Core/Sh/Shell.cs`, `src/Kumbu.Server/Mcp/ToolResult.cs`
- Test: `tests/Kumbu.Core.Tests/Sh/ShellPartsTests.cs`, `tests/Kumbu.Core.Tests/Attachments/SizeStepsTests.cs`, `tests/Kumbu.Core.Tests/Attachments/CropTests.cs`, `tests/Kumbu.Server.Tests/Mcp/ImageContentTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public abstract record ShellPart;
  public sealed record TextPart(string Text) : ShellPart;
  public sealed record ImagePart(byte[] Data, string MediaType) : ShellPart;
  // ShellResult: public IReadOnlyList<ShellPart>? Parts { get; init; }  — when set, Output is the parts' text joined (logs, OutputLimit sees text only and must not cut a parts result)
  // ICommand: bool ProducesImages => false;  (default interface member)
  public enum SizeStep { Small, Medium, Large }
  public sealed record StepLimits(int LongEdge, int Pixels, int Bytes);
  public static class SizeSteps { public static StepLimits Of(SizeStep s); public static bool TryParse(string text, out SizeStep step); public static string Name(SizeStep s); public static string Table { get; } /* the help table */ }
  public sealed record Crop(double X, double Y, double Width, double Height) { public static bool TryParse(string text, out Crop crop, out string error); public override string ToString(); /* "x,y,w,h" invariant */ }
  public sealed record Picture(byte[] Data, string MediaType, int Width, int Height, double OriginalWidth, double OriginalHeight);
  public enum PageType { Text, Mixed, Image }
  public sealed record PdfRegion(string Kind, double X, double Y, double Width, double Height);
  public sealed record PdfPage(int Number, double Width, double Height, int Rotation, PageType Type, int Chars, double TextCoverage, double ImageCoverage, double VectorCoverage, int Images, IReadOnlyList<PdfRegion> Regions);
  public sealed record PdfAnalysis(string Analyser, int PageCount, int AnalysedPages, string? Title, string? Author, IReadOnlyList<PdfPage> Pages);
  public sealed class PixelsUnavailableException(string message) : Exception(message);
  public interface IAttachmentPixels
  {
      /// <summary>The image at a step, optionally cropped (original pixel coordinates, EXIF-oriented).</summary>
      Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, CancellationToken ct);
      /// <summary>A rendered page (1-based), optionally a region in displayed points.</summary>
      Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, CancellationToken ct);
      /// <summary>The stored analysis (without page text); <paramref name="runIfPending"/> runs a missing or outdated one now, within its time limit. Null if there is none and it can't be had.</summary>
      Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct);
      /// <summary>The text of pages <paramref name="first"/> to <paramref name="last"/> of a stored analysis (index 0 = page first); null without one.</summary>
      Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct);
      void Count(AttachmentUse use);
  }
  public sealed record AttachmentUse(...); // filled in by Task 9; Task 3 defines it empty-bodied as `public abstract record AttachmentUse;`
  ```
  `ImageAsync`/`PageAsync` throw `PixelsUnavailableException` with a sentence for the model ("PDF rendering is unavailable right now; try again later", "…image is too large to look at (… megapixels; the limit is 50)") — never another exception for an expected failure.
- `ToolResult.From`: with `Parts`, one content block per part in order (`TextContentBlock`, `ImageContentBlock { Data = base64, MimeType }`; check the SDK 2.3 type names), the version block after them as today.
- `Shell` constructor: a new optional parameter after `IShellObserver? observer = null`: `IAttachmentPixels? pixels = null` (DI passes the server's scoped implementation once Task 4 registers it; Core tests pass a fake or nothing). Tasks 5–8 construct `img`/`pdf` and hand `pixels` to `Rg`/`Semsearch` from it; without it `img` and `pdf` are not registered. For the refusal test, an `internal void Register(ICommand command)` on `Shell` (Core tests see internals; check `InternalsVisibleTo`).
- `Shell.RunAsync`: if the pipeline has more than one stage and any stage names a command with `ProducesImages`, answer `sh: {name} shows images and can't be part of a pipeline; run it on its own` before running anything.

- [ ] **Step 1: Write the failing tests**
  - `SizeStepsTests`: the three rows equal the constraint values; `TryParse("Large")` is case-insensitive; `Table` contains `768`, `1568`, `2576`, `0.4 MP`, `1.2 MP`, `3.7 MP`, `150 KB`, `400 KB`, `1 MB`.
  - `CropTests`: `"0,1200,1440,800"` parses; `"1,2,3"`, `"a,b,c,d"`, negative x, zero or negative width/height, NaN → false with an error naming the expected form `x,y,w,h`; decimals with `.` parse invariantly.
  - `ShellPartsTests` (a fake command with `ProducesImages` registered through a test hook, or a test double of `Shell` built with it): `fake | head` and `cat /V/a.md | fake` → the refusal, the fake never ran; `fake` alone returns its parts untouched by `OutputLimit` (a 30,000-char text part plus an image).
  - `ImageContentTests` (`ToolResult.From`): a result with `[Text "a", Image png, Text "b"]` → three blocks in order, the image block base64 of the bytes with `image/png`.
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** both suites' focused tests, then the full Core suite.
- [ ] **Step 5: Commit** — "Let shell results carry images, and define size steps, crops and the pixel interface"

---

### Task 4: Server: sidecar client, analysis storage and the job queue

**Files:**
- Create: `src/Kumbu.Server/Attachments/PdfSidecar.cs` (HTTP client), `src/Kumbu.Server/Attachments/PdfAnalyses.cs` (store: seal, chunk, read, delete), `src/Kumbu.Server/Attachments/PdfAnalysisRunner.cs` (hosted service: jobs), `src/Kumbu.Server/Attachments/AttachmentPixels.cs` (`IAttachmentPixels`)
- Modify: `src/Kumbu.Server/KumbuOptions.cs` (`PdfOptions Pdf { Url = "" , AnalysisSeconds = 30, RenderSeconds = 10 }`), `KumbuOptionsValidator.cs` (Url: empty or absolute http URL), `src/Kumbu.Server/Vaults/Documents.cs` (`PdfAnalysisDocument`, `PdfJobDocument`), `src/Kumbu.Server/Vaults/VaultCatalog.cs` (collections, indexes, `ContentCollections`), `src/Kumbu.Server/Vaults/UnlockedVault.cs` (`SealPdfAnalysis`/`OpenPdfAnalysis`, `Reseal` branch), `src/Kumbu.Server/Vaults/VaultCatalog.Rekey.cs` (`pdf_analyses` in `RekeyOrder` after `blobs`, its `pid` = the new blob id from `RekeyIds.Blobs`), `src/Kumbu.Server/Vaults/BlobSweep.cs` (delete analyses and jobs of released blobs, and orphans), `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (enqueue a job in `TryCommitAsync` for each PDF state it commits whose blob has neither analysis nor job; `IAttachmentPixels` registration), `src/Kumbu.Server/Program.cs`
- Test: `tests/Kumbu.Server.Tests/Attachments/FakePdfSidecar.cs`, `tests/Kumbu.Server.Tests/Attachments/PdfAnalysisStoreTests.cs`, `tests/Kumbu.Server.Tests/Attachments/PdfJobTests.cs`

**Interfaces:**
- Consumes: Task 3's Core types; the sidecar API (plan decisions).
- Produces:
  ```csharp
  public interface IPdfSidecar   // PdfSidecar implements it over HttpClient; tests replace it
  {
      Task<SidecarAnalysis> AnalyseAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken ct);
      Task<Picture> RenderPageAsync(ReadOnlyMemory<byte> pdf, int page, StepLimits target, Crop? crop, CancellationToken ct);
      Task<Picture> RenderImageAsync(ReadOnlyMemory<byte> image, StepLimits target, Crop? crop, CancellationToken ct);
  }
  public sealed record SidecarAnalysis(PdfAnalysis Analysis, IReadOnlyList<string> PageTexts);
  public sealed class SidecarException(string code, string message) : Exception(message) { public string Code { get; } = code; } // codes as the sidecar's plus "unavailable" (connection refused, no Url, 5xx), "timeout"
  ```
  `PdfSidecar` checks every image it receives: magic bytes PNG or JPEG matching the content type, `ImageHeader.Read` dimensions equal to `X-Kumbu-Width/Height` and within the target's long edge (+1 px rounding) — else `SidecarException("failed", …)`. Analysis JSON is bounded (reject over 64 MiB) and validated (page numbers 1…N, coverages 0…1, finite numbers).
  `PdfAnalyses`: `Task StoreAsync(UnlockedVault, byte[] blobId, SidecarAnalysis, CancellationToken)` (replace all parts in one transaction), `Task<PdfAnalysis?> HeaderAsync(UnlockedVault, byte[] blobId, CancellationToken)`, `Task<IReadOnlyList<string>?> TextAsync(UnlockedVault, byte[] blobId, int first, int last, CancellationToken)` (reads only the chunks covering the range), `const int MaxPages = 2000`, `const string Analyser = "pdfjs-dist@6.4.299+rules.1"` (must equal the sidecar's `ANALYSER`; an analysis with another analyser counts as outdated).
  `PdfAnalysisRunner`: on `VaultCatalog.Unlocked` and once a minute, takes the jobs of unlocked vaults one at a time (bounded: one analysis at a time server-wide), opens the blob, calls the sidecar (its HTTP time-out above the sidecar's largest limit: 21 min), stores, deletes the job; on `unavailable`/`timeout` keeps the job (`tries` + 1, retried next round; after 5 tries on a non-`unavailable` failure gives up and records the failure so `pdf` reports "this PDF couldn't be analysed: …"); never logs content or names, only vault id and outcome. `Task<PdfAnalysis?> RunNowAsync(UnlockedVault, AttachmentState, CancellationToken)` for `pdf`'s on-demand run: waits at most 60 s for the result (shares the server-wide gate); past that the job stays queued and `pdf` answers that the PDF is being analysed. An `encrypted` answer is stored as a failed analysis so `pdf` says "this PDF is password-protected; Kumbu² can't read it".
- [ ] **Step 1: Write the failing tests** (`app` and `mongo` collections; `FakePdfSidecar` returns canned analyses and solid-colour PNGs of the requested size, counts calls, can be switched to "down" or "slow"):
  - `PdfAnalysisStoreTests`: store and read back a 2,000-page analysis with 20 KB of text per page (> 16 MB in total: several parts, none over 16 MB); `TextAsync(…, 1500, 1502)` returns three pages and reads only one text part (assert through a counting hook or by part numbers); the sealed bytes don't contain a page's text in plain; another vault's key can't open it.
  - `PdfJobTests`: uploading a PDF (`PUT` through the files API, as `FileApiTests` do) leaves a job; with the fake sidecar up the runner stores the analysis and removes the job; with it down the job stays and runs after it comes back; a restart (a new host on the same database) finds the job and runs it after unlock; uploading the same bytes again under another name adds no second job or analysis; trashing and expiring the attachment (BlobSweep past `Grace` and retention) deletes analysis and job; deleting the vault deletes them; re-keying the vault re-seals the analysis so it opens under the new key and the blob's new id (the existing re-keying tests show how to run one).
- [ ] **Step 2: Run** — FAIL.
- [ ] **Step 3: Implement.** Register `PdfSidecar` (typed `HttpClient`, `Timeout` = the job limit + 5 s), `PdfAnalyses`, `PdfAnalysisRunner` (hosted), `AttachmentPixels` (scoped; uses `EncryptedNoteStore` to resolve the vault and open blobs). `compose.yaml` is Task 10's.
- [ ] **Step 4: Run** the focused tests, then the full server suite.
- [ ] **Step 5: Commit** — "Analyse PDFs in the sidecar and keep the analysis sealed with their blob"

---

### Task 5: `img`

**Files:**
- Create: `src/Kumbu.Core/Sh/Commands/Img.cs`, `src/Kumbu.Core/Sh/PictureText.cs` (the text block shared with `pdf`)
- Modify: `src/Kumbu.Core/Sh/Shell.cs` (register `img` when an `IAttachmentPixels` is given; DI passes the server's), `src/Kumbu.Core/Sh/Commands/Help.cs` (drop the `img` stub topic: the command's own help replaces it; the `attachments` chapter mentions `img`), `src/Kumbu.Server/Attachments/AttachmentPixels.cs` (`ImageAsync`: header check, open blob, sidecar, metrics later)
- Test: `tests/Kumbu.Core.Tests/Sh/ImgTests.cs` (fake `IAttachmentPixels`), `tests/Kumbu.Server.Tests/Attachments/ImgToolTests.cs` (MCP over the app, fake sidecar)

**Interfaces:**
- `img <path> [--size small|medium|large] [--crop x,y,w,h]`; `ProducesImages = true`; `ReadsInput = false`.
- Add `img` to `KumbuMetrics.KnownCommands` (src/Kumbu.Server/Metrics/KumbuMetrics.cs): `Known_commands_are_the_shells` compares it with the shell's help list (likewise `pdf` in Task 6).
- Text block (exact shape; `PictureText.For(...)`):
  ```
  /Screens/shot.png (version 3f9c2a1b): image (PNG), original 1440×3120 px; medium: 724×1568 px PNG, 312 KB
  ```
  With a crop: `; crop 0,1200,1440,800` before the step. With a page (Task 6): `/Papers/a.pdf (version …), page 4 of 12 (mixed): page 595×842 pt; medium: 1109×1568 px PNG, 288 KB`. Over the byte target: `…, 1.2 MB (over the 400 KB target for medium)`. Aspect ratio over 4:1 either way: a line `Very tall/wide: details may be too small to read; look at a part with --crop x,y,w,h.` Always last: `If no image follows, your client dropped it: tell the user so instead of guessing what it shows.`
- Refusals (`ShellResult.Error`, kind `Invalid`/`NotFound`): a note → `img: /x.md is a note; cat reads notes`; a PDF → `img: /a.pdf is a PDF; look at its pages with pdf /a.pdf <pages> --images (see 'help pdf')`; missing → as `cat` words it; a crop outside the image → `img: the crop 10,10,5000,5 lies outside the image (1440×3120 px)`; header over the limits → the `PixelsUnavailableException` message.
- [ ] **Step 1: Write the failing tests** — Core: each step passes its `SizeStep` to the fake; default `medium`; crop parsed and passed; text block exactly as above for a fake 1440 × 3120 → 724 × 1568 PNG of 312 KB; tall hint for 1440 × 6000; refusals for a note, a PDF, a missing path, `--size huge`, a bad crop; `img a | head` refused (Task 3). Server: MCP `sh` `img /Samples/<a sample image uploaded first>` returns `[text, image]` with `image/png` and the fake's bytes; `--crop` reaches the fake sidecar with the same numbers; sidecar down → tool error with "unavailable".
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — "Show an image attachment to the model with img"

---

### Task 6: `pdf` and `cat` on a PDF

**Files:**
- Create: `src/Kumbu.Core/Sh/Commands/PdfCommand.cs`, `src/Kumbu.Core/Sh/PageRange.cs`
- Modify: `src/Kumbu.Core/Sh/Shell.cs` (register `pdf`), `src/Kumbu.Core/Sh/Commands/Help.cs` (drop the `pdf` stub; `attachments` chapter updated: `img`, `pdf`, search of PDF text), `src/Kumbu.Core/Sh/Commands/Cat.cs` and `src/Kumbu.Core/Sh/AttachmentText.cs` (the PDF line gains page count and type runs and points to `pdf`), `src/Kumbu.Server/Attachments/AttachmentPixels.cs` (`PageAsync`, `AnalysisAsync`, `PageTextAsync`)
- Test: `tests/Kumbu.Core.Tests/Sh/PdfCommandTests.cs`, `tests/Kumbu.Core.Tests/Sh/PageRangeTests.cs`, `tests/Kumbu.Server.Tests/Attachments/PdfToolTests.cs`

**Interfaces:**
- `pdf info <path>`; `pdf <path> [pages] [--images | --images=all] [--crop x,y,w,h] [--size S]`. `pages`: `N`, `N-M`, `N-` (to the end); default all. `ProducesImages = true`.
- `pdf info`: `smith-2025.pdf: PDF, 12 pages, 2.1 MB` / `Title: …` / `Author: …` (if present) / `Pages: 1-3 text, 4 mixed, 5-12 text` / then per non-text page `p4 mixed: vector drawing ≈ 35 % — regions (pt, x,y,w,h): drawing 72,300,451,260; image 80,90,200,120` (at most 8 regions per page, then `…and N more`); over the output limit, the per-page lines stop with `…pages 300-2000 not listed; pdf info shows the first N`. Analysed fewer than all: `Analysed the first 2000 of 2400 pages; later pages have no text or types.`
- Page headers (exact): `--- Page 4 of 12: mixed (text; vector drawing ≈ 35 % of the page) — add --images to see it ---`, `--- Page 7 of 23: image (no text layer; one image covers the page) — add --images to see it ---`, `--- Page 2 of 9: text (1 small image ≈ 6 %) ---`, `--- Page 3 of 9: text ---`; with `--images` and a rendered page, the hint is left out and the rendered page's `PictureText` block follows the text. Percentages rounded to whole numbers; "≈ 0 %" never printed (left out instead).
- `--images`: renders `mixed` and `image` pages of the range; `--images=all`: every page. At most 4 rendered pages: the range stops after the page that brought the fourth image, with `--- Stopped after page 9: at most 4 images per call. Continue with: pdf /Papers/a.pdf 10-20 --images ---`. Text-only: at most 20 pages and within `OutputLimit.MaxChars`, same continuation form without the flag.
- `--crop` with a range of more than one page → `pdf: --crop takes a single page, e.g. pdf /a.pdf 4 --crop 72,300,451,260`; with one page it renders that region (the page's text is not printed; the block names the crop).
- Pending analysis: `AnalysisAsync(runIfPending: true)`; if still null: `--- Page N of ?: not analysed yet (PDF rendering is unavailable right now) ---` text mode prints `pdf: /a.pdf hasn't been analysed yet: its text and page types aren't known. Try again in a while; pdf /a.pdf 1 --images=all shows a page as an image.`; `--images` → `pdf: --images chooses pages by their type, which isn't known yet; use --images=all for a range`; `--images=all` and `--crop` render anyway (page count unknown → the sidecar's `no_such_page` becomes `pdf: /a.pdf has no page 40`).
- `cat` on a PDF: `paper.pdf: PDF, 12 pages (1-3 text, 4 mixed, 5-12 text), 2.1 MB — read it with pdf (help pdf)`; not analysed: `paper.pdf: PDF, 2.1 MB — not analysed yet; pdf reads it (help pdf)`.
- `help pdf` and `help img`: usage, the size table (`SizeSteps.Table`), the 4-image limit, the text page limit, crop units, page types with their thresholds, that scans aren't found by search, that the model's client may drop images, and each refers to the other.
- [ ] **Step 1: Write the failing tests** — `PageRangeTests` (`4`, `5-9`, `5-`, `0`, `9-5`, `x` → errors naming the form; clamped to page count with a message for a start past the end). `PdfCommandTests` with a fake analysis of 12 pages (types as in the example) and fake texts: headers exactly as above; `--images` on 1-12 renders pages 4 and others typed non-text, `--images=all 1-12` stops after 4 with the continuation line; text-only 1-30 of a 30-page document stops at 20 with the continuation; `--crop` with `4-5` refused, with `4` renders a crop and passes the numbers; info output; pending + sidecar down cases; `cat` lines. `PdfToolTests` (app, fake sidecar): upload a PDF, run the job, `pdf info` and `pdf … --images` through MCP → text and image blocks in page order.
- [ ] **Step 2–4: Run, implement, run.**
- [ ] **Step 5: Commit** — "Read PDFs page by page with pdf"

---

### Task 7: `rg` finds text in PDFs

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/Rg.cs` (an optional `IAttachmentPixels`; after the notes, each PDF below the paths with a stored analysis, read with `PageTextAsync` in pages of 100), `src/Kumbu.Core/Sh/Shell.cs`, `src/Kumbu.Core/Sh/Commands/Help.cs` (`attachments` chapter), `Rg.Help`
- Test: `tests/Kumbu.Core.Tests/Sh/RgPdfTests.cs`, one MCP test in `tests/Kumbu.Server.Tests/Attachments/PdfToolTests.cs`

**Interfaces:** match lines `/V/paper.pdf:p7: <line>`; `-n` → `/V/paper.pdf:p7:12: <line>`; `-C/-A/-B` within a page (context lines with `-` separator as for notes: `/V/paper.pdf:p7-…`); `-l` lists the PDF once; `-c` → `/V/paper.pdf:3`; `-m` counts per PDF; `--files` notes only; `--tag` excludes PDFs; a PDF without stored analysis is skipped silently (no on-demand run); `rg` on a single PDF path searches it; `-i`, `-w`, `-F`, `-v` as for notes.
- [ ] **Step 1: Tests** for each line form above, a PDF without analysis skipped, `--tag` excluding PDFs, scope by folder; `rg` with no PDFs behaves byte-for-byte as before (run the existing `Rg` tests).
- [ ] **Steps 2–4.** **Step 5: Commit** — "Let rg search the text of PDFs"

---

### Task 8: `semsearch` finds PDF pages

**Files:**
- Modify: `src/Kumbu.Server/Search/SearchIndex.cs` (index a PDF attachment's pages: chunks = pages with text, `StartLine` = page number, `Version` = the attachment's version token; `CatchUp` includes PDFs with a stored analysis and doesn't delete their embeddings as "note gone"; the runner enqueues a PDF's paths after storing its analysis), `src/Kumbu.Server/Attachments/PdfAnalysisRunner.cs`, `src/Kumbu.Core/Sh/Commands/Semsearch.cs` (a hit on a `.pdf` prints `path p7`, its score and the start of the page text from `PageTextAsync`; BM25 over notes only), `Semsearch.Help`, `Help.cs`
- Test: `tests/Kumbu.Server.Tests/Search/PdfSearchTests.cs`, `tests/Kumbu.Core.Tests/Sh/SemsearchPdfTests.cs`

- [ ] **Step 1: Tests** — server (FakeEmbedder): upload a PDF whose page 2 holds a unique phrase, run the job, `app.IndexedAsync()`, MCP `semsearch <phrase words>` → a line starting `/…/x.pdf p2`; trashing the PDF removes its embeddings; a newer version of the PDF re-embeds; a vault switch of provider re-embeds PDFs too. Core: a fake index returning a PDF hit prints `p7` and the page text start; `--words` doesn't list PDFs.
- [ ] **Steps 2–4.** **Step 5: Commit** — "Let semsearch find pages of PDFs"

---

### Task 9: Metrics and the dashboard row

**Files:**
- Modify: `src/Kumbu.Core/Attachments/IAttachmentPixels.cs` (`AttachmentUse` records), `Img.cs`, `PdfCommand.cs` (call `Count`), `src/Kumbu.Server/Metrics/KumbuMetrics.cs`, `src/Kumbu.Server/Attachments/AttachmentPixels.cs`, `PdfAnalysisRunner.cs`, `PdfSidecar.cs`, `monitoring/grafana/dashboards/kumbu.json` (row "Attachments"), the label guard's allowed sets
- Test: `tests/Kumbu.Server.Tests/Metrics/AttachmentMetricsTests.cs`; the existing `LabelGuardTests` and `DashboardTests` cover the rest

**Interfaces (metric names; label values from enums only):**
- Analysis, per analysed page: `kumbu_pdf_pages_analysed_total{type}`; histograms `kumbu_pdf_page_text_coverage_ratio`, `kumbu_pdf_page_image_coverage_ratio`, `kumbu_pdf_page_vector_coverage_ratio` (buckets 0, .01, .05, .1, .15, .2, .25, .3, .4, .5, .6, .7, .85, .95, 1), `kumbu_pdf_page_chars` (0, 50, 200, 500, 1000, 2000, 4000, 8000); per PDF `kumbu_pdf_pages_per_document` (1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000), `kumbu_pdf_analysis_duration_seconds` (.1 … 30), `kumbu_pdf_analysis_failures_total{reason}` (`timeout`, `unavailable`, `invalid`, `failed`).
- Use: `kumbu_pdf_calls_total{mode}` (`info`, `text`, `images`, `images_all`, `crop`), `kumbu_pdf_pages_delivered_total{as}` (`text`, `image`), `kumbu_pdf_pages_rendered_total{type}` (`text`, `mixed`, `image`, `unknown`), `kumbu_pdf_text_pages_rendered_total{via}` (`images_all`, `crop`) — the 25 % threshold's miss signal, `kumbu_img_calls_total{step}`, `kumbu_img_crops_total`, `kumbu_img_refusals_total{reason}` (`note`, `pdf`, `not_found`, `too_large`, `bad_crop`, `bad_size`, `unavailable`), `kumbu_render_duration_seconds{kind}` (`page`, `image`), `kumbu_render_failures_total{kind,reason}`, `kumbu_image_delivered_bytes{kind}` (51200, 102400, 153600, 204800, 409600, 614400, 1048576, 2097152), `kumbu_image_delivered_long_edge_pixels{kind}` (256, 512, 768, 1024, 1568, 2048, 2576).
- Dashboard row "Attachments": page types per minute, coverage heatmaps or p50/p90 of the three coverages, pages per PDF, `pdf` calls by mode, text pages rendered (the threshold signal, with a description saying what it means), `img` by step, render p95 by kind, delivered bytes p50/p95, analysis failures.
- [ ] **Step 1: Tests** — after an `img` and a `pdf --images` through MCP (fake sidecar), the series above rose with the right labels; `LabelGuardTests` and `DashboardTests` pass with the new allowed sets and panels.
- [ ] **Steps 2–4.** **Step 5: Commit** — "Measure how PDFs look and how agents read them"

---

### Task 10: Compose, images, CI and documents

**Files:**
- Modify: `compose.yaml` (service `pdf`; network `pdf` marked `internal: true` shared by `kumbu` and `pdf` only; `kumbu` also stays on `default`; `Kumbu__Pdf__Url: http://pdf:8090`), `compose.build.yaml` (`pdf: build: pdf`), `compose.traefik.yaml` only if `docker compose config` shows it needs it, `.github/workflows/docker.yml` (a second image `ghcr.io/scai-ngo/kumbukumbu-pdf` from `pdf/`, tagged and signed like the main one — a matrix or a second job), `.github/workflows/ci.yml` (a job: setup Node 22, pnpm, `pnpm -C pdf install --frozen-lockfile`, `pnpm -C pdf test`, `pnpm -C pdf build`), `.github/dependabot.yml` (npm `/pdf`, docker `/pdf`), `README.md` (the sidecar service, `KUMBU_PDF_IMAGE`, what `img`/`pdf` do, the measurement idea), `.env.example`, `docs/concept.md` §12 (the revised PDF decision: scans yield no text but their pages can be seen; the shared pipeline; the sidecar; images decoded there too; final `img`/`pdf` shape), `docs/security-architecture.md` (§7: the sidecar, the rule "untrusted PDFs are never parsed, and untrusted images never decoded, in the process that holds vault keys", its network, limits and hardening, the accepted limit "a compromised sidecar sees the PDFs and images sent to it while compromised", the analysis sealed per blob and its lifecycle in §5; file:line references), `THIRD-PARTY-NOTICES.md` (pdf.js Apache-2.0, @napi-rs/canvas MIT, Skia BSD-3 via it)
- Service `pdf` (exact): `image: ${KUMBU_PDF_IMAGE:-ghcr.io/scai-ngo/kumbukumbu-pdf:latest}`, `networks: [pdf]`, `read_only: true`, `tmpfs: ["/tmp:size=64m"]`, `cap_drop: [ALL]`, `security_opt: ["no-new-privileges:true"]`, `user: "1000:1000"`, `mem_limit: 1g`, `cpus: 1.5`, `pids_limit: 128`, `restart: unless-stopped`; `kumbu` gets `depends_on: pdf: condition: service_started` only if that doesn't make the server unusable when the sidecar is down (it must not: prefer no hard dependency).
- [ ] **Step 1:** `docker compose config -q` for `compose.yaml`, with `compose.build.yaml`, with `compose.traefik.yaml` (with `KUMBU_HOST` set), with `compose.monitoring.yaml` (with the Grafana password) — all valid.
- [ ] **Step 2:** From inside the `pdf` network the sidecar has no route out: `docker compose -p kumbu-a2 run --rm --entrypoint node pdf -e "fetch('https://example.org').then(()=>process.exit(1),()=>process.exit(0))"` exits 0.
- [ ] **Step 3:** Docs; `git grep -n -i` for organisation names over the diff is empty.
- [ ] **Step 4: Commit** — "Run the PDF sidecar in compose, build and sign its image, and document it"

---

### After the tasks (the lead)

Full verification (server and core suites, web install/test/build, sidecar test/build), hands-on through MCP with the stack (`docker compose -p kumbu-attachments …`): `img` at each step and a crop; a generated paper-like PDF with two charts through `pdf info`, text, `--images` and a crop of one chart; a scanned-page PDF; note `docs/notes-attachments-model-hands-on.md` with Chris's checklist (client test of `docs/attachments-client-research.md` §6: the six-digit code per client and the size steps). Whole-diff review, fixes, draft PR (from `monitoring`; says so).
