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

## Agent behaviour test

**Not yet run.** A blind run through a subagent of this session was blocked by Claude Code's auto mode (the task includes a delete). To be run as in phase 4a: a fresh Claude Code session, not resumed.

**Fixture** (vault `Technik`, local, written in this session): the log from phase 4a, plus `/Technik/Index.md`, `/Technik/Projekte/Photovoltaik Halle 2.md` (frontmatter link, heading link with alias), `/Technik/Besprechungen/2026-10-20 Jour fixe.md` (embed of a section by vault path, alias link), `/Technik/Archiv/2025/Wärmepumpe Standort Süd.md` (a similar name, links to Nord) and `/Technik/Vorlagen/Projektnotiz.md` (the link only inside a code block, so it must stay untouched). `backlinks` on the log lists 6 lines in 4 notes.

**Prompt:**

> Das Wärmepumpen-Projekt am Standort Nord ist so gut wie abgeschlossen. Bitte verschieb die Notiz dazu in mein Archiv für 2026 und benenne sie dabei in „Wärmepumpe Nord (Halle 3)" um. Es soll dabei nichts kaputtgehen. Sag mir danach, welche Notizen auf das Projekt verweisen.
>
> Außerdem brauche ich die Notiz zur Photovoltaik in Halle 2 nicht mehr – lösch sie bitte.

**What to watch for:** whether the agent finds `mv` and trusts it instead of `write` plus `rm` plus hand-made link edits; whether it answers the question with `backlinks` (and after the move, on the new path); whether it calls `backlinks` before `rm` on the PV note and mentions that `Jour fixe` links to it (and that the PV note links to the heat pump); whether the code block in the template stays untouched.

## Suggested next step

Run the agent behaviour test. Then build the decided observations 4 (link form) and 11 (seeder only fills an empty vault), and observations 1, 2, 3 and 6, which are small, before step 5; 7 to 9 can follow when they come up. Then build order step 5 (encryption).
