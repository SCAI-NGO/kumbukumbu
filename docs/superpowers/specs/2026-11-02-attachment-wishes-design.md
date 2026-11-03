# Attachment wishes for 0.3.5: `--rotate`, `--frame`, where matches lie, regions with labels (design)

Status: **design**, 2026-11-02, for review by the maintainer, then an unattended run on `feature/attachment-wishes`. Extends [the model-reading design](2026-10-28-attachments-model-reading-design.md), whose guiding decisions and anti-goals stay in force. Source: the wishes clients voiced in [the second client round](../../attachments-client-research.md) (§6.2, line 279). Line numbers refer to the tree this was written against, the branch's start.

No version bump and no tag: that stays with the maintainer. Four `ASSUMPTION:` marks below need a look; everything else is decided.

## Why

Five clients worked through images and PDFs on 2026-11-01, and four things got in their way:

1. Every client read the sheet photographed sideways, all called it awkward, and only the one with a sandbox could turn it.
2. A GIF arrives as its first frame, and nothing can reach the others.
3. `rg` finds a word on a page but not where on the page, so a look at it costs a whole page or a guess.
4. The regions of `pdf info` stop at the figure's drawing, and leave out its axis labels and legend when those are text (`src/Kumbu.Core/Sh/Commands/PdfCommand.cs:57-58`).

Items 1 to 3 are plain work. Item 4 is a heuristic that may not be good enough. The maintainer decided: **the run tries it and reports honestly whether it is reliable enough; if it isn't, it is left out and the rest still ships.** §4.5 states the measure.

## Non-goals

- **Marking text that isn't visible on the page** (the untested idea of §6.2). A region from `rg --regions` says where a text box lies, not whether anyone can see it.
- **Naming what a region shows** ("pie chart"): a non-goal of the design this extends.
- **Anything needing image recognition on the server**, so also: finding out by itself that a page is sideways, straightening a photo taken at a slight angle, finding labels drawn into a raster image, OCR.
- Angles other than 90°, 180°, 270°; remembering a turn for a file; a turn or a frame in the web app's lightbox.
- Several frames in one call, a contact sheet of frames, animated WebP or PNG (they stay a still, as today).
- Regions in `semsearch`, or for notes.
- A cache of pixels (still decided against, see §6).

## 1. `--rotate`

### 1.1 Syntax

```sh
img <path> --rotate 90|180|270
pdf <path> [pages] --images --rotate 90|180|270
pdf <path> [pages] --images=all --rotate 90|180|270
pdf <path> N --crop x,y,w,h --rotate 90|180|270
```

- Degrees **clockwise**, as a person turns a sheet; only these three values. `--rotate 0` is refused: it would be a state with nothing to say.
- `--rotate` goes with `--size` and `--crop`, in any order on the line. Given twice, the last one wins, as for every option (`src/Kumbu.Core/Sh/Commands/Options.cs:18-19`).
- On a range, every page shown as an image turns by the same amount. One call has one rotation. A page that needs a different one takes its own call.
- For `pdf`, it needs a call that delivers pictures. It doesn't change a page's text.
- `pdf info --rotate` is refused by the existing sentence (`PdfCommand.cs:111-114`, extended with `parsed.Has("rotate")`).
- The "Continue with" line carries it on (`PdfCommand.cs:454-463`): `Continue with: pdf /Work/a.pdf 5-20 --images=all --rotate 90`.

### 1.2 Order and coordinates

The order is the same for every combination: **original → camera's rotation (EXIF) or the page's own `/Rotate` → `--rotate` → `--crop` → size step**.

- `--rotate` adds to what is already applied: a JPEG's EXIF orientation (`pdf/src/image.ts:3-5`) and a PDF page's rotation (`pdf/src/render.ts:21`). `cat`, `pdf info` and `rg --regions` keep reporting the file without `--rotate`, so what they report doesn't depend on any flag.
- **Size steps:** the long edge and the pixel budget don't care about direction (`pdf/src/steps.ts:12-14`), so a turned picture has the same size with its sides swapped, or the same size after 180°. For a crop, the step applies to the crop's own `w` and `h`, in the turned frame.
- `PictureText.cs:38` compares a picture's width with the original's: with no crop and a turn of 90° or 270° it must use the original's **height**, or the "Reduced to … %" hint is wrong.

ASSUMPTION: with `--rotate`, `--crop` counts **in the turned picture**: x and y from the top left of what the client is looking at, in pixels for an image and in points for a page. Recommended, because in the client round crops worked out from the picture hit "on the first try in most cases" (§6.2), and a sideways sheet is the case where a client looks at the turned picture and then wants a part of it. The price: a region from `pdf info` or `rg --regions` belongs to the page not turned, so combining one with `--rotate` needs a conversion, which `help pdf` gives in one line (§1.5). Alternative: `--crop` always in the unturned frame, so regions combine freely and each file has one coordinate system, but every crop read off a turned picture then needs the conversion, for each model, on the sheet that was already awkward.

### 1.3 Implementation

