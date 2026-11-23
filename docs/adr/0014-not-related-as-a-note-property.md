# 0014. Store "Not related" as a note property

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [link prediction](../superpowers/specs/2026-11-22-link-prediction-design.md) §3.6

## Context

Similar-note suggestions offer **Not related**, which hides a pair for good, for everyone, on every device and for agents. It needs a place to be kept.

## Decision

The rejected pairs are a list in the note's properties, `not-related:`, as paths in the vault without `.md` and without `[[…]]`. It is written to the note the tap was on and read from both sides; `sh similar --unlinked` leaves those pairs out too.

## Why

- It syncs, versions and shows in Obsidian like any property, and is sealed with the note.
- No new storage that re-keying, deletion and moves would have to learn about.
- Without `[[…]]`, neither the graph nor `--unlinked` reads it as a link.

## Consequences

- Renaming the other note makes the pair show again; accepted.
- The property is the list of rejections; a screen to review them can come later.
