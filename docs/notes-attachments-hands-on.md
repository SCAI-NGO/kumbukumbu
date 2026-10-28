# Test notes: attachments, part 1, hands-on

Date: 2026-10-28. Hands-on test of attachments (spec [2026-10-27-attachments-foundation-design](superpowers/specs/2026-10-27-attachments-foundation-design.md)) by Claude, on the branch `attachments-foundation` of both repositories, before the pull requests.

Set-up: the server published from `src/Kumbu.Server` (Release) with the built web app in its `wwwroot`, on `127.0.0.1:8090`, against a local `mongod --replSet rs0`, search off (`Kumbu:Search:Enabled=false`), `Kumbu:PublicUrl=http://localhost:8090`, no dev token. The first account came from `bootstrap-admin` and was made through the join page. The browser was Playwright's Chromium (headless), driven by scripts: once at phone size with touch, as `Pixel 7` and as `iPhone 14` (WebKit isn't installed here, so "iPhone 14" is Chromium with the iPhone's viewport, pixel ratio, touch and user agent, **not Safari**), and once at desktop size (1440 × 900, mouse and keyboard). Pinch and pan were sent as raw touch events over the Chrome DevTools Protocol (`Input.dispatchTouchEvent`), double taps as two taps 80 ms apart. Fixtures were made by a script: canvas "screenshots" exported from the browser as PNG, JPEG and WebP, a five-page and a two-page PDF written by hand (text, a filled rectangle and a `Link` annotation), a 17 MB PNG of noise (2600 × 2200), a 16 MB PDF and a file with a HEIC header. The plugin was driven through its real engine and API client in Node, with the end-to-end harness's file adapter (`e2e/nodeFiles.ts`), against the same server.

## Checks

| Suite | Command | Result |
|---|---|---|
| Server and core | `dotnet test --solution Kumbu.slnx` | 2068 tests: 2062 passed, 6 skipped (the model tests and a TensorX key), 0 failed |
| Web app | `pnpm -C web test --run` | 71 files, 1042 tests passed |
| Web build | `pnpm -C web build` | built; the pdf.js chunk is 542 kB (165 kB gzipped), loaded only when a PDF is shown |
| Plugin | `pnpm test --run && pnpm lint && pnpm build` | 17 files, 277 tests passed; lint and build clean |
| Plugin race | `pnpm e2e` against this server | converged, 0 edits lost, 0 unexpected conflict copies (below) |

## Web app

