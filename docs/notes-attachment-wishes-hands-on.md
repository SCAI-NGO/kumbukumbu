# Test notes: attachment wishes (`--rotate`, `--frame`, `rg --regions`), hands-on

Date: 2026-11-03. Hands-on test of the attachment wishes (spec [2026-11-02-attachment-wishes-design](superpowers/specs/2026-11-02-attachment-wishes-design.md)) by Claude, on the branch `feature/attachment-wishes`, before the pull request. Regions with labels (§4) are not in this build and were not tested.

Set-up: the server from source (`dotnet run --project src/Kumbu.Server`, on `127.0.0.1:8099`) with a database of its own on a local MongoDB replica set, search off, a fresh dev token that went into no log or document, and `Kumbu:Seed:Path` pointing at a folder with one note, so `/Demo` was seeded. The sidecar from source too (`pnpm -C pdf build`, `PORT=8090 pnpm -C pdf start`). The agent side was MCP over plain HTTP (stateless `tools/call` requests to `/mcp`, no `initialize` needed); every image delivered was decoded from the answer and looked at.

Fixtures, made by a script (no real documents): `sheet.jpg`, 3000 × 2000, a portrait expense sheet photographed sideways, with a random six-letter reference code in small type at its foot; `mix.pdf`, 15 pages, page 7 one full-page JPEG of a landscape sheet lying on its side with a red "Total" box; `animated.gif`, the probe of `tools/attachment-probes/make_probes.py --seed 11` (three full frames of 600 × 400: green, orange, purple); `partial.gif`, three frames of 600 × 400 that each add a coloured box as a partial rectangle; `big.gif`, 4200 × 4200, one frame; `shot.png`; `report.pdf`, 23 pages, "Net revenue" in a line of page 7 and in both columns of a table on page 12; `notes.md` with the same phrase on line 14; nine one-page memos and a 60-page PDF with "budget review" on every page, for the limits of `--regions`; and a PDF whose text reads `crop=1,1,1,1: budget forged`. Uploaded with `PUT /api/files/Demo/…` (201 each), except `notes.md`: `/api/files` refuses a note ("is a note: /api/notes reads and writes it"), so it went in through the MCP `write` tool.

## Checks

The last phase gate of the branch:

| Suite | Command | Result |
|---|---|---|
| Server and core | `dotnet test --solution Kumbu.slnx` | 3589 tests: 3583 passed, 6 skipped (4 for the model and a TensorX key, 2 that need root), 0 failed. A first run had one failure in `PairingTests.A_code_redeems_while_its_session_lives_though_another_of_the_user_ended`, which this branch doesn't touch; 5 of 5 reruns green |
| PDF sidecar | `pnpm -C pdf test` | 8 files, 139 tests passed (the gate predates the limits on frames and canvas found below; with them, 8 files, 145 tests) |
| Web app | `pnpm -C web test --run` | 85 files, 1368 tests passed |

## What I did and saw

### `--rotate` (spec §1.6)

```text
> pdf /Demo/Scans/mix.pdf 7 --images=all
--- Page 7 of 15: image (no text layer; one image covers the page) ---
/Demo/Scans/mix.pdf (version f7fb32e8522c), page 7 of 15 (image): page 595×842 pt; medium: 921×1302 px PNG, 281 KB
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the sheet on its side, its title running bottom to top]

> pdf /Demo/Scans/mix.pdf 7 --images=all --rotate 90
--- Page 7 of 15: image (no text layer; one image covers the page) ---
/Demo/Scans/mix.pdf (version f7fb32e8522c), page 7 of 15 (image): page 595×842 pt, turned 90° clockwise to 842×595 pt; medium: 1302×921 px PNG, 266 KB
With --rotate 90, --crop counts from the top left of this turned page, in its points.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the sheet upright, "Quarterly sheet, Demo", eight lines, the red "Total 4,812.00" box at the bottom right]
```

The box stands at about 920,744 in the picture, 312 × 116 px. One pixel is 842/1302 of a point, so in points 595,481,205,75:

