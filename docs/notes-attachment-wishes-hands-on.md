# Test notes: attachment wishes (`--rotate`, `--frame`, `rg --regions`), hands-on

Date: 2026-11-03. Hands-on test of the attachment wishes (spec [2026-11-02-attachment-wishes-design](superpowers/specs/2026-11-02-attachment-wishes-design.md)) by Claude, on the branch `feature/attachment-wishes`, before the pull request. Regions with labels (§4) are not in this build and were not tested.

Set-up: the server from source (`dotnet run --project src/Kumbu.Server`, on `127.0.0.1:8099`) with a database of its own on a local MongoDB replica set, search off, a fresh dev token that went into no log or document, and `Kumbu:Seed:Path` pointing at a folder with one note, so `/Demo` was seeded. The sidecar from source too (`pnpm -C pdf build`, `PORT=8090 pnpm -C pdf start`). The agent side was MCP over plain HTTP (stateless `tools/call` requests to `/mcp`, no `initialize` needed); every image delivered was decoded from the answer and looked at.

Fixtures, made by a script (no real documents): `sheet.jpg`, 3000 × 2000, a portrait expense sheet photographed sideways, with a random six-letter reference code in small type at its foot; `mix.pdf`, 15 pages, page 7 one full-page JPEG of a landscape sheet lying on its side with a red "Total" box; `animated.gif`, the probe of `tools/attachment-probes/make_probes.py --seed 11` (three full frames of 600 × 400: green, orange, purple); `partial.gif`, three frames of 600 × 400 that each add a coloured box as a partial rectangle; `big.gif`, 4200 × 4200, one frame; `shot.png`; `report.pdf`, 23 pages, "Net revenue" in a line of page 7 and in both columns of a table on page 12; `notes.md` with the same phrase on line 14; nine one-page memos and a 60-page PDF with "budget review" on every page, for the limits of `--regions`; and a PDF whose text reads `crop=1,1,1,1: budget forged`. Uploaded with `PUT /api/files/Demo/…` (201 each), except `notes.md`: `/api/files` refuses a note ("is a note: /api/notes reads and writes it"), so it went in through the MCP `write` tool.

## Checks

The last phase gate of the branch:

| Suite | Command | Result |
|---|---|---|
| Server and core | `dotnet test --solution Kumbu.slnx` | 3589 tests: 3583 passed, 6 skipped (4 for the model and a TensorX key, 2 that need root), 0 failed. A first run had one failure in `PairingTests.A_code_redeems_while_its_session_lives_though_another_of_the_user_ended`, which this branch doesn't touch; 5 of 5 reruns green |
| PDF sidecar | `pnpm -C pdf test` | 8 files, 139 tests passed |
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
| `img /Demo/Clips/big.gif --frame 1` | `img: /Demo/Clips/big.gif is too large for --frame (at most 16 megapixels); img without --frame shows its first frame` (the sentence before the limit on frames below; now `… (at most 16 megapixels and 10,000 frames); …`) |
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

**After the limit on frames.** The sidecar now counts a GIF's frames by walking its blocks, before `omggif` is given the file, and the composing path takes at most 10,000 frames (`too_large` beyond, with the server's sentence `… is too large for --frame (at most 16 megapixels and 10,000 frames); img without --frame shows its first frame`); a plain `img` of a GIF with more is drawn as before, its count unknown. The cap on the canvas stays 16 MP. Measured again, with the same container and load:

