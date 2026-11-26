# 0039. Bases follow Obsidian's semantics; Kumbu² only adds diagnostics

- **Date:** 2026-11-25
- **Status:** Accepted
- **Decided by:** Chris, with Claude
- **Spec:** [Queries: the data side](../superpowers/specs/2026-11-24-queries-data-side-design.md), [Bases follow Obsidian](../superpowers/specs/2026-11-25-bases-follow-obsidian-design.md)

## Context

Kumbu²'s query engine reads Obsidian's Bases: `.base` files and ` ```base ` blocks. Its conformance cases (`tests/query-conformance/`) were written from our reading of the Bases documentation (`source: spec`). On 2026-11-25 we ran them against Obsidian 1.15.1 itself, automated: Obsidian with a local debugging port, Playwright attached over CDP, each case written into a test vault and its values read back through Obsidian's own Bases API. Obsidian differs from our reading in several places: a base runs over every file of the vault, the `.base` file included; a note's `file.name` drops `.md`; text that reads as a number compares with numbers; most errors are values in a cell, not an error of the query; some errors of a base stop all of its views. The findings are in the Kumbu² note `/SWE/Kumbu/0.8.0/Obsidian cross-checks.md`.

This also corrects [0026](0026-a-query-is-a-note-its-arguments-bound-into-this.md)'s aside that an unknown function fails the whole base in Obsidian: it gives an error in each cell of the formula.

## Decision

Where a base's result differs, Kumbu² follows Obsidian: which rows, in which order, with which values, and when a base or view is an error. Kumbu² may add diagnostics on top that don't change the result, such as a summary of the errors in a query's cells for an agent before the rows. A conformance case moves to `source: obsidian <version>` once Obsidian has been seen to give its result.

## Why

- A `.base` file is Obsidian's data format, and the same file is opened in both. If Kumbu² showed other rows than Obsidian for the same notes, one of the two would be wrong to the person looking, and they couldn't tell which: honesty is security.
- "Kumbu²'s semantics first" holds for Kumbu²'s own features (calendar, board, history). Bases are not one of them; they are a format we read.
- Diagnostics on top keep what our stricter reading was for: an agent calling `query execute` should not overlook an error hidden in a column.

Lost: keeping our stricter reading (no coercion between types, an error of the whole query for an unknown function), which is cleaner on its own, but would make the same base mean two things.

## Consequences

- The C# engine changes where the cases now say so; those cases carry `pending: [cs]` until it does, and the offline engine is held to the same cases.
- Following Obsidian needs a spec for what the cases can't say yet: the base file and attachments as rows, the notation of an error value in a cell, and which method names Obsidian has (it has no `upper()` or `matches()` on text).
- Obsidian may change between versions. The automated run is repeatable, so a new Obsidian version is checked by running it again, and a case records the version it was seen in.
