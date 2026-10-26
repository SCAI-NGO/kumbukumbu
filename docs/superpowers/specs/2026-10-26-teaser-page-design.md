# The Kumbu² Teaser Page — Design

**Date:** 2026-10-26 · **Status:** approved style sketch, wording to be written
**Sketch:** `docs/superpowers/specs/2026-10-26-teaser-sketch.html` (open it in a browser; it is the binding reference for look and motion, its texts are placeholders)

## Intent

A public page that introduces Kumbu² to people who have never heard of it, at `https://teaser.kumbu.tidesson.net`, linked from SCAI's site (https://scai.world, where the project is being announced).

SCAI's software falls into two groups. User-oriented projects such as Chatsundere (https://teaser.chatsundere.me) are loud and nerdy on purpose. Infrastructure projects (Hecate, Okeanos, Kumbu²) are serious without being stiff, with a wink, and fit for a business that isn't stuck in the 1970s. This page belongs to the second group and goes further: it should feel friendly and inviting to people outside the nerd bubble. Its message is "hi, I'm Kumbu², a friendly, safe place that wants to help you think".

## Two stages

The full page describes v0.2.0 (Obsidian sync, install as an app, export, seeing what the AI changed), which isn't released yet. So it goes out in two stages:

1. **Now: a "coming soon" page** at `index.html`. The page's top part only: the title "Kumbu²", the subtitle "One calm place for your notes — on every device, and for every AI you think with.", one small sprout that grows from a pencil ground line into a bud with a handwritten "coming soon", and the footer links. Same look and motion as the sketch, nothing to click but the links.
2. **Now, alongside: the full page as an unlisted preview** at `internal-preview.html`, for internal review before launch. It is linked from nowhere and carries `<meta name="robots" content="noindex, nofollow">`; `robots.txt` disallows it. It is *unlisted, not secret*: the repository is public, so anyone who looks at `site/` finds it. Nothing on it may be confidential.
3. **After v0.2.0 is tagged: launch.** A release check, like the welcome vault's, removes every card describing a feature that didn't make the release and checks every claim against the released code; then the full page becomes `index.html`, and `internal-preview.html` goes.

The page starts from moments the reader knows, not from features. Use cases and pain points come first; what Kumbu² does and how follow; the technology waits in its own corner for those who want it.

## Who reads it

- **Users.** About 4 of 10 on both scales, general computer use and AI use. They know notes are files in folders and keep things in order on purpose (note-takers, idea collectors, Zettelkasten people). They use AI for real work, not single questions, and know the "my AI knows nothing about my stuff" problem first-hand. Connecting an app to claude.ai today is "paste an address, sign in, Allow", which this group can do.
- **Deployers.** About 7 of 10: Docker, Compose, a VPS. They want licence, setup and architecture, and the technical terms are welcome in their section.

## Goals

- A reader recognises at least one of their own moments within seconds of opening a section.
- A reader understands that a Kumbu² vault is *theirs* and shared across every app and every AI they connect, not one more assistant's private memory.
- A user never has to read MCP, OAuth or "encrypted at rest" to understand what Kumbu² does for them.
- A deployer finds licence, setup and architecture in one place.

## Anti-goals (the page failed if…)

- …someone says "but Claude already has memory and projects". Answer this head-on in the first section (below).
- …someone says "too complicated for me" because jargon came too early.
- …it promises what isn't tested, e.g. that a particular open-weights model works well. Say "any app that speaks MCP", not "works great with model X".
- …it names the author's personal projects. The example is "a worldbuilding project", never by name.
- …it is COR-specific in any way (`kumbu-open-audience`: SCAI builds for an open audience).
- …it tracks visitors or leaks their address to third parties.

## Non-goals

- Screenshots or real UI. The page stays drawn, on purpose, for now.
- Languages other than English.
- A sign-up, a try-out instance or a contact form. The way in for users is the closing line below.

## Look and feel (decided)

The sketch fixes these; match it.

- **Palette "Meadow"**, light and pastel: paper `#fbf8f2` with a faint dot grain, ink `#4a4553`, graphite `#6d6676`, soft `#8a8394`; sage `#cfe3c9`/`#9cc293`, peach `#f8d5c2`/`#eaa98a`, lavender `#dcd3f0`/`#b2a3dc`, butter `#f6e3b4`/`#e2c27a`, stems `#8fae86`. Light only; no dark mode.
- **Type:** Fraunces for headings, Nunito for text, Caveat for the handwritten notes and flower labels. Sizes as in the sketch's v2 step (body 19.5 px), which Chris asked for after the first sketch was too small.
- **Pencil feel**, subtle: a light SVG displacement filter on drawn lines, "wobbly" hand-drawn borders (the asymmetric `border-radius` trick), dashed borders on cards. Organic, never overdone.
- **It grows like flowers.** On load: the title rises, the subtitle, then a pencil ground line draws itself, stems grow one after another, leaves unfold, blossoms open with a little overshoot and sway gently, then "pick a flower ↓" and the footer links appear. About 4 seconds in all. `prefers-reduced-motion` shows everything at once.
- **Half-interactive.** The page builds itself, then the reader chooses what to see by clicking a flower. The chosen flower's petals open a bit wider, and its section appears below the garden.
- **Cards** in each section show a short line. Hover lifts and tilts a card slightly. A click grows it, from where it sits, into a centred sheet over a milky, blurred veil (at most 600 px wide, always within the screen, scrolling inside if long), with the longer text. A click on the sheet or the veil, "close ✕" or Escape shrinks it back to its spot. Links inside the sheet work without closing it.
- **Every outside link opens in a new tab** (`target="_blank" rel="noopener"`).
- **Closing line for users**, above the footer, in the page's own voice: "Kumbu² runs on servers people host for their friends, teams and communities — ask yours, or ask us.", with "ask us" linking to https://scai.world (where the contacts are).
- **Footer:** two handwritten links with a highlighter underline, "the code on GitHub" → https://github.com/SCAI-NGO/kumbukumbu and "made by SCAI" → https://scai.world.

