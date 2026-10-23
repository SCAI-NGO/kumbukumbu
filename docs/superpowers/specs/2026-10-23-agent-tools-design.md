# Agent Tools for Larger Changes — Design

**Date:** 2026-10-23
**Scope:** `docs/concept.md` §12, step 6: viewing and comparing earlier versions, changes across many notes, and MongoDB transactions to make them atomic. Plus observation 7 from [notes-test-phase5.md](../../notes-test-phase5.md).
**Status:** The step, its place in the build order, and the questions in §3 are settled with Chris (2026-10-23). The details are written by Claude for review with the PR.

## 1. Intent

Working on one note with Kumbu² already feels like working in a local directory with Claude Code. Two things are missing for v1:

1. **Seeing what changed.** In Claude Code every edit shows as a diff, and `git diff` shows it again afterwards. In Kumbu² `history` lists versions, but an earlier version can't be read without restoring it, and nothing compares two versions. For the person whose notes an agent changed, that is the difference between trusting and checking by hand.
2. **Changes across many notes.** Renaming a tag in 40 notes is one `sed` call locally, and 80 calls (`cat` and `edit` per note) in Kumbu². It works, but it is slow and costs context. `edit` has no "replace every occurrence", and `mv` moves one note at a time, not a folder.

v1 stays text only (concept §12). No scripting: the shell stays a fixed set of commands that can't escape the vault model.

### Success criteria
- An agent or a person can read any version that `history` lists, with every command that reads a note, and see what changed between two versions, or since a version, as a line or word diff.
- A person can ask "what did the agent change in my vault this afternoon?" and get an answer in one call.
- Replacing a text in every note of a folder is one preview and one apply. The apply changes all notes of the preview or none, keeps every note's history, and never loses a concurrent edit.
- A folder can be moved or renamed with one `mv`, links included, all or nothing.
- `mv`, `rm` and `restore` become single transactions; the cancellation guard from the phase 5 review is no longer needed.
- After a repair in the database, the first access already gives the right answer (phase 5, observation 7).

### Out of scope
Attachments, regular expressions in `replace` (v2, if experience shows the need), frontmatter queries (a later step if `rg` turns out not to be enough), undo of a whole session (v2, see §3), several vaults in one change.

## 2. Design

### 2.1 Reading an earlier version

`--version <v>` on every command that reads the content of one note: `cat` (also with a section, `cat "note.md#Goals" --version <v>`), `head`, `tail`, `wc`, `sort`, `outline`, `links`, and `rg` with exactly one note as path. Output limits as usual, and no version block, since an earlier version can't be edited, only restored.

All reading commands get it, not just `cat` (decided by Chris): if two commands on the same note could return different states, the context would hold contradicting data. Where a version makes no sense, the command says so instead of ignoring the option: `ls`, `backlinks` (it reads other notes, each with its own versions), `rg` over a directory or several notes (`--version names a version of one note; give rg a single note`).

Why not `note.md@<v>`: `@` is allowed in note names, and Obsidian users do use it. An option keeps paths unambiguous.

### 2.2 Diff

`diff <path> [<from> [<to>]]`, unified diff with three lines of context, the way `git diff` looks:

- `diff note.md` → the previous version against the current one ("what did the last write change").
- `diff note.md a1b2c3d4e5f6` → that version against the current one ("what changed since I read it").
- `diff note.md a1b2… f6e5…` → two versions.
- `diff <path> <path>` → two notes, for near-duplicates.

Header lines name the versions with time and writer from `history`, so it reads like a commit:

```
--- /Demo/plan.md  a1b2c3d4e5f6  2026-10-23 14:02 UTC  dev
+++ /Demo/plan.md  9f8e7d6c5b4a  2026-10-23 14:30 UTC  dev (mv)
@@ -12,3 +12,4 @@
```

The diff is line-based (Myers, as in git). `--word` adds a word diff inside changed lines, as `git diff --word-diff` does (`[-old-]{+new+}`), since Markdown paragraphs are often single long lines and one changed word otherwise marks the whole paragraph. Decided by Chris: in v1, it makes the feature complete.

### 2.3 What changed recently

