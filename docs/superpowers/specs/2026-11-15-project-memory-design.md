# Project memory — design

Flagship of 0.6.0: **advanced support for operating Kumbu² as a project-oriented memory system.** Any agent, in any client, keeps a memory across sessions as plain notes in the person's vaults: who they are, how they work together, where each project stands. Kumbu² ships the convention as a system skill, and makes its general tools better at the work such a memory needs. Nothing in the server knows what a memory is.

Brief: Kumbu² note `/SWE/Kumbu/Ideas/Project memory 2026-11-15.md` (the maintainer's vault), written by Claude from the session that moved this project's own memory into Kumbu² and consolidated 73 memory notes into 11. The figures below come from that session and from two test sessions run with a draft of the skill the same day.

## Why

- **Claude Code's local memory is tied to one machine.** Sessions on claude.ai, in cloud containers and in other clients (Grok, ChatGPT, Hermes) can't see it. Kumbu² already reaches all of them through MCP.
- **Kumbu² adds what a memory file lacks:** history with who changed what, search by meaning, links that report when they break, and a person who reads and corrects the memory in the web app. The memory stays theirs.
- **`docs/concept.md` says Kumbu² is "not an AI memory system in the MemPalace sense (where the AI decides what to store and where)".** Here the AI does decide what to store, so it happens in the open: as notes the person owns, sees and edits, written by a visible key, every write a version in history.

## Goals

1. A fresh session, in any client with a Kumbu² connection, with no prompt about memory, reads the memory that applies to it before its first answer.
2. It finds and reads that memory in a few calls, not one call per note.
3. It writes what it learnt into the right note, without lost updates when several sessions write at once.
4. It keeps the memory small: stale notes merged or removed in a few calls, reversibly.
5. A person can see what was remembered, when and why.

## Non-goals and anti-goals

- **No memory-specific code in the server.** No `memory` command, no memory store, no memory settings on keys. Decided by the maintainer, because the memory is a convention on the file system and the code only makes the general tools better ("was brauchen wir wirklich?"). An anti-goal: if a change only helps memory, it doesn't belong here.
- **No assumption that a person works in projects.** Kumbu² is also a diary, a hobby notebook, a reading list. Nothing the server says presumes a project; the skill treats project memories as optional.
- No automatic extraction of memories from conversations; no embeddings of chats.
- No change to how any client (Claude Code included) loads its own memory.

## How it works

A **memory** is a folder holding a `MEMORY.md` and its notes. `MEMORY.md` is the index, one line per note (`- [Title](note.md) — what it holds`), under front matter:

```
---
project: kumbukumbu
description: Kumbu², the Markdown second brain served over MCP
---
```

- With `project:`, the memory belongs to that project: in a code session, the repository's name; elsewhere, matched against the conversation by name and description.
- Without `project:`, it is the person's own memory and holds in every session.
- A person may have none, one, or several of each kind; they live wherever the person keeps notes (`/Work/Memory/`, `/Diary/Memory/`, `/SWE/Kumbu/Memory/`).

Finding them needs nothing new: `rg --files | rg "/MEMORY\.md$"` lists them, `rg -n "^project: " | rg "/MEMORY\.md:"` shows which project each belongs to. Both were tried on the maintainer's vaults on 2026-11-15.

## Approaches considered

1. **A server-side memory feature:** a `memory` command, a memory setting per key or connection, the memory index put into the handshake instructions by the server. Precise and fast, but memory-specific code in the server, and on claude.ai one connector serves every project, so the server can't tell which project a session is in.
2. **One credential per project with a fixed memory folder.** Clean, but a person needs a connector per project, which is clumsy on claude.ai and unlike how people use Kumbu².
3. **A convention plus a skill, and better general tools (chosen).** The skill travels in the handshake like every skill, the agent works out its project from its own context, and every improvement to the tools helps all work, not only memory. Chosen by the maintainer: no memory-specific code, and the skill can be read and changed like any note.

## The parts

### 1. The system skill `project-memory`

Shipped beside `create-skill` and `ingest-attachments` (`src/Kumbu.Core/Skills/System/`). Its text starts from the maintainer's skill (`/Skills/project-memory/SKILL.md`, tested 2026-11-15), reworded so it fits people without projects:

- A memory without `project:` is "your memory", read whole in every session; project memories are optional.
- It offers to start a memory only when the person wants something kept, or when they work in a project that has none and their other projects have one. Never at every start, unasked.
- At the start: before the first answer, even to a greeting, and alongside any start-up steps the project's own instructions give (a CLAUDE.md, a status file), which don't replace it. Why: in the second test, a repository's CLAUDE.md with its own start-up protocol won over the skill, and the session read no memory until asked.
- What to keep (decisions with reasons, corrections, preferences, a project's state that outlasts the day, where things live) and what not (day plans, what the notes or the repository already say, secrets). Why: a third of the 73 notes consolidated on 2026-11-15 were day plans gone stale.
- Write into the topic's note rather than a new note per fact; `cat` for the version token, then `edit`; on a conflict read again and redo.
- Tell the person in one line what was remembered.
- Offer to consolidate past about fifteen index lines.
- In a shared vault, members read the memory too.

**The text** is copied from the maintainer's skill and reworded as above; it names no person, customer or employer. It is read at every session start, so it is kept tight: about 500 tokens, against about 900 for the draft. The maintainer reviews the final wording in its pull request, since the text is the feature.

A person who wants their own variant copies it (`skills copy project-memory`) and changes the copy. **Only a skill in the person's own vault Skills** (`/Skills/project-memory`) takes the system skill's place, in the handshake list and in `skills cat project-memory`; a `_skills/project-memory` in any vault, shared or not, doesn't. *Claude's call:* every session follows this skill before its first answer, so a member of a shared vault must not be able to swap it for everyone with access to that vault. **Left to the builder:** how a user skill and a system skill of the same name are listed today.

### 2. Start-of-session skills in the handshake instructions

Today the instructions say "Before a task, look for a skill whose description matches it and follow it" (`src/Kumbu.Core/Skills/SkillInstructions.cs:15`). A skill meant for the start of every session has no sentence of its own, and in the second test it lost to a repository's start-up protocol.

- **Add one sentence to the skills section,** in both modes (list and hint): a skill whose description says to use it at the start of a session is read and followed before the first answer, alongside the client's or project's own start-up steps. The wording is general: any start-up skill, not memory.
- **In "Hint only" mode, start-up skills are named too,** one line each as in the list, while task skills stay unlisted. Decided by the maintainer, because otherwise a hint-only key never meets its memory (`skills find` with a greeting finds nothing), and the cost is about 100 tokens per session, in the cached prefix ("bei 5000 hätte ich nein gesagt, aber bei 100…"). README's description of the setting changes with it.
- **Credentials that can read nothing the skill would find** still get the sentence. *Claude's call:* the skill's two `rg` calls find no memory and it stops there; filtering by rights would add code for a harmless case.
- **The mark counts only for system skills and for skills in the person's own vaults that no other user changed** (`SharedBy is null && !ByOther`). Any other marked skill, in a vault shared with her or with a file another user last wrote, is an ordinary skill: listed as usual in "List and hint", not named at all in "Hint only", as before this change. Decided by the maintainer after the final review, because it is the boundary `Effective` already keeps for replacing a system skill: a writer of a shared vault must not steer every reader's agent before its first answer, and a start-up skill is a stronger lever than a task-matched one, the more so in "Hint only", which named no shared skill before.
- **List such skills first.** **Left to the builder:** how a skill is recognised as a start-up skill. Options: a front-matter key (`when: session-start`, checked by `skills check`), or the description's wording. A front-matter key is explicit and testable; the description alone needs no new key. Recommended: the front-matter key, since it is unambiguous and the sentence can name it.

### 3. `cat` with several paths

`cat <path>[#section] <path>…` prints each note in turn under a header line, each with its own version token, so a following `edit` has what it needs.

- The output limit (about 8000 tokens per call, `OutputLimit`) applies to the whole call. A note that doesn't fit prints its outline, as a single long note does today; the call never cuts a note in the middle.
- `--version` stays single-path.
- Attachments print their one-line description, as today.
- Why: reading this project's memory took about 75 calls on 2026-11-15; in the first test, two `cat` calls in a row could have been one.

### 4. `rm` and `mv` with several paths

- `rm <path> <path>…` removes each, into the trash as today. Each removal stands alone: one that fails (damaged, not there, a folder with notes) is reported, the others go ahead. One line per path in the result.
- `mv <path> <path>… <folder>/`: several sources into a folder, POSIX-style; with more than one source the target must be a folder. Sources may mix notes, attachments and folders. Links are rewritten as today, `--keep-links` as today.
- *Claude's call:* each source is moved on its own, as with `rm`: one that fails is reported, the others go ahead, one line per path. Each single move keeps today's all-or-nothing with its link rewrites. Moves run in the order given, so a link between two notes moved together ends up pointing at the moved note.
- Why: removing 62 stale memory notes took 62 calls.
- Today's error "rm removes one note at a time" goes. "There is no -r" stays: a folder with notes in it still can't be removed.

### 5. `rg -g` / `--glob`

`rg -g <glob>` includes, `rg -g '!<glob>'` excludes paths, repeatable, as in ripgrep. Also with `rg --files`.

- **Ripgrep's rules**, so agents' habits work (*Claude's call*): `*` and `?` don't cross `/`, `**` does, `[…]` as usual; a glob without `/` matches the file name at any depth (`-g '*.md'`); a glob with `/` is anchored to the path given to `rg` (the root `/` when none is given). So `rg Kumbu /SWE -g '!**/Memory/**'` searches `/SWE` without any memory folder.
- There is no globbing in the code yet. **Left to the builder:** whether `Microsoft.Extensions.FileSystemGlobbing` (MIT, part of .NET) can be made to follow these rules on paths that aren't on a disk, or a small matcher of our own does it more simply.
- Why: excluding the memory folder from a search failed on 2026-11-15 with "unrecognized option"; agents expect ripgrep's options.

### 6. A message with a write

`write`, `edit` and `replace` take an optional `message` (one line, like a commit message: "consolidated from 73 notes"). `history` shows it beside the version; the web app's history shows it too.

- Sealed like the note's other history data, since it is content.
- *Claude's call:* only these three tools. A multi-note `replace` gives its message to every note it changes. `rm` and `mv` take none: a removal is visible in the trash and a move in history, and the reason for a consolidation lands with the `MEMORY.md` edit that goes with it. The web app's own saves take none: a person knows why they wrote.
- When writes coalesce into one version (`NoteWriter.cs:71`), the version keeps its messages. **Left to the builder:** keep the last, or join them.
- Why: the history is an audit log. A person who sees that an agent rewrote a memory note should see why, without reading the conversation.

### 7. Several replacements in one `edit`

`edit` takes a list of `{old_text, new_text}` pairs, applied in order to the same version, all or none.

- Why: on 2026-11-15 two edits sent with the same version token collided, and the second had to be redone after reading the note again.
- **Left to the builder:** the parameter's shape, keeping today's single `old_text`/`new_text` working for clients that send those.

## Order and size

Parts 1 and 2 are the feature; 3 to 7 make it cheap. Suggested order: 2 and 1 together (testable together: a fresh session reads its memory), then 3, 4, 5, then 6 and 7. Each part is a pull request of its own and gets its entry in the version's changelog.

## Testing

- Unit and integration tests per part, written first (xUnit; Vitest where the web app shows the write message).
- **The real test is a session:** after parts 1 and 2 are on staging, a fresh session in each of three clients (Claude Code in a repository with its own start-up protocol, claude.ai without a project, one non-Claude client) with no prompt about memory. Passing: it reads the memory before its first answer. Then a session that works without any project: it reads the person's memory and offers nothing unasked.
