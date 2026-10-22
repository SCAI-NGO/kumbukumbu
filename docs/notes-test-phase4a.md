# Test notes: phase 4a (sections)

Date: 2026-10-22. Hands-on test of build order step 4a (PR #4, merged as `f7ff1ac`): `outline`, section addresses, section edits, and the phase 3 remarks. Setup as in [phase 1](notes-test-phase1.md): stack on `127.0.0.1:8083`, sample vaults `Demo` and `Ideas`. `dotnet test`: 474 passed, 0 failed, 0 skipped (about 5 s).

Afterwards the database was wiped with `docker compose down -v` and reseeded from the samples.

## Setup hiccup: the old server was still running

The Claude Code session was restarted after `git pull`, but the stack had not been rebuilt: the image dated from before the merge, so the server, and with it the tool descriptions the new session loaded, were still phase 3. Fixed with `docker compose up --build -d`. The new descriptions were then read with `tools/list` over `curl`; the session's tool schemas stay phase 3 until the next restart, so `replace_section` was called through a small JSON-RPC client, everything else through the session's own tools.

The README already says to start a new client session after upgrading (line 58). It does not say that the server must be rebuilt first; see observation 4.

## Results

A test note `/Demo/sections.md` held YAML frontmatter, text before the first heading, two `## Notes` under one parent, a fenced code block with `#` lines, a `#tag` line, a heading with umlauts, `### C\# tips ###` with an escaped hash and a closing sequence, a `####` under it, and two blank lines before the last heading.

### Outline and reading

| Call | Result |
|---|---|
| `outline /Demo/sections.md` | All 8 headings with line ranges, indentation, `[1]`/`[2]` for the duplicates and token sizes; frontmatter, code block and `#tag` correctly ignored; `(before the first heading)` for the intro; a usage hint as last line |
| `cat …#Gates` | The section only; trailing blank lines left out; version of the whole note |
| `cat …#Notes` | `ambiguous, it matches #Architecture#Notes[1], #Architecture#Notes[2]` |
| `cat "…#Architecture#Notes[2]"` | Correct, including the code block and the `#tag` line |
| `cat "…#größe & überblick"` | Found by the case-insensitive fallback, umlauts included |
| `cat …#Deep` | Found from the unique last level alone |
| `cat "…#Größe & Überblick#Deep"` (skipping `### C\# tips`) | `no such section`; consistent with the design (an address is a contiguous chain) |
| `cat …#Architecture \| wc -l` | `22`, matching the outline's range 7–28 |
| `tail -n 2 "…#Größe & Überblick"` | Correct lines of the section |
| `cat …#Nope` | `no such section ('outline …' lists them)` |
| `rg Gate …#Gates` | Rejected with the exact `cat … \| rg` to use instead |
| `outline …#Architecture` | Rejected: give the note without a section |
| `outline` of a 3200-line note | Truncated at a line end; `outline … \| rg "Part 79[0-9]"` narrowed it to the right lines |
| `cat` of a long note | Footer now: `narrow it with outline, cat 'note.md#Section', rg, head or tail` |

### Headings with `#` in them

| Heading in the source | Address that works | Address that fails |
|---|---|---|
| `# C# tips` (plain hash) | `cat "/Demo/hash.md#C\# tips"` | |
| `# Q\#A` (Markdown-escaped hash, rendered `Q#A`) | `cat '/Demo/hash2.md#Q\\\#A'` (single quotes only) | `"…#Q\#A"`, `"…#Q\\\#A"` (double quotes) |

See observation 1.

### Section edits

| Call | Result |
|---|---|
| `edit` `…#Gates` without `old_text` | `appended to section #Architecture#Gates … at line 14`, before the blank lines that separate it from the next heading |
| `edit` `…#Notes[2]` with `old_text: "notes."` | Edited at line 22; `notes.` occurs twice in the note but once in the section, so the address disambiguates as designed |
| `edit` with a stale version | Usual conflict message |
| `edit` `…#Gates` with `replace_section` | Heading and body replaced (`## Gateways`); outline updated |
| `edit` `…#Deep` with `replace_section` and empty `new_text` | Section removed; the message names the full address `#Größe & Überblick#C\\\# tips#Deep` |
| `edit` `…#Last` with `replace_section` and text without a heading | Accepted; the heading is gone and the text joins the previous section (see observation 2) |
| `edit` with `old_text` and `replace_section` | `give either old_text or replace_section, not both` |
| `write` with a section in the path | Rejected, pointing to `edit` with `replace_section` |

### Phase 3 remarks

| Remark | Result |
|---|---|
| 1. Missing required parameter | Fixed: `edit: version is required; get it from 'cat <path>' (it comes as a separate block)`; likewise `path is required; an absolute path like /Work/plan.md` and `write: content is required; the note's full Markdown text` |
| 2. Trash entries at one path | Fixed: `trash` shows version and line count; `restore /Demo/t.md b640e840b19d` brought back the older one; restoring the other while a note exists is refused with what to do |
| 4. Repeated tokens in a history | Fixed: `help history` explains it |

## Observations from the agent's side

The section model is easy to pick up: `outline` once, then `cat` or `edit` with the address, and every mistake gets a message that names the fix. Section edits save a lot of context on long notes. Nothing blocks the next step.

1. **A Markdown-escaped `#` in a heading is a quoting trap.** For `# Q\#A` (rendered `Q#A` in Obsidian) the outline shows `Q\#A` and suggests double quotes, but the only working address is `'…#Q\\\#A'` in single quotes. An agent copying from the outline fails twice and has to guess. Two options: match headings by their rendered text (Markdown escapes removed), so `#Q\#A` works like `#C\# tips` does; or let `outline` print a ready-to-copy address for headings that need escaping. The first is simpler for agents and matches what Obsidian shows. Rare in real notes, so low priority.
2. **`replace_section` with text that has no heading removes the heading silently.** The text then belongs to the previous section and the old address is gone. That can be wanted (merging sections, deleting a heading), but it can also be an agent forgetting to repeat the heading. Mention it in the result, e.g. `replaced section #Last …; the new text has no heading, so it now belongs to #Größe & Überblick`.
3. **Section address with a space but no quotes**: `cat /Demo/long.md#Part 799` gives `cat: too many arguments`, as before for paths. When an argument after the first starts like the rest of a heading, a hint `(quote paths and sections with spaces)` would save a round trip. Nice to have.
4. **Upgrade order in the README.** Line 58 should spell out the order: `git pull`, `docker compose up --build -d`, then a new client session. Without the rebuild a restarted client loads the old server's tool descriptions, which looks like the new version but isn't.

## Suggested next step

Observations 1 to 4 are small; 2 and 4 are the useful ones. Then build order step 4b: links, backlinks, link normaliser and `mv`, with link rewriting off for renames coming from the Obsidian plugin ([obsidian-plugin-design-spec.md](obsidian-plugin-design-spec.md) §3).
