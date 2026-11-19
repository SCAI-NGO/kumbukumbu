# Reading CSV, HTML and XML Implementation Plan

> Execute task by task, subagent-driven, with a review after each and a whole-branch review at the end. Steps use checkbox (`- [ ]`) syntax.

**Goal:** `sh csv`, `sh html` and `sh xml` read structured text files.

**Spec:** `docs/superpowers/specs/2026-11-19-reading-csv-html-xml-design.md` (approved 2026-11-19; decision numbers below refer to it).

## Global constraints

- British English; SPDX header on new files; match the shell's idiom (`Head.cs`, `Rg.cs`, `Options`, `CommandArgs`, `ShellResult`, the output limit and its closing line).
- **Test first:** a failing test before each step's code.
- **Hard (security):** HTML is parsed without scripting or a loader, nothing fetched (decision 8); XML with `DtdProcessing.Prohibit`, no resolver, no entity expansion (decision 9). Both pinned by tests.
- Only text files (decision 2): `text/plain; charset=utf-8` attachments, or piped input for `csv`.
- One heavy test run at a time in the container.

## Task 1: `csv` (decisions 3–6)

**Files:** `src/Kumbu.Core/Sh/Commands/Csv.cs` (+ a small parser beside it if it grows), registration where the shell's commands are listed, `Help.cs`/`Manual.cs`; tests in `tests/Kumbu.Core.Tests/Sh/`.

- [ ] Overview without options; `--rows`, `--cols` (names, letters, numbers), an `A1` range; `--tsv`; `--sep`, `--header`/`--no-header`; dialect sniffing (`,` `;` tab `|`); RFC 4180 quoting, BOM, CRLF; Markdown table with escaping; output limit with "csv: N of M rows shown; narrow with --rows or --cols"; a malformed row reported with its line; refusals for bytes, notes, missing paths.

## Task 2: `html` and `xml` (decisions 7–9)

**Files:** `src/Kumbu.Core/Sh/Commands/Html.cs`, `Xml.cs`, `src/Kumbu.Core/Kumbu.Core.csproj` (AngleSharp, its licence in `THIRD-PARTY-NOTICES.md`), registration, help; tests.

- [ ] `html`: overview, selector → text per element, `--html`, `--attr`, `--count`, a table as Markdown; a no-fetch test against a local listener.
- [ ] `xml`: overview, XPath → string values, `--xml`, `--count`, `--ns`, the default namespace as `d` and the hint; a DTD refused (XXE and billion-laughs samples), no external resource.

## Task 3: Docs, console completion, the whole suite

- [ ] `README.md` (the `sh` list), `docs/security-architecture.md` (HTML: nothing runs or loads; XML: no DTD), the console's completion of command names (`web/src/console/`) if it lists them.
- [ ] Whole server and core suite, web suite and build; a whole-branch review; open the PR.
