# 0036. Meaning enters a query through its frontmatter, as a score the base can read

- **Date:** 2026-11-25
- **Status:** Accepted
- **Decided by:** Claude, under Chris's go-ahead to work 0.8.0 ahead (2026-11-25)
- **Spec:** [Embeddable properties](../superpowers/specs/2026-11-25-embeddable-properties-design.md)

## Context

With `summary` vectors, a query should find notes by meaning, filtered by properties and ranked by closeness. The base block of a query note has to stay valid for Obsidian, where an unknown function fails the whole base ([ADR 0026](0026-a-query-is-a-note-its-arguments-bound-into-this.md)).

## Decision

A query note declares `meaning:` in its frontmatter beside `params:`: the property (`summary` or `description`), the text (a parameter or a quoted text), an optional `min` and an optional score name `as` (`closeness` by default). Before the base runs, the text is embedded once, every note gets its cosine with that text as a property under the score name, and notes under `min` are left out. The base filters, sorts and shows the score as any property. The offline engine fails such a query with `meaning-offline`.

## Why

- An unknown property is only empty in Obsidian, so the same query note still renders there, unranked.
- The evaluator stays synchronous and per row: one embedding per run, not a provider call inside an expression.
- A visible score lets a person see why a note came first.

Lost: a function such as `similar(summary, this.topic)` in the language (fails the base in Obsidian, waits on a provider per row); ranking without a visible score.

## Consequences

- `meaning:` is a data format Kumbu² keeps compatible, as `params:` is.
- The score name shadows a note's own property of that name for the run; `query check` warns about it.
- A query with `meaning:` depends on the vault's provider being reachable: it fails rather than run unranked.
