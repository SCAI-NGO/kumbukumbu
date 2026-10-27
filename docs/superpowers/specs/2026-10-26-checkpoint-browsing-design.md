# Looking into a Checkpoint — Design

**Date:** 2026-10-26 · **Status:** agreed in chat, for v0.2.0 · **Release:** one PR, branch `checkpoint-browsing`, Chris merges

## Intent

A checkpoint keeps an earlier state of a note, a folder or a vault (concept §4), but today the web app can only make, restore or remove one. Restoring is all or nothing. The reader often wants something smaller: to **look** at the earlier state, find a thought again, and take it along, either by copying a passage into today's note or by handing it to their AI ("look, this is what we thought before — does it fit?").

The two moments it serves, in Chris's words:

- "What did things look like a week ago, and how did we see them then?"
- "We worked out concept xyz and dropped it later… it wasn't that bad, I hope it's still there."

Nothing in this design changes or writes notes. It is read-only from end to end.

## Goals

- From the Checkpoints sheet, a checkpoint opens as a read-only vault: its folder tree and its notes, rendered the way the web app renders notes today.
- It is impossible to mistake the checkpoint for today's state, on a phone as well as on a laptop.
- Text in a checkpoint note can be selected and copied normally, on a phone as well.
- One action per note hands it to the reader's AI without copying the whole text: **Copy for your AI**.
- A checkpoint exports as a ZIP that Obsidian opens as a vault, like the normal export.

## Non-goals

- Restoring a single note from a checkpoint in the web app. The AI can already do it in the shell (`restore <path> --checkpoint <name>`); restoring the whole checkpoint stays as it is.
- Comparing a checkpoint with today, or searching inside a checkpoint in the web app.
- Automatic checkpoints, or browsing the vault "as it was on a date" from the history. That is a later "Time Machine" on the roadmap; the history (10 versions per note) and the trash (30 days) stay as they are.
- Any change to how checkpoints are made, stored, counted or restored.

## Anti-goals (it failed if…)

- …a reader edits or deletes something while looking at a checkpoint, or believes they did.
- …a reader thinks today's note is gone because they are looking at the checkpoint, or the other way round.
- …the view or the export shows anything the checkpoint doesn't hold (a note from today mixed in), or anything outside what the reader may read.
- …it touches the write path, the sync or the stored checkpoints. This lands a day before a demo.

## What the reader sees

### The Checkpoints sheet

Each checkpoint in the list gets two more actions beside **Restore** and **Remove**: **Open** and **Export as ZIP**. Both work for every checkpoint the list shows, whatever it covers (a note, a folder or the vault).

### The checkpoint view

**Open** closes the sheet and shows the checkpoint in the shell, in place of today's vault:

