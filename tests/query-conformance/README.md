# Query conformance cases

The cases every query implementation of Kumbu² is held to: the C# engine in `src/Kumbu.Core/Queries` (runner: `tests/Kumbu.Core.Tests/Queries/ConformanceTests.cs`) and, from 0.8.0 on, the offline engine in `packages/eidolon-core`. A case is written once, here, and never copied into a test of either side; each side has one runner that reads every file below this folder. Why: two implementations of one semantics drift unless one set of cases holds them both.

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
pending: [ts]         # optional: implementations that don't pass it yet; never skipped silently
```

`source: spec` marks our own reading where Obsidian hasn't been checked; a case moves to `obsidian <version>` once someone has seen the same result in Obsidian.

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

A runner fails on an empty folder and on a file it can't read.