| Step | Phone (Pixel 7, "iPhone 14") | Desktop |
|---|---|---|
| Upload a screenshot, a JPEG, a WebP, two PDFs and a `.heic` in one pick, from the gallery's Upload | The sheet said "5 files to upload, 1 file skipped"; opened, the skipped list read "IMG_0001.heic: not a format Kumbu² keeps: notes (.md, .markdown), PDFs (.pdf) and images (.png, .jpg, .jpeg, .gif, .webp)". Then "0 notes and 5 files uploaded, 0 replaced, 1 file skipped". The file input's `accept` lists the concrete types (`.pdf,.png,…,image/webp`), not `image/*` | – |
| The server refuses directly | – | `PUT` of `.heic` and `.svg`: 400 "Kumbu² keeps notes, PDFs and images (PNG, JPEG, GIF, WebP) only."; PDF bytes named `evil.png`: 400 "evil.png isn't a PNG image (its content doesn't match its name)."; 17 MB: 413 "A file can have at most 15 MB."; a second `PUT` without a version: 409 with the file's state |
| Gallery | Three tiles a row, PDFs as their first page, images as themselves, names on a gradient; Date (grouped "Today"), Folder and Name all ordered as expected | Five tiles a row; Ctrl-click selects, the bar shows "2 selected · 54.7 KB" |
| Open from the gallery | Tap → `/view/Work/layout_editor_bad1.png`, "2 / 5" in the gallery's Name order | Click → the lightbox, arrows at the sides |
| Zoom and pan | Double tap: 200 %, again: back; pinch out: 300 %, pinch in: back to fit; one-finger drag while zoomed pans. The percentage shows top right | Mouse wheel zooms (five 200-pixel wheel steps gave 739 %, quick), drag pans, double click goes back |
| Next, previous, close | Swipe left: the next file in the gallery's order (`paper.pdf` after `layout_editor_bad1.png` in Name order); swipe down closes | ArrowRight / ArrowLeft move through the Date order; Esc goes back to the gallery |
| Info | Folder, pages or pixel size, size, date, writer | as on the phone |
| Page a PDF | "Page 1 / 5" top right; two swipes up reached "Page 5 / 5"; pinch zoom 250 % | – |
| Open from a note | A note with `![[layout_editor_bad1.png]]`, `[[report.pdf]]`, `![](holiday.jpg)`, `![[missing.png]]` and an external image: the three files are chips with their kind's icon; `missing.png` looks like a missing note's link (dashed, "(missing)") and offers no "Create the note"; the external image stays `[image: x]`. A tap on the PDF chip opens the lightbox; Close goes back to the note | – |
| Open from the tree | The tree lists the files with their kind's icon; a tap opens the lightbox in the folder's order ("1 / 5") | – |
| Download two files | – | "Download 2 files? They come as one ZIP. Your password confirms it; Settings can turn this question off." With the password: `Work files 2026-10-28.zip` with the two files at their paths |
| Turn the question off | – | Unticking "Ask for my password when downloading several files" asked for the password first ("Download several files without the password? Anyone with this session could then take many files at once."), R16; ticking it again asked nothing. With it off, two files came as a ZIP without a question |
| A ZIP that fails | – | Two files selected, one trashed behind the gallery's back, then Download: the gallery said "There is no file at /Work/webp-shot.webp (if it was removed, it is in the trash)." in its bar; nothing replaced the page |
| History and restore | A new version of the PNG (`PUT` with its version); the lightbox's ⋯ → History shows both as thumbnails with time, writer and size; a tap shows "This version" over "Now" as pictures; "Restore this version" made the old one current, keeping the replaced one in the history | – |
| Move | ⋯ → "Rename or move": name (extension fixed) and folder. Moving `layout_editor_bad1.png` into `Screens` said "No links needed rewriting" (correct: `![[layout_editor_bad1.png]]` still resolves by name). Moving and renaming `holiday.jpg` to `Screens/holiday 2026.jpg` rewrote the note's `![](holiday.jpg)` to `![](/Screens/holiday%202026.jpg)` | – |
| Delete and restore | Delete in the lightbox: "Delete holiday.jpg? It goes to the vault's trash. This note links here; its link will lead nowhere: Bug report"; the note's chip then showed as missing. The vault's Trash listed both trashed files with the image icon, folder, size and "gone for good in 30 days"; Restore brought each back | – |
| Checkpoint | Checkpoints → Make "hands-on": "/Work/ · 2 notes · 5 files"; Open shows the checkpoint's tree with the files' icons, and a file opens in the lightbox ("Checkpoint hands-on · read only", `?checkpoint=hands-on`) | – |
| Export | Vault ⋯ → Export as ZIP… with the password: `Work 2026-10-28.zip` holds both notes and all five files at their paths, byte for byte equal to the fixtures (`cmp`), the note with its rewritten link | – |
| Shrink | The 17 MB PNG: "Too large for Kumbu² (15 MB). Shrink it so it fits? huge-photo.png · It is uploaded as huge-photo.webp. · Skip it / Shrink"; Shrink → "Shrinking huge-photo.png …" → uploaded as `huge-photo.webp`, 5.0 MB | – |
| A file in its own tab | – | `GET /api/files/Work/report.pdf`: `Content-Type: application/pdf`, `Content-Disposition: inline; filename="report.pdf"; filename*=UTF-8''report.pdf`, `Content-Security-Policy: sandbox; default-src 'none'; frame-ancestors 'none'`, `nosniff`, `no-store`, the version as `ETag`; `?download=1` gives `attachment` and the browser downloads `report.pdf`. Headless Chromium has no PDF viewer, so the PDF downloaded rather than showing in the tab. An image in its own tab shows, in an opaque origin (`document.domain` throws `SecurityError`) |
| Copy link | – | ⋯ → Copy link on a PDF put `[[report.pdf]]` on the clipboard |
| Activity | Attachment writes, moves (renamed, with `from`), restores and a shrunk upload show as rows like notes'; `fileKind` in the API | – |