- **`pdf/src/render.ts:21-28`:** the viewport takes the turn itself: `shown = p.getViewport({ scale: 1, rotation: (p.rotate + turn) % 360 })` and the same `rotation` in the scaled viewport, whose offsets then count in the turned frame. Nothing is converted, and drawings are drawn anew, so they stay sharp. `pageWidth` and `pageHeight` stay those of the page without `--rotate`, from a viewport of its own.
- **`pdf/src/image.ts:31-40`:** `inside` and `fit` work with the turned size; the context's transform does the turn. 90° turns at scale 1 must be exact to the pixel (tested). `originalWidth` and `originalHeight` stay the picture before `--rotate`.
- **`pdf/src/server.ts:87-100`, `pool.ts:12-15`, `worker.ts:23`:** a `rotate` query parameter taken only from the set 90, 180, 270 (`bad_request` otherwise), passed along in the job.
- **The answer names what it did.** `:latest` of the two images doesn't move atomically (`docs/notes-attachments-model-hands-on.md`), and an older sidecar ignores a parameter it doesn't know, so the server would call an unturned picture turned. The sidecar answers `X-Kumbu-Rotate: 90`; `PdfSidecar.PictureAsync` (`src/Kumbu.Server/Attachments/PdfSidecar.cs:212-234`) throws `Invalid("The image isn't turned as asked")` if it differs from the request, absent counting as 0. The model then gets the existing "couldn't be rendered".
- **`src/Kumbu.Core/Attachments/IAttachmentPixels.cs:63,66`**, `IPdfSidecar` (`PdfSidecar.cs:21,24`) and `Query` (`:113-115`) gain the rotation, as a new `int rotate` validated by one parser in `src/Kumbu.Core/Attachments/`. The four `FakePixels` in `tests/Kumbu.Core.Tests/Sh/` and `FakePdfSidecar` follow.
- **The check before the round trip** (`src/Kumbu.Core/Sh/Commands/Img.cs:75-79`, `PdfCommand.cs:470-473`) compares with the turned size.

### 1.4 Outputs and errors

The text block (`src/Kumbu.Core/Sh/PictureText.cs:49-60`) gains three things when `--rotate` is given: `, turned 90° clockwise to 842×595 pt` after the original size, `of the turned page` or `of the turned image` after the crop, and a line above the "If no image follows" one:

```text
With --rotate 90, --crop counts from the top left of this turned page, in its points.
With --rotate 90, --crop counts from the top left of this turned image, in its pixels.
```

| Situation | Sentence |
|---|---|
| Value not allowed | `img: --rotate turns clockwise by 90, 180 or 270 degrees, not '45' (leave it out for no turn)`, likewise `pdf:` |
| `pdf` without pictures | `pdf: --rotate turns the pictures of a page; add --images, --images=all or --crop` |
| Crop outside, image | `img: the crop 0,2500,800,600 lies outside the image as turned by --rotate 90 (3000×2000 px)` |
| Crop outside, page | `pdf: the crop 700,500,300,200 lies outside page 7 as turned by --rotate 90 (842×595 pt)` |

The value is checked before the path is opened, like `--size` (`Img.cs:52-56`). The sidecar's `bad_crop` remains the backstop for what can't be checked first, with today's sentence (`AttachmentPixels.cs:116`).

### 1.5 Help

`help img` (`Img.cs:15,20`, and after `--crop` at `:29`):

```text
img <path> [--size small|medium|large] [--rotate 90|180|270] [--crop x,y,w,h] [--frame N]
  --rotate D        turn the picture clockwise by 90, 180 or 270 degrees, on top of the camera's rotation,
                    for a sheet lying on its side. With it, --crop counts in the turned picture, from its
                    top left.
```

`help pdf` (`PdfCommand.cs:31,36`, and after `--crop` at `:58`):

```text
  --rotate D        turn each page shown as an image clockwise by 90, 180 or 270 degrees, for a sheet lying
                    on its side; needs --images, --images=all or --crop, and leaves the text as it is. With
                    it, --crop counts in the turned page, from its top left. Regions from 'pdf info' and
                    'rg --regions' are for the page not turned: x,y,w,h on a page W×H, with W and H rounded
                    down, is H-y-h,x,h,w when turned by 90, W-x-w,H-y-h,w,h by 180, y,W-x-w,h,w by 270.
```

The conversions are checked against the sidecar's rendering in a test (§8), so the help can't go wrong unnoticed. W and H are rounded down because an A4 page prints as 842 pt but measures 841.89: rounded to nearest, a converted region with y = 0 would pass the page's edge by 0.11 pt and be refused.

### 1.6 Worked example

A sheet photographed sideways, page 7 of a PDF. A first look without `--rotate` showed it lying on its side, so:

```text
> pdf /Work/Scans/mix.pdf 7 --images=all --rotate 90
--- Page 7 of 15: image (no text layer; one image covers the page) ---
/Work/Scans/mix.pdf (version 6f3a), page 7 of 15 (image): page 595×842 pt, turned 90° clockwise to 842×595 pt; medium: 1303×920 px JPEG, 318 KB
With --rotate 90, --crop counts from the top left of this turned page, in its points.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the sheet, upright]
```

The total stands at about 1000,760 in the picture, 250×100 px. One pixel is 842/1303 of a point, so in points that is 646,491,162,65:

```text
> pdf /Work/Scans/mix.pdf 7 --rotate 90 --crop 646,491,162,65 --size large
/Work/Scans/mix.pdf (version 6f3a), page 7 of 15 (image): page 595×842 pt, turned 90° clockwise to 842×595 pt; crop 646,491,162,65 of the turned page; large: 1350×541 px PNG, 88 KB
With --rotate 90, --crop counts from the top left of this turned page, in its points.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the total, upright]
```

The same for an image, 3000×2000 px as `cat` reports it:

