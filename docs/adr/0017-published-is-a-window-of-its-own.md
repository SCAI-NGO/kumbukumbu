# 0017. Published is a window of its own, with the bar's actions

- **Date:** 2026-11-23
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** `docs/superpowers/specs/2026-11-23-published-overview-design.md`

## Context

The owner wants one place for everything they published: open it, update it, withdraw it, and see where the copy is older than the note. An overview existed as a read-only section at the bottom of Settings → Vaults; every action needed the note opened first.

## Decision

Published is a window of its own (`/published`), as Recent and What's on are, opened from the drawer's account row while the server publishes and from the command palette ("Open / Published"). Each row offers the note's bar's own actions, Update copy (only where the copy is older) and Unpublish, through the same hooks and dialogs. The overview's answer carries each publication's walk (`notes`, `depth`, `without`) and still never its link.

## Why

- Publishing is a first-class use, not a setting: in practice people write notes in order to publish them (Chris, 2026-11-23, from use at work and his own). A place among the things one opens, as Recent, says so; a page in Settings, beside Access (the other things given out), was weighed and lost on exactly that.
- The same actions and dialogs as the bar: one way to update or withdraw, wherever it starts (unified UX).
- No Copy link on the list: the overview would put every share code on one screen. Copy link stays on the note's bar, a click away.

## Consequences

- The overview endpoint answers a little more (the walk), still nothing a reader of the copy could use.
- The old section in Settings → Vaults is gone; the README points to the window.
