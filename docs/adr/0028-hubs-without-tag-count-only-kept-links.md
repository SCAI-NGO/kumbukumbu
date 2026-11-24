# 0028. `graph --hubs --without-tag` counts only the links of the notes kept

- **Date:** 2026-11-24
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [Knowledge synthesis](../superpowers/specs/2026-11-22-knowledge-synthesis-design.md) §6

## Context

`--without-tag synthesis/hypothesis` exists so that the vault doesn't read an agent's earlier findings back as evidence. On `graph --hubs` it took the tagged notes off the list, but each remaining note's count still held its links to and from them, while `--unresolved` with the same filter counted the links of kept notes only. The spec didn't say which counts were meant.

## Decision

With `--without-tag`, a hub's links in and out count only where the note at the other end isn't left out by that filter. The other filters (paths, `--tag`, the kinds of node) keep their counts as before.

## Why

A hypothesis note links the notes it connects; counted, it raises their degree, which is exactly what the filter is meant to prevent. Counting only between kept notes for every filter was weighed and left: a hub below a folder would then lose its links from the rest of the vault, a change nobody asked for in a fix.

## Consequences

`--hubs` and `--unresolved` agree under `--without-tag`. `--orphans` still asks the whole graph whether a note has any link, so a note linked only by a hypothesis isn't an orphan under the filter.
