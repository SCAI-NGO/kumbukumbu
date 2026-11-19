# Console autocomplete Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tab completion in the console on the desktop, a suggestion row on the phone.

**Architecture:** A pure completion function over line, cursor, working directory and a folder lister; a small cache of `ls` answers per folder; the console page wires Tab (desktop) and a chip row (phone) to it.

**Tech Stack:** React, Vitest, in `web/src/console/` only.

**Spec:** `docs/superpowers/specs/2026-11-18-console-autocomplete-design.md`

## Global Constraints

- British English; SPDX headers on new files; match surrounding comment density.
- No server change; folder entries come from `sh` `ls <folder>` (vaults: `ls`) through the console's existing `consoleCall`.
- Cache per folder for the console's session, refreshed after any command ran and at most every 10 s; a failing `ls` offers nothing.
- Folder/vault completes with `/`, a file with a trailing space; names with spaces or quotes are quoted as `tokenise.ts` reads them; `_archive/` only once `_` is typed; `cd` completes folders and vaults only.
- Desktop no-match flash: the command line's background lights up and fades in about 200 ms (accent colour, phosphor in Green/Amber), no movement, no sound, no output line.
- Phone chips: one scrollable line above the input, 48 px high targets, tap completes and keeps focus; hidden when the input is empty, while running, or with no match; in Green/Amber outlined in phosphor.
- Tests: `pnpm -C web test --run` (2 SkillPage failures are environment-only), `pnpm -C web build`.

## Review Focus

1. A vault or folder name with spaces, quotes or `#` completes into a line the tokeniser reads back as the same path. → Task 1.
2. Completing in a discreet vault's folder: nothing of it stays in the cache once the vault hides (the console resets on hide; the cache must reset with it). → Task 1.
3. Tab with the command running or an empty line does nothing harmful. → Task 2.
4. Typing fast doesn't fire one `ls` per keystroke. → Task 1.
5. The chip row never covers the output's last line on the phone. → Task 2.

---

### Task 1: completion logic and folder cache

**Files:** Create `web/src/console/complete.ts`, `web/src/console/complete.test.ts`, `web/src/console/entries.ts` (the cache), its test.

**Interfaces:**
- Produces: `complete(line: string, cursor: number, cwd: string, list: (folder: string) => Promise<string[] | null>): Promise<{ replaceFrom: number; replaceTo: number; matches: string[]; insert: string | null }>` — `matches` are display names (folders with `/`), `insert` the text to put in place of `[replaceFrom, replaceTo)` (the full match or the common prefix, quoted as needed), null when nothing to insert. `listFolder(folder: string): Promise<string[] | null>` in `entries.ts`, cached; `forgetEntries(): void` called after each command and on the console's reset.

- [ ] **Step 1: failing tests** for every rule in the spec's "What completes" (unique match, common prefix, quoted names, relative `..`/`.`, `cd` folders only, first word commands incl. after a pipe, `_archive/` only when typed, vaults at `/`), plus the cache: one `ls` per folder, refreshed after `forgetEntries()`, at most every 10 s, null on failure, cleared on the console's reset (Review Focus 2).
- [ ] **Step 2:** run, see them fail.
- [ ] **Step 3:** implement. Parse `ls` output lines: an entry is the first field (attachments carry a summary after two spaces; vault lines may carry "(shared by …)"). Command list: the console's own plus a fixed list of `sh` commands kept next to `help.ts`.
- [ ] **Step 4:** run, see them pass; commit "Complete paths and commands in the console".

### Task 2: Tab, the flash and the phone's suggestion row

**Files:** Modify `web/src/console/ConsolePage.tsx`, `web/src/console/run.ts` (call `forgetEntries` after a command), `web/src/index.css` (flash keyframe), `web/src/console/ConsolePage.test.tsx`.

**Interfaces:** Consumes `complete`, `listFolder`, `forgetEntries` from Task 1.

- [ ] **Step 1: failing tests:** desktop Tab completes (unique, prefix), a second Tab with nothing new lists matches as one output line without running anything, no match sets the flash class and removes it again, Shift+Tab not intercepted, Tab ignored while running; phone: chips appear for a path word, tap completes and keeps focus, hidden when empty/running/no match, the log's bottom padding grows with the row; Green chips carry the phosphor outline.
- [ ] **Step 2:** run, see them fail.
- [ ] **Step 3:** implement.
- [ ] **Step 4:** full web suite and build; commit "Let Tab and a suggestion row complete in the console".
