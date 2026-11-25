# 0034. Offline queries match regular expressions in linear time

- **Date:** 2026-11-25
- **Status:** Accepted
- **Decided by:** Claude (the plan for the offline query engine), with the maintainer's go-ahead for 0.8.0
- **Spec:** [Queries: the data side](../superpowers/specs/2026-11-24-queries-data-side-design.md)

## Context

The query language has `matches` with a regular expression. A pattern is anyone's input: a query note in a shared vault is written by others and run by a reader. On the server, the C# engine compiles patterns with .NET's `RegexOptions.NonBacktracking`, so a pattern such as `(a+)+$` cannot run for ever. The offline engine in `packages/eidolon-core` runs in a browser tab, where a hung pattern freezes the page the reader is looking at, and the browser's `RegExp` backtracks.

## Decision

The offline engine matches with RE2, through `re2js` (MIT, pure JavaScript), as the server does with .NET's non-backtracking engine. It never builds a `RegExp` from a query's pattern. A pattern RE2 rejects is the `regex` error, as on the server.

## Why

Matching time is linear in the text, whatever the pattern, on both sides. The alternatives lost: `RegExp` with a time limit (it cannot be interrupted in a tab without a worker per query), a worker that is killed after a timeout (a new moving part for the one case, and a query that is merely slow would fail), or limiting the patterns by a hand-written check (it would reject safe patterns and miss unsafe ones).

## Consequences

- Neither side has lookaround or backreferences: both engines accept the same subset, so a pattern that runs on the server runs offline, and one that doesn't, doesn't.
- RE2's `\d`, `\w`, `\s` and `\b` are ASCII-only, where .NET's are Unicode; a pattern using them can match differently on the two sides. Use explicit classes such as `[0-9]` or `\p{L}` where it matters.
- One more runtime dependency in `packages/eidolon-core`, checked by the boundary test (permissive licence, no Node, no DOM).
- The conformance cases (`tests/query-conformance`) hold the two engines to the same results, patterns included.
