# Empty folders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A folder holding a zero-byte `.vaultkeep` exists even when nothing else is in it: made with `mkdir` in `sh` and "New folder" in the web app, removed with `rm <folder>` and "Remove folder", hidden from every listing, and carried through export, import, folder upload, seeding, checkpoints, activity and the sync API.

**Architecture:** The marker is stored as an **attachment record with a reserved name**: a new `AttachmentFormat.Marker` (kind `Folder`, media type `application/octet-stream`, content must be empty) recognised by the exact file name `.vaultkeep`. Every path that copies a vault as it is (export, checkpoints, sync, re-keying, trash, moves, deletion, blob sweep) already iterates attachments and so carries markers with no change; only the presentation points (`ls`, the tree, `history`, `trash`, checkpoint diffs, link resolution, a few counts, the web's sheets) learn to hide the marker or show it as its folder. Writes go through `AttachmentWriter`, so rights, writer, changeset, activity and trash are the attachments' own. Over REST a marker is just a file at `…/.vaultkeep` on `/api/files`; no endpoint is added.

**Tech Stack:** .NET 10, xUnit, MongoDB (unchanged schema); React 18, Vite, Vitest, openapi-fetch, fflate.

**Spec:** `docs/superpowers/specs/2026-10-30-empty-folders-design.md` (read it first). This plan covers everything except its section "Obsidian plugin (0.1.2)", which gets its own plan in `kumbu-obsidian`, written from "Interface for the plugin" below. Code map taken at `9a603c7`; line numbers may have moved.

## Global Constraints

- British English in code, comments, docs and commit messages; `// SPDX-License-Identifier: AGPL-3.0-or-later` atop new source files where siblings have it. No organisation, customer or employer names; examples use `/Work/Drafts`.
- Commit messages imperative and free-form, no `feat:`-style prefixes. Branch `empty-folders`, one pull request, squash merge.
- **Name:** `.vaultkeep`, matched exactly (ordinal, case-sensitive) as the **last** segment, directly inside the folder it keeps: `/Work/Drafts/.vaultkeep`. **Zero bytes.** Its version token is `VersionToken.Of([])` = `e3b0c44298fc`.
- **Only the deepest folder** gets one: `mkdir /Work/Drafts/2026` writes `/Work/Drafts/2026/.vaultkeep` only.
- **It stays** when content arrives; removing the last note leaves the folder.
- **Never shown** in `ls`, the tree, `rg --files` (the shell's `find`), `rg`, `semsearch`, search results, the activity, a note picker. Where a removed or changed marker must be named (activity, trash, `history`, checkpoint diffs) it is named as its folder, `/Work/Drafts/`, with the kind `folder`.
- **Listed as itself** only where the vault is copied as it is: export, import, checkpoints, the sync API, `/api/files`.
- A write like any other: write rights, the writer in history and activity, the writer's changeset, the trash for 30 days.
- `mkdir <path>` has **no options and no `-p`**; parents are always implied. `help mkdir` says "an empty folder stays until you remove it".
- An existing vault changes in no way without someone making a folder (no migration, no new collection, no new field).
- Build and tests: `dotnet test --solution Kumbu.slnx`; `pnpm -C web install --frozen-lockfile`; `pnpm -C web test --run`; `pnpm -C web build`.

## Review Focus

1. **Moving a kept folder** (`mv /Work/Drafts/ /Work/Old/`, the web's "Rename or move", the plugin's per-file `/api/move`) must carry the marker. `NoteNames.NameProblem` refuses every dot segment today, so without Task 1's exception *every* move of a kept folder fails as a whole. Test in Task 3 (`Moving_a_kept_folder_moves_its_marker`) and Task 5.
2. **A folder whose only content is a kept subfolder** (`/Work/A/.vaultkeep`, `/Work/A/B/.vaultkeep`): `rm /Work/A` refuses and counts 1 entry; the tree shows A with B inside; "Remove folder" is offered on B, not on A. Tests in Tasks 2 and 8.
3. **Folder names from macOS arrive in NFD** (upload, import, plugin). `Bespréchung/.vaultkeep` and its NFC spelling are one folder, never two. Test in Task 5 (`An_nfd_folder_name_keeps_one_folder`); `readImport` and `collectFiles` already normalise.
4. **An export, unpacked and zipped again by macOS,** carries `__MACOSX/Work/Drafts/._.vaultkeep` beside the marker: only the exact name is a marker, the resource fork stays ignored, and the empty folder comes back. Test in Task 9.
5. **An agent that types a marker path** (`cat /Work/Drafts/.vaultkeep`, `ls`, `rm`, `mv`, `history`) gets "No such file or directory", as for any path that doesn't exist; nothing describes the marker. Test in Task 3.

## Marker storage (decision)

The marker is an attachment record with a reserved name, not a note and not a third kind. Attachments already travel through every place that copies a vault as it is (export at `Api/ExportApi.cs:113,154`, checkpoints, sync at `Api/SyncApi.cs:122`, re-keying, trash, moves, deletion, blob sweep), whereas a note would also reach search, tags, links, the board and the text tools, and a third kind would need its own collection in all of those; the attachment route touches only the handful of presentation points listed in Tasks 3–5 and 7. The cost is small and known: the account-deletion receipt counts markers among attachments (sealed, so they can't be told apart without the key), and every marker of a vault shares one zero-byte blob.

## Interface for the plugin

What the Obsidian plugin can rely on from a server with this change (from Task 5 on). Paths are from the root of the vault tree; `{vault}` and each segment URL-encoded as for any file.

- **Version list:** `GET /api/vaults/{vault}/versions?attachments=1` lists each marker as an entry `{"path": "/Work/Drafts/.vaultkeep", "version": "e3b0c44298fc", "kind": "folder"}`, in path order with the rest; a removed or moved-away marker appears in `deleted` of a `since` answer; markers count in `state` (and the `ETag`). Without `attachments=1` no marker path appears anywhere in the answer, as for every attachment.
- **Read:** `GET /api/files/{vault}/Drafts/.vaultkeep` → `200`, empty body, `Content-Type: application/octet-stream`, `ETag: "e3b0c44298fc"`. `404` when there is none.
- **Write:** `PUT /api/files/{vault}/Drafts/.vaultkeep` with an empty body and no `version` → `201` with `FileStateView` (`mediaType` `application/octet-stream`, `size` 0, `version` `e3b0c44298fc`). It exists already → `409` with `FileConflictView` (its `current.version` is the same token, so a client can treat it as done). A body that isn't empty → `400` ("A .vaultkeep keeps its folder and holds nothing; send it empty."). A marker directly in the vault (`/Work/.vaultkeep`) → `400` ("A vault exists on its own; it needs no .vaultkeep."). A dot segment above it (`/Work/.obsidian/.vaultkeep`) → `400`. A note or file standing where a folder of the path must be → `409` without a file state. A read-only key → `403`.
- **Delete:** `DELETE /api/files/{vault}/Drafts/.vaultkeep?version=e3b0c44298fc` → `204`; the marker goes to the trash. Allowed whatever the folder holds (the folder then exists only while something is in it). `404` if gone.
- **Move:** `POST /api/move` with `from` and `to` both ending in `/.vaultkeep` moves a marker like a file (`MovedView.version` `e3b0c44298fc`); a `to` with any other name is refused (`400`). A folder move (`from` ending in `/`) moves the marker with the rest.
- **Hints:** a marker's write, move or removal sends the vault's change hint on `/api/ws` as any write does.
- **An older server** (before this change) answers `400` to `GET`/`PUT /api/files/…/.vaultkeep` ("…neither a note nor a file Kumbu² keeps…" or the unsupported-format sentence) and never lists `kind: "folder"`. The plugin treats such a `400` as final for that path.

## Calls taken in this plan (for review)

- No new REST endpoint: the web app's "New folder" and "Remove folder", the import and the folder upload write and delete markers with `PUT`/`DELETE /api/files`, as the plugin does; the "exists already" check of "New folder" is made against the tree, and a race only writes a marker into a folder that exists anyway.
- `mkdir` on a folder that exists (kept, implied, or a vault) answers without an error: parents are always implied, so it behaves as `mkdir -p` would. A folder name with an extension (`v1.png`) is allowed; only an existing note or file at the path is an error.
- A removed folder is listed in `trash` and `history` as `/Work/Drafts/` with the word `folder`, and `restore /Work/Drafts/` brings it back. "Remove folder" asks no confirmation.
- A marker directly in a vault is refused over REST and ignored by import, upload and seeding. A `.vaultkeep` with content is refused over REST (the plugin's copy would otherwise never match); import, upload and seeding send or store it empty, since it carries nothing.
- The account-deletion receipt counts markers among attachments (see "Marker storage"). A checkpoint's restore message counts a returning folder among "back".
- The spec's `tree` and `find` are not shell commands (`Shell.Hints` points to `ls` and `rg --files`); the plan tests `rg --files` and the web tree API in their place.

---

## Part A — server and core

### Task 1: The marker format and its writes

**Files:**
- Create: `src/Kumbu.Core/Attachments/FolderMarker.cs`
- Modify: `src/Kumbu.Core/Attachments/AttachmentFormat.cs` (enum `AttachmentKind.Folder`; `Marker`; `ForName`, `Problem`, `KindOf`, `Description`, `RenameProblem`), `src/Kumbu.Core/Paths/VaultPath.cs:34` (doc of `IsAttachment`: markers too), `src/Kumbu.Core/Notes/NoteNames.cs:24` (`NameProblem`), `src/Kumbu.Core/Attachments/AttachmentWriter.cs` (`CheckNewAsync`, messages), `src/Kumbu.Core/Sh/AttachmentText.cs:33` (`Summary`)
- Test: `tests/Kumbu.Core.Tests/Attachments/AttachmentFormatTests.cs`, `tests/Kumbu.Core.Tests/Attachments/AttachmentWriterTests.cs`, `tests/Kumbu.Core.Tests/Paths/VaultPathTests.cs`

**Interfaces:**
- Produces:
  - `public static class FolderMarker { const string Name = ".vaultkeep"; const string MediaType = "application/octet-stream"; static string Version { get; } /* VersionToken.Of([]) */; static bool Is(VaultPath path); static VaultPath In(VaultPath folder); static VaultPath FolderOf(VaultPath marker); static string Shown(VaultPath path) /* "/Work/Drafts/" for a marker, else path.Value */; static AttachmentState State(string blobId) }`
  - `AttachmentKind.Folder`; `AttachmentFormat.Marker` (Extension `.vaultkeep`, `FolderMarker.MediaType`, `AttachmentKind.Folder`), **not** in `All`; `ForName(".vaultkeep")` returns it (exact name, checked before the extension loop); `KindOf` → `"folder"`; `Problem` → null for empty content, else `"A .vaultkeep keeps its folder and holds nothing; send it empty"`.
  - `NoteNames.NameProblem` lets the last segment through when it is exactly `.vaultkeep`; every other dot segment stays refused.
  - `AttachmentWriter.WriteAsync` on a marker: refuses a marker directly in a vault (`"A vault exists on its own; it needs no .vaultkeep"`, `ShellErrorKind.Invalid`); names its tool `mkdir` and the path as `FolderMarker.Shown` in every message.
  - `AttachmentText.Summary(state)` → `"folder"` for a marker state.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData(".vaultkeep", true)]
[InlineData("x.vaultkeep", false)]
[InlineData(".VaultKeep", false)]
[InlineData(".vaultkeep.md", false)]
public void Only_the_exact_name_is_the_folder_marker(string name, bool marker) // ForName(name) == Marker ⇔ marker; KindOf("/W/D/" + name) == "folder" ⇔ marker
[Fact] public void The_marker_is_no_format_listed_to_users()        // !AttachmentFormat.All.Contains(Marker); UnsupportedMessage unchanged
[Fact] public void A_marker_must_be_empty()                         // Problem(/W/D/.vaultkeep, []) null; Problem(…, [0x20]) == "A .vaultkeep keeps its folder and holds nothing; send it empty"
[Fact] public void The_marker_version_is_the_token_of_no_bytes()    // FolderMarker.Version == "e3b0c44298fc"
[Fact] public void NameProblem_lets_a_marker_through_only_as_the_last_segment() // /Work/D/.vaultkeep null; /Work/.vaultkeep/a.md and /Work/.x/.vaultkeep refused
```

```csharp
[Fact] public async Task A_marker_is_written_like_a_file_in_the_writers_changeset()   // WriteAsync("/Work/Drafts/.vaultkeep", [], null, null): created; Current.Size 0, MediaType octet-stream, Version e3b0c44298fc; Changeset equals that of a note written just before
[Fact] public async Task A_marker_directly_in_a_vault_is_refused()                     // "/Work/.vaultkeep" → Invalid, "A vault exists on its own; it needs no .vaultkeep"
[Fact] public async Task A_marker_below_a_note_is_not_a_directory()                    // note /Work/plan.md; "/Work/plan.md/.vaultkeep" → Conflict, "mkdir: /Work/plan.md: Not a directory"
[Fact] public async Task Removing_a_marker_names_its_folder()                           // RemoveAsync → "removed /Work/Drafts/; it stays in the trash for 30 days ('restore /Work/Drafts/' brings it back)"
```

- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter "AttachmentFormat|AttachmentWriter|VaultPath"` — expect FAIL (`FolderMarker` missing).
- [ ] **Step 3: Implement** the interfaces above. `RenameProblem` for a marker source: null when the target is a marker, else `".vaultkeep keeps its name"`.
- [ ] **Step 4: Run** the filter again — expect PASS; then the whole `Kumbu.Core.Tests` — no other test changes.
- [ ] **Step 5: Commit** `git commit -m "Recognise .vaultkeep as a folder marker kept like an attachment"`

### Task 2: `mkdir`, `rm` of an empty folder, `restore` of a folder

**Files:**
- Create: `src/Kumbu.Core/Sh/Commands/Mkdir.cs`
- Modify: `src/Kumbu.Core/Attachments/AttachmentWriter.cs` (two methods), `src/Kumbu.Core/Sh/Commands/Rm.cs`, `src/Kumbu.Core/Sh/Commands/Restore.cs:54`, `src/Kumbu.Core/Sh/Shell.cs` (register `Mkdir` after `Mv` when `files` isn't null; drop the `"mkdir"` entry of `Hints`), `src/Kumbu.Core/Sh/Manual.cs` (`ToolDescription`: a `mkdir` line after `mv`; `rm` and `trash` lines), `src/Kumbu.Server/Metrics/KumbuMetrics.cs:384` (`"mkdir"` in `KnownCommands`, after `"mv"`)
- Test: `tests/Kumbu.Core.Tests/Sh/FolderCommandTests.cs` (new; set up as `AttachmentCommandTests`), `tests/Kumbu.Core.Tests/Sh/HelpTests.cs`; `tests/Kumbu.Server.Tests/Metrics/ToolMetricsTests.cs` must pass unchanged

**Interfaces:**
- Consumes: Task 1's `FolderMarker`, `AttachmentWriter.WriteAsync`/`RemoveAsync`/`RestoreAsync` on marker paths.
- Produces:
  - `AttachmentWriter.MakeFolderAsync(VaultPath folder, CancellationToken ct) -> Task<ShellResult>`
  - `AttachmentWriter.RemoveFolderAsync(VaultPath folder, CancellationToken ct) -> Task<ShellResult>`
  - `sealed class Mkdir(AttachmentWriter files) : ICommand` — `Name "mkdir"`, `Usage "mkdir <path>"`, `Summary "make a folder; an empty folder stays until you remove it"`

Exact answers (the spec's behaviour, worded here once):

| Case | Result |
|---|---|
| new folder | Ok `made folder /Work/Drafts/; an empty folder stays until you remove it (rm /Work/Drafts)` |
| exists (kept or implied) | Ok `/Work/Drafts/ exists already; nothing was written` |
| a vault | Ok `/Work/ is a vault and exists already; nothing was written` |
| no such vault | Error `mkdir: there is no vault 'Wrk' (ls lists the vaults; mkdir does not create vaults)` |
| a note / file at the path | Error `mkdir: /Work/plan.md: File exists (it is a note)` / `(it is a file)` |
| read-only | Error from `ICaller.Refuse("mkdir", …)` |
| `-p` | Error `mkdir: there is no -p: the folders above are always made` |
| `rm` kept empty folder | Ok `removed folder /Work/Drafts/; it stays in the trash for 30 days ('restore /Work/Drafts/' brings it back)` |
| `rm` folder with content | Error `rm: /Work/Drafts: Is a directory holding 3 entries (rm removes one note or file at a time, or an empty folder)` — entries = notes and files below, at any depth, the folder's own marker left out (a kept subfolder's marker counts as one) |
| `restore /Work/Drafts/` | no note or file at the path and its marker in the trash: `files.RestoreAsync(FolderMarker.In(path).Value, null, ct)` → `restored /Work/Drafts/ from the trash` |

`help mkdir`:

```
mkdir <path>
  Make a folder; the folders above it are made as needed. An empty folder stays until you remove it
  (rm <path>), so it can wait for what goes into it: a place for drafts, or a vault laid out first.
  A folder that exists already is left as it is. There are no options.

  mkdir /Work/Drafts
  mkdir /Work/Projects/2026
```

- [ ] **Step 1: Write the failing tests** in `FolderCommandTests`: `Mkdir_makes_a_kept_folder_that_ls_lists` (ls /Work has `Drafts/`; the store holds `/Work/Drafts/.vaultkeep`), `Mkdir_writes_only_the_deepest_marker` (`mkdir /Work/A/B` → no `/Work/A/.vaultkeep`), `Mkdir_on_an_existing_folder_writes_nothing` (kept, and implied by a note; revision unchanged), `Mkdir_on_a_note_path_is_an_error`, `Mkdir_on_a_read_only_vault_is_refused` (`FixedCaller(Writers.Dev, v => false)`), `Mkdir_has_no_p`, `Rm_removes_a_kept_empty_folder_to_the_trash`, `Rm_refuses_a_folder_with_content_and_counts_it` (two notes → "holding 2 entries"), `Rm_counts_a_kept_subfolder_as_an_entry` (Review Focus 2: `/Work/A/.vaultkeep`, `/Work/A/B/.vaultkeep` → "holding 1 entry"), `Restore_brings_a_removed_folder_back`, `A_folder_stays_when_its_last_note_goes` (mkdir, write a note into it, rm the note; `ls /Work` still has `Drafts/`); in `HelpTests`: `Help_mkdir_says_an_empty_folder_stays`.
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter "FolderCommand|Help|ToolMetrics"` — expect FAIL.
- [ ] **Step 3: Implement.** `MakeFolderAsync` checks in the table's order, then `WriteAsync(FolderMarker.In(folder).Value, ReadOnlyMemory<byte>.Empty, null, null, ct)` and words its result; `Rm` sends a path that is neither note nor attachment to `RemoveFolderAsync` when `files` isn't null. `Rm.Help` and `Summary` say it removes an empty folder; drop "Directories can't be removed".
- [ ] **Step 4: Run** the filter again — expect PASS.
- [ ] **Step 5: Commit** `git commit -m "Add mkdir and let rm and restore take an empty folder"`

### Task 3: The marker stays out of sight in the shell

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/CommandArgs.cs:28` (`TryParsePath`: a marker path is `"{command}: {arg}: No such file or directory"`), `src/Kumbu.Core/Sh/Commands/Cat.cs:49,57,103` (the same for its own parses), `src/Kumbu.Core/Sh/Commands/Ls.cs:87` (a direct marker adds no entry; a folder with nothing but its marker lists as empty), `src/Kumbu.Core/Sh/Commands/History.cs:121,170` (marker rows: what `FolderMarker.Shown`, tail `folder` plus the changeset), `src/Kumbu.Core/Sh/Commands/Trash.cs:42` (`{time}  /Work/Drafts/  folder  (by …)`), `src/Kumbu.Core/Attachments/StoreAttachments.cs:20` (`PathsUnderAsync` leaves markers out: no link resolves to one), `src/Kumbu.Core/Notes/NoteMover.cs:73` (the count leaves markers out; no empty parentheses when only markers moved)
- Test: `tests/Kumbu.Core.Tests/Sh/FolderMarkerHiddenTests.cs` (new)

**Interfaces:**
- Consumes: Task 2's `mkdir`/`rm` through the shell.
- Produces: nothing new; behaviour only.

- [ ] **Step 1: Write the failing tests**
  - `Ls_shows_a_kept_folder_and_never_its_marker` — `ls /Work` → `Drafts/`; `ls /Work/Drafts` → `""`; after a note: `ls /Work/Drafts` → `a.md` only.
  - `A_marker_path_is_no_such_file` (Review Focus 5) — theory over `ls`, `cat`, `history`, `rm`, `outline`, `mv /Work/Drafts/.vaultkeep /Work/x/.vaultkeep`: `IsError`, output contains `No such file or directory`.
  - `No_search_or_listing_names_the_marker` — `rg --files /Work`, `rg -l . /Work`, `semsearch drafts /Work`, `tags /Work`: output never contains `.vaultkeep`.
  - `History_of_a_folder_names_the_folder` — `history /Work` has a row with `/Work/Drafts/` and `folder`, none with `.vaultkeep`.
  - `Trash_names_a_removed_folder` — after `rm /Work/Drafts`: `trash /Work` has `/Work/Drafts/  folder`.
  - `A_link_to_vaultkeep_leads_nowhere` — note with `[[.vaultkeep]]`; `links` reports it not found.
  - `Moving_a_kept_folder_moves_its_marker` (Review Focus 1) — `mv /Work/Drafts/ /Work/Old/` → `moved /Work/Drafts/ to /Work/Old/`; `ls /Work` → `Old/`; same with a nested kept folder and a note inside (`(1 note)`, no attachment counted).
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter FolderMarkerHidden` — expect FAIL.
- [ ] **Step 3: Implement** the modifications listed.
- [ ] **Step 4: Run** — expect PASS; whole `Kumbu.Core.Tests` green.
- [ ] **Step 5: Commit** `git commit -m "Keep folder markers out of the shell's listings"`

### Task 4: Checkpoints and activity keep folders

**Files:**
- Modify: `src/Kumbu.Core/Notes/Checkpoints.cs:199–206` (diff rows with `FolderMarker.Shown`: `new      /Work/Drafts/`, `removed  /Work/Drafts/`), the preview lines built from `RestorePreview.Changes` (same display), `Contents` at `:667` (counts leave markers out: a checkpoint of one note and one kept folder is "1 note"), `src/Kumbu.Core/Notes/Activity.cs` (`ActivityFile` doc: `FileKind` may be `"folder"`, its `Path` the marker's)
- Test: `tests/Kumbu.Core.Tests/Sh/CheckpointTests.cs`, `tests/Kumbu.Core.Tests/Notes/ActivityTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: `ActivityFile.FileKind == "folder"` for a marker's state (from `AttachmentFormat.KindOf`, Task 1), `Path` the marker path; Task 5's views and Task 7's sheet rely on it.

- [ ] **Step 1: Write the failing tests**
  - `A_checkpoint_brings_back_a_removed_empty_folder` — `mkdir /Work/Drafts`, `mkcp /Work before`, `rm /Work/Drafts`; `diff /Work --checkpoint before` lists `removed  /Work/Drafts/`; `restore --checkpoint before /Work` previews `/Work/Drafts/`, apply with the plan token; `ls /Work` → `Drafts/`.
  - `A_folder_made_after_a_checkpoint_goes_with_its_restore` — the restore of the vault trashes the marker; `ls /Work` no longer has `Drafts/`.
  - `Lscp_counts_no_marker` — one note and one kept folder → "1 note".
  - `Activity_lists_a_made_folder_in_the_writers_changeset` — a note and a marker written by one writer within the idle time: one row, two files, the marker's `FileKind` `"folder"`, `Kind` `Added`; after `rm`: a row with the marker `Deleted`.
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter "CheckpointTests|ActivityTests"` — expect FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expect PASS.
- [ ] **Step 5: Commit** `git commit -m "Show folders, not their markers, in checkpoint diffs and counts"`

### Task 5: REST: the tree, files, sync and history views

**Files:**
- Modify: `src/Kumbu.Server/Api/NoteApi.cs:279` (`TreeOf`: a marker is never a `TreeNote`; its folder stays, also when empty), `src/Kumbu.Server/Api/SyncApi.cs:17,23` (doc comments: `kind` may be `folder`), `src/Kumbu.Server/Api/NoteApi.cs:52` and `HistoryApi.cs` (doc comments of `TreeNote`, `TrashEntryView`, `ActivityFileView`, `RestoreChangeView`: `folder`), `src/Kumbu.Server/Api/HistoryApi.cs:418` (`CheckpointEntryView.Attachments` leaves markers out)
- `FileApi.cs` needs no change (the format check, the image-only header read, serving and deleting all follow `AttachmentFormat`); its OpenAPI `MediaTypes` stay as they are, so `web/src/api/schema.d.ts` needs no regeneration.
- Test: `tests/Kumbu.Server.Tests/Api/FolderMarkerApiTests.cs` (new; `[Collection("app")]`, `RestClient` as in `FileApiTests`)

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces: the surface in "Interface for the plugin" above; the web app (Tasks 7–9) uses `PUT`/`DELETE /api/files/{vault}/{path}` with `path` ending in `/.vaultkeep`.

- [ ] **Step 1: Write the failing tests**
  - `The_tree_shows_a_kept_folder_and_never_its_marker` — `GET /api/vaults/Work/tree`: folder `Drafts` with no notes; after a note there, `notes` holds only it.
  - `A_marker_round_trips_over_the_files_api` — `PUT` empty → 201, `mediaType` `application/octet-stream`, `size` 0, `version` `e3b0c44298fc`; `GET` → 200, 0 bytes, `ETag "e3b0c44298fc"`; second `PUT` → 409 with `current.version` `e3b0c44298fc`; `DELETE ?version=e3b0c44298fc` → 204; `/api/vaults/Work/trash` has the entry with `kind` `folder`.
  - `A_marker_with_content_or_in_a_vaults_root_or_a_dot_folder_is_a_400` — three cases, the sentences of the interface section.
  - `A_read_only_key_cannot_write_a_marker` — 403.
  - `Versions_list_a_marker_with_kind_folder_only_with_attachments` — full list with `attachments=1` has the entry; a `since` answer after `DELETE` has it in `deleted`; without `attachments=1`, neither answer contains `.vaultkeep`.
  - `A_marker_moves_by_path_and_with_its_folder` — `/api/move` marker → marker: 200, `version` `e3b0c44298fc`; marker → `/Work/x.png`: 400; `from: "/Work/Old/"` folder move carries it.
  - `Activity_names_a_made_folder` — `/api/vaults/Work/activity` row with `fileKind` `folder`.
  - `An_nfd_folder_name_keeps_one_folder` (Review Focus 3) — `PUT` of `Bespréchung/.vaultkeep`, then of the NFC spelling → 409; tree has one folder.
  - `A_checkpoint_entry_counts_no_marker` — `attachments` 0 for a checkpoint of a kept folder.
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter FolderMarkerApi` — expect FAIL (tree lists `.vaultkeep`).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expect PASS; then the whole server suite.
- [ ] **Step 5: Commit** `git commit -m "Serve folder markers over the files and sync APIs and hide them in the tree"`

### Task 6: Export, seeding and the security document

**Files:**
- Modify: `src/Kumbu.Server/Notes/SeedScanner.cs` (`SeedScan` gains `Folders`; a file named exactly `.vaultkeep` whose folders have no dot segment and that lies at least one folder inside a vault folder names its folder, whatever it holds; `Vault/.vaultkeep` is skipped silently; every other dotfile stays ignored), `src/Kumbu.Server/Notes/Seeder.cs:88–116` (a vault folder with folders but no notes is filled, not left alone; one `PutBlobAsync(vault, empty)` and a `NoteBatch` of `AttachmentCreates` with `FolderMarker.State(blob)`, writer `Writers.Seed`, committed with `TryCommitAsync`; the log line counts folders: `Seeded 3 notes and 1 folder into …`), `docs/security-architecture.md` §5.4 (one bullet after "Attachments")
- `ExportApi.cs` needs no change: markers are attachments with a zero-byte blob, written as `.vaultkeep` entries where they are, for vaults and checkpoints alike.
- Test: `tests/Kumbu.Server.Tests/Api/ExportApiTests.cs`, `tests/Kumbu.Server.Tests/Notes/SeedScannerTests.cs`, `tests/Kumbu.Server.Tests/Notes/SeederTests.cs`

**Interfaces:**
- Consumes: Task 1's `FolderMarker`.
- Produces: `public sealed record SeedScan(IReadOnlyList<SeedFile> Files, IReadOnlyList<string> Warnings, IReadOnlyList<string> Vaults, IReadOnlyList<VaultPath> Folders)`.

The bullet for `docs/security-architecture.md` §5.4, as written:

> - **Folder markers** (`.vaultkeep`, which keep an empty folder) are attachment records like any other: their path is sealed in the record, their bytes are one zero-byte blob per vault, and they are versioned, trashed, checkpointed, exported and re-keyed with the attachments. Making one is a write with the rights any write needs (`sh mkdir`, `PUT /api/files`).

- [ ] **Step 1: Write the failing tests**
  - `An_export_holds_an_empty_folder_as_a_zero_byte_vaultkeep` — ZIP of `/Work` has entry `Work/Drafts/.vaultkeep`, length 0; the export of a checkpoint holding it too.
  - `Collects_vaultkeep_markers_as_folders` — `Work/Drafts/.vaultkeep` (with content `x`) → `Folders` = `/Work/Drafts`; `Work/.vaultkeep`, `Work/.obsidian/a/.vaultkeep`, `Work/Drafts/.gitkeep` → none, and no warning.
  - `Seeding_makes_the_kept_empty_folders` — seed folder `Work/Drafts/.vaultkeep` alone: the vault is created and `ls /Work` → `Drafts/`.
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter "ExportApi|SeedScanner|Seeder"` — expect FAIL on the seed tests (the export test may pass already; keep it as the guard).
- [ ] **Step 3: Implement** the scanner, the seeder and the document bullet.
- [ ] **Step 4: Run** — expect PASS.
- [ ] **Step 5: Commit** `git commit -m "Seed empty folders and pin markers in exports"`

## Part B — web app

### Task 7: Folders in paths, trash, activity and checkpoint previews

**Files:**
- Modify: `web/src/api/paths.ts` (`FileKind` gains `"folder"`; `fileKindOf` → `"folder"` for a last segment exactly `.vaultkeep`; `isAttachment` true only for `image` and `pdf`), `web/src/components/fileKindIcon.ts` (`folder: Folder`), `web/src/shell/TrashSheet.tsx` (a folder entry: name `Drafts/`, crumb of its parent, Restore as for any entry, no "Open"), `web/src/shell/ActivitySheet.tsx` (`kindOf` takes `"folder"`; a folder file is not a button; its text `Drafts/` (`renameText` with folders for a move); its label `made` / `removed` / `moved` for `added` / `deleted` / `renamed`; `countsText` counts folders as "folder(s)"), `web/src/shell/CheckpointsSheet.tsx:249` (`shownPath(c.path)`; a folder's change text "comes back" / "goes to the trash")
- Test: `web/src/api/paths.test.ts`, `web/src/shell/ActivitySheet.test.tsx`, `web/src/shell/HistorySheets.test.tsx` (trash)

**Interfaces:**
- Produces in `web/src/api/paths.ts`: `export const MARKER = ".vaultkeep"`, `isMarker(path: string): boolean`, `markerOf(folder: string): string` (`/Work/Drafts` → `Drafts/.vaultkeep`, below the vault, as `putFile` takes it), `shownPath(path: string): string` (`/Work/Drafts/.vaultkeep` → `/Work/Drafts/`, any other path as it is).

- [ ] **Step 1: Write the failing tests**: `fileKindOf names a marker a folder and nothing else` (`x.vaultkeep`, `.VaultKeep` stay notes), `markerOf and shownPath`, `the activity shows a made folder as the folder, not a button`, `the trash lists a removed folder and restores it` (mock `POST` restore with the marker path).
- [ ] **Step 2: Run** `pnpm -C web test --run src/api/paths.test.ts src/shell/ActivitySheet.test.tsx src/shell/HistorySheets.test.tsx` — expect FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expect PASS.
- [ ] **Step 5: Commit** `git commit -m "Show folders, not their markers, in the trash, activity and checkpoints"`

### Task 8: New folder and Remove folder

**Files:**
- Create: `web/src/shell/NewFolderDialog.tsx` (as `NewVaultDialog`: title "New folder", field "Name", buttons "Cancel" / "Create folder")
- Modify: `web/src/shell/TreeDrawer.tsx:54` (`FolderMenu`: "New folder…" after "New note here", for the vault's root and every folder; "Remove folder" while `folder.folders.length === 0 && folder.notes.length === 0` and the folder isn't the vault root — the tree hides the marker, so such a folder exists only through it), `web/src/shell/Dock.tsx:17` ("New folder…" beside "New note", in the dock's folder), `web/src/shell/MobileShell.tsx:186` (state for the dialog; `onNewFolder` in `treeProps`), `web/src/state/editor.ts` (`revealFolder`, `selectedFolder`), `TreeDrawer.tsx` `FolderButton` (`aria-current="true"` and the accent background for `selectedFolder`; scrolled into view and focused when it becomes selected)
- Test: `web/src/shell/TreeDrawerFolders.test.tsx` (new, as `TreeDrawerAttachments.test.tsx`)

**Interfaces:**
- Consumes: Task 7's `markerOf`, `web/src/upload/putFile.ts` (`putFile(vault, path, bytes, version?)`), Task 5's API.
- Produces:
  - `NewFolderDialog(props: { parent: string; onClose(): void })` — path = `${parent}/${name.trim()}`; a folder at that path in the tree (`findFolder`) → the message `A folder named "{name}" exists already.` and `revealFolder(path)`, nothing sent; else `putFile(vault, markerOf(path), new Uint8Array(0))`: `created` or `conflict` (made meanwhile) → invalidate `["tree", vault]`, `revealFolder(path)`, close; an `HttpError` → `FormError` with the server's sentence.
  - `useEditor` gains `selectedFolder: string | null` and `revealFolder(path: string): void` (expands the folder and every folder above it, sets `selectedFolder`).
  - "Remove folder": `api.DELETE("/api/files/{vault}/{path}", { params: { path: { vault, path: markerOf(folder) } } })`, no confirmation (the folder is empty and the trash keeps it); 204 or 404 → invalidate the tree.

- [ ] **Step 1: Write the failing tests**: `New folder in a folder's menu writes the marker and selects the folder` (assert the `PUT` URL `/api/files/Work/Drafts/Inbox/.vaultkeep` with an empty body; the folder row then has `aria-current`), `New folder in the vault's menu and in the dock`, `a name that exists as a folder says so, selects it and writes nothing`, `a refused name shows the server's sentence`, `Remove folder is offered only on an empty folder` (Review Focus 2: A holding only kept B → offered on B, not A; a folder with a note → not offered), `Remove folder deletes the marker and the folder goes`.
- [ ] **Step 2: Run** `pnpm -C web test --run src/shell/TreeDrawerFolders.test.tsx` — expect FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expect PASS; `pnpm -C web test --run` all green.
- [ ] **Step 5: Commit** `git commit -m "Let the web app make and remove empty folders"`

### Task 9: Import and folder upload keep empty folders

**Files:**
- Modify: `web/src/data/readImport.ts` (`ImportVault` gains `folders: string[]`, paths of markers below the vault; an entry whose last segment is exactly `.vaultkeep`, with no dot segment before it and at least one folder below the vault, goes there instead of `skipped`, whatever its size and content, never unpacked; `Vault/.vaultkeep` and `__MACOSX/…` stay out silently), `web/src/data/runImport.ts` (after a vault's files: each marker with `importApi.putFile(name, path, new Uint8Array(0), null)`; `created` and `conflict` are both fine; a 400 is listed in `skipped` as for a file; markers count in no total and no summary), `web/src/upload/planUpload.ts` (`UploadPlan` gains `markers: T[]`, by the same rule, never `skipped`), `web/src/upload/UploadSheet.tsx` (sends each marker as zero bytes after the files; uncounted; a pick of nothing but markers reviews as `{n} empty folder(s) to upload` instead of "Nothing to upload")
- Test: `web/src/data/readImport.test.ts`, `web/src/data/runImport.test.ts`, `web/src/upload/planUpload.test.ts`, `web/src/upload/UploadSheet.test.tsx`

**Interfaces:**
- Consumes: Task 5's `PUT /api/files` for markers; `putFile`.
- Produces: `ImportVault.folders: string[]`; `UploadPlan<T>.markers: T[]`.

- [ ] **Step 1: Write the failing tests**
  - `an exported ZIP with an empty folder imports the folder` — ZIP `Work/Drafts/.vaultkeep` (0 bytes) and `Work/a.md` → `folders` `["Drafts/.vaultkeep"]`, nothing skipped.
  - `a macOS re-zip keeps the folder and ignores its resource fork` (Review Focus 4) — plus `__MACOSX/Work/Drafts/._.vaultkeep` → same `folders`, nothing skipped.
  - `other dotfiles stay skipped and a vault-root marker is ignored` — `Work/.gitkeep` skipped as today; `Work/.vaultkeep` neither in `folders` nor `skipped`.
  - `runImport puts each marker empty and counts it nowhere` — fake `ImportApi`: `putFile("Work", "Drafts/.vaultkeep", Uint8Array(0), null)`; a `conflict` answer leaves the summary as with no markers.
  - `planUpload sends markers apart` — `Vault/Drafts/.vaultkeep` in `markers`; `.obsidian/x/.vaultkeep` skipped as Obsidian's.
  - `uploading a folder with an empty subfolder puts its marker` — `UploadSheet` with a picked `Vault/Drafts/.vaultkeep` sends `PUT /api/files/{vault}/{folder}/Vault/Drafts/.vaultkeep` with an empty body.
- [ ] **Step 2: Run** `pnpm -C web test --run src/data src/upload` — expect FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — expect PASS; then `pnpm -C web test --run` and `pnpm -C web build`.
- [ ] **Step 5: Commit** `git commit -m "Bring empty folders along in imports and folder uploads"`

## Part C — finishing

### Task 10: Whole-branch check

- [ ] **Step 1:** `dotnet test --solution Kumbu.slnx` (4 skips without the model and a TensorX key), `pnpm -C web test --run`, `pnpm -C web build` — all green.
- [ ] **Step 2:** By hand against a dev server: `mkdir /Work/Drafts` over MCP, see it in the web tree, reload, sign out and in, restart the server; export the vault and unzip it (`Work/Drafts/.vaultkeep`, 0 bytes); import the ZIP into a new vault; `rm /Work/Drafts`, find it in the trash and the activity, restore it; make a checkpoint, remove the folder, restore the checkpoint.
- [ ] **Step 3:** Push the branch and open the pull request "Keep empty folders with a .vaultkeep marker" (body: the tasks, the marker decision, the plugin interface, the calls listed above).
