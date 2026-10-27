# Kanban Board — Design

**Date:** 2026-10-27 · **Status:** agreed in chat, written for review · **Release:** v2.5 (0.2.5)

## Intent

A vault can carry one board: cards in columns, ordered within each column, as in Trello. The first look at the board is a planning look (what is due next, in which order, what waits), and most of the work on it happens in a conversation with an AI: "let's see what we get done tonight", ideas turned into cards with the AI's questions making them better, cards moved as the plan takes shape. The user moves cards by hand about as often (70 % by hand, 30 % in the conversation, by the maintainer's estimate).

The earlier leaning in `docs/concept.md` ("its own data model, not a note") stood for one value: **moving a card is an atomic operation with tools of its own**, not text editing. This design keeps that value and stores the board as notes: **a card is a note**, its place on the board is in its frontmatter, and the server reads and changes that place. Cards then are on a par with notes without any of it being rebuilt: history, undo, checkpoints, links and backlinks, `mv`, the trash, search and the Obsidian sync all apply as they are.

## Goals

- One board per vault at most ("one vault, one board", `docs/concept.md`, "Kanban boards"), defined by a note, `/Board/Board.md`.
- Columns are free, with a template that suits the planning look: Ideas · Backlog · Today · Doing · Done · Rejected.
- Order within a column, set by drag and drop, by "Move to…", or by an agent; each move changes exactly one note.
- Agents read the board in stages (an overview of titles, then `cat` of a card) and change it with an MCP tool whose actions say what is to happen: add, move, update, create, rename a column.
- Done and Rejected tidy themselves into `/Board/Archive/` once they hold many cards, and only cards that have lain there a while.
- Rejecting a card takes a reason, kept on the card.
- Cards may carry `tags` (the Obsidian property) and a due date (`due`).
- The web app shows the board phone-first (one column per screen, "Move to…") and on a laptop as columns side by side with drag and drop, "Move to…" there too.

## Non-goals

- Who works on a card (an assignee): one person's board until sharing comes in v4, if ever.
- A board view in the Obsidian plugin: later, alongside v4/v5. In Obsidian, `/Board/` is a folder of notes; a card moves there by editing its `column`. The format is chosen so that the plugin can show and change it later without a migration.
- Import and export in the Obsidian Kanban plugin's format (`docs/concept.md` planned it): cards are notes already, which Obsidian reads.
- A general tag search over all notes: a feature of its own, later, which then covers cards too. The board shows tags and filters by one.
- Several boards per vault, nested boards, sub-folders of cards (other than `Archive/`).
- Time of day in dates: days are enough for planning and for the archive rule.

## Anti-goals (it failed if…)

- …moving a card can lose text someone was writing on it at the same time (in Obsidian, in the web app, through `edit`).
- …moving one card rewrites other cards (except a rare re-ranking of one column, below).
- …the server reformats frontmatter it doesn't own: a move changes only its own keys' lines.
- …a card silently disappears from the board because its frontmatter is wrong.
- …the board changes on its own while nobody touches it.
- …the web app can do something to the board that an agent can't, or the other way round.

## Data

### The board: `/Board/Board.md`

An ordinary note. Its frontmatter is what the server evaluates; its body is for people and the AI.

```markdown
---
kumbu-board: 1
columns:
  - Ideas
  - Backlog
  - Today
  - Doing
  - Done: [archivable]
  - Rejected: [archivable, reason]
archive: { after: 30, minDays: 14 }
---
# Kumbu²

Kanban board for the work on Kumbu².

## Instructions

Don't let ideas go stale: when Today and Backlog run short, look at Ideas. Reject a card only with a reason.
Today is what comes next, not literally today.
```

- `kumbu-board`: the format's version; a board without it, or with a version the server doesn't know, is shown as a problem, not guessed at.
- `columns`: in order, each a name or `Name: [flags]`. Names are unique (ignoring case), 1 to 64 characters, no `/`. Flags: `archivable` (the column tidies into the archive), `reason` (a card moves there only with a reason).
- `archive.after`: how many cards an archivable column holds before tidying (default 30); `archive.minDays`: how long a card stays in it at least (default 14). Both per board.
- `## Instructions`: the section the `board` reader prints first, so the AI works the way this board is meant to be worked. The template's text is English (product text); the user rewrites it in their own words.
- `Board.md` is not a card. Nor is anything below `/Board/Archive/`, which is the archive, nor anything in a sub-folder of `/Board/` (listed as a problem).

