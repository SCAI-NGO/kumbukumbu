# Files: every file type, large files

**Status:** decisions approved by Chris on 2026-11-18 ("das passt sehr gut alles"). Version 0.6.0, track B of the 0.6.0 tracks. Brief: `/SWE/Kumbu/Internal Roadmap/0.6.0 tracks/Track B Files.md`. Reading CSV, HTML and XML (part 3 of the brief) gets a spec of its own once parts 1 and 2 are merged.

## Goal

Every file may travel with the vault; Kumbu² does nothing of its own with it. Upload, download, move, link, history, trash, checkpoints and export work for every file, through every way in (web app, WebDAV, MCP's upload URL); preview, editing in the browser, sharing single files and a file-centred UI do not come. Files larger than 15 MB are kept too, up to a bound the admin sets.

Why (brief, power user feedback of 2026-11-16): about 220 files in a professional user's vault are refused today (Excel, Word, CSV, HTML, Python, anything over 15 MB), over WebDAV too, so the vault can't travel whole. Attachments without a preview are enough for him. The line against Nextcloud, approved by Chris: every file may travel with the vault, Kumbu² does nothing of its own with it.

## Decisions

### Part 1: every file type

1. **A new kind, `file`.** Every path that doesn't end in `.md` and isn't a format Kumbu² knows (PDF, PNG, JPEG, GIF, WebP, SVG; a skill folder's text files and the folder marker as today) is a file: `AttachmentKind.File`, kind `"file"` wherever clients are told kinds (sync, tree, recent, history, graph). Known formats keep their check: a `.png` must still hold PNG bytes. `UnsupportedMessage` and its refusals go.
   Why: one rule ("not a note, so a file"), no list of allowed extensions to keep up with.
2. **What a path is, is decided by what is stored there, not by its name.** Today a path that is neither a note nor a known format can only be a folder (`/Work/v1.2`); from now on it can be a file too. `VaultPath.IsAttachment` becomes "not the root and not a note": it says a file *may* be kept there, and every reader looks it up. A file and a folder never share a name: writing a file where a folder is, or making a folder (or anything below it) where a file is, is refused with a sentence that names the other. *The heart of the tests*: `ls`, `cat`, `mv`, `rm`, `restore`, links, WebDAV `PROPFIND` and the tree for folders with dots in their names, files without an extension, and both meeting.
3. **Text is recognised by its content, not its extension.** A file that is valid UTF-8 without a NUL byte and at most 15 MB is kept as `text/plain; charset=utf-8`, as a skill folder's text files are today; `cat` prints it and `rg` searches it. Every other file is kept as `application/octet-stream`. The media type is decided at each write and kept in the record; a rename doesn't change it.
   Why: an agent working on a CSV over MCP is Kumbu²'s strength, and the content is what decides whether it reads as text. Price, accepted: a CSV exported by Excel in Windows-1252 stays opaque; a conversion is a later, separate decision. `semsearch` doesn't embed files (out of scope for 0.6.0).
4. **Files are only ever served as downloads.** Every endpoint that serves a file's bytes (`/api/files`, checkpoint files, WebDAV `GET`, the reader, AI links) sends kind `file` with `Content-Disposition: attachment`, its stored media type (`text/plain; charset=utf-8` or `application/octet-stream`, never anything a browser renders as a document), `nosniff` and the sandbox policy (`Security.FileContentPolicy`), whatever the request asks for. Hard constraint: an uploaded `.html` shown inline would be stored XSS, on publish pages too. A test asserts the three headers for an `.html` file on each endpoint.
5. **The web app shows a file as a chip, with no preview.** An icon, the name, the size and "Download"; the lightbox shows the same instead of drawing it as an image (today an unknown kind falls back to "image" or "note" in several places, all of which learn `file`). Text files get no viewer either.
   Why: the line against Nextcloud; agents read text files with `cat`.
6. **Publishing leaves files out in 0.6.0.** A link from a published note to a file shows as its name, without a link, as a link to a note left out does. Why: no sharing of single files (the Nextcloud line); a file inside a publication would be exactly that.
7. **Renaming:** a file may take any name that isn't a note's (`.md`) or a known format's whose bytes it doesn't hold (`report.docx` can't become `report.png`); an image or PDF may become a file only by its bytes, which they never are, so they keep their extensions as today.
8. **WebDAV:** what the parking rules park (`.DS_Store`, `*.tmp`, `~$*` and the others, `DavRules.Patterns`) stays parked by name; everything else that used to be a 415 is now kept.

### Part 2: large files

9. **Up to 15 MB nothing changes; larger files are kept in chunks.** A blob of at most 15 MB stays one sealed document in `blobs`, exactly as today: no migration, and every consumer that reads a whole file keeps working. A larger file is a *head* in `blobs` plus chunks of 4 MiB in a new collection `blob_chunks` (vault id, the head's document id, the chunk's number, the sealed bytes, when written). The chunks are written first and the head last, so no partial file is ever visible: a blob exists only once its head does.
   Why: MongoDB's 16 MB document limit is the reason for today's limit; GridFS would still need encryption per chunk and brings nothing else we need.
10. **Each large file has a random key of its own.** Its chunks are sealed under that data key, each with associated data naming the vault, the head's document id, the chunk's number and whether it is the last (`chunk:{vaultId}:{headId}:{n}:{last|more}`), so chunks can't be swapped, reordered, moved to another file or cut off. The head's sealed payload (under the vault's content key, context `blob:{vaultId}:{bid}` as today) holds the data key, the size, the number of chunks and the SHA-256 of the whole file. The blob id of a large file is `HMAC(HKDF(vault key, "kumbu/v1/large-blob-id"), SHA-256(bytes))`, computed while streaming (*changed while building:* a prefix under the ordinary blob-id key, as first written here, would let a small file whose bytes are that prefix and a hash collide with a large file's id, and deduplication would then hand one file's record the other's bytes; a key of its own rules that out); the same bytes still get the same id within a vault, so identical uploads are stored once (a second upload's chunks are deleted when its head finds the id taken).
    Why: re-keying a vault then reseals only the small head (new blob id from the hash, data key re-wrapped), never a gigabyte of chunks. **Honest limit, for `security-architecture.md`:** the chunks aren't encrypted again by a re-keying; whoever holds an old database copy and the old key reads the file as it was, which they could read in that copy anyway; nothing written after the re-keying opens with the old key.
11. **Bound per file: 1 GiB by default**, the admin's setting `Kumbu:Vaults:MaxFileBytes` (at least 15 MiB). Every way in streams the body: one `PUT`, no resumable upload. The quota is checked before each chunk is written, and before the first when the request declares its length; a refusal half way deletes the chunks already written. WebDAV's request limit of 64 MiB stays for notes and parked files and lifts to the bound for a file's `PUT`.
    Why streaming and no resumable upload: what we really need is that a large file arrives; resuming is a protocol of its own (tus) for a need we haven't seen. Price: behind a reverse proxy with a read timeout (Traefik's `readTimeout=10m` in `compose.traefik.yaml`), 1 GiB over a slow link can time out; the README says so.
12. **Large PDFs and images keep their kind** and are shown by the browser, streamed (the viewer, the lightbox, the original), but what reads a whole file in the server (PDF text and pages, `img`, crop and rotate, thumbnails, `pdfimages`, publishing) works up to 15 MB; beyond it, an honest sentence says Kumbu² keeps the file but doesn't read it ("larger than 15 MB: kept, but its text isn't read"). Why: the sidecar and the pixel code hold whole files in memory, sized for 15 MB.
13. **Reads stream.** Downloads, WebDAV `GET`, the ZIP export and checkpoints read a large file chunk by chunk (one chunk opened at a time), holding a `FileReads` slot per chunk, not per file; `cp`, checkpoints and moves keep naming the blob id and read no bytes, as today.
14. **The sweep and deletions know chunks.** The blob sweep releases a head together with its chunks, and deletes chunks older than `BlobSweep.Grace` whose head doesn't exist (an upload that stopped half way); deleting a vault or an account deletes its chunks; chunks count towards the storage figure (`Admin.BytesByVaultAsync`) and `df`.
15. **The web app uploads any file, large ones streamed.** The file picker accepts everything, a drop takes everything; a file over 15 MB is sent as the `File` itself (not read into memory first) with a progress bar; one over the bound is refused before sending, with the bound named. Only `web/src/upload/` changes for this, not the editor or the layout (track A's area), agreed with track A's session.

## Out

- Previews, editing in the browser, sharing single files, a file-centred UI (the Nextcloud line).
- Text extraction from Office files (a later, separate decision, like PDFs).
- Resumable uploads; converting non-UTF-8 text.
- Reading CSV, HTML and XML with `sh` commands: its own spec, after parts 1 and 2.

## Still to do outside this repository

- **The Obsidian plugin** (frozen, own repository): its spec says it doesn't sync attachments ("They stay local", `docs/obsidian-plugin-design-spec.md:22`); still to check there that the sync list's new kind `file` and large files don't trip it, as the `.svg` check due before the release. Chris takes this up in a session of its own on 2026-11-19.
- **rclone bisync over WebDAV** with a large file: at least one test by hand (the fixtures are in the backlog).

## Touch points

From the map of 2026-11-18 (paths at that `master`, `3549088`):

- Kind and format: `src/Kumbu.Core/Attachments/AttachmentFormat.cs` (`ForName`, `ForPath`, `KindOf`, `Problem`, `RenameProblem`, `MaxBytes`), `src/Kumbu.Core/Paths/VaultPath.cs:35`, `src/Kumbu.Server/Api/NoteApi.cs:1089–1126` (`PathKind`), `AttachmentWriter.cs`, `AttachmentText.cs`, `LinkResolver.cs:123,137`, `NoteMover.cs`, `TreeWriter.cs`, `Cat.cs`, `Rm.cs`, `Restore.cs`, `Help.cs`, `Manual.cs`, `DavWrites.cs:58,450`, `DavTree.cs`, `UploadTool.cs`, `AgentUploadApi.cs`, `SyncApi.cs`, `RecentApi.cs`, `HistoryApi.cs`, `GraphApi.cs`, publishing (`Bundles.cs`, `Publications.cs`), skills (`SkillCopier.cs`, `SkillUpdateTaker.cs`, `MarketplaceIndex.cs`).
- Serving: `FileApi.cs` (`Disposition`), `DavContent.cs`, `ReaderApi.cs`, `AiLinkApi.cs`, `Security.cs`.
- Storage: `EncryptedNoteStore.cs` (`PutBlobAsync`, `OpenBlobAsync`), `VaultCatalog.cs` (`ReadBlobAsync`, indexes, deletion), `Documents.cs`, `UnlockedVault.cs` (`BlobId`, `SealBlob`, `OpenBlob`, rekey of `blobs`), `VaultCatalog.Rekey.cs`, `BlobSweep.cs`, `Admin.cs`, `StorageQuota.cs`, `IAttachmentStore.cs`, `StoreAttachments.cs`, `CheckpointView.cs`, `ExportApi.cs`, `DavBody.cs`, `DavEndpoints.cs:28`, `KumbuOptions.cs`.
- Web: `web/src/api/paths.ts`, `api/notes.ts`, `upload/` (`planUpload.ts`, `UploadSheet.tsx`, `putFile.ts`), `attachments/Lightbox.tsx`, `Thumbnail.tsx`, `note/MarkdownView.tsx` (`AttachmentChip`), `components/fileKindIcon.ts`, and the places that fall back to "image" or "note" (`MoveDialog.tsx`, `ActivitySheet.tsx`, `TreeDrawer.tsx`, …).
- Docs: `README.md` (formats, the bound, the proxy timeout), `docs/security-architecture.md` (files only as downloads; chunks, the data key and re-keying).

## Pull requests

1. **Every file type** (decisions 1–8), up to 15 MB.
2. **Large files** (decisions 9–15).

Test first for each; a security review of the track before the freeze (Saturday 2026-11-21, evening), before the whole-release audit.
