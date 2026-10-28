# Attachments, part 2: the model reads attachments — `img` and `pdf` (design)

Date: 2026-10-28. Brainstormed with Chris (input: a design chat of the same day, "PDF Attachments: Brainstorm Input"); built overnight (brief: `docs/superpowers/plans/2026-10-28-overnight.md`). Part of the omnibus release 0.2.5. Builds on part 1 (`2026-10-27-attachments-foundation-design.md`: storage, upload, sync, gallery, lightbox) and on monitoring (`2026-10-28-monitoring-design.md`). Part 1 is built (#81, #84); plan this part against its code, not its plan. Replaces the two parallel packages part 1 announced ("PDF" and "images for the model"): they share so much that they are one package with two commands.

## Why

Markdown guarantees "always text"; an attachment can't. A clean PDF with a text layer is the best case, not the normal one. Chris: "I'm with garbage in, garbage out, but a clean text PDF really is a best-case scenario." Two kinds of PDF matter from the start:

- **Scanned pages:** no text layer, the page is a picture.
- **PDFs with diagrams:** a research paper with text, two charts side by side, more text. Chris's partner is a scientist; pulling information out of a chart is the point. As a developer, Chris showed Claude a Hetzner network graph as a screenshot to ask what the load was. Diagrams carry information, and the model's image comprehension can read it.

So the model must be able to *look at* a page, not only read its text layer. The server still interprets nothing: it shows, the model on the client side understands (as with `img`).

This revises the decision of Oct 27 in `docs/concept.md` §12 ("Scanned PDFs without a text layer yield no text in v2.5, and the app says so"): they still yield no text, but their pages can be seen. Part 1's non-goal "server-side PDF rendering" was for part 1; this part adds it, isolated (below). Update §12 accordingly.

## Guiding decisions (Chris)

- **Garbage in, garbage out**, but get close to the common cases. No attempt to rescue every bad scan.
- **Simple first, learn from real use.** v2.5 is also a measuring instrument: what agents ask for tells what to build next. Hence the metrics (below) and monitoring before this part.
- **A PDF is always a list of pages, and every page has a known type.** The ambiguity becomes a data type.
- **No AI on the server** (unchanged from Oct 27): no OCR, no captioning, no layout analysis beyond the deterministic measures below.
- **Text by default, pixels on request.** Images stay in a model's context and are re-sent on every later turn; they are never spent unasked.

## Goals

1. `img`: show an image attachment to the model (concept §12, decided Oct 27; unchanged except where this document is more precise).
2. `pdf`: read a PDF page by page, as text by default, with rendered pages where they are needed, and zoom into a region.
3. One pixel pipeline for both: size steps, pixel budget, hard long edge, crop, limits, answer format.
4. Per-page analysis on upload: text layer, page type, measurements; stored sealed with the attachment.
5. `rg` and `semsearch` find text inside PDFs.
6. All PDF parsing happens in an isolated sidecar container, never in the process that holds vault keys.
7. Metrics that answer the open calibration questions.

## Non-goals

- OCR, image captions, semantic page types (`diagram`, `photo`, `table`), transcriptions written back by agents (all ideas for later: "attachments deluxe").
- Finding figures as separate objects on a page (layout analysis). The page is the unit; a region is reached by crop.
- A cache of rendered pages (decided by Chris): pages are rendered fresh on every request. Revisit only if the metrics show a need.
- De-duplication across vaults or users by content hash (part 1 de-duplicates within a vault only; this part adds nothing across).
- Agents uploading attachments (upload ticket flow); the model reads, people upload.
- Embedded PDF blobs or `resource_link` as the way to the model (research §5: no portable path).
- `img`/`pdf` output piped into other commands.

## Anti-goals

- An untrusted PDF parsed in the server process (it holds unlocked vault keys).
- Images sent to the model without being asked for.
- A page the model is told is "text" while it holds a diagram, with no hint that something was seen there.
- Derived data (page text, measurements) surviving the attachment's deletion, or stored unsealed.
- A metric that names a user, vault, file or content (monitoring design, anti-goals).

## The pixel pipeline (shared by `img` and `pdf`)

- **Size steps** `small`, `medium` (default), `large`: each a pixel budget and a hard long edge. Starting values *(my call, from the research §3; constants in one place, printed by `help img` and `help pdf`)*:

  | Step | Long edge | Pixel budget | ≈ Claude visual tokens |
  |---|---|---|---|
  | `small` | 768 px | 0.4 MP | ~500 |
  | `medium` | 1568 px | 1.2 MP | ~1,550 (standard cap) |
  | `large` | 2576 px | 3.7 MP | ~4,750 (high-res tier) |

  Byte targets per step under "Encoding" below.

  The size test of research §6 (50 KB / 200 KB / 1 MB per client) has not been run yet; it may move these numbers or add a byte target per step. Keep that easy.
- An image is scaled down to fit both limits, keeping its aspect ratio; never enlarged, never refused for size.
- **Crop** `--crop x,y,w,h` at full resolution: for an image in the original's pixel coordinates (EXIF-oriented, as part 1 reports them); for a PDF page in **PDF points of the page as displayed** (rotation applied, the CropBox; 1 pt = 1/72 in) *(my call: stable whatever the step, and the analysis reports regions in the same units)*. A PDF region is **rendered anew** at the step's size, so vector diagrams stay sharp; the render scale is capped *(my call: at 600 dpi equivalent)* so a tiny region isn't blown up without end.
- **Answer format**, for every image delivered: an MCP `image` block, and a text block naming the file, its version token, the page (PDF), the original size (pixels, or points for a page), the step, the delivered size, the crop if any, and a line for the model: if no image arrived, the client dropped it (ChatGPT's connectors do, research §2.6), say so to the user rather than guess. Very wide or tall images get a hint to crop.
- **Encoding** *(my call)*: each step has a provisional **byte target**: `small` 150 KB, `medium` 400 KB, `large` 1 MB (constants next to the step table, revisited after the size test). PNG when the PNG fits the target (crisp text and line art on rendered pages and screenshots), else JPEG at quality 85, lowered in steps to 60 until it fits; if it still doesn't fit, it is sent as it is and the text block says its size. The sidecar applies the same rule to rendered pages, given the target with the request.
- **Limits** (anti-bomb): `img` keeps part 1's header check (50 MP, 16,384 px) before decoding and caps decoder memory and time (ImageSharp configuration). The sidecar's output is checked by magic bytes and header like any untrusted image, but not decoded again.
- **Library:** ImageSharp for images (concept §12). PDF pages come out of the sidecar already at the target size.
- **One `img`/`pdf` call delivers at most 4 images** *(my call: Hermes takes at most 4 per result; Claude Code caps a result at 25,000 tokens)*. Text pages don't count.
- `ShellResult` today carries text only (`src/Kumbu.Core/Sh/ShellResult.cs`, `src/Kumbu.Server/Mcp/ToolResult.cs`). It gains an ordered list of parts (text and image) so `ToolResult` emits mixed content in order. A command producing images refuses to be part of a pipeline, with a clear message.

## `img`

As concept §12, with the pipeline above:

```sh
img /Screenshots/layout_editor_bad1.png
img /Screenshots/layout_editor_bad1.png --size large
img /Screenshots/layout_editor_bad1.png --crop 0,1200,1440,800
```

`img` on a PDF refuses and points to `pdf … --images` *(my call: one command per thing to look at, no aliases)*.

## Page analysis (on upload, deterministic)

For each page, the sidecar measures (with pdf.js: `getTextContent()` with positions, `getOperatorList()` for images and paths, the viewport for size and rotation):

- the text layer, in reading order as pdf.js gives it, and its character count;
- the area covered by text items;
- embedded raster images: count, positions and area;
- vector drawing: clusters of path operations, with their bounding boxes and area; thin lines (rules, table borders) don't count as figures *(how to cluster and what counts as thin is the builder's call, documented with reasons)*;
- the page size in points and its rotation.

**Page types** (decided with Chris; thresholds are starting values to calibrate from the metrics):

| Type | Rule | Example |
|---|---|---|
| `image` | one image covers ≥ 85 % of the page, with or without a text layer *(85 % is my call)* | a scan (also one with an invisible OCR layer), a photo page |
| `mixed` | images plus vector figures cover ≥ 25 % of the page *(Chris: below that it's logos and the like)* | a paper page with two charts |
| `text` | otherwise, including an empty page | body text, a simple table |

Figure regions (images and vector clusters, in points) are part of the analysis, so `pdf info` can offer them as crop targets.

### Storage

- The analysis belongs to the PDF's **blob** (part 1: one sealed blob per distinct content within a vault), sealed with the vault key like everything else, with the analyser's version (the pdf.js version plus a number of our own for the rules). Every record and version that references the blob shares it; it is deleted with the blob, so deletion (GDPR included), trash expiry and re-keying cover it with no extra cascade. Re-keying re-seals it.
- Stored so a 15 MB, several-hundred-page PDF doesn't break the 16 MB document limit (e.g. pages in chunks; builder's call). PDFs over a page limit are analysed up to that limit and say so *(my call: 2,000 pages)*.
- **When it runs:** the analysis needs the vault key, which the server holds only while the vault is unlocked. An upload or sync of a PDF enqueues it (durably, in MongoDB, without content); it runs while the vault is unlocked and resumes after a restart or on the next unlock. **A `pdf` call on a PDF whose analysis is still pending runs it right there** *(my call)*, within the job's time limit, and stores it; only if that fails or times out does `pdf` answer "not analysed yet" for text and types, with `--images=all` still rendering (rendering doesn't need the analysis), and `--images` saying it can't choose pages without the analysis. A newer analyser version re-analyses lazily the same way.
- **Not in the working set** *(my call)*: page text stays out of the unlocked vault's in-memory working set (part 1's anti-goal, and a vault with many long PDFs would weigh a lot). The record carries only the page count and the type summary; page text and measurements are loaded and unsealed on demand, per PDF. `rg` therefore reads PDFs through an async source next to the notes it reads today (`src/Kumbu.Core/Sh/Commands/Rg.cs`), one PDF at a time; its duration is a metric, so slowness shows. `semsearch` keeps page embeddings with the vault's other embeddings, as sections' are, and loads a page's text only for the hits it shows.
- If the sidecar is down: stored text keeps working; rendering and pending analyses fail with a clear message ("PDF rendering is unavailable right now") and are retried for analysis.