`history` on a directory or a vault lists the latest writes below it, newest first, one line per write: time, writer, path, version, lines added and removed.

```
history /Demo --since 2h
2026-10-23 14:30 UTC  dev (mv)  /Demo/plan.md        9f8e7d6c5b4a  +1 -1
2026-10-23 14:02 UTC  dev       /Demo/notes/ideas.md  a1b2c3d4e5f6  +12 -0
```

`--since` takes `30m`, `2h`, `1d` or a UTC time. Without it, the last 50 writes. It only shows what the history still holds (10 versions per note, coalesced), which the help says.

Combined with `diff`, this is the review of a session: `history /Demo --since 2h`, then `diff` on each line that matters. Once API keys exist (step 8), the writer names the agent.

### 2.4 Replace every occurrence in one note

`edit` gets `replace_all` (default false), like Claude Code's `Edit`: every occurrence of `old_text` (in the note, or in the section of the path) becomes `new_text`. The result says how many were replaced. Without `replace_all`, the rule stays "exactly once".

### 2.5 Replace across many notes

A new MCP tool `replace`, not an `sh` command, since content does not go through `sh` (concept §5). Literal text, as in `edit`, with an option `word` (match whole words only, as `rg -w`). Case-sensitive: `Phase` and `phase` are two runs, each with its own preview, which is clearer than one clever pattern. Two calls:

1. **Preview:** `replace(path, old_text, new_text, word)` with `path` a folder, a vault, or a note. It lists **every** affected note with its number of matches, one line each, and before → after excerpts (line number and the changed words in context) for the first few, then a **plan token**. If even the list of notes doesn't fit the output limit, the preview refuses with `… matches in 412 notes, too many for one preview; narrow the path`. That is the only cap: an apply never touches a note the preview didn't name.
2. **Apply:** the same call with `plan` set to that token. The token stands for the exact set of notes *and their versions* the preview saw. The apply runs as **one transaction** (§2.7): if any of those notes changed since the preview, nothing is written, and the result names the changed notes and asks for a new preview. Otherwise every note is changed, and the result says `replaced 37 occurrences in 12 notes`.

Every changed note gets its own history entry, marked `dev (replace)`, so it doesn't coalesce with earlier writes and can be restored on its own.

Why a preview first: a change over many notes is the one place where an agent's mistake is expensive to undo by hand. The preview costs one call and shows exactly what will happen; the plan token makes sure what was shown is what is applied.

Regular expressions were weighed and left for v2 (decided by Chris): for prose, the real needs are whole words and case, which `word` and two runs cover; a regex option can come once real use shows a need for it.

### 2.6 Moving a folder

