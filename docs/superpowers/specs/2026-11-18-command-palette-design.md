# Command palette and favourites

Status: design, agreed with the maintainer on 2026-11-18 (UX review included). Target: 0.6.0.

## Why

Getting to a settings page, a vault's board or a note you open every day takes several clicks today, and the web app has no keyboard way to jump anywhere. Obsidian and VS Code solve this with a command palette on Ctrl+P. Obsidian has no way to pin what you reach often, though, and the maintainer missed that during the Obsidian plugin tests, where he kept going back to the same two settings. That gap is where the favourites come from.

## Goals

- Ctrl+P (Cmd+P on a Mac) opens a palette that jumps to commands, vaults and notes by name, from anywhere in the signed-in shell, Write mode included.
- Favourites: anything in the palette can be starred and then stands on top, on every device of the person.
- A visible way in on the desktop and the phone, so nobody needs to know the shortcut.

## Non-goals

- **No semantic or full-text search in the palette.** The last row hands the text over to the existing search instead. Decided by the maintainer, because the palette must answer at once and predictably; semantic results are ranked and late and would reorder the list while typing. This still gives a second way into search, without a second search UI.
- No VS Code prefixes (`>`, `@`, `#`) and no fuzzy scoring beyond what is described below.
- No attachments and no archived notes as rows. Attachments are reached through a vault's gallery.
- No reordering of favourites in v1. They stand in the order they were added, until reordering is missed in daily use (YAGNI, the maintainer's call).
- No printing shortcut. Ctrl+P takes the browser's Print, which stays in the browser's menu. The maintainer: printing is unimportant, and should Kumbu² ever support it, it gets its own thought then.
- No second shortcut such as Ctrl+K: many editors use it for inserting a link, which we may want later, and Obsidian users expect Ctrl+P.

## Anti-goals

- A favourite, a note name or a count that tells anyone about a discreet vault the session hides.
- A favourite that names a vault in the clear in the database.
- A palette that lags or reorders itself while typing.

## The palette

### Ways in

- **Ctrl+P / Cmd+P**, caught on `document` in the capture phase with `preventDefault`, everywhere in the signed-in shell and its windows, Write mode included. On a Mac, Ctrl+P stays CodeMirror's "line up"; only Cmd+P opens the palette there. It doesn't open over a dialog (`topDialog()`, as Ctrl+S in `web/src/note/MarkdownEditor.tsx:185` does). Pressed while the palette is open, it closes it.
- **A star button in the top bar** (`web/src/shell/TopBar.tsx`), the same on desktop and phone, lucide `Star`, tooltip and accessible name "Favourites and commands (Ctrl+P)" (⌘P on a Mac). Why a star: favourites are what the palette shows first. Why not in the dock's search pill: one field with two meanings would undo the clear split between commands and search.

### Shape

On the desktop a centred panel near the top of the viewport, as in Obsidian, with the field at the top and the list below. On the phone a sheet from below, as search is, with the field near the thumb. Esc, a click outside and ✕ on the phone close it; focus returns to where it was.

### Rows

Each row shows its **name** on the left and, muted on the right, **where it is**: a command's group, a note's vault and folder (`Work / projects`). On the phone the place stands below the name. Each row ends in a **star**: on the desktop shown on hover, focus or when set; on the phone always shown. Arrow keys move the highlight, Enter opens the row, **Ctrl+D** (Cmd+D) toggles the highlighted row's favourite, caught only while the palette is open (as a browser's bookmark).

| Group | Rows | Opens |
|---|---|---|
| Commands | Settings: Appearance, Vaults, Access (Keys, Connections, …), Skills (Installed, …), Calendar, Security, Data, About; Admin: Users, Invitations (admins only); Open: Recent, What's on, Console; Colour scheme: one row per scheme | the route of the window (`web/src/router.tsx:132–169`), or the theme setting |
| Vaults | each vault the session lists, and per vault "Board", "Calendar", "Gallery" | `/notes/:vault`, `/board/:vault`, `/calendar/:vault`, `/gallery/:vault` |
| Notes | the notes of the vaults the session lists, outside `_archive/` | the note |
| (last row) | "Search notes for ‘…’", always, once something is typed | the search sheet (`setOverlay("search")`) with the text as its query |

The exact command list follows the routes and tabs as they are when it is built; a command appears only where its route does for the person (admin rows for admins).

### Filtering and order

- Every word typed must appear somewhere in the row's place or name, in any order, case-insensitive: "set vau" finds "Settings: Vaults", "plan work" finds `/Work/plan.md`.
- Order: favourites, then commands, vaults, notes, then the search row. Within a group, rows where a word matches at the start of a word come first, then the rest; alphabetical within each.
- At most 50 notes are shown, so a large vault doesn't slow the list.

### The empty palette

