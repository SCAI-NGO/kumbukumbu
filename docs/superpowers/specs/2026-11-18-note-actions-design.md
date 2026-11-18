# Note actions: duplicate, the palette, the keyboard menu

Status: design, agreed with the maintainer on 2026-11-18 (UX review included). Target: 0.6.0, stacked on the command palette (`2026-11-18-command-palette-design.md`, #237).

## Why

A note's actions live in one place today: the ⋯ menu in the dock, in Read mode only. Three things are missing. There is no way to duplicate a note. The command palette, now that it exists, can't reach a note's actions. And nothing opens the note's menu from the keyboard, in Write mode not at all.

## Goals

- **Duplicate:** a copy of the note beside it, named "Copy of …".
- **The note's actions in the palette:** while a note is open, a group "Note" with the same items as its menu.
- **The note's menu from the keyboard:** Shift+F10 and the context-menu key, in Read and Write mode.

## Non-goals

- No duplicating of folders, attachments or several notes at once.
- No name dialog for the copy: it is renamed like any note, afterwards.
- No right-click menu. The right click stays the browser's: copy, paste and spelling need it.
- No Ctrl+Space. Decided by the maintainer: it switches the input method under fcitx/ibus on Linux and the input source on a Mac, and editors use it for completion, which we may want for links later; Shift+F10 and the menu key are every platform's keyboard context menu and what screen-reader users know.
- No sh command or MCP tool for duplicating: agents copy with `cat` and `write` already.

## Anti-goals

- A copy that doubles an appointment in the calendar or What's on.
- A copied board card the board shows as broken.
- Losing text: a copy made while Write mode holds unsaved text must hold that text.
- A note action in the palette that runs without the question the menu asks (Delete).

## Duplicate

**What the copy takes along** (decided by the maintainer): the note's text byte for byte, tags (flags included) and every other property, with these exceptions:
- the calendar's keys go: `when`, `until`, `due`, `task`, `upcoming`, `tz`, `closed`, `reason` (`src/Kumbu.Core/Calendar/CalendarKeys.cs:10`, and `due` for cards). Why: otherwise the copy stands twice in the calendar and What's on; whoever duplicates an appointment gives the copy its own date, usually a different one.
- a board card keeps its `column` and loses `rank` and `moved`. Why: the column is what makes a note in `/<vault>/Board/` a card (`src/Kumbu.Core/Board/BoardReader.cs:57`), so the copy stands as a second card at the end of the same column. The maintainer: moving a card is an operation we never have enough to decide where it should go; in doubt it stays, and the person moves it.

The calendar's seven keys are matched in any case (`When:` goes), `due`, `rank` and `moved` exactly as written, as the server reads each (`CalendarKeys.cs` folds the seven; the board and a card's `due` read `Frontmatter.Read`'s keys as written). Why: a key the server reads as a date must go, whatever its spelling; one it doesn't read may stay.

Removing a key removes its whole entry with the extent `Frontmatter.Set` gives it (`src/Kumbu.Core/Board/Frontmatter.cs`): its line, a comment after the value included, and the lines indented under it or starting with `- ` (a list, a map, a block), with the blank lines between those; every other byte stays as it was. This also holds where the properties box can't read the frontmatter (a nested map, a block scalar): only a missing or unclosed frontmatter block leaves the text unchanged. Why: the server's calendar reads the dates of such a frontmatter all the same, so copying it unchanged would double the appointment the anti-goal names. When nothing is left in the frontmatter, the empty block stays (`---` `---`); the builder may drop it if that's simpler and tested.

**The name.** "Copy of <name>.md" in the same folder; if taken, "Copy of <name> 2.md", then 3, and so on, as `Untitled` notes are numbered. Notes are renamed from their heading only while they are "Untitled …" (`web/src/note/nameFromHeading.ts:4`), so the copy keeps its name. A name past the server's length limit is cut at the end of `<name>`, before " 2".

**What is copied.** The text as the person sees it: in Write mode with unsaved text, that text is saved first (the editor's own save), then copied; if the save fails, nothing is copied and the failure is shown as the save's. Claude's call: copying the server's older text would lose what is on screen.

**Where it is offered.** In the note's menu as "Duplicate" (lucide `CopyPlus`: "Copy file path" beside it has `Copy`), where the person may write the folder (as "Rename or move" is offered), and as "Note: Duplicate" in the palette. Not for a note under `_archive/`: the copy would be archived too, which nobody asks for.

**After duplicating** (decided by the maintainer): the original stays open. A short notice "Copy of plan.md created" with **Open** goes to the copy. Why: the intention isn't visible, so staying is the safe case; editors tend to open the copy (Obsidian, Google Docs), file managers stay (Finder, Explorer, Dolphin), so there is no standard, and the notice gives the one click for those who want to edit the copy and names it. It goes after a few seconds or on Esc, as the shell's other notices do.

**How.** In the web app, through the existing note write (`PUT /api/notes/{vault}/{path}` with the content, which answers 409 when the note exists: then the next number is tried), with the frontmatter keys removed by the web app's frontmatter text helpers (`web/src/note/frontmatterText.ts`). No new endpoint. Attachments the note embeds are not copied: the copy stands in the same folder, so its links resolve to the same files.

## The note's actions in the palette

While a note is shown or edited, the palette has a group **Note** before Commands, its rows named exactly as the menu's items and placed with the note's title ("Publish", "Duplicate", "History", "Rename or move", "Delete note", "Flag", "Add property…", and the rest of the menu) (`noteActions`, `web/src/shell/MobileShell.tsx`). The row's place is the note's title, so it is plain which note it acts on. Later, with the multi-editor, it is the note that has the focus (the maintainer).

- **The same list as the menu,** from one source, so the two never drift; only the items that fit the mode, as in the menu.
- **Not pinnable:** they act on the note that is open. The rows have no star, and Ctrl+D does nothing on them.
- **Delete note asks first,** as the menu does (decided by the maintainer: without the question it would be done too quickly). Every row runs exactly what the menu item runs, dialogs included.
- **The palette is gone before a row runs,** and over a window the window closes first, as for Search, and the row runs once it is gone. Why: what the row opens (the find field, Delete's question) must have the focus, and the shell under a window is inert.
- **Filtering** as for every row: "no pub" finds "Note: Publish". In the empty palette the Note group is not shown (favourites and Recent stay the empty palette's); it appears once something is typed.

## The note's menu from the keyboard

**Shift+F10 and the context-menu key** (`ContextMenu`) open the note's menu. Decided by the maintainer:

- **Write mode:** the menu opens at the caret, as a context menu, so hand and eye stay with the text; Esc closes it and the caret is where it was.
- **Read mode:** it opens the dock's ⋯ menu, the same menu where the mouse knows it.
- The same items in both, only those that fit the mode: "Find in note", "History", "Publish…" and "Add property…" in Read mode only. Why: "Find in note" searches the rendered note; the other three act on the saved text (History restores over it, Publish sends it, Add property writes into it) while Write mode may hold unsaved text on screen. Leaving them out of Write mode is the smaller safe change than saving first; Duplicate saves first because its anti-goal is losing that text.
- Not over a dialog (`topDialog()`), not while the palette or another menu is open, not without an open note.
- The context-menu key alone opens it; with the browser's own context menu it is the browser's when the key isn't caught (the key's `keydown` is prevented while a note is open).

## Settled after the builder check (Claude's calls)

- **One source for the items:** a hook (`useNoteActions(path)`) returns the menu's items; the dock's menu, the keyboard menu and the palette all render it. The Write-mode save before a copy is reached through a function NoteView registers in the editor store (`useEditor`, e.g. `flushDraft(): Promise<boolean>`), so the hook can run it wherever the item is chosen; the failure shows where the save's failures show today.
- **Every item of the menu, with the menu's labels verbatim** ("Delete note", "Add to favourites", the copy items, the calendar and task items): one list, so the palette never says something the menu doesn't. "Note: Add to favourites" stars the open note, which is useful in the palette too; the rows themselves still carry no star and ignore Ctrl+D.
- **The row model:** `Row.favourite` becomes optional (absent on Note rows) and `run` gains `{ action(): void }`; the star, Ctrl+D and the favourite key skip rows without `favourite`.
- **The keyboard menu shares the dock's `Menu` list** (`MenuList`) in a `PointMenu` placed at a point, rendered by the shell outside the dock so it exists while the dock is hidden: in Write mode at the caret, in Read mode the same list at the dock's ⋯ button, where the dock's own menu opens (Claude's call: the same list in the same place, without opening the dock's menu by script). With Find in note open the dock is hidden, and the key is left to the browser in the find field.
- **The notice** follows the shell's alert pattern (`ConflictToast`'s look), with **Open** and ✕; Esc closes it while it has the focus. It goes by itself after 8 seconds unless the focus is in it.
- **Naming retries:** the note write without a version is create-only and answers 409 when the note exists (`createEmpty`, `web/src/api/notes.ts:179`, relies on that); the next number is tried on each 409, up to 99, then "There are already 99 copies of <name>; rename one and try again." (the server's 409 sentence names no copies).

## Left to the builder

- The exact menu positioning at the caret (Write mode is a plain textarea, so the caret's position is measured with a mirror element of the same styles; corrected after the build, the first draft assumed CodeMirror), flipping above when there is no room below.
- How the menu's items become one source for the dock, the keyboard menu and the palette (a hook returning `{ label, icon, onSelect, … }[]` is the obvious shape).
- The notice's component, following the shell's existing notices.

## Tests

- Duplicate (Vitest): the copy's content equals the original's but for the removed keys; a multi-line value is removed whole; a card keeps `column` and loses `rank` and `moved`; names "Copy of x.md", then "Copy of x 2.md" on a 409; unsaved Write-mode text is saved first and is in the copy; a failed save copies nothing; no item for `_archive/` notes or without write; the notice names the copy and **Open** opens it.
- Palette: the Note group appears with a note open and text typed, not in the empty palette, not without a note; its rows have no star and Ctrl+D does nothing; "Delete note" opens the same question as the menu.
- Keyboard menu: Shift+F10 and ContextMenu open it in Read mode (the dock's menu) and in Write mode (at the caret); not over a dialog or the palette; Esc returns the focus to the editor at the same caret.
