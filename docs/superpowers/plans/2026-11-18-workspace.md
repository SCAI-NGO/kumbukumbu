# Workspace: implementation plan

Spec: `docs/superpowers/specs/2026-11-18-workspace-design.md` (approved 2026-11-18). Track A of 0.6.0. Read the spec first, whole; where this plan and the spec disagree, the spec wins, and the plan is corrected.

## Before the first task

1. **Wait for the base:** #233, #234, #235, #236, #237 and note actions (`claude/note-actions`, spec `2026-11-18-note-actions-design.md`) merged to `master`. Note actions changes `MarkdownEditor.tsx`, `MobileShell.tsx`, `TopBar.tsx` and adds `useNoteActions`, `PointMenu` and `flushDraft`; PRs 1 and 3 build on them.
2. Rebase `claude/workspace` (spec and this plan) onto the new `master`.
3. `pnpm -C web install --frozen-lockfile`, then `pnpm -C web test --run` and `pnpm -C web build` green on the base. Record the counts.
4. Read on `master` as it then is: `web/src/state/editor.ts`, `web/src/shell/MobileShell.tsx` (render from the shell's `return`), `web/src/note/NoteView.tsx`, `web/src/note/MarkdownEditor.tsx`, `web/src/note/linkCompletion.ts`, `web/src/palette/filter.ts`, `web/src/discreet/hideAgain.ts`. Line numbers below are from `b5edc95` and will have moved.

## Discipline, for every task

- **A failing test first** (Vitest, or Playwright for flows), run and seen failing for the right reason; then the code; then the test green.
- **One review per task** by a fresh subagent against the spec's section and this task: correctness, the spec's anti-goals, British English, the repo's style. Fix what it finds before the next task.
- **Evidence for every "done":** the command and its result (`pnpm -C web test --run`, `pnpm -C web build`, the e2e grep). No "should pass".
- Match the surrounding code: comment density, naming, `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- Stay in `web/` (and `docs/`). A need in `src/` is a stop: ask, track B owns `src/`.
- **Stop and ask** when a soft constraint makes a task many times bigger; the hard ones (marked in the spec's anti-goals: wrong pane, lost text, discreet note left visible) are never traded away.

## Pull requests and branches

Each PR is its own branch off `master`, squash-merged by the maintainer once every check is green; the next branch starts from the new `master`.

| PR | Branch | Holds |
|---|---|---|
| 1 | `claude/workspace` | spec, this plan, the side panel |
| 2 | `claude/workspace-completion` | link completion |
| 3a | `claude/workspace-panes` | the pane model, no visible change |
| 3b | `claude/workspace-split` | the split view |
| 4 | `claude/workspace-templates` | note templates |

Open each PR as soon as its gates are green, without asking (put a `[skip ci]` doc commit before the last code commit, never as the tip). After each merge: tick the item in `/SWE/Kumbu/Internal Roadmap/0.6.0.md` and write its entry into `/SWE/Kumbu/Versions/0.6.0.md` (New / Better, bold lead-in, what it means for people).

E2E: `pnpm -C web e2e --grep @workspace` (Docker is needed; `e2e/stack/run.sh` brings the stack up and down). New specs go to `web/e2e/workspace/`, tagged `@workspace`; they run on the `desktop` project (1280×800, which is exactly the new breakpoint) and, where the spec says "below 1280 nothing changes", on a 1024 px viewport set in the test.

---

## PR 1: side panel

### Task 1.1: `useWide`

- Test (Vitest): `useWide()` is true at `(min-width: 80rem)`, false below, false without `matchMedia`; follows `change`.
- Code: `web/src/hooks/useWide.ts`, beside `useDesktop.ts`, same shape (consider one helper both use if it stays a few lines; don't build a framework).

### Task 1.2: panel state

- Test: collapsed state and the chosen tab survive a reload (`localStorage`), with defaults expanded and "Links"; a throwing `localStorage` falls back to the defaults.
- Code: `web/src/hooks/useSidePanel.ts`, following `useSidebarCollapsed.ts`.

### Task 1.3: `SidePanel` with Links and Outline

- Tests (Vitest, `SidePanel.test.tsx`): tabs "Links" and "Outline" with the tab pattern chosen (spec: the windows' or the Access screen's; pick the one already used for tabs and say which in the PR); Links renders `BacklinksList` for the path; Outline lists headings from the text it is given and marks the current one (`aria-current="location"`); "This note has no headings." when empty; a click calls `onJump(line)`.
- Code: `web/src/note/SidePanel.tsx`. Takes `path`, `text` (live or saved), `currentLine`, `onJump`. Reuse `outline()` and the outline sheet's list markup where it fits (`NoteTools.tsx`).

### Task 1.4: into the shell, one place for links

- Tests: at wide, the panel is beside the note and "Linked from" is not at the note's bottom; at desktop-but-narrow (1024) and on phones it is at the bottom as today; the dock's ⋯ has no "Outline" at wide.
- Code: `MobileShell.tsx` renders the panel right of `<main>` at wide, with a toggle at the top bar's right end (`TopBar.tsx`, mirroring the tree's toggle and its ids pattern); `NoteView.tsx` leaves out `BacklinksList` when told the panel shows it (a prop, not a store field).

### Task 1.5: live outline and jumping in Write mode

- Tests: in Write mode the outline is built from unsaved text (type a `## New` line, it appears); a click puts the caret at the start of the heading's line, the textarea focused and the line scrolled into view; the current heading follows the caret's line in Write mode and the scroll in Read mode.
- Code: the editor publishes its text and caret line where the panel can read them. Simplest that works: the editor already owns the text; expose `liveText` and `caretLine` from `MarkdownEditor` through the editor store (one small field each, set on change and selection change, through `useDeferredValue` on the reading side). PR 3b's preview reads the same fields, so name them for both.

### Task 1.6: Ctrl+Shift+\

- Tests: toggles the panel; `code` too (AltGr layouts); nothing over a dialog (`topDialog()`); hiding it while it holds the focus moves the focus to its toggle.
- Code: beside the tree's Ctrl+\ handler in `MobileShell.tsx`, same shape.

### Task 1.7: e2e and the existing suite

- `web/e2e/workspace/panel.spec.ts`: the flows of 1.4–1.6 in a real browser.
- Run the whole desktop suite: the desktop project is 1280 px wide, so the panel now shows there. Fix the tests that assumed "Linked from" below the note (known: `web/e2e/mermaid/blocks.spec.ts:109` compares positions; `aliases.spec.ts` and `mermaid.spec.ts` find the region by name, which should still work). Change a test's expectation only where the spec changed the behaviour, and say so in the PR.

Gate: Vitest, build, `@workspace`, and the full desktop and phone e2e green. Open PR 1.

---

## PR 2: link completion

### Task 2.1: `linkQuery` learns `#`, `#^` and `](`

- Test cases first (`linkCompletion.test.ts`): `[[Note#` → `{ kind: "heading", note: "Note", query: "" }`; `[[#Intro` → heading of the open note; `[[Note#^ab` → `{ kind: "block", … }`; `[[Note|` → null still; `[x](pl` → `{ kind: "markdown", query: "pl" }`; `[x](/a/b.md#Gro` → markdown heading; `![[` as `[[`; nothing across lines.
- Code: widen `LinkQuery` to a tagged union; the note-name case stays as it is.

### Task 2.2: resolving the name

- Tests: the note whose vault path ends with the name, ignoring case, archive left out; several: the first in text order; none: null. Mirror the server's `LinkResolver` (find it in `src/`, read only) and cite it in a comment.

### Task 2.3: headings and blocks of another note

- Tests: headings from `outline()` with `headingText`, the characters `# | ^ [ ]` and `%%` dropped for the inserted text; block ids from `blocks.ts` with the start of their text; filtered by what is typed; "Loading…" while the note loads (`useNote`, cached).
- Code: in `LinkCompletion.tsx`, the list's options by kind; insertion through `completeLink` and `apply` as today.

### Task 2.4: Markdown links

- Tests: after `](`, the note list; picking inserts `/Folder/Note.md` with `%20`; `#` after a `.md` path lists headings inserted as `#Heading%20text`.

### Task 2.5: one ranking

- Test first: the same query ranks the same notes in the same order in the palette and the link list; the palette's existing tests unchanged.
- Code: one note-ranking function used by `filter.ts` and `rankNotes`. If this grows beyond a small change, stop, leave both as they are, and write it into the spec's "Moved to 0.7.0" with the reason.

### Task 2.6: e2e

- `web/e2e/workspace/completion.spec.ts`: type `[[Plan#`, pick a heading, the link is in the text and leads to the heading in Read mode; `[[Plan#^`, pick a block; `[t](`, pick a note; the inserted Markdown link opens the note.

Gate as PR 1. Open PR 2.

---

## PR 3a: the pane model (no visible change)

### Task 3a.1: the audit, written down

- Before code: list every reader and writer of `currentPath`, `mode`, `setMode`, `openNote`, `endWrite`, `beforeRead`, `unsaved`, `draft`, `flushDraft`, `noteTool`, `find` in `web/src` (not tests), and mark each "focused note" or "note being written". Put the list into the PR description. The rule from the spec: `currentPath` and `mode` mean the focused pane's; the save state belongs to the one editor.

### Task 3a.2: panes in the store

- Tests (`editor.test.ts`): `panes.left`, `panes.right` (null), `focused`; `currentPath` and `mode` are the focused pane's after `focus("right")`; `openNote` opens in the focused pane; at most one pane in Write mode: `setMode("write")` on the focused pane first awaits `endWrite` of the other; a failed save keeps Write where it was, the text untouched; `closePane(side)` saves a writing pane first.
- Code: `web/src/state/editor.ts`. Keep `currentPath` and `mode` as stored fields kept in step (simplest for the 40 readers), or derive them; decide by which keeps the diff smaller and say why.

### Task 3a.3: the URL

- Tests: `?beside=` is read into `panes.right` and written from it; opening in the right pane pushes a history entry; back and forward restore both.
- Code: where the shell keeps the store in step with the URL (`MobileShell.tsx`, the effect on `path`).

### Task 3a.4: discreet vaults

- Test first: with a discreet vault's note in either pane, hiding the discreet vaults closes that pane; a writing pane's unsaved text goes where `unsaved` goes today.
- Code: `web/src/discreet/hideAgain.ts`.

Gate: the whole Vitest and e2e suites green with no visible change. Open PR 3a.

---

## PR 3b: the split view

### Task 3b.1: two panes in the shell

- Tests: at wide with `panes.right`, two `NoteView`s side by side, each with its own note bar, properties and banners; below wide, only the focused pane; the dock under the focused pane only; the top bar's Read/Write acts on the focused pane.
- Code: `MobileShell.tsx` renders a `Pane` per side (extract from what renders the one note today; `NoteView` keeps `key={path}`).

### Task 3b.2: focus, plainly

- Tests: the focused pane carries the 3 px accent top line and `aria-current="true"` with its label; the other's note bar dimmed; a click on a pane's note bar or margin focuses it without starting Write; Ctrl+Alt+← / → focus left and right (not over a dialog, not in the palette).
- Tree: the focused pane's note marked as today, the other's with the quiet mark and "Open in the other pane" (`TreeDrawer.tsx`).

### Task 3b.3: where things open

- Tests (Playwright, hard): focus right, click a note in the tree → opens right, left unchanged; same for the palette, search, Recent and What's on; a link opens in its own pane; Ctrl+Alt+click on a link opens right and focuses it; Ctrl+Enter on a palette note row opens right; "Open beside" in a tree row's menu opens right.
- Code: one function "open in the focused pane" every opener calls; links pass their pane.

### Task 3b.4: Split view on and off

- Tests: the `Columns2` button in the left note bar and "Split view" in the palette start the split with Write left and the same note right; the focus stays in the left editor; a read-only note keeps Read left; the button again or the right ✕ closes the right pane (saving first if it writes); the left ✕ (only in the split) leaves the right as the only pane.

### Task 3b.5: write follows the click, caret where clicked

- Tests (Vitest for the mapping, Playwright for the flow, hard): the mapping from a rendered point to a source offset for plain text, emphasis, links' labels, list items, quotes, code; not found or ambiguous → the proportional fallback; formula, diagram, embed → start of the block's line. Flow: click a word in the reading pane → Write mode there, the selection's offset at that word; a drag selects and switches nothing (and the selection can be copied); a link, task box or image keeps its click; single pane and 1024 px: a click starts nothing.
- Code: `data-line` on paragraphs, list items, quotes, code blocks and tables in `MarkdownView.tsx` (through `useLine`, as headings carry it); the mapping in its own small module (`web/src/note/clickToSource.ts`) with `caretPositionFromPoint` / `caretRangeFromPoint`.

### Task 3b.6: the live preview

- Tests: the pane that isn't writing, on the same note, renders the editor's live text (type, see it); it carries `Glasses` and "Live preview of <note>"; it keeps the block at the caret's line in view, and a hand scroll stops that until the next keystroke; a click into its text swaps the roles with the caret where clicked; a link clicked in it opens in its pane and the preview ends; typing stays quick (the preview reads through `useDeferredValue`; check with a long note in Playwright that a typed line lands in the textarea at once).
- Code: the preview reads `liveText` and `caretLine` from PR 1's fields.

### Task 3b.7: e2e

- `web/e2e/workspace/split.spec.ts` holds the flows of 3b.3–3b.6 and `?beside=` on reload; one phone and one 1024 px test that nothing of the split shows.

Gate as PR 1. Open PR 3b.

---

## PR 4: note templates

### Task 4.1: placeholders

- Tests (`templates.test.ts`): `{{title}}`, `{{date}}` (`YYYY-MM-DD`), `{{time}}` (`HH:mm`), `{{date:dddd, D MMMM YYYY}}`, `{{time:HH:mm:ss}}`, every listed token; the calendar's zone, the browser's without one, a date near midnight in another zone; `{{other}}` and `<% x %>` kept.
- Code: `web/src/note/templates.ts`, no date library (the tokens are few; `Intl.DateTimeFormat` with `timeZone` for the parts, English names).

### Task 4.2: merging properties

- Tests: a missing key added; lists united in the note's order with the new appended; a scalar the note has kept; no frontmatter → the template's; unreadable frontmatter (`frontmatterUnreadable`) → body only and the hint text; placeholders resolved in values.
- Code: in `templates.ts`, through `frontmatterText.ts`'s helpers.

### Task 4.3: the list and insertion

- Tests: "Insert template" (`LayoutTemplate`) in Write mode's toolbar (mind `toolbarCut.ts`, which decides what fits) and as a palette command while a note is in Write mode; lists the notes under `/<vault>/Templates/` with their sub-folder; filters as typed; Enter inserts at the caret, replacing a selection, one undo step where `textEdit.ts` gives it; empty state text and **New template** making `/<vault>/Templates/Untitled.md` and opening it; the template note unchanged afterwards.

### Task 4.4: e2e

- `web/e2e/workspace/templates.spec.ts`: make `Templates/Meeting.md` with properties and placeholders, insert it into a note with properties, check the merged frontmatter, today's date in Europe/Vienna, the body at the caret.

Gate as PR 1. Open PR 4.

---

## At the end

- Each merged PR has its tick in `0.6.0.md` and its entry in `Versions/0.6.0.md`.
- What didn't make the freeze (Saturday 2026-11-21, evening) goes back into the backlog from the end of the list (templates first, then the split view), with a line why.
- `README.md` names the workspace where it lists the web app's features; `docs/` where the editor is described.
