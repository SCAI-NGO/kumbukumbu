# Test notes: phase 6 (agent tools for larger changes)

Date: 2026-10-24. Hands-on test of build order step 6 (PR #8, merged as `c8cc7cb`): `--version` on reading commands, `diff`, `history` on folders, `edit replace_all`, the `replace` tool with plan tokens, `mv` of whole folders, and every write as a MongoDB transaction on a single-node replica set. Setup as in [phase 1](notes-test-phase1.md): stack on `127.0.0.1:8083`, rebuilt by Chris with `docker compose up --build -d`, sample vaults `Demo`, `Ideas` and the local vault `Technik`. `dotnet test`: 660 passed.

Fixture in `/Demo/p6/` (later `/Demo/q7/`): `alpha.md` (mentions `Hecate`, `Hecates-Proxy`, lower-case `hecate`; links `[[beta]]` and the broken `[[p6/gamma]]`), `beta.md` (`Hecate` in prose and in a code block, `[[alpha]]`), `sub/gamma.md` (`[[alpha]]`), plus `/Demo/outside.md` linking into the folder in four forms (`[[p6/beta]]`, `[[p6/sub/gamma|Gamma]]`, `[[gamma#Gamma]]`, `[[/p6/alpha]]`).

The live `tools/list` was read after reconnecting: `sh`, `write`, `edit`, `replace`, and the descriptions match `Manual.cs`.

## Results

### `diff`, `history` and `--version`

| Check | Result |
|---|---|
| `diff` on a note with no earlier version | `diff: /Demo/p6/alpha.md has no earlier version ('history /Demo/p6/alpha.md')` |
| `diff <note>` after a `replace` | Unified diff, git layout, header names path, version, time and writer (`dev (replace)`) on each side |
| `diff --word` | Changed lines marked `~`, words as `[-Hecate-]{+Janus+}` |
| `diff <note> V1 V2` (newer first) | Works in either order |
| `diff <note> <other note>` | Works; header names both paths |
| `cat`, `cat …#Notes`, `wc -w`, `links` with `--version`; `cat --version V note \| rg -c` | Earlier text, section addressing and pipes all work on the old version |
| `outline --version deadbeef0000` | `has no version deadbeef0000 ('history …' lists them)` |
| `rg … <folder> --version`, `ls --version` | Refused with the reason (`names a version of one note`) |
| `history /Demo/p6`, `history / --since 30m`, `--since 2026-10-24T11:03` | Newest first, with writer, version and `+added -removed`; the UTC time works |
| `history --since yesterday` | `--since takes 30m, 2h, 1d or a UTC time like 2026-10-23 14:00 (got 'yesterday')` |
| History after a folder `mv` | Travels with each note |

### `edit replace_all`

`replace_all` with the section path `#Notes` changed all three `line` in that section and nothing outside it: `replaced 3 occurrences in section #Alpha#Notes`.

### `replace`

| Case | Result |
|---|---|
| Preview, `word: true`, `Hecate` → `Janus` | 5 occurrences in 2 notes, counts per note, inline excerpts; `Hecates-Proxy` and lower-case `hecate` left out. The code block occurrence is included and visible in the preview |
| Apply after one of the notes changed | `nothing was replaced: /Demo/p6/beta.md changed since the preview; preview again`; the other note untouched |
| Apply with the right token but `word: false` | `this plan was made for a call with another path, old_text, new_text or word`; the plan stays usable |
| Apply the same plan twice | Second call: `unknown or expired plan` (observation 4) |
| A plan that failed once | Consumed; the next try says `unknown or expired` too |
| Without `word`, `the` in `/Demo` | 12 occurrences in 7 notes including `there` and `then`, all visible in the excerpts; excerpts cover the first 5 notes only (observation 8) |
| Same text, empty `old_text`, no match, missing path, `/` | `are the same`, `old_text is empty`, `no occurrences of "Zeus" below /Demo/q7`, `No such file or directory`, `works within one vault` |
| `alpha` → `omega` (word) | Preview proposes changing `[[alpha]]` to `[[omega]]` in two notes, without a word about links (observation 1). Applied at the end, followed by `mv` of the note |
| Two plans on the same note, applied one after the other | First wins, second `changed since the preview` |
| **Real race:** two plans on the same two notes, applied at the same instant by two `curl` processes, 10 rounds (scratchpad script) | Every round exactly one winner (A 5×, B 5×), the loser wrote nothing; afterwards no note held a mix of both |
| Undo of a `replace` | `restore <note> <version>` per note works; `diff` then shows the way back, the writer is `dev (restore)` |

### `mv` of a folder

| Case | Result |
|---|---|
| `mv /Demo/p6/ /Demo/q7/` (rename) | `moved … (3 notes); rewrote 3 links in /Demo/outside.md`. The author's form was kept: `[[q7/beta]]`, `[[q7/sub/gamma\|Gamma]]`, `[[/q7/alpha]]`; `[[gamma#Gamma]]` still unique, left alone; the broken `[[p6/gamma]]` inside the folder left as it was |
| Into an existing folder where one target note exists | `/Demo/arch/q7/beta.md already exists`; nothing moved, no link touched (transaction) |
| Into itself (`/Demo/q7/sub/`, `/Demo/q7/`) | `a folder can't move into itself` |
| Without the trailing `/` | `/Demo/q7: Is a directory (to move the folder, write /Demo/q7/)` |
| Into another vault, a vault itself, a missing folder | Each refused with the reason |

### Phase 5 observations 7 and 8

| Observation | Result |
|---|---|
| 7: first access after a repair | Fixed. A note's blob replaced with another note's in MongoDB, server restarted: `cat` and `write` say `damaged: …`, `rg --files` leaves the note out. Original blob put back without restart: the **first** `cat` shows the note |
| 8: empty vault on every start | Fixed. Empty folder `Leer`: first start `Created vault Leer …`, second start `Left vault Leer alone: it is empty, and so is its folder` and `Seeded 0 notes into 0 vaults` |

The stack was wiped and reseeded afterwards. Server log during all of this: no `fail:` or exception apart from the deliberate damage.

Not tested: a transaction aborted by a server crash halfway (would need a kill at the right instant; the unit tests cover the abort path), a `replace` over more notes than fit into the output, history coalescing over real 10-minute windows. The blind agent run follows below.

## Blind run: renaming a project

Date: 2026-10-24, after the tests above, before step 6b. Setup as in the phase 4b notes: a fresh Claude Code session started by Chris in an empty scratchpad directory with `--mcp-config <0600 file> --strict-mcp-config` (only the Kumbu² server; the token file was deleted afterwards), no repository, no CLAUDE.md of the project, no project memory. The run took 7 minutes.

**Fixture** (`/Demo/arbeit/`, 10 notes in German, written through MCP, snapshot kept for a rerun): the project note `projekte/Hecate.md` (frontmatter, sections, named after the Greek goddess), `projekte/Hecate Retrospektive.md` (a retrospective of version 1 that might keep its name), two meeting minutes, two people, a todo list, a log with a `kubectl` command in a code block, `ideen/gates.md` with the genitive `Hecates`, and `ideen/namensideen.md`, a table of mythological names where Hecate is also the goddess. Links in every form: bare `[[Hecate]]`, `[[Hecate#Architektur]]`, `[[Hecate|das Gate-Projekt]]`, `[[projekte/Hecate]]`, `[[Hecate Retrospektive]]` and `[[Hecate Retrospektive#Was nicht gut lief]]`. Identifiers in inline code: `hecate-proxy`, `hecate-rules/`.

**Prompt (Chris, verbatim):** „Das Projekt Hecate heißt jetzt Janus. Bitte aktualisiere meine Notizen."

### What the agent did

1. Explored: `rg -i hecate`, `rg --files | rg -i hecate`, `cat` of the project note, the name table and the log, `rg -i janus` (to see whether the new name was already taken).
2. **Asked two questions before writing anything**, each with a recommendation:
   - Technical identifiers (`hecate-proxy`, `hecate-rules/`, the `kubectl` command): leave them (recommended) or rename them too. The reason given was that they are real names of deployments and folders, which stay until the infrastructure is renamed too. Chris: leave them.
   - Older entries (retrospective, log, both meetings): "links yes, text no" (recommended: links lead to the new note, old minutes keep the name of the time, the retrospective note is renamed with a note "then Hecate"), rename everything, or keep the retrospective's name. Chris: the recommendation.
3. `mv` of both project notes **first**: 11 links rewritten by the server in every form (heading, alias and path kept).
4. `write` of the project note (title, prose, a sentence on the old name, `aliases: [Hecate]` in the frontmatter for Obsidian), then targeted `edit` calls with `old_text` in the retrospective, the todo list, `anna.md`, `gates.md` (`Hecates Regeln` → `Die Regeln von Janus`, grammatically right) and the name table (Hecate "wieder frei", Janus given to `[[Janus]]`).
5. A final `rg -n Hecate` to check what is left, and a summary that lists exactly what was renamed, adapted and left alone on purpose.

**It never used `replace`.** The tool was loaded, but each change needed judgement (current state or historical record, project or goddess), so it worked note by note.

### Result

| Check | Result |
|---|---|
| Links after the run (`links` on all 10 notes) | 16 links, none broken, none ambiguous |
| The goddess in `namensideen.md` | Left alone, as text and in the table's origin column |
| Identifiers in code | Unchanged, as Chris answered |
| Meetings, log, retrospective text | Text unchanged, links updated, as Chris answered. `Anna stellt die Hecate-Regeln … vor, siehe [[Janus#Architektur]]` is the intended mix |
| History | The fixture's state survives in every changed note (e.g. `todo.md`: fixture `dev`, link rewrite `dev (mv)`, edit `dev`): the `dev (mv)` writer breaks the coalescing chain |
| The agent's summary | Accurate; "11 wikilinks" is the exact count |

### What this means for the decisions

- **Observation 1 stands, with lower urgency.** A careful agent did exactly what the warning would recommend (`mv` first, then text). The renaming with judgement made `replace` the wrong tool here, so the warning protects the other case: an agent, or a user's explicit "replace Hecate with Janus everywhere", that does reach for `replace`. A literal `replace` on this fixture would have changed the goddess, `Hecates`, the old minutes and the retrospective's links. The blind run doesn't test that path; a rerun after step 6b with the prompt „Ersetze überall Hecate durch Janus." would.
- **The questions were the best part.** The agent found both traps the fixture was built around, plus the history question, on its own, and asked instead of guessing. Nothing in the manual told it to; the tool output (`rg -i hecate` showing code identifiers and old minutes) was enough.
- **For step 6b, changesets and the dev token:** here the fixture (written as `dev` at 11:35) and the agent's run (as `dev` from 11:41) were 6 minutes apart. Today the `dev (mv)` label keeps them apart; with changesets "whichever tool wrote them", they would be one changeset, and the fixture's state would not be in the history. Until step 8 gives each agent its own key, everything is `dev`. That is right for the design, but tests must leave more than 10 minutes between preparing a fixture and a run (or keep a snapshot, as here).
- Small: `aliases: [Hecate]` makes Obsidian find the note under the old name, in search and link suggestions. Kumbu²'s link resolution ignores aliases (as Obsidian does for `[[Hecate]]`); `rg Hecate` still finds the note through its frontmatter.

## Observations from the agent's side

1. **`replace` rewrites wikilinks without a word.** Renaming a term is the tool's own example ("rename a project everywhere"), and a project usually has a note with that name. `alpha` → `omega` turns `[[alpha]]` into `[[omega]]`, which leads nowhere until the note is moved to `omega.md` too. In either order the result is then right: verified for replace-then-mv, where `backlinks /Demo/q7/omega.md` afterwards lists all three links and `mv` rewrote the path link in `outside.md`. An agent that only replaces, as the description suggests, leaves the links broken. The preview shows it (`[[[-alpha-]{+omega+}]]`), but an agent reads that as intended. Suggest: the preview names occurrences inside `[[…]]` separately, `2 of them are inside [[links]]: after the replace they lead to [[omega]], which doesn't exist; to rename a note, mv it first (that rewrites its links), then replace the rest`. A refusal would be too strong: replacing link text on purpose is legitimate.
   **Decided by Chris (Oct 24, 2026): warn.** The preview resolves every changed link as it would be after the replace and names those that would lead nowhere, with the `mv` hint for links to the note being renamed. A second example shows why this is needed even when the agent moves the note first: renaming Hecate also changes `[[Hecate Retrospektive]]`, a link to another note that keeps its name. The replace itself stays literal; leaving link targets out (an option `replace` would need explaining) was rejected as the default.
2. **A series of `replace` calls pushes the pre-session state out of the history.** Each changed note gets its own history entry marked `dev (replace)` that never coalesces (by design, spec "agent tools" line 81, so each replace can be undone on its own). After the race (20 replaces on `alpha.md`) its history held only the replace states. The original version `11b017bb178f` and the agent's own edits were gone. Ten renames in a row on one glossary note have the same effect, and concept §4 promises "state before this agent session is always recoverable". Options: (a) replaces by the same writer coalesce with each other within the window, but not with other writes (so the pre-replace state survives, and the single replaces of one session can't be undone one by one); (b) the history always keeps the oldest entry of the current window as a pinned baseline; (c) a larger limit for checkpoint entries. I'd take (a): undoing "everything this session replaced" is the common case.
   **Decided by Chris (Oct 24, 2026): changesets.** Writes close together in time form a **changeset**: everything one writer writes within the window (10 minutes since its last write) is one changeset and gives one version per note, whichever tool wrote it. `replace` loses its exception, so a series of replaces coalesces like any other writes and the state before the changeset survives. Each history entry also records the id of the changeset it belongs to; that costs little now and lets a later feature group or undo a whole changeset across notes (observation 3). Also decided: a changeset lasts **1 hour** at most, and `restore` stays a checkpoint that never joins a changeset. Named checkpoints (`mkcp`, `rmcp`) came up as a related idea; see concept §4. The word is *changeset*, not transaction: in Kumbu² a transaction means MongoDB's all-or-nothing (CONCEPTS chapter 7).
3. **Undoing a `replace` is one `restore` per note.** The agent has to collect the versions from `history` itself. A `replace` that changed 30 notes is the case where that hurts. Idea for later: `history` on a folder could group the rows of one replace (they share a plan token), and `restore` could take that token. Not needed now; the changeset ids from observation 2 are the groundwork (undo a changeset across notes, with a preview and plan token, so a later human edit isn't overwritten).
4. **A used plan looks like an unknown one.** After a successful apply the same call says `unknown or expired plan`. An agent whose first apply timed out on the client side can't tell whether anything was written and may preview and apply again, which is harmless here only because the second preview finds nothing. Suggest remembering used tokens for the plan lifetime: `plan 09a8ee9677d8 was already applied at 11:03 (5 occurrences in 2 notes)`, and after a failed apply `plan … failed: … changed since the preview; preview again`.
5. **`write` can make links ambiguous; `mv` protects against that, `write` doesn't.** Creating `/Demo/arch/q7/beta.md` made `[[q7/beta]]` in `/Demo/outside.md` ambiguous (`links` reports both candidates). That's consistent with "resolve at read time" and with Obsidian. A hint in the write result would cost little and rewrite nothing: `note: [[q7/beta]] in /Demo/outside.md is now ambiguous (2 notes end with q7/beta)`.
6. **Folder history doesn't show moves.** After `mv /Demo/p6/ /Demo/q7/`, `history /Demo` lists the notes under their new paths, as if they had always been there; only the link rewrite (`dev (mv)`) hints at it. The move records exist in the `moves` collection. Suggest a row per move: `11:03  dev (mv)  /Demo/p6/ → /Demo/q7/  3 notes`.
7. **`diff --version` says only `unrecognized option`.** The manual's "Earlier versions: … take --version" makes it a natural guess. Suggest: `diff takes versions as arguments: diff <path> <version> [<version>]`.
8. **The preview shows excerpts of the first 5 notes only.** That's sensible for output size, but the line `… excerpts of the first 5 notes only` could name the way to the rest: `rg -n -F 'the' /Demo` (needs `-F` in `rg`, or the escaped regex).
9. **`help mv` still says "most specific form".** Since the phase 4b decision, `mv` keeps the author's form and falls back to the most specific one (`src/Kumbu.Core/Sh/Commands/Mv.cs:23`). The behaviour is right, the help text is outdated.

Minor: `mv /Demo/q7/ /Demo/q7/` says `/Demo/q7/q7/ is inside /Demo/q7/` (the target exists, so it means "into"). Correct, slightly surprising, no change needed.

## Suggested next step

1. **Build order step 6b** (added to concept §12): implement the decisions on observations 1 (warn on links that would break) and 2 (changesets with ids, `replace` without its exception), together with named checkpoints (concept §4).
2. Small fixes in step 6b, or with step 7 at the latest: 4, 7, 9; 5, 6 and 8 when convenient; 3 is an idea for later.
3. Rerun the blind test after step 6b, with the same fixture (snapshot) and a second, literal prompt („Ersetze überall Hecate durch Janus.") that leads to `replace`, to see the link warning work.
4. Then build order step 7 (embeddings and hybrid search).
