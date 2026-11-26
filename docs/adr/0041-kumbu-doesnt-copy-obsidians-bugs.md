# 0041. Kumbu² doesn't copy Obsidian's bugs

- **Date:** 2026-11-26
- **Status:** Accepted
- **Decided by:** Chris, with Claude
- **Spec:** [Bases without Obsidian's bugs](../superpowers/specs/2026-11-26-bases-without-obsidian-bugs-design.md)
- **Supersedes in part:** [0039](0039-bases-follow-obsidian.md), where Obsidian's result is plainly a bug

## Context

[0039](0039-bases-follow-obsidian.md) made Kumbu² follow Obsidian 1.15.1 wherever a base's result differs. The automated run behind it also recorded results that look like bugs rather than design:

- A sort key without a direction, or with `asc` in lower case, sorts nothing.
- `missing || "none"` is `true`.
- `a = 1` is null instead of a syntax error.
- A value declared as a number in `types.json` but written `"4.50"` stays text.

## Decision

Kumbu² follows Obsidian's Bases where Obsidian does what it means to do. It does not copy what is plainly a bug, unless copying it is truly necessary. A case that departs from Obsidian is marked `source: kumbu` with its reason. These four results become Kumbu²'s own:

1. A sort without a direction is ascending, and the direction ignores case.
2. `&&` and `||` give an operand, as in JavaScript.
3. A single `=` is a syntax error.
4. A declared type converts a value that converts cleanly, and `query check` warns about the values that don't.

## Why

- Kumbu² is a product of its own that reads Obsidian's format, not a copy of Obsidian.
- Obsidian will fix its bugs. A bug we copied would then turn into a difference of our own, found by our users.
- A person writing `sort: [{property: due}]` means ascending, and someone writing `missing || "none"` means the fallback. Doing what they mean is the honest result; following Obsidian here would preserve its mistake.

Lost: the same result as Obsidian 1.15.1 in these four places. Someone who opens the same base in both sees the difference only where Obsidian is wrong. The conformance cases name each difference.

## Consequences

- Each departure is a case with `source: kumbu` and a reason, so a new Obsidian run shows when Obsidian has fixed one.
- Results that are design stay as Obsidian has them: every file is a row, errors are values in their cells, comparisons coerce, and a view without a name is an error of the base.
