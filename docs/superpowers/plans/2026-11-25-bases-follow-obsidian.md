# Bases follow Obsidian — plan

Spec: `docs/superpowers/specs/2026-11-25-bases-follow-obsidian-design.md` (decisions 1–22, its §7 steps). ADR 0039. The findings it cites are in the Kumbu² note `/SWE/Kumbu/0.8.0/Obsidian cross-checks.md`, section "Automated run 2026-11-25"; Obsidian's reference is obsidian.md/help/bases/functions and /syntax.

Goal: both query engines, the C# one in `src/Kumbu.Core/Queries` (with `src/Kumbu.Core/Sh/Commands/QueryCommand.cs`) and the TypeScript one in `packages/eidolon-core/src/query`, give a base the result Obsidian 1.15.1 gives, held to the cases in `tests/query-conformance/`, with Kumbu²'s diagnostics on top.

## Global constraints

- **The spec decides; the cases show it.** Every behaviour change starts as a case (new, or a changed one), red on the engine being changed. A case Obsidian confirmed is `source: obsidian 1.15.1`; a case from the reference or from a reading the spec marks **probe** is `source: spec`. Never change a case's expected values to fit an engine; where an engine and a case disagree, the engine is wrong unless the spec says otherwise, and then the case changes with a line in the report saying which decision.
- **C# first, then TypeScript, in the same task.** A task ends with every case it touches green on both engines and its `pending` gone. `pending` may stay only on a case another, later task owns, and then with that task named in the report. Never add `pending` to get green.
- **Both engines keep their shape.** Same names, same error codes and, where practical, the same messages (Obsidian's wording for error values in cells, as the findings quote it: `Cannot find function "nothing"`, `Invalid operator between String and Number`, `Unable to parse "soon" as a number.`, `Cannot find "month" on type String`, `Infinite loop detected in formula`, `Cannot find "missing" on type Formulas`). The limits stay: expression length 10,000, nesting 64 (a chain's steps count, a lambda's body one level), base nodes and YAML values 10,000, YAML depth 64; regular expressions in linear time (C# `RegexOptions.NonBacktracking`, TS `re2js`; never `new RegExp` on a query's pattern).
- **The sync code stays untouched:** nothing in `packages/eidolon-core/src/engine`, `src/api`, `src/live` or `test/fakes`.
- **The conformance format** is `tests/query-conformance/README.md`. Changes to it (new fields, the `error` notation, the one date type) land in its README in the task that introduces them, and in both runners (`tests/Kumbu.Core.Tests/Queries/ConformanceTests.cs`, `packages/eidolon-core/test/query.conformance.test.ts`).
- **Style.** `// SPDX-License-Identifier: AGPL-3.0-or-later` first; British English; comments only where the reasoning isn't obvious, at the density of the surrounding code. No customer or employer names in code, docs or test data. Unit tests only for what one engine has inside; behaviour goes into cases.
- **Checks before each commit:** `dotnet test --project tests/Kumbu.Core.Tests` (the query and sh tests at least: `-- --filter-namespace Kumbu.Core.Tests.Queries --filter-namespace Kumbu.Core.Tests.Sh`), `pnpm -C packages/eidolon-core test`, `pnpm -C packages/eidolon-core build`. Commit messages imperative, free-form, ending with the session's attribution lines.

## Task 1: rows and file fields (decisions 1–3, 8)

Cases: `expressions/file-fields.yaml` and `bases/*` that list rows. The case format gains:
- a file in `notes` whose path doesn't end in `.md` is an attachment (or, ending in `.base`, a base file) with that text as content;
- an optional `file:` on a base case, the path of the base file itself (`case.base`): the base is then one of the vault's files and a row where its filters let it be; left out, the base is a block outside the vault, as now;
- optional `times:` (`path: { ctime: …, mtime: … }`) for `file.ctime`/`file.mtime`; left out, both are null in a case.

Engines: a query's rows are every file (notes, `.base` files, attachments; the query note and the base file included; archived notes included); `QueryNote` (`note.ts`) carries a kind or extension, size in bytes (UTF-8 of the text for a note, the stored size for an attachment), ctime and mtime. An attachment has empty properties and tags. `file.name` drops `.md` only; `file.basename` drops any extension; `file.folder` is `/` at the root; new fields `size`, `ctime`, `mtime` (dates with a time). `QueryCommand` reads attachments too (`StoreAttachments.UnderAsync`) and no longer leaves out `_archive/` for a run (folder markers `.vaultkeep` and the parking area stay out; `query ls` still lists only queries outside the archive). Update the `FieldReference` lists, `help query` and `create-query` (`src/Kumbu.Core/Skills/System/create-query/SKILL.md`: filter `file.ext == "md"` for notes only, `!file.inFolder("_archive")` to skip the archive). Base cases whose rows change because the base file or the query note is now a row are updated to Obsidian's rows.

## Task 2: values and YAML (decisions 4–7, 10)

Cases: `properties/*`, `expressions/dates.yaml`, `expressions/text-and-list-methods.yaml`, `expressions/file-has-tag-and-in-folder.yaml`, `expressions/numbers.yaml`.

