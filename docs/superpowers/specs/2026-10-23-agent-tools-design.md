# Agent Tools for Larger Changes — Design

**Date:** 2026-10-23
**Scope:** `docs/concept.md` §12, step 6: viewing and comparing earlier versions, and changes across many notes. Plus observation 7 from [notes-test-phase5.md](../../notes-test-phase5.md).
**Status:** Draft by Claude, for Chris to sleep on. The step itself and its place in the build order are decided (2026-10-23); everything below is a proposal. Questions marked **Q** need Chris's decision before the build.

## 1. Intent

Working on one note with Kumbu² already feels like working in a local directory with Claude Code (comparison in the conversation of 2026-10-23, summarised here). Two things are missing for v1:

1. **Seeing what changed.** In Claude Code every edit shows as a diff, and `git diff` shows it again afterwards. In Kumbu² `history` lists versions, but an earlier version can't be read without restoring it, and nothing compares two versions. For the person whose notes an agent changed, that is the difference between trusting and checking by hand.
2. **Changes across many notes.** Renaming a tag in 40 notes is one `sed` call locally, and 80 calls (`cat` and `edit` per note) in Kumbu². It works, but it is slow and costs context. `edit` has no "replace every occurrence", and `mv` moves one note at a time, not a folder.

v1 stays text only (concept §12). No scripting: the shell stays a fixed set of commands that can't escape the vault model.

### Success criteria
- An agent or a person can read any version that `history` lists and see what changed between two versions, or since a version, as a diff.
- A person can ask "what did the agent change in my vault this afternoon?" and get an answer in one call.
- Replacing a text in every note of a folder is one preview and one apply, with every note's history kept and nobody's concurrent edit lost.
- A folder can be moved or renamed with one `mv`, links included.
- After a repair in the database, the first access already gives the right answer (phase 5, observation 7).

### Out of scope
Attachments, frontmatter queries (a later step if `rg` turns out not to be enough), undo of a whole session as one operation (Q6 asks whether it should be in), several vaults in one change.

## 2. Proposals

### 2.1 Reading an earlier version

`cat <path> --version <v>` (also with a section: `cat "note.md#Goals" --version <v>`). Prints the version's text with the usual output limit, and no version block, since an earlier version can't be edited, only restored.

Why not `note.md@<v>`: `@` is allowed in note names, and Obsidian users do use it. An option keeps paths unambiguous.

**Q1:** `--version` on `cat` (and so on `head`, `tail`, `wc`, `rg`?), or only on `cat`? I'd start with `cat`; the others can read it through a pipe (`cat note.md --version a1b2 | rg -n TODO`).

### 2.2 Diff

`diff <path> [<from> [<to>]]`, unified diff with three lines of context, the way `git diff` looks:

- `diff note.md` → the previous version against the current one ("what did the last write change").
- `diff note.md a1b2c3d4e5f6` → that version against the current one ("what changed since I read it").
- `diff note.md a1b2… f6e5…` → two versions.

Header lines name the versions with time and writer from `history`, so it reads like a commit:

```
--- /Demo/plan.md  a1b2c3d4e5f6  2026-10-23 14:02 UTC  dev
+++ /Demo/plan.md  9f8e7d6c5b4a  2026-10-23 14:30 UTC  dev (mv)
@@ -12,3 +12,4 @@
```

The diff is line-based (Myers, as in git). Markdown paragraphs are often single long lines, so one changed word marks the whole line. A word diff inside changed lines (`--word`) would help; **Q2:** in v1 or later?

Two notes: `diff <path> <path>` compares two notes. Cheap to add with the same code; it helps with near-duplicates. Proposal: in.

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

A new MCP tool `replace`, not an `sh` command, since content does not go through `sh` (concept §5). Two calls:

1. **Preview:** `replace(path, old_text, new_text)` with `path` a folder, a vault, or a note. Literal text, as in `edit`. It returns per note the number of matches and a short excerpt of the first few (line number, before → after), and a **plan token** at the end.
2. **Apply:** the same call with `plan` set to that token. The token stands for the exact set of notes *and their versions* the preview saw. Each note is changed with its own compare-and-swap, as `edit` does. A note that changed since the preview is skipped and named; the result says `replaced 37 occurrences in 12 notes; skipped 1 changed since the preview: /Demo/x.md (run the preview again)`.

Every changed note gets its own history entry, marked `dev (replace)`, so it doesn't coalesce with earlier writes and can be restored on its own.

Why a preview first: a change over many notes is the one place where an agent's mistake is expensive to undo by hand. The preview costs one call and shows exactly what will happen; the plan token makes sure what was shown is what is applied.

**Q3:** Literal text only, or regular expressions too (`regex: true`, with `$1` in `new_text`)? Regex makes "rename `#projekt-x` but not `#projekt-xy`" possible (`#projekt-x\b`), and the shell's `rg` already uses a non-backtracking engine. I'd include it, with `\b` in the help as the common case.

**Q4:** A cap on the number of notes per apply (e.g. 200) and on the preview's size, with "narrow the path" when it's exceeded? I'd say yes for the preview (it has to fit the output limit anyway) and no for the apply.

### 2.6 Moving a folder

`mv <dir>/ <target>/` moves every note below the folder, keeping the structure below it, and rewrites links in one pass, as for a single note (the moved notes' own relative links included). Move records are written for every note, so old paths say where they went.

Before anything is written, everything is checked: no target exists, all names are valid, the target isn't inside the source. Then the notes are moved one by one, each atomic. MongoDB on a single node has no transactions, so a failure in the middle (the server stops) leaves part of the folder moved; the move records show which, and `mv` of the rest finishes it. **Q5:** Is that acceptable, or should a folder move write a journal first so it can be completed or rolled back on start? I think it's acceptable for v1: nothing is lost, every note is in exactly one place, and the links of the moved part are already correct.

The trailing `/` on the source is required. It says "I mean the folder", so a mistyped note name never moves a folder. Without it, `mv` on a folder says `Is a directory (to move the folder, write /Demo/a/)`.

`rm` of a folder stays out: deleting many notes at once is easy to get wrong and rarely needed.

### 2.7 Phase 5, observation 7

`GetAsync` and `ReadAsync` in `EncryptedNoteStore` refresh a path that is marked damaged *before* reading; `IsDamagedAsync` then only checks the marker. The first access after a repair returns the note.

## 3. Open questions for Chris

| | Question | Proposal |
|---|---|---|
| Q1 | `--version` only on `cat`, or on every reading command? | Only `cat`; pipes do the rest |
| Q2 | Word diff in v1? | Later, if line diffs of long paragraphs turn out to be hard to read in practice |
| Q3 | Regular expressions in `replace`? | Yes, literal by default |
| Q4 | Caps for `replace`? | Only on the preview's size |
| Q5 | Folder move without a journal? | Yes for v1 |
| Q6 | Undo of a whole session (all writes of one writer since a time) as one command? | Not in step 6: per-note `restore` covers it, and without per-agent keys "one writer" is everyone. Revisit with step 8 |

## 4. Testing

Unit tests in `Kumbu.Core.Tests` for the diff (empty notes, no final newline, CRLF never appears since content is stored with LF, long lines, a version missing from history), the plan token (a note changed, removed or added between preview and apply), `replace_all` with sections, folder moves (nested folders, links in and out of the folder, relative links, a target inside the source, a name collision), and `history` on a directory with `--since`. Then the hands-on test as the agent, and a blind run with a realistic task such as "rename the project Hecate to Selene everywhere in my notes".