- **A banner** sits at the top, under the top bar, for as long as the checkpoint is open. It says which checkpoint and when it was made, and that this is read-only, e.g. *"Checkpoint before-cleanup · 19 Oct 2026, 14:02 · read only"*, with a clear way back: **Back to today**. It uses a colour distinct from the normal shell, so a glance tells the two apart.
- **The tree** shows only what the checkpoint holds, at the paths the notes had then. A checkpoint of a folder or a note shows the folders above it, so the reader sees where it sits, and nothing beside it.
- **A note** renders as in Read mode today. There is no Write mode, no editor, no autosave, no move, delete, restore or "changed elsewhere" banner, and no vault menu actions that change things. The tools that only read (outline, find in note) may stay if they work unchanged; that is the builder's call.
- **Links** inside a checkpoint note resolve within the checkpoint: a link to a note the checkpoint holds opens that note in the checkpoint; a link to anything else is shown as not in this checkpoint, with no jump to today's vault.
- **Copy for your AI** on each note puts one short paragraph on the clipboard that any AI connected to Kumbu² can act on, naming the vault, the checkpoint, its date and the note, and the exact shell command that reads it. Wording, roughly: *"From my Kumbu² vault Ideas, checkpoint before-cleanup (19 Oct 2026): /Ideas/concepts/xyz.md. Read it with: cat --checkpoint before-cleanup "/Ideas/concepts/xyz.md""*. A short confirmation shows that it was copied. *(The idea is Claude's; Chris agreed.)*
- **Back to today**, the browser's back button and switching vault all leave the checkpoint. The address of the view can be its own route, so a reload stays in the checkpoint; the builder decides the route.

### The export

**Export as ZIP** downloads the checkpoint's notes in the format of the normal export (`ExportApi`): one top-level folder named as the vault, the notes verbatim at their paths then, so the folder opens in Obsidian as a vault. The file name names the vault and the checkpoint, e.g. `Ideas checkpoint before-cleanup 2026-10-19.zip`. Like the normal export, it is for a signed-in session only, never for a key or an app connection.

## Who may

Whoever may read the vault may open its checkpoints, as in the shell today. Export follows the normal export: a session only. Nothing here needs write rights. A vault that is locked or being re-keyed refuses as the other reading endpoints do.

## The server

Two or three read-only endpoints next to the existing checkpoint ones in `HistoryApi` (`/api/vaults/{vault}/checkpoints/{name}/…`): the checkpoint's tree, one of its notes, and its export. They read through what exists (`Checkpoints.OpenAsync` → `CheckpointView`, `INoteStore.ReadCheckpointAsync`) and reuse the export's ZIP writer; no new storage, no change to `Checkpoint` or the store. Unknown vault or checkpoint: 404, as the other checkpoint endpoints answer. The exact routes and response shapes are the builder's, kept close to `/tree` and `/notes/…` so the web app can reuse its tree and note components.

## The welcome vault

`Working by hand.md`, section Checkpoints, gains one sentence on **Open** and **Export as ZIP**, and that the AI can read a checkpoint too. Labels exactly as in the app.

## Settled after the builder check

- **Reading rights are per vault, not per folder** (`ApiKeys` holds a right per vault; there is no folder scoping). Whoever may read the vault may read all of its checkpoints; no filtering inside a checkpoint.
- **A folder or note checkpoint** holds only the notes it covered then (`CheckpointView` holds exactly those). Its view shows them at their paths then, with the folders above them; its export has the same layout as a vault checkpoint's: the vault's folder on top, the covered notes at their full paths below it, so it still opens in Obsidian. *(Claude's call.)*
- **Links are resolved on the server**, as today (`/api/links/…` returns `LinkView`s). The checkpoint view gets its links the same way, resolved against the `CheckpointView` (it is an `INoteReader`, so the resolver runs on it unchanged). A link whose target the checkpoint doesn't hold comes back unresolved and shows as not in this checkpoint.
- **Quoting in "Copy for your AI"**: the shell takes double quotes with `\"` and `\\` as escapes (`Tokenizer`). The path and the checkpoint name are put in double quotes with those two escaped. Checkpoint names can't hold either (letters, digits, `.`, `-`, `_`).
- **No move, damage, history or "changed elsewhere" indicators** in the view: a checkpoint has none of these (`CheckpointView` reports no moves and nothing damaged). *(Claude's call.)*
- **Read-only by construction, not by hiding**: the view takes its data only from the new read-only endpoints and never mounts the editor or the autosave. Whether that is a read-only mode of the existing tree and note components or thin components of its own is the builder's call; the write path must not change. *(Claude's call.)*
- **One open per request**: a request opens the checkpoint once (`Checkpoints.OpenAsync` reads all its notes) and answers from that view; no cache across requests. *(Claude's call; checkpoints are bounded by the vault's size, and opening one is a rare, deliberate act.)*
- **The welcome text** is pinned by `tests/Kumbu.Server.Tests/Welcome/WelcomeTextTests.cs`; the added sentence updates whatever that test checks.

## Left to the builder

The route of the checkpoint view, the response shapes, the banner's exact colour within the design tokens, which read-only note tools stay, the exact wording of the banner, the confirmation and the "not in this checkpoint" hint (plain British English, Chris reviews), and the tests: at least that nothing in the view can write, that links stay inside the checkpoint, that the export holds exactly the checkpoint's notes, and that a key or connection can't export.