Before anything is typed: the favourites, then **Recent**, the last five notes opened (the editor's existing `recentPaths`, `web/src/state/editor.ts:43`). Without favourites, one line in their place: "Star anything here to keep it on top."

### Outside the palette

A note's actions menu (`noteActions` in `web/src/shell/Dock.tsx` and `web/src/shell/MobileShell.tsx`) gets "Add to favourites" / "Remove from favourites".

## Favourites: storage

Favourites are stored per person on the server, so they follow her between laptop and phone (why not browser storage). They are stored in **two parts by one rule** the security architecture already has: what names no vault may be stored in the clear, as the calendar's time zone is (`docs/security-architecture.md:301`). Decided by the maintainer.

- **Commands** (no vault in them: Settings: Appearance, Admin: Users, Theme: Dark) are a plain list of command ids on the user document. They survive a rotation of the user key.
- **Everything bound to a vault** (a vault, its board, calendar or gallery, a note) is a list sealed under a key derived from the user key, as `calendarViews` is (`src/Kumbu.Server/Accounts/CalendarSettings.cs`, label in the form `kumbu/v1/favourites`). Each entry holds the vault id, the kind and, for a note, its path inside the vault, never the vault's name. After a rotation of the user key the list no longer opens and these favourites are gone; the maintainer accepts that ("Verlust ist in dem Fall ok").

The web app shows both as one list, in the order added. The two lists are kept separately; their interleaving is by time added, so each entry carries when it was added.

**Discreet vaults.** The server answers only the favourites of vaults the session's store lists: a hidden discreet vault's favourites are left out of the answer, with no hint and no count, and come back once the vaults are shown. The palette's notes and vaults come from the same vault list and trees the shell uses, so they follow the discreet state already. Hiding the vaults again closes an open palette or drops its rows of hidden vaults at once.

**Stale entries.** A favourite whose note was moved, renamed or deleted, or whose vault is gone, is not shown. A move through Kumbu² should carry the favourite along where the server can see it (`mv` rewrites links already); where that isn't cheap, the web app drops a stale note favourite of a vault it lists the next time it writes the list, if it was added more than five minutes before that vault's tree was read (why: one starred on another device after the tree was read is not in it yet; the five minutes allow for the clocks of two devices). The server drops only the entries of a vault that is gone, or no longer hers, and does so on her next write: it keeps the entries of the vaults she has and the session hides, by the vaults it opened (not the discreet list, which still names a deleted vault), and puts the client's list in place of everything else (why on a write: only her own session can open the sealed list, so a vault's deletion can't reach the lists of its members; and why at all: such entries would count towards the cap for good). It never drops a note's entry as stale, so it can't take a hidden vault's entry for one.

**API.** Session-only, as the calendar view is (a key or connection gets 403): `GET /api/account/favourites` answers both parts as one ordered list; `PUT` writes the whole list (a person's favourites are few, so no per-item calls). Both answer a `version` of the list, a hash of what is stored, and a `PUT` names the version it was made from; another one is a 409 and nothing is written (why: a whole list written from a stale copy would undo another device's change). The web app then reads the list again and repeats its change once, by key, before it shows the error. The server checks each entry it receives: command ids by their form only (lowercase, dotted, at most 64 characters), since the commands live in the web app and a list on the server would drift from it, and the web app ignores ids it doesn't know; vault ids the session lists, paths as any note path is checked. A cap of 200 entries.

**A write while discreet vaults are hidden** (the builder check's question, settled by Claude, since it is the only way that keeps both decided rules, "no hint" and "back once shown"): the `PUT` replaces only what the session can see. The server opens the sealed list, keeps the entries of vaults this session doesn't list, as they were and where they were in the order, and puts the client's list in place of the rest; then it reseals, with a conditional write as `CalendarSettings` does for the views (`src/Kumbu.Server/Accounts/CalendarSettings.cs:108`), so two devices writing at once lose nothing. The kept entries count towards the cap, and the answer to an over-cap write says only that the list is full, with no count of what is hidden.

## Left to the builder

- The exact command ids and labels, from the routes and tabs at build time.
- The cap, the sealing label and the document fields, following `CalendarSettings`.
- Whether the palette loads the trees of vaults it hasn't loaded yet when it opens (React Query caches them), and how it stays fast with many vaults.
  Settled: it reads a vault's tree (`GET /api/vaults/{vault}/tree`, taken from the cache within a minute) when that vault holds a note favourite, at once, and every other listed vault's once something is typed (why: the empty palette shows no note of a tree but a favourite, and a palette opened for a command then asks for no tree at all).
- How a move carries a note favourite along, or the simpler drop of stale entries.

## Tests

- Server (xUnit): commands stored in the clear and kept over a key rotation; vault entries sealed (no vault id or path readable in the document), lost after a rotation; a hidden discreet vault's favourites absent from the answer, kept through a write made while they are hidden, and back when shown; unknown command ids, unlisted vault ids and bad paths refused; keys and connections refused.
- Web (Vitest): the filter (words in any order, case-insensitive, place and name), the order of groups and of matches, the 50-note cap, the empty palette with and without favourites, Ctrl+P in Write mode and not over a dialog, Cmd+P on a Mac with Ctrl+P left to the editor, Ctrl+D toggling, the search row handing over its text, hidden discreet vaults leaving no row.
