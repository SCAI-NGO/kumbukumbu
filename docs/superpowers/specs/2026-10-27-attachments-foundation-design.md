# Attachments, part 1: the foundation (design)

Date: 2026-10-27. Brainstormed with Chris; to be built overnight in a web session. Part of the omnibus release 0.2.5 (no release before it). Background: `docs/concept.md` §12 (decisions of Oct 27, 2026), research in `docs/attachments-client-research.md`.

## Why

Kumbu² has been text only on purpose (concept §12). Two people show what attachments are for:

- **The engineer's screenshot, on the move.** Testing something on the phone, he sees a layout that's hard to use, saves `layout_editor_bad1.png` into the vault, opens Claude on the phone and says "look at that PNG, I saw it just now when I wanted to do X". The screenshot is a reason to talk, not an archive item; success is the fewest steps between "seen" and "Claude looks at it". He works with screenshots a lot.
- **The scientist's PDFs, at the desk.** A court or forensic report, a research paper from Zotero. She archives; her PC is her home base and Obsidian her interface, so the PDFs must reach Kumbu² through the Obsidian sync without an extra upload step. She has an iPhone at work and an Android phone privately.

This part builds what both need underneath: storage, upload, sync, and a way for people to look at what's there. Tomorrow two packages build on it in parallel: **PDF** (text per page, an agent reads a PDF like a note) and **images for the model** (`img`, concept §12). Neither is in this part.

## The rule above all: an attachment behaves like a note

Chris: "I react allergically when an app suddenly behaves differently for things that feel adjacent" (principle of least astonishment). Wherever a person or an agent would expect an attachment to act like a note, it does:

- **History:** up to 10 versions, the same changesets (2 minutes idle for people, 10 for agents, 1 hour at most), the same `history` and `restore`.
- **Trash:** 30 days, restorable; `rm` moves to the trash.
- **Moves:** `mv` works on attachments, and links to a moved attachment in notes are rewritten as links to a moved note are (`![[shot.png]]`, `![](/Screenshots/shot.png)`, `[text](/x.pdf)`). `links`/`backlinks` show which notes link or embed an attachment.
- **Checkpoints** include attachments; a checkpoint's export and browsing show them.
- **Export and import** (ZIP) carry attachments at their paths.
- **Encryption** per vault, sealed like notes; re-keying covers them.
- **Deletion** (vault, account, GDPR) removes them; the deletion receipt counts them.
- **Activity** lists attachment writes, moves and removals like note changes.
- **Conflicts:** two different versions written against the same base give a conflict as for notes; in the Obsidian sync, a conflict copy with the usual name (`layout_editor_bad1 (conflict Laptop 2026-10-27 22-15).png`).
- **Writer identities** (person, device, agent) are recorded as for notes.

