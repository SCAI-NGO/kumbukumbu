# Ingesting attachments: from PDFs and images to notes

Date: 2026-11-08. Status: approved by the maintainer on 2026-11-08. Target: 0.4.0. Builds on `feature/skills` (system skills, §3.11 of `2026-11-08-skills-design.md`), which builds on `feature/webdav`.

## 1. Why

PDFs and photos pile up in a vault: manuals, data sheets, slides, invoices, whiteboard photos, pictures from the garden. Their text is searchable at best page by page, their pictures not at all. A capable model can turn them into proper notes: Markdown with headings, lists and tables, the pictures kept and described. This is a task for a **skill**, not a tool: the model does the judging, Kumbu² supplies the building blocks.

Two building blocks are missing today:

- A model connected over MCP can look at a PDF's pictures (`pdf --images`, `pdf --crop`) but cannot **store** them: `write` takes Markdown only. A note could only link to a PDF page, and that link breaks once the PDF is deleted.
- After ingesting, the source is either kept, and then every search finds its content twice, or deleted. There is no "keep it, but out of the way".

## 2. Goals, non-goals

**Goals**

- A new `sh` command **`pdfimages`** that stores the pictures of a PDF as image attachments: the embedded images, and any region of a page (a vector drawing, a chart, a circuit diagram) rendered as an image.
- A per-vault **archive**: a folder `/<vault>/_archive/` whose contents `ls`, `rg`, `semsearch`, `tags`, `backlinks` and the web app's tree and search leave out unless asked; a command **`archive <path>`** that moves something there under a timestamped name; an **Archive** view in the web app that shows what the archive holds and how much space it takes.
- A system skill **`ingest-attachments`** that turns one or more PDFs or images into notes, using the two above.

**Non-goals (later, if asked for)**

- Other formats (Word, PowerPoint, spreadsheets, e-mail). The skill's name leaves room for other "ingest" skills.
- OCR or any text recognition on the server. The model reads scans from the rendered pages, as `pdf` already lets it.
- The original encoded bytes of an embedded image (e.g. the JPEG stream as it sits in the PDF). Images are decoded and encoded anew (§3.1).
- Ingesting automatically on upload, or from the web app. The user asks their AI.
- An archive that expires or empties itself, a separate quota for it, or hiding it from WebDAV, Obsidian sync or the export.
- Archiving from the web app's tree. Moving into the archive is `archive` in `sh` (and so the AI); the web app shows the archive and restores or deletes from it.

## 3. Decisions taken

### 3.1 `pdfimages`

Decided by the maintainer: a console command that extracts pictures, not a general "save a crop" option on `pdf`. Vector drawings must be stored as images too, because **no information may be lost** in an ingest: the pictures stay in the notes, correct and concrete. PNG or JPEG for them is fine.

```
pdfimages <pdf> [pages] <folder>/                 embedded images of the pages (default all) into <folder>/
pdfimages <pdf> <page> --crop x,y,w,h <file.png>  one region of one page, rendered, into <file.png>
pdfimages --list <pdf> [pages]                    only list: page, number, pixel size, where on the page; writes nothing
```

- **Size: at most 8 MP.** Decided by the maintainer: Kumbu² keeps knowledge, not a photo album; knowledge needs no 50 MP, and the original stays for printing and showing. Every stored image, embedded or cropped, is scaled down to fit **8 megapixels** (aspect ratio kept, e.g. 3264×2448 for 4:3); smaller ones keep their own size. The output says per file when it was scaled down, and from what size.
- **Embedded images.** Every image the PDF paints on the given pages (image XObjects and inline images, as `measureDrawing` in `pdf/src/analyse.ts:112-186` already finds them), decoded by pdf.js, scaled down to 8 MP where larger, and encoded as **PNG**. Where the PNG would exceed the attachment limit (15 MB, `AttachmentFormat.MaxBytes`), JPEG at quality 90, then lower down to 60 (a list of its own: `pdf` and `img` keep theirs, `pdf/src/encode.ts:10`); an image that does not fit even then is skipped and named in the output.
  - Images smaller than the `MIN_FIGURE` share of their page (`pdf/src/classify.ts:36`, 1 %) are left out as decoration (bullets, rules, icons) unless `--all` is given; the output says how many were left out.
  - The same image painted on several pages (a logo in the header) is stored **once**, identified by a hash of its pixels; the output lists every page it appears on.
  - Images that pdf.js drops for exceeding `MAX_IMAGE_PIXELS` (50 MP, `pdf/src/pdfjs.ts:14`) leave no paint operation; where the sidecar can count pdf.js's warning for them, the output says "page N: 1 image over 50 MP left out (look with `pdf --crop`)".
  - Names: `<pdf name>-p<page>-<n>.<ext>`, n counting the images of that page from 1 in painting order, e.g. `manual-p4-1.png`. The folder is created if it does not exist.
