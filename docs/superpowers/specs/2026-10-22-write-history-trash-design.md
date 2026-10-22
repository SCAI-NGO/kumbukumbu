# Writing, Versions, History and Trash — Design

**Date:** 2026-10-22
**Scope:** `docs/concept.md` §12, step 3 (`edit` / `write` with version tokens; history + trash), plus the two fixes from [notes-test-phase2.md](../../notes-test-phase2.md) (done first, in their own commit).
**Status:** Two decisions settled with Chris (seeding, append); the rest written by Claude for review with the PR.

## 1. Intent

### Success criteria
- An agent reads a note with `cat`, gets a **version token** next to the content, and changes the note with `edit` or `write` by sending that token back. If someone else changed the note in the meantime, the write fails with a message that says what to do.
- `edit` replaces one unique passage (`old_text` → `new_text`) or appends to the end, so small changes never resend the whole note.
- `history <path>` lists earlier versions; `restore <path> <version>` brings one back. The state before an agent session is always recoverable.
- `rm` moves a note to the trash; `trash` lists it; `restore <path>` brings it back. Trash expires after 30 days.
- The seeder keeps working for people who edit files on disk, without overwriting what agents wrote.
- `CONCEPTS.md` chapter 3.

### Out of scope
Sections (`outline`, `cat note.md#Heading`, section edits), `mv` (it must rewrite links, step 4), `mkdir` (directories are implicit until vaults become real in step 5/7), per-key permissions (step 7), encryption (step 5).

## 2. Decisions

| Topic | Decision | Why |
|---|---|---|
| Tools | Three, as in concept §5: `sh` (now with `history`, `restore`, `rm`, `trash`), `write` and `edit`. Content travels only as JSON parameters of `write` and `edit`. | Multi-line Markdown in a shell string means quoting hell. `rm` and `restore` carry no content, so they fit `sh`. |
| Version token | 12 hex characters of the SHA-256 of the content. | Computed, not stored: the same content always has the same token, so there is nothing to migrate and nothing to get out of sync. The token only has to tell apart states of one note; 48 bits are plenty. Changing a note back to an earlier text gives the earlier token again, which is harmless: the content the agent read is exactly what is there. |
| Where the token appears | `cat` returns it as a second content block (`version of /Demo/welcome.md: 3f9a2c1b7d0e`), also when `cat` starts a pipeline. `write` and `edit` return the new token the same way. `history` lists tokens. | Concept §4: kept separate from the content, so a pipe never mixes it into the text. A model sees every content block. |
| Optimistic concurrency | `write` with a token overwrites only if the note still has that token; `write` without a token creates and fails if the note exists. `edit` always needs a token. In Mongo, the replace is one `UpdateOne` filtered on a revision counter (compare-and-swap), so two writers cannot both win. | Concept §4. No locks, no transactions (a standalone MongoDB has none). |
| `rm` and `restore` need no token | Both can be undone: `rm` goes to the trash, `restore` writes a version and keeps the one it replaces in the history. | A token protects against silently losing someone's change. Here nothing is lost. |
| History | The last **10** earlier states, stored inside the note's document (with who wrote them and when). Writes by the same writer within **10 minutes** coalesce: the state they replace is not kept again. `restore <path> <version>` takes a token from `history`. | Concept §4. Keeping the history inside the document makes every write and every move to the trash a single atomic operation. 11 × 1 MiB stays below MongoDB's 16 MiB document limit. Coalescing keeps "the state before this agent session" without filling the history with every intermediate step. |
| Who writes | For now `dev` (all MCP calls, the dev token) and `seed`. | Step 7 replaces `dev` with the API key's id; coalescing already works per writer. |
| Trash | A separate `trash` collection; the whole document moves there with `deletedAt`. A TTL index removes it after **30 days**. `restore <path>` without a version restores the most recently deleted note at that path, unless a note exists there. | Concept §4. MongoDB's TTL index does the expiry, no job needed. |
| New notes | Path must end in `.md`, lie inside an **existing vault**, not be a directory, and use no names that Windows can't store (`<>:"\|?*`, control characters, trailing dot or space, `CON`, `NUL`, `COM1` …). Size at most 1 MiB (UTF-8), as for seeding. CRLF becomes LF. | Concept §4. A typo like `/demo/x.md` should not create a vault `demo`; vaults get created on purpose once they are real (step 5/7). |
| `edit` | `old_text` must occur exactly once; otherwise the error says how often it occurs. Without `old_text`, `new_text` is appended (on a new line if the note doesn't end with one). | Settled with Chris: append is common (journal, inbox) and must not resend the note. Unique match is the rule models know from their own `str_replace` tools. |
| Seeding | **Smart update** (settled with Chris). The note remembers the content it was last seeded with. On start: a new file creates the note (unless that path is in the trash); a changed file updates the note only if nobody changed it through MCP since the last seed, and that update lands in the history like any write; if both sides changed, the database wins and the log says so. Notes seeded before this change count as unchanged since seeding. | The database is now the source of truth, but editing files on disk and restarting stays useful. A three-way comparison (disk, database, last seed) tells which side changed. |
| Tool annotations | `sh`: not read-only any more, not destructive (`rm` is soft). `write`: destructive (it overwrites). `edit`: not destructive. None idempotent except reading. | Clients use the hints to decide what to confirm. |

## 3. Shape

```
Kumbu.Core
  Notes/VersionToken      token = hex(SHA-256(content))[..12]
  Notes/StoredNote        content, revision, history, last writer/time, seed content
  Notes/INoteStore        INoteReader + get, create, compare-and-swap replace, trash, restore
  Notes/NoteWriter        all write rules: validation, tokens, coalescing, history cap, trash
  Sh/Commands/{History,Rm,Restore,Trash}
Kumbu.Server
  Notes/MongoNoteStore    INoteStore on collections notes + trash (TTL index)
  Notes/Seeder            smart update via SeedDecision (pure, tested without Mongo)
  Mcp/ShTool              adds the version block
  Mcp/WriteTools          write, edit → NoteWriter
```

`NoteWriter` takes a `TimeProvider`, so tests control the 10-minute window and the trash time.
