# Test notes: attachments, part 2 (the model reads attachments), hands-on

Date: 2026-10-29. Hands-on test of `img` and `pdf` (spec [2026-10-28-attachments-model-reading-design](superpowers/specs/2026-10-28-attachments-model-reading-design.md), plan [2026-10-28-attachments-model-reading](superpowers/plans/2026-10-28-attachments-model-reading.md)) by Claude, on the branch `attachments-model-reading`, before the pull request.

Set-up: a compose project of its own (`docker compose -p kumbu-a2 -f compose.yaml -f compose.monitoring.yaml up -d`), the app on `127.0.0.1:18081`, Grafana on `127.0.0.1:13001`, a dev token, search off (the sandbox's containers can't download the model). Both images were built from host-built output on their real base images (`mcr.microsoft.com/dotnet/aspnet:10.0` for the server, `node:22-bookworm-slim` for the sidecar), because the sandbox's containers can't reach the package registries; the repository's Dockerfiles were not changed and are built by CI. The sidecar ran with the hardening of `compose.yaml` (read-only, no capabilities, its internal network). The agent side was MCP over plain HTTP (stateless `tools/call` requests); every image delivered was decoded from the answer and looked at.

Fixtures, made by a script (no real documents): `paper.pdf`, three pages, page 2 with two charts side by side (bars and a line chart, each about 11 % of the page) between paragraphs; `scan.pdf`, one page that is a single full-page JPEG with no text layer; `screen.png`, a 1440 × 3120 "screenshot" with a red box reading "CODE 482913"; `photo.jpg`, 4000 × 3000. Uploaded with `PUT /api/files/…` (201 each).

## Checks

| Suite | Command | Result |
|---|---|---|
| Server and core | `dotnet test --solution Kumbu.slnx` | 2521 tests: 2515 passed, 6 skipped (the model tests and a TensorX key), 0 failed |
| PDF sidecar | `pnpm -C pdf test` (type check, then Vitest), `pnpm -C pdf build` | 6 files, 77 tests passed; type check and build clean |
| Web app | `pnpm -C web test --run`, `pnpm -C web build` | 72 files, 1079 tests passed; built |
| Compose files | `docker compose … config -q` with the build, Traefik and monitoring files | valid in every combination |

## What I did and saw

| Call | Result |
|---|---|
| `ls /Demo/Papers` | the four files with kind and size (`photo.jpg  image 4000×3000 3.5 MB`) |
| `cat paper.pdf` | `paper.pdf: PDF, 3 pages (1-3 text), 5 KB — read it with pdf (help pdf)` |
| `pdf info scan.pdf` | `Pages: 1 image`, `p1 image: one image covers the page, no text layer — regions (pt, x,y,w,h): image 0,0,595,842` |
| `pdf scan.pdf` | only the header `--- Page 1 of 1: image (no text layer; one image covers the page) — add --images to see it ---`; no image sent unasked |
| `pdf scan.pdf --images` | the page at `medium`: 920 × 1303 PNG, 119 KB, with the text block naming file, version, page, sizes and the "if no image follows, your client dropped it" line |
| `pdf paper.pdf 2` | the page's text, under a header for a text page (see the first finding) |
| `pdf paper.pdf 2 --crop 315,192,220,250 --size large` | the line chart alone, re-rendered at 1804 × 2050 px (the 600 dpi cap), PNG 62 KB, sharp |
| `img screen.png --size small` / medium / `--size large` | 354 × 768 (84 KB), 723 × 1568 (181 KB), 1188 × 2576 (415 KB), all PNG |
| `img screen.png --crop 880,1280,440,240` | 440 × 240 PNG; the code "482913" is readable in it |
| `img photo.jpg` | 1264 × 948 JPEG, 395 KB (under the 400 KB target for medium) |
| `img paper.pdf` | refused, pointing to `pdf … --images` |
| `img screen.png \| head` | refused: `img shows images and can't be part of a pipeline` |
| `rg -i -m 2 yield /Demo/Papers` | `/Demo/Papers/paper.pdf:p1: …` lines |
| Sidecar stopped | `img` → "image rendering is unavailable right now"; a PDF uploaded meanwhile → "hasn't been analysed yet …"; `--images=all` → "PDF rendering is unavailable right now"; the stored text of an analysed PDF still readable; `cat` says "not analysed yet" |
| Sidecar started again | the queued analysis ran within the next round (about a minute); `pdf info` then showed the pages |
| From inside the sidecar's container (after the cross-package review's fix) | `fetch` of `kumbu:8080/healthz`, `kumbu:8080/oauth/authorize?client_id=https://…` and `kumbu:9464/metrics`: 403 each; the server meanwhile used the sidecar normally (`img … --size small` delivered 354 × 768) |
| Prometheus | pages analysed by type (6 text, 1 image), `pdf` calls by mode, `img` calls by step, refusals and render failures by reason, and one render of a page classified `text` through `--crop` — the threshold's miss signal, from the chart crop above |