- **A region (`--crop`).** One page, a rectangle in the same points and coordinates as `pdf --crop` and `pdf info`'s regions (`Crop.TryParse`, `src/Kumbu.Core/Attachments/Crop.cs:14-38`), rendered anew at **300 dpi**, capped at 8 MP (the scale lowered to fit), encoded as PNG; if that exceeds 15 MB, JPEG at quality 90 down to 60. The target's extension follows the encoding: a `.png` target that has to become a JPEG is refused with a message to use `.jpg`. (In practice drawings compress well as PNG.) `--rotate` works as with `pdf`.
- **Output** of a writing call: one line per stored file with its path, page(s), pixel size, bytes and its rectangle on the page in points, ready to place the image and to name it in a note. For example:
  ```
  stored 3 images in /Work/Manual/images/ (2 left out as smaller than 1 % of their page, --all keeps them)
  /Work/Manual/images/manual-p1-1.png  p1  1200×400  88 KB  at 72,40,451,150
  /Work/Manual/images/manual-p4-1.png  p4, p9  2400×1600  1.9 MB  at 80,300,435,290
  /Work/Manual/images/manual-p7-1.jpg  p7  4000×3000  6.2 MB (JPEG: PNG over 15 MB)  at 0,0,595,842
  ```
- **Never overwrites.** Before writing the first file, every target name is checked; if any exists, nothing is written and the output names the existing ones (like `mv`, `src/Kumbu.Core/Sh/Commands/Mv.cs:20`). Choose another folder, or remove them first.
- **Writing** goes through `AttachmentWriter.WriteAsync` (`src/Kumbu.Core/Attachments/AttachmentWriter.cs:53-118`): its permission check (`Refuse("upload", …)`), format and magic-byte check, name rules, quota and encryption apply unchanged. Each file is its own write, in page order. If one fails midway (quota full), the command stops, keeps what it stored and says which files are stored and which not.
- **Where the target may be:** any folder in any vault the caller may write, like `write`. The source needs read access.
- **Permissions and channels:** a writing command. The web app's read-only console refuses it, `--list` included (`src/Kumbu.Server/Api/ConsoleApi.cs:39-45`; simplest to classify the whole command). With a checkpoint (`--checkpoint`) it is refused, as other writing commands are.
- **Memory:** pdf.js decodes an embedded image at full size before it is scaled down, so extraction runs in **one worker at a time**, image by image; the sidecar's `mem_limit` (`compose.yaml`) stays as it is (decided by the maintainer).
- **Where the work happens:** in the PDF sidecar (`pdf/`), the only place that opens PDFs. New routes for listing a page range's images and for producing one image (embedded by its number on the page, or a rendered region at 300 dpi), each returning one image at a time so the sidecar's 16 MiB limits stay as they are (`pdf/src/server.ts:16`, `src/Kumbu.Server/Attachments/PdfSidecar.cs:72`). `IAttachmentPixels` (`src/Kumbu.Core/Attachments/IAttachmentPixels.cs:85-103`) gains the matching methods; they return width and height with the bytes, which the server checks with `ImageHeader.Read` (`src/Kumbu.Server/Attachments/ImageHeader.cs:13`) as `PdfSidecar` does for pictures today.
- **Registries:** `KumbuMetrics.KnownCommands`, `ConsoleApi` (refused), `web/src/console/help.ts`, `Manual.ToolDescription`, the `attachments` help topic, `HelpTests`. A pointer in `pdf`'s help: "to store pictures as images: `pdfimages`".

