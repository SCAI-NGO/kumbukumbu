# 0012. No moving or renaming while offline in 0.7.0

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §2 "Renames offline"

## Context

The web app moves notes with the server rewriting links; the sync engine moves with links kept, because Obsidian rewrites them itself. A browser offline can do neither honestly without a link rewriter of its own.

## Decision

Offline, notes can be read, edited, created and trashed. Move and rename say "needs a connection". Offline move and rename are on the 0.8.0 backlog.

## Why

- It is the riskiest part of offline writing, and should not be built under release pressure.
- Create, edit and trash cover what people do away from a connection.

## Consequences

- A folder kept offline and moved online stays listed under its old path until kept again.
