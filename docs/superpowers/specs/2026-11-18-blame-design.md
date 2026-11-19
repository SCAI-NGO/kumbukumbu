# Who wrote what (`blame`) — design

Date: 2026-11-18. Status: approved by Chris on 2026-11-18 ("ja zu allem"), decisions in §3; the points marked *(Claude's call)* were approved with it. Size: M (server record, `sh`, REST, a web window).

Sources:
- the idea and the UX review with Chris's answers: `/SWE/Kumbu/Ideas/Who wrote what 2026-11-08.md`;
- the sealed per-note side record of the graph backend, the model this one departs from (§5.4): `docs/superpowers/specs/2026-11-16-graph-backend-design.md` §4.1.

## 1. Why

A person and her agent document one topic in parallel in the same vault: she writes down what she does by hand, the agent what it does. By the evening there is a complete protocol. To polish it, hand it in or check the agent's part, she needs to see which part came from whom; the agent needs the same to answer "which steps did I document, which did she?".

## 2. Goals, non-goals, anti-goals

Goals:
- For every line of a note, the writer who last changed it and when, recorded at write time and true for as long as the note lives (§4, §5).
- `sh blame <path>` by line and `blame <folder>` per note (§6); a "Who wrote what" window in the web app by section (§7).
- Exactly the disclosure the history already makes, no more (§8).

Non-goals:
- **Authorship of removed text.** Only the current lines are attributed; what was deleted is the history's matter (`diff`).
- **Attachments.** Notes only: an image or a PDF has no lines.
- **"Written by X, revised by Y".** Last writer only (decision 5).
- **Attribution in the editor.** No tint, no gutter in Write or Read mode: Track A owns the editor and the layout (decision 2). A toggle in Track A's side panel can follow later.
- **Export with marks per section.** The protocol summary of `blame <folder>` and the legend cover it for now.
- **Records in checkpoints and history entries.** A checkpoint and an earlier version keep their content only (§5.3).
- **Attested writer kinds** (the "attested history" idea): later, on top of this.

Anti-goals, which make this a failure even if it works:
- **A line attributed to someone who didn't write it,** or a changed line kept under an earlier writer. Where the record can't know, it says so ("earlier than …", §5.2); where it must coarsen, it errs towards the writer of the write (§5.5), never towards an earlier one.
- **A record taken from a client.** Only the server writes it (§8).
- **A member told apart from another member,** or a key name shown to someone the member panel doesn't show it to.
- **A score.** No percentages, no ranking of people: counts of sections and lines, named, in note order.

## 3. Decisions

All by Chris on 2026-11-18, each with his reason.

1. **Record at write time, not from the history.** A small sealed record beside each note of who wrote each line, updated by a line diff on each write: lines unchanged keep their writer, new or changed lines get this write's writer (label, reference, time). *Because* the history keeps only 10 versions (`NoteWriter.HistoryLimit`, `src/Kumbu.Core/Notes/NoteWriter.cs:27`) and a person and her agent taking turns make one version per turn, so by the evening of a day-long protocol a history-based view would be empty; only a record made at write time stays true.
2. **The web app gets a "Who wrote what" window over the note:** the note's text read-only, a writer gutter per section, a legend with counts ("12 sections by Brita, 9 by Veyalt"). Reached from the note's menu and the palette's Note group; no editor change. *Because* it needs no change in Track A's editor and layout; a toggle in Track A's side panel can follow later.
3. **Record by line, show by section in the web app, by line in `sh`.** *Because* lines are what a diff knows and agents can count, sections are how people read a protocol.
4. **`sh blame <path>`, its help titled "who wrote what"; no second name.** *Because* it is an agent feature first, Kumbu² aims mainly at prosumers, and that is what it is called.
5. **Last writer per line, as git does:** a line only reflowed or re-indented counts as changed. *Because* it is one simple rule people and agents know from git.
6. **`blame <folder>`: per note, lines per writer;** the protocol summary falls out of it. *Because* it follows how Kumbu² works anyway.
7. **Who may see it: exactly who may read the note's history,** with the writer labels the history shows (others' keys and connections as the member panel shows them); discreet and hidden vaults as for `history`. *Because* it follows how Kumbu² is built.

