# The UI round of 0.5.0

Status: approved by Chris, 2026-11-15. The UX review was held with Chris the same morning, point by point; his decisions and reasons are below. An independent read against the code corrected the draft the same morning (the existing toolbar, the writes, the menus).

## 1. Goal and decisions

0.5.0 goes out once this round is in: the UI finds from Chris's hand tests of `v0.5.0-pre.1` (list in Kumbu at `/SWE/Kumbu/Tests/Test list 0.5.0-pre.1.md`, "For the UI round") that the fix run (#203) didn't take. This spec is self-contained: everything needed from that list is written here. Everything here is in the web app; the server is not touched.

Decisions, each with Chris's reason:
- **Into 0.5.0, not a 0.5.1.** Chris, 2026-11-14: "ich möchte 0.5.0 so nicht rausgeben … lieber 1-2 tage später und dafür voll super".
- **The properties' position: a hint with "Move to properties", and "+ Add property"; no properties editor of its own** (§2.2). Chris: it solves the problem at the Pareto level, and he expects it to carry us for quite a while.
- **The explorer's toolbar covers the everyday 80 %; the rest stays one step away in ⋯** (§2.1). Chris: the quick access must cover 80 % of the cases, the other 20 % can take the detour, and we adjust as feedback comes in.
- **The calendar's four finds as proposed** (§2.3), including the What's on count moving onto the phone's new calendar button.
- **Nothing is locked in `Calendar/` or `Board/`** (§2.4). Chris: we trust people not to use `Calendar` as "I'll just copy everything in there".

Kumbu²'s principles hold for every item:
- **Campy:** notes are files and frontmatter, with no stores or layers beside them.
- **Human-centric, not process-centric:** the screen speaks of what the person wants, not of our fields.
- **Invite and lead.**
- **Honest:** a button does what it says; nothing is moved or hidden quietly.

## 2. The changes

### 2.1 The explorer's toolbar (hand test step 6)

