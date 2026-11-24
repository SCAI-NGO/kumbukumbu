# 0025. Publishing a note keeps the version it copied

- **Date:** 2026-11-24
- **Status:** Accepted
- **Decided by:** Chris, 2026-11-24
- **Spec:** `docs/superpowers/specs/2026-11-23-published-overview-design.md` (F5)

## Context

A copy records, per note, the version it was made from, and Compare reads that version from the note's history ([0018](0018-compare-a-copy-by-its-note-version.md)). Writes in one changeset coalesce: the state a write replaces goes into the history only if it belongs to another changeset. A note published in the middle of an edit session therefore lost its copied version at the next save, and Compare said "This version is no longer kept" long before the history pruned anything.

## Decision

Publishing and Update copy mark the current state of every note they copy (the start and, for a bundle, each member). The next write of content keeps a marked state in the history even within its changeset, as a restore already keeps the state it brings back (`keepCurrent`), and clears the mark; a move keeps it. The mark is set in the publication's own transaction, under the vault's gate, so no save can come between the copy and the mark.

## Why

The copy points at a version that must exist, and this is the smallest rule that guarantees it. Weighed against it:

- Writing the copied state into the history at publish time, as a restore does: the history would list the current version twice until the next write.
- Ending the writer's changeset on the note (its changeset set to none): the state would lose the changeset it belongs to in Activity, and it closes more than the one version.
- The store looking up the publishing record on every write to keep a copied version: every write would read publishing, and the note's own rule would live outside `NoteWriter`.

## Consequences

- "No longer kept" means only that the history pruned the version (ten versions, `NoteWriter.HistoryLimit`).
- The mark bumps the note's revision without changing its content; a save that read the note before the publication and commits after it is refused as a conflict, as for any write that came between.
- A note published while being edited has one more version in its history than it would have had.
- Attachments and skill folders copied by a publication keep no such mark: Compare reads only notes.
