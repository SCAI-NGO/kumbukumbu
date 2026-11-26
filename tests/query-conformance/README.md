# Query conformance cases

The cases every query implementation of Kumbu² is held to: the C# engine in `src/Kumbu.Core/Queries` (runner: `tests/Kumbu.Core.Tests/Queries/ConformanceTests.cs`) and, from 0.8.0 on, the offline engine in `packages/eidolon-core/src/query` (runner: `packages/eidolon-core/test/query.conformance.test.ts`). A case is written once, here, and never copied into a test of either side; each side has one runner that reads every file below this folder. Why: two implementations of one semantics drift unless one set of cases holds them both.

What a person sees as a result belongs here: how a note's properties are typed, which rows a query gives and in what order, which error it gives, and where the limits both engines share stop a query (`expressions/limits-of-values-and-work.yaml`, `expressions/regex.yaml`). What only one implementation has inside (its parser, its caches, its storage, its stack) keeps ordinary unit tests on its own side.

## A case

One YAML file per case, named for what it shows.

```yaml
description: One sentence on what the case shows.
source: spec          # or "obsidian 1.15.1": checked in real Obsidian, with that version; or "kumbu": departs from Obsidian
reason: …             # with source: kumbu only, one line: the Obsidian behaviour it departs from, and why
types:                # optional: the "types" of the vault's .obsidian/types.json, which change no value
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
                      # a case pending for a known bug of one engine names the bug in its description
```

A file in `notes` is a note when its path ends in `.md` (case counting: `X.MD` is an attachment). Any other path is another file of the vault, a `.base` file or an attachment (`photo.png`), with that text as its content and no properties or tags; its `file.size` is the text's UTF-8 length, as a note's is. A query runs over every file, as Obsidian's Bases do. `file.ctime` and `file.mtime` are null unless the case gives a file its times, each written `yyyy-MM-ddTHH:mm:ss`, without a zone:

```yaml
times:                # optional: path: when the file was created and last changed
  People/Ann.md: { ctime: 2026-11-20T09:15:00, mtime: 2026-11-24T18:00:00 }
```

A case about expressions (in `expressions/`) gives a list of `cases` instead of `expect`, each an expression with its value for every note, or the error of the query the expression is:

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
    values: { Ann.md: error Cannot find function "nothing" }
  - expression: a = 1
    error: syntax             # or too-deep, too-large
    message: use == to compare   # optional: the error's message starts with this text
```

An expression that runs but can't give a value for a note (an unknown function or method, a formula that doesn't exist or uses itself, a calculation between types that don't fit) gives an error value in that note's cell, as Obsidian does; `error` is only for what fails the whole query: syntax, before any note is read, and the limits, which the runner looks for by evaluating the expression for every note. The expressions of one file are one run over its notes, so `random()` gives a note the same number in each.

A case about bases (in `bases/`) gives the base and a list of `runs`, each a view (the first when left out) with its columns and rows in order, or the error it is; `now` and `this` work as in an expression case. Without `file`, the base is a block outside the vault and not a row; with it, the base is that file of the vault, a row where its filters let it be:

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
    error: unknown-view        # or base (a view without a name, an invalid filter group: every view of the base), and the expressions' errors (syntax, too-deep, too-large)
```

A case about query notes (in `queries/`) names the query, optionally its parameters and views as read, and `runs` with the call's `args` (a list gives a multitext parameter one value per item); a run may name a `query` of its own. The query note is one of the notes, so it is a row of its own base where the filters let it be, as in Obsidian:

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
    error: missing-param   # or no-base, params, unknown-param, param-value, meaning, and the bases' codes
```

A query note with `meaning:` ranks its rows by vectors no case can compute without a model, so the case gives them. The offline engine has none, and a case says what it gives instead:

```yaml
vectors:             # optional: the vectors a run with meaning takes
  query: [1, 0]      # the text's
  notes:             # path: the vector of the meaning's property, for each note that has one
    Notes/Same.md: [1, 0]
offline:             # optional: what the offline engine gives for every run instead of what the run expects
  error: meaning-offline