### A card: `/Board/<Title>.md`

The title is the file name, as in Obsidian. Below the frontmatter is free text, with links to notes as anywhere.

```yaml
---
column: Doing
rank: a0V
due: 2026-11-03
tags: [security]
moved: { Backlog: 2026-10-27, Today: 2026-10-28, Doing: 2026-10-29 }
---
```

- `column`: the column's name as `Board.md` spells it (matched ignoring case).
- `rank`: the place within the column, a sortable key (below). Missing: the card sorts after those that have one, by title.
- `due`: a date, `YYYY-MM-DD`. Past it, the card is overdue.
- `tags`: the Obsidian property, a list.
- `moved`: the day the card last entered each column, by column name. Written by every move and add; the archive rule reads it.
- `reason`: why the card was moved into a `reason` column, written with that move.
- `archived`: the day the card went into the archive.

**Problems** are cards the board can't place: no `column`, a column `Board.md` doesn't name, frontmatter that doesn't parse, a sub-folder of `/Board/`. They are listed as problems (the web app shows them red), never dropped.

### Ranks

Ranks are strings over `0-9A-Za-z`, compared ordinally ("fractional indexing", as Trello, Linear and Figma use it): `Between(a, b)` gives a key strictly between two neighbours (either may be missing, for the top or the bottom), so a move gives a new key to the moved card only. When a key would grow past 24 characters, the server re-ranks that column evenly in one changeset: one write per card of the column, which happens only after many insertions at the same place.

### Reading and writing frontmatter

- Read with a YAML parser (YamlDotNet, a new dependency of `Kumbu.Core`); `Outline.FrontmatterEnd` already finds where frontmatter ends.
- Written surgically: the server replaces, adds or removes only the lines of its own keys (`column`, `rank`, `due`, `tags`, `moved`, `reason`, `archived`) and leaves every other line of the frontmatter as it is, byte for byte. A note without frontmatter gets a block at the top. A key the server owns that is written in a form it can't rewrite in place (a multi-line `moved:` block, say) is replaced as a whole, in the one-line form above.

## Tools for agents

### Reading: `board`, a new reader in `sh`

```
board <vault> [--column <name>] [--tag <tag>]
```

Prints the board's `## Instructions`, then the columns in order, each with its count and its cards' titles by rank, with `due` (marked when overdue) and tags; long columns are cut after 20 titles with a hint (`board /Kumbu² --column Ideas` for all); archivable columns show only their count unless asked for by `--column`; problems come last, with what is wrong. Details come from `cat` of a card, as for any note. `board` is a reader: it takes `--checkpoint` and so appears in `help checkpoints`. Without a board: "no board in /Vault; the board tool's create makes one when the user asks for it".

```
## Instructions
Don't let ideas go stale …
Doing (2)
  Export needs the password      due 2026-11-03  #security
  Kanban spec
Today (3)
  …
Ideas (41): first 20
  …
  board /Kumbu² --column Ideas for all
Done (37) · Rejected (5): archivable; board /Kumbu² --column Done
Problems (1)
  /Kumbu²/Board/Old idea.md: no column
```

### Writing: the MCP tool `board`

One tool, an `action` and the arguments of that action, each card named by its title (or its path):

- `add`: `title`, `column`, optional `text`, `position`, `due`, `tags`. Creates `/Board/<title>.md`. Fails if the card exists.
- `move`: `card`, `column`, optional `position`, and `reason` where the column requires one (refused without).
- `update`: `card`, `due` and/or `tags` (set, or `null` to remove).
- `create`: makes `Board.md` from the template; only when the user asks for a board, as `mkcp` is only for when the user asks.
- `rename_column`: `from`, `to`; renames in `Board.md` and in every card's `column` and `moved`, all in one changeset or nothing, as `replace` applies a plan.

`position`: `top`, `bottom` (the default) or `{ "before": card }` / `{ "after": card }`.