```text
> img /Work/Scans/sheet.jpg --rotate 90
/Work/Scans/sheet.jpg (version 21d0): image (JPEG), original 3000×2000 px, turned 90° clockwise to 2000×3000 px; medium: 894×1341 px JPEG, 239 KB
Reduced to 45 %: small text and digits may be misread; look at a part with --crop x,y,w,h before quoting them.
With --rotate 90, --crop counts from the top left of this turned image, in its pixels.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image]
```

## 2. `--frame N`

### 2.1 Syntax and order

```sh
img <path> --frame 2
img <path> --frame 2 --rotate 90 --crop 0,0,300,200 --size small
```

- `img` only, GIFs only, one whole number from 1: frames count from 1, as pages do.
- **A frame as it looks when played:** frames 1 to N composed in order on a transparent canvas, with each frame's disposal, not frame N's raw rectangle (most GIFs store only what changed). Without this, a frame is often a fragment on nothing.
- The order of §1.2 starts one step earlier: **frame → `--rotate` → `--crop` → size step**. Transparency goes on white after that, as for any image (`pdf/src/image.ts:41-42`).
- Without `--frame`, the first frame through today's path. `--frame 1` is frame 1 through the composing path; for a sound GIF they look the same.

### 2.2 Implementation

`@napi-rs/canvas` has a GIF encoder but no way to reach a later frame (its `index.d.ts`, 1.0.13; `loadImage` gives the first).

ASSUMPTION: decode with **`omggif`** 1.0.10 (MIT, one file of pure JavaScript, no dependencies, used for `GifReader` only), pinned in `pdf/package.json` like the others and listed in `THIRD-PARTY-NOTICES.md`. Recommended: `GifReader` already lists frames with offset, disposal and transparency, and `decodeAndBlitFrameRGBA` composes onto a buffer. It is old and unchanged for years, which is tolerable because it runs only in the sidecar, in a killable worker, on one file. Alternative: our own decoder of about 150 lines (LZW, block walk, interlace) in `pdf/src/`, with no new dependency but a parser to own.

New in `pdf/src/image.ts`:

- `frames(bytes)`: the count from `new GifReader(bytes).numFrames()`, for every GIF, with or without `--frame`. If the reader throws on a file Skia can still draw, the count is unknown and nothing fails.
- `gifFrameCount(bytes)`: the frames counted by walking the GIF's blocks, before `GifReader` is made, allocating nothing per frame and stopping past `MAX_FRAMES`, 10,000. Over it, `frames` gives no count (today's sentence), and `--frame` is `too_large`; blocks that run past the end give no count either.
- `composeFrame(reader, n)`: one RGBA buffer, and a second copy only for a frame with disposal 3, wrapped without copying (`new ImageData`) and drawn with `putImageData` into a canvas that takes the place of `image` from `:31`. A file-level `not_image` if the reader throws.

**Limits.** The header check (`image.ts:17-22`) allows 50 MP, which is too much here: a buffer, a copy for disposal 3, the index stream, an `ImageData` and a canvas would come to some 850 MB in one worker, and two workers plus the bodies in memory pass `mem_limit: 1536m` (`compose.yaml:132-136`) and kill the sidecar. So the composing path has a cap of its own, at first 16 MP (`too_large`; now 4 MP, below), decodes nothing with `loadImage`, keeps one copy buffer for all frames and wraps its buffer in `ImageData` without copying. That leaves, by estimate, about 255 MB a worker without a copy and 320 MB with one, and about 1.3 to 1.45 GiB for the whole sidecar at its worst: close to the limit, so the run measures it, and lowers the cap if it doesn't hold. Measured in the hands-on run (`docs/notes-attachment-wishes-hands-on.md`), it didn't hold, and not for the canvas: 16 MiB GIFs of some 600,000 tiny frames took the sidecar past its limit at 16, 12, 8 and 4 MP alike, because `omggif` lists every frame as an object when it opens a file and composing makes garbage for every frame before the last. So the composing path also takes at most **10,000 frames**, counted without `omggif`; a plain `img` of a GIF with more is drawn by Skia as before, with its count unknown. With the frames bounded, the canvas still didn't hold under sustained load: two workers composing back to back with 16 MiB bodies queued passed the limit within one or two minutes at 16 MP, and at 8 MP peaked 2 MiB under it. So the cap is **4 MP** (2000 × 2000, more than a 2560 × 1440 screen recording), measured in the same run. A plain `img` of a GIF stays on today's path. Two more gaps have to be closed, since `omggif` checks neither:

- `decodeAndBlitFrameRGBA` allocates `width × height` of the **frame's own** rectangle, up to 65,535 × 65,535 from a few bytes of file. A frame not inside the canvas is refused with `not_image` and a sentence of its own, `A frame of the GIF lies outside its canvas.`, before it is decoded.
- Composing costs time with N. The worker's 10 s (`server.ts:17`) kills what takes too long, so no extra limit; `AttachmentPixels.Refused` (`AttachmentPixels.cs:112`) says for a call with a frame `finding frame N of <path> took too long`, as a smaller step wouldn't help.

`omggif` calls `console.log` with fixed warnings on a truncated stream, once per decoded frame: about 1.4 million lines from a 16 MiB GIF of tiny frames, against the sidecar's rule that it logs method, route, status and duration only (`server.ts:3-4`). So `console.log` is silenced while frames are decoded in the worker, and restored after.