### 3.2 The archive

Decided by the maintainer: a folder per vault, not a flag per file, so it is visible everywhere files are visible; and the web app must **show** it, like a bin, not hide it behind a switch, because otherwise it fills up over the years, takes the quota, and nobody remembers it is there.

- **Where:** exactly `/<vault>/_archive/` at the root of a vault. A folder of that name elsewhere is an ordinary folder. Nothing else about leading underscores changes. (The board's `/<vault>/Board/Archive`, `src/Kumbu.Core/Board/BoardModel.cs:22-27`, is unrelated and stays.)
- **`archive <path>`**: moves a note, an attachment or a folder (path ending in `/`) into the archive, keeping its folder path and adding a timestamp to its name: `/Work/Inbox/report.pdf` becomes `/Work/_archive/Inbox/report 2026-11-08 213005.pdf`; a folder `/Work/Inbox/Manual/` becomes `/Work/_archive/Inbox/Manual 2026-11-08 213005/`. The timestamp is `YYYY-MM-DD HHmmss`, with seconds (decided by the maintainer: more correct than a counter; on the rare clash within one second the name still gets `-2`). It uses `NoteMover` (`mv`'s machinery), so history moves along and **links follow**: a note that links to the source now links into the archive, which is what keeps an ingested note's "Source:" link working. Refused for a path already in the archive, for the archive itself, for a vault's root, for anything in the vault **Skills**, and for a skill or anything inside a skill folder (`_skills/…`): archiving skills is not needed in this version (decided by the maintainer; to be reconsidered if users ask), and text files may only live in skill folders. The message says so and points to `rm`. Board and structural files follow `mv`'s permission rules unchanged.
  - Skill discovery skips a folder `_archive` in the vault Skills (`SkillDirectory.cs:96-114`), so a hand-made one never shows as a broken skill. This is the one change to the skills machinery.
  - The timestamp is in the server's time zone (the container's `TZ`, UTC unless set), documented in the README next to the other settings, so the name reads right for the people of that server (decided by the maintainer).
- **Hidden unless asked**, each with a new `--archive` option:
  - `ls`: leaves the `_archive/` entry out of a vault's root listing (decided by the maintainer: out of `ls` completely without `--archive`).
  - `rg` (also `--files`, `--tag`, and the "no such note, did you mean" name hint), `semsearch` (note hits **and** PDF page hits, which today come straight from the index, `src/Kumbu.Core/Search/SectionSearch.cs:176-188`), `tags`, `backlinks`.
  - A path **inside** the archive counts as asking for it: `ls /Work/_archive/`, `rg foo /Work/_archive/`, `cat`, `pdf`, `img`, `links`, `outline` on an archived path work as on any other.
  - Not hidden: `history` (an audit log shows what happened), `trash`, `du` (shows the archive as its own line, so space is visible), `df`, `restore`, `mv`, checkpoints, the board.
- **Links** prefer what is outside the archive: where a link (a bare name, a suffix) matches several notes or attachments and some lie outside `_archive/`, it resolves to those (`LinkResolver.cs:115-140`); only when every candidate is archived does it resolve into the archive. So an archived `Overview.md` never makes a living `[[Overview]]` ambiguous (decided by the maintainer).
- **The search index** keeps archived items; they are left out at query time. Restoring is then cheap, and `--archive` finds them by meaning too.
- **Restoring** in `sh` is `mv` back (it never overwrites; the AI asks the user when the name is taken). Writing or moving into `_archive/` by hand is allowed; such a file simply has no timestamp.
- **Not hidden from** WebDAV, Obsidian sync, the export, publishing or sharing (decided by the maintainer: those see every file, which is fine here). Archive hiding is about attention, **not a security boundary**: anyone who may read the vault may read its archive.
- **Web app:**
  - The vault's tree leaves `_archive/` out (`NoteApi.TreeOf`, `src/Kumbu.Server/Api/NoteApi.cs:422-440`, and the folder pickers that use it, e.g. Move and New folder) and shows instead, at the bottom of the vault's tree, a dimmed entry **Archive · 23 files · 412 MB** (count and size of everything below `_archive/`), present only when the archive is not empty.
  - It opens an **Archive view**: the archived items, newest first, each with its original path, the date it was archived (from the name's timestamp), size, **Open**, **Restore** and **Delete**. Restore moves the item back to its original folder without the timestamp; if that name is taken, a dialog asks for another. Delete moves it to the trash, as everywhere, a folder with everything in it: a new endpoint that puts a folder in the trash, built on what WebDAV uses (`TreeWriter.TrashAsync`, `NoteMover.cs:37`), with `rm`'s permission rules (decided by the maintainer). Both need write access; readers see the view without the buttons.
  - The search leaves the archive out, with a checkbox **Include archive** (like `--archive`).
  - The storage notice (`web/src/shell/StorageNotice.tsx:22`) names the archive along with the trash when it holds a noticeable share (from 10 % of the used space).

### 3.3 The system skill `ingest-attachments`

Decided by the maintainer: the name `ingest-attachments`, since other things may be ingested one day; plural, since a user may hand over several files at once ("make one note of these four photos of wild flowers, and look up what you know about the plant"). The Agent Skills name rules allow no underscore (`SkillManifest.cs:166`), so it is written with a hyphen.

Embedded like `create-skill` at `src/Kumbu.Core/Skills/System/ingest-attachments/SKILL.md`. Its description names the situations: the user wants knowledge from PDFs or images in their vault turned into notes ("make notes of this PDF", "ingest these screenshots", "what's in this manual, write it down"). Its instructions, short and in order, cover:

1. **What and where.** Which files (paths, a folder, "the new PDFs in /Inbox"); find them with `ls`/`rg --files` if the user named them loosely. Read access is enough to start; writing needs `write` on the target.
2. **Ask once, up front**, unless the user already said:
   - for a PDF: a **folder of notes** for this PDF (`<folder>/<title>/`, an overview note plus one note per chapter or topic, images in `images/`) or **one note**; recommend by size and structure (`pdf info`): one note for a few pages, a folder for a long document;
   - for several files: one note for all of them, or one each;
   - where the notes go, if not obvious (beside the source, or a folder the user names).
3. **Read the whole source.** `pdf info`, then `pdf` page by page (rendered pages for scans and figures), `img` for images. Nothing is left out because it seems minor; tables become Markdown tables, lists stay lists, numbers and units are copied exactly (look at a crop before quoting small print, as `pdf`'s help advises).
4. **Store the pictures.** `pdfimages <pdf> <folder>/images/` for the embedded images; for every drawing region `pdf info` lists (charts, diagrams, schematics), `pdfimages <pdf> <page> --crop … <folder>/images/<name>.png` with a name that says what it shows. Decorative images (`--all` is not needed) stay out.
5. **Write the notes** with `write`: Markdown, the structure of the source, each picture embedded where it belongs (`![What it shows](/Folder/images/manual-p4-1.png)`), and under each picture **what it shows in words**: the information it carries, values of a chart as a table where they can be read, the labels of a diagram. No question per picture. A line at the top names the source with a link (`Source: [manual.pdf](/Inbox/manual.pdf), pages 1–40`). The notes are in the language of the source unless the user asks otherwise, since translating loses information (decided by the maintainer).
6. **Images as input.** The image stays where it is (or moves beside the note if the user wants), is embedded in the note, and its information is written out as text or a table. Then ask: "Do you want to add a description or notes of your own?" For several images, one question for all. If the user asks for more (identify a plant, look something up), do it with the tools the AI has, and say what comes from where.
7. **Ask what happens to the source**, once for all files: **keep** it, **archive** it (`archive <path>`; links follow into the archive, so the Source line keeps working), or **delete** it (`rm`, in the trash for 30 days). Recommend archive.
8. **Report**: the notes and images written, anything left out and why (an image too large, a page unreadable).

Without `write` on the target, it does not look for another way: it gives the user the notes' text and says that the pictures need `pdfimages` with write access.

## 4. End to end

The user drags `Pump manual.pdf` (40 pages, 12 photos, 6 vector diagrams, a logo on every page) into `/Work/Inbox/` over WebDAV and says to Claude: "Make notes of the pump manual in Inbox."

1. Claude finds `ingest-attachments` in the skills list, reads it, runs `pdf info "/Work/Inbox/Pump manual.pdf"`: 40 pages, mixed, regions on 14 pages.
2. It asks: "It's 40 pages in six chapters. A folder `/Work/Pump manual/` with an overview and one note per chapter, or one note?" The user says folder.
3. `pdfimages "/Work/Inbox/Pump manual.pdf" "/Work/Pump manual/images/"` stores 12 photos and the logo once (`Pump manual-p1-1.png`, pages 1–40), leaves out 9 small icons, and lists each with its rectangle.
4. For the six diagram regions from `pdf info`, it runs e.g. `pdfimages "/Work/Inbox/Pump manual.pdf" 12 --crop 60,120,470,380 "/Work/Pump manual/images/wiring diagram.png"`.
5. It reads all 40 pages and writes `/Work/Pump manual/Overview.md` and six chapter notes, each picture embedded where it stood, the wiring diagram followed by a table of terminals and wire colours, the pressure curve followed by its values.
6. "Keep, archive or delete the PDF?" Archive. `archive "/Work/Inbox/Pump manual.pdf"` moves it to `/Work/_archive/Inbox/Pump manual 2026-11-08 213005.pdf`; the Source lines in the seven notes now point there.
7. `semsearch "maximum pressure of the pump"` finds the chapter note, not the PDF page twice. In the web app, the tree shows `Pump manual/` and, dimmed at the bottom, **Archive · 1 file · 8.4 MB**.

## 5. Security

- The sidecar already opens untrusted PDFs; the new routes run there with the same limits (50 MP per image, 16 MiB per response, its timeouts). Images are **decoded and encoded anew**, never passed through as the PDF's bytes, so what is stored is a plain PNG or JPEG that passes the magic-byte check.
- Every stored file goes through `AttachmentWriter`: write permission on the target vault, name rules, quota, encryption. No new way to write around it.
- A count limit per call: at most 500 images stored by one `pdfimages` call; above that it stops with a message to give a page range. (Protects the quota and the sidecar from a PDF with thousands of tiny images.)
- The read-only web console refuses `pdfimages` and `archive`.
- Archive hiding is not access control; the README and `ls`'s help say so.

**Not touched:** encryption, accounts, sharing and roles, OAuth, WebDAV, Obsidian sync, export, publishing, the skills machinery beyond adding a system skill and skipping `_archive` in the vault Skills, the sidecar's `mem_limit`.

## 6. Documentation

- README: `pdfimages`, the archive (what is hidden where, the time zone of the timestamp), the new system skill.
- `sh` help and `Manual.ToolDescription` for `pdfimages`, `archive` and every `--archive`; the `attachments` help topic.
- Welcome notes: a short paragraph in `src/Kumbu.Server/Welcome/Notes/Skills.md` with the end-to-end example in two sentences.

## 7. Order of work

1. Sidecar routes and `IAttachmentPixels` methods, with sidecar tests on generated PDFs (`pdf/test/fixtures.ts`: photo, chart, logo on every page, a huge image).
2. `pdfimages` in `sh`, with Core tests on fakes and a server test through MCP.
3. The archive in `sh`: `archive`, `--archive` on each command, hiding in `semsearch`'s PDF hits.
4. The archive in the web app: tree, Archive view, search checkbox, storage notice.
5. The system skill and documentation.

## 8. Left to the builder

- The sidecar's route shapes and how an embedded image is addressed (page plus painting order is fine).
- Whether a shared helper filters the archive or each command does it; the precedent is `.vaultkeep` (`src/Kumbu.Core/Attachments/FolderMarker.cs:7-14`, tested in `tests/Kumbu.Core.Tests/Sh/FolderMarkerHiddenTests.cs`).
- The Archive view's exact layout, matching the app's existing screens.
