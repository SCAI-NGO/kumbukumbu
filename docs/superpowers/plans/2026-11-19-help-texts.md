# Help texts that say what the code does — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** make `sh help`, the MCP tool descriptions, the error texts and the web console's help say what the code does, and fix the two bugs (§1, §2) and the two small behaviour gaps (§3.9, §3.10) the audit found.

**Architecture:** almost all changes are string constants: each command's `Usage`, `Summary` and `Help` in `src/Kumbu.Core/Sh/Commands/*.cs`, the topics in `Help.cs`, the tool descriptions in `Manual.cs`, the parameter descriptions in `src/Kumbu.Server/Mcp/*.cs`, and `web/src/console/help.ts`. Code changes only in `NoteWriter` (§1, §2), the callers of `AttachmentFormat.ForPath` that must agree with the writers (§2), `LinksCommand` (§3.9) and `Blame` (§3.10).

**Tech Stack:** C# / .NET 10, xUnit v3 on Microsoft.Testing.Platform; React/Vite with Vitest.

**Spec:** `docs/superpowers/specs/2026-11-19-help-texts-design.md` (approved; do not edit). Brief: `docs/superpowers/overnighter/2026-11-19-help-texts-brief.md`. Executors read both. Spec line numbers refer to `master` at `63d3ca46`: find the text, don't trust the number.

## Global constraints