- One date type: `DateValue` carries whether it has a time; `datetime` goes from the value union (the conformance notation keeps `date 1980-11-03` and `date 2026-11-23T14:30:00`; `datetime …` in cases becomes `date …`). Equality and order compare the moment; a date without a time equals only midnight (probe 4).
- Declared types don't change values: `Properties.read` types by YAML only. `PropertyTypes` stays, for `query properties`' declared type. The `declared-*` cases move to Obsidian's results (finding 10) and say so.
- YAML 1.2 core numbers: hex `0x1F`, octal `0o17`, `.inf`/`-.inf`/`.nan` are numbers.
- A single value is not made a list (`aliases: Annie` is text); list items keep their own types.
- Tags keep their `#` in `file.tags` and in a `tags` property; `file.hasTag("project")` still finds `#project/kumbu`.
- Numbers display as JavaScript writes them (`Infinity`, `-Infinity`, `NaN`); `n / 0` is `Infinity`.

## Task 3: errors are values, and the diagnostics (decisions 9, 21, 22)

Cases: `expressions/errors.yaml`, `expressions/arithmetic-and-text.yaml`, `expressions/functions.yaml`, `expressions/formulas.yaml`, `bases/base-errors.yaml`, a new `bases/a-filter-on-an-error.yaml` (finding 19), new `queries/` cases for the summary.

- An `ErrorValue` (message) in the value union; notation `error` or `error <start of message>`. Unknown function or method, missing formula, a formula cycle, an operator between types that don't fit, `number("soon")`, a field of a type that hasn't it: an error value in that cell. Until probe 1 answers: an error taints whatever uses it (operators, method calls, `if`'s condition), a filter that gives an error drops the row, a sort puts error rows after the empty ones.
- Kumbu²'s own errors stay errors of the call (decision 22), `syntax` and the limits included.
- `query execute` prints, before the table, the number of error cells per column with the first message of each (nothing when there are none). `query check` warns about every unknown function, method or formula and every regex RE2 can't run, with expression and position; warnings, the query still runs. Unit tests in `tests/Kumbu.Core.Tests/Sh/QueryCommandTests.cs`.

## Task 4: operators (decisions 11–13)

Cases: `expressions/logic.yaml`, `expressions/comparisons.yaml`, a new `expressions/date-arithmetic.yaml`.

- `&&`, `||`, `!` give booleans on JavaScript truthiness.
- Comparisons coerce: text that reads as a number equals and orders with that number; an order against null is null. Otherwise (probe 2) `==` between other types is false and an order between them null.
- Dates: `date ± "1d"` with units `y M w d h m s` and long names (`year(s)`, `month(s)`, `week(s)`, `day(s)`, `hour(s)`, `minute(s)`, `second(s)`), chained left to right (`"1y 2M"`); `duration(text)`; `date - date` is a duration; duration × number keeps the duration on the left. A duration displays and compares as milliseconds (probe 3).

## Task 5: the function and method set (decisions 14–17)

Cases: a new case file per type in `expressions/` (`functions-global.yaml`, `methods-any.yaml`, `methods-text.yaml`, `methods-number.yaml`, `methods-date.yaml`, `methods-list.yaml`, `methods-object.yaml`, `methods-link-and-file.yaml`, `regex.yaml`), `source: spec` except where a finding confirmed a result. The existing cases that use `upper` or text `matches` move to Obsidian's result (an error value).

Every function and method of the reference that gives data: globals `date`, `duration`, `file`, `if`, `link`, `list` (`list(missing)` is null, finding 8), `max`, `min`, `now`, `number`, `random`, `today`, `escapeHTML`; any `isTruthy`, `isType`, `toString`; text `contains`, `containsAll`, `containsAny`, `endsWith`, `isEmpty`, `lower`, `replace` (text replaces every occurrence, regex the first unless `g`, `$1`), `repeat`, `reverse`, `slice`, `split` (text or regex, limit), `startsWith`, `title`, `trim`, `length`; number `abs`, `ceil`, `floor`, `isEmpty`, `round(digits?)`, `toFixed`; date `date`, `format` (Moment tokens), `time`, `relative`, `isEmpty`, fields `year month day hour minute second millisecond`; list `contains`, `containsAll`, `containsAny`, `filter`, `flat`, `isEmpty`, `join`, `map`, `reduce` (`value`, `index`, `acc`), `reverse`, `slice`, `sort`, `unique`, `length`; object `isEmpty`, `keys`, `values`; link `asFile`, `linksTo`; file `asLink`, `hasLink`, `hasProperty`, `hasTag`, `inFolder`, fields `links`, `embeds`, `backlinks`, `file`; regex `matches`. Not: `upper`, text `matches`, `html`, `image`, `icon`. Regex flags `g i m s u`; an unknown flag, a pattern that doesn't compile or one RE2 can't run gives null. `random()` once per run and row. Lambda names only inside `filter`, `map`, `reduce`. `help query`'s reference lists and the `create-query` skill follow.

## Task 6: views (decisions 18–20)

Cases: `bases/unnamed-views.yaml`, `bases/base-errors.yaml`, `bases/sorting.yaml`, `queries/defaults-and-the-query-note.yaml`, `queries/options-lists-and-dates.yaml`, a new `bases/sort-directions.yaml`.

- A view without a name, or an invalid filter group, makes the whole base the `base` error, its named views too; a query note's block with such a view too.
- Only `ASC` and `DESC` sort; a key without a direction or with another one sorts nothing (rows in path order).
- `order` keeps column ids as written.

## Task 7: docs

`tests/query-conformance/README.md` complete for every format change; the spec 2026-11-24 points to the new one where it changed (done with the spec); `help query` and `create-query` read once more as a whole; `packages/eidolon-core/README.md`'s "Queries" section if the engine's surface changed. The Kumbu² changelog entry `/SWE/Kumbu/Versions/0.8.0.md` is the controller's, after the merge.
