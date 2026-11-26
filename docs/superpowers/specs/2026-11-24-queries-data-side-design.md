# Queries: the data side

Date: 2026-11-24. Status: built in steps for 0.8.0 on `release/0.8.0`, with the maintainer's go-ahead to finish the section (2026-11-24). Target: 0.8.0. The web UI comes later, from a UX session of its own; this covers what a query is, what it gives, and how agents reach it.

## 1. Why

Notes carry typed properties (a person's `birthday`, a task's `status`), and Obsidian's Bases turn them into tables. Kumbu² reads the same files and should give the same answers, and go further: a query is a note, with prose that explains it and parameters a caller fills in, so people and agents use the same named queries. Bridge both: compatible with Obsidian where it has a say, more where it hasn't.

## 2. Decisions

1. **Queries run in memory over the unlocked vault, never over a database index** ([ADR 0025](../../adr/0025-queries-run-in-memory-over-the-unlocked-vault.md), superseded by [0027](../../adr/0027-query-properties-are-parsed-once-per-content.md)). *Why:* everything MongoDB holds is sealed; an index over frontmatter would keep content in the clear. In memory there is also no query compiler for MongoDB to get wrong (no `$where`, no operator injection), and no index to go stale.
2. **Queries read the notes the way the calendar and tags do** (`ReadNotesUnderAsync` over the working set), and keep each note's parsed properties in memory beside its content, so a note is parsed once while it stays unchanged ([ADR 0027](../../adr/0027-query-properties-are-parsed-once-per-content.md)). *Why:* measured on 2026-11-24, parsing was nine tenths of a query (277 ms at p95 for 5,000 notes; 14 ms with the cache), and reading costs nothing to save; keyed by the content string, the cache lives exactly as long as the working set holds the content. Properties stay out of the sealed extract, which would save only the first parse after an unlock. *Changed from* the plan of 2026-11-23 (properties in the extract from the start) and from parsing per query until measured (ADR 0025).
3. **A query is a Markdown note** (`type: query`, a `params:` schema, prose, one or more ` ```base ` blocks); a `.base` file is a query too, without parameters ([ADR 0026](../../adr/0026-a-query-is-a-note-its-arguments-bound-into-this.md)). *Why:* queries are knowledge, versioned, linkable and in the graph; Obsidian renders the embedded base natively.
4. **A call's arguments are bound into `this`**, over the query note's own properties. *Why:* in Obsidian, `this` is the note a base is shown in; a query that reads `this.month` renders in Obsidian through the note it is embedded in, with that note's `month`. A separate `param.*` namespace would not render there.
5. **The ` ```base ` block stays pure Bases.** Kumbu²'s additions live in the frontmatter (`params:`). *Why:* an unknown function makes the whole base fail in Obsidian.
6. **One rule for failures.** What no note can fix (an unknown function or method, a formula that doesn't exist or uses itself, a regular expression that doesn't compile, a base that isn't one, a schema that isn't one, a missing or wrong argument) is an error with a code, before any note is read; a value of the wrong type in a note gives null. *Why:* messy notes are normal; a broken query is the author's to fix, and the code tells an agent what to fix. *Superseded* by [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) (2026-11-25), decisions 9, 21 and 22: an error in a cell is a value, as in Obsidian, and Kumbu² reports it beside the rows.
7. **Two engines, one set of cases.** The C# engine (`src/Kumbu.Core/Queries`) and, later in 0.8.0, the TypeScript one of the offline mode (`packages/eidolon-core`) are held to the same language-neutral cases in `tests/query-conformance/` (its README has the format). *Why:* two implementations of one semantics drift unless one set of cases holds them both.

## 3. Values and types

*Changed* by [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) §2: declared types don't change a base's values, and there is one date type. *Restored* by [Bases without Obsidian's bugs](2026-11-26-bases-without-obsidian-bugs-design.md) §2 item 4: a declared type converts a value that converts cleanly, and `query check` warns about the values that don't (one date type stays).

A property's type comes from the vault's `.obsidian/types.json` where it declares one (`birthday: date`), else from how the value is written (a plain `11` is a number, a quoted `"11"` text). `tags`, `aliases` and `cssclasses` are lists of text in every vault. A value that doesn't fit its declared type keeps its own value. Types: text, multitext (a list of text), number, checkbox, date, datetime (without a zone, as Obsidian keeps it). Cases: `properties/`.

## 4. The language

*Changed* by [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) §3–4: the functions and methods are Obsidian's, comparisons coerce as Obsidian's do; and §7: the limits below hold, with more beside them.

The Bases expression language: JavaScript precedence; `file.*` (name, basename, path, folder, ext, and `hasTag`, `inFolder`, `hasProperty`), `note.*` and bare property names, `formula.*`, `this.*`; dates with fields (`birthday.month`, `birthday.day`); text, list and number methods; `if`, `now`, `today`, `date`, `number`, `list`; regular expressions `/…/gimsu`, matched in linear time. Cases: `expressions/`.

Limits, since an expression is anyone's input: 10,000 characters, 64 levels of nesting (parser and formulas; a chain's steps, `.name`, `[index]` and a call, count as levels, so an expression's tree is never deeper whatever its length, and operator chains are walked in a loop), each formula checked once and worked out once per row (formulas that use one another twice per level would otherwise cost twice as much per level), no backtracking in regular expressions; a base or frontmatter of at most 10,000 YAML values and 64 levels, so anchors can't expand into millions. *Changed* by [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) §7 ([ADR 0040](../../adr/0040-query-work-is-bounded-and-regexes-are-a-common-subset.md)), which lists every limit with its reason: the formulas' levels added up along the way count against one budget of 512, a value and a run's work are bounded in size and steps (`too-large`), and a regular expression runs only in the common subset of RE2 and .NET, at most 64 positions written out.

## 5. Bases

Global `filters` and a view's own, groups `and`, `or`, `not` (none of its items); `formulas`; views with `order` (the columns), `sort` (stable, empty values last in both directions, ties by path), `limit`. A view without a name makes the whole base an error, and only `ASC` and `DESC` sort (*changed* by [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) §5). No views: a table of `file.name`. Cases: `bases/`.

## 6. Query notes and parameters

````markdown
---
type: query
params:
  month:
    type: number
    required: true
    description: The month, 1 to 12.
  kind:
    type: text
    options: [task, event]
    default: task
---
# Birthdays

The people whose birthday is in a month, by day.

```base
filters:
  and:
    - file.inFolder("People")
    - birthday.month == this.month
views:
  - type: table
    name: By day
    order: [file.basename, birthday]
    sort:
      - property: birthday
```
````

- A parameter has a `type` (text when left out; the Obsidian names: text, multitext, number, checkbox, date, datetime), `required` (true or false), a `default`, `options` (the values it is held to) and a `description`. A required parameter has no default.
- An argument is text, read as the parameter's type as a declared property is; a multitext parameter takes one argument per item. A parameter not given takes its default, else null; a required one not given, a value that doesn't fit, a name the query doesn't declare: an error.
- The views of a query note are those of its base blocks in order (blocks at the top level, as CommonMark reads fences; an unclosed one runs to the end); a call runs the first unless it names one.
- `this` is the query note with its own properties, the arguments over them. Embedded in a note (later, the UI), `this` will be that note, as in Obsidian.
- `type: query` marks a note for `query ls`; a note with a base block but no marker runs when called by its path.

Error codes: `no-base`, `params`, `unknown-param`, `missing-param`, `param-value`, `unknown-view`, `base`, and the language's `syntax`, `unknown-function`, `unknown-method`, `unknown-formula`, `formula-cycle`, `regex`, `too-deep`. Cases: `queries/`.

## 7. Agents: one surface in `sh`

No tool per query (fifty queries would be fifty tools); one command, the agent finds queries itself:

| Command | What it does |
|---|---|
| `query ls [path]` | The named queries below a path (every vault without one): path, title, parameters. |
| `query inspect <query>` | The query note's prose, its parameters (type, required or default, options, description) and its views. A well-written query note is a documented interface. |
| `query execute <query> [--view V] [--param name=value]...` | Runs it: the view, how many rows, and a table whose first column is each file's path (*changed* by [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) decision 21: before the table, a line for each column with errors in its cells). |
| `query check <query>` | Compiles every view without running it: each error with its code, its expression and the position in it, so an agent corrects itself instead of guessing. |
| `query properties [path]` | The properties below a path: name, type (declared, or as the values are written), how many notes have it. Without knowing there is a date `birthday` in `People/`, nobody writes a good query. |
| `help query` | The language: functions, methods and fields, from the evaluator's own lists, so the help never drifts from what runs. |

`<query>` is a path, or a name: the file name of exactly one marked query in the caller's vaults (ignoring case); two with the name is an error listing both. A query runs over the files of its own vault, archived notes and every other file included (*changed* by [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) §1); the vault's `types.json` no longer changes a value (§2 there). Arguments are untrusted input (an agent may have read them on a web page): they are only ever values, never expressions.

A system skill **`create-query`** (as `create-skill`) teaches the format, the `params:` schema, the rule that the base block stays pure Bases, a few examples, and the loop write → `query check` → `query execute`. *Why:* Bases is from 2025, models know it in patches, and Kumbu²'s additions not at all.

**The offline engine.** The same language runs in the browser's offline mode: `packages/eidolon-core/src/query/` (a TypeScript port of `Kumbu.Core/Queries`, held to the same cases in `tests/query-conformance`, runner `packages/eidolon-core/test/query.conformance.test.ts`). It reads YAML with the `yaml` package (ISC), which keeps each scalar's source text and style, so a plain `2026-11-03` and a quoted one stay what they are, as in YamlDotNet; aliases are resolved under the same budget as the server's (10,000 nodes, 64 levels). It matches regular expressions with `re2js` (MIT, RE2 in pure JavaScript), never the browser's own `RegExp`: a pattern is anyone's input, and a backtracking engine would let a query note in a shared vault hang a reader's tab; the server's counterpart is .NET's non-backtracking engine ([ADR 0034](../../adr/0034-offline-queries-match-regular-expressions-in-linear-time.md)); the two share a common core of the syntax and differ at its edges (see the ADR), so a pattern that keeps to the core behaves the same (*changed* by [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) §7: only that core runs, on both sides alike). *Why a port, not a shared core:* the server stays C# and the offline mode must run without it, so two implementations hold one semantics, and one set of cases keeps them from drifting.

## 8. Security

- Every query reads only what the caller's shell reads: the same note reader, scoped to the caller's vaults.
- The limits of §4 bound parsing and evaluation; a run is bounded by its steps and the size of its values, whatever the vault's size ([Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) §7).
- No expression reaches a database; arguments are values only.

## 9. Later

The web UI (a form from `params:`, results as table and cards; UX session first); embeddable text properties and semantic queries (specified in [Embeddable properties](2026-11-25-embeddable-properties-design.md)); views beyond the table; summaries.