## `pdf`

Names provisional; the builder may adjust them to the shell's conventions (`src/Kumbu.Core/Sh/Commands/`), keeping the behaviour.

```sh
pdf info /Papers/smith-2025.pdf                  # the manifest
pdf /Papers/smith-2025.pdf 5-9                   # text only, page by page
pdf /Papers/smith-2025.pdf 1-20 --images         # text, plus a rendered page where the type is mixed or image
pdf /Papers/smith-2025.pdf 4 --images=all        # render every page in the range, whatever its type
pdf /Papers/smith-2025.pdf 4 --crop 72,300,451,260 --size large
```

- **`pdf info`:** page count, metadata (title, author if present), page types summarised in runs (`1-3 text, 4 mixed, 5-12 text, 13 image`), and per non-text page its figure regions in points. A cheap first call before anything heavy. Long documents stay within the shell's output limit (`src/Kumbu.Core/Sh/OutputLimit.cs`) by summarising.
- **Text (default):** each page starts with a header line naming page, type and what was detected, e.g. `--- Page 4 of 12: mixed (text; vector drawing ≈ 35 % of the page) — add --images to see it ---`, `--- Page 7 of 23: image (no text layer; one image covers the page) — add --images to see it ---`, `--- Page 2 of 9: text (1 small image ≈ 6 %) ---`. The hint is the nudge: the model decides, but knows what it would miss. Scanned pages are **not** rendered automatically *(my call: predictable, never spends tokens unasked)*.
- **`--images`:** "as an image where it is needed or useful" (Chris): pages of type `mixed` and `image` come as text block plus rendered page, `text` pages as text only. **`--images=all`** renders every page of the range. At most 4 rendered pages per call; a range with more stops after the fourth with a line saying where to continue. Text-only ranges have a larger page limit *(builder's call, within the output limit)*.
- **`--crop`** only with a single page; implies a rendered image.
- **`--size`** as for `img`.
- `cat` on a PDF (part 1's one line) gains the page count and type summary and points to `pdf`.
- `help pdf` (part 1 adds the stub) explains all of it with the exact numbers; `help img` and `help pdf` refer to each other.

## Search

- **`rg`** searches the stored text layers of PDFs; a match shows the page instead of a line number, e.g. `/Papers/smith-2025.pdf:p7: …` *(format my call)*. `rg` options that make no sense for a page (e.g. line context) behave sensibly and are described in `help rg`.
- **`semsearch`**: each page's text is embedded as a section of the PDF (the existing section mechanism, `src/Kumbu.Server/Search/SearchIndex.cs`), results name the page. This is the Oct 27 decision "text per page and embeddings".
- Pages without text (scans) are not found by search; `help pdf` says so.
- Part 1's "`rg` and search skip attachments" changes to "skip images; search PDF text". Update `help`.

## The PDF sidecar (decided with Chris)

All PDF parsing (analysis and rendering) runs in its own container. The one-line rule for `docs/security-architecture.md`: **untrusted PDFs are never parsed in the process that holds vault keys.**

- **Engine: pdf.js** in Node (`pdfjs-dist`, the legacy build for Node, Apache 2.0) with `@napi-rs/canvas` for drawing. Chosen because the parser is JavaScript (memory-safe), only drawing is native (Skia), it is the engine the web app's lightbox already uses (people and the model see the same rendering), and the hardening is the same (`isEvalSupported: false`, no scripting). PdfPig and PDFium were considered and rejected: two parsers would disagree on page boxes and rotation, and PDFium is native throughout. Precedent for the pattern: Dangerzone renders untrusted documents to pixels in a sandboxed container.
- **In this repository**, its own folder (e.g. `pdf/`), pnpm, its own `Dockerfile`; built and signed with cosign in `docker.yml` as a second image (`ghcr.io/scai-ngo/kumbukumbu-pdf`), tagged like the main image; Dependabot for its npm packages; CI runs its tests.
- **Stateless and small interface** on the compose network only: "analyse these bytes" (returns the analysis) and "render page n at this size, optionally this region" (returns PNG or JPEG). The server sends decrypted bytes per request; the sidecar keeps nothing.
- **Hardened in `compose.yaml`:** a network of its own shared only with `kumbu` and marked `internal` (no way out), `read_only`, a small `tmpfs`, `cap_drop: [ALL]`, `no-new-privileges`, a non-root user, memory, CPU and pids limits, `restart: unless-stopped`. Each job runs in a worker that is killed on a time limit (e.g. 30 s analysis, 10 s render; builder's call), so one hostile PDF can't stall the rest. Concurrency is bounded.
- No authentication between server and sidecar *(my call)*: the network has exactly these two members. Documented as an accepted limit: a compromised sidecar sees the PDFs sent to it while compromised.
- The compose file stays production-ready for the Hetzner deployment; `README.md` and `.env.example` describe any new variables.

## Metrics (the measuring instrument)

Following the monitoring design (no labels from user input):

- **Page structure**, per analysed page: histograms of text coverage, image coverage, vector-figure coverage, characters per page; counter by page type; histogram of pages per PDF; analysis duration and failures.
- **Use:** `pdf` calls by mode (`info`, text, `--images`, `--images=all`, `--crop`), pages delivered as text and as image, rendered pages by type, **renders of pages classified `text`** (via `--images=all` or `--crop`: the signal that the 25 % threshold misses figures), render duration and failures, bytes delivered per image.
- `img` calls by step, crops, delivered sizes; refusals by reason.
- A dashboard row "Attachments" with these.

## Docs to update

`docs/concept.md` §12 (the revised PDF decision, the shared pipeline, the sidecar, the final `img`/`pdf` shape), `docs/security-architecture.md` (the sidecar, its network and limits, the rule above, the accepted limit), `README.md` (the sidecar service, variables), `help`.

## Testing

- Sidecar (Vitest or Node's test runner): classification on generated fixtures (a text page, a page with a vector chart covering ~35 %, a page with a small logo, a full-page scan image with and without an invisible text layer, a rotated page, an empty page), region boxes in points, rendering a page and a region at a requested size, the time limit killing a job (a fixture that loops or a stubbed slow job), refusing a non-PDF.
- Server (xUnit, sidecar faked): `img` steps, crop and the text block; `pdf info`, text with headers, `--images` selecting by type, `--images=all`, the 4-image limit and the continuation line, `--crop` with one page only, "not analysed yet", sidecar down; analysis queued and resumed after unlock; analysis deleted with the blob and re-sealed on re-keying; `rg` page matches; semsearch results naming pages; images refusing pipelines; metric labels from fixed sets.
- Fixtures are generated in the tests or small files committed under `tests/`, with no real documents and nothing that names an organisation.
- Hands-on (`docs/notes-attachments-model-hands-on.md`): with the stack running, as an agent through MCP: `img` at each step and a crop; a generated paper-like PDF with two charts read with `pdf info`, text, `--images` and a crop of one chart; a scanned-page PDF. Chris runs the client test of research §6 (six-digit code, per client) afterwards; leave him a short checklist in the note.

## Left to the builder

Exact command and flag spelling within the shell's conventions, the sidecar's API shape, how analysis is chunked in storage, the clustering of vector paths, the time and concurrency limits, the text page limit per call, the encoding details, metric names and buckets. Each with a line of reason in the plan or the hands-on note.
