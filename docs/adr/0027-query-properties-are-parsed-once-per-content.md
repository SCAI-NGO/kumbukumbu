# 0027. Queries run in memory over the unlocked vault, each note's properties parsed once per content

- **Date:** 2026-11-24
- **Status:** Accepted
- **Decided by:** Chris (measure, then upgrade if it pays, 2026-11-24); Claude (the cache over the extract, from the figures)
- **Spec:** [Queries: the data side](../superpowers/specs/2026-11-24-queries-data-side-design.md)
- **Supersedes:** [0025](0025-queries-run-in-memory-over-the-unlocked-vault.md)

## Context

[0025](0025-queries-run-in-memory-over-the-unlocked-vault.md) put queries in memory over the unlocked vault's working set, parsing each note's frontmatter per query, and left properties for the sealed extract until a measurement showed the parsing mattered. The threshold agreed on 2026-11-24: an extract (or a cache) once `query execute` over 5,000 notes takes more than 200 ms at p95 and parsing is more than half of it.

Measured on synthetic vaults (5 to 15 properties and 2 to 5 KB of prose a note; Release build, 4 cores; `QueryCostMeasurement` in `tests/Kumbu.Core.Tests/Queries`), p95 in milliseconds:

| Notes | Read | Parse | Evaluate | `query execute` before | after |
|---:|---:|---:|---:|---:|---:|
| 1,000 | 0.3 | 131 | 1 to 4 | 94 | 5 |
| 5,000 | 0.5 | 184 | 4 to 11 | 277 to 300 | 14 to 20 |
| 20,000 | 2 | 863 | 17 to 400 | 882 to 1,042 | 316 to 639 (p50 27 to 81) |

Parsing was over 90 % of a query; reading is a listing of the working set, which the server holds decrypted, so neither MongoDB nor the seal is on a query's path.

## Decision

Queries are evaluated in memory, over the notes of the unlocked vault's working set, by an evaluator in `Kumbu.Core`, with no property index in the database (as in 0025). A note's parsed properties are kept in memory beside its content, keyed by the content string itself and the vault's declared types, and dropped with the content: each note is parsed once while it stays unchanged, not once per query. Properties do not go into the sealed extract.

## Why

- The threshold is crossed (277 ms at p95 for 5,000 notes, nine tenths of it parsing); with the cache the same query takes 14 ms.
- Parsing is what costs, and it is the same on every query; reading costs nothing to save. A cache removes exactly the repeated work.
- Keyed by the content string (a weak table), an entry lives exactly as long as the working set holds that content: no plaintext outlives the vault's lock, no invalidation on writes (a changed note is a new string), nothing to keep current.
- The sealed extract would save only the first parse after an unlock (under a second for 20,000 notes), at the cost of a stored format, its migration and a write-time parse. Not worth it now.

Lost: properties in the sealed extract (a stored format for one parse per unlock); parsing per query (nine tenths of a query's time).

## Consequences

- The first query after an unlock, or over many freshly written notes, still parses: up to about 0.8 s for 20,000 notes.
- Memory grows by the parsed properties of the notes queried, released with their content.
- Evaluation is now the cost: a view that matches and sorts every note of 20,000 takes tens of milliseconds, with GC pauses at p95. Very large vaults may still need more (a later record, if so).
- The offline mode's evaluator (TypeScript) is held to the same conformance cases; whether it caches is its own call.
- A query reads through the caller's note reader, so it sees exactly what the caller may read.
