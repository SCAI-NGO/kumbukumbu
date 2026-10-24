# Changesets, Named Checkpoints and the Phase 6 Fixes — Design (step 6b)

**Date:** 2026-10-24
**Scope:** `docs/concept.md` §12, step 6b: changesets with ids in the history, named checkpoints (`mkcp`, `lscp`, `rmcp`, `--checkpoint`), and the fixes from [notes-test-phase6.md](../../notes-test-phase6.md) (observations 1, 2, 4–9; 3 stays an idea for later).
**Status:**
- Settled with Chris on 2026-10-24:
  - changesets
  - checkpoints with their limit and browsing
  - the replace warning
  - the three questions in §3
- The rest is written by Claude for review with the PR.

## 1. Intent

- A session's writes give each note **one version**, whatever tool made them, so a series of `replace` or `mv` calls never pushes the state before the session out of the history (phase 6, observation 2).
- A user can keep a state under a name for as long as they want, and read, compare and restore it like the current state.
- `replace` says when it would break links (observation 1). A plan says what became of it (observation 4). The smaller observations are fixed along the way.

Out of scope:
- Undoing a changeset across notes (observation 3, groundwork only: the ids).
- Export of a checkpoint (web UI, step 9).
- Restoring a note from the trash with its history as part of a checkpoint restore (it comes back as a new note with the checkpoint's content).

## 2. Changesets

| Topic | Decision | Why |
|---|---|---|
| What | All writes of one writer whose gaps are under 10 minutes, for 1 hour at most, are one changeset. Each note gets one version per changeset. | Concept §4. Replaces chapter 3's per-note 10-minute window. |
| Which tools | Every write: `write`, `edit`, `replace`, and the link rewrites of `mv`. The writer label still names the tool (`dev (replace)`, `dev (mv)`). | Decided by Chris (Q3 below). A long session must not fill the history with its own steps. |
| Never joins one | `restore` (also from the trash and from a checkpoint) and seeding. Their state has no changeset, so the next write never coalesces into it. | A restore is a checkpoint (concept §4) and must be undoable on its own. |
| Id | 16 hex characters, random. `history <folder>` shows the first 8. | Groundwork for undoing a changeset across notes. |
| Where it lives | On every note state (current and history entries): id and start time. The writer's open changeset is derived from the vault's notes: the writer's latest current state, if under 10 minutes old and the changeset under 1 hour. | No session state to keep or lose; survives a restart; notes from before 6b have none and simply don't coalesce. |
| Writer identity | The label up to " (": `dev (mv)` belongs to `dev`. | One writer, several tools. |

Consequence noted in the phase 6 notes: until step 8, everything is `dev`. Preparing a test fixture and running an agent on it within 10 minutes makes them one changeset. Keep 10 minutes between them, or a checkpoint.

## 3. Named checkpoints

Questions settled with Chris on 2026-10-24:

| # | Question | Decision |
|---|---|---|
| Q1 | `restore` of a folder or vault checkpoint: notes made since | They go to the trash. Afterwards the folder matches the checkpoint exactly; the preview lists all three groups (changed, back, to trash). |
| Q2 | Where names are unique | Per vault. `--checkpoint name` is unambiguous wherever in the vault it is used. |
| Q3 | Do `mv` link rewrites join the changeset? | Yes, like every tool (§2). |

| Topic | Decision |
|---|---|
| Commands | `mkcp <path> <name>` (note, folder or vault), `lscp [path]`, `rmcp <path> <name>`, all in `sh`. Names: letters, digits, `.`, `-`, `_`, starting with a letter or digit, up to 64. |
| Limit | 10 per vault by default: `Kumbu:Vaults:CheckpointLimit`, `KUMBU_CHECKPOINT_LIMIT` in `.env`. `mkcp` beyond it names `lscp` and `rmcp`. `lscp <vault>` ends with `N of 10 checkpoints in Vault`. |
| Agents | The manual and `help mkcp` say: set or remove checkpoints only when the user asks. |
| Reading | `--checkpoint <name>` on `ls`, `cat`, `outline`, `rg`, `links`, `backlinks`, `head`, `tail`, `wc`, `sort`. The command runs unchanged on a read-only view of the checkpoint's notes (`CheckpointView`).<br>The checkpoint is found in the vault of the first path argument. No version block in the output. Refused: with `--version`, right of a pipe, and on other commands. A path outside the checkpoint gets `is not in checkpoint 'x' (it covers …)`. |
| `diff --checkpoint` | A note: the checkpoint's state against the current one. A missing side is shown as `(not in checkpoint 'x')` or `(removed since)`.<br>A folder: one line per note that is `new`, `changed` (`+a -r`) or `removed` since. |
| `restore --checkpoint` | A note: restored at once, as a write with its own version; a removed note comes back.<br>A folder or vault: a preview with a plan token, then `--plan <token>`. The plan covers every note in scope at its version; any change or new note before the apply refuses it.<br>The apply is one transaction (replaces, creates, notes to the trash). A path inside a vault checkpoint restores just that folder. |
| Storage | `checkpoints`: one sealed document per checkpoint (name, path, author, time, each note's path, version and text id). A keyed hash of the name in `pid` with a unique index enforces names per vault.<br>`checkpoint_texts`: each content once per vault, sealed, under an HMAC of the content (unique index). Equal texts across checkpoints are stored once. `rmcp` removes the texts no other checkpoint holds, in the same transaction.<br>The manifests are in the working set; texts are read when a command needs them. |
| Integrity | A text's id is an HMAC of its content, checked on opening. A checkpoint with a text that is missing or doesn't open refuses as a whole (`checkpoint 'x' doesn't open …`). A silently missing note would make a folder restore move the current note to the trash. |

## 4. The phase 6 fixes

| Obs. | Fix |
|---|---|
| 1 | The `replace` preview counts occurrences inside `[[links]]`. It resolves each changed link as it would be after the replace and lists those that would lead nowhere or be ambiguous, with where they lead now. The hint: `To rename a note, mv it first: that rewrites its links, and this replace then leaves them alone.` The replace itself stays literal. |
| 2 | Changesets (§2): `replace` loses its "always its own version" exception. |
| 4 | `Plans` remembers what became of each plan for its hour. Examples: `plan X was already applied at 11:03 UTC (replaced 5 occurrences in 2 notes); a plan is applied once`, or `plan X failed at 11:03 UTC: … changed since the preview; preview again`. Shared by `replace` and folder restores. |
| 5 | `write` that creates a note names the links it made ambiguous: `note: [[q7/beta]] in /Demo/outside.md now match two notes (…)`, at most 5 plus a count. |
| 6 | `history <folder>` lists moves from the move records: one row per note move, one row per folder move (`/Demo/p6/ → /Demo/q7/  3 notes`, grouped by writer and time). Write rows also show their changeset. |
| 7 | `diff --version` says `diff: takes versions as arguments: diff <path> <version> [<version>]`. |
| 8 | Excerpts beyond 5 notes end with the `rg -n -F` command that shows every line (with `-w` for `word`). |
| 9 | `help mv` describes the link form as decided in phase 4b: the author's form, the most specific one only where that would be ambiguous. |

## 5. Testing

- **Core:**
  - changesets: across notes, a 10-minute gap, the 1-hour cap, restore outside any changeset, a series of replaces keeping the state before
  - plan outcomes
  - link warnings
  - the `rg` hint
  - move rows in `history`
  - the ambiguity hint on create
  - `mkcp`/`lscp`/`rmcp`, with limit and name errors
  - `--checkpoint` on reading commands and its refusals
  - a checkpoint outliving the history
  - `diff` and `restore` of a note and of a folder (preview, apply, plan used, a change between preview and apply, a folder within a vault checkpoint)
- **Server:**
  - checkpoints survive a restart and store equal texts once
  - `rmcp` removes only unshared texts
  - a tampered text refuses the checkpoint
  - a batch with creates and trashes is all or nothing
  - changesets survive a restart
  - nothing readable in any collection, checkpoints included
- **MCP:** a checkpoint, `replace`, `diff` and `restore` against the checkpoint, end to end.
- Then the rerun of the blind test with the literal prompt („Ersetze überall Hecate durch Janus.“), as the phase 6 notes suggest.