**Interface:** `/render/image` takes `frame` (`integer`, 1 to 1,000,000) and answers `X-Kumbu-Frame`, checked like `X-Kumbu-Rotate` (§1.3), and `X-Kumbu-Frames` for a GIF whose count is known. `Picture` (`IAttachmentPixels.cs:10`) gains `int? Frames`. A frame past the end is `no_such_frame`, 422, in `server.ts:23-33` and `PdfSidecar.cs:77-89`, reaching `Img` as a new `PixelsProblem.BadFrame`. The server never parses a GIF: it sees only the sidecar's numbers, validated as whole numbers above 0.

### 2.3 Outputs and errors

`PictureText.ForImage` (`PictureText.cs:44`) replaces today's sentence:

| Case | Note line |
|---|---|
| No `--frame`, more than one frame | `An animated GIF of 3 frames, shown as a still: frame 1. --frame N shows another.` |
| No `--frame`, one frame | none |
| No `--frame`, count unknown | today's sentence, `A GIF is shown as a still: if it is animated, only its first frame.` |
| `--frame N` | none; the head says `, frame 2 of 3` (or `, frame 2` with the count unknown) |

| Situation | Sentence |
|---|---|
| Not a whole number from 1 | `img: --frame is one frame, counting from 1, not '1-3'` |
| Not a GIF | `img: --frame is for GIFs; /Work/Screens/shot.png is a PNG` |
| Past the end | `img: /Work/Clips/moving.gif has no frame 7; img without --frame says how many it has` |
| Canvas over 4 MP, or more than 10,000 frames | `img: /Work/Clips/big.gif is too large for --frame (at most 4 megapixels and 10,000 frames); img without --frame shows its first frame` |

`pdf --frame` stays `pdf: unrecognized option '--frame'`. `ImgRefusal` (`IAttachmentPixels.cs:43`) gains `BadRotate` and `BadFrame`, and `KumbuMetrics.Label` (`src/Kumbu.Server/Metrics/KumbuMetrics.cs:439-448`) must name both, or its fallback counts them as `unavailable`.

Past the end names no count on purpose: the sidecar's answer would need a new field through worker, pool and client for a number the first plain `img` already gave.

### 2.4 Help

`help img`, after `--rotate`, and `Img.cs:31` changes to "A GIF is shown as a still, its first frame unless --frame says otherwise.":

```text
  --frame N         a GIF only: frame N, from 1, as it looks when played. One frame a call; a plain img
                    says how many there are.
```

### 2.5 Worked example

The probe `animated.gif`, three frames of 600×400 px:

```text
> img /Work/Clips/animated.gif
/Work/Clips/animated.gif (version 91be): image (GIF), original 600×400 px; medium: 600×400 px PNG, 2 KB
An animated GIF of 3 frames, shown as a still: frame 1. --frame N shows another.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: frame 1]

> img /Work/Clips/animated.gif --frame 2
/Work/Clips/animated.gif (version 91be), frame 2 of 3: image (GIF), original 600×400 px; medium: 600×400 px PNG, 2 KB
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: frame 2]

> img /Work/Clips/animated.gif --frame 7
img: /Work/Clips/animated.gif has no frame 7; img without --frame says how many it has
```

## 3. Where matches lie: `rg --regions`

### 3.1 Syntax

```sh
rg --regions <regex> [path...]
```

A new flag (`src/Kumbu.Core/Sh/Commands/Rg.cs:22-29`), for `rg` over paths only, off by default: it has a cost (§3.3), and `rg` must stay fast and its output unchanged for everyone else. Works with `-i`, `-F`, `-w`, `-n`, `-m`, `-A`, `-B`, `-C`.

Refused, next to the `--tag` check at `Rg.cs:88-91`, since none of them prints a matched line of a PDF: `rg: --regions says where matches lie in PDFs; it doesn't go with -l, -c, -v, --files, --tag or --version, nor right of a pipe`. A pipe to the right of it is fine: the regions are part of the text.

### 3.2 Output

For PDFs only. The region goes **before** the text, so a line of a hostile PDF can't forge one: whatever looks like a region after `: ` is text.

```text
/Work/paper.pdf:p7:crop=72,298,214,17: Net revenue (after adjustments)
/Work/paper.pdf:p7:12:crop=72,298,214,17: Net revenue (after adjustments)     with -n
/Work/paper.pdf:p12:crop=310,505,96,16;470,505,96,16: Net revenue  Net revenue    two matches in a line
/Work/paper.pdf:p12:crop=310,505,96,16;470,505,96,16;630,505,96,16;+2 more: …      at most three in a line
/Work/paper.pdf:p7- a context line, as today
/Work/notes.md:14:a line of a note, as today
```

The numbers are ready for `pdf <path> <page> --crop x,y,w,h`: **PDF points of the page as displayed**, from the top left, whole numbers, the same frame as `pdf info` and `--crop` (`src/Kumbu.Core/Attachments/Crop.cs:6`, `PdfCommand.cs:55-57`), and with no `--rotate` (§1.2).

A line with a match but no region (a zero-width match, a match in white space, a page of §3.4) prints as it does today. Nothing is guessed.

### 3.3 Where the coordinates come from

Today they are thrown away: `pdf/src/analyse.ts:70-78` uses each text item's box for the coverage and keeps only the text.

