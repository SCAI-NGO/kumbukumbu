# Project memory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the system skill `project-memory`, make start-up skills reach every session, and give agents better general tools for working with notes as memory: `cat`, `rm` and `mv` with several paths, `rg -g`, a message with writes, several replacements per `edit`.

**Architecture:** No memory-specific code. Skills gain a start-up marker in their front matter that the handshake instructions honour in both modes; the shell commands and write tools grow general multi-path, glob and message support, each in its own task and pull request.

**Tech Stack:** C# / .NET 10 (`src/Kumbu.Core`, `src/Kumbu.Server`), xUnit v3 on Microsoft.Testing.Platform, React + Vitest (`web/`).

**Spec:** `docs/superpowers/specs/2026-11-15-project-memory-design.md`

## Global Constraints

- British English in code, comments, docs, commit messages; source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- No memory-specific code in the server: no `memory` command, store or key setting.
- Nothing the server says presumes the person works in projects.
- Error texts read like shell errors, `<command>: <what>`, so a model can act on them.
- Every user-visible tool change updates its `Help` text, `Usage`, and the matching lines of `src/Kumbu.Core/Sh/Manual.cs` and `README.md`.
- One pull request per task group below (Tasks 1–2, 3, 4–5, 6, 7–8, 9); squash merge on green CI only.
- Tests first. Run Core tests with `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class <Namespace.Class>`, server tests with `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class <…>` (needs Docker or `KUMBU_TEST_MONGO`), the web with `pnpm -C web test --run`. Before each pull request: `dotnet test --solution Kumbu.slnx` and `pnpm -C web test --run && pnpm -C web build`.

## Review Focus

1. **A user skill named like a system skill in a shared vault's `_skills/`** must not replace the system skill, in the handshake or in `skills cat`: an instruction-injection route (Task 2 tests it).
2. **`cat` over several long notes** must never cut a note in the middle nor exceed the output limit; each printed note's version token must match what `edit` expects (Task 3 tests both).
3. **`rm`/`mv` with several paths where one fails** (missing, damaged, a folder with notes) must still do the others and report every path; a failed `mv` must leave no half-moved note (Tasks 4, 5).
4. **Glob anchoring:** `rg x /SWE -g '!**/Memory/**'` excludes `/SWE/Kumbu/Memory/a.md`; `-g '*.md'` matches at any depth; `-g 'Kumbu/*.md'` is anchored to the operand, not to `/` (Task 6).
5. **Coalesced writes** (several writes in one changeset collapse into one version) keep the last message given, and a write without a message doesn't erase the version's earlier one (Task 7).

---

### Task 1: Start-up skills in the handshake instructions

**Files:**
- Modify: `src/Kumbu.Core/Skills/SkillManifest.cs` (validate `metadata.kumbu-when`)
- Modify: `src/Kumbu.Core/Skills/Skill.cs:14` (new init property)
- Modify: `src/Kumbu.Core/Skills/SkillDirectory.cs:88,116` (set it from the manifest)
- Modify: `src/Kumbu.Core/Skills/SkillInstructions.cs:13-32`
- Modify: `src/Kumbu.Server/Mcp/HandshakeInstructions.cs:87` (list in both modes)
- Modify: `README.md:383`, `src/Kumbu.Core/Sh/Commands/SkillsCommand.cs` help (one sentence on `kumbu-when`)
- Test: `tests/Kumbu.Core.Tests/Skills/SkillInstructionsTests.cs`, `SkillManifestTests.cs`, `tests/Kumbu.Server.Tests/Mcp/HandshakeInstructionsTests.cs`

