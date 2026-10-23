# Test notes: phase 5 (encryption at rest)

Date: 2026-10-23. Hands-on test of build order step 5 (PR #6, merged as `e2c00b7`): encrypted vaults, key slots, recovery keys, the working set in memory, and the phase 4b feedback. Setup as in [phase 1](notes-test-phase1.md): stack on `127.0.0.1:8083`, rebuilt with `update.sh`, sample vaults `Demo`, `Ideas` and the local vault `Technik`. `dotnet test`: 571 passed.

The database still held plaintext data from phase 4b, so after the update every call failed with `The database holds plaintext notes from before encryption (step 5). Wipe it with 'docker compose down -v' …`, as designed. After `docker compose down -v` the seeder created three vaults (6 notes) and wrote one recovery key file each. Tokens and recovery keys used in the tests lived in 0600 files in the session scratchpad and were deleted afterwards; `.env` was restored byte for byte, and the stack was wiped and reseeded at the end.

## Results

### Encryption at rest

| Check | Result |
|---|---|
| `mongodump` of the database, searched for vault names, note names, paths, content (`Demo`, `welcome`, `Wärmepumpe`, `Überlegungen`, `Part 2`, `second brain`, …) and the writer `dev` | No match in any collection. Field names (`rev`, `blob`) were found, so the search itself worked |
| What MongoDB shows | `vaults`: id, sealed `meta`, time. `notes`/`trash`/`moves`: vault id, keyed path hash `pid`, `rev`, sealed `blob`, times. `slots`: vault id, kind, lookup hash, wrapped key |
| Indexes | Unique on `notes (vault, pid)`; TTL of 30 days on `trash.deletedAt` and `moves.at` |
| Dev token in the dump | Not present |
| Recovery keys and the token in the server log | Not present; the log names only the file a key was written to |
| Recovery key files | `/recovery/<vault>-<id>.recovery-key.txt`, mode 0600, owner `app`; the file explains what the key is for and how to use it |
| Wrong bearer token | `401` before any vault is touched; `/healthz` stays `200` without a token |

### Tampering with the database

Done directly in MongoDB, then the server restarted so the vaults were loaded again; the original documents were put back afterwards.

| Change | Result |
|---|---|
| Sealed blob of one note copied over another note of the same vault | Log: `fail: Document … of vault Demo does not open; it was changed or damaged outside Kumbu²`. The note vanished for the agent: `rg --files` lists the rest, `cat` says `No such file or directory`, `links` shows `[[kumbu]] → not found` (observation 2) |
| `write` to the path of that damaged note | `An error occurred invoking 'write'.`; the log shows an unhandled `AuthenticationTagMismatchException` from `VaultCatalog.RefreshAsync` (observation 1) |
| `rev` of a note changed | Refused on opening, same log line: the revision is bound into the note's context |
| Note of `Demo` copied into `Ideas`, with its `pid` | Refused, same log line: the vault is bound too |

### Token change and recovery keys

| Step | Result |
|---|---|
| New `KUMBU_DEV_TOKEN`, no recovery keys | Log: `warn: 3 vaults in the database don't open with the dev token …`, and `fail: Did not create vault Demo: some vaults don't open with the dev token, and one may be this one` for each folder. With the new token `ls` returns an empty text, `cat` says `No such file or directory` (observation 3). The old token gets `401` |
| Two of three recovery keys in `KUMBU_RECOVERY_KEYS` | `Recovery key opened vault Demo; the dev token now has a slot there`, the same for `Ideas`; `ls` shows `Demo/` and `Ideas/` with all notes, including those written by agents before the change. `Technik` stays locked, and its `fail:` line remains |
| Restart with the same keys still set | Same `now has a slot there` lines again, but the slot count stays the same (5 key slots, 3 recovery slots): idempotent (observation 5) |
| Mistyped, truncated and nonsense keys | `warn: A recovery key from Kumbu:Recovery:Keys opened no vault (mistyped, or its vault is gone)` per key; the server starts normally |
| Third key | `Technik` opens; no `fail:` left |
| Back to the original token | Works at once: the old token keeps its slots, as documented |

### Seeder and vault lifecycle

| Case | Result |
|---|---|
| New folder `Neu` with one note | `Created vault Neu; its recovery key was written to /recovery/Neu-….recovery-key.txt`; the other vaults `Left … alone: it already holds notes` |
| Empty folder `Leer` | Not a vault, and no log line about it; `write /Leer/erste.md` says `there is no vault 'Leer'` (observation 4) |
| `write` to a vault that doesn't exist | `there is no vault 'Gibtsnicht' (ls lists the vaults; writing does not create new ones)` |
| `Kumbu__Vaults__IdleMinutes=1` (compose override, test only) | All four vaults were unlocked at start by the seeder, then `Locked vault … after 1 idle minutes` for each; the next `rg` unlocked them again on demand, results unchanged |
| Several restarts | All notes, history, trash and move records survived |

### Phase 4b feedback

Fixture `/Demo/lt/`: `hub.md` links `/Demo/lt/a/target.md` in every form (frontmatter, bare, partial path, vault path, leading `/`, `./` relative, heading with alias, block, embed), plus `[[nowhere]]`, `[[ ]]`, `[[|x]]`; `b/side.md` links it as `[[../a/target]]`.

| Item | Result |
|---|---|
| 4: `mv` keeps the link form | `mv …/a/target.md …/c/goal.md` rewrote 10 links: `[[goal]]`, `[[c/goal]]`, `[[lt/c/goal]]`, `[[/lt/c/goal]]`, heading, alias, block, embed and frontmatter kept; both relative links became the vault path `[[lt/c/goal]]`, as decided |
| 4: move back | Every link back to its original form, except the two relative ones, which stay `[[lt/a/target]]` (by design) |
| 1: move record | `cat` of the old path: `No such file or directory; it was moved to /Demo/lt/c/goal.md on 2026-10-23 19:23 UTC (dev)` |
| 2: `rm` names dangling links | `… 10 links in /Demo/lt/b/side.md, /Demo/lt/hub.md now lead nowhere`; `restore` brings the note back and `links /Demo/lt` shows only `[[nowhere]]` again |
| 3, 13: own history entry | `history /Demo/lt/hub.md`: my `dev` write, then two `dev (mv)` entries, one per move |
| 6: `links` on a section | `links "…/hub.md#Links"` lists only that section's links; `#Other` says `(no links)` |
| 7: `links` on a directory | `links /Demo` lists only broken and ambiguous links, as `path:line: link → not found` |
| 8: `backlinks` without any | `(no notes link here)` |
| 10: `[[ ]]`, `[[\|x]]` | No longer listed as links |
| 12: version block | Starts with its own line break |

## Observations from the agent's side

For an agent nothing changed, and that's the point: every tool behaved as in phase 4b, the 4b feedback is in and reads well, and I noticed the encryption only through restarts taking a moment longer. The decided `mv` link form is exactly right: a rename back leaves the notes as they were. The tampering checks did what the spec promises. The weak spots are all at the edges where something is *missing*, and there the agent gets less information than the operator.

Ordered by usefulness:

1. **Bug: `write` to the path of a damaged document throws.** The create fails on the unique index, `RefreshAsync` tries to open the damaged document and the `AuthenticationTagMismatchException` escapes as `An error occurred invoking 'write'.` The agent can't tell what happened and would likely retry. Expected: a clear error such as `write: /Demo/projects/kumbu.md is held by a document that doesn't open (changed or damaged outside Kumbu²); see the server log`. Other paths through `RefreshAsync` (a failed compare-and-swap on `edit`, `mv`, `restore`) probably have the same problem; a test that damages a document and then runs each write tool on its path would find them.
2. **A damaged note disappears without a trace for the agent.** Only the server log knows. An agent sees `No such file or directory` and dangling links, and may "repair" them by writing a new note. Suggest: `cat` of such a path says `damaged` instead of `No such file`, and `ls` of the directory, or at least the vault root, ends with `(1 note here doesn't open; changed or damaged outside Kumbu²)`. That doesn't leak anything to a token that has the vault's key anyway.
3. **A locked vault looks like no vault.** After a token change without recovery keys, `ls` returns an empty text, not even `(no vaults)`. For an agent that is indistinguishable from "this user has no notes", and it might start creating structure, which is refused (`there is no vault`) and makes it more confusing. Suggest `(no vaults)` for the empty case and, while the dev token is the only credential, `(3 vaults in the database don't open with this token; see the server log)`. Once there are per-user keys (step 7), the second part has to go, since then it would tell one user about another's vaults.
4. **An empty folder doesn't become a vault, silently.** The spec says "a folder becomes a vault" and "an empty vault still exists", and the README "each folder becomes an encrypted vault". The seeder groups files, so a folder without `.md` files is skipped without a log line. Either create the vault (then it can be filled with `write` right away, which is what I tried), or log `Skipped folder Leer: no notes`. I'd create it.
5. **Small wording in the seeder log.**
   - `the dev token now has a slot there` repeats on every start while `KUMBU_RECOVERY_KEYS` is set, although the slot already exists. `already has a slot there; remove KUMBU_RECOVERY_KEYS from .env` would also remind of the cleanup step.
   - The hints say `Kumbu__Recovery__Keys` / `Kumbu:Recovery:Keys`; with Compose the variable is `KUMBU_RECOVERY_KEYS`. Naming both helps.
   - `Seeded 0 notes into 0 vaults; left 3 vaults alone` counts the vaults it refused to create as "left alone"; `1 vaults`, `1 notes`.
6. **A vault that becomes empty is seeded again.** The seeder fills a vault without notes and without trash. If someone deletes every note of a vault and the trash expires after 30 days, the next start refills it from the seed folder. Unlikely while seeding is only for development, but worth a line in the README, or a marker on the vault that it was seeded once.

Not tested: the 512 MB memory cap (would need a large fixture), two server processes on one database, rollback with an old sealed document (a known limit in the spec). No blind agent run this time: the agent-facing surface is the same as in phase 4b, and the 4b changes were checked directly above.

### update.sh

Chris wrote `update.sh` for this phase (pull, test, build, start). Reviewed together: `set -e`, `cd "$(dirname "$0")"`, `git pull --ff-only`, tests before the deploy, and after the start a wait for `Application started`, a check of the startup log for `fail:`/`crit:` lines, and a hint when recovery key files are waiting in `/recovery`. With that, the plaintext refusal at the start of this test, and a locked vault after a token change, both stop the script with the reason instead of passing silently.

## Retest after the fix (PR #7)

Date: 2026-10-23, after PR #7 (`ca82dff`, merged as `3e2537e`), deployed with `update.sh`. `dotnet test`: 588 passed. Same procedure as above; the stack was wiped and reseeded afterwards, `.env` restored byte for byte.

| Observation | Result |
|---|---|
| 1: write over a damaged document | Fixed. One byte flipped in a note's blob, server restarted. `write`, `edit`, `mv` (as source and as target), `rm`, `history`, `restore`, `cat` and `wc` on that path all answer `<command>: /Demo/dmg/victim.md: damaged: the stored note doesn't open (changed or damaged outside Kumbu²; the server log has details). Its path stays taken until the document is repaired or removed in the database`. No exception in the log |
| 2: damaged note visible | Fixed. `ls /` and `ls /Demo` end with `(1 note here doesn't open: changed or damaged outside Kumbu²; the server log has details)`. `ls /Demo/dmg` says `No such file or directory`, which can't be helped: the path is inside the sealed blob, only its keyed hash is known |
| 3: locked vaults | Fixed. With a new token: `(no vaults)` and `(3 vaults in the database don't open with this token; the server log says how to recover them)`; with one vault locked, the open vaults followed by the same line in the singular |
| 4: empty folder | Fixed. `Created vault Leer; its recovery key was written to …` |
| 5: log wording | Fixed. Second start with the keys still set: `Recovery key for vault Demo: the dev token already has a slot there; remove KUMBU_RECOVERY_KEYS from .env (Kumbu__Recovery__Keys)`; `did not create 4 vaults (see above)` is counted apart; `1 vault`, `1 note` |
| 6: emptied vault seeded again | Documented in the README |

New, all small:

7. **The first access after a repair in the database gives a wrong answer once.** After putting the original blob back (no restart), the first `cat` said `No such file or directory`; the second showed the note. The reason is in `CommandArgs.ReadNoteAsync` (`src/Kumbu.Core/Sh/Commands/CommandArgs.cs:43-55`): `ReadAsync` returns `null`, then `IsDamagedAsync` refreshes the note from MongoDB, finds it healthy and returns `false`, and the caller goes on as if the path were empty. `rm`, `mv`, `restore` and `write` (`NoteWriter.cs:67`, `:219`, `:261`, `NoteMover.cs:21`) follow the same pattern. Suggest doing the refresh *before* the read: `GetAsync`/`ReadAsync` in `EncryptedNoteStore` refresh a path that is marked damaged, and `IsDamagedAsync` only looks at the marker. Rare (it needs an operator repairing the database by hand), so no hurry.
8. **An empty vault counts as filled on every start.** On each start the seeder logs `Seeded 0 notes into 1 vault` for `Leer`, which already exists, and no `Left vault Leer alone` line. Only the wording.
9. **A vault created while another token is set has no slot for the old token.** `Leer` was created while the test token was active. After going back to the original token, `ls` reported `1 vault … doesn't open with this token` and the seeder refused to create `Leer` again. This is consistent with key slots (the old token never had access to that vault). It's worth knowing when switching tokens back and forth, and with step 7 it is how it should be anyway.

## Suggested next step

Observation 7 can go along with step 6, or whenever `EncryptedNoteStore` is touched next; 8 and 9 need nothing. Build order step 6 (embeddings and hybrid search) can start.

### Before the retest

Fix observation 1 (with tests for every write path over a damaged document) and the small observations 3 to 5 before step 6; 2 and 6 can follow. Then build order step 6 (embeddings and hybrid search).