ASSUMPTION: **asked of the sidecar when wanted, not stored.** A new route takes a PDF and pages, and answers each page's text and boxes; nothing is written. Recommended, because it needs no change to the sealed format, no new analyser version and no re-analysis (which would make the first `pdf` call on every existing PDF wait for up to 60 s, `PdfAnalysisRunner.cs:52`), and because rendering works the same way: bytes opened per request, sent, forgotten. The price is a sidecar call per PDF with matches, so `--regions` is an opt-in with limits, and it depends on the sidecar being up. Alternative: store the boxes with the text in the analysis (`PdfAnalyses.cs:445-448`). Then `--regions` is free and works with the sidecar away, but `SearchTextAsync`, `TextAsync`, chunking and re-keying all meet a new kind of part, for a result seldom asked for.

**Route** `POST /text-boxes?pages=7,12` (body: the PDF; at most 50 pages; job kind `boxes`, under the render limit of 10 s, `pdf/src/server.ts:17`):

```json
{ "pages": [ { "n": 7, "width": 595.28, "height": 841.89, "text": "…",
               "items": [ { "at": 0, "end": 31, "x": 74.2, "y": 300.1, "w": 210.3, "h": 11.0 } ] } ] }
```

- `text` comes from one function shared with the analysis (`analyse.ts:68-79`: items' `str` in order, `\n` after `hasEOL`, `trimEnd`). `at` and `end` are offsets into it, in UTF-16 units on both sides. Items of white space have no entry.
- **A page of more than 20,000 items** answers `items: []`: outlined or hostile text, and no use for a region.
- `no_such_page` as for rendering; `not_pdf`, `encrypted` as today.
- `IPdfSidecar` and `IAttachmentPixels` gain `TextBoxesAsync(…, IReadOnlyList<int> pages, ct)`. `PdfSidecar` checks the answer as it checks an analysis (`PdfSidecar.cs:280-337`): at most 16 MiB, pages as asked, offsets within the text and ascending, finite numbers at or above 0. `AttachmentPixels` opens the bytes through `BytesAsync` (`AttachmentPixels.cs:85-88`), the same path as rendering.

**A box, correctly.** `analyse.ts:76-77` treats every item as horizontal. pdf.js gives `width` as a length along the text's direction (`Math.abs(totalWidth)`, in `runBidiTransform`), so a turned y-axis title or a line on a sideways sheet comes out as the wrong rectangle. The new function takes the four corners from the unit vectors of the transform: origin `(e, f)`, `u = (a, b)/|(a, b)|` scaled by `width`, `v = (c, d)/|(c, d)|` scaled by `height`, each through `viewport.transform`, and the bounding box of those. An item with a zero vector has no box. `analyse.ts` keeps its coverage boxes as they are unless item 4 ships (§4.3), as `textCoverage` is stored and rules 1 stay valid.

**From a match to a region**, in `Rg.AddPdfLinesAsync` (`Rg.cs:286-356`):

1. The search keeps running on the stored text. With `--regions`, the matched lines of one PDF are held until its text is read (`:305-337`), then `TextBoxesAsync` is called once, for the pages that have matches.
2. `Search` gains `Spans(line)` over `regex.EnumerateMatches`, on the same regex; zero-width matches are skipped. A line's offset in the page text comes from adding up the lengths from `Lines.Split` (`src/Kumbu.Core/Sh/Lines.cs:8-13`), which splits on `\n` and drops nothing else.
3. A match's box is the union of the **items** it touches, not of the line (pdf.js keeps a line's columns as separate items, so a table cell stays a cell, and two columns don't merge) and not of the word (items have no per-glyph widths, and a guess is not worth it; the whole fragment is also the more useful look). Equal boxes in a line count once.
4. Grown by `max(2 pt, 0.3 × the box's short side)`, as a text box runs from the baseline and leaves out descenders, then made whole by `Crop.Enclosing`: x and y rounded down, the far edges up, then cut at the page's size rounded down.

`Crop.Enclosing` (new, `src/Kumbu.Core/Attachments/Crop.cs`) also replaces the rounding of `pdf info`. `PdfCommand.Point` (`:275,281`) rounds x and w each to nearest, so a figure flush with the page's edge can come out past it (x 100.6, w 494.7 on a page 595.3 wide gives 101 + 495 = 596) and the "ready for --crop" region is refused. Noticed while reading; same helper, one line.

### 3.4 When regions can't be had

The search result is never lost. The matches print without regions, and a `Notice` (`src/Kumbu.Core/Sh/ShellResult.cs:47`) says why, joined with the note about other writers at `Rg.cs:201` if both apply. It goes above the output and isn't passed down a pipe, so it can't be taken for a match.

| Situation | Notice |
|---|---|
| More than 8 PDFs have matches (`MaxRegionPdfs`; each means sending a whole file) | `Regions only for the first 8 PDFs with matches; name a PDF to get its own.` |
| A PDF has matches on more than 50 pages (`MaxRegionPages`) | `Regions for /Work/paper.pdf only on its first 50 pages with matches; use -m, or look at one page: pdf /Work/paper.pdf 60.` |
| Sidecar away, busy or too slow | `No regions: PDF rendering is unavailable right now. The matches are all here.` |
| The sidecar's text of a page differs from the stored text (an older analyser, an older sidecar) | `No regions on some pages of /Work/paper.pdf: its stored text comes from an older analysis. pdf info /Work/paper.pdf renews it.` |

The stored text decides what matches; the sidecar's text is compared with it character by character, and a page that differs gets none. So a region can never point at a different text than the one printed.

### 3.5 What a region doesn't promise

On a scan with an OCR layer the boxes are where the OCR put its words, which may be off. On a page with covered text the box is where the covered text lies. Both are why the next step is to look: the crop shows what is there. `help rg` says so in a sentence, no more; marking it is a non-goal.

### 3.6 Help

`help rg` (`Rg.cs:44-50`, and after `--tag` at `:66-67`); `help pdf` adds at `PdfCommand.cs:63` "`rg --regions` also says where on the page a match lies.":

```text
  --regions                  for matches in PDFs, also where they lie on the page, ready for pdf's --crop:
                             /Work/paper.pdf:p7:crop=72,298,214,17: text. In points of the page as displayed,
                             around the piece of text that holds the match. It shows where the text box lies,
                             not that anyone can see it: look with 'pdf /Work/paper.pdf 7 --crop 72,298,214,17'.
                             Regions come from the PDF itself each time, for up to 8 PDFs a search; not with
                             -l, -c, -v, --files, --tag or --version, nor right of a pipe.
```

### 3.7 Worked example

```text
> rg --regions -i 'net revenue' /Work/Papers
/Work/Papers/notes.md:14:Net revenue looks odd here
/Work/Papers/report.pdf:p7:crop=72,298,214,17: Net revenue (after adjustments)
/Work/Papers/report.pdf:p12:crop=310,505,96,16;470,505,96,16: Net revenue  Net revenue

> pdf /Work/Papers/report.pdf 7 --crop 72,298,214,17 --size small
/Work/Papers/report.pdf (version a1c9), page 7 of 23 (text): page 595×842 pt; crop 72,298,214,17; small: 768×61 px PNG, 9 KB
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the line]
```

A client that wants the rest of the table row widens the numbers itself: `--crop 40,298,515,17`.

## 4. Regions with a figure's legend and labels: the attempt

Runs **last**, in commits of its own, so it can go without touching the rest.

### 4.1 What it would add

Today's tight region stays exactly as it is. A figure may gain a second, larger one, in the same units:

```text
p4 mixed: vector drawing ≈ 35 % — regions (pt, x,y,w,h): drawing 72,300,451,260 with labels 48,290,520,300; image 80,90,200,120
```

An entry with nothing to add stays as today. So the worst outcome for a client is a larger or incomplete crop it chose, next to the tight one that was there anyway.

### 4.2 The rule

Deterministic and by position only: no recognition, and a raster figure gains nothing from what is drawn into its pixels. In `pdf/src/classify.ts`, after the regions at `:67-70`, for a region that isn't a full page's image (a scan's OCR text isn't its label):

- **Candidates:** text boxes short enough to be labels (fewer than 30 characters; `PageMeasure.textBoxes` gains a count), and the small path boxes that fell under `MIN_FIGURE` (legend swatches).
- **Blockers:** text boxes of 30 characters or more: body text, long captions.
- **Growth:** starting from the tight box, repeatedly add a candidate within `LABEL_GAP_PT` (starting value 12), unless it lies closer to another region, is inside another region, or the box would then reach a blocker's centre or another figure. A chain, so a legend's second line follows its first. Processed in a fixed order. Above 5,000 text boxes a page is skipped.
- **Result:** the union, `labelled` on the region, cut at the page.

Constants may change during tuning (§4.5); the rule's shape may too, as long as it stays position-only.

### 4.3 What it would change

`RULES_VERSION` to 2 (`classify.ts:25`) and `PdfAnalyses.Analyser` to match (`src/Kumbu.Server/Attachments/PdfAnalyses.cs:42`). `analyse.ts:76-77` uses the corrected boxes of §3.3, since rotated labels depend on them. `Region` in TypeScript, and `RegionJson`, `PdfRegion`, `RegionRecord` (`PdfSidecar.cs:277,320-325`, `PdfAnalysis.cs:7`, `PdfAnalyses.cs:441`) gain an optional box, checked like the others. `InfoLine` (`PdfCommand.cs:270-279`) prints it.

Effects of the new analyser, all through the existing mechanism: each PDF is analysed anew at its next `pdf` call, waiting up to 60 s and otherwise falling back on the old analysis (`AttachmentPixels.cs:66-74`). `rg` keeps using what is stored. Nothing is embedded again, because the text is unchanged and vectors are reused by hash (`src/Kumbu.Server/Search/SearchIndex.cs:229-233`). Page types don't change, as `classify` uses neither the new boxes nor text coverage for them (`classify.ts:61-65`); the measure holds this as a condition.

Help, `PdfCommand.cs:57-58`, replacing "which leave out a figure's labels and legend when those are text":

```text
                            gives the regions. A region is the figure's drawing or image alone; 'with labels' adds
                            the text and legend around it, found by position, so it may miss something or take
                            in a line too many.
```

### 4.4 Worked example

```text
> pdf info /Work/Papers/report.pdf
report.pdf: PDF, 12 pages, 2.1 MB
Pages: 1-3 text, 4 mixed, 5-12 text
p4 mixed: vector drawing ≈ 35 % — regions (pt, x,y,w,h): drawing 72,300,451,260 with labels 48,290,520,300

> pdf /Work/Papers/report.pdf 4 --crop 48,290,520,300
/Work/Papers/report.pdf (version a1c9), page 4 of 12 (mixed): page 595×842 pt; crop 48,290,520,300; medium: 1442×832 px PNG, 96 KB
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the chart with its axes and legend]
```

### 4.5 The measure of "reliable enough"

**What exists doesn't reach it.** `tools/attachment-probes/make_probes.py` draws images only; its `chart.png` (`:111-138`) has its labels in the pixels, and no text layer to measure. The PDFs of the 2026-11-01 round are private, and `mix.pdf` of the playbook is made by hand per run, so neither is in the repository. `pdf/test/fixtures.ts:58-64` has a chart with no labels at all. What exists is the **list of layouts that matter**, in the playbook's table (`docs/test-playbook-attachments.md:45-61`). So the run builds a corpus from it.

**Corpus.** `tools/attachment-probes/make_figure_pdfs.py <out> --seed N` (uv, like its sibling; nothing committed but the script), drawing new values for a seed:

- **Three generators,** each making its own text and drawing: pdf-lib, as in the fixtures; matplotlib (`pdf.fonttype 42`, so text stays text); **LibreOffice Calc**, an `.xlsx` with a chart through openpyxl, converted with `soffice --headless`. Calc is the generator of the charts that showed the problem in §6.2.
- **Layouts** (as far as a generator can make them, at least 20 figures per generator):
  1. one large chart, legend to the right (playbook, line 52);
  2. one large chart, legend below;
  3. two small charts side by side, about 11 % each (line 53);
  4. a chart between paragraphs, close above and below;
  5. a chart in one column of two (line 49);
  6. a chart over a long caption;
  7. a chart on a page turned by 90° (line 60);
  8. a chart with a turned axis title and slanted category labels.
- **Controls,** with no chart: plain text, a table with lines (line 50), a small logo in text (line 54), four photos (line 59), a full-page scan with an OCR layer (line 57), an empty page.

**Ground truth by name.** Every label, tick label, axis title and legend entry carries its figure's token (`zqA3`, `zqB7`; Calc through a number format). Body text comes from a word list with no tokens and no digits. So whichever generator made it, an item's identity is read from the text pdf.js gives, not from the generator's idea of the layout. A caption starts with `Figure N` and counts for neither side.

**One figure passes if both hold:**

- **Complete:** its drawing, and each text item with its token, lie at least 90 % by area in the `with labels` region (an entry with no such region is judged by the tight one).
- **Clean:** no item of body text or of another figure has its centre in it.

**Procedure.** `pnpm -C pdf eval:regions <out>` prints the table and the verdict. Constants are tuned on seeds 1 to 5. The verdict is taken once on the **held-out** seeds 101 to 110, which the tuning hasn't seen. If it fails, one more round of tuning is allowed, with seeds 201 to 210 as the new held-out set, and both results are reported. Then it stops: no third look.

ASSUMPTION: **reliable enough** means, on the held-out set of at least 60 figures: **at least 90 % of figures pass overall, at least 80 % for each generator, at least 70 % for each layout**; and on the controls and the existing fixtures of `pdf/test/`, **no page type or tight region differs** from rules 1, and no `with labels` region contains a body line. Recommended because the tight region stays alongside, so a miss costs one more crop, while a rule right only three times in four would teach clients not to trust it. The per-generator bar stops a rule tuned to tidy matplotlib output from passing without Calc. If LibreOffice can't run in the session, the verdict is "not measured" and the item is left out; the brief's environment section must therefore install `libreoffice-calc`. Alternatives: a lower bar (80 % overall), or two generators, accepting that the case behind the wish goes unmeasured.

**Reporting.** `docs/notes-attachment-wishes-hands-on.md` holds the table per generator and layout, for both held-out rounds if there were two, the constants, the failures by kind, and the verdict in one sentence. The same sentence goes into the pull request, whichever way it went.

**If it isn't reliable enough:** a revert commit on the branch takes out `with labels`, `RULES_VERSION` stays 1, the analyser stays, and the help keeps its present sentence. The script, the eval and the numbers stay, so that a later attempt starts from them; the attempt's commit is named in the note. Items 1 to 3 ship regardless.

**What this can't show.** It measures geometry on generated pages, not whether a model reads a chart better, and not the maintainer's real PDFs. If it ships, the playbook gains a step (§7), and the first real PDF that disagrees outweighs the table.

## 5. Security boundary

Nothing moves. The rule of the design this extends still reads: **untrusted PDFs are never parsed in the process that holds vault keys**, and neither are images.

- **Same reading paths.** All new reads go through `IAttachmentPixels`: `store.VaultOfAsync` and `catalog.ReadBlobAsync` with the request's credential (`AttachmentPixels.cs:82-88`). `rg` finds its PDFs through `StoreAttachments.UnderAsync` as before (`Rg.cs:165`), so `--regions` reaches only what the caller could already read; no new endpoint, no new route to the sidecar beyond its internal network (`compose.yaml:120-121`).
- **Bytes stay sealed.** They are opened per request, sent over the compose network and forgotten. §1, §2 and §3 store nothing at all, neither pixels nor boxes nor frame counts. §4 adds one box per region inside part 0, sealed under the vault key and bound to the blob as today, and gone with the blob, deleted or re-keyed along with it (`PdfAnalyses.cs:27-35`).
- **GIFs and new parsing stay in the sidecar.** `omggif` runs in a worker under the existing heap, time and container limits; the frame-rectangle check of §2.2 closes the bomb it would open. The server learns only validated numbers.
- **What comes back is checked:** the `rotate` and `frame` echoes, the existing header and size checks on pictures (`PdfSidecar.cs:212-234`, symmetric in width and height), and the bounds on the new text-box answer (§3.3).
- **Parameters are whitelisted on both sides,** `rotate` from a set, `frame` and `pages` by `integer`. Nothing a person typed reaches a query unparsed.
- **Text from a PDF stays data.** A line can't forge a region (§3.2), and the region's numbers come from the validated answer.
- **Metrics hold no names:** enums and flags only (`IAttachmentPixels.cs:45`). The label guard test covers the additions (`tests/Kumbu.Server.Tests/Metrics/LabelGuardTests.cs`).

The accepted limit stays: a compromised sidecar sees the files sent to it. `--regions` sends files that a plain search wouldn't, on the caller's explicit request; one sentence for `docs/security-architecture.md`.

## 6. Cache effects

There is no cache of pixels (non-goal of the design this extends, and none in `AttachmentPixels`), so nothing has to be invalidated:

- Each turn, frame and crop is rendered fresh: same bytes and same limits as any other call. `--frame N` decodes frames 1 to N each time.
- A client that recognises pixels it already holds (Hermes, research §2.5) sees a turned picture as new, correctly.
- Stored analyses, page text and embeddings are untouched by §1 to §3. §4 goes through the existing analyser-version mechanism (§4.3).

## 7. Metrics and docs

**Metrics** (`src/Kumbu.Server/Metrics/KumbuMetrics.cs:398-420`, `:596` and following, and the dashboard's Attachments row): `kumbu_pictures_turned_total{command=img|pdf}`, `kumbu_img_frames_total`, `kumbu_rg_regions_total{outcome=delivered|limited|unavailable}`, as new `AttachmentUse` records. They answer whether anyone uses what was asked for.

**Docs:**

- `docs/concept.md` §12: the new flags.
- `docs/attachments-client-research.md` line 279: what was built, what was left out, and why.
- `docs/test-playbook-attachments.md`:
  - step 6 gets a question only frame 2 can answer, and the key of `make_probes.py:198` stops saying "no right answer yet";
  - a new probe `sideways.png` (a sheet stored on its side, no EXIF, a code in small type), with a step: "read the code, then crop it, and say how many tries it took";
  - section C: "find this word with `rg --regions`, then crop it. Did the crop hit it?";
  - if §4 ships: "crop the large chart with its labels. Could you read the axes and the legend without another call?";
  - a row each in the results table.
- `docs/security-architecture.md`: the sentence of §5.
- `THIRD-PARTY-NOTICES.md`: `omggif`.
- `README.md` needs nothing: no new variable or service.

## 8. Testing

**Sidecar (Vitest, `pdf/test/`):**

- Turning a page and an image by each of the three angles, probed at four coloured corners, with and without a crop, on a page with `/Rotate` (`rotatedPage`), with a CropBox (`cropBoxPage`) and on a JPEG with EXIF (the `exif` helper of `image.test.ts`).
- The conversions in the help: a region of a tight box, converted by the printed formula and cropped with `--rotate`, hits the same colour.
- GIFs through `GifEncoder`: frame 2 with disposals 0, 2 and 3 and partial rectangles, composed correctly; a frame past the end; a hand-made GIF whose frame rectangle declares 60,000 × 60,000 is refused without an allocation of that size; the frame count; turn and crop on a frame.
- `/text-boxes`: boxes on horizontal text, turned text, a `/Rotate` page, a CropBox page; `text` identical to what `analyse` stores; limits on pages and items.
- The echoed headers. Parameters out of range.

**Core (xUnit, `tests/Kumbu.Core.Tests/Sh/`):**

- `ImgTests`, `PdfCommandTests`: every sentence of §1.4 and §2.3; the text blocks; "Reduced to" for a turned picture; `--rotate` kept in the "Continue with" line and passed on in `WithoutAnalysisAsync`; the check against the turned size; a text-only `--rotate` refused.
- `RgPdfTests`: the line formats with and without `-n`; context lines; union by item, two matches in a line, the cap of three; refusals with each flag named; all four notices; a mismatch of text giving no region; a forged `crop=` in the text staying text; output of a plain `rg` byte-identical to today.
- `CropTests`: `Crop.Enclosing` at the page's edge, the case of §3.3.

**Server (xUnit, `tests/Kumbu.Server.Tests/Attachments/`):** `PdfSidecarTests` for the echo refused, the new codes and malformed text-box answers; `PdfToolTests`, `ImgToolTests` through the faked sidecar; metric labels from fixed sets.

**Hands-on, in `docs/notes-attachment-wishes-hands-on.md`:** with the stack running and through MCP, each worked example of this design on generated files, the real outputs quoted. A checklist for the maintainer: the client round of the playbook's new steps, which the run can't do, as it has neither the clients nor the private PDFs. Everything generated stays neutral: no real documents, nothing that names an organisation.

## 9. Order and scope for the run

1. `--rotate`; 2. `--frame`; 3. `rg --regions`; 4. the attempt of §4, last.

A pull request with all of it, its description stating the verdict of §4.5. Both images must come from the same tag, as always, and the echo of §1.3 covers the moment when they don't.

## Left to the builder

The names of records and helpers, the transform used for a turned image, `Crop.Enclosing`'s exact shape, how `AddPdfLinesAsync` holds its lines, metric buckets, and the eval's code and output format. The sentences and formats above are not left open: if one proves wrong in the hands-on test, change it and say why in the note.