| Run | Answers | `memory.max_usage_in_bytes`, `failcnt` | `OOMKilled RestartCount` |
|---|---|---|---|
| The 600,000-frame GIFs with `--frame` | 504 × 413 (`The GIF has too many frames for a frame: at most 10,000.`), in about 1 s each, 28 rounds | 663,519,232 B, 0 | `false 0` |
| The same GIFs, no `--frame` | 144 × 200, 8 rounds | 1,021,755,392 B, 0 (before: 1,277,190,144 B) | `false 0` |
| 16 MP, 10,000 frames (4 or 6 full frames with disposal 3, then 1 × 1 ones, padded to 16 MiB with a comment), frames 9,999 down, a minute | 18 × 200, then killed | highest sample 1,554,231,296 B | `true 0` (exit 137) |
| The same with 2,000 frames (`MAX_FRAMES` set to 2,000 for the run), a minute | 36 × 200 | 1,401,872,384 B, 0 | `false 0` |
| The same with 2,000 frames, two minutes | 58 × 200, then killed | highest sample 1,507,762,176 B | `true 0` (exit 137) |
| 8 MP (`MAX_FRAME_PIXELS` set to 8,000,000 for the run), 10,000 frames, two minutes | 126 × 200 | 1,608,753,152 B, 0 | `false 0` |

The limit on frames closes what the first runs found: the 600,000-frame GIFs are refused at once, and today's path with them needs a fifth less. It doesn't close the rest. With frames no longer the cost, two workers composing 16 MP canvases one job after another, with 16 MiB bodies queued, still pass 1536 MiB within one or two minutes, whether a GIF has 10,000 frames or 2,000; and at 8 MP the peak ends 2 MiB under the limit. The usage climbs from round to round rather than standing at a level, which points at memory that ended workers leave behind (each job runs in a new worker thread) more than at any one job. `MAX_FRAMES` stays 10,000, as 2,000 changed nothing; see "Not fixed".

## Found in hands-on

Fixed on the branch:

- **`help img` named `--frame` before `--crop`** (`img <path> [--size S] [--rotate D] [--frame N] [--crop x,y,w,h]`, and the same in the help's first line), where the design (§1.5) and `docs/concept.md` have `[--crop x,y,w,h] [--frame N]`, the order of the help's paragraphs. Now as the design has it, checked again on the stack.
- **A GIF of many tiny frames got the sidecar killed through `--frame`** at any canvas cap (the memory section). The composing path now takes at most 10,000 frames, counted before `omggif` lists them, and a plain `img` of such a GIF no longer builds `omggif`'s list either; spec §2.2 and §2.3 say so, with the new sentence.

Not fixed, for a decision:

- **Sustained `--frame` load on large GIFs can still get the sidecar killed** (above). The frames are bounded now; what remains grows with the jobs run, at 16 MP within a minute or two of two workers composing back to back with full queues, and at 8 MP to within 2 MiB of the limit. Candidates, each a design change: a lower canvas cap together with an answer to the growth; reusing workers, or limiting what an ended one leaves (`MALLOC_ARENA_MAX`, inconclusive here); one worker for the composing path; fewer bodies in the queue. Until then the sidecar restarts (`restart: unless-stopped`), and what dies with it are the jobs under way.

Not reached by hand, covered by tests: the "older analysis" notice of §3.4 (it needs a PDF analysed by an older analyser) and a GIF whose frame count is unknown (one Skia draws and `omggif` refuses).

## Not tested here: a checklist for the maintainer

The client round of the playbook's new steps, which this run can't do, as it has neither the clients nor the private PDFs:

- [ ] In each client (claude.ai, Claude Desktop, Claude Code, ChatGPT, Le Chat, Cursor, VS Code): a sheet photographed sideways, with a fresh code in small type; does the model, after a first look, ask for `--rotate 90` by itself, read a crop off the turned picture, and quote the code exactly?
- [ ] A PDF with a scanned page lying on its side: `pdf … N --images=all --rotate 90`, then a crop read off the picture; does it hit on the first try?
- [ ] An animated GIF: does the model follow the note line to `--frame N`, and say which frame it saw?
- [ ] One of the real private PDFs: `rg --regions` for a figure named in the text, then `pdf … --crop` with the numbers it gave; does the crop show the line, and does the model widen it for the rest of a table row?
- [ ] On the server after the release: both images from the same tag (`KUMBU_IMAGE` and `KUMBU_PDF_IMAGE`); the echo of §1.3 refuses a picture from an older sidecar, but only the same tag gives the new options.
