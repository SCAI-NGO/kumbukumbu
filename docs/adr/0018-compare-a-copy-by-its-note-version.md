# 0018. Compare a published copy by the note version it was made from

- **Date:** 2026-11-23
- **Status:** Proposed
- **Decided by:** Chris (asked for a side-by-side view); the way of comparing is the UX review's recommendation (F5), awaiting his call
- **Spec:** `docs/superpowers/specs/2026-11-23-published-overview-design.md`

## Context

The owner wants to see how a published copy and the note differ, side by side. The copy is a filtered fair copy, sealed under its share code; the publishing record already keeps, per note of the copy, the note version it was made from (to tell that the copy is older).

## Decision

Compare shows the note's own text at the version the copy was made from beside the note now, in the comparison view the activity and search use (`CompareVersions`, without its mode switch). The owner's publication reads (the note's and the overview) carry `copied`, the path and version of each note of the copy, the start first. A version the history no longer keeps says so in its column. For a bundle, a picker chooses its note.

## Why

- No new decryption path: the copy stays readable only through its code, and the owner's history read, with all its checks, does the work.
- It reuses a view people know, with its diff and its phone layout.
- It shows what the owner changed, which is the question behind "is the copy older?". Weighed against it: the stored copy against a fresh fair copy of the note. That shows exactly what Update copy would change, filter included, but needs the copy opened for the owner and the filter run on every view.

## Consequences

- What the copy's filter takes out (comments, tags) appears on the left as it is in the note, not as the reader sees it; the sheet says that the left side is the note the copy was made from.
- Once the history prunes that version, Compare can only say so.
