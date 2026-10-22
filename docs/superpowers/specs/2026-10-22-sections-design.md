# Sections — Design (step 4a)

**Date:** 2026-10-22
**Scope:** The section half of `docs/concept.md` §12 step 4 (heading index, section addressing, section edits), plus observations 1, 2 and 4 from [notes-test-phase3.md](../../notes-test-phase3.md). Links, backlinks and `mv` follow in step 4b.
**Status:** Split into 4a/4b and "content is never rewritten automatically" settled with Chris (now in concept §4); the rest written by Claude for review with the PR.

## 1. Intent

- An agent sees the structure of a long note (`outline`) and reads one part of it (`cat "/Work/plan.md#Architecture#Gates"`) instead of the whole note, which is what the truncation footer has been promising.
- An agent changes one section without touching the rest: replace it, append to it, or replace text inside it.
- The phase 3 remarks: a clear message for a missing required parameter; trash entries that can be told apart and restored by version; a note in `help history` about repeated tokens.

Out of scope: links, backlinks, `mv` (4b); setext headings (`Title\n=====`), which Obsidian renders but which are rare in practice; tags.

## 2. Decisions

| Topic | Decision | Why |
|---|---|---|
| What is a heading | ATX headings (`#` to `######`, up to three spaces of indent, a space after the hashes, an optional closing `#` sequence). Not inside fenced code blocks (``` or `~~~`), not inside YAML frontmatter at the top of the note. | CommonMark, and what Obsidian's outline shows. `#tag` at the start of a line is a tag, not a heading. |
| What is a section | From its heading line to the next heading of the same or a higher level; subsections belong to it. Trailing blank lines are not part of what `cat` shows or what an edit replaces, so the spacing before the next heading survives. | The usual reading of a Markdown outline. |
| Address | As in concept §4: `note.md#A#B`. The first `#` after `.md` starts the address; each further `#` goes one level down. `\#` is a literal `#`, `\\` a backslash. An address may start at any level and matches the end of a heading's chain, so `#Gates` finds `## Gates` under `# Architecture` if it is the only one. | Same as Obsidian's `[[note#A#B]]`. Short addresses are what models write first. |
| Duplicates | Headings with the same text under the same parent get an index, `#Notes[1]`, `#Notes[2]`. An address that matches more than one heading is an error listing the full addresses to choose from, never a silent first match. Matching is exact; if nothing matches exactly, it falls back to ignoring case. | Concept §4: stale or vague addresses fail loudly. |
| Where addresses work | Everywhere a command reads one note: `cat`, and `head`, `tail`, `wc`, `sort` at the start of a pipeline. The version token is always the whole note's, because writes go to the whole note. `outline` takes a note; `rg` searches notes and rejects an address with a hint to use `cat … \| rg`. | One rule, implemented once in the code that reads a note for a command. |
| `outline` | One line per heading: line range, indented heading with its `#`s, index if duplicated, size in tokens. Text before the first heading shows as `(before the first heading)`. A last line explains how to address a section. | Enough to pick a section without reading the note. |
| Section edits | `edit`'s `path` may carry an address. With `old_text`: the replacement must be unique **within the section** (a way to disambiguate). Without `old_text`: append at the end of the section, before its trailing blank lines. With the new flag `replace_section`: the whole section, heading included, becomes `new_text`. | Concept §5 (replace/append/insert section). Appending to a section's end is also how a new section is inserted after it. No new tool, no new mode string. |
| `write` with an address | Rejected, pointing to `edit`. | `write` replaces whole notes. |
| Missing required parameter | A call-tool filter checks the tool's own input schema before the SDK binds arguments, and answers like every other error: `edit: version is required; get it from 'cat <path>' (it comes as a separate block)`. The schema still marks the parameters as required. | Phase 3, observation 1. Uses the schema as the single source, so a new tool gets it for free. |
| Trash entries | `trash` shows version token and line count per entry. `restore <path> <version>` restores a trashed version when no note exists at the path. | Phase 3, observation 2. |
| Truncation footer | `narrow it with outline, cat 'note.md#Section', rg, head or tail`. | Concept §5. |
