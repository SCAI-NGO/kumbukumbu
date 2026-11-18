# `ls -l`, console shortcuts and peek Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ls -l`/`-a` in `sh`; console shortcuts `l`, `la`, `lsa` and the console command `p <note>`.

**Architecture:** `Ls.cs` gains the two flags; the console rewrites the shortcuts in `run.ts` before sending and answers `p` by `cat` plus `MarkdownView` in a new output kind.

**Tech Stack:** C# (.NET 10, xUnit) in `src/Kumbu.Core`; React/Vitest in `web/src/console`.

**Spec:** `docs/superpowers/specs/2026-11-18-console-shortcuts-and-peek-design.md`

## Global Constraints

- British English; SPDX headers on new files; match surrounding comment density.
- Time in `-l` in the format `history` already prints (find it in `src/Kumbu.Core/Sh/Commands/History.cs`), UTC.
- Usage string exactly `ls [-l] [-a|--archive] [path]`; the manual line in `src/Kumbu.Core/Sh/Manual.cs:10` uses the same usage and keeps the column alignment of its neighbours.
- No shortcuts or `p` in `sh`. The console never writes.
- Tests: `dotnet test --solution Kumbu.slnx` (4 skip without the model), `pnpm -C web test --run` (2 SkillPage failures are environment-only), `pnpm -C web build`.

## Review Focus

1. `ls -l` on a vault of thousands of notes stays quick (measured, figures in the spec). → Task 1.
2. `l /Work | rg md` keeps its pipe; `cat x | l` is not rewritten. → Task 2.
3. A peeked note with a `<script>` or an `onerror` in raw HTML renders as safely as in the note view (same renderer, same sanitising). → Task 2.
4. `p` on an attachment, a folder or a missing note: a clear error, no crash. → Task 2.
5. A link in a peeked note to a note in a discreet vault behaves as the same link in the note view. → Task 2.

---

### Task 1: `ls -l` and `ls -a`

**Files:** Modify `src/Kumbu.Core/Sh/Commands/Ls.cs`, `src/Kumbu.Core/Sh/Manual.cs:10`; tests in `tests/Kumbu.Core.Tests/Sh/` (Ls tests, `HelpTests.cs:52`, `ShellTests.cs:139`), `tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs:49`; README if it shows `ls` usage.

- [ ] **Step 1: failing tests:** `-l` on a folder lists `name  size  time` for notes, `name/  time` (newest below) for folders, attachment summary plus time; `-a` equals `--archive`; `-la`, `-al`, `-l -a` work; `-l` on a note's path; `-l` at the root unchanged; unknown flag `-x` errors as other commands do; usage and manual line updated.
- [ ] **Step 2:** run, see them fail.
- [ ] **Step 3:** implement. Short flags via the existing `Options` parser if it supports short names and clustering; else the smallest extension of it (check `src/Kumbu.Core/Sh/Options.cs` or wherever `Options.TryParse` lives). Sizes and times from the notes read below the folder (`ReadNotesUnderAsync`, or a cheaper reader method if one exists that yields the current version's time without content).
- [ ] **Step 4:** measure `ls -l` on a vault root with 16,000 notes (reuse the graph backend's measurement fixture/test, if one exists — `rg -n 16000|16_000 tests`); write the figure into the spec's `ls` section. Too slow (> ~1 s)? folders show no date, and the spec says so.
- [ ] **Step 5:** full `dotnet test --solution Kumbu.slnx`; commit "Let ls show sizes and times with -l, and the archive with -a".

### Task 2: console shortcuts and peek

> Ruling during review: peek reads the note through the notes API (the note view's query), not `cat`; see the spec's Console section.

**Files:** Modify `web/src/console/run.ts`, `web/src/console/help.ts`, `web/src/console/store.ts` (a new `Output` kind for a peeked note), `web/src/console/ConsolePage.tsx` (render it); tests in `web/src/console/ConsolePage.test.tsx` and/or a new `run.test.ts`.

- [ ] **Step 1: failing tests:**
  - `l`, `la`, `lsa` (alone, with a path, with a pipe) send `ls -l`, `ls -a`, `ls -la` with the rest kept, and the `→` line shows it; not rewritten inside a later stage or as an argument.
  - `p note.md` (relative to cwd) renders the note: a heading as a heading, a link as a link; clicking the link navigates to the note with replace and the console window closes; task boxes are disabled; a Mermaid block shows as code; raw `<script>`/`onerror` are not executed (same assertions as the note view's sanitising tests); `p` alone, on a folder, on an attachment, on a missing note: an error line with the word "Error:".
  - `help` lists `l`, `la`, `lsa`, `p`; `help p` prints peek's help.
  - In Green, the peeked note's container carries the phosphor colour variables (as the rest of the output).
- [ ] **Step 2:** run, see them fail.
- [ ] **Step 3:** implement. `p` resolves its path like other console paths, calls `sh` with `cat <path>`, strips the version line, and stores `{ kind: "note", path, source }`; render with `MarkdownView` (`web/src/note/MarkdownView.tsx`) with `tasksDisabled`, links resolved as the note view does for that path, Mermaid as code (check how MarkdownView decides on Mermaid and pass the prop that keeps it as code, or wrap). Links leaving the window use `replace={useInWindow()}` semantics as elsewhere in windows.
- [ ] **Step 4:** full web suite and build; commit "Add l, la, lsa and peek to the console".