No console errors in the app during any run, apart from the expected 404 of the forced ZIP error.

### Layout at phone width, against the mockups

The gallery matches `gallery.html`: header with back, title "Gallery · Work", Select and Upload; the segmented switch; day headings; square tiles three a row with the name on a gradient. The app is in the user's light scheme, the mockup in a dark one; both use the scheme's tokens. The lightbox follows `lightbox.html` in its parts (close, name, info, ⋯ on top; download, move, link, delete at the bottom), with two differences: the mockup shows the info line (folder · size · pixels · time) under the picture all the time, the app behind the Info button; and a tall phone screenshot fills the screen, so the top and bottom bars lie over the picture on a gradient rather than on black.

## Obsidian plugin

The engine ran in Node against the server, with a temp folder as the Obsidian vault (script outside the repository). Starting state: locally a PDF (`Papers/forensic report.pdf`), a PNG, a 17 MB PNG, a 16 MB PDF, a `.docx`, a note, and `Screenshots/Übersicht.png` named in NFD as macOS and iOS name it; in Kumbu² a PDF and a JPEG uploaded with another key.

| Step | Result |
|---|---|
| First sync ("Keep both") | 4 uploaded, 2 downloaded; not synced: `brief.docx` (unsupported), the 16 MB PDF and the 17 MB PNG (too large) |
| Both ways | The uploaded PDF and PNG are byte for byte the local files; the downloaded PDF and JPEG byte for byte the uploaded ones |
| NFD | The NFD name arrived as `Screenshots/Übersicht.png` in NFC; the local file kept its name |
| Reason texts | "Kumbu² keeps notes, PDFs and images only."; "Larger than 15 MB, the limit for PDFs."; "Larger than 15 MB. Upload it in the Kumbu² web app, which can make it smaller."; status bar summary "1 file, 2 too large" |
| Notice | Two new too-large files at once: one notice, "Kumbu² Sync: 2 files are too large to sync. The settings list them under “Not synced”, with what to do."; the next check: none; one new file alone: "Kumbu² Sync: Photos/huge-photo.png isn't synced. Larger than 15 MB. Upload it in the Kumbu² web app, which can make it smaller." No notice for the `.docx` |
| A local change | A new PNG written over the local file went up |
| Conflict | Kumbu² and the device both changed the PNG from the same base: `Screenshots/layout_editor_bad1 (conflict Laptop 2026-10-28 05-31).png`, on both sides |
| A deletion in Kumbu² | The trashed PDF went to the local `.trash/` |
| A local rename | Went up as a move (`keepLinks`, as for notes, since Obsidian rewrites the links itself) |

The race run (`pnpm e2e`, 180 s, notes only) against the same server (seed 495793): first sync 15 uploaded, 15 downloaded, 7 unchanged, 8 conflict copies; 490 operations from both sides over live hints; both phases converged, nothing lost, 0 unexpected conflict copies (7 during the run, all explained), exit 0.

## Found in hands-on

Nothing that loses or corrupts data, and nothing fixed in code beyond texts. Points for a later pass:

