# Teaser Page Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A "coming soon" page at `teaser.kumbu.tidesson.net` now, and the full teaser as an unlisted `internal-preview.html`, both published from `site/` by GitHub Pages.

**Architecture:** Static files in `site/`: two HTML pages sharing one stylesheet, self-hosted fonts, a small script for the preview's flowers and sheets. A workflow deploys `site/` to Pages. No framework, no build step.

**Tech Stack:** HTML, CSS, vanilla JavaScript, inline SVG; GitHub Actions (`actions/configure-pages`, `upload-pages-artifact`, `deploy-pages`, pinned by SHA like the other workflows).

**Spec:** `docs/superpowers/specs/2026-10-26-teaser-page-design.md`; look and motion: `docs/superpowers/specs/2026-10-26-teaser-sketch.html`.

## Global Constraints

- British English; no content specific to one organisation; the worldbuilding project is never named; no personal names.
- No request leaves the page's own origin except the user clicking an outside link: no Google Fonts, no CDN, no analytics, no cookies.
- Every outside link: `target="_blank" rel="noopener"`.
- Palette "Meadow" and type sizes exactly as in the sketch; light only.
- `prefers-reduced-motion: reduce` shows the final state at once.
- No horizontal scrolling at 360 px width.
- `internal-preview.html`: `noindex, nofollow`, disallowed in `robots.txt`, linked from nowhere.
- The server image is unchanged: `site/` in `.dockerignore`.

## Review Focus

- Phone at 360 px: four flower labels readable (not scaled below ~16 px), no overlap, no sideways scroll.
- Keyboard only: Tab reaches each flower and card, Enter/Space opens, Escape closes, focus returns to the card.
- Opening a sheet near the bottom of a long section on a short phone screen: the sheet fits the viewport and scrolls inside.
- Clicking a second card while a sheet is animating closed: no stuck veil, no two sheets.
- Fonts fail to load: text still renders in fallback fonts with the same layout roughly intact.

---

### Task 1: The coming-soon page, fonts and deployment

**Files:**
- Create: `site/index.html`, `site/assets/teaser.css`, `site/assets/fonts/*.woff2`, `site/assets/fonts/OFL-*.txt`, `site/CNAME`, `site/robots.txt`, `.github/workflows/pages.yml`
- Modify: `.dockerignore` (add `site/`)

- [ ] **Step 1:** Fetch latin-subset `woff2` files for Caveat (500, 700), Fraunces (400, 600; the `opsz` axis not needed) and Nunito (400, 600), e.g. from `@fontsource/*` on jsDelivr, with each family's OFL licence text. Declare them with `@font-face` (`font-display: swap`) in `teaser.css`, with system fallbacks.
- [ ] **Step 2:** Move the sketch's shared styles (tokens, paper, pencil classes, title, tagline, links, keyframes, reduced motion) into `teaser.css`.
- [ ] **Step 3:** `index.html`: title "Kumbu²" (superscript 2), the subtitle from the spec, an SVG with the pencil ground line and one sprout growing into a bud with the handwritten "coming soon", the two footer links. `<title>Kumbu² — coming soon</title>`, a meta description, `lang="en"`.
- [ ] **Step 4:** `CNAME` = `teaser.kumbu.tidesson.net`; `robots.txt` allows all, disallows `/internal-preview.html`.
- [ ] **Step 5:** `pages.yml`: on push to `master` with `paths: [site/**, .github/workflows/pages.yml]` and `workflow_dispatch`; permissions `pages: write`, `id-token: write`, `contents: read`; concurrency group `pages`; checkout → configure-pages → upload-pages-artifact (`path: site`) → deploy-pages, each pinned to a full commit SHA with the version as a comment.
- [ ] **Step 6: Verify.** `rg -n 'https?://' site --glob '!*.txt'` shows only the outside links (GitHub, scai.world and, later, the ones the spec names) and SVG namespaces. Screenshot with `chromium --headless --screenshot --window-size=1280,900` and `--window-size=360,780` after the animation (`--virtual-time-budget=6000`), and look at both.
- [ ] **Step 7: Commit** "Add the coming-soon teaser page and its Pages workflow".

### Task 2: The full page as an unlisted preview

**Files:**
- Create: `site/internal-preview.html`, `site/assets/teaser.js`
- Modify: `site/assets/teaser.css`

- [ ] **Step 1:** `internal-preview.html` with the robots meta, the garden of four flowers (where it hurts · how Kumbu² helps · how it works · run your own), one `<template>` per section, the closing line with "ask us" → https://scai.world, the footer.
- [ ] **Step 2:** Write the wording for every card, short line and sheet text, from the spec's "Structure and content". Check each claim against `README.md`, `docs/superpowers/specs/2026-10-26-obsidian-sync-design.md` and `docs/superpowers/specs/2026-10-26-demo-round-design.md`; the deployer section links `compose.yaml`, `.env.example`, `LICENSE` and `docs/security-architecture.md` on GitHub, and the Obsidian card links `https://github.com/SCAI-NGO/kumbu-obsidian`.
- [ ] **Step 3:** `teaser.js`: flowers (click and Enter/Space, `aria-expanded`, `aria-controls`), the FLIP sheet from the sketch with `role="dialog"`, `aria-modal`, focus moved into the sheet and back to the card on close, a guard so a second open waits until a close has finished.
- [ ] **Step 4:** Phone layout below 640 px: the garden as 2 × 2 (two SVG rows, or a different viewBox), labels at least 16 px rendered.
- [ ] **Step 5: Verify** with headless screenshots at 1280 and 360, a section open, and a sheet open (via a `#open=<section>/<n>` debug hash or a small Playwright-free script with `--remote-debugging`; the builder chooses), plus the Review Focus list by hand.
- [ ] **Step 6: Commit** "Add the full teaser as an unlisted preview".

### Task 3: Social preview and the branch's review

**Files:**
- Create: `site/assets/og.png` (1200 × 630, rendered from an SVG still of the garden with `rsvg-convert`), the SVG source next to it
- Modify: both HTML pages (Open Graph and Twitter card meta, absolute URL `https://teaser.kumbu.tidesson.net/assets/og.png`)

- [ ] **Step 1:** Draw the still, render it, add the meta.
- [ ] **Step 2:** Whole-branch review against the spec and the Global Constraints.
- [ ] **Step 3: Commit**, push the branch `teaser-page`, open a PR listing what Chris has to do: Settings → Pages → Source "GitHub Actions"; DNS `teaser.kumbu CNAME scai-ngo.github.io`; "Enforce HTTPS" once the certificate is there.
