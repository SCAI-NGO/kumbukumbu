# 0025. Queries run in memory over the unlocked vault, never over a database index

- **Date:** 2026-11-23
- **Status:** Superseded by [0027](0027-query-properties-are-parsed-once-per-content.md)
- **Decided by:** Chris (in memory, no index, 2026-11-23); Claude (the extract once measured, 2026-11-24, under his go-ahead for the section)
- **Spec:** [Queries: the data side](../superpowers/specs/2026-11-24-queries-data-side-design.md)

## Context

Queries filter and sort notes by their frontmatter properties. The first plan was a synchronous index of the properties in MongoDB. But everything Kumbu² keeps in MongoDB is sealed (`docs/security-architecture.md`): an index over frontmatter would store note content in the clear.

## Decision

Queries are evaluated in memory, over the notes of the unlocked vault's working set, by an evaluator in `Kumbu.Core`. There is no property index in the database. The first build parses each note's frontmatter per query, as the calendar does; the properties join the sealed extract (links, tags, dates) only once a measurement shows the parsing costs enough to matter.

## Why

- Encryption at rest holds without an exception for queries.
- No query compiler for MongoDB, so no `$where`, no operator injection through a property named `$gt`.
- No index to keep current: every write goes through the server, and the working set is what the server reads anyway. No watcher, no staleness, no Redis.
- A few thousand notes take milliseconds, as search and the calendar show. Storing properties in the extract now would add a stored format and its migration for a cost not yet seen.

Lost: the MongoDB index (breaks encryption at rest); properties in the extract from the start (cost without a measured need).

## Consequences

- A query's cost grows with the vault; very large vaults may need the extract (a later record, if so).
- The offline mode needs its own evaluator (TypeScript), held to the same conformance cases.
- A query reads through the caller's note reader, so it sees exactly what the caller may read.