**Interfaces:**
- Produces: `Skill.AtStart` (`bool`, init, default `false`); front matter `metadata: { kumbu-when: session-start }` marks a start-up skill (Agent Skills keeps tool-specific keys under `metadata`; Kumbu²'s carry the `kumbu-` prefix).
- Produces: `SkillInstructions.StartHead` (const string, exact copy below).

- [ ] **Step 1: Write the failing tests**

In `SkillInstructionsTests.cs` (helpers `System`, `InVault`, `Entries` exist):
```csharp
private static Skill Start(string name, string description) => System(name, description) with { AtStart = true };

[Fact]
public void List_mode_names_start_up_skills_first_under_their_own_head()
{
    var section = SkillInstructions.Section(InstructionsMode.ListAndHint, [System("create-skill", "Write a skill."), Start("project-memory", "Read the memory.")]);
    Assert.StartsWith(SkillInstructions.StartHead + "\n- project-memory (system): Read the memory.", section);
    Assert.Contains("\n- create-skill (system): Write a skill.", section);
    Assert.Single(Entries(section), e => e.StartsWith("project-memory"));
}

[Fact]
public void Hint_only_names_start_up_skills_and_nothing_else()
{
    var section = SkillInstructions.Section(InstructionsMode.HintOnly, [System("create-skill", "Write a skill."), Start("project-memory", "Read the memory.")]);
    Assert.Equal(["project-memory (system): Read the memory."], Entries(section));
    Assert.EndsWith(SkillInstructions.Hint, section);
}

[Fact]
public void Without_start_up_skills_hint_only_is_the_hint_alone() =>
    Assert.Equal(SkillInstructions.Hint, SkillInstructions.Section(InstructionsMode.HintOnly, [System("create-skill", "Write a skill.")]));
```
Change `Hint_only_lists_none` (line 46) into the last test above. Make `Hint` public (it is asserted).

In `SkillManifestTests.cs`:
```csharp
[Fact]
public void Kumbu_when_takes_only_session_start()
{
    var manifest = SkillManifest.Read("x", "---\nname: x\ndescription: d\nmetadata:\n  kumbu-when: always\n---\n");
    Assert.Contains("metadata kumbu-when must be session-start", manifest.Problems);
}
```
In `HandshakeInstructionsTests.cs`, next to `Hint_only_sends_no_list` (line 151): `Hint_only_names_the_system_start_up_skill` — a hint-only key's instructions contain `- project-memory (system):` and no `- create-skill`. (It passes only after Task 2 adds the skill; write it now, expect it to fail until then.)

- [ ] **Step 2: Run the Core tests and see them fail**

Run: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class Kumbu.Core.Tests.Skills.SkillInstructionsTests`
Expected: FAIL (`AtStart`, `StartHead` don't exist).

- [ ] **Step 3: Implement**

- `SkillManifest.Read`: if `Metadata` holds `kumbu-when` with any value but `session-start`, add the problem `metadata kumbu-when must be session-start`.
- `SkillDirectory` lines 88 and 116: set `AtStart = manifest.Metadata.TryGetValue("kumbu-when", out var w) && w == "session-start"`.
- `SkillInstructions`:
  ```csharp
  public const string StartHead = "Skills for the start of every session: read each with sh `skills cat <name>` and follow it before your first answer, even to a greeting, alongside any start-up steps of your client or the project:";
  ```
  `Section`: start-up skills first under `StartHead` (one `skill.Line(MaxDescription)` each), then, in list mode, `ListHead` and the other skills; in hint mode, `Hint` after the start-up lines (or `Hint` alone when there are none). `MaxEntries` counts both groups together.
- `HandshakeInstructions.cs:87`: list in both modes (`await ListAsync(...)` always); `Section` decides what to show.
- README line 383: "**Hint only**, which says only that skills exist and to run `skills find` … before a task" gains "; skills marked for the start of a session (`kumbu-when: session-start` under `metadata`) are named in both modes".

- [ ] **Step 4: Run the tests and see them pass**

Run: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace Kumbu.Core.Tests.Skills`
Expected: PASS. The server test stays red until Task 2.

- [ ] **Step 5: Commit**

```bash
git add src/Kumbu.Core/Skills src/Kumbu.Server/Mcp/HandshakeInstructions.cs src/Kumbu.Core/Sh/Commands/SkillsCommand.cs README.md tests
git commit -m "Name start-up skills in the handshake instructions, in both modes"
```

### Task 2: The system skill `project-memory`, and who may replace a system skill

**Files:**
- Create: `src/Kumbu.Core/Skills/System/project-memory/SKILL.md` (picked up by `Kumbu.Core.csproj:12`)
- Modify: `src/Kumbu.Core/Skills/SkillDirectory.cs` (new `Effective`)
- Modify: `src/Kumbu.Server/Mcp/HandshakeInstructions.cs:102-117`, `src/Kumbu.Core/Sh/Commands/SkillsCommand.cs:124-154` (use `Effective`)
- Modify: `README.md` (the skills section: the new system skill, and replacing it)
- Test: `tests/Kumbu.Core.Tests/Skills/SystemSkillsTests.cs`, `SkillDirectoryTests.cs`, `tests/Kumbu.Core.Tests/Sh/SkillsCommandTests.cs`

**Interfaces:**
- Consumes: `Skill.AtStart`, `kumbu-when` (Task 1).
- Produces: `public static IReadOnlyList<Skill> SkillDirectory.Effective(IReadOnlyList<Skill> skills)` — drops a system skill when a skill of the same name lies in the person's own Skills vault (its `Folder` is `/<vault>/<name>`, directly under a vault root, as `InAsync` line 96 places them; a `/<vault>/_skills/<name>` never replaces one).

- [ ] **Step 1: Write the failing tests**

`SystemSkillsTests.cs`:
- `Project_memory_is_a_start_up_skill`: `SystemSkills.Find("project-memory")` is not null, and the listed `Skill` has `AtStart == true`.
- `Project_memory_presumes_no_project`: its SKILL.md contains `"Without \`project:\`"` and `"project memories are optional"`, and contains neither `"kumbukumbu"` nor `"Chris"`.
- `Every_system_skill_follows_the_rules` (line 24) covers its front matter.
- `Project_memory_stays_short`: `content.Length <= 2400` (the text below is about 2,150 characters, some 540 tokens; spec part 1 asks for about 500).

`SkillDirectoryTests.cs`:
- `A_skill_in_the_Skills_vault_takes_the_place_of_the_system_skill`: store with `/Skills/project-memory/SKILL.md`; `Effective(await ListAsync(null))` has exactly one `project-memory`, with `Folder` `/Skills/project-memory`.
- `A_skill_in_a_vaults_skills_folder_never_replaces_a_system_skill`: store with `/Garden/_skills/project-memory/SKILL.md` (a shared vault, see `Fakes/SharedVaults.cs`); `Effective(...)` keeps the system one and the vault one (both listed; the system one first).

`SkillsCommandTests.cs`: `A_built_in_skill_has_an_address_of_its_own` (191–202) changes: with `/Skills/create-skill`, `skills cat create-skill` prints the user's, `skills cat system:create-skill` the built-in; with `/Work/_skills/create-skill` instead, `skills cat create-skill` still answers `names 2 skills` as today.

- [ ] **Step 2: Run and see them fail**

Run: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace Kumbu.Core.Tests.Skills`
Expected: FAIL (`Find("project-memory")` is null, `Effective` doesn't exist).

- [ ] **Step 3: Write the skill (exact copy)**

```markdown
---
name: project-memory
description: Read and keep the person's memory, notes about who they are, how you work together and, if they work in projects, where each one stands. Use at the start of every session, before your first answer; and when you learn something worth keeping for later sessions, such as a decision and its reason, a correction or a preference.
metadata:
  kumbu-when: session-start
---
# Memory

A memory is a folder with a `MEMORY.md`, the index (one line per note: `- [Title](note.md) — what it holds`), and its notes. Without `project:` in its front matter, it is the person's own memory and holds everywhere. With `project: <name>` it belongs to one project; project memories are optional.

## At the start

1. `rg --files | rg "/MEMORY\.md$"` lists the memories; `rg -n "^project: " | rg "/MEMORY\.md:"` shows their projects. None: carry on without.
2. Read every memory without `project:` whole: `MEMORY.md` and its notes.
3. If you work in a project (in a code session, the repository's name; otherwise what the conversation is about) and a memory has its `project:`, read its `MEMORY.md`, then the notes your task touches. Unsure which: ask in one line.
4. Use what you read; don't recite it.

## Remembering

- Keep decisions with their reasons, the person's corrections and preferences, where things live, a project's state that outlasts the day. Not day plans, not what the notes already say, never secrets.
- Write into the note of its topic (`cat` it, then `edit` with its version token); a new topic gets a note and a line in `MEMORY.md`. On a version conflict, read again and redo.
- With no memory yet, offer to start one when the person wants something kept, or when they work in a project that has none while others have one. Ask where. Never offer unasked at every start.
- Say in one line what you remembered.

## Keeping it small

Past about fifteen index lines, or when notes repeat or contradict each other, offer to consolidate: merge by topic, drop what is done, keep the index current. Removed notes stay in the trash for 30 days. In a vault shared with others, its members read the memory too.
```

- [ ] **Step 4: Implement `Effective` and use it** in `HandshakeInstructions.ListAsync` and in `SkillsCommand.FindAsync` (line 147) before matching by name. `skills ls` keeps listing both.

- [ ] **Step 5: Run the tests and see them pass**

Run: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-namespace Kumbu.Core.Tests.Skills` and `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class Kumbu.Server.Tests.Mcp.HandshakeInstructionsTests`
Expected: PASS, Task 1's server test included.

- [ ] **Step 6: Commit and open the pull request for Tasks 1–2** ("Ship the project memory skill and name start-up skills in the instructions"). The maintainer reviews the skill's wording there.

### Task 3: `cat` with several paths

**Files:**
- Modify: `src/Kumbu.Core/Sh/ShellResult.cs` (new `Versions`)
- Modify: `src/Kumbu.Core/Sh/Commands/Cat.cs:16-80` (usage, help, multi-path)
- Modify: `src/Kumbu.Server/Mcp/ToolResult.cs:21-25` (one block per version)
- Modify: `src/Kumbu.Core/Sh/Manual.cs` (`cat` line in `ToolDescription`)
- Test: `tests/Kumbu.Core.Tests/Sh/CatTests.cs`, `tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs`

**Interfaces:**
- Produces: `ShellResult.Versions` (`IReadOnlyList<NoteVersion>?`, init) — set only when a result carries more than one note; `Version` stays the single-note field.

Behaviour (decided here): each path prints as `==> <path> <==` then its text, a blank line between; per path, a section address works as today; an attachment prints its one-line description; a path that fails prints its error line in place (`cat: /x.md: No such file or directory`) and the others go on; the result is an error only when every path failed. Notes are printed whole in the order given while they fit the remaining budget (`OutputLimit.MaxChars` minus 200); a note that doesn't fit prints its outline (`OutlineCommand.Render`) under its header, marked `[outline only: too long for this call; read it alone or by section]`. `--version` with more than one path is refused: `cat: --version takes one path`. No embeds block with several paths. One path behaves exactly as today (`Shell.cs:142-147` untouched).

- [ ] **Step 1: Write the failing tests** in `CatTests.cs`:
  - `Several_notes_print_under_headers_with_a_version_each`: `/Demo/a.md` "one\n", `/Demo/b.md` "two\n" → output `"==> /Demo/a.md <==\none\n\n==> /Demo/b.md <==\ntwo\n"`, `Versions` equal to both `NoteVersion`s with `VersionToken.Of` of each content, `Version` null.
  - `A_missing_path_is_reported_in_place_and_the_rest_printed`: `cat /Demo/a.md /Demo/nope.md` → not an error, output contains `cat: /Demo/nope.md: No such file or directory` and `one`.
  - `All_paths_missing_is_an_error`.
  - `A_note_past_the_budget_prints_its_outline`: two notes of 15,000 chars each with headings → the second prints `[outline only:`; output length ≤ `OutputLimit.MaxChars`.
  - `Version_takes_one_path`.
  In `McpClientTests.cs`: `Cat_with_two_paths_sends_two_version_blocks` (two `version of …:` text blocks).
- [ ] **Step 2: Run and see them fail** — `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class Kumbu.Core.Tests.Sh.CatTests`; Expected: FAIL (`cat: too many arguments`).
- [ ] **Step 3: Implement** in `Cat.RunAsync` (remove the line-58 refusal; one path keeps the current code path) and `ToolResult.From` (each of `Versions` as `\nversion of {path}: {token}`). Usage becomes `cat [--version V] <path>[#section] [<path>…]`.
- [ ] **Step 4: Run and see them pass** (same command, then the server class `Kumbu.Server.Tests.Mcp.McpClientTests`).
- [ ] **Step 5: Commit and open the pull request** ("Let cat print several notes in one call").

### Task 4: `rm` with several paths

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/Rm.cs:12-40`
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs:293`, `src/Kumbu.Core/Attachments/AttachmentWriter.cs` (`RemoveFolderAsync` error text)
- Test: `tests/Kumbu.Core.Tests/Sh/WriteCommandsTests.cs:46`, `tests/Kumbu.Core.Tests/Notes/NoteWriterTests.cs:446`

Behaviour: each path is removed on its own with today's dispatch (extract it as `private Task<ShellResult> RemoveOneAsync(VaultPath path, CancellationToken ct)`); output is one line per path in order, success or error; the result is an error when any path failed, but the others are done. Error texts lose "one note at a time": `rm: {path}: Is a directory (rm doesn't remove folders with notes in them; there is no -r)` and `rm: {folder}: Is a directory holding N entries (rm removes notes, files and empty folders; there is no -r)`.

- [ ] **Step 1: Write the failing tests** in `WriteCommandsTests.cs`: drop the `InlineData` at line 46; add `Rm_removes_each_path_and_reports_each` (`rm /Demo/a.md /Demo/nope.md "/Demo/Meeting Notes.md"` → `IsError` true, both existing notes in the trash, three lines, the middle one `rm: /Demo/nope.md: No such file or directory`). Update the expected text at `NoteWriterTests.cs:446`.
- [ ] **Step 2: Run and see them fail** — `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class Kumbu.Core.Tests.Sh.WriteCommandsTests`.
- [ ] **Step 3: Implement**; usage `rm <path> [<path>…]`, help example `rm /Demo/old.md /Demo/older.md`.
- [ ] **Step 4: Run and see them pass**, then `--filter-namespace Kumbu.Core.Tests.Sh` (DamageTests, FolderCommandTests, AttachmentCommandTests must stay green).
- [ ] **Step 5: Commit** ("Let rm remove several paths in one call").

### Task 5: `mv` with several sources

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/Mv.cs:13-66`
- Test: `tests/Kumbu.Core.Tests/Sh/FolderMoveTests.cs`, `tests/Kumbu.Core.Tests/Notes/NoteMoverTests.cs`

Behaviour: with more than one source the target must end in `/`, else `mv: with several sources the target must be a folder, ending in '/'`; each source moves on its own, in the order given, with today's single-move call (`MoveFolderAsync` for a source ending in `/`, else `MoveAsync(source, target.Child(source.Name), …)`), so each keeps its all-or-nothing commit with its link rewrites; one line per source; the result is an error when any failed. `--keep-links` applies to all. Two sources remain today's `mv <path> <target>`.

- [ ] **Step 1: Write the failing tests**:
  - `Several_notes_move_into_a_folder_and_their_links_follow`: `/W/a.md` links `[[b]]`, `/W/b.md`, `/W/c.md` links `[b](/b.md)`; `mv /W/a.md /W/b.md /W/Old/` → both under `/W/Old/`, `c.md` now links `/Old/b.md`, `a.md`'s link still resolves to `b`.
  - `A_failing_source_leaves_the_others_moved` (one missing source → error result, the others moved, nothing half-moved).
  - `Several_sources_need_a_folder_target`.
  - `Notes_and_folders_mix` (`mv /W/a.md /W/Sub/ /W/Old/`).
- [ ] **Step 2: Run and see them fail** — `--filter-class Kumbu.Core.Tests.Sh.FolderMoveTests`.
- [ ] **Step 3: Implement**; usage `mv [--keep-links] <path>… <target>`; help states the per-source rule.
- [ ] **Step 4: Run and see them pass**, then `--filter-namespace Kumbu.Core.Tests.Notes` and `Kumbu.Core.Tests.Sh`.
- [ ] **Step 5: Commit and open the pull request for Tasks 4–5** ("Let rm and mv take several paths").

### Task 6: `rg -g` / `--glob`

**Files:**
- Create: `src/Kumbu.Core/Sh/Glob.cs`
- Modify: `src/Kumbu.Core/Sh/Commands/Rg.cs:36-44` (spec), `:52-103` (help), `:178-230` (`SearchNotesAsync`), `:643-673` (`ListFilesAsync`)
- Test: `tests/Kumbu.Core.Tests/Sh/GlobTests.cs` (new), `tests/Kumbu.Core.Tests/Sh/RgTests.cs`

**Interfaces:**
- Produces: `public sealed class Glob` with `public static bool TryParse(string pattern, out Glob? glob, out string? error)`, `public bool Excludes { get; }` (pattern started with `!`), `public bool Matches(VaultPath path, VaultPath root)`.

Decided here: our own matcher, no library. `Microsoft.Extensions.FileSystemGlobbing` works on directory trees and its rules differ from ripgrep's, which the spec requires. Rules: `*` and `?` don't cross `/`; `**` does (`**/` also matches nothing); `[…]` and `[!…]` as classes; a leading `/` anchors to `root` like a pattern with `/`. A pattern without `/` is tested against each segment of the path below `root` when it excludes (so `-g '!Memory'` drops everything under a `Memory` folder, as ripgrep prunes it) and against the file name alone when it includes. A pattern with `/` is tested against the path relative to `root`. A path is kept when it matches at least one include (or there are none) and no exclude. Translate each pattern to a `Regex` with `RegexOptions.NonBacktracking | RegexOptions.CultureInvariant`, as `Rg.cs:710` does. A bad class (`[` unclosed) is `rg: bad glob '<pattern>': unclosed [`.

- [ ] **Step 1: Write the failing tests** in `GlobTests.cs` (root `/SWE` unless stated):
  - `!**/Memory/**` excludes `/SWE/Kumbu/Memory/a.md`, keeps `/SWE/Kumbu/a.md`.
  - `!Memory` excludes `/SWE/Kumbu/Memory/a.md`.
  - `*.md` includes `/SWE/Kumbu/Ideas/x.md`, not `/SWE/x.pdf`.
  - `Kumbu/*.md` includes `/SWE/Kumbu/a.md`, not `/SWE/Kumbu/Ideas/a.md`, not `/SWE/Other/Kumbu/a.md`.
  - `Kumbu/**` includes `/SWE/Kumbu/Ideas/a.md`.
  - `?.md` includes `/SWE/a.md`, not `/SWE/ab.md`; `[ab].md` includes `/SWE/b.md`; `[!ab].md` excludes nothing but includes `/SWE/c.md` only.
  - root `/`: `SWE/Kumbu/**` includes `/SWE/Kumbu/a.md`.
  - `TryParse("[ab")` fails with `unclosed [`.
  In `RgTests.cs`: `Glob_excludes_a_folder_from_the_search` (`rg Kumbu /SWE -g '!**/Memory/**'`), `Glob_narrows_files` (`rg --files /SWE -g '*.pdf'`), `Several_globs_combine`, `Glob_on_piped_input_is_refused` (`cat /SWE/a.md | rg -g '*.md' x` → `rg: --glob filters paths and can't be used on piped input`).
- [ ] **Step 2: Run and see them fail** — `--filter-class Kumbu.Core.Tests.Sh.GlobTests`.
- [ ] **Step 3: Implement `Glob`**, then add `new('g', "glob", TakesValue: true)` to `Spec`, parse all `options.Values("glob")` up front, and filter with each operand's `path` as `root` beside the `ArchiveFolder.Contains` filters (`Rg.cs:223,227` and `:659-673`; in the `--tag` branch use its single scope as root). Help gains `-g, --glob GLOB  only paths matching GLOB, or not matching !GLOB (ripgrep's rules; repeatable)` and the example `rg -n Kumbu /SWE -g '!**/Memory/**'`.
- [ ] **Step 4: Run and see them pass**, then `--filter-class Kumbu.Core.Tests.Sh.RgTests` and `RgPdfTests`.
- [ ] **Step 5: Commit and open the pull request** ("Let rg filter paths by glob, with ripgrep's rules").

### Task 7: A message with a write, in Core and in the sealed history

**Files:**
- Modify: `src/Kumbu.Core/Notes/StoredNote.cs:79,90` (`Message` on `HistoryEntry` and `StoredNote`)
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs:33-75,82,141,425` (carry and coalesce it)
- Modify: `src/Kumbu.Core/Notes/Replacer.cs:24,225`
- Modify: `src/Kumbu.Core/Notes/NoteHistory.cs:7,16`, `src/Kumbu.Core/Sh/Commands/History.cs:98-101,265`
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs:592,599,605-609,712` (payloads)
- Test: `tests/Kumbu.Core.Tests/Notes/NoteWriterTests.cs`, `tests/Kumbu.Core.Tests/Sh/WriteCommandsTests.cs`, `tests/Kumbu.Server.Tests/Vaults/` (the existing sealing round-trip test class for notes)

**Interfaces:**
- Produces: `HistoryEntry(..., string? Message = null)`, `StoredNote(..., string? Message = null)` (last positional parameters); `NoteVersionEntry(..., string? Message = null)`.
- Produces: optional trailing `string? message = null` on `NoteWriter.WriteAsync`, `NoteWriter.EditAsync`, `NoteWriter.Next`, `Replacer.RunAsync`. Other callers stay as they are and pass none.
- Produces: `public static bool NoteWriter.TryMessage(string? message, string tool, out string? clean, out ShellResult? error)` — trims; refuses a newline or more than 200 characters: `{tool}: message is one line of at most 200 characters`.

Rules: `Next` writes `current.Message` into the history entry it creates and puts the new message on the new state. When the write coalesces (`NoteWriter.cs:71`), the state keeps `message ?? current.Message`. Payloads: `[property: JsonPropertyName("msg"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null` on `HistoryPayload` and `NotePayload`, mapped both ways. `history` prints the message after the line: `<token>  <time>  <writer>  <n> lines  — <message>`, before `(current)`.

- [ ] **Step 1: Write the failing tests**:
  - `A_write_message_reaches_history` (write with "consolidated", edit with "tidied" → `history` shows both on their versions).
  - `A_coalesced_write_keeps_the_last_message` and `A_coalesced_write_without_message_keeps_the_earlier_one` (two writes in one changeset).
  - `A_message_is_one_line` (newline → error, 201 chars → error, nothing written).
  - `Replace_gives_its_message_to_every_note`.
  - Server: sealing round-trip keeps `Message` on the note and its history; a note sealed before this change (no `msg`) reads back with `Message` null.
- [ ] **Step 2: Run and see them fail** — `--filter-class Kumbu.Core.Tests.Notes.NoteWriterTests`.
- [ ] **Step 3: Implement** as above.
- [ ] **Step 4: Run and see them pass**; then the server vault tests.
- [ ] **Step 5: Commit** ("Keep a message with a write in the note's history").

### Task 8: The message in the tools and in the web app's history

**Files:**
- Modify: `src/Kumbu.Server/Mcp/WriteTools.cs:16-45` (`message` parameter on all three)
- Modify: `src/Kumbu.Core/Sh/Manual.cs:67,81,121` (one sentence each)
- Modify: `src/Kumbu.Server/Api/NoteApi.cs:194,844` (`HistoryEntryView.Message`)
- Modify: `web/src/api/schema.d.ts:12284` (`message?: string | null`, by hand or `pnpm -C web gen:api`), `web/src/note/HistorySheet.tsx:34-40,114-115`
- Test: `tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs`, `web/src/shell/HistorySheets.test.tsx`

**Interfaces:**
- Consumes: Task 7's `message` parameters and `NoteVersionEntry.Message`.

The tool parameter: `[Description("Optional: one line on why, kept with this version in the note's history (at most 200 characters)")] string? message = null`. The web history shows the message in the list row after the writer (`by {writer} — {message}`) and in the detail.

- [ ] **Step 1: Write the failing tests**: `Write_with_a_message_shows_it_in_history` over MCP (`write` then `sh history`); Vitest `shows a version's message in the history list` with a `HistoryEntryView` fixture carrying `message: "consolidated"`.
- [ ] **Step 2: Run and see them fail** — server class `Kumbu.Server.Tests.Mcp.McpClientTests`; `pnpm -C web test --run HistorySheets`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and see them pass**, then `pnpm -C web build`.
- [ ] **Step 5: Commit and open the pull request for Tasks 7–8** ("Let agents say why they wrote, in the note's history").

### Task 9: Several replacements in one `edit`

**Files:**
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs:141-241` (extract one replacement, fold over a list)
- Modify: `src/Kumbu.Server/Mcp/WriteTools.cs:25-35` (`edits`; `new_text` optional)
- Modify: `src/Kumbu.Core/Sh/Manual.cs:81`
- Test: `tests/Kumbu.Core.Tests/Notes/NoteWriterTests.cs`, `tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs`

**Interfaces:**
- Produces: `public sealed record TextEdit(string OldText, string NewText);` in `src/Kumbu.Core/Notes/NoteWriter.cs`.
- Produces: `public Task<ShellResult> EditManyAsync(string pathText, string version, IReadOnlyList<TextEdit> edits, CancellationToken ct, string? message = null)`.
- The MCP `edit` gains `[Description("Several replacements on the same version, applied in order, all or none: a list of {old_text, new_text}; each old_text must occur exactly once (in the section, if the path has one) when its turn comes. Instead of old_text and new_text")] TextEdit[]? edits = null`; `new_text` becomes `string? new_text = null`. Neither given: `edit: new_text or edits is needed`; both: `edit: give new_text or edits, not both`; `replace_section` or `replace_all` with `edits`: `edit: edits takes no replace_section or replace_all`. At most 64 edits: `edit: at most 64 edits in one call`.

Rules: extract `NoteWriter.cs:204-235` (one exact replacement within the scope) into a private pure method returning the new content or the error, and fold the list over the current content; the first error returns at once, naming the edit: `edit: edit 2 of 3: old_text not found in /W/a.md` (the existing texts, prefixed `edit N of M: `). One `ReplaceAsync` at the end: one version. Message `edited {path}: N changes`.

- [ ] **Step 1: Write the failing tests**:
  - `Several_edits_apply_in_order_as_one_version` (two edits, the second's `old_text` created by the first → applied; `history` grows by one).
  - `A_failing_edit_changes_nothing` (second `old_text` absent → error `edit 2 of 2:`, content and version unchanged).
  - `Edits_stay_in_the_section` (path with `#Section`, an `old_text` that occurs outside the section only → error).
  - MCP: `Edit_takes_a_list_of_replacements`; a client sending only `old_text`/`new_text` works as before.
- [ ] **Step 2: Run and see them fail** — `--filter-class Kumbu.Core.Tests.Notes.NoteWriterTests`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run and see them pass**; then the full suite: `dotnet test --solution Kumbu.slnx`.
- [ ] **Step 5: Commit and open the pull request** ("Let one edit make several replacements on the same version").

## After each merge

Claude adds the pull request's entry to the version's changelog in Kumbu² (`/SWE/Kumbu/Versions/0.6.0.md`) and ticks its item in `/SWE/Kumbu/Internal Roadmap/0.6.0.md`; a cloud run without that connector names the entry in its report instead.

## Acceptance (from the spec)

After Tasks 1–2 are on staging: a fresh session in Claude Code in a repository with its own start-up protocol, in claude.ai without a project, and in one non-Claude client, each with no prompt about memory, reads the memory before its first answer. A session that works without any project reads the person's memory and offers nothing unasked.
