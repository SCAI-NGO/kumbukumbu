# Flags, the explorer's search and order, recent changes, Take photo, and the calendar's fixes

Date: 2026-11-14. Status: approved by the maintainer on 2026-11-14; builder check done (its three questions settled as Claude's calls in §2.2, §2.4, §4 and §6.2).

The last features of 0.5.0, after the calendar (#197). They share one idea: in a vault people work in together, you should find what matters and what just changed at a glance, without searching for it. Obsidian can't offer much of this because it isn't collaborative; Kumbu² is.

The principles of the calendar spec (`2026-11-12-calendar-design.md`) apply unchanged: **campy** (notes are files and keep their meaning in human-readable semantics, little abstraction, Outlook as the counter-example) and **invite and lead** (a screen speaks of what the person wants, never of our fields or states). The decisions below were taken by the maintainer on 2026-11-14 in conversation; each carries its reason. Claude's own calls are marked *(Claude's call)*.

## 1. Goals, non-goals, anti-goals

Goals: §2 to §6.

Non-goals:

- **Personal bookmarks** ("only for me", like a star in Gmail). A flag belongs to the note (§2.1). A per-user marker would need a sealed per-user store and would be invisible outside the web app; it comes only if someone asks for it.
- **Semantic search in the explorer.** The search sheet already searches by meaning; the explorer filters by name and flag and hands over to it (§3.1).
- **Anything after the photo is taken** (§5): no processing, no hand-over to an AI, no target-folder picker. *Maintainer:* the AI is triggered by the user or by an agent they set up (for example a skill in Claude Code that looks for new photos every few minutes); that belongs to the AI side, not to Kumbu².
- More than one flag per note. *Maintainer:* "one flag, no over-engineering".
- An `sh` command for flags *(Claude's call)*: a flag is a tag, so agents read it with `tags` and `rg --tag` and set it by editing the frontmatter, as with any tag; `help tags` says so.

Anti-goals:

- **A second truth.** A flag is a tag in the note and nothing else; no field, store or index beside it. A board card's status stays its column.
- **Discreet vaults revealed.** Recent changes, the explorer's filters and the flag counts tell a session nothing about a discreet vault it isn't showing.
- **A button that doesn't do what it says.** Take photo is offered only where it opens a camera (§5).
- **An Outlook** (as in the calendar spec): every special function needs a reason it would be missed.

### An example from end to end

Alice owns the vault `Work` and shares it with Bob, a read-only member.

- Alice opens `/Work/Pump/Certificate.md`, taps ⋯ → **Flag** → **Red**. The frontmatter's `tags` gains `flag/red`, nothing else in the note changes; the header and the tree show a red flag beside the name.
- Bob opens the tree with **Last changed**: `Pump/` is at the top, because Certificate changed last. He opens **Recent changes**: "Certificate · 🚩 · Work/Pump · Alice · 2 min ago" is the first row. He types "pump cert" and taps the red flag chip; the row stays. He has nothing to set: no flag menu, no Take photo.
- Alice's agent runs `tags flag` and `rg --tag flag/red` and finds the note.
- Certificate is also an open task in the calendar (`task: open`). Alice sets ✓ (Done) on it; Kumbu² asks "Also mark it done in the calendar?", she taps **Mark done**: the note now carries `flag/done` instead of `flag/red`, and `task: closed`, `closed: <today>`. One Undo would take back both.
- After the meeting Alice opens the folder menu of `Work/Meetings/` on her phone, taps **Take photo** three times for three whiteboards: `Photo 2026-11-14 153012.jpg` and two more land there. Her agent, which looks for new photos, writes the meeting's note from them.
- In the explorer she taps the search, types "photo meet": only the three photos and their folder remain.

### Security boundary

New: writing a flag (a change to a note's frontmatter `tags`, through the same stores, checks and compare-and-swap as the calendar's writes), the tree's new fields (§3.2, §2.3), the recent-changes read (§4), and the photo upload (an ordinary upload through the existing upload path). Every read and write goes through what the caller may read or write today: a read-only member sees flags and recent changes, sets no flag and takes no photo. Not touched: `Auth/` and `CallerMiddleware`, `Crypto/` and encryption at rest, sharing and roles, publishing, WebDAV (`src/Kumbu.Server/Dav/`), sync, OAuth, the board's semantics, both CSPs in `src/Kumbu.Server/Api/Security.cs`. No persistent index, no stored copy of notes: like the calendar, everything is read on demand.

## 2. Flags

### 2.1 What a flag is, and whose

A flag marks a note at a glance: "very important", "something needs doing here", "hm, let's look at this".

**A flag belongs to the note, so every member who can read the note sees it, and everyone who may write the note may set or change it.** *Maintainer's reasoning:* a shared vault is a deliberate act; only its owner shares it, with people they trust. What is in it is shared knowledge, and so are its flags. In an own vault, "for us" and "for me" are the same.

### 2.2 A flag is a tag

*Maintainer:* "flag search also works through tag search, a wonderful format." A flag is one of eight tags, nested under `flag`:

| Tag | Shown as |
|---|---|
| `flag/red` | red flag |
| `flag/green` | green flag |
| `flag/blue` | blue flag |
| `flag/purple` | purple flag |
| `flag/yellow` | yellow flag |
| `flag/white` | white flag |
| `flag/done` | green tick ✓ |
| `flag/important` | exclamation mark ! |

So everything that filters by tag filters by flag already: the search sheet's tag filter, the calendar and What's on (`--tag`), `rg --tag` and `semsearch --tag` for agents, and Obsidian's tag pane. `flag` alone (the parent) matches every flagged note. Any other tag below `flag/` (`flag/maybe`, `flag/red/x`) is an ordinary tag and no flag.

**Reading.** A note's flag is its first known flag tag in the frontmatter's `tags`; if there is none there, its first in the text (`#flag/red`). Further flag tags are ordinary tags *(Claude's call: one rule, no error for a hand-written second flag)*.

**Writing** (the web app only). Setting a flag removes every known flag tag from the frontmatter's `tags` and adds the new one; **No flag** removes them. The rest of `tags`, the rest of the frontmatter and every other byte stay as they are, in the list form the note already uses; a note without frontmatter gets one with `tags: [flag/red]`; a `tags` left empty is removed, and a frontmatter left empty too, so set then remove gives the note back. Only the flag items change: other items keep their quoting and comments, and the string form `tags: a, b` stays a string, so this needs a small line-level editor for `tags` beside `Frontmatter.Set` (which replaces a key's whole extent) *(Claude's call, after the builder check)*. Writes use the calendar's compare-and-swap path (`FrontmatterWrites`, as `CalendarService` does), including leaving Write mode first in an open editor. A flag written in the text is never changed by Kumbu²: when the note's flag comes from its text, the flag menu says "This flag is written in the note's text (#flag/red); change it there." *(Claude's call: we don't edit a person's prose.)*

### 2.3 Where it shows and where it is set

*Maintainer:* in the tree beside the name, in the note's header, where it is also set, and in recent changes (§4).

- **The note's header:** the flag as its icon; tapping it, or ⋯ → **Flag**, opens the eight choices and **No flag**, each with its icon and name ("Red", "Done", "Important"). Without a flag, ⋯ → **Flag** is the way in; a header without a flag shows nothing extra.
- **The tree:** a small icon after the note's name. The tree's data carries each note's flag (§3.2).
- **Recent changes:** the icon on its row.
- Read-only: the icon shows, nothing to set.
- Colours meet contrast in the light and the dark scheme; the white flag is drawn outlined. Icon and name tell them apart, never colour alone.

### 2.4 The tick and the calendar

*Maintainer:* when someone sets ✓ (`flag/done`) on a note that is an open task in the calendar (`task: open`), Kumbu² asks: **"Also mark it done in the calendar?"** with **Mark done** and **Just the flag**. Mark done is one write that sets the tick and closes the task (`task: closed`, `closed`), with one Undo that takes back both *(Claude's call: one action, one Undo)*. Not asked for a board card *(Claude's call: a card's status is its column)*, nor the other way round: closing a task in the calendar sets no flag *(Claude's call)*.

## 3. The explorer: search and order

### 3.1 Search by name and flag

A search icon at the top of the tree opens a field; closing it (or Escape) clears it and gives the full tree back.

- **Words:** case-insensitive, separated by spaces, **every word must match** (AND). *Maintainer asked; Claude advised AND:* typing narrows down, as in VS Code, Rider or Obsidian's quick switcher; with OR the list would grow with every word. A note matches when each word is found in its path within the vault (folders and name), so "pump cert" finds `Pump/Certificate.md`.
- **Flag:** beside the field, chips for **Any flag** and the eight flags; one at a time. Words and flag together: both must match.
- The tree then shows only the matching notes and attachments and the folders on the way to them, opened. Nothing matches: "Nothing called that here." with the next item.
- **Search the contents…** below the field opens the existing search sheet with the typed words, for search by words and meaning.
- It covers the vault the tree shows, as the tree does.

### 3.2 Order: by name or by last change

A switch at the top of the tree: **Name** (as today) or **Last changed**, newest first. *Maintainer:* in a vault several people work in, what moved last belongs at the top; Obsidian sorts files by time, but not folders.

- **A folder is as new as the newest change anywhere below it, at any depth** (*maintainer*). An empty folder counts by its marker's stored write time, read like a note's; without a marker it goes last.
- Folders stay above files on each level, both in the chosen order *(Claude's call: as `ls` and the tree do today)*.
- The choice is remembered on the device, as the calendar's grid is *(Claude's call)*, and holds for every vault.
- The tree's data gains, per note, its last write time and its flag (`TreeNote` in `src/Kumbu.Server/Api/NoteApi.cs`; attachments have `writtenAt` already).

## 4. Recent changes

*Maintainer:* a screen of its own listing the notes and attachments by their last change, with their path, reachable from the explorer, with the same filters: for a very quick "deep search".

- **Where:** a **Recent changes** item at the top of the tree, beside search and order; its own route (no vault in the address, so nothing about a discreet vault reaches the address bar).
- **A row** per note or attachment as it is now: its icon, title, flag, path (vault and folders), **who** changed it last and **when** ("Bob · 10 min ago", the wording of Activity). Newest first. Tapping it opens the note, or an image or PDF in the lightbox.
- **Filters:** the words match the path with its vault (`Work/Pump/Certificate`), since the row shows it *(Claude's call)*; chips for the vaults, **all on by default**, each switched off and on with a tap (*maintainer*); the words (AND) and the flag chips of §3.1.
- **Which files:** those that exist now, in the vaults the caller can read; not the archive, not the trash. A moved file shows at its new path. Hidden discreet vaults are absent entirely, chips included, as in What's on.
- How many: the newest first, with **Show more** below *(builder's call on the page size)*.
- It is not Activity: Activity is a vault's audit log of changesets (who changed which files when, history is an audit log); Recent changes is one line per file, across vaults, for finding things.

## 5. Take photo

*Maintainer:* for phones whose upload chooser offers no camera. The use case: a meeting with several whiteboards, flip charts and sketches, photographed one by one; an agent or a skill the user runs (in Claude Code, say) turns them into one Markdown note that reaches everyone ten minutes after the meeting. Kumbu²'s part is only the photo landing in the vault.

- In a folder's menu, beside **Upload here**: **Take photo**. It opens the camera (`<input type="file" accept="image/*" capture="environment">`).
- The photo is stored in that folder as `Photo YYYY-MM-DD HHMMSS.jpg` (local time, the format of the archive's stamps; the extension of the bytes actually stored, after the upload's own shrinking), through the existing upload path and its checks. A name already taken gets ` 2`, ` 3`.
- Then it is there, with the upload's usual confirmation. Nothing else happens.
- Offered only on touch devices (`(pointer: coarse)`), where the browser opens a camera *(Claude's call: on a desktop the browser opens the file chooser instead, and the button wouldn't do what it says)*. Only for those who may write the folder.

## 6. The calendar's fixes

From the calendar overnighter's report (`docs/superpowers/overnighter/2026-11-13-calendar-report.md`), decided by the maintainer on 2026-11-14. Each amends the calendar spec, which says so in its own text.

1. **Day and week notes by their name count in What's on and the badge only when they are a task.** *Why:* tapping **Today** made the badge show 1 every day, for nothing that's on. They still show in the calendar views.
2. **A timed entry is relevant from the start of its day.** An appointment or task with a time and an `until` (14:00–15:00) is in What's on from the start of the day it begins, as one with only a time already is; its state is ongoing from the start of that day, as for a time without `until`; only the words change: before its time both read "today · 14:00", within a window "ongoing · until 15:00" *(Claude's call, after the builder check: the states stay, the wording says what the person needs)*. *Why:* the spec's table and §2.4 disagreed; one rule for both.
3. **The app-icon badge (OS) never counts discreet vaults**, shown or hidden; the badge inside the app follows the session as today *(Claude's call)*. *Why:* the OS badge outlives the session; if the app is closed while a discreet vault is shown, it would keep a count that includes it.
4. **Calendar keys are read ignoring case** (`When:`, `TASK:`). Two spellings of one key in a note are listed under "Dates I couldn't read" rather than one winning; a write keeps the spelling that is there. Only the calendar's keys; the board and tags stay as they are.
5. **Overdue says years** from 12 months on: "overdue · 3 years", not "40 months".

## 7. What's left to the builder

- Which components carry the search, the order switch and the Recent changes item at the top of the tree (`web/src/shell/TreeDrawer.tsx`), on a phone as on a desktop.
- How the server lists recent changes (from the history it already keeps for `history` and Activity), within the reading cost: no new store, no index. Measure it for 5000 notes over the server, as the calendar run did, and put the number in the report.
- The flag icons' exact shapes and colours within §2.3.
- The page size of Recent changes.
- Where the new endpoints live and how they are named, following `BoardApi` and `CalendarApi`.