- British English everywhere (colour, behaviour, initialise, licence as the noun).
- No new NuGet or npm package. No change to `src/Kumbu.Server/Auth`, `OAuth`, `Sharing`, `Vaults/EncryptedNoteStore.cs`.
- Help style: short sentences, plain words, examples in the shape the help already uses, no marketing; a help gets longer only by what it was missing; keep the surrounding indentation, lines up to about 120 characters.
- Numbers in a command's `Help`, `Usage` or a topic come from their constant (`NoteWriter.HistoryLimit`, `NoteWriter.TrashRetention`, `AttachmentFormat.MaxBytes`, `Blame.MaxNotes`, …); a plain `"""…"""` help may become `$"""…"""` for that (double any literal `{`/`}` then). `Manual.cs` is the exception: its texts are `public const string`s that feed `[Description]`; keep the number as written and add a test asserting the description contains the constant's value as text.
- The one-line-per-command list in `Manual.ToolDescription` changes only where §3 names `Manual.cs` or a line says something false. §4 adds nothing to it. `HelpTests.cs` (`The_readers_that_hide_the_archive_take_archive`, `ToolDescriptionMentionsEveryCommand`) pins lines of it: keep them true.
- Existing tests asserting old wording are updated to the new wording, never loosened.
- Names in tests: Alice, Bob, Dana, `Home`, `Work`, `Tools`. No customer or employer.
- Commit messages imperative, free-form, saying what changes for the agent or user, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Stage files by name. Subagents commit locally; they never push, merge or switch branches.
- Test commands (MTP runner; `--filter "FullyQualifiedName~…"` doesn't work; "Zero tests ran" is a failure):
  - one class: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*HelpTests"`
  - one test: `dotnet test --project tests/Kumbu.Core.Tests -- --filter-method "*Test_name"`
  - server classes: `--project tests/Kumbu.Server.Tests`
  - one web file: `pnpm -C web test --run src/console/help.test.ts`
- Mutations: after green and the commit, break each new behaviour or claim one at a time (revert the fix line, or restore the old text), run the test, watch it fail, restore with `git checkout -- <file>`. Record each mutation and its failing output for the report. A pure rewording without a test: "none: wording only".

## Review focus

1. **§2 in both directions.** A marked vault of another name (`Tools`) must now write and refuse as `Skills` did; an unmarked vault named `Skills` must now refuse a write of `run.py` exactly as `/Work/run.py` is refused. A write must never create what upload refuses: `write /Tools/x/run.bin`, a text name outside a skill folder (`/Tools/run.py`, the location itself) stay refused. Tests in Task 2 cover `/Tools/run.py` (directly in the vault, not inside a skill's folder) too.
2. **§1 only on success, only with a message.** A failed SVG write (malformed XML, conflict) with a message must answer the error alone, no "message not kept" line; a whitespace-only message (`TryMessage` turns it to null) gets no line. Task 1 tests a malformed SVG with a message.
3. **§3.9 never turns a missing path into a folder.** `/Work/nothing/` in an existing vault, and `/Nope` (no such vault) keep "No such file or directory"; a folder with a marker only (`mkdir`) counts as existing. Task 3 tests the missing folder and the `mkdir` folder.
4. **§3.10 refuses `--lines` only for folders.** `blame --lines /Work/note.md` keeps working; `blame --lines /Work` and `blame --lines /Work/folder/` are refused. Task 4 tests both.
5. **Help strings that became interpolated.** A `$"""` help with a literal `{`/`}` (a JSON or set example) must print unchanged; Task 9/10's `EveryCommandHasHelpThatStartsWithItsName` and the full `help` run catch a format exception, and the implementer greps the changed help for braces.

---

### Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1:** Tools: .NET SDK per `global.json` (roll forward; 10.0.404 present), `DOTNET_CLI_TELEMETRY_OPTOUT=1`; Node 26.13.1 in `/opt/node26`, pnpm 10.32.1 (`npm install -g pnpm@10.32.1`, `/opt/node26/bin` first on `PATH`); Docker running (`docker info`).
- [ ] **Step 2:** `pnpm -C web install --frozen-lockfile`.
- [ ] **Step 3:** `dotnet test --solution Kumbu.slnx` — record totals, passed, skipped, failed per project, duration, and the name of every failure. Planned (laptop): 7,780 / 7,696 passed / 83 skipped / 1 failed in `Kumbu.Server.Tests`.
- [ ] **Step 4:** A failure that is one of the known flaky tests (brief) or any other: run it alone three times with `-- --filter-method "*<name>"`, record each result.
- [ ] **Step 5:** `pnpm -C web test --run` and `pnpm -C web build` — record counts. Planned: 205 files, 4,111 tests, all passed; build passes with a chunk-size warning.

### Task 1: §1 — `write` says when it can't keep a message

**Files:**
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs` (`WriteAsync`, the SVG / text-file branch, about `:128-136`)
- Modify: `src/Kumbu.Server/Mcp/WriteTools.cs` (the `message` parameter's `[Description]`, about `:22`)
- Modify: `src/Kumbu.Core/Sh/Manual.cs` (`WriteDescription`, the `message:` paragraph)
- Test: `tests/Kumbu.Core.Tests/Attachments/SvgWriteTests.cs`, `tests/Kumbu.Core.Tests/Attachments/TextAttachmentTests.cs`, `tests/Kumbu.Core.Tests/Sh/HelpTests.cs`

**Interfaces:** Produces the constant line `message not kept: an SVG's or a skill file's history keeps no messages` (a `private const string` in `NoteWriter` is fine; one place only).

- [ ] **Step 1: Failing tests.**
  - `SvgWriteTests.A_message_on_an_SVG_write_is_not_kept_and_the_answer_says_so`: write `/Work/diagram.svg` (a valid `<svg xmlns="http://www.w3.org/2000/svg"/>`) with `message: "first sketch"`; assert not error, `Output == "created /Work/diagram.svg\nmessage not kept: an SVG's or a skill file's history keeps no messages"`, version not null.
  - Same file, `A_failed_SVG_write_with_a_message_answers_the_error_alone`: write malformed `<svg` with a message; assert error and `DoesNotContain("message not kept")`.
  - Same file, `An_SVG_write_without_a_message_answers_as_before`: no message → output is exactly `created /Work/diagram.svg`; and a whitespace-only message `"  "` → the same (TryMessage normalises it to null; check `TryMessage` first and adjust if it refuses whitespace — then assert what it does).
  - `TextAttachmentTests.A_message_on_a_skill_text_file_write_is_not_kept_and_the_answer_says_so`: `/Skills/pdf/run.py`, replace with version and message → `Output == "wrote /Skills/pdf/run.py\nmessage not kept: …"` (full line).
  - `HelpTests.Write_description_says_an_SVG_and_a_skill_file_keep_no_message`: `Manual.WriteDescription` (whitespace collapsed) contains `kept with this version in the note's history; an SVG's or a skill file's history keeps none`. For `WriteTools`'s parameter description: if `tests/Kumbu.Server.Tests` has a test reading tool descriptions (`rg -n 'WriteTools|Description' tests/Kumbu.Server.Tests/Mcp`), add the same assertion there by reflection on the parameter's `DescriptionAttribute`; otherwise add it to the nearest Mcp test class.
- [ ] **Step 2:** Run the three classes; expect the new tests to fail on the missing line / old wording.
- [ ] **Step 3:** In the branch, after `written` is computed and renamed: when `!written.IsError && message is not null`, return `written with { Output = written.Output + "\nmessage not kept: …" }`. Don't touch `AttachmentWriter`'s answer, `ShellResult.Notice` or `Mcp/Notices.cs`. Change `WriteDescription`'s message paragraph and the parameter description to say "kept with this version in the note's history; an SVG's or a skill file's history keeps none" (keep "shown by history" and the no-change sentence).
- [ ] **Step 4:** Run the classes; all pass.
- [ ] **Step 5:** Commit: "Say in write's answer that an SVG or a skill file keeps no message" (message body names the cause: attachment versions have no message field, `AttachmentWriter.WriteAsync` takes none).
- [ ] **Step 6: Mutations:** (a) drop the appended line → the two "not kept" tests fail; (b) append it also on error → the malformed test fails; (c) restore the old `WriteDescription` sentence → the help test fails.

### Task 2: §2 — `write` and `edit` ask for the marked skills vault

**Files:**
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs` (`WriteAsync` `:128`, `NotText` `:603-611` and its callers `:138`, `:189`, `:324`)
- Modify: `src/Kumbu.Core/Notes/Replacer.cs` (`:32`, its `NotText` call)
- Modify: other `ForPath` callers per Step 6
- Test: `tests/Kumbu.Core.Tests/Attachments/TextAttachmentTests.cs` (new tests), any existing test relying on the default name (Step 4)
- Modify: `docs/superpowers/open-items.md` (rows per Step 6)

**Interfaces:** `public static ShellResult? NotText(string tool, VaultPath path, string? skillsVault)` — no default; every caller passes `await store.SkillsVaultAsync(ct)` (in `Replacer`, its own store).

- [ ] **Step 1: Failing tests** in `TextAttachmentTests`, using `Make(skillsVault: "Tools")` with `store.AddVault("Tools")` (extend `Make` to add the vault, or add it in the test):
  - `Write_creates_a_text_file_in_a_skill_of_a_marked_vault_of_another_name`: `writer.WriteAsync("/Tools/x/scripts/run.py", "print(1)\n", null, Ct)` → not error, `Output == "created /Tools/x/scripts/run.py"`, stored media type `AttachmentFormat.TextMediaType`. (Today: refused — `ForPath` without the mark sees a File and `NotText` refuses it as "note names end in .md".)
  - `Edit_refuses_a_text_file_in_a_skill_of_a_marked_vault_of_another_name`: seed through `files.WriteAsync("/Tools/x/run.py", "print(1)\n"u8.ToArray(), null, null, Ct)`; `writer.EditAsync("/Tools/x/run.py", <version token>, null, "x", Ct)` → error `"edit: run.py is a text file in a skill; replace it whole with write"`. (Today: "note names end in .md; edit changes notes only".)
  - `Write_refuses_a_text_name_in_an_unmarked_vault_named_Skills_as_anywhere`: `Make(skillsVault: null)`; `writer.WriteAsync("/Skills/x/run.py", …)` → error exactly as `writer.WriteAsync("/Work/x/run.py", …)` with the path swapped: `"write: /Skills/x/run.py: note names end in .md; write keeps notes, SVGs and a skill's text files, and other files are uploaded (the upload tool)"`. (Today: sent to `AttachmentWriter` as text.)
  - Both directions of the boundary: `Write_keeps_refusing_what_upload_refuses_in_a_marked_vault`: with `Tools` marked, `/Tools/run.py` (in the location, not in a skill's folder) and `/Tools/x/tool.bin` are refused by `write` (assert error and that nothing is stored).
- [ ] **Step 2:** `dotnet test --project tests/Kumbu.Core.Tests -- --filter-class "*TextAttachmentTests"`: the first three fail for the reasons in brackets; record the messages.
- [ ] **Step 3:** In `WriteAsync`, take `var skillsVault = await store.SkillsVaultAsync(ct);` after parsing and pass it to `ForPath` and `NotText`; give `NotText` the parameter and pass it to each `ForPath` inside; update the other callers.
- [ ] **Step 4:** Run the full Core suite: `dotnet test --project tests/Kumbu.Core.Tests`. Each test that now fails because it wrote into `/Skills/…` relying on the default name gets `SkillsVault = "Skills"` on its fake (as `TextAttachmentTests.cs:20`); never a looser assertion. List each one changed.
- [ ] **Step 5:** Commit: "Let write and edit find a skill's text file in a skills vault of any name" — the body names the cause: `NoteWriter` called `AttachmentFormat.ForPath` without the caller's mark, so it fell back to the name `Skills`.
- [ ] **Step 6: The other callers.** `rg -n 'ForPath\(' src`. For each call without the argument decide and record the verdict (for the report):
  - decides whether a path is a skill's text file on a write, or on a read that must agree with the writers → pass the mark if it is already reachable (a store with `SkillsVaultAsync`, an `access.IsSkillsVault`, `open.FirstOrDefault(v => v.IsSkillsVault)`); candidates: `SyncApi.cs:186` `IsText`, `SkillUpdateTaker.cs:264` `IsText`, `DavWrites.cs:760`, `MarketplaceIndex.cs:275`.
  - asks only whether a path is an image or a PDF (`AgentUploadApi.cs:60`, `FileApi.cs:215,282`, `SkillCopier.cs:175`, `Bundles.cs:185`, `Publications.cs:909`, `AttachmentFormat.IsPdf` `:100`) → stays; but note that an `.svg` inside a skill's folder is Text under the mark and Image outside it, so check whether the answer can differ for `.svg`, and say so in the verdict.
  - `AttachmentFormat.KindOf` (`:153`) and any caller that would need a new dependency through several layers or a change of an endpoint's contract → leave it, add a row to `docs/superpowers/open-items.md` saying what and why.
  For each changed caller, a test in the owning test project if one exists for that caller (e.g. a Sync or DAV test with a marked vault of another name); run it red first. Commit each coherent change: "Decide a skill's text file by the mark in <area>".
- [ ] **Step 7: Mutations:** (a) pass `SkillPaths.SkillsVault` instead of the mark in `WriteAsync` → the `Tools` write test and the unmarked-`Skills` test fail; (b) the same in `NotText` → the edit test fails; (c) for each changed caller in Step 6, revert it → its test fails.

### Task 3: §3.9 — `links` on an empty folder

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/LinksCommand.cs` (`RunAsync` `:64-70`; help `:39-41`)
- Test: `tests/Kumbu.Core.Tests/Sh/LinksCommandTests.cs` (or the class that tests `links` today: `rg -l '"links ' tests/Kumbu.Core.Tests`)

- [ ] **Step 1: Failing tests:**
  - `Links_on_an_empty_vault_finds_nothing_broken`: vault `Home` with no notes → `links /Home` → `Ok("(no broken or ambiguous links below /Home)")` (take the exact text from `LinksCommand.cs:145`; use the path form it prints).
  - `Links_on_a_folder_holding_only_an_image_finds_nothing_broken`: `/Work/Screens/shot.png` only → `links /Work/Screens` (and `/Work/Screens/`) → the same answer for that folder.
  - `Links_on_a_folder_made_by_mkdir_finds_nothing_broken`: a folder with only its marker.
  - `Links_on_a_missing_folder_is_no_such_file`: `links /Work/nothing` → error containing `No such file or directory` (unchanged); `links /Nope` likewise.
  - `Links_with_a_version_on_an_empty_folder_keeps_its_error`: `links --version <any token> /Home` → `"links: --version names a version of one note; a directory has none"`.
- [ ] **Step 2:** Run the class; the three empty-folder tests fail ("Is a directory" / "No such file or directory").
- [ ] **Step 3:** A folder exists when its vault exists (`ListVaultsAsync`) and the path is the vault, or `StoreAttachments.PathsUnderAsync` finds attachments or a folder marker below it — as `Blame.cs:58-63` decides. Take the folder form when there are notes below or the folder exists. If the help (`:39-41`) needs a word to stay true, adjust it.
- [ ] **Step 4:** Run the class; pass.
- [ ] **Step 5:** Commit: "Answer links on an empty folder as on a folder with no broken links".
- [ ] **Step 6: Mutations:** (a) drop the vault check → the empty-vault test fails; (b) drop the attachments/marker check → the image and mkdir tests fail; (c) treat every path as a folder → the missing-folder test fails.

### Task 4: §3.10 — `blame` on a folder: its help and `--lines`

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/Blame.cs` (`Usage` `:26`, help `:31-36,47`, `MaxNotes` `:68`, `FolderAsync` call, about `:78-81`)
- Test: `tests/Kumbu.Core.Tests/Sh/BlameTests.cs` (or the class testing blame), `tests/Kumbu.Core.Tests/Sh/HelpTests.cs`

- [ ] **Step 1: Failing tests:**
  - `Blame_lines_on_a_folder_is_refused`: `blame --lines /Work` and `blame --lines /Work/Protocols/` → `Error("blame: --lines takes a note, not a folder")`.
  - `Blame_lines_on_a_note_still_works`: `blame --lines /Work/note.md` → not error (existing behaviour; may already exist — then skip).
  - `HelpTests.Help_blame_names_the_folder_limit_and_the_section_forms`: help contains `$"{Blame.MaxNotes}"` notes (make `MaxNotes` `internal` or `public const` if needed; Core.Tests has InternalsVisibleTo? check `rg -n InternalsVisibleTo src/Kumbu.Core`), contains `… and` / `more notes` as the command prints it, `#^block` (as the help writes it), and its synopsis line contains `[#section]`.
- [ ] **Step 2:** Run; fail.
- [ ] **Step 3:** Refuse before `FolderAsync` when `options` has `lines`. Help: synopsis `blame [--lines] <path>[#section]`, the 200-note limit from `MaxNotes` (interpolated), the "… and N more notes" line as `FolderAsync` prints it, the `#^block` form beside `#Heading`. `Usage` shows `[#section]` too, if the tool-description line of `blame` (`Manual.cs`) stays true (it says "#section works": fine).
- [ ] **Step 4:** Run; pass. Also `HelpTests` whole class.
- [ ] **Step 5:** Commit: "Refuse blame --lines on a folder and say in help what blame does there".
- [ ] **Step 6: Mutations:** (a) drop the refusal → the refusal test fails; (b) refuse for notes too → the note test fails; (c) hard-code 300 in the help → the help test fails.

### Task 5: §3.1, §3.2, §3.3 — the topics in `Help.cs`, `archive`'s help, `rg`'s pipe error

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/Help.cs` (the `attachments` topic: the `write` exception paragraph and "rg --files lists notes only"; the `archive` topic's list and "Asking" line; the command list's `rg --files` line if `Help.cs` holds one — find "--files")
- Modify: `src/Kumbu.Core/Sh/Commands/ArchiveCommand.cs` (`:35`, the list of what hides it)
- Modify: `src/Kumbu.Core/Sh/Commands/Rg.cs` (pipe error `:787`)
- Test: `tests/Kumbu.Core.Tests/Sh/HelpTests.cs`, `tests/Kumbu.Core.Tests/Sh/RgTests.cs` (or the class asserting the pipe error)

- [ ] **Step 1: Read the code each text describes:** `GraphCommand.cs:19,118` (graph hides the archive, `--archive` shows it), `CalendarCommand.cs:56,351`, `EntryReader.cs:60` (calendar leaves archived notes out, no `--archive`), `Rg.cs:815-818` (`--files` lists notes and text files; with `--tag` notes only), `NoteWriter.NotText` and `UploadTool.cs:66-72` (write: notes, SVGs, a skill's text files; upload takes an SVG too).
- [ ] **Step 2: Failing tests** in `HelpTests`:
  - `Archive_topic_names_graph_and_calendar`: `help archive` contains a `graph` line saying it hides the archive with `--archive` to show it, and a `calendar` line; contains `calendar has no --archive; a note in the archive has no place in the calendar`.
  - `Archive_help_names_graph_and_calendar`: `ArchiveCommand`'s own help names both.
  - `Attachments_topic_says_rg_files_lists_text_files_too`: the topic contains `'rg --files' lists notes and text files` (exact final wording up to you; assert it) and no longer `'rg --files' lists notes only`.
  - `Attachments_topic_says_what_write_takes`: the topic says write takes notes, SVGs and a skill's text files, and everything else is uploaded.
  - In the rg test class: the pipe error for `--files` (find its test; update its expected text to name notes and text files if it names notes only).
- [ ] **Step 3:** Run; fail.
- [ ] **Step 4:** Change the texts. Archive topic: add `graph      hides it; --archive shows it` and `calendar   leaves archived notes out` in the list's shape; the "Asking" sentence becomes true by adding "calendar has no --archive; a note in the archive has no place in the calendar". Check that the topic's "Not hidden" line doesn't name graph or calendar. Attachments topic: the write paragraph names notes, SVGs and a skill's text files as what write takes, and everything else is uploaded (the upload tool takes an SVG too); "'rg --files' lists notes only" becomes "lists notes and text files (with --tag, notes only)". The topic's "15 MB" and "30 days" use their constants if not already (`TrashRetention` already does; `AttachmentFormat.MaxBytes`: check what it holds — bytes — and format as the help does, or leave and say why).
- [ ] **Step 5:** Run `HelpTests`, the rg class and `ArchiveCommandTests`; pass.
- [ ] **Step 6:** Commit: "Say in help that graph and calendar hide the archive too, and what rg --files and write take".
- [ ] **Step 7: Mutations:** restore each old sentence in turn → its test fails.

### Task 6: §3.2, §3.3, §3.13, §3.14 — `Manual.cs`

**Files:**
- Modify: `src/Kumbu.Core/Sh/Manual.cs` (`ToolDescription`: the `rg --files` line `:14`, the `Checkpoints:` line `:57`, the `rm`, `trash` and `mv` lines; `WriteDescription` "The path must end in .md"; `UploadDescription`'s second sentence, about `:138-139`)
- Test: `tests/Kumbu.Core.Tests/Sh/HelpTests.cs` (a new `ManualTests`-style section in it, or a new `tests/Kumbu.Core.Tests/Sh/ManualTests.cs` with the SPDX header)

- [ ] **Step 1: Failing tests** (`Manual_…`):
  - `rg --files` line names notes and text files (`rg --files [path...]            list note and text file paths; …` — keep the alignment; assert the line).
  - `Checkpoints:` line contains `rg searches notes and text files there, not PDFs`.
  - `rm` line names attachments (`Rm.cs:15`), `trash` line names attachments (`Trash.cs:17`), `mv` line names attachments.
  - `WriteDescription` no longer says `The path must end in .md`; says what write takes (notes in `.md`, an SVG, a skill's text file).
  - `UploadDescription` doesn't imply write takes SVGs and upload doesn't: assert the sentence the change makes (read `:138-139` and `:149` first).
  - Numbers: `ToolDescription` contains `$"kept {NoteWriter.TrashRetention.Days} days"`; `UploadDescription` contains `"15 MB"` matching `AttachmentFormat.MaxBytes` (check how `MaxBytes` maps to 15 MB) and `"10 minutes"` if a constant holds that (`rg -n 'FromMinutes\(10\)' src`); `ToolDescription` "8000 tokens" if a constant holds it. One assertion per number that has a constant.
- [ ] **Step 2:** Run; fail.
- [ ] **Step 3:** Change the texts. Keep `HelpTests.The_readers_that_hide_the_archive_take_archive` and `ToolDescriptionMentionsEveryCommand` true.
- [ ] **Step 4:** Run `HelpTests` (and `ManualTests`); pass.
- [ ] **Step 5:** Commit: "Say in the tool descriptions what rg --files, write, upload, rm, trash and mv take".
- [ ] **Step 6: Mutations:** restore each old line → its test fails; change a constant's value in a scratch edit (e.g. `TrashRetention` to 31 days) → the number test fails; restore.

### Task 7: §3.4, §3.5, §3.6 — `skills`, `pdfimages`, `xml`

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/SkillsCommand.cs` (`Summary` `:30`, help `:46`)
- Modify: `src/Kumbu.Core/Sh/Commands/PdfImages.cs` (`Usage`, help `:30,40,54,56-57`; constants `Budget`, `Ceiling` `:25-26`, the 500 images limit)
- Modify: `src/Kumbu.Core/Sh/Commands/Xml.cs` (help `:63`)
- Test: `tests/Kumbu.Core.Tests/Sh/SkillsCommandTests.cs`, the pdfimages test class (`rg -l pdfimages tests`), the xml test class, `HelpTests`

- [ ] **Step 1: Read** `NoteWriter.NotText` (write/edit on a skill), `PdfImages.cs:12` (`Forms`), `:215` (`Budget / 2` for `--list`), `:337-353` (`Ceiling`), `:377` (`.png`/`.jpg`), `:520` (500 images: suggests `--crop`), `Xml.cs:130-131`.
- [ ] **Step 2: Failing tests:**
  - skills: `Summary` contains `find, read, check and copy`; help contains `skills reads, finds and copies a built-in skill; it changes no skill` and `write and edit change SKILL.md and a skill's other notes; its other text files are replaced whole with write`.
  - pdfimages: `Usage` shows its three forms as `Forms` does; help says `--list` stops after about `{Budget/2}` seconds, and "storing stops at the first page boundary after {Budget} seconds; after {Ceiling} it stops even within a page, and names that page as partly stored" (brief §7 — this corrects the spec's "a page started finishes within 50"), the file name is `.png` or `.jpg`, and over 500 images on the first page it suggests `--crop`. Numbers from the constants (assert with the constants' values).
  - xml: help contains `an XPath and its printing stop after 10 seconds` (number from its constant), and not `A call stops after 10 seconds`.
  If pdfimages' help isn't reachable from `TestShell.Of()` (it needs the pixels), test the command object's `Help` directly.
- [ ] **Step 3:** Run; fail. **Step 4:** Change the texts. **Step 5:** Run; pass.
- [ ] **Step 6:** Commit: "Say in help what skills, pdfimages and xml do, with pdfimages' real time limits".
- [ ] **Step 7: Mutations:** restore each old text → its test fails; a constant changed in a scratch edit → the number test fails.

### Task 8: §3.7, §3.8, §3.11 — `publish`, `cat`, calendar through a checkpoint

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/PublishCommand.cs` (`Usage`, help `:16,25-27`)
- Modify: `src/Kumbu.Core/Sh/Commands/Cat.cs` (help `:21,30,38,42-45`)
- Modify: `src/Kumbu.Core/Sh/Commands/CalendarCommand.cs` (help's `--checkpoint` line) and/or `Help.cs` where the spec's `:123-127` points (find "calendar" near `--checkpoint`)
- Test: `HelpTests`, the publish, cat and calendar test classes

- [ ] **Step 1: Read** `Publications.cs:161-162` (update keeps the stored depth and `--without` unless given), `Cat.cs:184-189` (text file anywhere), `Shell.cs:261-263` (embeds left out in a pipe, with `--version`, with `--checkpoint`), `Shell.cs:225`, `CalendarCommand.cs:110-111`.
- [ ] **Step 2: Failing tests:**
  - publish: help contains each line `publish ls [path]`, `publish add <note> [--depth N] [--without <note>]... [--dry-run]`, `publish update <note> [--depth N] [--without <note>]...`, `publish rm <note>`; says `update` keeps the stored depth and `--without` list unless given; the option lines come after add/update and before rm (assert index order). `Usage` per the spec if it stays one line; the `Manual.cs` line `publish ls|add|update|rm <note>` stays.
  - cat: help says an image or a PDF prints one line, an SVG its line and then its source, a text file its content (anywhere), any other file one line; the embedded-notes block is left out in a pipe, with `--version` and with `--checkpoint`; the synopsis shows `[--version V]`.
  - calendar: its help (as `help calendar` prints it, after `Help.Of` adds the checkpoint pointer) contains `the reading forms only; set, move, close, reopen and unset are refused there` right after the `--checkpoint` line.
- [ ] **Step 3:** Run; fail. **Step 4:** Change the texts. For calendar: if the `--checkpoint` line is the one `Help.Of` appends for every reader, add the sentence in `CalendarCommand`'s own help next to where it names checkpoints, or extend `Help.Of` for calendar only — choose the smaller change that prints the sentence right after the line; say which in the commit body. **Step 5:** Run; pass.
- [ ] **Step 6:** Commit: "Say in help what publish's subcommands take, what cat prints, and that calendar only reads in a checkpoint".
- [ ] **Step 7: Mutations:** restore each old text → its test fails.

### Task 9: §3.12 — the console names every refused form

**Files:**
- Modify: `web/src/console/help.ts` (`helpBlock`, the "It only reads:" line)
- Create: `web/src/console/help.test.ts`

- [ ] **Step 1: Failing test** `help.test.ts`: reads `refused` from `./console-fixture.json` (kept equal to `ConsoleApi.Refused` by `tests/Kumbu.Server.Tests/Api/ConsoleFixtureTests.cs`) and asserts `helpBlock` names each; a copied list, with a comment pointing at `src/Kumbu.Server/Api/ConsoleApi.cs` (`ShAsync`), of the subcommand refusals: `publish add`, `update`, `rm` (as the text phrases them), `calendar set, move, close, reopen and unset`, `skills copy` — assert each form appears in the text.
- [ ] **Step 2:** `pnpm -C web test --run src/console/help.test.ts` → fails on the calendar forms.
- [ ] **Step 3:** Add `calendar set, move, close, reopen and unset` to the refused list, keeping the sentence readable.
- [ ] **Step 4:** Run; pass. Also `pnpm -C web test --run src/console`.
- [ ] **Step 5:** Commit: "Name calendar's write verbs among what the console refuses".
- [ ] **Step 6: Mutations:** remove the calendar words → fails; remove `skills copy` → fails.

### Task 10: §4, part 1 — `--version` and option synopses of the line readers

**Files (Usage line and help synopsis, as the command already keeps them):**
- `src/Kumbu.Core/Sh/Commands/Head.cs` (`--version V`), `Sort.cs` (`--version V`; flags as separate brackets as its help synopsis), `Tail.cs` (`--version V`; Usage `[-n N|-n +N]`), `Wc.cs` (`--version V`; separate brackets), `OutlineCommand.cs` (help synopsis `--version V`), `LinksCommand.cs` (help synopsis `--version V`; other files marked `attachment (not a note)` `:22,193`)
- `Semsearch.cs` (`-n N` has `--max N`), `Restore.cs` (Usage `--plan <token>`), `PdfCommand.cs` (Usage options as its help synopsis), `Df.cs`, `Du.cs` (`-h, --human-readable`), `Backlinks.cs` (Usage `--unlinked` — note `HelpTests.The_readers_that_hide_the_archive_take_archive` pins `backlinks [--archive] <path>` in `Manual.ToolDescription` and the `help` list: the list is built from `Usage`, so update that test's `usage` value to the new `Usage` and keep the tool description's line as it is only if the test is split accordingly — choose: update the test's expected list line, and assert the tool description's line separately, unchanged), `DiffCommand.cs` (Usage `[--word]` on the checkpoint form)
- Test: `HelpTests` (one theory over the commands)

- [ ] **Step 1: Read** each command's option parsing to confirm each option exists as written (`TryTakeVersion`, `Options` specs).
- [ ] **Step 2: Failing tests:** a `[Theory]` `Usage_and_help_name_the_options_the_command_takes(command, fragment)` asserting `help <command>` (which starts with the synopsis) and the `help` list line both contain the fragment, rows: head/sort/tail/wc `--version V`; outline/links help `--version V`; semsearch `--max N`; restore `--plan <token>`; df/du `--human-readable`; backlinks `--unlinked`; diff `--word` on the checkpoint line; tail `-n +N`; links help `attachment (not a note)`; pdf's options (from its synopsis).
- [ ] **Step 3:** Run; fail. **Step 4:** Change `Usage` and help synopsis lines. Keep `Manual.ToolDescription` as it is. **Step 5:** Run `HelpTests`; pass; also `EveryCommandHasHelpThatStartsWithItsName`.
- [ ] **Step 6:** Commit: "Show in help every option head, tail, wc, sort, outline, links, semsearch, restore, pdf, df, du, backlinks and diff take".
- [ ] **Step 7: Mutations:** remove one fragment from two commands in turn → their rows fail.

### Task 11: §4, part 2 — calendar, rg, trash, history, lscp, ls, and numbers from constants

**Files:**
- `src/Kumbu.Core/Sh/Commands/CalendarCommand.cs` (Usage `calendar notifications [--tag t] [vault]` and the write forms; synopsis shows the vault operand; states include `today · <time>` for an entry ongoing before its time `:455`; `--week 46` is a week of the current ISO week-year `:169-170`)
- `Rg.cs` (`-l`, `-c` name text files beside notes and PDFs `:84-85,466-468`; `-l` right of a pipe is an error, about `:774`)
- `Trash.cs` (a removed folder's row `<path>/  folder  (by …)` `:22,52`)
- `History.cs` (any attachment, not "an image or a PDF"; a note's row may end in `— message` and `(current)` or `(restored)` `:66,79,115-124,142,150`; "10 versions" `:87` from `NoteWriter.HistoryLimit`)
- `CheckpointCommands.cs` (`lscp`: any path but `/` tells how many checkpoints the vault can still keep `:51`, `Notes/Checkpoints.cs:153`; "10 versions" `:17` from `NoteWriter.HistoryLimit`)
- `Ls.cs` (`-l` has no effect on the list of vaults `:29-30,71-90`)
- Test: `HelpTests`

- [ ] **Step 1: Read** each line of code named above and confirm the claim (e.g. run the command in a test to see the row's exact shape).
- [ ] **Step 2: Failing tests:** one assertion per claim in `HelpTests` (`Help_calendar_…`, `Help_rg_…`, `Help_trash_names_a_folders_row`, `Help_history_…`, `Help_lscp_…`, `Help_ls_l_…`), and `Help_history_and_checkpoints_take_the_history_limit_from_its_constant` asserting `$"{NoteWriter.HistoryLimit} versions"` in both helps.
- [ ] **Step 3:** Run; fail. **Step 4:** Change the texts. **Step 5:** Run `HelpTests` and the touched commands' test classes; pass.
- [ ] **Step 6:** Commit: "Say in help what calendar, rg, trash, history, lscp and ls -l print".
- [ ] **Step 7: Mutations:** restore each old text → its test fails; a scratch change of `HistoryLimit` → the constant test fails (it shouldn't: the help follows; instead hard-code "10" back into one help and change `HistoryLimit` to 11 in a scratch edit → the test fails).

### Task 12: Final gate

- [ ] `dotnet restore --locked-mode` → succeeds.
- [ ] `dotnet build --configuration Release --no-restore` → 0 errors.
- [ ] `dotnet test --solution Kumbu.slnx` → record counts; compare with Task 0; any new failure is investigated (flaky: alone three times).
- [ ] `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build` → record counts.
- [ ] Commit only if a fix was needed; push `feature/help-texts`.

### Task 13: Report and pull request

- [ ] Write `docs/superpowers/overnighter/2026-11-19-help-texts-report.md` with every section the brief lists (environment, plan review, Task 0 planned vs measured, per task commit and test output, each mutation, §2's verdict per `ForPath` caller, found on the way, decisions without Chris, deviations and open points, changelog entry, hand-check list, PR link).
- [ ] Add a row with the PR's link to `docs/superpowers/open-items.md`, plus any rows from Task 2.
- [ ] Commit, push, open the PR "Make the help, the tool descriptions and the console say what the code does" against `master`; description: changes in short, **Decisions made without Chris**, ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- [ ] `gh pr checks`; rerun a flaky job once and say so.
- [ ] **Do not merge, tag or deploy.**