Where the two can't behave alike, the difference must be visible and explained, never silent: `cat` on an attachment says what it is instead of printing bytes; `rg` and search don't look into attachments (an image has no text; PDF text comes with tomorrow's PDF package).

## Goals

1. Store attachments in a vault: PDF, PNG, JPEG, GIF, WebP.
2. Upload them in the web app on phone and desktop.
3. Sync them with the Obsidian plugin, both ways.
4. Show them to people: a gallery per vault, a lightbox with zoom and pan, PDFs paged with pdf.js.
5. Everything in "The rule above all".

## Non-goals (this part)

- **`img` and anything that hands image pixels to a model** — tomorrow's image package.
- **PDF text extraction, embeddings, an agent reading a PDF** — tomorrow's PDF package.
- **Sharing to the PWA from the phone's share menu** (Web Share Target): it needs a service worker, and Chris prefers uploading by hand over adding one. Kumbu² keeps having no service worker.
- **Images embedded inline in a note's text.** On a phone they swamp the text; a vault image in a note is a tappable chip that opens the lightbox (below). Chris's call ("embedded images in documents are a horror on mobile").
- **Other file types** (Office, ZIP, audio, …): "no false promises, no dead files nobody does anything with". They are refused with a clear message.
- **Files over 15 MB, GridFS, chunked storage.** Chris: 16 MB is already huge for one file; most large files are a poor use of storage.
- **Server-side PDF rendering or image transcoding of uploads.** Shrinking an image happens in the browser before upload (below).

## Anti-goals (would be a failure even if it works)

- An attachment that behaves differently from a note where nothing explains why.
- A file that silently doesn't arrive (Obsidian or upload) with no visible warning.
- Untrusted files decoded on the server without limits (decompression bombs), or a PDF that can run code in the viewer.
- Images or PDFs fetched from third parties when a note is shown (concept: a note's external `![](https://…)` must never tell a third party who reads it).
- Attachments held in the server's memory as part of an unlocked vault's working set.

## Formats and limits (decided by Chris)

- Accepted: `.pdf`, `.png`, `.jpg`/`.jpeg`, `.gif`, `.webp`, besides `.md`. Anything else is refused: in the web app with a message naming the accepted types; in the Obsidian plugin it stays on the "not synced" list without a notice (below).
- **The content decides, not the extension:** the server checks the magic bytes (`%PDF-`, PNG signature, `FF D8 FF`, `GIF87a`/`GIF89a`, `RIFF….WEBP`) and refuses a file whose content doesn't match its extension. *(My call: a renamed `.exe` is not an image.)*
- **Size: at most 15 MB per file** (a MongoDB document holds 16 MB, and the sealing and metadata need room). *(My call on the exact figure: 15 × 1024 × 1024 bytes, said as "15 MB" everywhere.)*
  - **PDF over 15 MB:** refused with the limit named.
  - **Image over 15 MB (web app):** "Too large for Kumbu² (15 MB). Shrink it so it fits?" — on yes, the browser re-encodes it (canvas → JPEG or WebP, lowering quality and then dimensions until it fits) and uploads the result under the same name with the matching extension; the dialog says so if the extension changes, and that an animated GIF loses its animation.
  - **Image over 15 MB (Obsidian):** not synced, with a notice (below). The plugin doesn't re-encode.
- **Image dimensions:** the server reads only the header (ImageSharp `Image.Identify`) and refuses images over **50 megapixels** or 16,384 px on a side, before anything is decoded. *(My call on the numbers; it guards against decompression bombs: a 50,000 × 50,000 one-bit PNG is a few hundred KB on disk and 10 GB as RGBA.)* This part never decodes pixels on the server; tomorrow's `img` will, with these limits plus decoder memory and time caps.

## Storage (my call; the builder may refine)

Notes keep their history inside the note's sealed document, and an unlocked vault's notes are held in memory as its working set (`src/Kumbu.Core/Notes/StoredNote.cs`, `src/Kumbu.Server/Vaults/UnlockedVault.cs`). Neither works for attachments: 10 versions of 15 MB don't fit in one 16 MB document, and images don't belong in memory. So attachments **behave** like notes and are **stored** differently:

- **The attachment record** sits where notes sit and is loaded with them: path, media type, size, pixel dimensions (images), page count is *not* needed here (the PDF package adds it), a content hash, writer, changeset and times, and its history as a list of version entries (each: hash, size, writer, time, changeset). It is small, sealed like a note, and takes part in moves, trash, checkpoints, changesets and the activity exactly as a note does. Whether this is a `StoredNote` with a kind or a sibling type is the builder's call; the shared behaviour must not be duplicated.
- **The bytes** of each version are one sealed document in a new collection (e.g. `blobs`): the vault id, a keyed hash of the content (HMAC with a key derived from the vault key, never a plain SHA-256: a plain hash would let anyone with the database confirm that a user holds a known file), and the sealed bytes. Identical content within a vault is stored once and referenced by every record and version that has it; a blob is deleted when nothing references it any more (current versions, history, trash, checkpoints). Blobs are loaded on demand only, never into the working set.
- **Re-keying** re-seals blobs too. Its batches are sized in bytes (`VaultCatalog.Rekey.cs`, `BatchBytes` 4 MB); a blob larger than a batch is a batch of its own.
- **Memory:** reading streams one blob at a time; the export streams them into the ZIP; nothing loads all attachments of a vault at once.

## Server API (REST)

Paths follow the notes API; the builder chooses the exact shapes and documents them in the OpenAPI file and `docs/security-architecture.md`.

- **Read:** `GET` an attachment's bytes by vault and path, with its media type, `Content-Disposition` (inline for viewing, attachment for downloading), `X-Content-Type-Options: nosniff`, and the API's usual `Cache-Control: no-store`. Optionally a version from its history.
- **Write:** `PUT` the bytes to a path (an upload). Conditional on the current version token as notes are (`If-Match`-style), so the sync and two tabs get conflicts, not lost writes. Checks: format, magic bytes, size, image header limits. Request body capped at a little over 15 MB.
- **Upload of many files at once:** the existing upload (`src/Kumbu.Server/Api/UploadApi.cs`, `web/src/upload/`) takes notes as text in JSON. Attachments go one request each (multipart or raw body, builder's call); the web app's upload dialog sends notes and attachments of one pick together and reports them together.
- **Sync and clients already in use** *(my call)*: Obsidian plugin 0.1.1 takes every entry of `GET /api/vaults/{vault}/versions` as a note and would fetch attachments through the notes API. So `versions` lists attachments **only when the client asks** (a query parameter such as `attachments=1`); without it, the answer is exactly today's. The new plugin asks, and recognises a server without attachment support (no such field in the answer, or the attachments API answering 404) and then keeps today's behaviour, attachments on the "not synced" list, so plugin and server can be updated in either order.
- **Version tokens** *(my call)*: an attachment's version token is computed like a note's, from the plain bytes (`src/Kumbu.Core/Notes/VersionToken.cs`, `kumbu-obsidian/src/engine/version.ts`), so the plugin and the import can tell local changes without the vault key. Like a note's, it is stored only sealed and given only to authorised clients. The keyed HMAC is the blob's storage key in the database, which is in the clear and must not reveal a known file.
- **Tree, `versions` (sync), `history`, `restore`, `move`, `delete`, `links`/`backlinks`, activity, trash, checkpoints, export, import:** include attachments, with their kind and media type where a client needs to tell them apart.
- **Download of several files** (gallery multi-select): one request that streams a ZIP of the chosen paths, at most 100 files *(my call on the number)*. **It is treated like an export** (decided by Chris): by default it needs the password, which turns into a one-time ticket as `POST /api/export/ticket` does (`src/Kumbu.Server/Api/ExportTickets.cs`; the same mechanism or a sibling), session only, and every such download is logged with ids and counts as exports are. **A user setting turns the password off for their own multi-file downloads** ("Ask for my password when downloading several files", default on), for people who do it often; the log line stays either way. Downloading a single file never needs the password, as opening a note doesn't.
- **No tickets are needed** for anything else: the web app fetches from its own origin with its session (same-origin requests carry the `SameSite=Strict` cookie), the plugin sends its API key. Concept §12 named tickets for people and programs; apart from the multi-file download above, this part finds they aren't needed, and `docs/concept.md` §12 gets a sentence saying so. *(My finding.)*

## MCP and the shell (this part only)

The agent-facing view of attachments in this part is what keeps the rule above:

- `ls` lists attachments with their size (and dimensions for images), marked so they aren't mistaken for notes.
- `cat` on an attachment prints one line instead of bytes, e.g. `layout_editor_bad1.png: image (PNG), 1440×3120, 412 KB` — tomorrow's packages extend it.
- `mv`, `rm`, `history`, `restore`, `links`, `backlinks`, `stat` (if present) work on attachments.
- `rg` and `search` skip attachments; `help` says so.
- `write`/`edit`/`replace` refuse an attachment path with a clear message (an agent can't write binary through text tools).
- `help` gets a short chapter on attachments (formats, limit, what works). Tomorrow's two packages each add their own chapter (`help img`, `help pdf`); to keep their merges apart, this part adds the chapter registry entries for both as empty stubs that say "coming in 0.2.5" *(my call, to avoid the conflicts #73 and #74 had in `Shell.cs`, `Manual.cs`, `Help.cs` and the help tests)*.

## Web app

All in the existing look (`web/src/theme/schemes.ts` tokens), mobile first, and on the desktop too. Mockups from the session: `docs/superpowers/specs/2026-10-27-attachments-mockups/gallery.html` and `lightbox.html` (open in a browser; texts in German, as shown to Chris).

### Upload

- The existing upload sheet (`web/src/upload/UploadSheet.tsx`) accepts the five formats besides notes, from the file picker (on phones: the photo library or files, whatever the OS offers — best effort on Android and iOS), from drag and drop on the desktop, and from paste (Ctrl/Cmd+V) of an image on the desktop *(my addition; cheap)*. Pasted images get a name such as `pasted 2026-10-27 22-15.png` that the user can change before the upload.
- Name and folder can be set before the upload; existing files get the sheet's existing conflict handling (replace, skip, ask).
- Refusals and the "shrink it?" question as under "Formats and limits".

### Gallery

- One per vault, reached like the board (a route such as `/gallery/:vault` and an entry next to the board's).
- Shows images and PDFs; a PDF's tile is its first page, rendered in the browser with pdf.js.
- A segmented switch: **Date** (newest first, grouped by day), **Folder** (sections A–Z, files A–Z within), **Name** (flat, A–Z across folders). The device remembers the choice (local storage, like the colour scheme).
- Multi-select by long press (phone) or a select mode / Ctrl-click (desktop); an action bar with the count and size: **Download** (one file directly, several as ZIP), **Move**, **Delete** (to the trash, with the usual confirmation).
- Tiles load lazily as they scroll into view; images are shown from the bytes the API returns (the browser scales them; no thumbnails on the server in this part — *my call*; revisit if a vault with hundreds of images is slow).

### Lightbox

- Full screen on black. Top: close, file name, info, menu. Bottom: download, move, copy link (`![[name.png]]`), delete. Info: folder, dimensions or page count, size, date, writer.
- Swipe left/right: next/previous in the gallery's current order (or the folder's, when opened from the tree); swipe down: close; pinch: zoom; one finger while zoomed: pan; double tap: 2× and back. A zoom percentage while zoomed. Desktop: arrow keys, Esc, mouse wheel zoom, drag to pan.
- PDFs: pages one below the other, pinch zoom, "page 3 / 14".
- A library is fine (e.g. yet-another-react-lightbox with its zoom plugin, MIT; builder's call), as long as it works with touch on Android Chrome and iOS Safari and has no CDN dependency.

### pdf.js

- From npm (`pdfjs-dist`, Apache 2.0), served from our own origin, worker included; no CDN.
- A current version, at least one with the fix for CVE-2024-4367, and `isEvalSupported: false` explicitly. No scripting, no form JavaScript; links in a PDF open in a new tab with `noopener`, never navigate the app.
- The Content-Security-Policy gets only what pdf.js needs (worker from self, `blob:` where required); document every change in `docs/security-architecture.md`.

### History, trash, activity (decided by Chris)

- **History sheet** for an attachment: its versions as thumbnails (PDF: first page) with date and writer; a tap opens that version in the lightbox; **Restore** as for notes. Where notes show a text diff side by side, two image versions are shown side by side (PDF: first pages), without a computed diff.
- **Trash:** a row with the kind's icon, name and size, restorable as notes are.
- **Activity:** attachment writes, moves and removals as rows like notes'; opening one goes to the lightbox.
- **Checkpoints:** browsing a checkpoint shows its attachments (tree and lightbox); restoring from a checkpoint works as for notes.

### Tree and notes

- The tree shows attachments with an icon by kind; a tap opens the lightbox (in the folder's order).
- In a rendered note, a link or embed to a vault attachment (`![[shot.png]]`, `![](/Screenshots/shot.png)`, `[[report.pdf]]`) is a chip with an icon and the name; a tap opens the lightbox. A link to a missing attachment looks like a missing note's link. External images stay the existing placeholder (`web/src/note/MarkdownView.tsx:345`).
- Link completion (`[[` in the editor) offers attachments too.

## Obsidian plugin (repo `SCAI-NGO/kumbu-obsidian`)

- The five formats move from "not synced" to synced (`src/engine/paths.ts`, `eligibility`); other files stay on the list with a new reason text: "Kumbu² keeps notes, PDFs and images only." Dot folders stay ignored.
- Binary content through the same engine: versions, conflicts and conflict copies as for notes; a changed file is sent whole.
- **Over 15 MB:** not synced, reason "too large" with the attachment limit (notes keep their 1 MiB), and **an Obsidian notice once per file** (not on every sync), naming the file, the reason and what to do: for an image, "upload it in the Kumbu² web app, which can make it smaller"; for a PDF, "Kumbu² takes PDFs up to 15 MB". Unsupported formats get no notice (a typical vault has many; they were never promised). The status bar count and the settings list stay as they are.
- The plugin's version is bumped with the release, not in this part.

## Security (to document in `docs/security-architecture.md`)

- Untrusted bytes: magic-byte check, size cap before reading the body fully, header-only image inspection with pixel and side limits; no pixel decoding on the server in this part.
- Serving: correct `Content-Type` from the checked format (never from the client), `nosniff`, `Content-Disposition` with a safe file name, no SVG (not accepted, so no script in images).
- Keyed content hashes for anything stored in the clear (the blob key); version tokens stay sealed, as notes' do.
- pdf.js hardening as above; CSP changes listed.
- The multi-file download: like an export (password ticket, log) by default; the user's setting to skip the password is a deliberate convenience, recorded as such (the stolen-laptop row: a valid session can then download many attachments at once without the password, as it can open each one).
- The new limits in the threat tables (a user filling storage: 15 MB × 10 versions per path; the existing per-vault or per-user quotas, if any, include attachments; if there are none, say so as a known limit).

## Testing

- Server (xUnit): storage round trip, versions and restore, moves with link rewriting, trash, checkpoint and export/import including attachments, re-keying a vault with a blob larger than a batch, deletion and the receipt's count, magic-byte and limit refusals, a header claiming 100,000 × 100,000 refused without decoding, content-hash de-duplication and blob cleanup, conflict on a stale version, the shell commands above.
- Web (Vitest): the history sheet's image versions and restore, the multi-file download with and without the password setting, upload of mixed picks, refusals, the shrink path (the re-encode loop with a stubbed canvas), gallery orderings and multi-select, lightbox keyboard navigation, the note chip.
- Plugin (Vitest, fake server and file system as today): binary sync both ways, conflict copy, the once-per-file notice, the reason texts.
- A hands-on note, as with every step (`docs/notes-attachments-hands-on.md`): on a phone (Android Chrome) and a desktop, upload a screenshot, open it from the gallery and from a note, zoom, page a PDF, sync a PDF from Obsidian and back.

## Left to the builder

Exact API shapes and status codes, the record type's place in the code, collection and index names, the lightbox library, the re-encode loop's steps, UI details within the mockups, how the "not synced" notice remembers which files it has announced. Each choice goes into the plan or the hands-on note with a line of reason.

## Work across two repositories

One branch per repository (`attachments-foundation`), one pull request each, squash merge after CI is green and Chris has looked. The server's pull request goes first; the plugin's needs a server with the new API to test against.
