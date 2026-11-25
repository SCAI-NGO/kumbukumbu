# 0032. `graph --orphans --without-tag` counts only the links of the notes kept

- **Date:** 2026-11-25
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [Knowledge synthesis](../superpowers/specs/2026-11-22-knowledge-synthesis-design.md) §6

## Context

[0028](0028-hubs-without-tag-count-only-kept-links.md) made `--hubs --without-tag` count only the links whose other end the filter keeps, and left `--orphans` asking the whole graph whether a note has any link. Under `--without-tag synthesis/hypothesis`, a note linked only by an earlier hypothesis was therefore not listed as an orphan, though the filter is meant to make the vault look as if those notes weren't there.

## Decision

With `--without-tag`, `--orphans` lists a kept note when none of its links in or out has a note at the other end that the filter keeps. The other filters (paths, `--tag`, the kinds of node) keep their meaning as before, as for `--hubs`.

## Why

One rule for every mode of `graph` under `--without-tag` is easier to reason about than one per mode. A note linked only by a hypothesis is exactly the unlinked note a synthesis run looks for; hiding it behind the agent's own earlier output is the read-back the filter exists to prevent. Counting only links between kept notes for every filter was left for the reason 0028 gives: a note below a folder would turn orphan for its links from the rest of the vault.

## Consequences

`--orphans`, `--hubs` and `--unresolved` agree under `--without-tag`. Without the filter nothing changes.