`mv <dir>/ <target>/` moves every note below the folder, keeping the structure below it, and rewrites links in one pass, as for a single note (the moved notes' own relative links included). Move records are written for every note, so old paths say where they went.

Before anything is written, everything is checked: no target exists, all names are valid, the target isn't inside the source. Then the whole move, link rewrites and move records included, runs as one transaction (§2.7): after a failure nothing has moved.

The trailing `/` on the source is required. It says "I mean the folder", so a mistyped note name never moves a folder. Without it, `mv` on a folder says `Is a directory (to move the folder, write /Demo/a/)`.

`rm` of a folder stays out: deleting many notes at once is easy to get wrong and rarely needed.

### 2.7 MongoDB as a single-node replica set

Decided by Chris (option (c) of the discussion), on the condition that it runs without anyone having to step in. MongoDB has transactions only on a replica set; a set with a single member costs nothing extra and keeps the deployment one container.

**Why such setups often need manual repair, and the fix.** The replica set configuration stores each member's host name. A bare `rs.initiate()` stores the container's host name, which is its container id, and that changes whenever the container is recreated: a new image, `docker compose up --force-recreate`, a changed compose file. After that, the node is no longer a member of its own set and there is no primary until someone runs `rs.reconfig`. Reproduced on 2026-10-23 with `mongo:8`: after a recreate, `rs.status()` says `Our replica set config is invalid or we are not a member of it`.

The fix is a fixed host name, initiated with that name, and a health check that waits for a writable primary:

```yaml
  mongo:
    image: ${KUMBU_MONGO_IMAGE:-mongo:8}
    command: ["--replSet", "rs0", "--bind_ip_all"]
    # The replica set config stores the member's host; the default hostname (the container id)
    # changes whenever the container is recreated, and the node would lose its own set.
    hostname: mongo
    volumes:
      - mongo-data:/data/db
    healthcheck:
      # Initiates the set on the first start (idempotent), then reports whether this node is the
      # writable primary, which takes a few seconds after every start.
      test: ["CMD", "mongosh", "--quiet", "--eval", "try { rs.status() } catch (e) { rs.initiate({ _id: 'rs0', members: [{ _id: 0, host: 'mongo:27017' }] }) }; quit(db.hello().isWritablePrimary ? 0 : 1)"]
      interval: 5s
      timeout: 10s
      start_period: 30s
      start_interval: 1s
      retries: 10
```

`kumbu` keeps `depends_on: mongo: condition: service_healthy`, so it only starts against a writable primary. Its connection string becomes `mongodb://mongo:27017/?directConnection=true`: the driver talks to that one node and doesn't depend on the host names in the set's configuration. Transactions work over a direct connection to the primary.

**Verified on 2026-10-23** in a throwaway stack (`mongo:8`, Docker on Linux), each case checked by waiting for the health check and then committing a two-document transaction, with both `?replicaSet=rs0` and `?directConnection=true`. Healthy again within 1 to 3 seconds and no data lost after: first start, `restart`, `up --force-recreate` (new container id), `down` and `up` (network recreated), `kill -9`, `stop -t 0`, three `kill -9` in a row. Not tested: a restart of the Docker daemon and a host reboot (both follow the `restart` path, as the data volume and the host name stay the same).

Tests: Testcontainers' MongoDB module has `WithReplicaSet()` (4.16.0, the version in use), so the server tests run against a replica set too.

Existing databases: none in production. Locally, `docker compose down -v` once, as in step 5.

Later, if MongoDB gets authentication: a replica set with auth needs a key file (mode 400, owned by the `mongodb` user), the other classic trap. Not needed while MongoDB is only reachable on the compose network.

With transactions in place, `mv`, `rm` and `restore` write their two documents in one transaction, and the cancellation guard from the phase 5 review (`notes-test-phase5.md`, spec §4 of the encryption design) is removed.

### 2.8 Phase 5, observation 7

`GetAsync` and `ReadAsync` in `EncryptedNoteStore` refresh a path that is marked damaged *before* reading; `IsDamagedAsync` then only checks the marker. The first access after a repair returns the note.

## 3. Decisions

Settled with Chris on 2026-10-23:

| | Question | Decision |
|---|---|---|
| Q1 | `--version` only on `cat`, or on every reading command? | Every command that reads one note (§2.1): parity, so the context never holds two states of one note |
| Q2 | Word diff in v1? | Yes, `diff --word` (§2.2) |
| Q3 | Regular expressions in `replace`? | No: literal with `word`; regex in v2 if needed (§2.5) |
| Q4 | Caps for `replace`? | Only what fits into one preview; the apply touches exactly the previewed notes (§2.5) |
| Q5 | Consistency of multi-note changes | Transactions on a single-node replica set, set up so it needs no manual repair (§2.7) |
| Q6 | Undo of a whole session as one command? | Not in v1: rare, and per-note `restore` covers it. To be thought about for v2, together with per-agent keys (step 8), when "one writer" means one session |

## 4. Testing

Unit tests in `Kumbu.Core.Tests` for the diff (empty notes, no final newline, long lines, word diff, a version missing from history), `--version` on each reading command and the refusals, the plan token (a note changed, removed or added between preview and apply), `word` matching, `replace_all` with sections, folder moves (nested folders, links in and out of the folder, relative links, a target inside the source, a name collision), and `history` on a directory with `--since`. Server tests for the transactions: a failure in the middle of a folder move or an apply leaves nothing changed. Then the hands-on test as the agent, including the compose restarts from §2.7 against the real stack, and a blind run with a realistic task such as "rename the project Hecate to Selene everywhere in my notes".
