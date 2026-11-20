# Help texts that say what the code does

**Status:** approved by Chris on 2026-11-19 ("finde ich gut so"), the decision of §1, the code changes and the principles included. Source: a read-only audit of every `sh` command's `Usage`, `Summary` and `Help`, the help topics, the MCP tool descriptions in `Manual.cs` and the web console's help, against the code on `master` at `63d3ca46`.

## Goal

An agent learns Kumbu² from `help`, the tool descriptions and the error texts, and nowhere else. Where they say less than the code does, it never finds a feature; where they say something else, it plans on behaviour that isn't there. This round makes every text match the code, and fixes the two places where the code, not the text, is wrong.

The audit found no option that a help documents and the code refuses, and every number quoted (15 MB, 1 GiB, 30 days, 10 versions, 200 characters, the time budgets except those in §3.5 and §3.6) matches. What it found: two bugs, about a dozen misleading texts, and about twenty-five small gaps.

## Principles

1. **The code is right unless it is clearly a bug.** Where text and code differ, the text changes; the code changes only in §1 and §2, and in the two small behaviour fixes of §3.9 and §3.10, each with the reason given there.
2. **Numbers come from the constants.** A help that quotes a limit takes it from the constant it names (`NoteWriter.HistoryLimit`, `NoteWriter.TrashRetention`, `AttachmentFormat.MaxBytes`, …) where the help is an interpolated string, so the two can't drift.
3. **The style of the texts stays.** Short sentences, the examples' shape, British English, the existing wording where it is right. A help gets longer only by what it was missing.
4. **Every changed claim gets a test where a test can hold it.** `HelpTests` already asserts claims of the help (`Help_ls_says_hiding_the_archive_is_not_access_control`); a corrected claim that names behaviour (a list of commands, an option, a limit) gets a test of the same kind, or the existing test is extended. Pure rewording needs none.

## 1. `write` drops the message for an SVG and a skill's text file (bug)

`NoteWriter.WriteAsync` (`src/Kumbu.Core/Notes/NoteWriter.cs:115-136`) checks `message`, then hands an SVG or a skill's text file to `AttachmentWriter.WriteAsync`, which takes no message: an attachment's versions have no message field. The tool's parameter description (`src/Kumbu.Server/Mcp/WriteTools.cs:22`) and `Manual.WriteDescription` promise that the message is kept with the version.

**Decision:** the message is for notes. The write of an SVG or a skill's text file still succeeds, and its result says, in one line after the usual answer, `message not kept: an SVG's or a skill file's history keeps no messages`. The `message` parameter's description and `WriteDescription` say "kept with this version in the note's history; an SVG's or a skill file's history keeps none".
Why not store it: attachment versions would need a new field, carried through history, restore, sync, WebDAV, the export and the encrypted store, for a message an agent rarely writes on an SVG. Why not refuse: an agent that always sends a message would see its SVG writes fail.
Whether and how an attachment's version keeps a message (perhaps as a metadata property) is looked at for 0.7.0 (Chris, 2026-11-19); this round only makes the texts and the result honest.

End-to-end: `write(path: "/Work/diagram.svg", content: "<svg …/>", message: "first sketch")` → `Created /Work/diagram.svg (version …)` and the line `message not kept: …`. `history /Work/diagram.svg` lists the version without a message, as today.

## 2. `write` decides a skill's folder by the name "Skills" (bug)

`AttachmentFormat.ForPath(path, skillsVault)` (`src/Kumbu.Core/Attachments/AttachmentFormat.cs:83-86`) says writers pass the caller's name for the marked skills vault. `NoteWriter.cs:128` and `:605-607` call it without, so they take the default name `Skills`; `UploadTool.cs:66-71` and `AttachmentWriter.cs:124-125` pass the mark. With a skills vault of another name, `write` sends a skill's `.py` to upload and upload sends it back to write: nothing can write it. With a vault named `Skills` that isn't marked, the reverse.

**Decision:** `NoteWriter` asks `SkillsVaultAsync` (the note store it already holds, as `NoteMover` and `TreeWriter` do) and passes it at `:128` and in the refusal at `:605-607`. The run also checks every other caller of `ForPath` without the argument (today `AttachmentFormat.cs:100,153`, `AgentUploadApi.cs:60`, `SyncApi.cs:186`, `FileApi.cs:215,282`, `DavWrites.cs:760`, `SkillCopier.cs:175`, `SkillUpdateTaker.cs:264`): where the call decides whether a path is a skill's text file on a write or a read that must agree with the writers, it passes the mark too; where it only asks whether a path is an image or a PDF (the skill rule can't change that answer), it stays. Each caller and its verdict goes into the report.