```text
> pdf /Demo/Scans/mix.pdf 7 --rotate 90 --crop 595,481,205,75 --size large
/Demo/Scans/mix.pdf (version f7fb32e8522c), page 7 of 15 (image): page 595×842 pt, turned 90° clockwise to 842×595 pt; crop 595,481,205,75 of the turned page; large: 1708×625 px PNG, 51 KB
With --rotate 90, --crop counts from the top left of this turned page, in its points.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the red box, "Total 4,812.00", upright and sharp]
```

The first try hit. The same for the image:

```text
> img /Demo/Scans/sheet.jpg --rotate 90
/Demo/Scans/sheet.jpg (version 833b78794124): image (JPEG), original 3000×2000 px, turned 90° clockwise to 2000×3000 px; medium: 894×1341 px PNG, 279 KB
Reduced to 45 %: small text and digits may be misread; look at a part with --crop x,y,w,h before quoting them.
With --rotate 90, --crop counts from the top left of this turned image, in its pixels.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the expense sheet upright, fourteen items, the total 3,127.45, a reference code too small to read with certainty]

> img /Demo/Scans/sheet.jpg --rotate 90 --crop 160,2720,640,110
/Demo/Scans/sheet.jpg (version 833b78794124): image (JPEG), original 3000×2000 px, turned 90° clockwise to 2000×3000 px; crop 160,2720,640,110 of the turned image; medium: 640×110 px PNG, 16 KB
With --rotate 90, --crop counts from the top left of this turned image, in its pixels.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: "Reference code" and the six letters, which match the generator's key]
```

The pictures come as PNG where the spec's example says JPEG: the encoder's choice for this content, not a sentence of the spec.

More of it:

| Call | Result |
|---|---|
| `pdf /Demo/Scans/mix.pdf 5-15 --images=all --rotate 90 --size small` | pages 5 to 8, each turned (text pages with their text above the picture), then `--- Stopped after page 8: at most 4 images per call. Continue with: pdf /Demo/Scans/mix.pdf 9-15 --images=all --rotate 90 --size small ---` |
| `pdf /Demo/Scans/mix.pdf 7 --images=all --rotate 270 --size small` | the sheet upside down, as 270° on a sheet that needs 90° must give |
| `img /Demo/Scans/sheet.jpg --rotate 90 --rotate 180 --size small` | the last wins: `turned 180° clockwise to 3000×2000 px`, the sheet on its other side |

Refusals of §1.4, each as the spec has it:

| Call | Answer |
|---|---|
| `img /Demo/Scans/sheet.jpg --rotate 45` | `img: --rotate turns clockwise by 90, 180 or 270 degrees, not '45' (leave it out for no turn)` |
| `pdf /Demo/Scans/mix.pdf 7 --images=all --rotate 45` | `pdf: --rotate turns clockwise by 90, 180 or 270 degrees, not '45' (leave it out for no turn)` |
| `img /Demo/Scans/sheet.jpg --rotate 0` | the same sentence with `not '0'` |
| `img /Demo/Scans/nothing.jpg --rotate 45` | the same sentence: the value is checked before the path is opened |
| `pdf /Demo/Scans/mix.pdf 7 --rotate 90` | `pdf: --rotate turns the pictures of a page; add --images, --images=all or --crop` |
| `pdf info /Demo/Scans/mix.pdf --rotate 90` | `pdf: info takes a path and nothing else, e.g. pdf info /Demo/Scans/mix.pdf` |
| `img /Demo/Scans/sheet.jpg --rotate 90 --crop 0,2500,800,600` | `img: the crop 0,2500,800,600 lies outside the image as turned by --rotate 90 (2000×3000 px)` |
| `img /Demo/Scans/sheet.jpg --crop 0,2500,800,600` | `img: the crop 0,2500,800,600 lies outside the image (3000×2000 px)`, today's sentence |
| `pdf /Demo/Scans/mix.pdf 7 --rotate 90 --crop 700,500,300,200` | `pdf: the crop 700,500,300,200 lies outside page 7 as turned by --rotate 90 (842×595 pt)` |

The spec's table names `(3000×2000 px)` for the image: that is an image of 2000 × 3000 turned, and the sentence gives the turned size, as here. No change.

### `--frame` (spec §2.5)

