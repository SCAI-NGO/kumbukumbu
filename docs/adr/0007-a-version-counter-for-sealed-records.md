# 0007. Protect the offline copy's records against rollback with a version counter

- **Date:** 2026-11-23
- **Status:** Accepted (implementation in progress)
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.7, §4.8

## Context

Each record of the copy is sealed with AES-GCM and bound to its own name, so it can't be read, changed or moved to another path. Sealing alone doesn't stop an older record, validly sealed for the same path, from being put back in place by someone with access to the disk; the engine would then upload the old text as a change. The option weighed was to put this on the 0.8.0 backlog, since the attack needs the disk and the old state can be recovered from the note's history.

## Decision

Sealed records carry a version counter, checked when they are opened, so a record older than the copy knows is refused as damage, never read as a change. It lands in 0.7.0, before the feature freeze.

## Why

- The fix is small and local to the store, and the days after the freeze leave room to test it.
- A replayed record would reach Kumbu² as an ordinary upload, for every member of a shared vault; history can undo it, but only once someone notices.

## Consequences

- A refused record is handled as other damage: the path is listed as not synced and left as it is in Kumbu².
