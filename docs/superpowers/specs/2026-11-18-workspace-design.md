# Workspace: side panel, link completion, split view, note templates

Status: approved by the maintainer on 2026-11-18, after a UX review (findings and decisions with their reasons in the maintainer's working notes, "Workspace 2026-11-18"). Target: 0.6.0, track A. Built in four pull requests, in this order, after the command palette (#237) and note actions (`2026-11-18-note-actions-design.md`) are merged: both change `MarkdownEditor.tsx`, `MobileShell.tsx` and `TopBar.tsx`.

## Why

People who work with many notes miss four things that keep them in Obsidian: backlinks and the outline as a fixed panel beside the note, completion of headings and blocks while typing links, two notes side by side, and note templates. The web app has the pieces (a `[[` completion by note name, "Linked from" at the note's bottom, an outline sheet) but not the workspace.

## Goals

1. **Side panel** on wide screens: Links and Outline beside the note.
2. **Link completion** for headings, block ids and Markdown links, beside the note names it already completes.
3. **Split view**: two panes, both editable, one at a time; the same note beside itself is its live preview ("Ghostwriter").
4. **Note templates**: insert a note from `Templates/` at the caret, with `{{date}}`, `{{time}}`, `{{title}}`.

## Non-goals

- Nothing changes on phones, tablets and windows narrower than 1280 px.
- No typing in two notes at once (see "One editor at a time").
- No server change: no new endpoint, no change to `sh` or the MCP tools. Templates are notes; agents read them with `cat`.
- Not in 0.6.0, moved to 0.7.0 (decided by the maintainer: 0.7.0 comes a week later and takes 0.6.0's rounding-off along): aliases in link completion (they need data the tree doesn't carry), New note from template.
- No new block ids written into another note from the completion list.
- No setting for the templates folder.
- No more than two panes; no tabs.

## Anti-goals

- **Opening a note in a pane the person didn't mean.** Hard (the maintainer: in Obsidian's side by side he keeps opening files in the wrong pane, and others he asked have the same problem).
- **Losing text when Write mode moves to the other pane.** Hard.
- **A discreet vault's note left on screen in the second pane** after the discreet vaults are hidden again. Hard (security).
- A preview that makes typing lag.
- "Linked from" shown twice on one screen.

## Widths

The side panel and the split view exist from **1280 px** of viewport width (`min-width: 80rem`, Tailwind's `xl`), a second hook beside `useDesktop` (`web/src/hooks/useDesktop.ts`). The docked tree stays at 1024 px. Decided by the maintainer: real working machines start at 1920 px; a 1280×800 screen falls below the line once the browser's frame is counted, and that is intended, side by side isn't sensibly possible there. Below 1280 px everything is as today: "Linked from" at the note's bottom, the outline as a sheet, one note. A URL with a second pane opened below 1280 px shows the pane that has the focus (the left one after a reload), and the split comes back when the window is wide again.

## 1. Side panel

- Docked on the right of the note (of both panes in the split view), collapsible; whether it is collapsed is remembered per device in `localStorage`, as the tree's state is (`web/src/hooks/useSidebarCollapsed.ts`). It holds no content. It starts expanded, so it is found.
- **Tabs:** **Links** ("Linked from" and unlinked mentions, `BacklinksList` as it is) and **Outline**. The last tab chosen is remembered per device. Leaves room for "Similar" (link prediction) and the local graph in 0.7.0 without another layout change.
- **One place:** where the panel is shown (expanded or collapsed), "Linked from" and the unlinked mentions leave the note's bottom (`NoteView.tsx:597`), and the top bar's Outline button (Read mode, `TopBar.tsx`) goes: the outline is the panel's tab. The dock's ⋯ menu never had "Outline".
- **It follows the focused pane** (see the split view).
- **The outline follows the text being written:** in Write mode it is built from the editor's text, unsaved included (`outline()` on the live text, deferred with `useDeferredValue`); in Read mode from the note as today. A click jumps: in Read mode as the sheet jumps today; in Write mode the caret goes to the start of the heading's line and the textarea scrolls it into view, the focus in the textarea. The heading the reader is in is marked (`aria-current="location"`): in Read mode the last heading above the top of the visible text, in Write mode the last heading at or above the caret's line.
- **Shortcut:** Ctrl+Shift+\ (Cmd+Shift+\ on a Mac) shows and hides it, the tree's Ctrl+\ mirrored; `code` too, as the tree's shortcut does. Not over a dialog. Hiding it while it holds the focus moves the focus to its toggle, as the tree does. The toggle sits at the top bar's right end, mirroring the tree's.
- Width: a fixed 300 px; resizable later if someone asks.

## 2. Link completion

Builds on `web/src/note/linkCompletion.ts` and `LinkCompletion.tsx`; the list, its keys and its place stay as they are.

- **Headings:** `[[Note#` lists the headings of the note the name leads to, in order, filtered by what is typed after `#`; `[[#` lists the open note's own headings from the live text. Inserted as `[[Note#Heading text]]`, the heading's text as shown (`headingText`, `outline.ts`: `## The **road**map` gives `[[Note#The roadmap]]`), with Markdown's backslash escapes taken out, the characters a link can't hold (`# | ^ [ ]` and `%%`) dropped and blanks collapsed (`headingLink`); the note view and embeds find a heading by such a link too, and by one holding the heading as written, after the exact and the case-blind match. Decided by Claude (2026-11-19), against the first idea of inserting the heading as written: the shown form, cleaned, because a link with Markdown marks inside it doesn't render in the web app (remark splits it), and the shown form resolves in the web app and, with track C's matching (#247), on the server. Rendering links with marks inside, as Obsidian users may write them, is a backlog item.
- **Blocks:** `[[Note#^` lists the block ids that note already has (`blocks.ts`), each with the start of its block's text, filtered by id and text. Only existing ids: no id is written into another note from the list (in a shared vault it may be someone else's note).
- **Which note the name leads to:** the server's rule as `linkTarget` already mirrors it (the note whose vault path ends with the link, ignoring case, archive left out). Several fit: as the server's resolver (`LinkResolver.cs`) takes it, for a link with a folder the one at that path below the open note's folder, then the one at that path from the vault's root; then the one in the open note's folder; else the first in text order (the server calls that case ambiguous and lets the reader choose; the list needs one note, and the first is the predictable one); none fits: no list. The note is read with the existing note read (`useNote`), so it is cached and reused; a cached note is not read again, as that would replace the text a view of it (the split view's other pane) shows; while it loads, the list says "Loading…".
- **Markdown links:** after `](` the same note list, inserting the vault path we write everywhere: `/Folder/Note.md`, spaces as `%20`. `](/Folder/Note.md#` lists that note's headings, inserted as `#Heading%20text`. Only for links whose `[text]` is on the caret's line. The list keeps `linkable`'s filter: a note with `# | [ ]` in its path isn't offered, though a Markdown link could encode it (rare, kept simple). `(#` and a relative `./x.md#` get no heading list. None in code: not in a fenced block or a code span; `d["a"](` outside code is offered, as CommonMark reads a link there.
- **Fences in list items and quotes:** a fence opened inside a list item or a block quote (`- ```…`, `> ```…`) isn't seen as one, so the list is offered there; `outline()` doesn't see it either. Accepted: rare in notes, and the two stay alike.
- **Embeds:** `![[` works as `[[` does, as today.
- **One ranking:** the palette's matcher (`web/src/palette/filter.ts`, every word, starts of words first) and `rankNotes` rank notes differently today. The builder makes both use one function for notes, the palette's behaviour unchanged (its tests stay green), so `plan` finds the same notes first in both. If that turns out larger than a small change, it moves to 0.7.0 and the spec's note says so. Decided by Claude: the link list puts the note whose name is the query (ignoring case and `.md`) first, and breaks ties by the note's own name before its folder, because it shows 8 rows, not the palette's 50, and the note named as typed must not fall off the list; a path typed as such (`projects/pl`, `/pro`) still finds its notes, after the rest.
- **IME:** keys stay the IME's while it composes, as today.

## 3. Split view

### The pane model

The editor store (`web/src/state/editor.ts`) gets two panes, `left` and `right`, each `{ path, mode }`, and `focused: "left" | "right"`; `right` is null without the split. **`currentPath` and `mode` stay and mean the focused pane's**, so the top bar, the dock, the palette's Note group, Shift+F10 and the side panel follow the focus without change. The builder audits every reader of `currentPath` and `mode` (about 14 and 10 files) and names in the plan which mean "the focused note" and which "the note being written".

**One editor at a time** (decided by the maintainer: "göttlich interaktiv", it gives the feeling of really switching; better than Obsidian and Ghostwriter offer, and the caret's place is never in doubt). At most one pane is in Write mode. Starting Write mode in one pane first ends it in the other with the same save as Done (`endWrite`); if that save fails, the switch doesn't happen and the failure shows where it shows today. So `beforeRead`, `unsaved` and `draft` stay single, and with `beforeRead` the note actions' save before a copy (`beforeRead({ reread: false })`, `web/src/shell/useNoteActions.tsx:111`): they belong to the one editor there is. Claude's reason: everything around saving stays as proven since 0.4.0, and the pane model shrinks to a path and a mode per pane.

Focusing a pane (a click on its note bar or its margins, Ctrl+Alt+← / →) doesn't end Write mode in the other: you can scroll and read on the right while the left still holds your caret and text. Only starting Write mode moves it.

### Turning it on and off

- **"Split view"** (lucide `Columns2`): a button in the note bar (`web/src/shell/NoteBar.tsx`) of the left pane, and a command in the palette. Decided by the maintainer, as Obsidian has a button for it.
- **Turning it on always starts the same way:** the left pane in Write mode, the right pane the same note's live preview. The focus stays in the left pane's editor. Where the note can't be written (a read-only share, a refused save), the left pane stays in Read mode and the right starts on the same note.
- **Then the right pane is chosen** (a click on its note bar or margins, or Ctrl+Alt+→) and a note picked in the tree opens there, in Read mode, as a note opens today; the left pane keeps Write mode and its caret. A click on the preview's *text* instead swaps the roles (see below), so the note bar is the way to choose the pane without writing in it; its focus mark says so at once.
- **Also, besides the button:** "Open beside" in a tree row's menu, Ctrl+Enter on a note row in the palette, Ctrl+Alt+click on a link (Obsidian's "open to the right"). These open the note in the right pane, starting the split if needed, and focus it. A plain Ctrl+click stays the browser's.
- **Turning it off:** the button again, or the ✕ in the right pane's note bar: the right pane closes. The left pane's ✕ (shown only in the split) closes the left; the right becomes the only pane. If the pane closed is in Write mode, it saves first as Done does.

### Where a note opens

One rule (anti-goal 1): **the tree, the palette, search, Recent and What's on open in the focused pane; a link opens in the pane it was clicked in.** Back and forward follow the URL.

### Which pane is focused, shown plainly

- **The focused pane is marked by more than colour:** a 3 px accent line along its top, its note bar in full contrast; the other pane's note bar and title dimmed (`text-dim`). `aria-current="true"` and a label ("Left pane, focused") for screen readers.
- **The tree shows both:** the focused pane's note marked as the current note is today; the other pane's note with a quieter mark (an outline, no fill), labelled "Open in the other pane".
- **The dock sits under the focused pane only**, so its ⋯ is plainly that pane's.

### Write follows the click, and the caret starts where you click

Hard (the maintainer: people will use the split for copy and paste). In the split view, a **plain click** (pointer down and up without moving, no selection made) on the text of a pane that isn't writing starts Write mode in that pane, with the caret at the spot clicked. A **drag selects text** as Read mode does and switches nothing, so text can be copied from the other pane. Links, task boxes, images, embeds, buttons and the properties box keep their own click. The single pane and narrow screens keep today's Read mode, where a click starts nothing.

Mapping the click back to the source: the clicked block's source line (`data-line`; the builder adds it to paragraphs, list items, quotes, code blocks and tables, where only headings, tasks and callouts carry it today, `MarkdownView.tsx`), and the clicked text node with its offset (`caretPositionFromPoint`, `caretRangeFromPoint` in Safari). The rendered text around the point (up to 16 characters before and after) is found in that block's source lines; the caret goes there. If it isn't found, or found more than once in the block, the nearest match to the proportional position is taken; inside a formula, a diagram or an embed, the caret goes to the start of the block's line.

### The same note beside itself: live preview

The maintainer's rule: **the pane that isn't writing, on the same note as the writing one, shows that note's live preview.** No separate mode.

- It renders the editor's text, unsaved included, with `MarkdownView` (the editor publishes its text to the store as it changes; the preview takes it through `useDeferredValue`, so typing never waits for the render).
- **It is marked:** lucide `Glasses` (reading glasses) in its note bar, labelled "Live preview of <note>". Why not the eye: `Eye` already stands for discreet vaults, publishing and the password field.
- **It follows the caret:** the block at or above the caret's line is kept in view (by `data-line`); scrolling the preview by hand stops the following until the next keystroke.
- **Symmetric by the rule:** a click into the preview's text starts Write mode there (caret where clicked), and the other pane becomes the preview.
- **A link clicked in the preview** opens in the preview's pane (where a link opens): you write, follow a link, and see that note beside you. The preview ends there.
- In Read mode in both panes on the same note: both show the note; nothing is special.

### The URL

`/notes/<vault>/<path>?beside=<vault path of the right pane>`, encoded as paths are. The left pane is the path, the right `beside`. Reload, back, forward and a shared link keep the split; which pane is focused is not in the URL (the left after a reload). Opening in the right pane pushes a history entry, as opening in the left does. A `beside` note that doesn't exist or can't be read shows in its pane as a missing note does today. Either pane may be in another vault; the active vault (the tree's) follows the focused pane.

### Each pane

Its own note bar (rename, delete, split, ✕, the preview's mark), its own properties box, banners (agent change, changed elsewhere, presence) and Read/Write; the top bar's Read/Write acts on the focused pane. The Write toolbar is the writing pane's. Find in note acts on the focused pane in Read mode.

### Discreet vaults

Hiding the discreet vaults again (`web/src/discreet/hideAgain.ts`) closes a pane whose note is in a discreet vault, both panes alike; if that pane was writing, its unsaved text goes the way `unsaved` goes today. Hard (anti-goal 3).

A discreet vault named in `?beside=` makes the address bar and history neutral (`/discreet`), as one in the left pane's address does (`discreetUrl`, `web/src/discreet/neutralWindow.ts`): the URL names no discreet vault in either pane.

## 4. Note templates

- **Where:** the notes in the folder `Templates` at the vault's root and below it, by convention, as `Board` makes a board (`src/Kumbu.Core/Board/BoardReader.cs`). No setting. Decided by the maintainer: one rule, visible in the tree, nothing to configure; an Obsidian user points Obsidian's template folder at `Templates` and both agree.
- **Insert at the caret:** "Insert template" (lucide `LayoutTemplate`) in Write mode's toolbar and as a command in the palette while a note is in Write mode. A list of the vault's templates by name (with their folder below `Templates/`), filtered as typed; Enter inserts. It replaces a selection, as typing would; one undo step where the textarea's undo allows (`textEdit.ts`).
- **No templates yet:** the list says "No templates yet. Notes in a folder named Templates at the top of this vault show up here." with **New template**, which makes `/<vault>/Templates/Untitled.md` and opens it (invite and lead).
- **Properties are merged**, not pasted as a second `---` block (Obsidian merges too): the template's keys the note lacks are added; lists (`tags`, `aliases`, any list) are united, keeping the note's order and appending what's new; a scalar the note has stays. Through the frontmatter helpers (`web/src/note/frontmatterText.ts`). If the note's frontmatter can't be read (`frontmatterUnreadable`), only the body is inserted and a hint says "The template's properties weren't added: this note's properties can't be read." A note without frontmatter gets the template's.
- **Placeholders,** resolved on insert, in the body and in property values: `{{title}}` (the note's name without `.md`), `{{date}}` (`YYYY-MM-DD`, the calendar's form), `{{time}}` (`HH:mm`), `{{date:FORMAT}}` and `{{time:FORMAT}}` with the Moment tokens `YYYY YY MM M DD D HH H mm ss ddd dddd MMM MMMM`, in English. In the user's time zone (the calendar's zone, `web/src/calendar/preferences.ts`; the browser's if none is set). Anything else in `{{…}}` (and Templater's `<% %>`) stays as written.
- **The template file keeps its raw `{{…}}`,** which Obsidian reads as its own. Templates are ordinary notes: they show in the tree and search; a template with `when: {{date}}` is no date, so the calendar ignores it.

## Shortcuts

| Keys | What |
|---|---|
| Ctrl+Shift+\ | side panel shown or hidden |
| Ctrl+Alt+← / → | focus the left or right pane |
| Ctrl+Alt+click on a link | open it in the right pane |
| Ctrl+Enter on a palette note row | open it in the right pane |

Cmd for Ctrl on a Mac. Split view and Insert template have no key of their own (palette and their buttons). No Ctrl+Space for completion: it switches the input method under fcitx/ibus and on a Mac, the reason note actions didn't take it. None of these clash with the browser, Ctrl+S, Ctrl+\, Ctrl+P or Shift+F10.

## Pull requests

1. **Side panel** (Links, Outline) at 1280 px.
2. **Link completion:** headings, blocks, Markdown links, one ranking.
3. **Split view:** first the pane model in the store with its audit, test-first, no visible change; then the second pane, the focus marks, write follows the click, the live preview, the URL.
4. **Note templates.**

If the freeze (Saturday 2026-11-21, evening) comes first, what isn't merged moves to 0.7.0 from the end: templates first, then the split view.

## Tests

Vitest for the logic, Playwright (`web/e2e/`) for the flows.

- **Side panel:** shown from 1280 px, not below; "Linked from" not at the bottom where the panel is; the outline from unsaved text in Write mode; a click on a heading puts the caret on its line; the current heading marked; Ctrl+Shift+\ hides and shows it, the focus moved to the toggle when it held it; collapsed state and tab survive a reload.
- **Completion:** `linkQuery` cases for `[[Note#`, `[[#`, `[[Note#^`, `](`, `](/x.md#`; heading text cleaned; ambiguous names take the first; no list for an unknown note; Markdown paths with `%20`; one ranking for palette and list, the palette's tests unchanged.
- **Pane model:** `currentPath` and `mode` follow the focus; at most one pane in Write mode; starting Write in the other pane saves first, and a failed save keeps Write where it was with its text; closing a writing pane saves first.
- **Where things open (Playwright):** after focusing the right pane, a tree click opens there and the left is unchanged; a link opens in its own pane; the palette opens in the focused pane, Ctrl+Enter in the right.
- **Click to write (Playwright):** a click on a word in the reading pane starts Write mode there with the caret at that word (the selection's offset in the source checked); a drag selects and switches nothing; a link click follows the link.
- **Live preview:** split view starts with Write left and the preview right, marked with the glasses; typing shows in the preview; a click into the preview swaps the roles, the caret where clicked; a link in the preview opens there.
- **URL:** `?beside=` restores both panes; below 1280 px only the focused pane shows.
- **Discreet:** hiding the discreet vaults closes a pane showing one of their notes.
- **Templates:** placeholders with formats and the time zone; unknown placeholders kept; properties merged (missing keys added, lists united, scalars kept); unreadable frontmatter inserts only the body with the hint; empty state with New template; the template file unchanged after inserting.

## Left to the builder

- The live text's way from the editor to the store (a field set on change, or a subscription), as long as the preview never slows typing.
- The proportional fallback of click-to-write in detail.
- How "Open beside" sits in the tree row's menu, beside its other items.
- Whether the side panel's tabs use the existing tab pattern of the windows (#232) or the Access screen's.