## Found in hands-on

Fixed on the branch:

- **A text page holding two charts got no nudge.** The two charts of `paper.pdf` page 2 cover about 22 % together, under the 25 % threshold, so the page is `text`; its header said `text (small drawing ≈ 22 %)` with no pointer to `--images`, and `pdf info` listed no regions for it. Text pages whose figures cover 5 % or more now say so (`--- Page 2 of 3: text (vector drawing ≈ 22 % of the page) — add --images=all or --crop to see it ---`, checked again on the stack), and `pdf info` lists their regions.

Not fixed, for issues:

- **The 25 % threshold misses side-by-side charts of moderate size.** Two charts of about 11 % each are a common layout in papers. Whether to lower the threshold, or count clusters rather than their sum, is a question for the metrics (`kumbu_pdf_text_pages_rendered_total` counts every look at a text page through `--images=all` or `--crop`).
- **The web app's search (REST `/api/search`) still finds notes only**; agents find PDF pages with `semsearch`.
- **PDF pages get no word bonus in `semsearch`'s hybrid ranking** (their text isn't loaded until display), so a page can rank below a note of similar meaning that contains the query's words; `--words` searches notes only. `help semsearch` says so.
- **`:latest` of the two images is not published atomically**: if one image's job fails on a release tag, the other's `:latest` moves alone. Tolerated (the server re-analyses only when its own analyser version changes), but a publish job that tags both digests together would close it.
- **The real `pdf/Dockerfile` build stage has not run here** (the sandbox's containers can't reach the npm registry); CI builds it on the pull request.
- **A staged store of an analysis is not atomic across two server processes** (one process per deployment is the design).

## Not tested here: a checklist for Chris

The client test of [`docs/attachments-client-research.md`](attachments-client-research.md) §6: does the model really see the image?

- [ ] Make an image with a fresh random six-digit code drawn into it (nowhere else: not in the name, the note or any metadata), upload it to a vault, and in each client ask the model, through Kumbu², for the code: claude.ai, Claude Desktop, Claude Code, ChatGPT (connector and Code Mode), Le Chat, Cursor, VS Code. Pass = the exact code. Note per client: read correctly, refused, or the result rejected, truncated or written to a file.
- [ ] The size steps, with the same picture: `img <path> --size small`, `medium`, `large` (about 150 KB, 400 KB and 1 MB targets), and a JPEG photo at about 50 KB, 200 KB and 1 MB; on Claude Code also `pdf <path> 1-4 --images=all` (four images in one result). The step at which a client fails tells whether it counts base64 characters or visual tokens; the steps' constants live in `src/Kumbu.Core/Attachments/SizeSteps.cs`.
- [ ] One of your partner's real papers with charts: `pdf info`, then `pdf … --images` and a `--crop` of one chart; does the model read the values off the chart?
- [ ] A scanned document (a phone scan of a letter): `pdf … --images`; the page is seen, not found by search.
- [ ] On the server after the release: both images from the same tag (`KUMBU_IMAGE` and `KUMBU_PDF_IMAGE`), the `kumbukumbu-pdf` package public on GHCR, and the dashboard's Attachments row filling.