## Structure and content

Four flowers, left to right. Each section has a heading, a handwritten subline and cards; each card has a short line and a longer text for the sheet. The wording is the builder's to draft from what follows, in plain British English, warm and concrete; Chris reviews it.

1. **where it hurts.** The reader's moments:
   - Notes out of reach on the go: on the train, on the phone; the files sit on a server or in a cloud drive whose web view is a pain to read.
   - Copy, paste, explain, again: every chat starts with pasting the same notes in.
   - The AI writes, you tidy up: it hands you text blocks instead of updating the note, the way a coding agent edits the docs before it writes the plan.
   - Every tool remembers a little: different tools for different jobs, and none of them share what they know. Example: what Claude Code worked out this morning is unknown to the chat on your phone tonight.
   - **The objection, as its own card:** "But doesn't my AI already remember things?" It does, for itself. A Kumbu² vault is yours, and every app and every AI you invite sees the same one.
2. **how Kumbu² helps.** In user words, no protocol names:
   - Open your notes anywhere: a web app on phone and laptop, installable like an app.
   - Your AI works right inside: add Kumbu² to your assistant once; it reads, searches and edits your notes, and you see what it changed.
   - Keep using Obsidian: pair with a short code, synced both ways.
   - Safe by default: stored encrypted, every change can be undone, you decide which vault each app may see.
   - It becomes a memory palace: a capable model finds its way around a well-kept vault and files things sensibly, so the vault grows into a shared memory you and your AI both use.
3. **how it works.** Still for users:
   - Each vault is a folder of notes, laid out the way Obsidian lays out a vault: Markdown, folders, links, headings. Export as a ZIP and Obsidian opens it as it is.
   - The Obsidian plugin syncs a vault both ways, on laptop and phone; conflicts lose nothing. The binding description is `docs/superpowers/specs/2026-10-26-obsidian-sync-design.md` (not the superseded `docs/obsidian-plugin-design-spec.md`): at the first sync, and only when there are conflicts, the user chooses keep both, keep local or keep Kumbu²; later conflicts keep both.
   - How to install the plugin is not explained on the page (it goes through BRAT for now, which is jargon for this audience): the card links to the `kumbu-obsidian` repository's README, which explains it. *(My call.)*
   - An editor for on the go: the web app, when there's no Obsidian at hand.
   - Any AI that speaks MCP: Claude, Claude Code, Hermes Agent (https://hermes-agent.nousresearch.com), Chatsundere (https://teaser.chatsundere.me) and any other app that connects AI to tools through MCP. "MCP" may appear here once, explained in the same breath.
4. **run your own.** For deployers; technical terms welcome:
   - Free software: AGPL-3.0-or-later, what it means in one or two sentences (use, change, host; share your changes if you offer it to others over a network).
   - Setup: the repository's `compose.yaml` (it pulls the published image) and a `.env` from `.env.example`, then `docker compose up`; link both on GitHub. The reference install with Komodo took about five minutes. No separate example file.
   - Under the hood: per vault an Obsidian-compatible virtual file system; .NET 10, MongoDB, AES-256-GCM at rest, Argon2id, an MCP server with OAuth; link to `docs/security-architecture.md` on GitHub.

Check every claim against the code when writing it; the page must not promise a feature the released version lacks.

## Hosting (my calls, Chris may change them)

- The page lives in `site/` in the `kumbukumbu` repo, next to the docs it describes: one `index.html`, its CSS and JS, the fonts. Plain HTML, CSS and a little JavaScript; no framework, no build step.
- A GitHub Actions workflow publishes `site/` to GitHub Pages on a push to `master` that touches `site/`.
- Custom domain `teaser.kumbu.tidesson.net` via a `site/CNAME` file; Chris adds the DNS record `teaser.kumbu CNAME scai-ngo.github.io` and turns on "Enforce HTTPS".
- **Fonts are self-hosted** (all three are under the SIL Open Font Licence), not loaded from Google Fonts: Google Fonts sends every visitor's address to Google, which European courts have ruled unlawful without consent, and the page's own point is privacy. Ship the licence texts alongside.
- No analytics, no cookies, no third-party requests at all.
- `.dockerignore` excludes `site/`, so the server image doesn't change.

## Further requirements

- **Phones.** Most first visits come from a link on a phone. Four flowers side by side at 360 px make the labels too small; the garden must stay readable there (for example 2 × 2, or labels below the blossoms). The builder chooses; no horizontal scrolling.
- **Accessibility.** Flowers and cards work by keyboard (Tab, Enter/Space, Escape) with a visible focus; the sheet takes focus while open and returns it on close; text contrast on the pastels meets WCAG AA.
- **A social preview:** `<title>`, description and an Open Graph image (a drawn still of the garden), so the link looks inviting when shared.

## Left to the builder

The exact wording (Chris reviews it); the layout on phones; the drawing of the flowers, leaves and the Open Graph image, within the sketch's style; file layout inside `site/`; the workflow details.
