# 0022. One module wires the offline mode together

- **Date:** 2026-11-23
- **Status:** Proposed
- **Decided by:** Claude, for Chris's review
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.12

## Context

The offline mode was built in parallel packages, each behind seams for the others: reading from the copy (`setOfflineSource`), writing into it (`setOfflineWrites`, `onOfflineWrite`, `draftsFor`, `flushOfflineEditors`), the engines (`listenToEngines`, `localChanged`) and the mode (`onContacted`, `onBeforeLock`). Each package left the wiring to whichever landed second; once all four were combined, nothing connected them, and two of them had their own `openCopy`.

## Decision

`web/src/offline/host.ts` is the one place that knows every part and connects them, started once in `main.tsx`. The mode's `openCopy` (with the idle lock and the wipe) is the only one.

## Why

- The parts stay as they were reviewed; the seams are their public faces, and the wiring reads in one screen.
- Wiring spread over the parts would make each depend on all the others, which their packages avoided on purpose.
- Rejected: putting the wiring into the mode (`mode.ts`). The mode would then import the source, the writes and the engines, and its tests would need all of them.

## Consequences

- A new part of the offline mode adds its seam and one line here.
- The end-to-end suite (`web/e2e/offline/`) is what proves the wiring; the parts' unit tests don't see it.
