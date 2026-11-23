# 0017. Published is a page of Settings, with the bar's actions

- **Date:** 2026-11-23
- **Status:** Proposed
- **Decided by:** Chris (asked for it); the place and the actions are the recommendation of the UX review, awaiting his call
- **Spec:** `docs/superpowers/specs/2026-11-23-published-overview-design.md`

## Context

The owner wants one place for everything they published: open it, update it, withdraw it, and see where the copy is older than the note. An overview existed as a read-only section at the bottom of Settings → Vaults; every action needed the note opened first.

## Decision

Published is a page of the settings window (`/settings/published`), beside Vaults, linked from the drawer's account row while the server publishes and from the command palette. Each row offers the note's bar's own actions, Update copy (only where the copy is older) and Unpublish, through the same hooks and dialogs. The overview's answer carries each publication's walk (`notes`, `depth`, `without`) and still never its link.

## Why

- Settings → Access already lists what the owner gave out, with Revoke; published links are the same kind of thing, and the settings window brings its navigation, phone layout and palette entries with it. A window of its own (as Recent) was weighed: more prominent, but a second kind of place for things given out.
- The same actions and dialogs as the bar: one way to update or withdraw, wherever it starts (unified UX).
- No Copy link on the list: the overview would put every share code on one screen. Copy link stays on the note's bar, a click away.

## Consequences

- The overview endpoint answers a little more (the walk), still nothing a reader of the copy could use.
- Settings has nine entries; the old section in Vaults is gone, and so is its place in the README.
