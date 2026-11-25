# Bases follow Obsidian

Date: 2026-11-25. Status: decided for 0.8.0 on `release/0.8.0`, under the maintainer's go-ahead to work 0.8.0 ahead on its own. It builds on [ADR 0039](../../adr/0039-bases-follow-obsidian.md): where a base's result differs, Kumbu² follows Obsidian and only adds diagnostics.

The source is an automated run of the conformance cases against Obsidian 1.15.1, which read Obsidian's own Bases API over CDP (Kumbu² note `/SWE/Kumbu/0.8.0/Obsidian cross-checks.md`). It is completed by Obsidian's reference: obsidian.md/help/bases/functions and /syntax. Points the run hasn't settled yet are marked **probe** and listed in `/SWE/Kumbu/0.8.0/Obsidian probes 2026-11-25.md`. Until a probe answers one, the reading given here holds and its case stays `source: spec`.

This changes the queries spec of 2026-11-24 in §2 decision 6 (one rule for failures), §3 (values and types) and §4 (the language). Those sections now point here.

## 1. Rows

1. **A base runs over every file of its vault.** That means notes, `.base` files and attachments, the query note and the base file themselves included (findings 1, 15, 21). Folder markers (`.vaultkeep`) and the parking area are not files of the vault and stay out. *Why:* Obsidian lists every file. A query that should list notes says so, as Obsidian's own examples do (`file.ext == "md"`).
2. **Archived notes are rows too.** `_archive/` is an ordinary folder to Obsidian. *Changed from* the queries spec §7, which left them out. *Why:* the same base must give the same rows on both sides; a query that should skip the archive filters `!file.inFolder("_archive")`. *Cost:* the existing named queries of a vault see archived notes until they filter. The `create-query` skill teaches the filter.
3. **Attachments' fields** are those of their file: `name` with its extension, `ext`, `size`, `ctime`, `mtime`, empty `properties` and `tags`. **Probe 5** confirms which fields Obsidian gives them.

## 2. Values

4. **One date type.** A date carries a time or not, as it was written or made (`today()` is midnight, `now()` has the time, `.date()` drops the time). There is no separate datetime type in values (finding 8). The conformance notation keeps `date 1980-11-03` and `date 2026-11-23T14:30:00`. Equality and order compare the moment, so `date("2026-11-25") == today()` holds at any time of that day only if **probe 4** says so. Until then, a date without a time equals only midnight.
5. **Declared types don't change a base's values** (finding 10). A base reads the YAML as YAML 1.2's core schema reads it: hex `0x1F`, octal `0o17` and `.inf` are numbers (finding 11), and a quoted value is text whatever `types.json` declares. `types.json` stays what `query properties` reports as the declared type, beside the type of the values. *Changed from* the queries spec §3. The `properties/` cases follow.
6. **A single value is not made a list** (finding 13): `aliases: Annie` stays text, and list items keep their own types.
7. **Tags keep their `#`** in `file.tags` and in a `tags` property read through a base (finding 6). `file.hasTag("project")` still finds `#project/kumbu`.
8. **`file.name` of a note drops `.md`**, and only `.md`; other files keep their extension. `file.folder` at the root is `/` (finding 2).
9. **An error is a value.** An unknown function or method, a formula that doesn't exist, a cycle, a calculation between types that don't fit (`name * 2`), `number("soon")`, or a field of a type that hasn't one (`birthday.month` on a text) gives an error value in that cell, with a message (findings 4, 14). It is not an error of the query. The conformance notation is `error`, optionally with the message's start. How an error travels through `+`, `==`, `&&`, `if`, `isEmpty`, sorting and filters is **probe 1**. Until it answers, an error taints whatever uses it, a filter that gives an error drops the row (finding 19), and a sort puts error rows after the empty ones.
10. **Numbers are JavaScript's.** `n / 0` is `Infinity`; `NaN` and the infinities display as JavaScript writes them.

## 3. Operators

