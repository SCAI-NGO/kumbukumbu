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

5. **The first `cat` of a long note is mostly wasted** (see the agent behaviour test below). An agent can't know a note's length before reading it, so it `cat`s the whole note and gets about 8,000 tokens of its beginning. Proposal for discussion: when a note exceeds the limit, `cat` returns a short beginning (for example 40 lines) followed by the note's top-level outline with line ranges, and the usual footer. The agent can jump straight to the right section, and the call costs a fraction of the tokens. Trade-off: in newest-first logs the beginning itself is often what was wanted, so the beginning should stay, just shorter. Needs a decision from Chris. The tested session itself suggested the stronger variant (outline only, no beginning).
6. **Say it up front in the tool description.** The truncation footer teaches `outline` only after the tokens are spent. One line in the `sh` description, next to the output limit, would teach it before: `Long notes: 'wc -c' or 'outline' first, then cat "note.md#Section" or rg -n.` Cheap, independent of observation 5, and suggested by the tested session itself.

## Agent behaviour test

Question from the earlier web session: does Claude use `outline` and section reads on long notes on its own, without being told?

**Setup.** A realistic project log, `/Technik/Projekte/Wärmepumpe Standort Nord.md` (vault `Technik`, local only, not committed): German, YAML frontmatter, newest entries first, months as `##`, entries as `###`, 862 lines, 41,205 characters, so a plain `cat` is truncated around line 500. The decision asked about (`### 2026-03-12 Entscheidung Wärmepumpe`, start-up planned for 2026-11-12) is at line 647, beyond the cut. Distractors: "Wärmepumpe" 81 times, "Inbetriebnahme" 13 times (several "Termin Inbetriebnahme unverändert"), a second decision in June (buffer tank).

A **fresh** Claude Code session (not resumed, so without knowledge of the design) got the phase 4a tool descriptions and this prompt, with no hint about outlines or sections:

> In meinem Kumbu-Gedächtnis gibt es ein Projekt-Logbuch zur Wärmepumpe am Standort Nord. Was wurde dort zur Wärmepumpe entschieden, und wann soll sie in Betrieb gehen? Trag bitte außerdem ein, dass sich die Inbetriebnahme auf den 26.11.2026 verschiebt, weil die Statik-Freigabe fürs Fundament noch fehlt.

**Tool calls, in order.**

1. `rg -il "wärmepumpe"`: found the note in one call.
2. `cat "/Technik/Projekte/Wärmepumpe Standort Nord.md"`: whole note, truncated (the one wasted call, about 8,000 tokens).
3. `rg -n -i "entscheid|inbetriebnahme|beschlossen|festgelegt|verschoben|verschiebt" "…"` and `outline "…"` in parallel: followed the footer's hint and narrowed it down. (The pasted log first showed only the `rg` call; the session's own review below names both.)
4. `cat "…#2026-03-12 Entscheidung Wärmepumpe"`: read the section, building the address from the `rg` hit (about 160 tokens).
5. `edit` with path `…#Oktober 2026`, `old_text: "### 2026-10-20 Rücksprache (Murat)"` and the new entry plus that heading as `new_text`: inserted `### 2026-10-22 Terminverschiebung Inbetriebnahme` with old date, new date and reason.

**Result.** Correct and minimal: 5 new lines, no `write` of the whole note, the March decision left untouched, history `seed` → `dev`. The log's convention (newest first, one `###` entry per date) was kept. The fixture accidentally contained entries dated after the test day (25, 26 and 28 October); the agent still placed its entry chronologically between 25 and 20 October instead of blindly at the top.

**The tested session's own review** (after the test was explained to it; summarised from its answer, in German):

| # | Call | Its verdict |
|---|---|---|
| 1 | `rg -il "wärmepumpe"` | Good: one hit, note found at once |
| 2 | `cat` of the whole note | Waste: about 14k tokens, cut at line 508, and the March decision lay behind the cut |
| 3 | `rg -n` and `outline` in parallel | Should have come right after step 1; `rg` for "Entscheidung" and "Inbetriebnahme" found line 647 at once |
| 4 | `cat …#2026-03-12 Entscheidung Wärmepumpe` | Good: exactly the section, about 160 tokens |
| 5 | `edit` on `#Oktober 2026` | Good: section-scoped, placed the entry correctly |

Its ideal path: `rg -il` → `outline` (or `rg -n`) → `cat #section` → `edit`, about 2k instead of 16k tokens. It named as working well: section addresses with dates in headings, the version token that `outline` returns too, and `old_text` only having to be unique within the section. It also spotted the distractors: the entries dated after the test day, the "unverändert" of 28 October, and the foundation approval that had been open for months. (The future-dated entries were an accident of the fixture generator, not intended as bait.)

Its proposals: (a) above a certain size, `cat` without a section returns the outline first instead of the content (a stronger variant of observation 5); (b) the `sh` description recommends "outline first, then `cat` the section" for long notes (observation 6).

**Findings.**

- Without any hint the agent used `rg` and `outline` together after the truncated `cat`, and built the section address from the `rg` hit. With a concrete question, `rg` plus a section address is the shortest path; `outline` helps to see the structure. Both should stay well supported.
- The truncation footer works as a teacher, but only after the tokens are spent: the full `cat` cost about 14k of the roughly 16k tokens of the whole task.
- Section-scoped `edit` with an `old_text` anchor is what an agent reaches for to insert into a log; it worked first time.
- The agent's own review matches the outside view, which makes it a useful part of this kind of test.

## Suggested next step

Observations 1 to 4 and 6 are small; 2, 4 and 6 are the useful ones. Observation 5 needs a decision first. Then build order step 4b: links, backlinks, link normaliser and `mv`, with link rewriting off for renames coming from the Obsidian plugin ([obsidian-plugin-design-spec.md](obsidian-plugin-design-spec.md) §3).
