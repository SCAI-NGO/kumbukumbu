# Workspace PR 3a: who reads and writes the editor's state

Task 3a.1 of `2026-11-18-workspace.md`. Every reader and writer in `web/src` (tests left out) of the editor store's `currentPath`, `mode`, `setMode`, `openNote`, `endWrite`, `beforeRead`, `unsaved`, `draft`, the note actions' flush, `noteTool`, `find`, `live`, `reading` and `goToLine`, each marked **focused** (the focused note: what the top bar, the dock and the palette show) or **written** (the note being written: the one editor and its save state). Line numbers are those of `63797f2`.

The rule from the spec: `currentPath` and `mode` mean the focused pane's; `beforeRead`, `unsaved`, `draft`, the flush, `live` and `goToLine` belong to the one editor, so to the pane in Write mode. Without a second pane on screen (until PR 3b) the focused pane and the written one are always the same, so every reader below keeps working unchanged; the column "3b" names those that must then read the writing pane rather than the focused one.

The note actions' flush is not called `flushDraft` (as the spec and the plan say): it is `beforeRead({ reread: false })` in `duplicate` (`web/src/shell/useNoteActions.tsx:111`).

| Where | What | Meaning | 3b |
|---|---|---|---|
| `shell/MobileShell.tsx:153-157` | `live`, `reading`, `mode`, `goToLine` (side panel) | written (`live`, `goToLine`); focused (`reading`, `mode`) | the panel of a pane: `live` only where its path matches, as now |
| `shell/MobileShell.tsx:174` | `mode`, `noteTool`, `openNote` (picked) | focused | |
| `shell/MobileShell.tsx:333` | `currentPath`, `openNote` (the URL followed) | focused | now per pane (`panes`, 3a.3) |
| `shell/MobileShell.tsx:349` | `currentPath` (rename problem shown) | focused | |
| `shell/MobileShell.tsx:373` | `mode`, `setMode("read")` (read-only note) | focused | the writing pane's note |
| `shell/MobileShell.tsx:394` | `openNote(created, "write")` (new note) | focused | |
| `shell/MobileShell.tsx:463-464` | `mode`, `noteTool` (find bar, dock) | focused | |
| `shell/TopBar.tsx:34` | `mode` | focused | |
| `shell/TopBar.tsx:85` | `setNoteTool("outline")` | focused | |
| `shell/ModeToggle.tsx:18` | `setMode("write")` | focused | |
| `shell/ModeToggle.tsx:26-27,44` | `mode`, `endWrite` | focused | Read on a reading pane must not end the other's Write |
| `shell/TreeDrawer.tsx:427` | `beforeRead` (sign out) | written | |
| `shell/TreeDrawer.tsx:536,828` | `currentPath` (current row) | focused | the other pane's row gets its quieter mark |
| `shell/useNoteActions.tsx:45,86,148` | `mode` (items that fit the mode) | focused | |
| `shell/useNoteActions.tsx:111` | `beforeRead({ reread: false })` (the flush before Duplicate) | written | |
| `shell/useNoteActions.tsx:122,148` | `setNoteTool("find" / "addProperty")` | focused | |
| `shell/useNoteMenuKey.ts:71,87` | `mode`, `caretAt` | focused (`mode`); written (`caretAt`) | |
| `shell/MoveDialog.tsx:67` | `beforeRead` | written | |
| `shell/MoveDialog.tsx:75-77` | `currentPath` (URL follows a move) | focused | the other pane's note too (`moved` maps both) |
| `shell/RenameVaultDialog.tsx:28-29` | `currentPath`, `beforeRead` | written | the writing pane's vault |
| `shell/MemberPanel.tsx:104` | `currentPath` (URL follows an alias) | focused | both panes |
| `note/NoteView.tsx:121,220,331,340` | `mode` | focused | the pane's own mode |
| `note/NoteView.tsx:69,234` | `setMode` (load failed; restored after a reload) | focused | |
| `note/NoteView.tsx:139,484` | `openNote(created, "write")` (a link to a new note) | focused | the link's pane |
| `note/NoteView.tsx:168-173,331-340,365,375,612,631-632` | `noteTool`, `find`, `setFind`, `setNoteTool` | focused | |
| `note/NoteView.tsx:351-369` | `reading`, `setReading` | focused | |
| `note/NoteTools.tsx:75-106` | `find`, `setFind`, `setNoteTool` (find bar) | focused | |
| `note/MarkdownEditor.tsx:94-97,205,214` | `draft`, `setDraft` | written | |
| `note/MarkdownEditor.tsx:147` | `openNote(target, "write")` (named untitled note) | written | |
| `note/MarkdownEditor.tsx:183-188` | `live`, `setLive` | written | |
| `note/MarkdownEditor.tsx:191-200` | `setGoToLine` | written | |
| `note/MarkdownEditor.tsx:229-236` | `setBeforeRead(guard, unsaved)` | written | |
| `note/MarkdownEditor.tsx:266,377` | `endWrite` (Ctrl+S, Done) | written | |
| `note/MarkdownEditor.tsx:274-294,305` | `currentPath`, `mode` held and set while a navigation is blocked | written | must hold the writing pane, not the focused one; and a change of `beside` alone is not blocked today |
| `calendar/api.ts:140-144` | `mode`, `currentPath`, `endWrite`, `beforeRead` | written | the writing pane's path |
| `calendar/CalendarPage.tsx:99` | `openNote(path, "write")` | focused | |
| `calendar/EntryLine.tsx:161` | `setMode("write")` | focused | |
| `search/SearchSheet.tsx:54` | `openNote(path)` | focused | |
| `update/apply.ts:107-111` | `mode`, `beforeRead`, `draft` | written | |
| `update/UpdateBar.tsx:20` | `beforeRead`, `draft` | written | |
| `api/sharing.ts:173` | `currentPath` (URL follows a vault renamed) | focused | both panes |
| `router.tsx:38` | `mode`, `currentPath` set at a session's end | both | both panes close (done in 3a) |
| `discreet/hideAgain.ts:76-79,137-141` | `currentPath`, `beforeRead`, `unsaved` | written | done in 3a.4 |
| `discreet/hideAgain.ts:144-145` | `draft` | written | |
| `discreet/hideAgain.ts:162-178` | `noteTool`, `mode`, `openNote` | focused | the pane that stays (done in 3a.4) |
| `discreet/leftElsewhere.ts:31-34` | `currentPath`, `unsaved`, `draft` | written | done in 3a.4 |
| `discreet/openNote.ts:16-17` | `currentPath` (the discreet note the server late-saves) | written | the writing pane's note |
| `discreet/state.ts:81` | `forgetShown` | both | panes close in `forgotten()` (done in 3a) |