```

A score is the cosine rounded to six decimals, in both engines. `text` names a parameter when it is a plain scalar, and that parameter must be of type text; any other scalar (quoted, or a block) is the literal text.

The server's runner passes `vectors` to every run, and the offline runner holds every run to `offline` when the case has it. A query whose `meaning:` can't be read fails at reading on both engines, so a case of such errors needs no `offline`.

`source: spec` marks our own reading where Obsidian hasn't been checked; a case moves to `obsidian <version>` once someone has seen the same result in Obsidian. Where Obsidian gives another result, the case takes Obsidian's ([ADR 0039](../../docs/adr/0039-bases-follow-obsidian.md)), unless Obsidian's result is plainly a bug: then the case keeps Kumbu²'s result with `source: kumbu` and a `reason:` naming what Obsidian does and why Kumbu² doesn't ([ADR 0041](../../docs/adr/0041-kumbu-doesnt-copy-obsidians-bugs.md)). A runner fails a `kumbu` case without a reason, and a reason on any other case; a new Obsidian run checks the `kumbu` cases to see whether Obsidian has fixed one.

## Values

A typed value is written as its type and its normalised text, so a comparison tests meaning, not serialisation:

| Written | Meaning |
|---|---|
| `text Ann`, `text` | text (`text` alone: the empty text) |
| `number 11`, `number 1.5`, `number Infinity`, `number NaN` | a number, in its shortest form; the infinities and NaN as JavaScript writes them |
| `boolean true` | a checkbox |
| `date 1980-11-03`, `date 2026-11-23T14:00:00` | a date, without a time or with one (without a zone, as Obsidian keeps it); one type, as in Obsidian |
| `duration 86400000` | a duration, in milliseconds: how it shows until probe 3 of the bases spec of 2026-11-25 (decision 13) |
| `null` | no value |
| `[text a, text b]` (a YAML list) | a list |
| a YAML map of values | an object |
| `file People/Ann.md` | a note's file, as `file` and `this.file` give it |
| `link People/Ann.md`, `link People/Ann.md\|Ann` | a link, by the path of the file it leads to (its target as written when it leads to none), and after `\|` the text it shows, if it was given one |
| `error`, `error Cannot find function "nothing"` | an error value: any (`error` alone), or one whose message starts with the text given, so a case can quote as much of Obsidian's message as it needs |

A regular expression has no notation of its own: a case shows one through `toString()`, which writes it as JavaScript does, the flags in `gimsu` order (`/a+b/ig.toString()` is `text /a+b/gi`). A list or object is compared item by item, so an `error` inside one matches as above. A number is written as JavaScript and .NET's round-trip format both write it, which has no notation for `-0`; no case gives one.

A runner fails on an empty folder and on a file it can't read; the offline runner also fails on a folder below this one that it doesn't know.

## Regular expressions

Both engines run only the part of RE2's syntax they read the same way (`RegexShape` in each; anything else is null), and the server rewrites a pattern to mean what RE2 means (`\d`, `\w` and `\s` as ASCII, `$` without `m` as the end of the text, case folding as RE2 folds). A pattern is also null past RE2's bound on counts (1,000) or past 64 positions written out, and every search is charged in steps, so a case can show `too-large` for a search; the rules and their reasons are in the bases spec of 2026-11-25, §7 decision 28 ([ADR 0040](../../docs/adr/0040-query-work-is-bounded-and-regexes-are-a-common-subset.md)). A class takes `\d`, `\w` and `\s` but not their negations, so `[\s\S]` is null: write `.` with the `s` flag. `expressions/regex.yaml` holds what runs, what is null and what a pattern means; `expressions/regex-checked-in-obsidian.yaml` what Obsidian was seen to give. Two differences remain, so no case pins them:

- `\b` and `\B` see a word character as Unicode on the server and as ASCII (`[0-9A-Za-z_]`) offline: `/caf\b/` matches "café" offline only. .NET's linear engine has no way to write RE2's boundary (no lookaround, no ECMAScript option).
- A character outside the BMP (an emoji) is two UTF-16 units to `.` and a negated class on the server and one code point offline, so a match's place and length can differ on such text. A pattern may not hold one.

One difference is a bug of .NET's linear engine, pinned as pending for C# (`expressions/regex-leftmost-match.yaml`).


## Known differences

Where the engines still give different results outside regular expressions, so no case pins them:

- `lower()` lowers a text as each platform does without a culture: the server by .NET's `ToLowerInvariant`, offline by JavaScript's `toLowerCase`. They differ on a few letters: `İ` (U+0130) becomes `i̇` (an `i` with a combining dot) offline and `i` on the server, and a capital sigma at the end of a word becomes `ς` offline and `σ` on the server.
