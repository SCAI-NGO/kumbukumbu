# Test notes: phase 4b (links, backlinks, mv)

Date: 2026-10-23. Hands-on test of build order step 4b (PR #5, merged as `b1e1747`): `links`, `backlinks`, `mv` with link rewriting, `mv --keep-links`, and the phase 4a feedback. Setup as in [phase 1](notes-test-phase1.md): stack on `127.0.0.1:8083`, image rebuilt after the merge, sample vaults `Demo` and `Ideas` plus the local vault `Technik` from phase 4a. `dotnet test` (run by Chris): 552 passed.

This time the session started after the rebuild, so its own tool descriptions were already phase 4b; no JSON-RPC workaround was needed.

## Results

A test folder `/Demo/lt/` held a hub note with every link form: frontmatter link, plain, `#Heading`, `#^block`, alias, embed, two attachments, upper case, vault path, `./`, `../`, an ambiguous name (`dup` in `/Demo/lt/sub/` and `/Demo/other/`), a missing target, `[[#Hub]]`, an umlaut target, `\|` in a table, inline code and a fenced block. A second note added a decomposed (NFD) `ö`, a link to a missing heading, and broken forms (`[[ ]]`, `[[]]`, `[[a[b]]]`, `[[|x]]`, `[[ alpha ]]`).

### links and backlinks

| Call | Result |
|---|---|
| `links /Demo/welcome.md` | Both links resolved, `[[kumbu]]` to `/Demo/projects/kumbu.md` by path suffix |
| `links /Ideas/retrofuturism.md` | `[[welcome]] → not found`: correct, no resolution across vaults |
| `links` on the hub note | All 18 links correct: attachments as `attachment (not a note)`, `[[dup]]` as `ambiguous:` with both candidates, `[[#Hub]]` as `this note`, `\|` in the table handled, code ignored, frontmatter counted |
| `[[dup]]` from `/Demo/lt/sub/gamma.md` | Resolves to the note in its own folder, as designed |
| NFD `[[Größe]]` | Resolves to the NFC note; stored bytes left as written (checked in Mongo) |
| `[[]]`, `[[a[b]]]` | Ignored |
| `[[ ]]`, `[[\|x]]` | `this note` (see observation 10) |
| `[[alpha#Nope]]` | Resolves to the note; the missing heading is not reported (observation 9) |
| `links … \| rg "not found\|ambiguous"` | Works, as the help suggests |
| `backlinks /Demo/lt/alpha.md` | 5 lines in `path:line:text` form, frontmatter line included |
| `backlinks` of a note only reached by an ambiguous link | Empty output, only the version block (observation 8) |
| `links "/Demo/lt/hub.md#Section"` | `No such file or directory` (observation 6) |
| `links /Demo`, `backlinks /Demo/lt` | `Is a directory` (observation 7) |
| `links` without an argument | `missing operand` |

### mv

| Call | Result |
|---|---|
| `mv /Demo/lt/beta.md /Demo/lt/kumbu.md` (name collision) | 4 links rewritten: the hub's `![[beta]]` and `[[beta#Part 2\|part two]]` to `lt/kumbu…`, and `[[kumbu]]` in `welcome.md` and `Meeting Notes.md` to `[[projects/kumbu]]`, because the move would have made them ambiguous. Exactly right |
| `mv /Demo/lt/alpha.md /Demo/lt/archive/` | Directory target, name kept; only the relative `[[../alpha]]` in gamma was rewritten, since name links still resolve |
| `mv …/archive/alpha.md …/archive/omega.md` | 10 links in 3 notes rewritten; heading, block, alias, embed `!`, `\|` in the table and the frontmatter quotes kept; the rest of each note byte for byte, NFD bytes included |
| `mv …/omega.md /Demo/lt/alpha.md` (back) | 10 links rewritten again, now as `[[lt/alpha…]]`, not the original `[[alpha…]]` (observation 4) |
| `mv /Demo/lt/hub.md /Demo/lt/sub` (existing directory without `/`) | Moved into it; the moved note's own relative links (`./sub/gamma`, `../welcome`) rewritten to `lt/sub/gamma` and `welcome` |
| Same move, `[[dup]]` in the moved note | Was ambiguous, now resolves to `/Demo/lt/sub/dup.md` without a word (observation 5) |
| `mv --keep-links …/kumbu.md …/beta.md` | `links left as they are (--keep-links)`; the hub's links to it now `not found`, as expected |
| `mv … --keep-links …` (option after the path) | Treated as a path, `No such file or directory` for the source. Fine, the help shows the option first |
| Target exists | `already exists; rm it first, or choose another name` |
| Other vault | `notes can't move between vaults (Demo → Ideas); a vault is its own space` |
| `.txt` target | `note names end in .md` |
| Same path | `… are the same note` |
| Section in the source | `moves whole notes; to move a section, edit both notes` |
| Directory as source | `Is a directory (mv moves one note at a time)` |
| One argument | `missing operand (mv <path> <target>)` |
| Name with umlaut and parentheses | Accepted |
| `history` of the moved note | Moved with it |
| `edit` on the old path with the old version | `No such file or directory (if it was removed, 'trash' lists it)` (observation 1) |
| `edit` on the new path with a version from before the link rewrite | Usual conflict message with the new version |
| `mv "/Demo/Meeting Notes.md" /Demo/archive/`, then `docker compose restart kumbu` | Known limitation confirmed: the seeder brings back `/Demo/Meeting Notes.md` (observation 11) |

### Phase 4a feedback

| Item | Result |
|---|---|
| Long-note hints in the `sh` description | Present: `A whole note longer than that prints its outline.` and `Long notes: 'wc -c' or 'outline' first …` |
| `cat` of the 862-line note alone | Returns its outline (123 lines, about 9,500 characters instead of about 8,000 tokens of the beginning) and the footer `note too long for one call (~13k tokens); its outline is above. Read a part: cat "…#Section", head -n 40 "…", rg -n … "…"`; the version block as usual |
| `cat` of the same note in a pipeline (`\| tail -n 3`) | Passes the full note on: the last three lines of the note |
| README upgrade order | In the README (commit `50d7ca2`) |

## Observations from the agent's side

`links` and `backlinks` are exactly what was missing since phase 1: one call instead of `rg --files | rg -i name`, and the `rg`-style output of `backlinks` reads at once. `mv` did the right thing in every case I tried, including the name collision, which I would not have thought of checking myself as an agent. The error messages all name the fix. Nothing blocks the next step.

Ordered by usefulness:

1. **The old path forgets the move.** After `mv`, the old path says `No such file or directory (if it was removed, 'trash' lists it)`, but the trash is empty. A second session, or the same one after context compaction, holding the old path from a note or a summary has no way to learn where the note went, short of guessing a name for `rg --files`. A short-lived move record (like the trash, e.g. 30 days) would let the error say `moved to /Demo/lt/sub/hub.md on 2026-10-23 (mv)`. `history` of the new note could show the move as well.
2. **`rm` says nothing about links that now point nowhere.** After `rm /Demo/lt/alpha.md` nine links were dangling, and since the note is gone, `backlinks` can no longer be asked. Cheap fix: `removed …; 9 links in 3 notes now lead nowhere (restore brings them back): /Demo/lt/hub.md, …`. An agent that checks `backlinks` before `rm` gets this anyway, but the blind test below is meant to show whether it does.
3. **Link rewrites coalesce into the writer's own recent version.** `history /Demo/lt/hub.md` showed a single version after two `mv`s: the rewrites merged with my `write` from minutes before (same writer within 10 minutes). The rewrite can't be undone on its own then, and nothing in the history says that a version came from a `mv`. Suggest: a rewrite by `mv` always starts its own history entry, with a marker such as `dev (mv)`.
4. **Design question: which link form `mv` writes.** The most specific form (`[[lt/archive/omega]]`) is robust, but it makes notes noisy, and a move back doesn't restore the original: `alpha → omega → alpha` leaves `[[lt/alpha]]` where `[[alpha]]` stood. Options:
   - (a) Keep the vault path (as now). Immune to later collisions from new notes created with `write`, which rewrites no links.
   - (b) The shortest form that resolves uniquely, like Obsidian's default ("shortest path when possible"). Readable, and a move back restores the original. Every later `mv` still checks every link, but a new note created with `write` could make such a link ambiguous; `links` would show it as `ambiguous`, not wrong.
   - (c) Change as little as possible: replace only the part of the link that names the moved note, keep the form the author chose if it stays unique, otherwise the vault path.

   **Decided (Chris, 2026-10-23): (c), falling back to (a).** It keeps the author's style, a move back restores the original, and it is never ambiguous at the time of the move. In detail:
   - Keep the form: a bare name stays a bare name (`[[alpha]]` → `[[omega]]`), a link with a path gets the new path (`[[projects/kumbu]]` → `[[archive/kumbu]]`). Case of the old link doesn't need to survive a rename.
   - Heading, block, alias, `\|` and the `!` of embeds stay, as now.
   - Relative links (`./`, `../`) and every link whose kept form would not resolve uniquely to the moved note after the move fall back to the vault path.
   - Remaining risk, accepted: a note created later with `write` can make a bare-name link ambiguous, or take it over if it lies in the linking note's folder. Hand-written links have the same risk today; the vault path only protected the links `mv` had rewritten.
5. **`mv` can turn an ambiguous link into a resolved one without saying so.** Moving the hub into `/Demo/lt/sub/` made its `[[dup]]` resolve to the `dup` in the new folder. There is no old target to rewrite it to, so leaving it is right, but the output could say `1 link was ambiguous and now leads to /Demo/lt/sub/dup.md`. Rare, low priority.
6. **`links note.md#Section` says `No such file or directory`.** `outline` with a section says `give the note without a section`; `links` should do the same. Better: support it, since on a long note (the 862-line log) the links of one section are what an agent wants.
7. **No vault-wide check for broken links.** After `mv --keep-links`, `rm` or a heading change, finding dangling links takes one `links` call per note, and there is no `xargs`. `links /Demo` (a directory) could print every link that is `not found` or `ambiguous` as `path:line: [[link]] → not found`. Useful for the Obsidian plugin case too.
8. **`backlinks` with no result prints nothing.** Only the version block comes back, which looks like an error to a reader. `(no notes link here)` would be clearer; listing ambiguous links that might mean this note (`/Demo/lt/hub.md:10: [[dup]] (ambiguous)`) would complete the picture.
9. **Heading targets aren't checked.** `[[alpha#Nope]]` shows as `→ /Demo/lt/alpha.md`. `links` could add `(no heading #Nope)`. More important later: renaming a heading with `edit` or `replace_section` silently breaks `[[note#Heading]]` links elsewhere; the `edit` result could mention backlinks to the old heading.
10. **`[[ ]]` and `[[|x]]` count as `this note`.** Obsidian doesn't treat them as links. Cosmetic.
11. **Seeder and `mv`.** Confirmed as documented, with a side effect that isn't documented: the resurrected copy lies in the old folder, so links from that folder (`[[Meeting Notes]]` in `welcome.md`) now quietly lead to the stale seed copy, and the moved note loses its backlinks. The moved copy was put in the trash afterwards, so the reseeded original is the only one again.

    **Decided (Chris, 2026-10-23): the seeder fills only an empty vault.** The seeder is a development aid that will be removed in a few days, not a test bed for future Obsidian syncs; seeding means "start everything from scratch". A vault that already holds notes (or trash) is left alone, so moved, edited and removed notes stay as they are. Adding sample files later then needs `docker compose down -v`. The update and conflict logic of `SeedDecision` can go.

    The underlying question (a note *is* its path) comes back with the Obsidian plugin: a client that was offline uploads a file at a path the server has moved since. The move record from observation 1 would let the server answer `moved to …` there too; worth checking against the [plugin spec](obsidian-plugin-design-spec.md) §3.
12. **The main output ends without a line break.** Claude Code joins the result blocks, so the output reads `… /Demo/welcome.mdversion of /Demo/lt/kumbu.md: …`. Checked with raw `tools/call`: the server sends two separate blocks; the client joins them. A trailing `\n` on the main text would avoid this. Cosmetic.

13. **Coalescing merges different sessions, and can swallow a restore.** Found while resetting the fixture between the runs. `/Technik/Index.md`, as stored:

    | Time (UTC) | Write | Effect on the history |
    |---|---|---|
    | 16:26 | created (`0d3d…`) by this session | |
    | 16:45 | run 1's `mv` rewrites its link (`09be…`) | `0d3d@16:26` goes to the history |
    | 16:48 | this session's `restore … 0d3d…` | `09be@16:45` goes to the history |
    | 16:51 | run 2's `mv` rewrites its link (`09be…`) | coalesced with the restore (same writer `dev`, within 10 minutes): the restored state of 16:48 is gone |

    `history` then lists `09be26e54d64` twice in a row (16:51 current, 16:45). Nothing was lost here, since the original is still in the history, but two things show:
    - Every session writes as `dev`, because there is one shared token. Coalescing, meant to fold one agent's burst of edits into one version, folds *different* agents' work together: this session's restore and the test session's `mv`.
    - A `restore` is a deliberate checkpoint; a later write by the same writer should not replace it.

    Suggest: a `restore` (like a `mv` rewrite, observation 3) is never coalesced into, and an entry identical to the one before it isn't stored twice. Separating writers properly needs per-client tokens or an MCP session id as writer; worth noting for step 5 or later.

## Agent behaviour test

A blind run through a subagent of this session was blocked by Claude Code's auto mode (the task includes a delete). Run 1 was then done in a fresh Claude Code session; see the end of this section.

**Fixture** (vault `Technik`, local, written in this session): the log from phase 4a, plus `/Technik/Index.md`, `/Technik/Projekte/Photovoltaik Halle 2.md` (frontmatter link, heading link with alias), `/Technik/Besprechungen/2026-10-20 Jour fixe.md` (embed of a section by vault path, alias link), `/Technik/Archiv/2025/Wärmepumpe Standort Süd.md` (a similar name, links to Nord) and `/Technik/Vorlagen/Projektnotiz.md` (the link only inside a code block, so it must stay untouched). `backlinks` on the log lists 6 lines in 4 notes.

**Prompt:**

> Das Wärmepumpen-Projekt am Standort Nord ist so gut wie abgeschlossen. Bitte verschieb die Notiz dazu in mein Archiv für 2026 und benenne sie dabei in „Wärmepumpe Nord (Halle 3)" um. Es soll dabei nichts kaputtgehen. Sag mir danach, welche Notizen auf das Projekt verweisen.
>
> Außerdem brauche ich die Notiz zur Photovoltaik in Halle 2 nicht mehr – lösch sie bitte.

**What to watch for:** whether the agent finds `mv` and trusts it instead of `write` plus `rm` plus hand-made link edits; whether it answers the question with `backlinks` (and after the move, on the new path); whether it calls `backlinks` before `rm` on the PV note and mentions that `Jour fixe` links to it (and that the PV note links to the heat pump); whether the code block in the template stays untouched.

### Run 1: not blind

**Setup went wrong twice.** The session started in `/home/chris/workspace`, but the Kumbu MCP server is configured for the project `/home/chris/workspace/kumbukumbu` only (`~/.claude.json`), so the session had no Kumbu tools. It found the repo, read the dev token from `.env` (without printing it), read `tools/list` and called `sh` through a small curl script. On the way, its third call was `rg -n -i 'wärmepumpe|photovoltaik|archiv' docs/notes-test-phase4b.md`, which showed it the fixture description (including that the code block must stay untouched), the prompt, and the `mv` rows of the results table. It did not see "What to watch for". So it knew beforehand that `mv` rewrites links, which was the main question.

**Kumbu calls, in order** (each batch through the script; from the session transcript):

1. `rg --files /Technik`, `rg --files | rg -i "wärme|photovolt|archiv"`, `help mv`, `backlinks` on the heat pump note, `backlinks` on the PV note
2. `cat` of the template, the index, the jour fixe and the PV note; `ls /Technik/Archiv`
3. `mv "/Technik/Projekte/Wärmepumpe Standort Nord.md" "/Technik/Archiv/2026/Wärmepumpe Nord (Halle 3).md"`
4. `backlinks` on the new path, `rg -n "Standort Nord" /Technik`, `links` on the jour fixe and the PV note, `outline` of the moved note
5. `rm "/Technik/Projekte/Photovoltaik Halle 2.md"`, `trash /Technik`, `rg -n "Photovoltaik Halle 2" /Technik`, `links /Technik/Index.md`

**Result.** Correct: one `mv`, the missing `Archiv/2026/` created by it, 6 links in 4 notes rewritten with embed, heading and alias kept, the template's code block untouched, the PV note in the trash. The answer listed all six linking lines, named the long link form, and reported the two links left dangling by the `rm` (index and jour fixe). It did not tidy the index on its own but asked: remove the PV line? move the heat pump to "Abgeschlossen" now or later?

**What still counts, despite the contamination:**

- `backlinks` on **both** notes before changing anything, then reading every linking note: the order the tools are meant for.
- Moving before deleting, with its own reason: the PV note, if restored, then already holds the corrected links.
- After the move, `rg -n "Standort Nord"` to find mentions that aren't links. A plain-text mention is something `mv` can't know about; the agent thought of it.
- After `rm` it had to search for the dangling links itself with `rg`: confirms observation 2.
- Asking instead of editing the index: good judgement, not a tool question.
- Side finding: an agent without the MCP tools but with a shell finds the token in `.env` and talks to the server directly. Expected for a dev setup with a shared token on localhost; one more reason for per-client tokens later.

### Run 2: blind

**Setup.** Fixture reset: the four linking notes restored to their versions before run 1, the note moved back with `mv --keep-links`, the PV note restored from the trash and to its first version; `backlinks` gave the same 6 lines as before run 1. A fresh session in an empty directory outside the repo, started with `claude --mcp-config <file> --strict-mcp-config` (only the Kumbu server, tools loaded natively). Same prompt. The session touched nothing but the Kumbu tools.

**Tool calls, in order** (from the transcript):

1. `ToolSearch` for the three Kumbu tools
2. `rg -i -l "wärmepumpe|photovoltaik|archiv"`: all candidate notes in one call
3. `ls`, `ls /Technik/Archiv/`: found `2025/` only
4. `cat` of the heat pump log: the note is too long, so it got the outline (about 9,600 characters instead of 8,000 tokens of the beginning; the phase 4a decision paying off)
5. `backlinks` on the heat pump note, `cat` of the PV note, `backlinks` on the PV note
6. `rg -n -i "Standort Nord|WP Nord|Halle 3|Photovoltaik Halle 2"`: mentions that aren't links, and whether the new name is taken
7. `cat` of the jour fixe, `head -n 10` of the log (its frontmatter)
8. `help mv`, then one `mv` to `/Technik/Archiv/2026/Wärmepumpe Nord (Halle 3).md`
9. `backlinks` on the new path, `rg -n "Standort Nord" /Technik`, `cat` of the template
10. **`AskUserQuestion` before the `rm`**: the PV note is still linked from the index and the jour fixe, and by its own content the tender is still running. Options: delete and defuse the links (recommended: drop the index line, turn the jour fixe link into plain text), only delete, don't delete. Chris answered "only delete".
11. `rm` of the PV note

About 16,000 characters of tool output for the whole task, the outline being the largest part.

**Result.** Correct and minimal, the same end state as run 1: one `mv`, 6 links in 4 notes rewritten, the code block untouched, the PV note in the trash. The answer listed the four remaining linking lines, said that the PV note's two links went with it, named the template's code block as "not a real link, left as it was", and listed the two links now dangling. It also pointed out what the move left stale in content: the archived note still says `status: in Umsetzung` and its title still has the old name, the index still says "Inbetriebnahme Ende November", and the log has entries after the test day, so it may still be in use.

**Findings.**

- Without knowing the design, the agent went `backlinks` → read → `help mv` → `mv` → `backlinks` on the new path. It trusted `mv` with the links and checked the result instead of editing links by hand.
- It used `backlinks` before deleting, unasked, and turned what it found into a question to the user. Observation 2 (`rm` reports dangling links) is still worth doing for agents that don't check first, but this one did.
- Its recommended option 1 contradicts itself: turning the jour fixe link into plain text is meant to leave the protocol unchanged, but it changes it. A design question about agent judgement, not about the tools. The edit path (remove an index line, unlink a link) is to be tested separately.
- Unlike run 1, the answer didn't say that the links now have the long form with the path. With the decided link form (observation 4) this matters less, since bare names stay bare.
- `rg -n` for the plain name after the move found the frontmatter field `projekt: Wärmepumpe Standort Nord` and the old title: text that `mv` rightly leaves alone, but that a user may want to change. The agent offered it instead of doing it.

## Suggested next step

Test the edit path separately: the same fixture and prompt, answering option 1 (delete and defuse the links). Then build the decided observations 4 (link form) and 11 (seeder only fills an empty vault), and observations 1, 2, 3, 6 and 13, which are small, before step 5; 7 to 9 can follow when they come up. Then build order step 5 (encryption).
