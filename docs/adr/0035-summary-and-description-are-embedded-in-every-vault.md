# 0035. A note's `summary` and `description` are embedded in every vault

- **Date:** 2026-11-25
- **Status:** Accepted
- **Decided by:** Claude, under Chris's go-ahead to work 0.8.0 ahead (2026-11-25)
- **Spec:** [Embeddable properties](../superpowers/specs/2026-11-25-embeddable-properties-design.md)

## Context

Search by meaning embeds a note's sections and skips its frontmatter. A curated summary would find what a note is about rather than what happens to be in it, but nothing embeds a property today, and Obsidian's `types.json` has no place for a marker of our own.

## Decision

The text values of two properties, `summary` and `description`, are embedded in every vault, one vector each, as the note's title, a line break and the value. Their vectors are stored in the note's sealed embeddings document beside its chunks, re-embedded only when the value's hash changes, and sent to the vault's embedding provider under the same rule as the note's text.

## Why

- The two names already mean "what this is about": `description` is the field of skills and memory files.
- A fixed pair needs no setting, no UI and no new sealed field before anyone asks for a third name.
- One document per note keeps the seal and the lifetime (trash, move, re-key) as they are, with no new collection. A note's sealed size now grows by one vector when it has a text summary or description, much as its chunk count already shows.

Lost: a marker in `types.json` (Obsidian rewrites the file and drops keys it doesn't know); a per-vault list now (a UI and a stored setting for a need not yet seen); a separate collection (a second size that would show which notes have a summary).

## Consequences

- Frontmatter values now leave the server for vaults on an external provider; the security architecture says so.
- The stored list holds any name, so a per-vault choice later needs no migration.
- A note with a summary costs one more small embedding request when the summary changes.
