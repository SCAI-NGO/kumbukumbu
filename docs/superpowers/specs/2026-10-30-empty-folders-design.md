# Empty folders — Design

**Date:** 2026-10-30 · **Status:** agreed in chat, written for review · **Issue:** #104 · **Release:** v2.5 (0.2.5), plugin 0.1.2

## Intent

Folders exist in Kumbu² only because paths bring them along: a folder appears with its first note, upload or move and goes with its last. So nobody can make a place before putting something into it. Chris missed it on 2026-10-29 while preparing an upload (`/<vault>/Probes/`), and it matters as much for agents: a user wants to give an agent a folder to write into ("put your drafts in `/Work/Drafts/`"), and an agent that structures a vault wants to lay out its folders first.

The answer is the one git found for the same gap: a marker file. A folder holding a `.vaultkeep` exists even when nothing else is in it. Because the marker is an ordinary entry of the vault, it travels wherever the vault goes: export, import, checkpoints, Obsidian, a plain directory on disk. That matters more than elegance here: people must be able to leave Kumbu² with what they built, folders included.

## Goals

- "New folder" in the web app's tree: in the ⋯ menu of a vault and of a folder, and wherever new notes are made.
- `mkdir <path>` in `sh`, for agents (and so over MCP), with write rights on the vault.
- An empty folder survives a reload, a sign-out, a restart, an export and import, a checkpoint and its restore, and the Obsidian sync in both directions.
- `ls`, the tree and every listing show the folder, never the marker.
- An empty folder can be removed again (web app and `sh`).

## Non-goals

- Empty folders as a stored concept of their own (a folder record): the marker does the job without a new kind of entry in every place that lists, exports, versions or syncs.
- `rm -r` or deleting a folder with content: not part of this; `rm` still removes one entry at a time.
- Anything visible about the marker in the user interface: no "this folder is kept" badge.

## Anti-goals (it failed if…)

- …the user or an agent ever sees `.vaultkeep` in `ls`, the tree, `find`, `rg`, `semsearch`, search results, the activity or a note picker.
- …a folder vanishes that someone made on purpose, in Kumbu² or in Obsidian, because a sync or a move emptied it.
- …the export of a vault can't be unpacked into a folder that Obsidian (or any editor) opens as it was.
- …an existing vault changes in any way without anyone making a folder.

## The marker

