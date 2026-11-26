# 0040. A query's work is bounded, and its regular expressions are a common subset

- **Date:** 2026-11-26
- **Status:** Accepted
- **Decided by:** Claude (the build of "Bases follow Obsidian", its reviews and rulings), with the maintainer's go-ahead for 0.8.0
- **Spec:** [Bases follow Obsidian](../superpowers/specs/2026-11-25-bases-follow-obsidian-design.md) §7

This refines [0034](0034-offline-queries-match-regular-expressions-in-linear-time.md): matching stays linear in time on both sides, and the patterns both sides run are now the same ones.

## Context

Query text is anyone's input: in a shared vault, one member writes a query note and another runs it, on the server or in a tab. Following Obsidian ([0039](0039-bases-follow-obsidian.md)) brought lambdas (`map`, `filter`, `reduce`), long texts and regex `replace` and `split`, and with them work that grows without end: `reduce(acc + acc, "x")` doubles a value at each item, formulas chained through each other went 64 × 64 levels deep and overflowed the stack in both engines, and a global `replace` by `/(?:a*c)?/` read the rest of the text once per match.

Linear-time matching (0034) was not enough either. .NET's `RegexOptions.NonBacktracking` builds its states lazily as it matches, and it doesn't look at its match timeout while it does: for written-out optional groups of overlapping branches the first search takes 380 ms at 63 positions, 5.5 s at 99, and minutes for a 720-character pattern of 40 such groups. re2js has no such states, but its cost per character grows with the pattern's size. And the two engines read the edges of the syntax differently (`\Z`, `\p{…}`, named groups numbered differently, `[a-z-[b]]`), so one pattern could mean two things.

## Decision

A query's work is bounded deterministically and identically in both engines: 10,000 characters and 64 levels of nesting for an expression, one budget of 512 levels for an expression and the formulas it uses added up, 1,000,000 in size and 128 levels of nesting for a value, 1,000,000 steps for a note, and 5,000,000 steps and 10,000,000 in size for a run. Past a bound the call fails with `too-deep` or `too-large`. The server also stops a cancelled run between rows.

A query's regular expressions are the common subset of RE2 and .NET that a strict reader (`RegexShape`, the same in both engines) accepts, with RE2's bound on counts, at most 64 positions written out (loops counted twice), and a charge in steps for building a pattern (128 × size²) and for each search (1 + read × (4 + size) / 20). Any other pattern is null, with a `query check` warning. The server rewrites an accepted pattern to mean what RE2 means.

## Why

- **Deterministic, not timed:** a time budget would give the same query a result on a fast machine and an error on a slow one, and differ between the server and a tab; the conformance cases could not hold the two to one result. Steps are counted the same on both sides, so a query is `too-large` everywhere or nowhere.
- **Bounded by the run, not only the note:** the bound on one note times the notes would let a large vault keep the server busy for hours. At its bound a run takes at most about 3 s on the server and 5 s in a tab, as measured over 35 kinds of expression; ordinary queries fit vaults of about 250,000 notes.
- **One depth budget:** each formula kept to 64 levels on its own, but not together; 512 fits a thread pool's 1.5 MB stack with room to spare (it runs out at about 1,420 levels in a Debug build).
- **A gate by written-out size:** it is the measure the lazy state building of .NET grows with, and re2js's cost too; at 64 the costliest pattern found takes 388 ms on its first search of 1,000,000 characters. A match timeout lost, because it doesn't fire while the states are built. A bound on counts alone lost, because optional groups written out reach the same cost without a count.
- **A common subset, by a whitelist:** a reader that looked only as far as it needed missed constructs (a `(?#` comment hid a pattern from the size check); reading only what both engines read alike means they can't disagree on what a pattern is.

## Consequences

- Some patterns RE2 runs are null here: `.{0,500}`, `a{0,1000}`, named groups, `\p{…}`, an escaped `_`, the twelve month names written out (74 positions), `^[a-z]{1,64}$` (66). `query check` names each, and the `create-query` skill says how to write them within the subset.
- A query can end in `too-large` where Obsidian would answer: a long text split into many parts, a large lambda over a large list, a frontmatter value over 1,000,000 characters.
- Every new function or method must charge its work in both engines alike, and its builders must check sizes as they go; the conformance case `expressions/limits-of-values-and-work.yaml` holds the bounds.
- Two engine differences stay, documented in `tests/query-conformance/README.md`: `\b` is Unicode on the server and ASCII offline, and text outside the BMP counts as two units on the server. A bug of .NET 10.0.15's NonBacktracking engine (a wrong leftmost match, `a?b` on "acbb") is pinned as `pending: [cs]` until .NET fixes it.
- The bound of 64 could be refined later (single-atom bodies are cheap in .NET but not in re2js), at the price of two sizes to keep equal.
