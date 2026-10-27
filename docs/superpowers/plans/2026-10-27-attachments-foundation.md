# Attachments, part 1 (foundation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store PDF and image attachments in a vault so they behave like notes everywhere, upload them in the web app, sync them with the Obsidian plugin, and show them in a gallery and a lightbox.

**Architecture:** An attachment is a small sealed *record* (path, media type, size, dimensions, version, history) kept in the vault's working set like a note, plus one sealed *blob* document per distinct content, loaded on demand and keyed by an HMAC of the bytes. Notes' rules (changesets, history of 10, trash, moves with link rewriting, checkpoints, activity, export, re-keying, deletion) are reused, not duplicated. The web app reads bytes from its own origin with its session; the plugin with its API key.

**Tech Stack:** .NET 10, MongoDB driver, xUnit, SixLabors.ImageSharp (header inspection only); React 18, Vite, Vitest, openapi-fetch, fflate, pdfjs-dist, a touch-capable React lightbox (builder's pick, MIT); Obsidian plugin in TypeScript with Vitest.

**Spec:** `docs/superpowers/specs/2026-10-27-attachments-foundation-design.md` (read it first; mockups in `docs/superpowers/specs/2026-10-27-attachments-mockups/`). Code map by file and line was taken at master `2af7d6f` / plugin `efec3a4`; line numbers may have moved.

## Global Constraints

- British English in code, comments, docs, commit messages; `// SPDX-License-Identifier: AGPL-3.0-or-later` atop new source files where siblings have it. No organisation, customer or employer names anywhere.
- Commit messages imperative and free-form ("Add attachment records to the vault store"), **no** `feat:`-style prefixes.
- Accepted formats: `.pdf`, `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp` (plus `.md` notes). Everything else refused.
- Content decides: magic bytes `%PDF-`; `89 50 4E 47 0D 0A 1A 0A`; `FF D8 FF`; `GIF87a`/`GIF89a`; `RIFF`…`WEBP` (bytes 8–11). A mismatch with the extension is refused.
- Size limit: **15 MB = 15 × 1024 × 1024 = 15,728,640 bytes** per file, said as "15 MB" in every message.
- Image header limits: refuse over **50,000,000 pixels** or over **16,384 px** on either side, read from the header only (ImageSharp `Image.Identify`); **no pixel decoding on the server** in this part.
- History 10 versions, trash 30 days, changesets 2 min idle (people) / 10 min (agents) / 1 h max — reuse `NoteWriter.Next` and `Changeset`, never re-implement.
- Version token of an attachment: `VersionToken.Of(bytes)` = first 12 hex of SHA-256 of the raw bytes (no newline normalisation); stored sealed only.
- Blob key in the database: HMAC under a new derived key label `kumbu/v1/blob-id`; never a plain hash in the clear.
- Multi-file download: at most **100** files; by default needs the password (export-ticket mechanism, 30 s, one use), always logged; the user's setting "Ask for my password when downloading several files" (default **on**) can turn the password off. Single files never need it.
- pdf.js: `pdfjs-dist` from npm, served from our origin (no CDN), a version with the CVE-2024-4367 fix (≥ 4.2.67), `isEvalSupported: false`, no scripting; PDF links open in a new tab with `noopener`.
- No service worker. Every CSP change listed in `docs/security-architecture.md` §7.
- Name ImageSharp's licence in the README's third-party notes: "SixLabors.ImageSharp, Six Labors Split License 1.0, used under its Apache 2.0 terms (open-source use)".
- Build and tests: `dotnet test --solution Kumbu.slnx`; `pnpm -C web install --frozen-lockfile`, `pnpm -C web test --run`, `pnpm -C web build`; plugin `pnpm install --frozen-lockfile && pnpm test --run && pnpm build`. Before a full server run, clear Mongo's data directory if the run uses a local `mongod` (each test makes its own database).
- Branches `attachments-foundation` in both repositories; each ends in a **draft** pull request to `master`.

## Review Focus

1. **iPhone photos are HEIC.** The web file input must list concrete types (`accept=".pdf,.png,.jpg,.jpeg,.gif,.webp,application/pdf,image/png,image/jpeg,image/gif,image/webp"`, never just `image/*`) so iOS Safari converts HEIC to JPEG on pick; a `.heic` that still arrives gets the "not supported" message naming the formats. Test in Task 12.
2. **File names from phones arrive in NFD** (macOS, iOS, some Android). Attachment paths are NFC-normalised at the API boundary and in the plugin as notes' are; an upload of `Bespréchung.png` and a later sync of the NFC spelling are the same file. Tests in Tasks 9 and 17.
3. **An upload cut off mid-body** must leave neither a record nor a blob. Test in Task 9.
4. **Two paths with identical bytes share one blob**; deleting, trashing past retention or re-keying one never loses the other's bytes. Tests in Tasks 3 and 4.
5. **Phone JPEGs carry an EXIF orientation.** Width and height reported for an image are the *displayed* ones (swapped for orientations 5–8), so the lightbox info and tomorrow's `img` agree with what people see. Test in Task 1.

---

## Part A — server (repository `kumbukumbu`)

### Task 1: Formats, limits and image header inspection

**Files:**
- Create: `src/Kumbu.Core/Attachments/AttachmentFormat.cs`
- Modify: `src/Kumbu.Core/Paths/VaultPath.cs` (add `IsAttachment`), `src/Kumbu.Core/Notes/VersionToken.cs` (bytes overload)
- Create: `src/Kumbu.Server/Attachments/ImageHeader.cs`; add package `SixLabors.ImageSharp` to `src/Kumbu.Server/Kumbu.Server.csproj` (update lock files with `dotnet restore Kumbu.slnx --force-evaluate`)
- Test: `tests/Kumbu.Core.Tests/Attachments/AttachmentFormatTests.cs`, `tests/Kumbu.Server.Tests/Attachments/ImageHeaderTests.cs`

**Interfaces:**
- Produces:
  - `enum AttachmentKind { Image, Pdf }`
  - `sealed record AttachmentFormat(string Extension, string MediaType, AttachmentKind Kind)` with `static IReadOnlyList<AttachmentFormat> All`, `const long MaxBytes = 15_728_640`, `static AttachmentFormat? ForPath(VaultPath path)`, `static string? Problem(VaultPath path, ReadOnlySpan<byte> head)` (null = accepted; else the user-facing reason)
  - `VaultPath.IsAttachment` (true for the six extensions, case-insensitive)
  - `VersionToken.Of(ReadOnlySpan<byte> bytes)`
  - `static class ImageHeader { const long MaxPixels = 50_000_000; const int MaxSide = 16_384; static (int Width, int Height)? Read(Stream s, out string? problem) }` — displayed dimensions (EXIF orientation 5–8 swaps)

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("/a/x.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, null)]
[InlineData("/a/x.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, null)]
[InlineData("/a/x.png", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "x.png isn't a PNG image (its content doesn't match its name)")]
[InlineData("/a/x.docx", new byte[] { 0x50, 0x4B }, "Kumbu² keeps notes, PDFs and images (PNG, JPEG, GIF, WebP) only")]
public void Problem_checks_extension_and_magic_bytes(string path, byte[] head, string? expected) { … }

[Fact] public void Of_bytes_is_sha256_prefix_without_newline_normalisation() // "a\r\n" bytes ≠ VersionToken.Of("a\n")
```

```csharp
[Fact] public void A_header_claiming_100000_by_100000_is_refused_without_decoding()   // hand-made PNG IHDR, 60 bytes; problem names 16,384 px
[Fact] public void An_exif_orientation_6_jpeg_reports_swapped_dimensions()            // 40×20 stored → (20, 40)
[Fact] public void A_50_megapixel_limit_counts_width_times_height()                   // 10,000×5,001 refused
```

- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter "AttachmentFormat|ImageHeader"` — expect FAIL (types missing).
- [ ] **Step 3: Implement** the types above. `ImageHeader.Read` uses `Image.Identify(DecoderOptions, stream)` with `DecoderOptions { Configuration with MemoryAllocator limits }`; it never calls `Load`.
- [ ] **Step 4: Run** the filter again — expect PASS.
- [ ] **Step 5: Commit** `git commit -m "Recognise attachment formats by their content and read image headers safely"`

### Task 2: Attachment records and the store contract

**Files:**
- Create: `src/Kumbu.Core/Attachments/StoredAttachment.cs`, `src/Kumbu.Core/Attachments/IAttachmentStore.cs`
- Modify: `src/Kumbu.Core/Notes/NoteBatch.cs` (attachment entries), `src/Kumbu.Core/Notes/StoredNote.cs` (`TrashedNote` gets a sibling), `tests/Kumbu.Core.Tests/Fakes/InMemoryNoteStore.cs` (implements `IAttachmentStore`)
- Test: `tests/Kumbu.Core.Tests/Attachments/InMemoryAttachmentStoreTests.cs`

**Interfaces:**
- Produces:
  - `sealed record AttachmentState(string MediaType, long Size, int? Width, int? Height, string Version, string BlobId)`
  - `sealed record AttachmentVersion(AttachmentState State, string WrittenBy, DateTime WrittenAt, Changeset? Changeset)`
  - `sealed record StoredAttachment(VaultPath Path, AttachmentState Current, long Revision, string LastWriter, DateTime LastWriteAt, IReadOnlyList<AttachmentVersion> History, Changeset? Changeset)`
  - `sealed record TrashedAttachment(StoredAttachment Attachment, string DeletedBy, DateTime DeletedAt, Changeset? Changeset)`
  - `interface IAttachmentStore { Task<StoredAttachment?> GetAttachmentAsync(VaultPath, CancellationToken); Task<IReadOnlyList<StoredAttachment>> ListAttachmentsUnderAsync(VaultPath folder, CancellationToken); Task<Stream?> OpenBlobAsync(string vault, string blobId, CancellationToken); Task<string> PutBlobAsync(string vault, ReadOnlyMemory<byte> bytes, CancellationToken) /* returns BlobId, de-duplicated */; Task<IReadOnlyList<TrashedAttachment>> ListAttachmentTrashAsync(VaultPath? path, CancellationToken); }`
  - `NoteBatch` gains `AttachmentCreates`, `AttachmentReplaces`, `AttachmentTrashes`, `AttachmentRestores` committed atomically with notes and `Moves` by `INoteStore.TryCommitAsync` (moves are path-based and already kind-agnostic).

- [ ] **Step 1: Write the failing tests** against `InMemoryNoteStore`: `Put_blob_twice_returns_the_same_id`, `A_batch_creating_a_note_and_an_attachment_is_atomic` (a conflict on the note leaves no attachment), `Trash_lists_attachments_with_their_deletion`.
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter InMemoryAttachmentStore` — FAIL.
- [ ] **Step 3: Implement** the records, the interface and the fake. `StoredAttachment.Version => Current.Version`.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Add attachment records and the store contract"`

### Task 3: Encrypted storage of records and blobs

**Files:**
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs` (working set `Attachments`, `AttachmentTrash`; key `kumbu/v1/blob-id`; seal/open with associated data `attachment:{vault}:{docId}:{rev}`, `attachtrash:{vault}:{docId}`, `blob:{vault}:{b64 blobId}`; `ApproxBytes` counts records only), `src/Kumbu.Server/Vaults/VaultCatalog.cs` (collections `attachments`, `attachment_trash`, `blobs`; unique `{vault,pid}` / `{vault,bid}` indexes; `attachment_trash.deletedAt` **without** TTL — expiry is swept so blob references can be released; add all three to `ContentCollections`; `LoadAsync` loads `attachments` and `attachment_trash`, never `blobs`), `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (implements `IAttachmentStore`; `TryCommitAsync` handles the attachment entries in the same transaction), `src/Kumbu.Core/Crypto/VaultCrypto.cs` (`Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)` overload), `src/Kumbu.Server/Accounts/Deletion.cs` (`DeletionSummary` gains `Attachments`, counted from `attachments`; receipt view in `Api/AccountApi.cs`)
- Create: `src/Kumbu.Server/Vaults/BlobSweep.cs` — releases blobs no record, trash entry (within 30 days) or checkpoint references; expires attachment trash past 30 days; runs from `VaultCatalog.Sweep()` for unlocked vaults, skipping blobs younger than 10 minutes (an upload in flight)
- Test: `tests/Kumbu.Server.Tests/Vaults/AttachmentStoreTests.cs`, extend `tests/Kumbu.Server.Tests/Accounts/DeletionTests.cs`

**Interfaces:**
- Consumes: Task 2's records and `IAttachmentStore`.
- Produces: `EncryptedNoteStore : IAttachmentStore`; `BlobSweep.RunAsync(UnlockedVault, DateTime now, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**
  - `An_attachment_round_trips_in_a_second_process` (record fields and blob bytes equal after reload)
  - `Identical_bytes_at_two_paths_are_one_blob_and_survive_removing_one` (Review Focus 4: trash one, sweep past 30 days, the other still opens)
  - `A_blob_id_in_the_database_is_not_a_plain_sha256` (`bid` ≠ SHA-256 of the bytes)
  - `Blobs_are_not_loaded_with_the_vault` (`ApproxBytes` grows by less than 1 KB for a 5 MB attachment)
  - `A_sweep_releases_a_blob_nothing_references_and_keeps_a_fresh_one`
  - `Deleting_a_vault_removes_attachments_and_blobs_and_the_receipt_counts_them`
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter "AttachmentStore|Deletion"` — FAIL.
- [ ] **Step 3: Implement.** Follow the checkpoint-text pattern (`EncryptedNoteStore.TryAdd/TryRemoveCheckpointAsync`) for content addressing.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Store attachments as sealed records and content-keyed blobs"`

### Task 4: Re-keying covers blobs

**Files:**
- Modify: `src/Kumbu.Server/Vaults/VaultCatalog.Rekey.cs` (`RekeyOrder` = `["checkpoint_texts", "blobs", "notes", "attachments", "trash", "attachment_trash", "moves", "embeddings", "checkpoints"]`; blob ids remapped like `textIds` and applied to records, trash and checkpoints; read chunks bounded by bytes so 100 × 15 MB is never read at once), `src/Kumbu.Server/Vaults/UnlockedVault.cs` (`Reseal` cases)
- Test: `tests/Kumbu.Server.Tests/Vaults/RekeyTests.cs`

- [ ] **Step 1: Write the failing tests**
  - `Rekeying_a_vault_with_a_blob_larger_than_a_batch_keeps_every_attachment` (`BatchBytes = 30_000`, a 200 KB blob)
  - `Rekeying_remaps_shared_blob_ids_for_every_referrer` (two paths, one blob, one in trash, one in a checkpoint)
- [ ] **Step 2: Run** `--filter Rekey` — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Re-key attachment blobs and their references"`

### Task 5: Writing, removing and restoring attachments

**Files:**
- Create: `src/Kumbu.Core/Attachments/AttachmentWriter.cs`
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs` (extract the history/changeset rule of `Next` into a generic helper both writers use; `WriteAsync`/`EditAsync` and `Replacer` refuse attachment paths with "x.png is an attachment; text tools can't change it")
- Test: `tests/Kumbu.Core.Tests/Attachments/AttachmentWriterTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2.
- Produces: `AttachmentWriter(INoteStore notes, IAttachmentStore store, TimeProvider time, ICaller caller)` with
  - `Task<WriteResult> WriteAsync(string path, ReadOnlyMemory<byte> bytes, (int W, int H)? dimensions, string? version, CancellationToken)` — `version` null = must be new; stale = conflict carrying the current `AttachmentState` (no bytes)
  - `Task<ShellResult> RemoveAsync(string path, CancellationToken, string? version = null)`
  - `Task<ShellResult> RestoreAsync(string path, string? version, CancellationToken)` (from history or trash)

- [ ] **Step 1: Write the failing tests**: `Writes_by_one_person_within_two_minutes_are_one_version`, `An_agent_gets_ten_minutes`, `The_eleventh_version_drops_the_oldest`, `A_stale_version_is_a_conflict_with_the_current_state`, `A_note_and_an_attachment_cannot_share_a_path_without_extension` (`/a/x.png` beside `/a/x.md` is fine; writing `/a/x.png` over a note path is impossible by construction — assert both exist), `Text_tools_refuse_attachment_paths`, `Restore_from_trash_brings_back_the_bytes`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Write, remove and restore attachments with notes' history rules"`

### Task 6: Links to attachments, moves and link rewriting

**Files:**
- Modify: `src/Kumbu.Core/Links/MarkdownLink.cs` (parse `![alt](path)` as an embed; today `ParseLine` skips a `[` after `!`), `src/Kumbu.Core/Links/LinkResolver.cs` (index attachment paths with their extension; `LinkKind.Attachment` gets its target), `src/Kumbu.Core/Notes/NoteMover.cs` (`MoveAsync`/`MoveFolderAsync` move attachments; `NewTarget` keeps an attachment's extension instead of adding `.md`; `Rewrites` covers `![[x.png]]`, `![](/p/x.png)`, `[t](/p/x.pdf)`, relative `![](img/x.png)`)
- Test: `tests/Kumbu.Core.Tests/Links/AttachmentLinkTests.cs`, `tests/Kumbu.Core.Tests/Notes/NoteMoverTests.cs`

- [ ] **Step 1: Write the failing tests**: `Markdown_image_links_are_found`, `A_wiki_embed_of_a_png_resolves_to_the_attachment`, `Moving_an_attachment_rewrites_wiki_and_markdown_links`, `Moving_a_folder_moves_its_images_and_keeps_relative_links_valid`, `Backlinks_of_an_attachment_list_the_embedding_notes`, `An_external_image_url_is_never_a_vault_link`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Treat attachments as link targets and move them with their links"`

### Task 7: Checkpoints, history views and activity

**Files:**
- Modify: `src/Kumbu.Core/Notes/Checkpoint.cs`, `Checkpoints.cs` (`CheckpointAttachment(Path, Version, BlobId)`; make, browse, plan and restore include attachments), `src/Kumbu.Core/Notes/NoteHistory.cs` (an `AttachmentVersionEntry(Version, WrittenAt, Writer, State, Current, Changeset)`), `src/Kumbu.Core/Notes/Activity.cs` (`ActivityFile` gains `Kind` "note"|"image"|"pdf"; rows fold attachment states like notes), `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (checkpoint references to blobs)
- Test: `tests/Kumbu.Core.Tests/Notes/CheckpointAttachmentTests.cs`, extend `ActivityTests`

- [ ] **Step 1: Write the failing tests**: `A_checkpoint_keeps_an_attachment_version_after_it_changes`, `Restoring_a_checkpoint_restores_attachments`, `Activity_lists_an_upload_a_move_and_a_removal_of_an_image`, `History_lists_attachment_versions_newest_first`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Include attachments in checkpoints, history and activity"`

### Task 8: The shell and its help

**Files:**
- Modify: `src/Kumbu.Core/Sh/Commands/Ls.cs` (attachments listed with size and, for images, dimensions, marked e.g. `layout_editor_bad1.png  image 1440×3120 412 KB`), `Cat.cs` (one line: `layout_editor_bad1.png: image (PNG), 1440×3120, 412 KB` / `report.pdf: PDF, 2.1 MB`), `Mv.cs`, `Rm.cs`, `History.cs`, `Restore.cs`, `Trash.cs`, `LinksCommand.cs`, `Backlinks.cs` (accept attachment paths), `Rg.cs` and `Semsearch.cs` (skip attachments), `src/Kumbu.Core/Sh/Help.cs` (`Help.Topics["attachments"]` with formats, the 15 MB limit and what works; `["img"]` and `["pdf"]` as stubs "Coming in 0.2.5: …"), `src/Kumbu.Server/Mcp/Manual.cs` (one line in `ToolDescription`: attachments exist, `help attachments`)
- Test: `tests/Kumbu.Core.Tests/Sh/AttachmentCommandTests.cs`, extend `HelpTests`

- [ ] **Step 1: Write the failing tests**: `Ls_marks_attachments_with_kind_and_size`, `Cat_on_an_image_prints_one_line_not_bytes`, `Rg_skips_attachments_and_help_says_so`, `Mv_and_rm_work_on_attachments`, `Help_lists_the_attachments_img_and_pdf_topics`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Let the shell list, move, remove and explain attachments"`

### Task 9: REST API for attachments

**Files:**
- Create: `src/Kumbu.Server/Api/FileApi.cs` — `GET /api/files/{vault}/{**path}` (`?version=`, `?download=1` for `Content-Disposition: attachment`), `PUT /api/files/{vault}/{**path}` (raw body, `Content-Type` ignored for the stored type; `?version=` the expected version, absent = must be new; `[RequestSizeLimit(15_728_640 + 64 KB)]`; read fully into a pooled buffer before any write so a cut-off body writes nothing), `DELETE /api/files/{vault}/{**path}?version=`; conflict view `FileConflictView(Error, Current: FileStateView)` with `FileStateView(Path, MediaType, Size, Width?, Height?, Version, WrittenAt, WrittenBy)`
- Modify: `src/Kumbu.Server/Api/NoteApi.cs` (`ApiPaths.Parse` gains an attachment mode; `TreeNote` gains `Kind` and `MediaType`; tree lists attachments; `/api/move`, `/api/links`, `/api/backlinks`, `/api/history` accept attachment paths), `src/Kumbu.Server/Api/SyncApi.cs` (`versions?attachments=1` adds attachment entries `VersionEntry(Path, Version, Kind)` and includes them in `state`/`deleted`; without it the answer is byte-for-byte today's), `src/Kumbu.Server/Api/HistoryApi.cs` (restore, version, trash, activity, checkpoint views carry `Kind`/`MediaType`/`Size`; `GET /api/version/...` for an attachment returns its state, bytes via `/api/files?version=`), `src/Kumbu.Server/Api/Security.cs` (CSP `img-src 'self' data: blob:`; add what pdf.js needs in Task 14, not here), `docs/security-architecture.md` (new endpoints, untrusted-bytes handling, blob ids in §5.6, attachment trash and blobs in §5.4)
- Test: `tests/Kumbu.Server.Tests/Api/FileApiTests.cs`, extend `SyncApiTests`, `NoteApiTests` (OpenAPI test lists `/api/files`)

- [ ] **Step 1: Write the failing tests**
  - `A_png_round_trips_with_its_media_type_nosniff_and_no_store`
  - `A_png_named_pdf_is_a_400_naming_the_mismatch`; `A_16_MB_file_is_a_413_saying_15_MB`; `A_docx_is_a_400_naming_the_formats`
  - `A_stale_version_is_a_409_with_the_current_state_and_no_bytes`
  - `A_body_cut_off_midway_writes_nothing` (Review Focus 3: abort the request stream; no record, no blob after a sweep)
  - `An_nfd_name_and_its_nfc_spelling_are_one_file` (Review Focus 2)
  - `Versions_without_attachments_parameter_is_unchanged` and `Versions_with_attachments_lists_them_with_kind`
  - `The_tree_lists_attachments_with_kind_and_media_type`
  - `Download_sets_a_safe_content_disposition` (a name with quotes and non-ASCII is RFC 6266-encoded)
- [ ] **Step 2: Run** `--filter "FileApi|SyncApi|NoteApi"` — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Serve and accept attachments over REST"`

### Task 10: Multi-file download, export, and the password preference

**Files:**
- Modify: `src/Kumbu.Server/Accounts/UserDocuments.cs` (`UserDocument.AskPasswordForDownloads` bool, absent = true), `src/Kumbu.Server/Api/AccountApi.cs` (`GET/PUT /api/account/preferences` → `PreferencesView(bool AskPasswordForDownloads)`, session only), `src/Kumbu.Server/Api/ExportApi.cs` (`GET /api/files/{vault}/zip?path=…&path=…&ticket=` streaming a ZIP of at most 100 attachments; requires a ticket from `POST /api/export/ticket` unless the user turned the preference off; logs like an export with ids and counts; vault and checkpoint exports now include attachments at their paths, streamed blob by blob), `docs/security-architecture.md` (§6 export: multi-file download; the stolen-laptop row: with the preference off, a session can download many attachments without the password), `README.md` ("Export and import": attachments included; the preference)
- Test: extend `tests/Kumbu.Server.Tests/Api/ExportApiTests.cs`, `AccountApiTests.cs`

- [ ] **Step 1: Write the failing tests**: `A_zip_of_several_files_needs_a_ticket_by_default`, `With_the_preference_off_no_ticket_is_needed_and_the_download_is_still_logged`, `A_zip_of_101_files_is_a_400`, `A_vault_export_contains_its_attachments`, `Preferences_default_to_asking`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Download several attachments as a ZIP and export attachments"`

## Part B — web app (`web/`)

### Task 11: API types, the tree, note chips and link completion

**Files:**
- Modify: run `pnpm -C web gen:api` against a server from this branch → `web/src/api/schema.d.ts`; `web/src/api/notes.ts` (`useAttachmentUrl(vault, path, version?) → string | undefined` — fetches with the session into an object URL, revoked on unmount), `web/src/api/paths.ts` (`fileUrl`, `isAttachment(path)`, `titleOf` keeps attachment extensions), `web/src/shell/TreeDrawer.tsx` (icons by kind; a tap opens the lightbox route from Task 13), `web/src/note/MarkdownView.tsx` (vault attachment links/embeds render as a chip `🖼 name.png` / `📄 name.pdf` opening the lightbox; external images keep today's placeholder), `web/src/note/wikiLinks.ts`, `web/src/note/linkCompletion.ts` (offer attachments, inserting `![[name.png]]` for images and `[[name.pdf]]` for PDFs)
- Test: `web/src/note/MarkdownView.test.tsx`, `web/src/note/linkCompletion.test.ts`, `web/src/shell/TreeDrawer*.test.tsx`

- [ ] **Step 1: Write the failing tests**: `renders a vault image embed as a chip that opens the lightbox`, `keeps an external image a placeholder and fetches nothing`, `shows a missing attachment like a missing note`, `completion offers images with an embed`.
- [ ] **Step 2: Run** `pnpm -C web test --run` — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Show attachments in the tree and as chips in notes"`

### Task 12: Upload, shrink and import

**Files:**
- Modify: `web/src/upload/planUpload.ts` (accept the six extensions; `MAX_ATTACHMENT_BYTES = 15_728_640`; classify: note, attachment, too large image → shrink offer, too large PDF / unsupported → refusal text), `web/src/upload/collectFiles.ts`, `web/src/upload/UploadSheet.tsx` (`accept` as in Review Focus 1; paste of an image on desktop names it `pasted YYYY-MM-DD HH-mm.png`, editable; attachments sent one `PUT /api/files` each with the sheet's conflict handling; one summary for notes and attachments), `web/src/data/readImport.ts`, `runImport.ts` (keep the six formats from a ZIP and upload them)
- Create: `web/src/upload/shrinkImage.ts` — `shrinkImage(file: File, maxBytes: number): Promise<File>`; canvas → `image/webp` if supported else `image/jpeg`, quality 0.92 → 0.5 in steps of 0.07, then dimensions × 0.85 per round until it fits; result keeps the name with the new extension
- Test: `web/src/upload/planUpload.test.ts`, `web/src/upload/shrinkImage.test.ts` (stubbed canvas), `web/src/upload/UploadSheet.test.tsx`

- [ ] **Step 1: Write the failing tests**: `accepts the six formats and refuses docx with the formats named`, `offers to shrink an image over 15 MB and says when the extension changes`, `warns that a GIF loses its animation`, `refuses a PDF over 15 MB naming 15 MB`, `the file input lists concrete types, not image/*` (Review Focus 1), `a heic file is refused with the formats named`, `import keeps attachments from a ZIP`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Upload images and PDFs, shrinking images that are too large"`

### Task 13: Lightbox and PDF viewer

**Files:**
- Create: `web/src/attachments/Lightbox.tsx` (route `/view/:vault/*`, opened from gallery, tree, chips, history; order passed as a list of paths), `web/src/attachments/PdfPages.tsx` (pdf.js, pages stacked, page counter), `web/src/attachments/pdf.ts` (lazy `pdfjs-dist` import with `isEvalSupported: false`, worker bundled from our origin)
- Modify: `web/package.json` (+ `pdfjs-dist`, + lightbox library), `web/vite.config.ts` (own chunk and worker), `web/src/router.tsx`, `src/Kumbu.Server/Api/Security.cs` (CSP additions for the worker, e.g. `worker-src 'self' blob:`, and `'wasm-unsafe-eval'` only if the chosen pdfjs-dist needs it — document why), `docs/security-architecture.md` §7
- Test: `web/src/attachments/Lightbox.test.tsx`, `web/src/attachments/pdf.test.ts`

- [ ] **Step 1: Write the failing tests**: `arrow keys move through the given order and Escape closes`, `the info panel shows folder, dimensions, size, date and writer`, `copy link puts ![[name.png]] on the clipboard`, `pdf.js is loaded with isEvalSupported false`, `a pdf link opens in a new tab with noopener`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement** to the mockup `lightbox.html` (black, top: close/name/info/menu; bottom: download/move/copy link/delete; pinch zoom, pan, double tap 2×, swipe to page and down to close; percentage while zoomed).
- [ ] **Step 4: Run** — PASS; check `pnpm -C web build` emits the worker as a same-origin asset.
- [ ] **Step 5: Commit** `git commit -m "Add the lightbox with zoom and pan, and the PDF viewer"`

### Task 14: Gallery and the download preference

**Files:**
- Create: `web/src/attachments/GalleryPage.tsx` (route `/gallery/:vault`; segmented switch Date | Folder | Name stored as `kumbu.galleryOrder` in local storage with try/catch like `theme/apply.ts`; lazy tiles; PDF tile = first page via `pdf.ts`), `web/src/attachments/useSelection.ts`, `web/src/attachments/downloadFiles.ts` (one file: direct; several: ask the password unless the preference is off, then `/api/files/{vault}/zip` with the ticket)
- Modify: `web/src/shell/TreeDrawer.tsx` (vault menu entry "Gallery" beside the board's), `web/src/routes/Settings.tsx` (checkbox "Ask for my password when downloading several files", default on, via `/api/account/preferences`), `web/src/router.tsx`
- Test: `web/src/attachments/GalleryPage.test.tsx`, `web/src/routes/Settings.test.tsx`

- [ ] **Step 1: Write the failing tests**: `orders by date newest first grouped by day`, `orders by folder A–Z then name`, `orders by name across folders`, `remembers the order`, `long press selects and the bar shows count and size`, `downloading several asks for the password by default`, `with the preference off it downloads without asking`, `delete moves the selection to the trash after confirming`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement** to the mockup `gallery.html`. **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the gallery with orderings, selection and downloads"`

### Task 15: History, trash, activity and checkpoints for attachments

**Files:**
- Modify: `web/src/note/HistorySheet.tsx` (for an attachment: versions as thumbnails with date and writer; tap opens the lightbox on that version; Restore), `web/src/shell/CompareVersions.tsx` (two image versions side by side; PDFs: first pages), `web/src/shell/TrashSheet.tsx` (icon, name, size; restore), `web/src/shell/ActivitySheet.tsx` (rows for attachments; open goes to the lightbox), `web/src/shell/CheckpointsSheet.tsx`, `web/src/routes/Checkpoint.tsx` (attachments in the tree and lightbox)
- Test: `web/src/shell/HistorySheets.test.tsx`, `web/src/routes/Checkpoint.test.tsx`

- [ ] **Step 1: Write the failing tests**: `an image's history shows thumbnails and restores a version`, `comparing two image versions shows both images`, `trash lists an image with size and restores it`, `activity opens an image in the lightbox`, `a checkpoint shows its attachments`.
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Show attachments in history, trash, activity and checkpoints"`

## Part C — Obsidian plugin (repository `kumbu-obsidian`)

### Task 16: Binary ports, API and server detection

**Files:**
- Modify: `src/engine/ports.ts` (`RemoteApi.readFile(path) → { bytes: ArrayBuffer, version } | null`, `putFile(path, bytes, base)`, `removeFile`; `LocalFiles.readBinary`, `writeBinaryIf(path, expectedVersion, bytes)`; `NotSyncedReason` keeps its values), `src/api/client.ts` (`HttpRequest.body?: string | ArrayBuffer`, `HttpResponse.bytes?: ArrayBuffer`), `src/obsidian/http.ts`, `src/api/types.ts` (`VersionEntry.kind?`), `src/obsidian/vaultFiles.ts` (`readBinary`/`createBinary`/`modifyBinary`), `src/engine/version.ts` (`versionOfBytes(bytes)` = SHA-256 of the raw bytes, 12 hex, no normalisation)
- Detection: the plugin asks `versions?attachments=1`; a server that ignores the parameter returns no `kind` on any entry and its `/api/files` answers 404 → `attachmentsSupported = false` and today's behaviour stays.
- Test: `test/api.test.ts`, `test/fakes/fakeServer.ts` (binary files, a switch for an old server)

- [ ] **Step 1: Write the failing tests**: `versionOfBytes does not normalise CRLF`, `an old server is detected and attachments stay unsynced`, `the client sends and receives array buffers`.
- [ ] **Step 2: Run** `pnpm test --run` — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** `git commit -m "Add binary file transfer and detect servers with attachments"`

### Task 17: Syncing attachments, limits and notices

**Files:**
- Modify: `src/engine/paths.ts` (`eligibility` → "sync" for `.md` and the six attachment extensions when supported; `conflictCopyPath` keeps any extension), `src/engine/engine.ts` (resolve/act/upload for attachments via the binary ports; `MAX_ATTACHMENT_BYTES = 15_728_640`; too-large attachments get reason "too-large" with their own text), `src/obsidian/texts.ts` (`reasonText`: unsupported → "Kumbu² keeps notes, PDFs and images only."; too-large note → "Larger than 1 MiB."; too-large image → "Larger than 15 MB. Upload it in the Kumbu² web app, which can make it smaller."; too-large PDF → "Larger than 15 MB, the limit for PDFs."), `src/main.ts` (`PluginData.announced: string[]`; a `Notice` once per too-large file naming it and the text above; unsupported formats get no notice; status bar and settings list unchanged)
- Test: `test/engine.attachments.test.ts`, `test/texts.test.ts`

- [ ] **Step 1: Write the failing tests**: `uploads a new png and downloads one the server has`, `a change on both sides gives a conflict copy keeping .png`, `a 16 MB pdf is not synced and announced once across two syncs`, `a docx stays on the list without a notice`, `an nfd file name syncs as its nfc spelling` (Review Focus 2).
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement.** **Step 4: Run** — PASS; `pnpm build`.
- [ ] **Step 5: Commit** `git commit -m "Sync PDFs and images, warning once about files too large"`

## Part D — finishing

### Task 18: Docs, hands-on test and draft pull requests

**Files:**
- Modify: `docs/concept.md` §12 (attachments built: one sentence each for storage, sync and that tickets were needed only for the multi-file download), `README.md` ("The web app": attachments, gallery, lightbox; third-party licence line for ImageSharp; "Syncing with Obsidian": attachments and the 15 MB notice), `docs/security-architecture.md` (check every item of the spec's Security section is there)
- Create: `docs/notes-attachments-hands-on.md`

- [ ] **Step 1:** Full checks: `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run`, `pnpm -C web build`, plugin `pnpm test --run && pnpm build`. All green; record counts in the hands-on note.
- [ ] **Step 2:** Hands-on with a running server (compose or `dotnet run`) and a browser at phone width and desktop: upload a screenshot, open it from the gallery, the tree and a note chip; zoom and pan; page a PDF; download two files (password asked; then with the preference off); move an image and check the note's link; restore an older version; trash and restore; make and browse a checkpoint; export a vault and look into the ZIP. Plugin against the same server with a test vault: sync a PDF and a PNG both ways, a conflict copy, a 16 MB file's notice. Write what happened, with anything surprising, into `docs/notes-attachments-hands-on.md`.
- [ ] **Step 3:** Commit `git commit -m "Write up the hands-on test of attachments"`; push both branches.
- [ ] **Step 4:** Open **draft** pull requests to `master`: `gh pr create --draft --title "Attachments, part 1: storage, upload, sync, gallery, lightbox"` in `kumbukumbu` and `gh pr create --draft --title "Sync PDFs and images"` in `kumbu-obsidian`, each body summarising the tasks, the hands-on result, open points, and ending with the attribution lines of the session.
