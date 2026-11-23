# 0011. Fetch large attachments on demand

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §6, §4.8

## Context

A vault's attachments can weigh far more than its notes, and browser storage is limited and may be cleared.

## Decision

The copy always holds every path with its kind, size and version. Note texts and images and PDFs up to 1 MB come with the first download. Larger attachments are fetched when opened online and kept within a budget (500 MB by default, least recently opened go first). **Keep offline**, set on a folder or vault, fetches everything below it up to the engine's limit.

## Why

- The tree is complete at once, and an interrupted download is a normal state, never an error.
- The person decides what heavy files are worth the space, where the files are.

## Consequences

- An attachment not fetched says "Opens when you're online" offline.
- Files changed on the device and not uploaded, small files and kept folders are never evicted by the budget.