Tests: a skills vault named `Tools` (marked), `write /Tools/x/scripts/run.py` creates a text file; `edit` on it gives "is a text file in a skill; replace it whole with write"; a vault named `Skills` that isn't marked: `write /Skills/x/run.py` is refused as an attachment path, as for any vault. Red first, against today's code.

## 3. Misleading texts

Each item: the text that changes, and what it says afterwards. File paths are under `src/Kumbu.Core/Sh/` unless they start otherwise.

1. **The archive topic and `archive`'s help** (`Commands/Help.cs:94-102`, `Commands/ArchiveCommand.cs:35`): add `graph` (hides the archive, `--archive` shows it; `GraphCommand.cs:19,118`) to the list of what hides it, and `calendar` (leaves archived notes out, no `--archive`; `CalendarCommand.cs:56,351`, `EntryReader.cs:60`). "Asking: --archive on each of them" becomes true for the list by saying "calendar has no --archive; a note in the archive has no place in the calendar". Test: the topic names `graph` and `calendar`.
2. **`rg --files`** (`Commands/Help.cs:79`, `Manual.cs:14`, the pipe error at `Commands/Rg.cs:787`): it lists notes and text files (`Rg.cs:815-818`); with `--tag`, notes only. Rg's own help (`Rg.cs:78`) is right already.
3. **`write`'s exceptions** (`Commands/Help.cs:49-52`, `Manual.cs:75` "The path must end in .md", `Manual.cs:138`): write takes notes, SVGs and a skill's text files; everything else is uploaded. Upload also takes an SVG (`UploadTool.cs:68-72`, as `Manual.cs:149` says); `Manual.cs:138-139` stops implying it doesn't.
4. **`skills`** (`Commands/SkillsCommand.cs:30,46`): the summary names copy ("find, read, check and copy"); "skills only reads and finds" becomes "skills reads, finds and copies a built-in skill; it changes no skill". "Change a skill's files with write and edit" becomes "write and edit change SKILL.md and a skill's other notes; its other text files are replaced whole with write" (`NoteWriter.cs:605`).
5. **`pdfimages`** (`Commands/PdfImages.cs:30,40,54,56-57`): Usage shows its three forms, as its own error usage (`Forms`, `:12`) does. "Stops after about 20 seconds": `--list` stops after about 10 seconds (`Budget / 2`, `:215`); storing stops at the first page boundary after 20 seconds, and a page started finishes within 50 (`Ceiling`, `:26,337-353`). The file name is `.png`, or `.jpg` where the JPEG fallback applies (`:377`). Over 500 images on the first page, it suggests `--crop` regions rather than a page range (`:520`).
6. **`xml`** (`Commands/Xml.cs:63`): "A call stops after 10 seconds" becomes "an XPath and its printing stop after 10 seconds" (`:130-131`); reading the file and the overview have no budget, as a 15 MB text file parses well within it. (The spec `2026-11-19-reading-csv-html-xml-design.md` §10 says "a call"; it stays as it is, and the report mentions it.)
7. **`publish`** (`Commands/PublishCommand.cs:16,25-27`): Usage shows each subcommand's own shape (`publish ls [path]`, `publish add <note> [--depth N] [--without <note>]... [--dry-run]`, `publish update <note> [--depth N] [--without <note>]...`, `publish rm <note>`). `--depth`'s "default 0" holds for `add`; `update` keeps the stored depth and `--without` list unless given (`src/Kumbu.Server/Publishing/Publications.cs:161-162`). The option lines sit under add and update, not under rm.
8. **`cat`** (`Commands/Cat.cs:21,30,38,42-45`): "an attachment (an image or a PDF) prints one line" becomes the full rule: an image or a PDF one line, an SVG its line and then its source, a text file its content (anywhere, not only in a skill's folder: `:184-189`), any other file one line. The embedded-notes block is left out in a pipe, with `--version` and with `--checkpoint` (`Shell.cs:261-263`). The synopsis shows `[--version V]`.
9. **`links <folder>`** (`Commands/LinksCommand.cs:39-41,70,77`) — behaviour fix: an existing folder (or vault) without notes answers as the help promises for a folder, with no broken links found, instead of "Is a directory" or "No such file or directory". The answer is the one the folder form gives when every link resolves. Why change the code: "No such file or directory" for a folder that exists is wrong, not merely undocumented. Test: an empty vault and a folder holding only an image.
10. **`blame` on a folder** (`Commands/Blame.cs:26,31-36,47,68,78-81`): the help names the 200-note limit (from `MaxNotes`) and the "… and N more notes" line, the `#^block` form beside `#Heading`, and shows `[#section]` in its synopsis. Behaviour fix: `--lines` with a folder is refused ("blame: --lines takes a note, not a folder"), as other commands refuse an option that doesn't apply (`PublishCommand.cs:140-143`), instead of being ignored. Test: the refusal.
11. **Calendar through a checkpoint** (`Commands/Help.cs:123-127`, `Commands/CalendarCommand.cs`): its help's `--checkpoint` line is followed by "the reading forms only; set, move, close, reopen and unset are refused there" (`Shell.cs:225`, `CalendarCommand.cs:110-111`).
12. **The console** (`web/src/console/help.ts:10`): the refused list adds `calendar set, move, close, reopen and unset` (`src/Kumbu.Server/Api/ConsoleApi.cs:89-94`). Vitest: the text names every refused form `ConsoleApi` lists (read the list from the help text; the server list is copied into the test with a comment pointing at `ConsoleApi.cs`).
13. **`rg` in a checkpoint** (`Manual.cs:57`): "rg searches notes only there, not PDFs" becomes "rg searches notes and text files there, not PDFs" (`CheckpointView.cs:13,51`, `Rg.cs:225`, `Shell.cs:254`). The topic's wording (`Help.cs:81`) is right already.
14. **`rm`, `trash`, `mv` in the tool description** (`Manual.cs:40-41` and the `mv` line): name attachments beside notes and folders (`Rm.cs:15`, `Trash.cs:17`).

## 4. Small gaps

Undocumented options and incomplete descriptions; each is added where its siblings are (Usage line and help synopsis as the command already keeps them).

- `--version V`: `head` (`Head.cs:8,12,16-23`), `sort` (`Sort.cs:12,17,22`), `tail` (`Tail.cs:6,12,17`), `wc` (`Wc.cs:8,16,21`); in the help synopsis of `outline` (`OutlineCommand.cs:17`), `links` (`LinksCommand.cs:19`), `cat` (§3.8).
- `semsearch`: `-n N` has the long form `--max N` (`Semsearch.cs:28,34,39,44`).
- `restore`: Usage shows `--plan <token>` (`Restore.cs:9,13`).
- `pdf`: Usage shows its options as the help synopsis does (`PdfCommand.cs:31,36`).
- `df`, `du`: `-h, --human-readable` (`Df.cs:15`, `Du.cs:20`).
- `backlinks`: Usage shows `--unlinked` (`Backlinks.cs:126,128`).
- `diff`: Usage shows `[--word]` on the checkpoint form (`DiffCommand.cs:127,140,180`).
- `calendar`: Usage shows `calendar notifications [--tag t] [vault]` and the write forms; the synopsis shows the vault operand (`CalendarCommand.cs:21,39,45,70,287-290`); the states include "today · <time>" for an entry ongoing before its time (`:455`); `--week 46` is a week of the current ISO week-year (`:169-170`).
- `rg`: `-l` and `-c` name text files beside notes and PDFs (`Rg.cs:84-85,466-468`); `-l` right of a pipe is an error (about `:774`), as the help says for `-c` the other way.
- `trash`: a removed folder's row (`<path>/  folder  (by …)`, `Trash.cs:22,52`).
- `history`: any attachment, not "an image or a PDF"; a note's row may end in "— message" and "(current)" or "(restored)" (`History.cs:66,79,115-124,142,150`).
- `links`: other files are marked `attachment (not a note)` (`LinksCommand.cs:22,193`).
- `lscp`: any path but `/` tells how many checkpoints the vault can still keep (`CheckpointCommands.cs:51`, `Notes/Checkpoints.cs:153`).
- `ls -l`: no effect on the list of vaults (`Ls.cs:29-30,71-90`).
- `tail`: Usage `[-n N|-n +N]` (`Tail.cs:12`); `wc` and `sort` Usage write their flags as separate brackets, as their help synopsis does (`Wc.cs:16`, `Sort.cs:17`).
- "10 versions" typed out in `CheckpointCommands.cs:17` and `History.cs:87` comes from `NoteWriter.HistoryLimit`.

## Security boundary

Help and description texts, and two small behaviour changes in reading commands (§3.9, §3.10). §2 touches how `write` and `edit` decide a skill's text file: it must not let a write create what upload or the skill rules refuse; the tests of §2 cover both directions. No authentication, authorisation, sharing, encryption or storage code is changed. `src/Kumbu.Server/Auth`, `OAuth`, `Sharing`, `Vaults/EncryptedNoteStore.cs`: not touched.

## Out of scope

- Storing a message on attachment versions: looked at for 0.7.0 (§1).
- New options or commands; behaviour changes beyond §1, §2, §3.9 and §3.10.
- `graph --checkpoint` (graph is not a checkpoint reader today, and the help says nothing else).
- Other specs: they stay as written, including the CSV/HTML/XML spec's "a call stops after 10 seconds".
- The Obsidian plugin and the web app's other texts.