`move` and `update` take no version token: the server reads the card, changes only its own frontmatter lines and writes it back in one step under the vault's write gate. The text of the card is not touched, so nothing anyone was writing is lost. The card's text goes through `write` and `edit` with their tokens, as for any note.

The tool's description tells the agent how to work the board: read `board` first and follow its Instructions; for "what's next", start with Doing, Today and Backlog, and bring in Ideas when they run short; give a reason when rejecting; create a board only when asked.

## Server

- **`BoardReader`** (`Kumbu.Core`): a pure function from `Board.md` and the notes under `/Board/` to a `Board` (columns with their cards in order, problems), over `INoteReader`, so it works on a checkpoint's view as on today's notes. Unit-tested without a database.
- **`BoardService`** (`Kumbu.Core`): the five write actions, on `NoteWriter` and `NoteMover`. The MCP tool and the REST endpoints both call it; neither can do what the other can't.
- **Archive, without a background job:** after each `add` or `move` into an archivable column, if the column holds more than `after` cards, the oldest of them by `moved[column]`, among those at least `minDays` there, go to `/Board/Archive/` until it holds `after`, each with `archived:` set. They go with `mv`, so links follow and the history has it; the writer is the caller's, with the tool label `board`. A board nobody touches never changes.
- **Rights and identity:** as for notes. Reading the board needs read on the vault, changing it write. Each change carries its writer in the history.
- **Concurrency:** two moves of the same card: the later one wins, both are in its history, neither blocks the other.
- **REST** for the web app, mirroring the tool: `GET /api/vaults/{vault}/board` (the `Board` as JSON, problems included; `?checkpoint=` for a checkpoint's), `POST /api/vaults/{vault}/board` (`create`), `POST /api/vaults/{vault}/board/cards` (`add`), `POST /api/vaults/{vault}/board/move`, `/update` and `/rename-column`. Errors as everywhere: `{"error": "<sentence>"}`.

## Web app

- **Entry:** the vault's ⋯ menu shows "Board" when the vault has one, else "Create board…" (the template, one click). Route `/board/<Vault>`.
- **Phone:** one column per screen, a bar of column names with counts to tap, swiping between columns. A card shows its title, `due` (red when overdue) and tags; tapping it opens the note in the editor. Its ⋯ menu: "Move to…" (the columns, then top or bottom), "Up", "Down"; a `reason` column asks for the reason. Problems show as a red bar at the top, each opening its card.
- **Laptop (from 1024 px):** all columns side by side, scrolling sideways; archivable columns narrow and collapsed, with their count. Drag and drop with the browser's own drag and drop (no new dependency), and the same ⋯ menu, for the keyboard and for anyone not at ease with a mouse. "+ Card" under each column: a title and optional text.
- **Up to date:** the board follows changes from elsewhere (an agent, Obsidian, another tab) with the polling the web app already does.
- **Checkpoints:** the checkpoint view shows the board as it was, read-only.

## Testing

- xUnit for `BoardReader` (columns, ranks, problems, archived and sub-folders left out), rank generation (`Between`, re-ranking), the surgical frontmatter writer (other lines unchanged byte for byte), `BoardService` (each action, the archive rule's two criteria, `reason`, `rename_column` all or nothing, a move while the card's text changes keeps the text).
- The `board` reader's output, `--column`, `--tag`, `--checkpoint`.
- Vitest for the board's views (phone and laptop), "Move to…", drag and drop, the reason dialog, problems.
- After building: Claude uses the tools hands-on, as the agent that will use them, on a test board, and writes down what it noticed; a playbook for Claude on claude.ai as for 0.2.2.

## Changes elsewhere

- `docs/concept.md`, "Kanban boards (v2.5)": replaced by a short summary of this design (a card is a note; tools for atomic moves; no own data model, no Kanban-format import or export).
- README, the welcome vault (a short section on the board in "Working by hand" and "Your AI in Kumbu²") and the security document (§4: what a key's rights mean for the board; nothing new is stored).

## Open for the plan

- Whether `rename_column` reuses the `replace` plan mechanism or a transaction of its own.
- The exact JSON shapes of the REST endpoints.
- How the phone view swipes between columns (CSS scroll snap is the likely answer).