1. **Export texts named only notes.** The export's password question ("The ZIP holds the notes as plain text …"), the checkpoint export's, Settings' "Your data" and the welcome note "Your data" didn't mention the files the ZIP now carries; the welcome note "Obsidian" still said the plugin syncs text notes only. *Fixed* (texts only; the web tests check the two dialogs' sentences).
2. **Upload from the gallery leaves the gallery.** The gallery's Upload button opens the upload sheet over the notes view (by design in the code: "The upload sheet lives with the notes"); after the upload one is on "Your vaults", not back in the gallery. Surprising on a phone, where the gallery is where one looks for the new screenshot. See "Offen für Chris".
3. **The file counter is hard to read.** The lightbox's "2 / 5" is small grey text on the bottom gradient; on a light screenshot or a PDF page it all but disappears, and on a PDF it sits under "Page 1 / 5", two similar counters for different things.
4. **The Info panel is translucent.** Over a PDF page the page's text shows through behind "Folder · Pages · Size …" and makes both hard to read.
5. **Wheel zoom is quick.** Five ordinary wheel steps go from fit to over 700 %.
6. **An image in its own tab isn't fitted to the window.** The sandbox CSP (`default-src 'none'`) also blocks the inline styles of Chrome's built-in image viewer, so the image is shown at full size top left and the console logs three CSP errors. Harmless, and the app never shows files that way. See "Offen für Chris".
7. **A checkpoint's folder rows centre their name** (`Screens` in the middle of the row). Pre-existing (the folder button of `routes/Checkpoint.tsx` lacks `text-left`), not new in this branch.
8. **Activity after a trash and restore.** A file trashed and restored shows as "added" at the restore, and its removal no longer shows. Notes behave the same (checked with a note), so this is the existing activity, not a difference.

## Not tested here: a checklist for Chris

- [ ] **Real iOS Safari** (iPhone): pick a photo from the library and check it arrives as JPEG (HEIC converted by the picker); pinch, pan, double tap and swipe in the lightbox; a PDF's pages and pinch; long press in the gallery to select; the shrink offer for a large photo (Safari encodes no WebP from a canvas, so it should become JPEG); download of one file and of a ZIP; the hidden-frame ZIP error.
- [ ] **Real Android Chrome**: the same, with the photo picker and the file picker; Share → Kumbu² is not expected (no Web Share Target).
- [ ] **Real Obsidian** on desktop and phone: a Zotero PDF folder syncing up; an image pasted into a note syncing up; the notice for a file over 15 MB appearing once per file (and again after the file shrinks and grows); the settings' "Not synced" list; a conflict copy's name on a real device name; the plugin against an older server (no `attachments` field) leaving files alone.
- [ ] **A PDF in its own tab** in a browser with a PDF viewer (desktop Chrome, Firefox, Safari): it should show (the viewer runs inside the sandbox) and its links must not reach the app.
- [ ] **Behind the real proxy**: an upload of 14 MB passes (body size limit at least 15 MB), and a 16 MB one gets Kumbu²'s 413 sentence, not the proxy's.
- [ ] **Many images**: a vault with a few hundred photos in the gallery on a phone (tiles load lazily, at most six at once; no server thumbnails).
- [ ] **HEIC conversion on iOS** specifically when sharing from Files rather than Photos (the Files picker may hand over the `.heic` itself, which is then refused with the formats named).

## Offen für Chris

The items below are kept verbatim, in German, as they were written for the maintainer during the build. ImageSharp was dropped for a header-only parser of our own; the first item is the record of that decision.

- **ImageSharp braucht einen Lizenzschlüssel.** SixLabors.ImageSharp 3.2 und 4.x brechen den Release-Build ohne `SixLaborsLicenseKey` ab (CI und Dockerfile bauen Release); 3.1.12 hat bekannte Sicherheitslücken (NU1902/NU1903). Gewählt: ein eigener Header-Parser ohne Abhängigkeit (PNG, JPEG mit EXIF-Orientierung, GIF, WebP), der nur Header liest. Für morgen (`img` dekodiert Pixel) brauchst du eine Entscheidung: Six-Labors-Community-Schlüssel holen und einbinden, oder eine andere Bibliothek (z. B. SkiaSharp/libvips-Bindings).
- **Passwort-Abfrage beim Mehrfach-Download abschalten verlangt das Passwort.** Sonst könnte eine gestohlene Sitzung den Schutz erst abschalten und dann alles herunterladen. Einschalten geht ohne Passwort. Wenn du das nicht willst: ein `if` in `AccountApi.SetPreferencesAsync` entfernen, der Einstellungsdialog fragt dann nicht mehr.
- **Beschädigte Notizen fehlen still im Export (schon vor diesem Branch so).** Anhänge, deren Bytes sich nicht öffnen lassen, brechen den Download jetzt sichtbar ab (Spec-Anti-Ziel „Datei kommt still nicht an"). Notizen, die sich nicht öffnen lassen, lässt der Export weiterhin ohne Hinweis weg. Soll das angeglichen werden (Abbruch oder Liste im ZIP)?
- **pdf.js 6.4 kennt `isEvalSupported` nicht mehr.** Die Option wird gesetzt (Plan), wirkt aber nicht; pdf.js 6.x erzeugt keinen Code per `eval` mehr, und die CSP ohne `'unsafe-eval'` verbietet es ohnehin. Wenn dir eine wirksame Option wichtig ist: pdf.js 5.7.x ist die letzte Version mit ihr.
- **Gesten ohne Bibliothek.** Pinch, Pan, Doppeltipp und Wischen sind eigener Code (`web/src/attachments/gestures.ts`), keine Lightbox-Bibliothek. Begründung in der Datei; auf echten Handys noch ungetestet.
- **Mehrfach-Download als POST-Formular in ein verstecktes iframe.** 100 Pfade in einer GET-URL können 8 KB überschreiten; deshalb gibt es zusätzlich `POST /api/files/{vault}/zip` (Formular). Damit Fehler in der App erscheinen statt die Seite zu ersetzen, antwortet nur diese POST-Route mit `frame-ancestors 'self'` (sonst überall `'none'`). Wenn dir das nicht gefällt: Formular ohne iframe (Fehler ersetzt dann die Seite).
- **Plugin: Hinweis auch für Notizen über 1 MiB.** Das Plugin meldet jetzt einmal pro Datei per Notice, wenn sie zu groß ist – auch bei Notizen über 1 MiB (bisher nur still in der Liste „nicht synchronisiert"). Das ist für Nutzer von 0.1.1 sichtbar neu, auch gegen alte Server. Wenn nur Anhänge gemeldet werden sollen: ein Zweig in `src/obsidian/announce.ts`.
- **Plugin: übergroße lokale Datei vs. verkleinerte Version in Kumbu².** Der Plugin-Hinweis rät, ein zu großes Bild in der Web-App hochzuladen (die es verkleinert). Danach liegt lokal das Original (z. B. 20 MB), in Kumbu² die kleinere Fassung unter demselben Namen. Gewählt (vorsichtig): das Plugin liest und überschreibt die lokale Datei nicht, sie bleibt in der Liste „nicht synchronisiert"; die kleinere Fassung wird nicht darüber geladen. Alternative: die kleine Fassung als Konfliktkopie holen, oder die lokale ersetzen.
- **Plugin: Umbenennen von `a.png` in `a.jpg` mit PNG-Inhalt.** Der Server lehnt die falsche Endung ab; das Plugin hat die alte Datei in Kumbu² aber schon in den Papierkorb gelegt (30 Tage wiederherstellbar). Die lokale Datei bleibt unversehrt und steht mit dem Grund in der Liste. Zurückhalten des Löschens bräuchte zusätzlichen Zustand.
- **Upload aus der Galerie landet bei den Notizen.** Der Upload-Knopf der Galerie öffnet das Upload-Blatt über der Notizansicht; nach dem Hochladen ist man auf „Your vaults", nicht zurück in der Galerie. So gebaut, weil das Blatt zur Notizansicht gehört. Auf dem Handy überraschend, wo man den neuen Screenshot in der Galerie sucht. Soll das Blatt in der Galerie aufgehen (und danach dort bleiben)?
- **Eine Datei im eigenen Tab: der Bildbetrachter des Browsers verliert seine Styles.** Die Sandbox-CSP der Datei-Antworten (`sandbox; default-src 'none'`) blockiert auch die Inline-Styles von Chromes eingebautem Bildbetrachter: das Bild steht in voller Größe oben links statt eingepasst, die Konsole meldet CSP-Fehler. Harmlos. Gewählt: so lassen (strengste Variante). Alternative: `style-src 'unsafe-inline'` in diese CSP aufnehmen (Skripte bleiben durch `sandbox` und `default-src 'none'` aus).
- **Die neue Plugin-Version im README und in der Willkommensnotiz.** Beide sagen jetzt „ab der Plugin-Version nach 0.1.1" bzw. „Kumbu² Sync 0.1.1 und älter synchronisieren nur Text", weil die Versionsnummer erst mit dem Release festgelegt wird. Beim Release die konkrete Nummer eintragen.