## 4. The rule

Lines are what `Lines.Split` makes of the content (`src/Kumbu.Core/Sh/Lines.cs:8`): split on `\n`, one trailing newline ends the last line. Every write path has made `\r\n` `\n` before (`NoteWriter.Normalize`). Lines compare ordinally: a changed character, indentation or reflow is a changed line (decision 5).

A write that replaces content A with content B, by writer W (its label, `WriterRef` and time, as `StoredNote.LastWriter`, `Ref`, `LastWriteAt` of the new state):

- the line diff of A and B (§5.5) pairs every line of B either with an equal line of A, which keeps the writer it had, or marks it added, which gets W;
- lines of A the diff removes take their record with them.

A note created by W: every line is W's. A write that keeps the content (a move without a link rewrite, a restore from the trash, an upload that keeps the current state) changes no line's writer.

## 5. The record

### 5.1 Shape

A new type in Core, `src/Kumbu.Core/Notes/Authorship.cs` *(Claude's call: Core, so the in-memory store of the tests and the server compute it with the same code)*:

```
Authorship(string Hash, DateTime? Earlier, bool Dropped, IReadOnlyList<Write> Writes, IReadOnlyList<Run> Runs)
Write(string Label, WriterRef? Ref, DateTime At)
Run(int Lines, int Write)          // Write: an index into Writes, or -1: not recorded
```

*Amended while building:* `Write` and `Run` are nested in `Authorship` (`Authorship.Write`, a name too general for the namespace), and `Ref` is nullable, as `HistoryEntry.Ref` is: a write backfilled from the history keeps the reference its entry carries, and the payload keeps a legacy and an unknown reference apart (§5.3).

- `Hash`: the SHA-256 of the content it describes (`VersionToken.Hash`), the full hash as for extracts. A record whose hash isn't the content's is treated as missing (§5.2). Why: a downgrade to a server without the record, then an upgrade, or any future bug, would otherwise show a record of another text.
- `Runs`: consecutive lines of one write, in order; their `Lines` add up to the content's line count. Adjacent runs of the same write are merged; writes no run names are dropped on every update. So the record grows with the number of changes of writer in the note, not with its length.
- `Earlier`: for runs with `-1`, the time they were last changed at or before (§5.2); null when every line is recorded.
- `Dropped`: the record outgrew its bound and started again (§5.5). *Amended while building:* set while lines from before the restart are left, so it goes with `Earlier` when the last of them does; the line `blame` adds for it (§6) needs `Earlier`'s time.

`StoredNote` gets `Authorship? Authorship = null` (`src/Kumbu.Core/Notes/StoredNote.cs:93`); null means none recorded.

### 5.2 Notes from before the record: backfilled from the kept history, at read and on the first write

*(Claude's call, recommended over "everything before the upgrade is unrecorded".)* A note without a valid record reads its attribution from its own history, computed by one function, `Authorship.Of(StoredNote)`:

1. Revision 1: every line is the creator's (the current state is the note's first).
2. Otherwise start from the oldest state the history keeps, S₀. If the note's creation is known and is S₀ (`NoteCreation.Of` gives `AtLeast: false` and S₀'s time), S₀'s lines are S₀'s writer's. If not, they are unrecorded, `Earlier` = S₀'s time: S₀'s writer changed some of them, but which is lost with the state before it.
3. Then each later state in order, the current one last, by the rule of §4.

The write path uses the same function as its base: the first write after the upgrade stores `Of(current)` updated by that write. Nothing runs at upgrade or unlock; reads before and after that first write show the same thing.