```text
> img /Demo/Clips/animated.gif
/Demo/Clips/animated.gif (version 6e988e5cff61): image (GIF), original 600×400 px; medium: 600×400 px PNG, 2 KB
An animated GIF of 3 frames, shown as a still: frame 1. --frame N shows another.
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: green]

> img /Demo/Clips/animated.gif --frame 2
/Demo/Clips/animated.gif (version 6e988e5cff61), frame 2 of 3: image (GIF), original 600×400 px; medium: 600×400 px PNG, 2 KB
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: orange]

> img /Demo/Clips/animated.gif --frame 7
img: /Demo/Clips/animated.gif has no frame 7; img without --frame says how many it has
```

The probe's key: green, orange, purple. `--frame 3` showed purple, `--frame 1` green.

| Call | Result |
|---|---|
| `img /Demo/Clips/partial.gif --frame 3` | frame 3 of 3 as played: the red, blue and green boxes of the three frames together on white |
| `img /Demo/Clips/partial.gif --frame 2 --rotate 90 --crop 0,0,300,200 --size small` | `frame 2 of 3: … turned 90° clockwise to 400×600 px; crop 0,0,300,200 of the turned image; small: 300×200 px PNG, 781 B`: the red box at the right edge from x 200, y 40 to 200, where a turn by 90° puts it; the blue one outside the crop |
| `img /Demo/Clips/animated.gif --frame 2 --rotate 180 --size small` | orange |
| `img /Demo/Clips/big.gif --size small` | one frame: no note line about frames (the picture is black, as made) |

Refusals of §2.3:

| Call | Answer |
|---|---|
| `img /Demo/Clips/animated.gif --frame 1-3` | `img: --frame is one frame, counting from 1, not '1-3'` (likewise `'0'`, `'x'`) |
| `img /Demo/Screens/shot.png --frame 2` | `img: --frame is for GIFs; /Demo/Screens/shot.png is a PNG` (a JPEG: `is a JPEG`) |
| `img /Demo/Clips/animated.gif --frame 7` | `img: /Demo/Clips/animated.gif has no frame 7; img without --frame says how many it has` |
| `img /Demo/Clips/big.gif --frame 1` | `img: /Demo/Clips/big.gif is too large for --frame (at most 16 megapixels); img without --frame shows its first frame` (the sentence before the limits found below; now `… (at most 4 megapixels and 10,000 frames); …`) |
| `pdf /Demo/Scans/mix.pdf 7 --frame 2` | `pdf: unrecognized option '--frame'` |

### `rg --regions` (spec §3.7)

```text
> rg --regions -i 'net revenue' /Demo/Papers
/Demo/Papers/notes.md:14:Net revenue looks odd here
/Demo/Papers/report.pdf:p7:crop=68,321,162,19: Net revenue (after adjustments)
/Demo/Papers/report.pdf:p12:crop=306,521,68,19;466,521,68,19: Net revenue Net revenue

> pdf /Demo/Papers/report.pdf 7 --crop 68,321,162,19 --size small
/Demo/Papers/report.pdf (version 8579e985b369), page 7 of 23 (text): page 595×842 pt; crop 68,321,162,19; small: 768×90 px PNG, 15 KB
If no image follows, your client dropped it: tell the user so instead of guessing what it shows.
[image: the line "Net revenue (after adjustments)", whole, with a little room around it]
```

The two regions of page 12 each show one cell, "Net revenue", and the columns didn't merge. Widened by hand, `pdf /Demo/Papers/report.pdf 12 --crop 40,503,515,55 --size small` shows the row with "Returns" above and "Amount 1,143 1,253" below.

| Call | Result |
|---|---|
| with `-n` | `/Demo/Papers/report.pdf:p7:14:crop=68,321,162,19: Net revenue (after adjustments)`, `…:p12:6:crop=…` |
| plain `rg -i 'net revenue' /Demo/Papers` | as before the branch: `/Demo/Papers/report.pdf:p7: Net revenue (after adjustments)` |
| `-C1` on `report.pdf` | context lines `/Demo/Papers/report.pdf:p7- Line 12 of section 7: …` as today, the match with its region between them |
| `-m 1`, `-w -F 'Net revenue'` | regions as above |
| `rg --regions 'x*' /Demo/Papers/report.pdf \| head -3` | a line without an x (a zero-width match only) prints without a region; the others with |
| `rg --regions … \| head -2` | a pipe to the right is fine; the regions go down it |
| `rg --regions budget /Demo/Odd` | `/Demo/Odd/forged.pdf:p1:crop=68,126,156,20: crop=1,1,1,1: budget forged`: the forged region stays text, after the real one |

