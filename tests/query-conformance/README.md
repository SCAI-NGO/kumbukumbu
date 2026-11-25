# Query conformance cases

The cases every query implementation of Kumbu² is held to: the C# engine in `src/Kumbu.Core/Queries` (runner: `tests/Kumbu.Core.Tests/Queries/ConformanceTests.cs`) and, from 0.8.0 on, the offline engine in `packages/eidolon-core/src/query` (runner: `packages/eidolon-core/test/query.conformance.test.ts`). A case is written once, here, and never copied into a test of either side; each side has one runner that reads every file below this folder. Why: two implementations of one semantics drift unless one set of cases holds them both.

What a person sees as a result belongs here: how a note's properties are typed, which rows a query gives and in what order, which error it gives. What only one implementation has inside (its parser, its limits, its storage) keeps ordinary unit tests on its own side.

## A case

One YAML file per case, named for what it shows.

```yaml
description: One sentence on what the case shows.
source: spec          # or "obsidian 1.10": checked in real Obsidian, with that version
types:                # optional: the "types" of the vault's .obsidian/types.json
  birthday: date
notes:                # path: the note's full text
  People/Ann.md: |
    ---
    birthday: 1980-11-03
    ---
expect:
  properties:         # path: every property of the note, typed
    People/Ann.md:
      birthday: date 1980-11-03
  problems:           # optional: the notes whose frontmatter can't be read
    - People/Broken.md
pending: [ts]         # optional: implementations that don't pass it yet (cs, ts); each runner skips it visibly, never silently
```

A file in `notes` is a note when its path ends in `.md`. Any other path is another file of the vault, a `.base` file or an attachment (`photo.png`), with that text as its content and no properties or tags; its `file.size` is the text's UTF-8 length, as a note's is. A query runs over every file, as Obsidian's Bases do. `file.ctime` and `file.mtime` are null unless the case gives a file its times:

```yaml
times:                # optional: path: when the file was created and last changed
  People/Ann.md: { ctime: 2026-11-20T09:15:00, mtime: 2026-11-24T18:00:00 }
```

A case about expressions (in `expressions/`) gives a list of `cases` instead of `expect`, each an expression with its value for every note, or the error the expression is:

```yaml
description: A date has year, month (1 to 12) and day.
source: spec
now: 2026-11-23T14:30:00   # optional: the clock of now() and today(); without it, 2026-01-01T12:00:00
this: Projects/Kumbu.md    # optional: the note the query is shown in, one of the notes
formulas:                  # optional: the formulas formula.name refers to
  double: n * 2
notes:
  Ann.md: |
    ---
    birthday: 1980-11-03
    ---
cases:
  - expression: birthday.month
    values: { Ann.md: number 11 }
  - expression: nothing(1)
    error: unknown-function   # or syntax, unknown-method, unknown-formula, formula-cycle, regex, too-deep
```

A case about bases (in `bases/`) gives the base and a list of `runs`, each a view (the first when left out) with its columns and rows in order, or the error it is. Without `file`, the base is a block outside the vault and not a row; with it, the base is that file of the vault, a row where its filters let it be:

```yaml
file: case.base       # optional: the base file's own path
base: |
  filters: file.inFolder("People")
  views:
    - type: table
      name: By name
      order: [file.basename, birthday]
runs:
  - view: By name
    columns: [file.basename, birthday]
    rows:
      - [text Ann, date 1980-11-03]
  - view: Missing
    error: unknown-view        # or base, and the expressions' codes
```

A case about query notes (in `queries/`) names the query, optionally its parameters and views as read, and `runs` with the call's `args` (a list gives a multitext parameter one value per item); a run may name a `query` of its own:

```yaml
query: Queries/Birthdays.md
params:              # optional: each parameter as read
  month: { type: number, required: "true", description: "The month, 1 to 12." }
views: [By day]      # optional: the views, in order
runs:
  - args: { month: "11" }
    columns: [file.basename, birthday]
    rows:
      - [text Ann, date 1980-11-23]
  - args: {}
    error: missing-param   # or no-base, params, unknown-param, param-value, and the bases' codes
```

`source: spec` marks our own reading where Obsidian hasn't been checked; a case moves to `obsidian <version>` once someone has seen the same result in Obsidian. Where Obsidian gives another result, the case takes Obsidian's ([ADR 0039](../../docs/adr/0039-bases-follow-obsidian.md)).

## Values

A typed value is written as its type and its normalised text, so a comparison tests meaning, not serialisation:

| Written | Meaning |
|---|---|
| `text Ann`, `text` | text (`text` alone: the empty text) |
| `number 11`, `number 1.5` | a number, in its shortest form |
| `boolean true` | a checkbox |
| `date 1980-11-03` | a date |
| `datetime 2026-11-23T14:00:00` | a date with a time, without a zone, as Obsidian keeps it |
| `null` | no value |
| `[text a, text b]` (a YAML list) | a list |
| a YAML map of values | an object |
| `file People/Ann.md` | a note's file, as `file` and `this.file` give it |

A runner fails on an empty folder and on a file it can't read; the offline runner also fails on a folder below this one that it doesn't know.
