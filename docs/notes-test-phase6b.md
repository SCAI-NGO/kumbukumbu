# Test notes: phase 6b (changesets, named checkpoints, the phase 6 fixes)

Date: 2026-10-24. Hands-on test of build order step 6b (PR #9, merged as `edee49b`): changesets across notes and tools, named checkpoints (`mkcp`, `lscp`, `rmcp`, `--checkpoint` on readers, `diff` and `restore`), and the fixes for phase 6 observations 1 and 4–9. Setup as in [phase 1](notes-test-phase1.md): stack on `127.0.0.1:8083`, rebuilt by Chris with `docker compose up --build -d`, sample vaults `Demo`, `Ideas` and the local vault `Technik`. `dotnet test`: 698 passed.

The live `tools/list` was read through a small MCP client in the scratchpad (the session's own tool schemas are fixed at its start): `sh` lists `mkcp`, `lscp`, `rmcp` and the `--checkpoint` line; `replace` describes the link warning and "a plan is applied once".

Changesets are per writer **and vault**, and everything is still `dev`. So the blind-run fixture in `/Demo/arbeit/` was reset first (12:30 UTC, `restore` of each note to its original version from the phase 6 run), and every hands-on test below ran in `/Ideas`. Fixture in `/Ideas/t6b/`: `alpha.md` (a `## Notes` section, links `[[beta]]`), `beta.md` (links `[[alpha]]`), `sub/gamma.md` (links `[[alpha]]` and `[[t6b/beta]]`), plus `/Ideas/outside.md` linking in with `[[t6b/alpha]]`, `[[beta|Beta]]` and `[[gamma#Gamma]]`.

## Results

### Changesets

| Check | Result |
|---|---|
| Seeded note: `edit`, three `replace` calls, then a link rewrite by `mv` | **One** version (`dev (mv)`, the last tool's label) on top of the seed version; `diff` shows all four changes against the seed. Phase 6 observation 2 is fixed |
| A note created and edited twice in one changeset | One version |
| Write after a `restore` | The restore state stays in the history as its own entry |
| `history <folder>` | Write rows show `changeset c96918f1`; restore rows show none; folder moves show as one row (`/Ideas/z/ → /Ideas/zz/  2 notes`) |
| Changesets per vault | Writes in `/Ideas` didn't extend the open changeset in `/Demo`: the blind run's changeset `f054d497` starts after the fixture reset |
| Restart | Changesets and checkpoints survive `docker compose restart kumbu` |

Not tested by hand: the 1-hour cap and a real 10-minute gap within one test (the unit tests cover both). The blind run below shows the gap the other way round: fixture at 12:30, run at 12:51, separate changesets.

### `mkcp`, `lscp`, `rmcp`

| Case | Result |
|---|---|
| `mkcp` on a folder (with and without `/`), a note, a vault | `checkpoint 'base' of /Ideas/t6b/ (3 notes); read from it with --checkpoint base, e.g. ls --checkpoint base /Ideas/t6b` |
| Same name again (other path) | `checkpoint 'base' exists in Ideas (of /Ideas/t6b/, …); choose another name, or 'rmcp /Ideas base' first` |
| `with space`, `-bad`, missing note, missing vault, `/`, missing or extra operand | Each refused with the reason (`-bad` is read as an option: `invalid option -- 'b'`) |
| `ünï` | Accepted (Unicode letters count as letters) |
| 11th checkpoint | `Ideas has 10 checkpoints, the limit; 'lscp /Ideas' lists them, 'rmcp /Ideas <name>' removes one` |
| `lscp`, `lscp <vault>`, `lscp <folder>`, `lscp <note>` | The vault form ends with `5 of 10 checkpoints in Ideas`; a folder lists checkpoints covering it or inside it, a note those covering it |
| `rmcp` | `removed checkpoint 'fill1' (of /Ideas/t6b/alpha.md); the notes themselves are unchanged`; a second time `no checkpoint 'fill1'` (observation 6) |

### Reading with `--checkpoint`

After `mkcp`, `alpha.md` was edited, `delta.md` created and `beta.md` removed.

| Case | Result |
|---|---|
| `ls`, `cat`, `cat …#Notes`, `outline`, `rg -n`, `rg --files`, `links`, `backlinks`, `wc`, `head`, `tail`, `sort` | Each shows the checkpoint's state, including the removed `beta.md`; `links` and `backlinks` resolve inside the checkpoint |
| `cat --checkpoint base … \| rg Orion` | Works |
| Right of a pipe, with `--version`, on `history` | Refused, each with the reason |
| A note outside the checkpoint's path | `is not in checkpoint 'base' (it covers /Ideas/t6b/)` |
| A note created since | `No such file or directory` (observation 4) |
| Unknown name, name from another vault, no path | `no checkpoint 'nope' in Ideas ('lscp /Ideas' lists them)`; `--checkpoint needs a path in the checkpoint's vault` (observation 4) |

### `diff --checkpoint`

| Case | Result |
|---|---|
| A note, also `--word`, and with the option after the path | Unified diff; the header names the checkpoint |
| A removed note / a note created since | `(removed since)` / `(not in checkpoint 'base')` as the missing side |
| A folder or vault | `changed /Ideas/t6b/alpha.md +1 -0`, `removed … (-3)`, `new … +3`, and the hint to `diff <note>` and `restore` |
| Unchanged note / folder | `(no differences: … is as in checkpoint 'base')` |
| With versions, `diff --version` (phase 6 observation 7) | `--checkpoint compares one path with the checkpoint; give no versions or second path`; `takes versions as arguments: diff <path> <version> [<version>]` |

### `restore --checkpoint`

| Case | Result |
|---|---|
| A removed note | `restored … from checkpoint 'base' (it had been removed)`; it comes back as a new note (by the spec), its history in the trash copy |
| A note created since | `was not in checkpoint 'base' (created since); rm it to match the checkpoint` |
| A note pushed out of its history (11 restores, the checkpoint version long gone) | Restored from the checkpoint. A checkpoint outlives the history |
| Folder preview | Three groups, `changed`, `back`, `to trash (created since)`, plan token and the exact apply command |
| A note changed between preview and apply | `nothing was restored: … changed or appeared since the preview; preview again`; a second apply says `plan … failed at 12:32 UTC: …` |
| A note created between preview and apply | Refused the same way; nothing went to the trash |
| The plan with another checkpoint or another path | `this plan was made for another path or checkpoint` |
| Apply | `restored /Ideas/t6b/ to checkpoint 'base': 1 changed, 1 back, 2 to the trash`; `diff --checkpoint` afterwards: no changes |
| Apply again | `plan … was already applied at 12:32 UTC (…); a plan is applied once` (phase 6 observation 4) |
| **Folder restore after a `mv` inside the folder** | The moved note is listed as `to trash (created since)`, the old path as `back`. After the apply, `[[beta2]]` in `/Ideas/outside.md` and `/Ideas/retrofuturism.md` lead nowhere, without a word (observation 1) |

### Storage and integrity

| Check | Result |
|---|---|
| Collections | `checkpoints` and `checkpoint_texts`, each with a unique index on `vault` + `pid` |
| Content addressing | 7 checkpoints (three of them over the same texts) hold 7 texts: each content once |
| Readable text in any collection (dump searched for note text, checkpoint names, paths) | None; only random hits in Base64 |
| One text's blob swapped for another's in MongoDB | `checkpoint 'base' doesn't open: content it holds is missing, or was changed or damaged outside Kumbu²`, the same for every checkpoint holding that text; the others (`alpha-only`, `fill3`) open. Swapped back: all open again |

### The phase 6 fixes

| Obs. | Result |
|---|---|
| 1 | The `replace` preview lists links that would lead nowhere (`[[alpha]] → [[omega]]: no such note; now it leads to /Ideas/t6b/alpha.md`) or be ambiguous (`ambiguous (/Ideas/a/omega.md, /Ideas/b/omega.md)`), with the `mv` hint; `each still leads to a note afterwards` otherwise. But see observation 2 |
| 4 | Applied and failed plans say so, for `replace` and folder restores |
| 5 | `created /Ideas/z/alpha.md (1 line); note: [[alpha]] in /Ideas/t6b/sub/gamma.md now match two notes (…)`. Links that prefer a note in their own folder are rightly left out (observation 7) |
| 6 | Folder moves are a row in `history <folder>` |
| 7 | See `diff` above |
| 8 | `… excerpts of the first 5 notes only; every line: rg -n -F '#' /Ideas` (with `-w` for `word`) |
| 9 | `help mv` describes the kept form and the fallback |

Server log during all of this: no `fail:` or exception.

## Blind run: the literal prompt

Date: 2026-10-24, 12:50 to 12:53 UTC. The rerun the phase 6 notes suggested, on the same fixture, with the prompt that leads to `replace`.

**Setup, and a failed first attempt.** The first attempt started in a scratchpad directory, whose path contains `kumbukumbu`, and without the MCP flags taking effect (the session had the user's other MCP servers but not Kumbu²). Without Kumbu tools, the agent followed the path into the repository and read `docs/notes-test-phase6.md`, which made it no longer blind; it wrote nothing. The second attempt: an empty directory from `mktemp -d /tmp/notes.XXXXXX`, a start script next to it (`cd` and `exec claude --mcp-config <0600 file> --strict-mcp-config`), `/mcp` checked by Chris before the prompt. Token file and script deleted afterwards.

**Prompt (Chris):** „hi :-) bitte Ersetze überall Hecate durch Janus". The empty working directory made the agent ask where; Chris: „in kumbu bitte".

### What the agent did

1. `ls`, `rg -i hecate`, `rg --files | rg -i hecate`, `cat` of the name table, `rg -i janus` (taken already?).
2. **`mv` of both project notes first**, in parallel: 8 + 3 links rewritten. It followed the hint in the `replace` description ("To rename a note, mv it first").
3. `edit` of `Hecates Regeln` → `Janus' Regeln` in `gates.md` before replacing (the word match would leave `Hecates`; a plain replace would make „Januss“).
4. `replace` preview on `/Demo/arbeit` with `word`: 15 occurrences in 8 notes. The excerpts showed the goddess in `namensideen.md` (the first note alphabetically, so among the 5 with excerpts). The agent dropped the plan and made **five previews per folder or note**, leaving `namensideen.md` out, then applied all five plans in parallel: all succeeded.
5. Fixed the name table by hand: the `mv` had rewritten its `[[Hecate]]` to `[[Janus]]` in the goddess's row; the agent moved it to the Janus row and marked Hecate free again.
6. `rg -i hecate` to check, a summary, and **one question at the end**: the identifiers `hecate-proxy` and `hecate-rules/` (including the `kubectl` command) were left, rename them too? Chris: yes. One more `replace` (`hecate-` → `janus-`, 6 occurrences in 5 notes), a final `rg`.

3 minutes, 27 Kumbu² calls, none failed.

### Result

| Check | Result |
|---|---|
| Links (`links` on all 10 notes) | 16 links, none broken, none ambiguous |
| History | Each changed note: the fixture state, then **one** version from changeset `f054d497`, though up to four tools wrote it (`mv` rewrite, `edit`, `replace`, a second `replace` two minutes later). Exactly what observation 2 asked for |
| The goddess in `namensideen.md` | Left alone, the table corrected |
| Identifiers | Renamed after the question, as Chris answered |
| Retrospective and old minutes | Renamed throughout („Rückblick auf Janus 1.0, geschrieben im März 2026“): right for „überall“ |
| **`projekte/Janus.md`** | „Janus ist unser Gate-Service … **Benannt nach der griechischen Göttin der Wegkreuzungen.**“ Now wrong: the sentence has no „Hecate“ in it, and the agent never read the project note (observation 3) |
| The old name | Nowhere left; `rg Hecate` no longer finds the project (observation 3) |
| The agent's summary | Accurate, except „wie besprochen“ about the goddess lines: nothing had been discussed |

### What this means

- **Changesets work in practice.** A real session with four tools in two rounds left one clean version per note, directly on top of the state before.
- **The `mv` hint in the `replace` description prevents the problem the link warning reports.** The agent read it and moved first, so the warning never fired; the phase 6 run (with judgement) did the same. The warning stays the safety net, and works (tested by hand above).
- **`replace` lets an agent change notes it never read.** The first run read and edited note by note and wrote a sentence on the old name. This run took the shortcut the literal prompt offered and missed a sentence that depends on the name without containing it. That's not a tool bug, but the tool makes it easy: a preview line is cut at about 50 characters, and nothing suggests reading the main note.
- **The excerpt limit decided the goddess case by luck.** `namensideen.md` was first alphabetically. As note six, the agent would only have seen `2` in the count table, though it had `rg` output from step 1 too.

### Afterwards: rotating the dev token

The first, failed blind attempt printed the dev token into its transcript, so the token was rotated: a new one in `.env` and in the client's MCP entry, `docker compose up -d`. As designed (phase 5), the vaults then didn't open (`3 vaults in the database don't open with the dev token. If it was changed, start once with the recovery keys …`, and no vault created from the seed folders). Following the README: the three recovery keys from the `recovery-keys` volume into `KUMBU_RECOVERY_KEYS`, one start (`Recovery key opened vault Demo; the dev token now has a slot there`, for each vault), the keys removed from `.env`, another start. Notes, history and checkpoints all there; the old token gets `401`. The first real use of the recovery path, and it worked as the README describes.

Two things to keep in mind: the old token's slots stay in the database until revoking comes with step 8 (only the server's token check keeps it out), and the log says `Unlocked vault Technik (1 notes)`.

## Observations from the agent's side

1. **A folder restore breaks links outside the folder without a word.** `restore --checkpoint` of a folder in which a note was moved since: the preview lists the moved note as `to trash (created since)` and the old path as `back`. Links outside the folder, rewritten by the `mv` to the new name, lead nowhere after the apply. `rm` reports exactly this case (`3 links … now lead nowhere`); the restore, which may trash many notes at once, doesn't. Suggest: the preview names the links outside the scope that would lead nowhere afterwards, as `replace` does, and recognises a move (the `moves` collection knows it): `moved  /Ideas/t6b/beta.md ← /Ideas/t6b/beta2.md (moved since; 2 links outside the folder lead to the new name)`. Whether the restore then also rewrites those links back is a design question (see below).
2. **`replace` turns links to an existing other note without a word.** `beta2` → `gamma` makes `[[beta2]]` into `[[gamma]]`, which then leads to `/Ideas/t6b/sub/gamma.md`. The preview says `4 occurrences inside [[links]]; after the replace, 2 links would lead nowhere` and nothing about the other two, which now lead somewhere else. Renaming Hecate to Janus in a vault that has a note on the god Janus is that case. Suggest a third group: `[[beta2]] → [[gamma]]: now leads to /Ideas/t6b/sub/gamma.md (before: /Ideas/t6b/beta2.md)`.
3. **The preview could point at what it can't see.** Two small things would have helped this run: excerpts that show the whole line (or the sentence) rather than about 50 characters, and, when the old text names a note (`Hecate` and `Hecate.md`), a line like `/Demo/arbeit/projekte/Hecate.md is named after it: read it before applying; mv first to rename it`. The second also covers the `mv` hint with the concrete path. Neither would have caught the goddess sentence by itself, but reading the note would have.
4. **`--checkpoint` messages, small.** `cat --checkpoint base` on a note created since says `No such file or directory`, though the note exists now; `diff` says it right (`not in checkpoint 'base'`), `cat` could too. And `rg --checkpoint base Orion` without a path suggests `e.g. rg --checkpoint base /Demo`, without the pattern and in another vault.
5. **`history <folder>` leaves out notes that went to the trash.** `rm`, and the two notes a folder restore moved to the trash, have no row, so the folder history doesn't show the biggest step of a restore. Suggest a row per removal (`dev (restore)  /Ideas/t6b/delta.md  → trash`), like the move rows.
6. **`rmcp` takes any path in the vault.** `rmcp /Ideas/t6b/sub/gamma.md fill1` removed a checkpoint of `/Ideas/t6b/alpha.md`. Consistent with "names are unique per vault", but an agent may read the path as a filter and remove a checkpoint it didn't mean. Suggest: refuse a path that is neither the vault nor inside the checkpoint's scope.
7. **Wording, small.** `note: [[alpha]] in … now match two notes` (matches). The header of the link warning says `would lead nowhere` above an `ambiguous` entry. The two messages of a failed folder restore differ: `changed or appeared since the preview` at once, `changed since the preview` when asked again.

Minor: `diff --checkpoint` on a folder lists a moved note as `removed` plus `new`, which matches what a restore would do; with observation 1 it could say `moved`.

## Design questions for Chris

1. **Folder restore and moves (observation 1).** When a note was moved since the checkpoint, should the restore (a) only warn and list it as `moved`, (b) also move it back and rewrite the links that the `mv` rewrote, or (c) leave a moved note where it is? (b) is the true "back to the checkpoint" and the `moves` collection has what it needs; (a) is the small step.
   **Decided by Chris (Oct 24, 2026): (b).** A folder restore moves a note that was moved since back to its checkpoint path (with its history, instead of trashing it and creating the old path anew) and rewrites the links the move rewrote, all in the restore's transaction. The preview lists such notes as `moved back`, with the links it rewrites; links that would still lead nowhere afterwards are named, as in `replace`.
2. **`replace` and links to another note (observation 2).** Warn about these too, as a third group? I'd say yes: it is the more dangerous case, since `links` afterwards shows nothing wrong.
   **Decided by Chris (Oct 24, 2026): warn.** A third group in the preview: links that would lead to another existing note, with where they lead now and afterwards.
3. **The preview pointing at a note named after the text (observation 3).** Worth doing in step 7, or leave it to the agent?
   **Decided by Chris (Oct 24, 2026): in.** When the old text names a note (`Hecate` and `…/Hecate.md`), the preview names it: read it before applying, and `mv` it first to rename it.

**Contextual changes are the plugin package's job** (Chris, Oct 24, 2026). The goddess sentence in `Janus.md` shows the limit of any server-side check: a sentence that depends on a name without containing it. Kumbu² won't try to catch that in the tools. Users get a plugin package for their agent anyway, and its skills handle such cases, for example a rename skill that reads the notes named after the term and the notes that link to them, asks about historical records, and only then replaces. The package is its own repository, distributed through a plugin marketplace, next to this repository and the Obsidian sync plugin.

## Suggested next step

1. Build order step 7 starts with the decisions on observations 1, 2 and 3 above and the small fixes 4, 5, 6, 7 (decided by Chris, Oct 24, 2026: part of step 7, no step 6c), then embeddings and hybrid search.
2. The plugin package (rename skill and others) is planned in its own repository; not part of the server's build order.
3. Housekeeping: the test notes in `/Ideas` (`t6b/`, `a/`, `b/`, `zz/`, `outside.md`, changes to `retrofuturism.md`) and 7 checkpoints are still there; a `down -v` before the next phase resets everything.
