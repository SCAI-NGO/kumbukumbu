# Test notes: phase 2 (sh pipes, filters, rg and help)

Date: 2026-10-22. Hands-on test of build order step 2 (PR #2, merged as `7e32880`), run locally with Claude Code as the MCP client. Setup as in [phase 1](notes-test-phase1.md): stack on `127.0.0.1:8083`, sample vaults `Demo` and `Ideas`. `dotnet test`: 295 passed, 0 failed, 0 skipped.

For the truncation test a temporary vault `Big` held one note of 3000 lines (about 210,000 characters); it was removed afterwards and the database reseeded with `docker compose down -v`.

## Results

All calls returned instantly.

### Navigation and search

| Call | Result |
|---|---|
| `rg --files \| rg -i kumbu` | `/Demo/projects/kumbu.md`: resolves the `[[kumbu]]` wikilink, as the tool description suggests |
| `rg -l "\[\[welcome\]\]"` | `/Ideas/retrofuturism.md`: finds backlinks by hand |
| `rg -n -i memory` | Three hits in `/Ideas/Überlegungen.md`, as `path:line:text` |
| `rg -n Gedächtnis /Ideas` | Correct hit; umlauts work in patterns |
| `cat "/Demo/Meeting Notes.md" \| rg -n Agreed \| wc -l` | `2` |
| `rg --files \| sort -r \| head -n 3` | Works (ordering: see observation 2) |
| `tail -n +3 /Demo/welcome.md \| head -n 4` | Correct lines |
| `help rg` | Full page with options and examples |

### Misuse and errors

| Call | Result |
|---|---|
| `cat … > /Demo/hack.md` | `'>' is not supported; the only operator is '\|'` |
| `ls /Demo && cat …` | `'&&' is not supported` |
| `cat $(rg --files \| head -n 1)` | `'$(' is not supported` |
| `cat /Demo/*.md` | `No such file or directory` (no globbing) |
| `grep -r kumbu /` | `command not found: grep — use rg (try 'help rg')` |
| `rg "(a+)+$"` | Returns instantly (no catastrophic backtracking) |
| `rg "(?=foo)bar"` | Rejected: look-around needs backtracking, which rg never does |
| `ls \| cat /Demo/welcome.md` | `cat does not read from a pipe; only rg, head, tail, wc, sort do` |
| `rg 'unclosed('` | `regex parse error: … Not enough )'s.` |
| `rg "unclosed(` | `unterminated quote` |
| `rg -l kumbu /Demo/../../..` | Clamped to the root, searches all vaults |
| `head -n -5 …` | `invalid number of lines: '-5'` |
| `ls \|` | `syntax error: '\|' needs a command after it` |

### Output limit

`cat /Big/huge.md` was cut at 24,000 characters with `[truncated — showing 24000 of 210791 chars (~70264 tokens); narrow it with rg, head or tail]`. The hint works: the next call, `rg -c TODO`, gave `/Big/huge.md:3000` directly.

## Observations from the agent's side

Phase 1 observations 1 to 3 (wikilinks, search, `help`) are resolved.

1. **Truncation cuts mid-line.** The output above ended in `Line 350: the quick brown fox jumps over the lazy dog, TODO`, losing the rest of the line. An agent can take a cut line for a complete one. Cut at the last line break within the limit instead (unless a single line is longer than the limit; then cut that line and say so).
2. **`sort` is ordinal**, like `LC_ALL=C`: `/Ideas/Überlegungen.md` sorts after `/Ideas/retrofuturism.md`; umlauts land after `z`, far from their base letters. Vault content is human text, often German. Proposal: compare with `CultureInfo.InvariantCulture` by default (Ü sorts with U, deterministic on every server). Check that the runtime image actually has ICU; with `InvariantGlobalization` or a chiseled image without ICU, .NET silently falls back to ordinal. A test with umlauts should pin the expected order. Whether `rg --files` and `ls` should use the same order is worth deciding at the same time.
3. **Stale tool description in running clients** (no action needed). Claude Code reads the tool description at session start, so a session that was open across the deploy still had the phase 1 description. The new commands worked anyway, as the server does not depend on it. Worth a sentence in the README: restart the MCP client after upgrading the server.

## Suggested next step

Fix observations 1 and 2 (small, each with tests), add the README sentence from observation 3, then carry on with build order step 3 (`edit` / `write` with version tokens; history and trash).
