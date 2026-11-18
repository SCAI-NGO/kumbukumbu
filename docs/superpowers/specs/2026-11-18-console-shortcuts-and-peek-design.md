# `ls -l`, console shortcuts and peek

Status: design, agreed with the maintainer on 2026-11-18. Target: 0.6.0. A small change; built in parallel with BYOK embeddings and Publish 2.0, as it touches `Ls.cs`, the manual line for `ls` and `web/src/console/` only.

## Why

The maintainer's terminal habits: `l`, `la`, `lsa` for listings and `p` ("peek", his `glow`) to read a note rendered. In the console they make it feel like home. `ls -l` is useful to agents too: what changed lately and what is large, in one call, where today it takes `ls`, `du` and `history`.

## Goals

- `ls -l` and `ls -a` in `sh`, for agents and the console alike.
- Console-only shortcuts `l`, `la`, `lsa` and the console command `p <note>`.

## Non-goals

- No shortcuts or `p` in `sh`: agents don't need them.
- No writes from the console, also not from a peeked note's task boxes.
- No Mermaid rendering in peek.

## Approaches

- **`ls -l` in `sh`, the shortcuts rewritten in the console (chosen).** One implementation serves agents and the console; the console needs no endpoint of its own.
- **A console-only endpoint for the long listing.** Rejected once the maintainer wanted `ls -l` for agents too: two implementations of one listing.
- **Peek rendered on the server.** Rejected: the app's note renderer is already in the client and sanitises what it renders.
- **Peek in a terminal style (monospace, bold headings).** Rejected by the maintainer in favour of the app's renderer (A).

## `ls` (server, `src/Kumbu.Core/Sh/Commands/Ls.cs`)

- `-a` is short for `--archive`.
- `-l` adds the size and the last change to each entry: a note its UTF-8 size and the time of its current version; a folder no size and the newest change below it; an attachment its summary as today (kind, pixel size, file size) and the time of its current version. Time in UTC, in the format `history` uses. Columns aligned.
- Combined flags work: `-la`, `-al`, `-l -a`, with `--archive`.
- `ls -l` on a note's path lists that note's line; at the root (the vaults) `-l` adds nothing.
- Cost: about `du` on the same folder. The reader gets a method that yields size and time of the notes at and below a path (`INoteReader.ListNoteStampsAsync`); the encrypted store answers it from its working set, as it does the listing, so `-l` reads no more than `ls` does. Measured with 16,000 notes of about 2 KB in 200 folders, 5-run mean, Debug build, on `InMemoryNoteStore`, the store the Core tests use (there is no MongoDB in the build session, and the server tests need Docker, so the encrypted store itself was not timed; it does the same work over its in-memory working set): `ls /Big` 4 ms, `ls -l /Big` 13 ms, `du /Big` 10 ms. Far under a second, so folders keep their date (the newest change below them, archive excluded unless `-a`). A checkpoint keeps no times of its notes, so on `--checkpoint` they show the checkpoint's time, as its attachments do. Columns: name, size or attachment summary, time; a folder's size column is blank, so the times line up.
- Usage `ls [-l] [-a|--archive] [path]`; `help ls`, the manual line (`Manual.cs:10`) and the tests that pin the usage follow. Why the manual: every agent reads it on connecting.

## Console (`web/src/console/`)

- **Shortcuts:** a line whose first word is `l`, `la` or `lsa` is rewritten to `ls -l`, `ls -a`, `ls -la` before it is sent, the rest of the line (paths, pipes) kept; the `→` line shows what was sent, as for relative paths. Only as the first word of the line's first stage.
- **Peek:** `p <note>` (relative paths as everywhere in the console; a section `plan.md#Goals` or a block `plan.md#^goal` too) reads the note through the notes API, with the note view's own query, and renders it with `MarkdownView` in the output, under its path, shown as a line in the console's path style. Not through `cat`: the shell decorates what it prints (a notice of another writer above, a block of embedded notes below, an outline instead of a note too long for one call), which the console would have to parse away; the notes API gives the note whole, however long. A section or a block is cut from the whole note with its lines kept, so its links, which go by the note's lines, still match.
  - links work: a link opens the note and closes the console window (as links from any window do: replace);
  - a link to a note that doesn't exist is marked missing, without the note view's button to create it: the console never writes;
  - task boxes show, disabled;
  - Mermaid shows as code;
  - images as in notes;
  - in Green and Amber everything is phosphor, headings brighter; in Plain the scheme's look.
  - `p` without an argument, or on a folder, a missing note or an attachment: an error in words, as the console's other errors.
  - a peeked note of a discreet vault that hides again collapses to a neutral line, "(a note that's hidden again)": nothing of it, its path or its vault stays on screen, and it is read no more (the console's reset on hiding follows, but a render may come before it).
- **Help:** the console's own help block names `l`, `la`, `lsa` and `p`; `help p` explains peek.

## Tests

- xUnit: `ls -l` (note, folder, attachment, archive, root, a note's path), `-a`, combined flags, the usage and manual line.
- Vitest: the rewrite (with path and pipe, not inside a later stage), peek (rendered headings and links, a link closes the window, disabled task boxes, errors), help.

## Left to the builder

- The column layout of `-l`.
- Where in the console the rewrite lives (next to the local commands in `run.ts`).
