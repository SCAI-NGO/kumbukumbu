# Queries: the data side

Date: 2026-11-24. Status: built in steps for 0.8.0 on `release/0.8.0`, with the maintainer's go-ahead to finish the section (2026-11-24). Target: 0.8.0. The web UI comes later, from a UX session of its own; this covers what a query is, what it gives, and how agents reach it.

## 1. Why

Notes carry typed properties (a person's `birthday`, a task's `status`), and Obsidian's Bases turn them into tables. Kumbu² reads the same files and should give the same answers, and go further: a query is a note, with prose that explains it and parameters a caller fills in, so people and agents use the same named queries. Bridge both: compatible with Obsidian where it has a say, more where it hasn't.

## 2. Decisions

1. **Queries run in memory over the unlocked vault, never over a database index** ([ADR 0025](../../adr/0025-queries-run-in-memory-over-the-unlocked-vault.md)). *Why:* everything MongoDB holds is sealed; an index over frontmatter would keep content in the clear. In memory there is also no query compiler for MongoDB to get wrong (no `$where`, no operator injection), and no index to go stale.
2. **The first build reads the notes the way the calendar and tags do** (`ReadNotesUnderAsync` over the working set), and parses each note's frontmatter per query. Properties go into the sealed extract (beside links, tags and dates) only once a measurement shows the parsing matters. *Why:* the calendar parses the same frontmatter per call over the same working set and is fast enough for vaults of thousands of notes; the extract would add a stored format and its migration for a cost not yet seen. *Changed from* the plan of 2026-11-23 (properties in the extract from the start): same direction, one step later.
3. **A query is a Markdown note** (`type: query`, a `params:` schema, prose, one or more ` ```base ` blocks); a `.base` file is a query too, without parameters ([ADR 0026](../../adr/0026-a-query-is-a-note-its-arguments-bound-into-this.md)). *Why:* queries are knowledge, versioned, linkable and in the graph; Obsidian renders the embedded base natively.
4. **A call's arguments are bound into `this`**, over the query note's own properties. *Why:* in Obsidian, `this` is the note a base is shown in; a query that reads `this.month` renders in Obsidian through the note it is embedded in, with that note's `month`. A separate `param.*` namespace would not render there.
5. **The ` ```base ` block stays pure Bases.** Kumbu²'s additions live in the frontmatter (`params:`). *Why:* an unknown function makes the whole base fail in Obsidian.
6. **One rule for failures.** What no note can fix (an unknown function or method, a formula that doesn't exist or uses itself, a regular expression that doesn't compile, a base that isn't one, a schema that isn't one, a missing or wrong argument) is an error with a code, before any note is read; a value of the wrong type in a note gives null. *Why:* messy notes are normal; a broken query is the author's to fix, and the code tells an agent what to fix.
7. **Two engines, one set of cases.** The C# engine (`src/Kumbu.Core/Queries`) and, later in 0.8.0, the TypeScript one of the offline mode (`packages/eidolon-core`) are held to the same language-neutral cases in `tests/query-conformance/` (its README has the format). *Why:* two implementations of one semantics drift unless one set of cases holds them both.

## 3. Values and types

A property's type comes from the vault's `.obsidian/types.json` where it declares one (`birthday: date`), else from how the value is written (a plain `11` is a number, a quoted `"11"` text). `tags`, `aliases` and `cssclasses` are lists of text in every vault. A value that doesn't fit its declared type stays the text it is. Types: text, multitext (a list of text), number, checkbox, date, datetime (without a zone, as Obsidian keeps it). Cases: `properties/`.

## 4. The language

The Bases expression language: JavaScript precedence; `file.*` (name, basename, path, folder, ext, and `hasTag`, `inFolder`, `hasProperty`), `note.*` and bare property names, `formula.*`, `this.*`; dates with fields (`birthday.month`, `birthday.day`); text, list and number methods; `if`, `now`, `today`, `date`, `number`, `list`; regular expressions `/…/gimsu`, matched in linear time. Cases: `expressions/`.

Limits, since an expression is anyone's input: 10,000 characters, 64 levels of nesting (parser and formulas), no backtracking in regular expressions; a base or frontmatter of at most 10,000 YAML values and 64 levels, so anchors can't expand into millions.

## 5. Bases

Global `filters` and a view's own, groups `and`, `or`, `not` (none of its items); `formulas`; views with `order` (the columns), `sort` (stable, empty values last in both directions, ties by path), `limit`. A view without a name is called "View N". No views: a table of `file.name`. Cases: `bases/`.

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
| `query execute <query> [--view V] [--param name=value]...` | Runs it: the view, how many notes, and a table whose first column is each note's path. |
| `query check <query>` | Compiles every view without running it: each error with its code, its expression and the position in it, so an agent corrects itself instead of guessing. |
| `query properties [path]` | The properties below a path: name, type (declared, or as the values are written), how many notes have it. Without knowing there is a date `birthday` in `People/`, nobody writes a good query. |
| `help query` | The language: functions, methods and fields, from the evaluator's own lists, so the help never drifts from what runs. |

`<query>` is a path, or a name: the file name of exactly one marked query in the caller's vaults (ignoring case); two with the name is an error listing both. A query runs over the notes of its own vault, archived notes left out, with its vault's `types.json`. Arguments are untrusted input (an agent may have read them on a web page): they are only ever values, never expressions.

A system skill **`create-query`** (as `create-skill`) teaches the format, the `params:` schema, the rule that the base block stays pure Bases, a few examples, and the loop write → `query check` → `query execute`. *Why:* Bases is from 2025, models know it in patches, and Kumbu²'s additions not at all.

## 8. Security

- Every query reads only what the caller's shell reads: the same note reader, scoped to the caller's vaults.
- The limits of §4 bound parsing and evaluation; a run is bounded by the vault's size, as a search is.
- No expression reaches a database; arguments are values only.

## 9. Later

The TypeScript evaluator for the offline mode (after the C# one has fixed the semantics); the web UI (a form from `params:`, results as table and cards; UX session first); properties in the sealed extract if measured; embeddable text properties and semantic queries; views beyond the table; summaries.
