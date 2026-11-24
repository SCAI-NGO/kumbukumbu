# 0026. A query is a note, its arguments bound into `this`

- **Date:** 2026-11-23
- **Status:** Accepted
- **Decided by:** Chris, with Claude
- **Spec:** [Queries: the data side](../superpowers/specs/2026-11-24-queries-data-side-design.md)

## Context

Obsidian keeps a base in a `.base` file or a ` ```base ` block. Kumbu² wants named queries that people and agents call with parameters, documented well enough to be used without reading their code, and that still render in Obsidian.

## Decision

A query is a Markdown note: frontmatter with `type: query` and a `params:` schema (type, required, default, options, description per parameter), prose, and one or more ` ```base ` blocks that stay pure Bases. A `.base` file is a query too, without parameters. A call's arguments are bound into `this`, over the query note's own properties; agents call queries through one `query` command in `sh`, not a tool per query.

## Why

- A query is knowledge: versioned, linkable, in the graph, with prose that is its documentation (`query inspect` shows it).
- `this` is what Obsidian gives a base: the note it is shown in. A query reading `this.month` renders in Obsidian through the note that embeds it. A `param.*` namespace would not.
- Kumbu²'s additions in the frontmatter keep the base block valid for Obsidian, where an unknown function fails the whole base.
- One command keeps the tool list short and routing clear; the agent discovers queries as it discovers notes.

Lost: `.base` as the primary format (no prose, no parameters); a `param.*` namespace (doesn't render in Obsidian); a tool per query (bloats the tool list).

## Consequences

- The `params:` schema is a data format Kumbu² keeps compatible from now on.
- Arguments are text read as the declared type; they are values only, never expressions, since an agent may have read them on a web page.
- A query note shown in another note (the UI, later) binds that note into `this`, as Obsidian does.
