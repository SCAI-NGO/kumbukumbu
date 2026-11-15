# Test notes: phase 3 (write, edit, history, trash, smart seeding)

Date: 2026-10-22. Hands-on test of build order step 3 (PR #3, merged as `11960f3`), including the two fixes from [phase 2](notes-test-phase2.md). Setup as in [phase 1](notes-test-phase1.md): stack on `127.0.0.1:8083`, sample vaults `Demo` and `Ideas`. `dotnet test`: 395 passed, 0 failed, 0 skipped (under 6 s).

The Claude Code session had been started before the deploy, so it only knew the old `sh` tool (see phase 2, observation 3). `sh` was called through the session's own MCP tool; `write` and `edit` through a small JSON-RPC client calling `tools/call` on `/mcp` with the dev token. Afterwards the vaults were reset from `samples/` and the database wiped with `docker compose down -v`.

## Results

### Phase 2 fixes

| Check | Result |
|---|---|
| `cat` of a 1000-line note | Cut after a whole line: `[truncated after line 446 — showing 23973 of 53890 chars (~17964 tokens); …]` |
| `rg --files /Ideas \| sort` | `retrofuturism.md` before `Überlegungen.md`: Ü sorts with U |

### Write and edit

| Call | Result |
|---|---|
| `cat` | Content plus a separate block `version of /Demo/welcome.md: 79c521baa7f8`, also at the start of a pipeline |
| `write` new note without version | `created /Demo/inbox.md (3 lines)` plus version |
| `write` existing note without version | Rejected; says to pass the version from `cat` |
| `edit` append | `appended to /Demo/inbox.md (now 4 lines)` |
| `edit` with a stale version | `has changed since version c93185c38b16 (it is now 0cbab28b8cca); cat it again and redo the change` |
| `edit` with `old_text` occurring twice | `old_text occurs 2 times …; include more of the surrounding text` |
| `edit` with `old_text` not found | Says it must match exactly and to `cat` the current text |
| `edit` replace with umlauts | `edited /Demo/inbox.md at line 3`; content correct |
| `edit` on a missing note | `No such file or directory (if it was removed, 'trash' lists it)` |
| `edit` without `version` | `An error occurred invoking 'edit'.` (see observation 1) |
| 10 parallel `edit` calls with the same version | Exactly 1 succeeded, 9 got the conflict message |
| `write` with CRLF line endings | Stored as LF (16 bytes for two 8-character lines) |
| `write` of 1 MiB + 1 byte (ASCII and multi-byte) | Rejected with the exact size and the limit |

### Path rules for new notes

| Path | Result |
|---|---|
| `/demo/x.md` | `there is no vault 'demo' (… writing does not create new ones)` |
| `/Demo/x.txt`, `/Demo/projects` | `note names end in .md` |
| `/Demo/CON.md` | Reserved name on Windows |
| `/Demo/a?b.md` | Contains `?`, which Windows can't store |
| `/Demo/ordner./x.md`, `/Demo/ordner /x.md` | Folder ends with a dot or space |
| `/Demo/trailing. .md` | Created (correct: only the end of a name matters) |
| `/Demo/a\u0000b.md` | Contains a control character |
| `/Demo/.obsidian/workspace.md` | Rejected: a leading `.` hides it in Obsidian |
| `/Demo/projects/kumbu.md/sub.md` | `Not a directory` |
| `/x.md` | Notes live inside a vault |
| `Demo/relative.md` | Paths must be absolute |
| `/Demo/../../Ideas/escape.md` | Normalised to `/Ideas/escape.md` and created there |
| `/Demo/neu/tief/verschachtelt/Übersicht.md` | Created; missing folders are fine |

### History and restore

| Call | Result |
|---|---|
| Two appends by `dev` to a seeded note within 10 minutes | History: current state plus the `seed` state; the intermediate `dev` state coalesced away. The state before the agent session survives. |
| `restore /Demo/welcome.md 79c521baa7f8` | Restored; the replaced state stays in the history, so the restore can be undone |
| `restore` with an unknown version | Says `history` lists the versions |
| `help restore` | Clear page with both forms and examples |

### Trash

| Call | Result |
|---|---|
| `rm /Demo/inbox.md` | Moved to the trash, with the hint how to restore it |
| `cat` after `rm` | `No such file or directory` |
| `restore` while a note exists at the path | Rejected, pointing to `history` |
| `rm` twice at the same path, then `trash /Demo` | Two identical lines (see observation 2) |
| `restore /Demo/inbox.md` | Most recently removed one comes back with its old version token |
| `rm /Demo`, `rm /Demo/projects` | `Is a directory (rm removes one note at a time)` |
| `rm /Demo/*.md`, `rg -l x \| rm` | No globbing; `rm` does not read from a pipe |

### Smart seeding

Setup before restarting the server: `retrofuturism.md` edited through MCP, `projects/kumbu.md` removed through MCP, then files on disk changed.

| Case | Result |
|---|---|
| File changed on disk, note untouched through MCP (`Meeting Notes.md`) | Updated |
| File changed on disk, note restored through MCP to its seeded content (`welcome.md`) | Updated (counts as unchanged since the seed); history shows `seed` as the new writer |
| Changed on disk and through MCP (`retrofuturism.md`) | Database kept, warning in the log (see observation 3) |
| New file on disk (`fresh.md`) | Created |
| Note in the trash, file still on disk (`kumbu.md`) | Left in the trash, logged |

Summary line: `Seeded 6 notes in 2 vaults: 1 created, 2 updated, 1 kept as changed through MCP, 1 left in the trash (0 files skipped)`.

## Observations from the agent's side

The model is consistent and the messages are excellent: every error says what to do next, usually with the exact command. Nothing here blocks the next step.

1. **Missing required parameter gives a generic error.** `edit` without `version` returns `An error occurred invoking 'edit'.` instead of a message like the others. Clients usually validate against the schema first, so this is rare, but a model that ignores the schema learns nothing from it. Return `edit: version is required; get it from 'cat <path>'` (same for missing `path`, `new_text`, `content`).
2. **Trash entries at the same path can't be told apart.** After removing a note twice at one path, `trash` shows two identical lines, and `restore <path>` always takes the most recent one; the older one can't be reached. Show the version token and line count per entry (as `history` does), and accept `restore <path> <version>` for a trashed note.
3. **When the database wins at seeding, the disk version is not kept in Kumbu².** It still exists on disk, so nothing is lost yet, but the warning repeats on every start and the only way to get the disk text in is by hand. For seeding this is acceptable. For the Obsidian sync plugin the same situation (changed locally and on the server) must not drop either side; see the conflict section of [obsidian-plugin-design-spec.md](obsidian-plugin-design-spec.md).
4. **The same token can appear twice in a history** (after a restore to an earlier text). This is correct by design (same content, same token) and harmless, since either entry restores the same text. Worth one line in `help history` so nobody takes it for a bug.

## Suggested next step

Observations 1, 2 and 4 are small and can go in with build order step 4 (heading index, sections, links, backlinks, link normaliser). Observation 3 is input for the plugin design, not a change to seeding.
