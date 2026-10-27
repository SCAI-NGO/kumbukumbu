# Markdown Links — Design

**Date:** 2026-10-26 · **Status:** agreed in chat · **Release:** v2.5 (0.2.5), built ahead on a branch; not merged before the demo of 2026-10-27

## Intent

Kumbu² understands only `[[wikilinks]]` (`src/Kumbu.Core/Links/WikiLink.cs`). Standard Markdown links, `[text](target)`, are invisible to `links` and `backlinks`, and `mv` breaks them silently. They arrive from every direction: people who write them by hand, Obsidian with "Use [[Wikilinks]]" switched off, AIs, and notes imported from other tools or OKF bundles (`docs/okf-gap-analysis.md`, point 1). Kumbu² should read all of them as links, keep them working when notes move, and recommend the cleanest form to agents.

## Goals

- Every form a Markdown link to a note takes in the wild counts as a link wherever wikilinks count today: `links`, `backlinks`, the web app's link resolution (a click opens the note), the checkpoint view (links stay inside the checkpoint; this closes the point parked from PR #40), and `mv`.
- `mv` never leaves a Markdown link to a note broken.
- Agents are told to write new links as absolute Markdown links.

## Non-goals

- Converting wikilinks into Markdown links, or the other way round. Kumbu² never changes a link's form on its own, except the `mv` rule below.
- OKF's "`/` is the bundle root" (v3, with OKF bundles). Here `/` is the vault root, as in Obsidian.
- Image and file links (`![](picture.png)`, `[pdf](report.pdf)`): they come with attachments, later in v2.5. This branch bumps no version.
- Reference-style links (`[text][ref]` with `[ref]: target`) *(Claude's call: rare in notes; the builder may add them if they fall out of the parser for free)*.

## Anti-goals (it failed if…)

- …a link that worked in Obsidian stops working in Kumbu², or the other way round.
- …`mv` rewrites a link to the wrong note, or changes a link's text or heading.
- …a rename in Obsidian makes the server and Obsidian both rewrite the same links, so they conflict or double up. Wikilinks avoid this today (the Obsidian sync moves without the server rewriting links, `NoteMover`'s `rewriteLinks`); Markdown links must take exactly the same path.
- …an external URL, a `mailto:` or an image is treated as a note link.

## What counts as a link to a note

`[text](target)`, outside fenced code blocks and inline code (the same exclusions as `WikiLink.Parse`). Frontmatter as wikilinks do today. The target, as Obsidian resolves it:

- **Absolute in the vault:** `/Work/plan.md`, and without the leading slash `Work/plan.md` (Obsidian's "Absolute path in vault").
- **Relative to the note:** `./sub/plan.md`, `../plan.md` (Obsidian's "Relative path to file").
- **Shortest path:** `plan.md`, resolved like a wikilink (the note whose path ends with it; the same ambiguity handling and the same "ambiguous link" dialog in the web app).
- **Spaces and special characters:** percent-encoded (`Meeting%20Notes.md`) and in angle brackets (`<Meeting Notes.md>`); both decode to the same path. Paths are compared as `LinkResolver` compares them today (case-insensitive, NFC).
- **A heading or block:** `plan.md#Goals`, `plan.md#Goals%20and%20ways`, `#^block`; and `[text](#Heading)` for a heading of the note itself, like `[[#Heading]]`.
- **Without `.md`:** `[Plan](plan)` counts if Obsidian resolves it to `plan.md` *(the builder checks Obsidian's behaviour and follows it)*.
- **Not a note link:** anything with a scheme (`https:`, `http:`, `mailto:`, `obsidian:` …), images (`![…](…)`), and targets that resolve to no note and look like a file (`report.pdf`), exactly as `LinkKind` treats such wikilinks today. A target that resolves to no note and looks like a note is an unresolved link, as for wikilinks.

## `mv`

When `mv` moves a note or a folder (with link rewriting, as today for wikilinks):

- **Any Markdown link `mv` has to touch is written absolute**, in the form `/Folder/Note%20name.md` (vault-relative, leading slash, `.md`, spaces and characters that need it percent-encoded as Obsidian encodes them). A link has to be touched when
  - its **target** moved, or
  - it is **relative** (`./`, `../`) and the **note it stands in** moved, so the relative path would now point elsewhere; or a shortest-path link would resolve differently or ambiguously after the move.
  Decided by Chris: rewriting a relative link as a new relative path would be a source of flaky edge cases; absolute is always right.
- The link text, a `#heading` or `#^block`, and an angle-bracket or title part (`[t](x.md "title")`) stay byte for byte; only the path changes.
- Links `mv` doesn't have to touch stay exactly as they are, in their form.
- Wikilinks keep today's behaviour.

## What agents are told

The tool description and the shell manual (`src/Kumbu.Core/Sh/Manual.cs`) recommend, for new links: `[text](/Folder/Note.md)`, absolute in the vault, with `%20` for spaces. Why, in a few words: unambiguous, survives moving the note it stands in, standard Markdown that every tool reads. Reading every form stays. *(Chris: "agents make the cleanest format".)* The welcome vault's texts stay as they are.

## Settled after the builder check

- **A relative link in a moved note** is rewritten only if it would resolve to a different note (or to none, or ambiguously) after the move, the same test `NoteMover.Rewrites` applies to wikilinks; a relative link that still reaches the same note (both moved together, e.g. inside a moved folder) stays as it is. *(Claude's call, within Chris's rule: whatever `mv` does touch becomes absolute.)*
- **`Work/plan.md` without a leading slash** resolves as Obsidian does: an exact vault path wins; otherwise it is a path suffix like a wikilink, with the same ambiguity handling. *(Claude's call; the builder confirms against Obsidian.)*
- **In the checkpoint view** a Markdown link resolves against the checkpoint's notes, as wikilinks do since PR #40; a target the checkpoint doesn't hold shows as not in this checkpoint, never as today's note.

## Left to the builder

The parser (one that keeps the exact source text of each part, so a rewrite changes only the path, as `WikiLink` does), how `links`/`backlinks` show the two kinds (they may look the same), the encoding table (follow Obsidian), the web app change for clicks on Markdown links, and the tests, at least: every form above resolves; external, image and file links don't count; code blocks and inline code don't count; `mv` of the target, of the source with a relative link, and of a folder rewrites to absolute and leaves text and heading alone; a link `mv` doesn't touch is unchanged; a sync move (no rewriting) leaves Markdown links alone, as it leaves wikilinks; checkpoint view resolves Markdown links inside the checkpoint.