- **Name:** `.vaultkeep`, directly inside the folder it keeps: `/Work/Drafts/.vaultkeep`. Zero bytes.
- **It stays** when content arrives, as `.gitkeep` does. So a folder made on purpose outlives its last note: emptied again, it is still there. *(Builder's call taken by Claude: this is what keeps "the folder I made" stable; removing the marker on the first note would make the folder vanish again with the last one.)*
- **Only the deepest folder** gets one. `mkdir /Work/Drafts/2026` writes `/Work/Drafts/2026/.vaultkeep`; `/Work/Drafts` exists because the path implies it, as today.
- **Hidden everywhere it would be noise** (anti-goals above). It is listed only where the vault's files are copied as they are: export, import, checkpoints, the sync API, history of the folder's own changes if there is one.
- **A write like any other:** it needs write rights, carries the writer (user, key, connection or device) in the history and activity ("made folder /Work/Drafts"), and goes to the trash like any entry when removed.

How the marker is stored is left to the builder. It is neither a note (`.md`) nor an attachment (`AttachmentFormat`, `src/Kumbu.Core/Paths/VaultPath.cs:32–35`), so it needs a place: a reserved name the note or attachment store accepts, or a small third kind. The constraints are the anti-goals and the round trip through export, import, checkpoint and sync. Pick whichever touches the fewest listings.

## Web app

- **New folder** opens a name field (checked as paths are, `VaultPath`; the same rules as a note's name minus the `.md`), creates the marker and selects the new folder in the tree.
- A name that already exists as a folder says so and selects it; nothing is written.
- **Remove folder** in a folder's ⋯ menu, offered only while the folder holds nothing but its marker. It removes the marker, so the folder goes.

## `sh`

- `mkdir <path>`: makes the folder (and implies its parents). A folder that exists already, kept or implied, answers that it exists and writes nothing. No options: parents are always implied, so there is no `-p`. A path that names a note or attachment is an error. `help mkdir` says what the folder is for in one line ("an empty folder stays until you remove it").
- `rm <folder>`: today "Directories can't be removed" (`src/Kumbu.Core/Sh/Commands/Rm.cs:18`). New: a folder that holds nothing but its marker is removed (the marker goes to the trash); one with content keeps the old error, now saying how many entries it holds.
- `ls` shows a kept, empty folder like any folder. `tree`, `find`, `rg` and `semsearch` never show the marker.
- Read-only keys and connections: `mkdir` answers like any write on a read-only vault.

## Export and import

- The export writes the marker as a zero-byte file `.vaultkeep` where it is, so the unpacked ZIP has the folder and opens in Obsidian as it was (Obsidian hides dotfiles itself). *(Claude's call: an empty directory entry in the ZIP alone would be lost on the way back, as the import works on files.)*
- The import takes `.vaultkeep` files back as markers; any other dotfile stays ignored as today. So do the web app's folder upload (`web/src/upload/`) and the seeding of a vault (`SeedScanner`), so that an unpacked export uploaded as a folder keeps its empty folders. *(Claude's call.)*
- The import runs in the browser over `POST /api/upload` (notes) and `PUT /api/files` (attachments); markers need a REST write of their own, the one "New folder" needs anyway.

## Checkpoints and history

- A checkpoint holds the markers like any entry, so restoring it brings the empty folders back.
- The marker has no versions worth keeping, but it goes through the same writer as any entry so the activity shows who made or removed a folder.

## Obsidian plugin (0.1.2)

- Build on the plugin's `origin/master` (attachment sync, the 0.1.2 state), not an older checkout. `eligibility` in `src/engine/paths.ts` ignores every dot segment today. New: a path whose **last** segment is `.vaultkeep` and whose folders have no dot segment syncs both ways, as a zero-byte file. Everything else with a dot stays ignored (`.obsidian`, `.trash`).
- The engine deletes folders a remote delete or move left empty (`leftFolders` in `src/engine/engine.ts`). A folder holding a `.vaultkeep` is not empty for that rule; `removeFolderIfEmpty` already asks the adapter, which sees the dotfile, so this mostly holds already and needs a test.
- Obsidian's vault index has no entries or events for dotfiles: reading, writing and deleting markers, and following folder renames and deletes, go through the adapter and the folder events.
- The plugin's 0.1.2, released with attachment sync and this together (decided by Chris, 2026-10-30), may meet a server without markers. A server older than this change ignores or refuses `.vaultkeep`; the plugin must not loop on it (one failed upload is logged, not retried forever).

- **A folder made in Obsidian** (decided by Chris, 2026-10-30): when someone creates a folder in Obsidian, the plugin writes a `.vaultkeep` into it, which then syncs up as any marker does. So a folder made on the device reaches the server and the agents, as one made in the web app reaches the device. Only for folders created while the plugin runs and outside dot folders; folders that already exist when the plugin is first set up are left alone.
- **Which creations count** *(Claude's call)*: Obsidian fires the same `create` event for a folder made with "New folder", one made implicitly for a new note's path, one the plugin makes itself for a download (`ensureFolder`) and one another program makes. The plugin writes a marker only for a folder that it did **not** make itself and that is **still empty a few seconds after** its creation. So implicit folders (they hold a note at once), a second device's first sync (the plugin makes those folders) and the "left empty" cleanup are unaffected, and a deliberate empty folder gets its marker. Folders made while the plugin is paused or offline stay local until something lands in them; no full check looks for them, since that check couldn't tell them from folders that existed before the plugin.

## Security

- The marker carries no content, but its path does, sealed as every path is. Nothing about the threat model changes: `mkdir` is a write with the rights any write needs.
- `security-architecture.md` gets one line in the data inventory (markers are entries of the vault, sealed alike).

## Tests

- Core (xUnit): `mkdir` (new, existing, implied parent, on a note path, read-only), `rm` on a kept empty folder and on one with content, the marker hidden from `ls`/`find`/`rg`/`tree`/`semsearch`, a checkpoint round trip.
- Server (xUnit): export writes the marker, import reads it back, the REST and sync APIs list it.
- Web (Vitest): New folder, the duplicate case, Remove folder only on an empty kept folder.
- Plugin (Vitest): a remote `.vaultkeep` creates the folder; a folder with a marker survives the "left empty" sweep; other dotfiles stay ignored.