Today, under the vault's name, the tree's toolbar (`web/src/shell/TreeTools.tsx`, rendered in `TreeDrawer.tsx` around line 707) has a search icon ("Find in this vault", which opens Find by name with the flag chips), the large Name / Last changed switch, and a "Recent changes" link on a row of its own. The vault's ⋯ sits top right beside the vault's name (`FolderMenu` with `extra={vaultItems}` in the panel's header), far from the tree: Chris had to think about how to make a new note.

- **`TreeTools` becomes one slim row directly above the folders**, in the docked sidebar and in the phone's drawer alike. In this order:
  1. **+ New note**: the only item with text, the accent's button; it makes a note in the vault's root, as the vault menu's New note does today.
  2. icons, each 48 × 48 px with its name as its accessible label and as a tooltip on hover: **Upload** (into the vault's root), **Calendar** (the vault's calendar; it carries the vault's share of What's on, as the vault menu's Calendar does today), **Board** (only when the vault has one), **Recent changes** (the clock, as today's link; still across the vaults, `/recent`), **Sort** (one icon; see below), **Find in this vault** (the search icon of today: Find by name with the flag chips, and Search the contents… inside it, unchanged);
  3. **⋯**: the vault's menu, the same items as today. **The ⋯ beside the vault's name goes**: one menu, in one place.
- **Sort** is one icon button that switches between Name and Last changed; the radiogroup goes. Its label says what it shows now and what a tap does ("Sorted by name. Sort by last changed"). The order is kept as today (`useTreeOrder`, across the vaults).
- **The vault's menu** keeps Calendar and Board as items too (people look there as well), and gains **Recent changes** as an item.
- **Where the row is too narrow**, as in the phone's drawer at 375 px or a narrow docked sidebar (it can be resized, so the row follows its width live): + New note, Calendar, Board, Find and ⋯ stay in the row; Upload, Sort and Recent changes move into ⋯, at its top. Whatever fits at 48 px stays, in the order above, the rest goes into ⋯; + New note keeps its text and ⋯ always stays.
- While Find by name is open, the row is today's search row (field, ✕, chips), as today.
- **What a person can't do isn't offered**: without write, no + New note and no Upload, as the menus decide today.
- **Flag chips** in Find by name wrap onto further rows instead of scrolling, and show only the flags that some note of this vault carries. A chip that is pressed stays shown while it is pressed, even when no note carries its flag any more. Only here: the chips elsewhere (Recent changes) stay as they are.

### 2.2 Properties at the top (Chris's alias test, 2026-11-14)

Chris wrote `---` / `aliases: [kumbu]` / `---` below `# aliastest`. Markdown reads that as a rule and a setext H2 (`aliases: [kumbu]` over `---`), shown in the H2's style (small orange caps), which he took for a property. And he couldn't find a way to add a property in the web app.

- **The hint.** In Read mode, a block in the body (not at the very top) that looks like frontmatter gets a notice right above it: **"Properties go at the very top of a note."** With write, it has the button **Move to properties**; without write, only the sentence.
  - *Looks like frontmatter* means: a line that is exactly `---`, then one or more lines that each read as a YAML mapping's key (`key: value`, `key: [a, b]`, `key:` followed by `- item` lines), then a line that is exactly `---`; with no line that `properties.ts` couldn't read. Anything else is left alone: a rule, a setext heading over prose, a table. When in doubt, no hint.
  - Code blocks and the note's real frontmatter are never looked at.
- **Move to properties** moves the block's keys into the note's frontmatter, created at the very top if the note has none, and removes the block (both `---` lines included) from the body. It is one write, with the note's version as every write here has, so a change elsewhere meets the conflict handling of today.
  - If a key of the block is in the frontmatter already, nothing is moved, and the notice says which key: "`aliases` is already in the properties: change it there." Never merged quietly.
  - Every byte of the frontmatter's other lines and of the body outside the block stays as it was, as `CalendarService` and the flag editor keep them on the server today. Here the change is made in the web app on the note's text and saved with the plain note save (`useSaveNote`, after the editor has settled, with the note's version); no new endpoint.
  - It is never done without the tap.
- **+ Add property.**
  - At the bottom of the properties box, open or as it is on notes that don't fold, a **+ Add property** button.
  - A note without frontmatter has no box; there, the note's ⋯ menu has **Add property…** (with write). The box's button opens the same dialog.
  - The dialog asks for a **name** and a **value**. Adding writes `name: value` as the last line of the frontmatter, creating the frontmatter at the very top when there is none, keeping every other byte. The value is text. It is written plain only when it is plainly safe (letters, digits, spaces inside, and `-`, `_`, `.`, `/`, not starting with a digit or a sign, and not `true`, `false`, `yes`, `no`, `on`, `off`, `null` or `~` in any case), and in double quotes otherwise, with `\\` and `\"` escaped; an empty value is `""`. Nothing is guessed about types.
  - A name that is already there is refused with "`<name>` is already in the properties: change it in the note." A name must be a plain key: letters, digits, `-` and `_`, starting with a letter.
- **No YAML library** is added: the web app reads frontmatter by hand (`web/src/note/properties.ts`), and the checks above are line patterns, conservative by design.
- **No properties editor of its own** in this round (an area above the text with `---` only in a source mode). That stays in the backlog.

### 2.3 The calendar (hand test block E)

1. **Back to the calendar.** A note opened from a calendar view (a vault's week or month) or from What's on has **‹ Calendar** (or **‹ What's on**) in its header, 48 px high, before the calendar line. It goes back to the view it came from, the same week or month, with the same filter. A note opened any other way doesn't show it. The way it came is carried in the navigation's state, not in the address; a reload or a link shows the note without it.
2. **The folders open their views.** In the tree, a tap on the name of the folder `/<vault>/Calendar/` opens the vault's calendar; a tap on the name of `/<vault>/Board/` opens the board, when the vault has one. The chevron in front of the name still opens and closes the folder. Only these two folders, at the vault's root; every other folder behaves as today. The row's accessible name says what the tap does ("Calendar, open the calendar"), and the chevron is a button of its own ("Show the folder Calendar").
3. **The calendar on the phone.** The dock at the bottom (`web/src/shell/Dock.tsx`: Files, Search, ⋯, +) gets a **Calendar** button between Files and Search, 48 × 48 px, that opens What's on. It carries What's on's count and its overdue state, as a badge on its corner. The count leaves the file tree's button in the top bar (`web/src/shell/TopBar.tsx`, the phone branch), which keeps only its own job. The desktop is unchanged. The dock shows in Read mode only, as today, so the count isn't on screen in Write mode; that is accepted.
4. **A to-do, or just an appointment.** Wherever an entry's menu is offered today (`calendarItems` in `web/src/calendar/DateDialog.tsx`: the note's ⋯ and the tree's file menu), an entry with a date gets **Make it a to-do** when it has no `task`, and **Just an appointment** when it has one. Both go through the calendar's existing write (`set` with `task: true` or `task: false`), which keeps every other byte. **Just an appointment** is offered for an open to-do only: a closed one keeps its `closed` and its reason, which `task: false` would drop. Not offered for board cards (their state is the column) nor for day and week notes, which are entries by their name. Without write, neither is offered.

### 2.4 The `Calendar` and `Board` folders stay ordinary folders

Chris asked in hand-test step 6 whether `Calendar/` should take uploads, notes and sync at all. It does, as today: a lock would have to hold in the web app, sync, WebDAV and `sh` alike, and would still surprise Obsidian and Dolphin. A note there without a date is simply not on the calendar. Nothing to build; this section records the decision.

## 3. End-to-end example

Alice works in `Work` on her phone, at 375 px.

1. She opens the drawer. Below "Work" she sees **+ New note**, the calendar, the board and the search icons, and ⋯; Upload, Sort and Recent changes are at the top of ⋯. She taps Find: the flag chips show red and blue only, the two in use, without scrolling.
2. She taps **+ New note**, writes `# Standup`, then below it `---`, `aliases: [daily]`, `---`, and taps Done. In Read mode, above that block: "Properties go at the very top of a note." and **Move to properties**. She taps it: the note now starts with `---` / `aliases: [daily]` / `---`, then `# Standup`; the properties box shows `aliases`.
3. In the box she taps **+ Add property**, enters `owner` and `Alice`. The frontmatter now ends with `owner: Alice`; nothing else changed.
4. In the dock she taps **Calendar**, with its badge "2": What's on opens. She taps an entry, "Dentist", Thursday 14:00. The note opens with **‹ What's on** in its header. In its ⋯ she taps **Make it a to-do**: the calendar line shows the checkbox ☐ To do. She taps **‹ What's on** and is back where she was.
5. On the laptop, in the docked sidebar, she taps the folder name **Calendar**: the calendar of `Work` opens. The chevron in front of it opens the folder in the tree, showing its day notes.

## 4. Tests

- Unit tests (Vitest) for each change: the block detection of §2.2 with its edge cases (a rule, a setext heading over prose, a block in a code fence, a block whose lines don't parse, two blocks), the moving and the adding with byte preservation and the refusals, the quoting of values, the toolbar's cut by width, the chips' selection, the folders' taps, the back link's state, the to-do switch.
- End-to-end tests in the UI suite (`web/e2e/`, the 0.5.0 run): the example of §3, at 375 px with touch and at 1280 px, in the light and the dark scheme. Tests that the changes alter (the sort switch, the vault menu's place, the badge's place) are updated; none is loosened.

## 5. Anti-goals

- Moving or rewriting anything in a note without the person's tap.
- A new store, setting or server endpoint; this round is a view over the existing writes.
- A second menu with the same items in two places (the old ⋯ beside the vault's name goes).
- Hints where nothing is wrong: when in doubt about a block, no hint.

## 6. Security boundary

- The server is not touched. Every write goes through the existing note write with its version token and the person's rights (`mayChange` in `web/src/api/notes.ts`; a board card wants `maintain`).
- Values a person types into Add property are written as YAML text, quoted where needed, never interpreted; the note view renders them as today (sanitised).
- Discreet vaults: nothing here names a discreet vault where it isn't shown; the dock's badge is What's on's, which already leaves hidden vaults out.

## 7. Out of scope

- A properties editor of its own above the text (backlog).
- QR codes for shares, and the invitation by QR code's warning (0.6.0).
- The PWA's icon badge on KDE.
- The free date range for `calendar`.
- Anything in `docs/superpowers/open-items.md` not named here.