Refusals of §3.1: `-l`, `-c`, `-v`, `--files`, `--tag x`, `--version abc` and `cat /Demo/Papers/notes.md | rg --regions revenue` each answer `rg: --regions says where matches lie in PDFs; it doesn't go with -l, -c, -v, --files, --tag or --version, nor right of a pipe`. (`rg --regions --version` with nothing after it gets rg's own `option '--version' requires an argument` first, as `--version` takes a value.)

Notices of §3.4:

| Situation | Notice |
|---|---|
| `rg --regions budget /Demo/Many` (nine PDFs) | `Regions only for the first 8 PDFs with matches; name a PDF to get its own.`; the ninth memo's line without a region |
| `rg --regions budget /Demo/Long` (60 pages) | `Regions for /Demo/Long/long.pdf only on its first 50 pages with matches; use -m, or look at one page: pdf /Demo/Long/long.pdf 51.`; pages 51 to 60 without regions. With `-m 3`, no notice |
| Sidecar stopped (`fuser -k 8090/tcp`) | `No regions: PDF rendering is unavailable right now. The matches are all here.` above all three matches, without regions |

Meanwhile, with the sidecar stopped, `img … --rotate 90` and `img … --frame 2` answered `img: image rendering is unavailable right now; try again later`, and `pdf /Demo/Scans/mix.pdf 7 --images=all --rotate 90` the header `… — not rendered: PDF rendering is unavailable right now; try again later ---`. Started again, the regions and frames came back on the next call.

### Metrics

After a few calls, `kumbu_pictures_turned_total` counted `img` and `pdf`, `kumbu_img_frames_total` the frame shown, `kumbu_rg_regions_total{outcome="delivered"}` the search, and `kumbu_img_refusals_total` `bad_rotate` and `bad_frame`, each under its own label, not as `unavailable`.

### The memory of the composing path

The sidecar's image couldn't be built here (its build stage gets `SELF_SIGNED_CERT_IN_CHAIN` from the npm registry through the sandbox's proxy), so the built `pdf/dist` ran with its `node_modules` mounted in the Dockerfile's base image (`node:26-bookworm-slim`, by digest), under `compose.yaml`'s limits and hardening: `--memory=1536m --memory-swap=1536m --cpus=1.5`, read-only, no capabilities. The host has cgroup v1, so the peak is `memory.max_usage_in_bytes` (there is no `memory.peak`), with samples of `memory.usage_in_bytes` every half second.

The GIFs: 16 MiB each, a few full-canvas frames with disposal 3, then as many 1 × 1 frames as fit, about 600,000 frames a file; at 4000 × 4000 (16 MP), and likewise at 12, 8 and 4 MP. The load: two `POST /render/image?longEdge=2576&pixels=3700000&bytes=1048576&frame=…` at once, 16 more 16 MiB bodies queued behind them, repeated for a minute. The last frame can't be composed within the worker's 10 s on 1.5 CPUs (every answer 504; peak 1,520,140,288 B, not killed), so the runs below ask for frame 250,000, which one request alone composes in 5.5 s:

| Cap (`MAX_FRAME_PIXELS`) | GIFs | Answers before the end | `OOMKilled RestartCount` | Highest usage |
|---|---|---|---|---|
| 16 MP | 4000 × 4000 | 504, 504, 504, 504, 200, 504, 504, 504, 504 | `true 0` (exit 137) | 1,548,881,920 B |
| 12 MP | 3464 × 3464 | 200, 504, 200, 504 | `true 0` (exit 137) | 1,572,208,640 B |
| 8 MP | 2828 × 2828 | 200, 200, 200, 200 | `true 0` (exit 137) | 1,573,146,624 B |
| 4 MP | 2000 × 2000 | 200, 200, 200, 200 | `true 0` (exit 137) | 1,608,798,208 B |
| 16 MP, 1004 frames in 1.3 MB | 4000 × 4000 | 54 × 200 | `false 0` | 1,105,334,272 B (peak) |
| 16 MP, no `--frame` (today's path) | 4000 × 4000 | 108 × 200 | `false 0` | 1,277,190,144 B (peak) |

So a lower cap doesn't hold: the sidecar is killed at 4 MP as at 16. With these files the memory goes with the number of frames, not with the canvas. Measured in a single process, `omggif`'s reader alone, which lists every frame when it is made, takes the process from 60 to 183 MiB for 650,000 frames; composing frame 250,000 of a 4 MP GIF peaks at 405 MiB (frame 5: 314 MiB), of which the canvas is 16 MB.

**After the limit on frames.** The sidecar now counts a GIF's frames by walking its blocks, before `omggif` is given the file, and the composing path takes at most 10,000 frames (`too_large` beyond); a plain `img` of a GIF with more is drawn as before, its count unknown. The walk skips extensions by `omggif`'s own rules, so it never counts fewer frames than `omggif` would read (a review found a crafted application extension that hid 500,000 frames from a plainer walk). The cap on the canvas stayed 16 MP at first. Measured again, with the same container and load:

| Run | Answers | `memory.max_usage_in_bytes`, `failcnt` | `OOMKilled RestartCount` |
|---|---|---|---|
| The 600,000-frame GIFs with `--frame` | 504 answers of 413 (`The GIF has too many frames for a frame: at most 10,000.`), in about 1 s each, 28 rounds | 663,519,232 B, 0 | `false 0` |
| The same GIFs, no `--frame` | 144 × 200, 8 rounds | 1,021,755,392 B, 0 (before: 1,277,190,144 B) | `false 0` |
| 16 MP, 10,000 frames (4 or 6 full frames with disposal 3, then 1 × 1 ones, padded to 16 MiB with a comment), frames 9,999 down, a minute | 18 × 200, then killed | highest sample 1,554,231,296 B | `true 0` (exit 137) |
| The same with 2,000 frames (`MAX_FRAMES` set to 2,000 for the run), a minute | 36 × 200 | 1,401,872,384 B, 0 | `false 0` |
| The same with 2,000 frames, two minutes | 58 × 200, then killed | highest sample 1,507,762,176 B | `true 0` (exit 137) |
| 8 MP (`MAX_FRAME_PIXELS` set to 8,000,000 for the run), 10,000 frames, two minutes | 126 × 200 | 1,608,753,152 B, 0 | `false 0` |

The limit on frames closes what the first runs found: the 600,000-frame GIFs are refused at once, and today's path with them needs a fifth less. It doesn't close the rest. With frames no longer the cost, two workers composing 16 MP canvases one job after another, with 16 MiB bodies queued, still pass 1536 MiB within one or two minutes, whether a GIF has 10,000 frames or 2,000; and at 8 MP the peak ends 2 MiB under the limit. `MAX_FRAMES` stays 10,000: 2,000 only delayed the kill.

**After the cap of 4 MP.** So the cap on the canvas is 4 MP now (2000 × 2000; a 2560 × 1440 screen recording fits), with the server's sentence `… is too large for --frame (at most 4 megapixels and 10,000 frames); img without --frame shows its first frame`. The same load for two minutes, and for comparison today's path without `--frame` on a still image near the 50 MP header limit:

| Run | Answers | `memory.max_usage_in_bytes`, `failcnt` | `OOMKilled RestartCount` |
|---|---|---|---|
| 4 MP, 10,000 frames (2000 × 2000, built as above, 16 MiB), frames 9,999 down, two minutes | 180 × 200, 10 rounds, the slowest 13 s | 1,410,973,696 B, 0 | `false 0` |
| No `--frame`: a 7000 × 7000 PNG (49 MP), 2.1 MB, two minutes | 216 × 200, 12 rounds | 948,813,824 B, 0 | `false 0` |
| No `--frame`: the same PNG padded to 16 MiB with a private chunk, two minutes | 52 × 200, 2 × 504, 3 rounds | 1,413,484,544 B, 0 | `false 0` |

At 4 MP the composing path holds, about 190 MiB under the limit, and with the same bodies today's path on a large still image comes to the same level. Most of that level and its climb from round to round come from two workers and 16 bodies of 16 MiB in the queue, which predate this branch, not from composing.

## Found in hands-on

Fixed on the branch:

- **`help img` named `--frame` before `--crop`** (`img <path> [--size S] [--rotate D] [--frame N] [--crop x,y,w,h]`, and the same in the help's first line), where the design (§1.5) and `docs/concept.md` have `[--crop x,y,w,h] [--frame N]`, the order of the help's paragraphs. Now as the design has it, checked again on the stack.
- **A GIF of many tiny frames got the sidecar killed through `--frame`** at any canvas cap (the memory section). The composing path now takes at most 10,000 frames, counted before `omggif` lists them by `omggif`'s own rules for extensions, and a plain `img` of such a GIF no longer builds `omggif`'s list either.
- **With frames bounded, 16 MP canvases still got it killed under sustained load.** The cap of the composing path is 4 MP now, measured. Spec §2.2 and §2.3 say both, with the new sentence.

Not fixed, for a decision:

- **The sidecar's memory under full queues** stands at about 1.4 GB of its 1536 MiB with today's path alone (a 49 MP still image, 16 MiB bodies), and climbs from round to round. Not from this branch, and it held here; but the margin is small. Candidates, each a design change: fewer bodies in the queue; reusing workers, or limiting what an ended one leaves (`MALLOC_ARENA_MAX`, inconclusive here).

Not reached by hand, covered by tests: the "older analysis" notice of §3.4 (it needs a PDF analysed by an older analyser) and a GIF whose frame count is unknown (one Skia draws and `omggif` refuses).

## Not tested here: a checklist for the maintainer

The client round of the playbook's new steps, which this run can't do, as it has neither the clients nor the private PDFs:

- [ ] In each client (claude.ai, Claude Desktop, Claude Code, ChatGPT, Le Chat, Cursor, VS Code): a sheet photographed sideways, with a fresh code in small type; does the model, after a first look, ask for `--rotate 90` by itself, read a crop off the turned picture, and quote the code exactly?
- [ ] A PDF with a scanned page lying on its side: `pdf … N --images=all --rotate 90`, then a crop read off the picture; does it hit on the first try?
- [ ] An animated GIF: does the model follow the note line to `--frame N`, and say which frame it saw?
- [ ] One of the real private PDFs: `rg --regions` for a figure named in the text, then `pdf … --crop` with the numbers it gave; does the crop show the line, and does the model widen it for the rest of a table row?
- [ ] On the server after the release: both images from the same tag (`KUMBU_IMAGE` and `KUMBU_PDF_IMAGE`); the echo of §1.3 refuses a picture from an older sidecar, but only the same tag gives the new options.

## Regions with labels: the attempt

**Verdict.** Regions with labels are not reliable enough: on held-out figures they passed 84.4 % (seeds 101–110) and 83.3 % (seeds 201–210) against a bar of 90 %, with Calc at 65.6 % and 60.0 % against 80 %, so they are left out.

The attempt widened the regions of `pdf info` and `rg --regions` to take in a figure's axis labels, tick labels and legend. It was measured on generated figures (`tools/attachment-probes/make_figure_pdfs.py`, `pnpm -C pdf eval:regions`) with a bar of 90 % overall, 80 % per generator and 70 % per layout, and with the page types, the tight regions and the body lines held to the baseline. Its box could also take in neighbouring drawing regions of the same chart (grouped bars split into tight regions per group). On seeds 1–5, which the tuning saw, it went from 48.9 % (first version) to 87.4 % (after round 2); the held-out rounds, on seeds the tuning never saw, are the verdict.

### Held-out round 1: seeds 101–110, rules at 0c6309a

270 figures, judged by labelled box 243, by tight box 1, with no region 26.

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

| failures by kind | matplotlib | calc | pdf-lib | all |
|---|---|---|---|---|
| no region | 0 | 23 | 3 | 26 |
| drawing outside | 0 | 0 | 0 | 0 |
| label outside | 3 | 2 | 6 | 11 |
| label missing | 0 | 0 | 0 | 0 |
| too few tick labels | 0 | 0 | 0 | 0 |
| body text inside | 0 | 0 | 0 | 0 |
| other figure inside | 1 | 10 | 2 | 13 |

Not met: overall 90 % (84.4 %), each generator 80 % (Calc 65.6 %), each layout 70 % (lowest side-by-side 65.0 %). Met: at least 60 figures, page types and tight regions as the baseline, no body line in a region with labels.

### Held-out round 2: seeds 201–210, rules at 8dfdddb

270 figures, judged by labelled box 239, by tight box 1, with no region 30. One tuning round was allowed between the two, chosen from the failures on seeds 1–5 only.

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

| failures by kind | matplotlib | calc | pdf-lib | all |
|---|---|---|---|---|
| no region | 0 | 26 | 4 | 30 |
| drawing outside | 0 | 0 | 0 | 0 |
| label outside | 2 | 3 | 3 | 8 |
| label missing | 0 | 0 | 0 | 0 |
| too few tick labels | 0 | 0 | 0 | 0 |
| body text inside | 0 | 0 | 0 | 0 |
| other figure inside | 2 | 11 | 2 | 15 |

Not met: overall 90 % (83.3 %), each generator 80 % (Calc 60.0 %), each layout 70 % (lowest side-by-side 60.0 %). Met: the same two as in round 1. Nothing was changed after this run: no third look, no bar, seed set or corpus.

### Constants at the end

`LABEL_CHARS` 30; `LABEL_GAP_PT` 40 (12 at the first version); `ROW_GAP_PT` 3 (new in round 2); `MAX_LABELLED` 5,000; `RULES_VERSION` 2. Unchanged for rules 1: `THIN_PT` 2, `MERGE_PT` 6, `MIN_FIGURE` 0.01, `FULL_PAGE_IMAGE` 0.85, `MIXED` 0.25, `GRID` 200, `MAX_CLUSTERED` 20,000. The large gap kept no body text out; the blockers did (no body line in any region, controls and fixtures included).

### What limited it

- **Figures with no region at all** (26 and 30 of 270; 23 and 26 of them Calc, which is 26 % and 29 % of its 90 figures). Rules 1 finds nothing there, tight regions may not change, and a rule that only widens existing regions has nothing to widen. On seeds 1–5 this capped Calc at 73.3 %, and no tuning could move it.
- **Neighbour assignment on side-by-side pages** ("other figure inside": 13 and 15, mostly Calc). A chart whose partner has no region takes in the partner's labels, and nothing in position and character count tells the two apart. Side-by-side was the lowest layout in both rounds (65.0 % and 60.0 %).
- **Labels beyond reach** ("label outside": 11 and 8), for example a legend further from its plot than the gap, and merged rows of category labels, which are one item of 30 or more characters and so look like body text to the blockers.

### The attempt's commits

- Stage A, stays: `0d15f64` Measure regions with a corpus of generated figures; `8c0395e` (the baseline guard and the sizes side by side).
- Stage B: `2b72e26` Try regions that take in a figure's labels and legend.
- Stage C: `3b9dd06` Measure a label's nearness to the other charts' own regions; `b4de89a` Store regions with no labelled box in their old shape (review fixes); `0c6309a` Tune the growth of labelled regions (round 1: `LABEL_GAP_PT` 12 to 40); `8dfdddb` Tune the growth of labelled regions (round 2: `ROW_GAP_PT` 3).
- Reverted by five revert commits, `3416997` to `8f8e29d`. `RULES_VERSION` stays 1, `PdfAnalyses.Analyser` keeps `+rules.1`, and `help pdf` keeps its present sentence. The script, the eval and the corpus stay.

### Where a later attempt would start

The eval and the corpus are still there: `uv run tools/attachment-probes/make_figure_pdfs.py <dir> --seed N` and `pnpm -C pdf eval:regions <dir> --seeds A-B`, with seeds 1–5 for tuning and fresh seeds for each verdict. The first problem is finding a region for Calc charts under rules 1, before any rule about labels: the figures that have none. After that, telling a chart's own labels from a neighbour's on side-by-side pages, and merged rows of category labels from body lines.

This measures geometry on generated pages, not whether a model reads a chart better, and not the maintainer's real PDFs; a first real PDF that disagrees would have outweighed the table either way.