Why backfill rather than not: on the day of the upgrade, a protocol begun that morning would otherwise read "not recorded" from top to bottom, the exact case the feature is for. It is correct at the history's precision: changesets are per writer (`Changeset.ForAsync`), so a version never mixes two writers' writes, and coalescing only folds one writer's writes together. Where it ends, said honestly: at the oldest kept version (at most 10 back); a backfilled line's time is that of the version that changed it, which for a changeset is its last write, as `history` shows it. Cost: at most 10 bounded diffs per note (§9), paid at read until the note's next write; a write pays it once.

### 5.3 Where it lives: in the note's own sealed payload, in the same transaction

*(Claude's call.)* One more field of `NotePayload` (`src/Kumbu.Server/Vaults/UnlockedVault.cs:759`), `by`, omitted when null, sealed with the note under its document id and revision (`SealNote`, `UnlockedVault.cs:276`). Compact: `Writes` as `[label, ref, at]`, `Runs` as one flat array `[lines, write, lines, write, …]`. *Amended while building:* the whole field is `{h, w, r, e?, d?}` (`AuthorshipPayload`, `UnlockedVault.cs`); `ref` is the reference as every payload seals it (`WriterRefPayload`), `"legacy"` for a write backfilled from a history entry from before references, and null for one sealed without a reference, so that each reads back as the history shows it.

Why here and not in a collection of its own like the extracts:

- The record is **a source of truth, not derived data.** An extract can be parsed anew from the note, so the graph backend writes it after the commit and lets a stale one heal itself (graph spec §4.1). A lost record can't be rebuilt beyond the history; so it is written in the same transaction as the content, and the compare-and-swap on the revision that guards the note guards it too. Two writers racing: the loser's transaction fails as today, and its retry computes from the winner's state.
- Everything else **comes free**: a move re-inserts the note's payload at the new path (`EncryptedNoteStore.cs:321`), the trash keeps the whole `NotePayload` (`TrashPayload`), a restore from the trash re-inserts it, re-keying reseals it, deleting a vault or an account deletes it, and `df`, the quota and the admin's figures count it, since `notes` is one of `VaultCatalog.ContentCollections` (`VaultCatalog.cs:761`). `UnlockedVault.ApproxBytes` adds its size for the memory limit.
- No new collection, index, or document per write.

The history entries and the checkpoints keep no record: an earlier state's attribution is needed by no one (§4 attributes only the current lines), and a restore attributes by the rule (§5.4).

### 5.4 Who computes it: the store, from the state it replaces

Every write that changes a note's content or path ends in `EncryptedNoteStore`: `TryCreateAsync` (`:175`), `TryCommitAsync` (`:200`, which `TryReplaceAsync` uses) and `TryRestoreFromTrashAsync` (`:912`). There, before sealing, the store sets

```
next = next with { Authorship = Authorship.After(storedCurrent, next) }
```

where `storedCurrent` is the working set's entry the compare-and-swap replaces (null for a create), and `After` is `Of(storedCurrent)` updated by §4 with `next`'s writer, or `Of(storedCurrent)` unchanged when `WriterRef.Keeps(current, next)` and the content is the same. Whatever `Authorship` the caller put on `next` is ignored.

*Amended while building:* a carried state keeps `Of(storedCurrent)`, not `storedCurrent.Authorship`. A move counts the revision up (`NoteMover.cs:273`), so a note from before the record that was never written since (revision 1, all its creator's) would read "earlier than …" after a plain move; storing the backfilled record keeps the read the same. It costs a move of such a note the backfill once (§9). `InMemoryNoteStore` (tests) does the same in its three methods.

Why the store and not `NoteWriter.Next`: it covers every path, including those that build a `StoredNote` themselves (create in `NoteWriter.cs:154`, `cp` in `TreeWriter.cs:150`, the board's new card, uploads, the seeder, the welcome vault, a checkpoint restore of a removed note at `Checkpoints.cs:319`), and it computes against the state actually replaced, not the one a caller believed current.

What each path then records, which needs no code of its own:

| Write | Lines that change writer |
|---|---|
| `write`, `edit` (MCP and `sh`), web saves (`PUT /api/notes`), Obsidian sync, WebDAV `PUT` | the changed ones, to the caller (the session's user, the device key, the share's "label (dav)") |
| `replace` across notes | the changed ones, to "label (replace)" |
| `mv`, `archive` | none for a plain move; the lines whose links the move rewrites, in the moved note and in the notes linking to it, to "label (mv)" / "(archive)" |
| calendar `set`/`move`/…, board card moves, flags | only the front-matter or flag lines they rewrite, to the caller |
| `rm`, then `restore` from the trash | none: the record goes into the trash and comes back with the note |
| `restore <path> <version>`, checkpoint restore | the lines that differ from the current state, to "label (restore)" (below) |
| checkpoint restore of a removed note, `cp`, Duplicate, an Obsidian conflict copy, the seeder, uploads | a new note: every line to its writer (below) |

**A restore of a version: the restorer, for the changed lines** *(Claude's call)*. Lines equal in the current state and the restored one keep their writers; the others become "Chris (restore)". Why: the record answers who made each current line be there, and the history, an audit log first, says the restore did; attributing the lines to their original writers would need a record per history state and would hide that a person brought them back. The mark "(restore)" in the label tells the reader at once, and `history` names the version. git does the same with a revert.

**A copy is a new note: every line the copier's** *(Claude's call)*. Duplicate in the web app is a read and a create (`web/src/api/notes.ts:236`), `cp` a create (`TreeWriter.cs:150`); both give the copy a history that starts with the copier, and the record follows the same rule. Why: Duplicate is mostly used to start from a template, where keeping the template author's name on every line would be noise; the original keeps its record. A conflict copy is the device's: it is the evidence of the conflict, as its history is.

### 5.5 The diff and its bounds

Myers' line diff, as `diff` and `history` use (`src/Kumbu.Core/Diffs/Myers.cs`), with a budget: a new overload `Myers.Diff(a, b, comparer, maxCells)` that returns null when the search after the common start and end are taken off would need more than `maxCells` cells (its trace holds one array of `2·(n+m)+2` ints per step d, so cells = (d+1)·(2·(n+m)+2)). `maxCells` = 2,000,000 (about 8 MB, a few ms) *(Claude's call; the first task measures it and may move it)*.

Over the budget, **every line of B from the first difference to the last counts as changed** and gets W; the common start and end keep their writers. This over-attributes to the writer of the write, never under-attributes: an unchanged line in the middle may read as W's, a changed one never as an earlier writer's. It happens only when a single write changes a very large span in many places (an agent rewriting a whole long note), where W's claim to the middle is nearly true anyway.

**The record's own bound.** At most 4,096 runs *(Claude's call)*: a protocol of 2,000 lines whose writer changes every line stays inside, and the field stays under about 600 KB at its worst, beside a note document that holds up to 11 states of 1 MiB. A write that would leave more starts the record again: every line unrecorded, `Earlier` = this write's time, `Dropped` = true, and `blame` says why (§6). Why not merge runs to fit: a merged run names a writer for lines she didn't write.

## 6. `sh blame`

```
blame [--lines] <path>[#section]
blame <folder>
```

Help, first line "who wrote what":

```
blame [--lines] <path>
  Who wrote what: for each run of lines of a note, the writer who last changed them and
  when (the newest write in the run, UTC). A line counts as changed when any character in it
  changed, indentation and reflow included, as in git blame. Recorded at every write since
  Kumbu² 0.6.0; lines older than that come from the history, which goes back 10
  versions, and read "earlier than <time>" where it ends. A section (note.md#Heading)
  limits it to that section. --lines prints every line with its writer, time and text.
blame <folder>
  For each note below the folder: lines per writer (blank lines not counted), then the
  total per writer and the number of notes.

  blame /Work/protocol.md
  blame /Work/protocol.md#Results --lines
  blame /Work/Protocols/
```

Output of a note, compact for agents *(Claude's call)*: a run is a maximal span of consecutive lines with the same writer as shown to the caller (§8), its time the newest of its lines'. Columns padded as `history` pads them; times as `History.Time` (`History.cs:268`).

```
1-6      Brita          2026-11-18 09:12 UTC
7-19     Veyalt         2026-11-18 11:40 UTC
20       Brita          2026-11-18 14:02 UTC
21-42    Veyalt         2026-11-18 16:55 UTC
43-80    earlier than 2026-11-17 18:00 UTC
Brita 18 lines, Veyalt 31, earlier 30 (blank lines not counted)
```

With `--lines`: `20  Brita  2026-11-18 14:02 UTC  <text of line 20>`, under the shell's output limit like `cat` (beyond it, the hint to give a section). A dropped record adds a last line: `(the record started again at 2026-11-18 15:00 UTC: more than 4096 changes of writer)`. The result carries the note's version as `cat` does (`ShellResult.Version`), so an agent can edit on it.

A folder: one line per note in path order, `/Work/Protocols/2026-11-18.md  Brita 18, Veyalt 31, earlier 30`, then `total: Brita 240 lines in 12 notes, Veyalt 410 in 9`. At most 200 notes, then `… and 57 more notes; blame a folder below it`. A note `blame` refuses as `history` does: an attachment (`blame: /Work/scan.pdf: only notes have lines`), a missing path, a damaged note.

**MCP gets it through `sh` only** *(Claude's call)*: it is a read, like `history`; a line goes into `Manual.ToolDescription`. No MCP tool of its own.

The command lives in `src/Kumbu.Core/Sh/Commands/Blame.cs`, beside `History.cs`, with the same constructor (`INoteStore`, clock, `IWriterLabels`).

*Amended while building:* the constructor takes no clock (`Blame(INoteStore, IWriterLabels?)`): nothing in the output is relative to now. The web app's console runs `blame` too, a read like `history` (`ConsoleApi.Allowed`, its completion in `web/src/console/paths.ts`), and a line with it takes a permit of the history scans as `history` does: on a folder it reads every note below, with the history of those from before the record.

## 7. The web app: the "Who wrote what" window

**The window.** A light window (`WindowFrame` kind `light`, 672 × 720 px, a click on the dimmed notes closes it), like Recent and What's on since #232, title "Who wrote what", heading the note's title. Route `/blame/:vault/*` with `handle: { window: true }` (`web/src/router.tsx:136`), and `blame` added to the vault routes of `web/src/discreet/neutralWindow.ts:8`, so a discreet note's address stays `/discreet`. Not a sheet like History (`HistorySheet.tsx`) *(Claude's call)*: the text needs the room.

**Reached** from the note's menu, "Who wrote what", right after "History" and in read mode only, as History is (`web/src/shell/useNoteActions.tsx:123`); the palette's Note group takes it from the menu (`web/src/palette/rows.ts:55`). Why read mode only: the window shows the server's state, which in Write mode could lag behind the editor.

**Content**, top to bottom:

1. The legend: "12 sections by Brita, 9 by Veyalt, 3 from before 17 Nov" (sections by their main writer, §7.1), each writer with a small colour mark and the kind as History shows it ("· Obsidian device"). Below, when any line is unrecorded, one sentence: "Kumbu² records who wrote each line from version 0.6.0 on; lines from before 17 Nov 18:00 are older than what the note's history keeps." (the time is the record's `Earlier`).
2. The sections in note order. Each: a gutter with the writer ("Brita", or "Brita and others"), the kind, and the time of the newest line ("2 h ago", the full time on hover and for screen readers); beside it the section rendered read-only with `MarkdownView`. On the phone the gutter sits above the section. Activating the gutter opens the section's detail in place: every writer with her lines and newest time ("Brita, 14 lines, 14:02 · Veyalt (mv), 1 line, 16:10").
3. Colour is never the only carrier: every gutter names its writer.

Times in the browser's time zone, as History has them (`timeText`, `ago`).

### 7.1 Sections and "mixed"

Computed on the server (§7.2), with Core's `Outline` (`src/Kumbu.Core/Sections/Outline.cs`), so that the window and `sh` read the same headings:

- the front matter, if any: one section, "Properties";
- the lines before the first heading, if any is not blank: one section, "Top";
- each heading outside code (`Outline.Parse`): a section from its line to the line before the next heading of any level. Flat, so no line counts twice.

A section's **main writer** is the writer of most of its non-blank lines; a tie goes to the one with the newest line. Writers are told apart here by identity, the shown name without the tool's mark (`Writers.Identity`), so "Chris (mv)" counts as Chris; the detail keeps the mark. Unrecorded lines count as one writer, "before <date>". A section is **mixed** ("Brita and others") when at least one of its non-blank lines is by another writer than the main one, unrecorded lines included. Why so strict *(Claude's call)*: a threshold would hide a one-line change by the agent in a long section, the change a person most wants to find. Blank lines carry a writer but count nowhere: a blank line between paragraphs is no one's work.

### 7.2 REST

`GET /api/blame/{vault}/{**path}` beside `/api/history` (`src/Kumbu.Server/Api/NoteApi.cs:258`), same rights and errors (400, 401, 404, 503; an attachment 400):

```
BlameView(string Path, string Version, string Content, DateTime? Earlier, bool Dropped,
          BlameWriterView[] Writers, BlameSectionView[] Sections)
BlameWriterView(string Display, string? Kind)
BlameSectionView(int Start, int End, string Title, int? Writer, bool Others,
                 BlameCountView[] Counts)      // Writer null: before the record
BlameCountView(int? Writer, string Display, int Lines, DateTime At)
```

The content and its version come in the same answer, so the text and the record are of one state. Lines are 1-based, inclusive. `Earlier`: the record's (§5.1), for the sentence of §7 item 1; null when every line is recorded.

*Amended while building:* `Writers` are the writers by identity (§7.1) and kind as shown: the label without the tool's mark, then rendered, since a rendered name ("Alice (Lab)", a member "Sam (Home)") may hold brackets of its own; so two writers shown alike are one entry and an index tells no one apart; `Writer` in a section and in a count is an index into them. A count is per writer as shown, the mark kept ("Chris (mv)" beside "Chris", both of writer "Chris"); the unrecorded lines' count has `Writer` null, `Display` "earlier" and `At` the record's `Earlier`, for the web app to word as "before <date>". An attachment answers 400 with "… is an attachment: only notes have lines."

## 8. Security

- **Trust boundary.** The record is written by the store only, from the state it replaces and the writer the caller's credential resolves to (`ICaller.WriterIn`, `ReferenceIn`); no API, sync message, WebDAV property or MCP argument carries one, and one set on a `StoredNote` by any caller is overwritten (§5.4). An Obsidian write is the device key's, a WebDAV write the share's ("label (dav)", `RequestCaller.cs:240`), exactly as the history records them.
- **Sealed like the content.** In the note's payload, under the vault key, bound to the document id and revision; a database dump shows neither writers nor line counts. The record names writers by label and `WriterRef` only, as the history does (collab design §8.1): a member's write stores the membership's random writer id, no user id.
- **No new disclosure beyond the history.** Whoever may read the note may read its history (`History.cs`, `NoteApi.HistoryAsync` check nothing more), so `blame` and `/api/blame` read through the same store with the same rights, and every writer is shown through `IWriterLabels.RenderAsync` (`src/Kumbu.Server/Api/WriterLabels.cs`). Runs are formed after rendering, from the shown name: to a member, every other member is "another user", and two of them side by side make one run, so no run boundary tells them apart. What is new is depth, not kind: the history shows a writer and a time per version for 10 versions; the record keeps a writer and a time per line for longer.
- **Discreet and hidden vaults** as for `history`: the store doesn't serve a hidden vault, so `blame` answers "No such file or directory" and the endpoint 404; the window's route is a vault route for the neutral address (§7).
- **Bounds against abuse.** A writer can't make the record outgrow its bound (§5.5) or a diff outgrow its budget; the note's 1 MiB limit holds first.

## 9. Performance

- **Per write:** splitting both texts into lines and comparing the common start and end, O(n); then Myers on the middle within the budget. Measured on 2026-11-19 *(replacing the estimates)*, `Authorship.After` in a Release build on a cloud machine shared with other jobs (4 vCPUs, Intel Xeon @ 2.10 GHz, .NET 10), a note of 20,000 lines and 1.16 MB, averaged over 10 runs:

  | Write | Time | Allocated | Diff |
  |---|---|---|---|
  | one line changed | 11 ms | 13 MiB | within the budget |
  | a section of 200 lines rewritten | 15 ms | 14 MiB | within |
  | the first and the last line changed | 10 ms | 15 MiB | within |
  | 12 lines changed across the note | 14 ms | 21 MiB | within, at the budget's edge |
  | 30 lines changed across the note | 13 ms | 21 MiB | over: the fallback |
  | every 10th line changed | 10 ms | 21 MiB | over: the fallback |

  Most of it is the fixed part, not the diff: splitting both texts (about 2 ms each, which also makes most of the allocation), hashing the old and the new content (about 1 ms each) and the common start and end (under 1 ms). So the cost is about 10 ms for any write to a note that size, and scales with its length; a note of 100 KB pays a tenth. The search up to the budget is cheap, and 2,000,000 cells stay: on the 1 MiB note the budget allows about 12 changed lines spread over the whole note, on a note of 2,000 lines about 125, so the fallback stays the rare case §5.5 describes. The write already seals up to 11 states of the note.
- **Per read:** none for a recorded note; up to 10 bounded diffs for one from before the record (§5.2). Measured as above: the backfill of the 1 MiB note from 10 states, 83 ms; of 1,600 notes of 1.2 KB with 10 states each (the `blame <folder>` case), 184 ms in all. The 200-note cap bounds the rest.
- **Memory:** a run is two ints, a write a label, a reference and a time; counted in `ApproxBytes`.

## 10. Tests, written first

Core (xUnit, `tests/Kumbu.Core.Tests`):
- `Authorship.After`: a create gives every line the creator; an unchanged line keeps its writer, a changed, inserted or re-indented one gets the new writer; a removed line goes; a trailing newline added changes nothing; adjacent runs merge and unused writes go.
- The budget: over `maxCells` the span from first to last difference goes to the writer, the common start and end keep theirs; `Myers.Diff` with a budget equals the unbounded one under it.
- The bound: a write leaving more than 4,096 runs starts the record again, `Dropped` set.
- `Authorship.Of`: revision 1; a history holding the creation; a history that has overflowed (lines "earlier than" the oldest state); a record whose hash isn't the content's reads as missing.
- Every write path through `InMemoryNoteStore`, each a test: write, edit, replace, mv with and without a link rewrite (in the moved note and in a linking one), archive, rm and restore from the trash, restore of a version (changed lines to "(restore)" only), checkpoint restore, cp, calendar set, a board move, a flag; and a forged `Authorship` on `next` ignored.
- `blame`: runs, `--lines`, a section, a folder with its total and its cap, unrecorded lines, a dropped record, an attachment refused, the help's first line; with a labels fake showing two members as "another user", one run.

Server (xUnit, `tests/Kumbu.Server.Tests`):
- The payload round trip, sealed; a payload without `by` reads null; re-keying, the trash and a move keep the record; a downgrade's stale record (hash mismatch) backfills.
- Two writers racing on one note: the record always describes the stored content.
- A WebDAV `PUT` and a device key's sync write are attributed to the share and the device.
- `/api/blame`: sections, main writer, "others", the front matter and top sections, a member's view of another member and of the owner's key, 404 for a hidden discreet vault, 400 for an attachment.

Web (Vitest):
- The window: legend counts, gutters, "and others", "before …" and the sentence, the detail on activation, the phone layout, names present without colour.
- The menu item in read mode only, and in the palette's Note group; the route of a discreet note shows `/discreet`.

## 11. Build plan outline

1. **Core record.** `Authorship` with `After` and `Of`, `Myers.Diff` with a budget; measure §9's figures. Tests of §10 Core, first two blocks.
2. **Store.** The `StoredNote` field, `NotePayload.by`, the computation in `EncryptedNoteStore` and `InMemoryNoteStore`, `ApproxBytes`; the write-path and server tests.
3. **`sh blame`.** The command, its help, the line in `Manual.ToolDescription`; its tests.
4. **REST.** `/api/blame` with sections, the OpenAPI types for the web client; its tests.
5. **Web window.** Route, neutral address, menu item, the window; its tests; `pnpm -C web build`.

Then the 0.6.0 changelog entry in Kumbu² (`/SWE/Kumbu/Versions/`), the day the pull request merges.

## 12. UX re-read, as a user who expects Kumbu² to be human-centric

- **Brita, polishing her protocol.** She opens "Who wrote what" and sees her sections and the agent's, named, in her text's order. Good. Where the agent fixed one typo of hers, the section reads "Brita and others": true, and the detail shows it is one line. Acceptable; a threshold would hide exactly what she checks.
- **She renamed a note the protocol links to.** The lines holding those links now read "Brita (mv)" if she renamed it, or the agent's name with "(mv)" if it did. By the rule (decision 5) that is right, and the mark says why; it could still surprise. **Decided by Chris on 2026-11-18: no exception.** mv's rewrite gives the line to the mover, marked "(mv)", because one rule holds (decision 5) and the anti-goal forbids a changed line under an earlier writer; the mark says what happened.
- **The day of the upgrade.** Older lines read "earlier than …" with one plain sentence why; nothing claims more than is known.
- **Duplicate of a protocol** makes every line hers. Right for templates; for a "Protocol v2" it loses the agent's part in the copy, while the original keeps it. Worth a line in the help of `blame`, not more for now.
- **The word.** The web app never says "blame"; it says "Who wrote what". Only the console has `blame`, for agents and those who know git.
- **Shared vaults.** A member sees other members as "another user", as everywhere; no one learns more about anyone than the history already told them.
- **No score.** Counts of sections, no percentages, no "most active": it shows work, it doesn't rate people.

## 13. Builder check: what an implementer could read two ways

- *Which current?* The store's working-set entry at the revision being replaced, never the caller's `current` object (§5.4).
- *Runs in `sh`* are formed after rendering, by the shown name; *sections in the web* group by identity (mark stripped). Two different rules on purpose: `sh` shows the mark, the legend counts people.
- *A run's time* is its newest line's; `--lines` gives each line's own.
- *Blank lines* carry a writer and show in runs; they count in no total and decide no section.
- *Restore from the trash* goes through `TryRestoreFromTrashAsync`, which computes from the trashed note: same content, so the record comes back unchanged.
- *`Earlier`* is per note; a note can hold recorded and unrecorded runs mixed after backfill, never two `Earlier` times.
- *Budget cells* are counted for the middle after the common start and end (§5.5), not for the whole text.

## 14. Open points

- Rendering a section on its own breaks footnotes and reference-style links whose definition sits in another section; accepted for a read-only view, to revisit if it shows.
- A checkpoint restore of a removed note gives every line to the restorer; a record per checkpoint note would keep them, at a small cost, if people miss it.