11. **`&&`, `||` and `!` give booleans** (finding 12), on truthiness as JavaScript counts it.
12. **Comparisons coerce** as Obsidian does. Text that reads as a number equals and orders with that number (`"11" == 11`, `"11" > 10`), and an order against null is null, so a filter on it drops the row (finding 3). The rest of the matrix (booleans, dates against text, lists, null in arithmetic) is **probe 2**. Until then, `==` between other types is false and an order between them is null.
13. **Date arithmetic** follows the reference. `date ± "1d"` takes the units `y M w d h m s`, with their long names, chained left to right. `duration(text)` makes a duration, and `date - date` is a duration. A duration times a number keeps the duration on the left. How a duration displays and compares is **probe 3**. Until then it displays in milliseconds and compares as a number of milliseconds.

## 4. The language

14. **The functions and methods are Obsidian's**, every one that gives data. That is the reference's list: globals `date`, `duration`, `file`, `if`, `link`, `list`, `max`, `min`, `now`, `number`, `random`, `today`, `escapeHTML`. Methods per type: any (`isTruthy`, `isType`, `toString`), text, number, date (`format` in Moment's tokens, `relative`, `time`), list (`filter`, `map`, `reduce` with `value`, `index` and `acc`, `flat`, `sort`, `unique` and the rest), object, link, file and regex. Not taken: `upper` and the text `matches` (Obsidian hasn't them, finding 18). `html`, `image` and `icon` come with the UI, since they only render. *Changed from* the queries spec §4's own list. *Why:* a base written in Obsidian must run here.
15. **Regular expressions match through the regex:** `/^ann/i.matches(name)` (finding 17). An unknown flag, a pattern that doesn't compile, or one RE2 can't run (lookaround, backreferences) gives null, not an error, as Obsidian gives for its own failures. *Why null for RE2's refusals too:* the engine stays linear in time (ADR 0034). A pattern Obsidian runs and RE2 doesn't is the one place the two differ; `query check` names it.
16. **`random()`** is drawn once per run and row, so a sort by it stays stable within the run.
17. **Lambdas** (`value`, `index`, `acc`) are names only inside `filter`, `map` and `reduce`; elsewhere `value` is the property of that name. The nesting limits of the 2026-11-24 spec §4 count a lambda's body as one level.

## 5. Bases and views

18. **A view needs a name.** One without a name makes the whole base an error (`base`), as does an invalid filter group (finding 7). An embedded block with such a view shows nothing (finding 16).
19. **Only `ASC` and `DESC` sort.** A sort key without a direction, or with another one (`asc`), sorts nothing, and the rows keep path order (finding 20).
20. **Column ids are kept as written** in `order`. Obsidian normalises `status` to `note.status` (finding 9), which is notation and not meaning.

## 6. What Kumbu² adds

21. **Diagnostics on top, never instead.** `query execute` prints, before the table, how many cells hold an error, by column, with the first message of each. `query check` warns about every unknown function, method or formula, and every regex RE2 can't run, with its expression and position. These are warnings: the query still runs, as in Obsidian. *Why:* an agent reading a table must not overlook an error hidden in a column. That is the strictness the old rule (one error for the whole query) was for.
22. **Kumbu²'s own errors stay errors of the call:** `no-base`, `params`, `unknown-param`, `missing-param`, `param-value`, `unknown-view`, `base`, `syntax`, and the limits (`too-deep`, sizes). *Why:* they are about the call or about a base that can't be read, not about a cell.

## 7. Steps

1. Take over PR #338's cases and ADR (renumbered 0039), and mark the changed cases `pending: [cs, ts]` until each engine follows.
2. Both engines, C# first, then TypeScript, held to the cases. In order: rows (1–3) and file fields (8); values and YAML (4–7, 10); errors as values (9, 21, 22); operators (11–13); the function and method set (14–17); views (18–20).
3. New cases for every reference function, `source: spec` until a run confirms them.
4. The probes. Their answers move cases to `source: obsidian 1.15.1`, or correct them.
