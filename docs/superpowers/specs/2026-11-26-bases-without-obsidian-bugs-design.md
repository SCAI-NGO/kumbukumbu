# Bases without Obsidian's bugs

Date: 2026-11-26. Status: decided by Chris for 0.8.0 on `release/0.8.0`. It changes [Bases follow Obsidian](2026-11-25-bases-follow-obsidian-design.md) in the four places below ([ADR 0041](../../adr/0041-kumbu-doesnt-copy-obsidians-bugs.md), which supersedes [0039](../../adr/0039-bases-follow-obsidian.md) there).

## 1. The rule

**Kumbu² follows Obsidian's Bases where Obsidian does what it means to do, and doesn't copy what is plainly a bug.** It copies a bug only where it truly has to: for example, where a base would otherwise give rows in Kumbu² that the same person sees missing in Obsidian, and they would act on that difference. *Why:* Kumbu² is a product of its own that reads Obsidian's format. Obsidian will fix its bugs, and a copied bug would then become our own difference. Each case that departs from Obsidian says so with `source: kumbu` and its reason.

## 2. What changes

1. **A sort key without a direction sorts ascending.** The direction ignores case (`asc`, `Desc`). A direction that is neither gives the warning `sort-direction`, and the key sorts ascending. *Changed from* decision 19 (finding 20: Obsidian sorts nothing).
2. **`&&` and `||` give one of their operands, as in JavaScript.** `a || b` is `a` when it is truthy, else `b`. `a && b` is `a` when it is falsy, else `b`. So `missing || "none"` is `"none"`. `!` still gives a boolean. Errors keep decision 9's rules: an error operand that is evaluated taints the result, and `false && error` is `false`. *Changed from* decision 11 (finding 12: Obsidian gives booleans).
3. **A single `=` is a syntax error** (`syntax`), with the message `use == to compare` at its position. Bases has no assignment. Not affected: `==`, `!=`, `<=`, `>=`, and `=` inside a text or a regex. *Changed from* finding 18 (Obsidian silently gives null).
4. **A declared type changes a value when the value converts cleanly.** The declared type is the vault's `.obsidian/types.json`. Converting cleanly means:
   - number: a text that is a plain decimal (`"4.50"` is 4.5);
   - checkbox: the text `true` or `false`, in any case;
   - date: a text that is an ISO date (`"2026-11-23"`);
   - datetime: an ISO date with a time;
   - text: a number or date, taken as its YAML source text (`01010` stays `"01010"`);
   - multitext: a single text, which becomes a list of one.

   `tags`, `aliases` and `cssclasses` are multitext in every vault. A value that doesn't convert keeps its own value, and `query check` warns about it with `type-mismatch`, giving the property, the declared type, how many notes and the first of them. *Changed from* decision 5 and decision 6 (findings 10 and 13). This is the 2026-11-24 spec's §3 again, plus the warning.

Decision 18 stays as Obsidian has it: a view without a name, or an invalid filter group, is the `base` error, which `query check` already names.

## 3. Steps

Each change is made in both engines, C# first, then TypeScript, and held to the conformance cases.

- Cases that change become `source: kumbu` with a `reason:` line.
- The conformance README defines `source: kumbu` and `reason:`.
- `help query` and the `create-query` skill follow.
