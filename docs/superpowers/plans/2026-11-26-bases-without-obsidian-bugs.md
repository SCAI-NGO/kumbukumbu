# Bases without Obsidian's bugs: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Both query engines stop copying four Obsidian 1.15.1 bugs: sort directions, `&&`/`||` values, a single `=`, and declared types.

**Architecture:** Behaviour lives in the language-neutral cases in `tests/query-conformance/`. Each task changes its cases first, then the C# engine (`src/Kumbu.Core/Queries`), then the TypeScript engine (`packages/eidolon-core/src/query`).

**Tech Stack:** C# (.NET 10, xUnit), TypeScript (Vitest), YAML cases.

**Spec:** `docs/superpowers/specs/2026-11-26-bases-without-obsidian-bugs-design.md` (§2, items 1–4). ADR 0041.

## Global Constraints

- **Cases come first.** A case that departs from Obsidian is marked `source: kumbu` with a one-line `reason:` that names the Obsidian behaviour it departs from and why. Never change a case's expected values to fit an engine.
- **C# first, then TS, in the same task.** Both keep the same names, error and warning codes, and messages:
  - `sort-direction`: `sort direction "<d>" is neither ASC nor DESC; sorting ascending`
  - `syntax`: `use == to compare`
  - `type-mismatch`: `<n> notes have <property> values that aren't <type> (first: <path>)`
- **Keep as they are:** the limits (expression length, nesting, steps), and the regex rules of ADR 0040.
- **Don't touch:** `packages/eidolon-core/src/{engine,api,live}` and `test/fakes`.
- **Style:** SPDX header first; British English; comments only where the reasoning isn't obvious.
- **Checks before each commit:**
  - `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace Kumbu.Core.Tests.Queries --filter-namespace Kumbu.Core.Tests.Sh`
  - `pnpm -C packages/eidolon-core test`
  - `pnpm -C packages/eidolon-core build`
- **Commits:** imperative message, ending with the session's attribution lines.

## Review Focus

1. **Errors in `||` and `&&`.** `error || x` is the error, `x || error` with `x` truthy is `x`, and `false && error` is `false`. Task 1 owns this case.
2. **A single `=` inside a text or a regex** (`name == "a=b"`, `/a=b/.matches(x)`) stays valid. Task 1 owns this case.
3. **A declared number holding `"4,50"` or `"1e3"`** doesn't convert and gets a warning. Only plain decimals convert: optional sign, digits, optional fraction. Task 3 owns this case.
4. **A declared multitext holding a list** stays the list, and a single text becomes a list of one. Task 3 owns this case.
5. **`query execute` doesn't change from a type mismatch.** The mismatch only shows in `query check`. Task 3 owns this test.

---

### Task 1: Operators

**Files:**
- C#: `src/Kumbu.Core/Queries/Evaluator.cs` (logic operators), `ExpressionParser.cs` (`=`).
- TS: `packages/eidolon-core/src/query/evaluator.ts`, `parser.ts`.
- Cases: `tests/query-conformance/expressions/logic.yaml` (and any other case expecting a boolean from `||`/`&&`), `expressions/syntax-errors.yaml` or the existing syntax-error case file.
- Docs: `tests/query-conformance/README.md`, which defines `source: kumbu` and `reason:`. Both runners accept these fields (`tests/Kumbu.Core.Tests/Queries/ConformanceTests.cs`, `packages/eidolon-core/test/query.conformance.test.ts`).
- `help query` (`src/Kumbu.Core/Sh/Commands/QueryCommand.cs`) and `src/Kumbu.Core/Skills/System/create-query/SKILL.md` describe `||` as a fallback.

- [ ] **Step 1: Write the cases, `source: kumbu`.**
  - `missing || "none"` is `"none"`.
  - `nick && zero` is `0`, where `nick: "N"` and `zero: 0`.
  - `"" || 0` is `0`.
  - `1 && "x"` is `"x"`.
  - `!missing` is `true`.
  - The three error rows from Review Focus 1.
  - `a = 1` is `error: syntax` with the message start `use == to compare`.
  - `name == "a=b"` and `/a=b/.matches(name)` parse.
- [ ] **Step 2: See them red on both engines.**
- [ ] **Step 3: Implement in C#, then TS.** Truthiness is unchanged. The parser rejects a lone `=` token at its position; `==`, `!=`, `<=` and `>=` are already their own tokens.
- [ ] **Step 4: Green, run the checks, commit.**

### Task 2: Sort directions

**Files:** `src/Kumbu.Core/Queries/Base.cs` (the sort keys, around `sort-direction`), `packages/eidolon-core/src/query/base.ts`, cases `tests/query-conformance/bases/sort-directions.yaml` and `bases/sorting.yaml`, `help query`, and the skill.

- [ ] **Step 1: Change the cases to `source: kumbu`.**
  - No direction sorts ascending.
  - `asc` and `desc` work in any case.
  - `up` gives the warning `sort-direction` and sorts ascending.
  - Empty values still go last both ways, errors after them, and ties stay in path order.
- [ ] **Step 2: See them red, implement in C# then TS, see them green, commit.**

### Task 3: Declared types convert clean values

**Files:**
- C#: `src/Kumbu.Core/Queries/Properties.cs` (read with the vault's `PropertyTypes`), `PropertyTypes.cs`.
- TS: `packages/eidolon-core/src/query/properties.ts`, `propertyTypes.ts`. `propertyTypes.ts` exports that are still unused may be wired in or removed.
- Cases: `tests/query-conformance/properties/declared-*.yaml`. These were moved to Obsidian's results by #340: move them back to the spec's rules, `source: kumbu`.
- `QueryCommand.cs` (`query check`), `tests/Kumbu.Core.Tests/Sh/QueryCommandTests.cs`, `help query`, the skill.

- [ ] **Step 1: Write the cases, per the spec's §2 item 4.**
  - number `"4.50"` is 4.5;
  - checkbox `"false"` is `false`;
  - date `"2026-11-23"` is a date;
  - datetime `"2026-11-23T10:00"` is a date with a time;
  - text `01010` is `"01010"`;
  - text `2026-11-23` is `"2026-11-23"`;
  - multitext `Brita` is `["Brita"]`;
  - `aliases: Annie` with no `types.json` is `["Annie"]`;
  - number `"4,50"` stays `"4,50"`;
  - number `"1e3"` stays text.
  - Use the existing `types:` field of the case format, which declares a `types.json`.
- [ ] **Step 2: See them red, then implement in C#, then TS.**
  - Typing happens where properties are read for a run, through the vault's declared types, as before #340.
  - Use the existing `Properties.Typed(raw, type)`, from the YAML source text, where it fits.
- [ ] **Step 3: Add the `query check` warning.**
  - It reads the notes the query runs over. For each declared property with values that don't convert, it gives one `type-mismatch` warning, in the message format of the Global Constraints.
  - Tests:
    - `Check_warns_about_values_that_do_not_fit_their_declared_type`.
    - `Execute_is_unchanged_by_a_mismatch`.
- [ ] **Step 4: Green, run the checks, update the docs, commit.** The docs to update:
  - the spec 2026-11-24 §3 points to the new spec;
  - `query properties` is unchanged.
