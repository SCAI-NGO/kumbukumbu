# Export and Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A user exports one vault or several as a ZIP of Markdown folders that open as Obsidian vaults, and imports such a ZIP into new or existing vaults.

**Architecture:** The server streams the ZIP from the session's unlocked vaults (one new file, `ExportApi.cs`). Import is client-only: the browser unpacks the ZIP with `fflate`, compares hashes with the vault's version list, asks once per existing vault, then uses the existing `POST /api/vaults` and `POST /api/upload/{vault}` in batches.

**Tech Stack:** .NET 10, `System.IO.Compression`, xUnit with `KumbuAppFactory`; React 18, TanStack Query, `fflate`, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-26-demo-round-design.md` §2 (binding), plus `Global`.

## Where and how this runs

- Branch `export-and-import` from `master`; one pull request; Chris merges it.
- **Merge order:** after `step-10-oauth` (if it merges first, symbols such as `VaultBusyException` exist; named, not line-numbered, below) and **after the Obsidian sync server PR** (`docs/superpowers/plans/2026-10-26-obsidian-sync-server.md`, its Task 4 adds `GET /api/vaults/{vault}/versions`, which the import pre-check needs). Tasks 1–2 (export) don't depend on it and can be built first.
- Commands: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~ExportApiTests"`, `pnpm -C web test`, `pnpm -C web build`; after a server contract change `pnpm -C web gen:api` against a running server (port 8083).
- Commit messages imperative, free-form, with the session's attribution lines.

## Global Constraints

- Sessions only for every endpoint here (`SessionOnlyMetadata`); API keys, connections and the dev token get `403`. Never an agent.
- Export holds only current notes, verbatim: no history, trash, checkpoints or settings.
- ZIP names: one vault `<Vault> <yyyy-MM-dd>.zip`; several `Kumbu² export <yyyy-MM-dd>.zip` (server's UTC date). Inside, one top-level folder per vault named exactly as the vault, notes at their paths below it.
- Entry modification time = the note's `LastWriteAt`.
- Import limits are the upload's: at most **200 files** and **5 MB** of text per request (`UploadApi.MaxFiles`, `UploadApi.MaxBytes`); a note at most **1 MiB** (`NoteWriter.MaxBytes`).
- Version hash for the pre-check: first **12** hex characters of SHA-256 over the UTF-8 text after CRLF → LF (`VersionToken.Of`).
- Copy (exact): vault menu `Export as ZIP`; Settings section `Your data`; buttons `Export my vaults…`, `Import vaults…`; summary `<n> vaults, <m> notes imported · ✓ no conflicts`.
- British English; the repo's comment density.

## Review Focus

1. **A `.md` file in the ZIP that isn't UTF-8** (Latin-1 from an old tool): expected to be skipped with "not UTF-8 text", never imported with replacement characters. Test in Task 3 (`a file that isn't UTF-8 is skipped, not mangled`).
2. **A ZIP with attachments of hundreds of MB:** expected to import the notes without inflating images or PDFs into memory. Test in Task 3 (`entries that aren't notes are never inflated`).
3. **Entry names that escape or confuse** (`../x.md`, `/abs.md`, `a\b.md`, `__MACOSX/`, `.DS_Store`, `.obsidian/`): expected skipped with a reason, nothing written outside its vault. Test in Task 3 (`unsafe and system entries are skipped`).
4. **Note and folder names with non-ASCII characters** (`Bücher/Ü².md`): expected to unpack with the same names on Windows and macOS (UTF-8 flag on entries). Test in Task 1 (`Non_ascii_names_survive_the_zip`).
5. **Importing the same export twice:** expected nothing written the second time and the summary saying so, no conflict question. Test in Task 4 (`a second import of the same export changes nothing`).

## API contract

```
GET /api/vaults/{vault}/export                     session only
200 application/zip, Content-Disposition: attachment; filename="Work 2026-10-26.zip"; filename*=UTF-8''…
404 vault unknown to the session   403 not a session   503 vault being re-keyed (as other vault endpoints)

GET /api/export?vault=Work&vault=Ideas             session only; each vault once, at least one
200 application/zip, filename "Kumbu export 2026-10-26.zip", filename* "Kumbu² export 2026-10-26.zip"
400 no vault given   404 any vault unknown (nothing is sent)
```

Used, unchanged: `GET /api/vaults`, `POST /api/vaults {name}`, `POST /api/upload/{vault}/{**folder}` (`{files, onConflict}` → `UploadResultView`), and from the Obsidian server PR `GET /api/vaults/{vault}/versions` (full list: `{ notes: [{ path: "/Work/a.md", version }], … }`).

---

### Task 1: Export endpoints

**Files:**
- Create: `src/Kumbu.Server/Api/ExportApi.cs`
- Modify: `src/Kumbu.Server/Program.cs` (register next to `app.MapUploadApi()`)
- Test: `tests/Kumbu.Server.Tests/Api/ExportApiTests.cs`

**Interfaces:**
- Produces: `public static class ExportApi { public static void MapExportApi(this IEndpointRouteBuilder app); }` with the two routes of the contract; `internal static string ZipName(IReadOnlyList<string> vaults, DateTime utcNow)`.

- [ ] **Step 1: Write the failing tests** (style of `UploadApiTests`, `RestClient.NewAsync`, open the response with `new ZipArchive(stream)`):
  - `A_vault_exports_as_one_folder_of_its_notes`: create `/Arbeit/a.md` and `/Arbeit/p/b.md`; entries are exactly `Arbeit/a.md`, `Arbeit/p/b.md`; contents verbatim; `Content-Disposition` file name `Arbeit <today>.zip`.
  - `Entries_carry_the_last_write_time`: each entry's `LastWriteTime.UtcDateTime` within 2 s of the note's `lastWriteAt` (ZIP time has 2-second precision).
  - `Non_ascii_names_survive_the_zip`: `/Arbeit/Bücher/Ü².md` reads back with that exact entry name.
  - `An_empty_vault_exports_its_folder`: a single directory entry `Arbeit/`.
  - `Several_vaults_export_side_by_side`: a second vault `Ideen`; `GET /api/export?vault=Arbeit&vault=Ideen` has both folders; name `Kumbu export <today>.zip` and `filename*` with `Kumbu²`.
  - `Export_is_for_sessions_only`: with an API key of the user (`KeyAsync`, write) both routes answer `403`.
  - `Another_users_vault_is_not_found`: a second user's vault → `404`; `GET /api/export?vault=Arbeit&vault=<theirs>` → `404`, body has no ZIP.
  - `Export_needs_a_vault`: `GET /api/export` → `400`.
- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~ExportApiTests"`. Expected: FAIL (404 for the routes).
- [ ] **Step 3: Implement `ExportApi`.** Check every vault first (`NoteApi.RequireVaultAsync` on its root), so a `404` comes before any byte. Then write a `ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true)` straight to the response (no buffering in memory; ZIP streaming works on a non-seekable stream with data descriptors). Per vault: a directory entry `<vault>/`, then for each note of `store.ListNotesUnderAsync(root)` its `StoredNote` via `GetAsync`, entry `<vault>/<path below root>`, `LastWriteTime = new DateTimeOffset(note.LastWriteAt, TimeSpan.Zero)`, `CompressionLevel.Optimal`. File name via `ContentDispositionHeaderValue` with `FileNameStar` (non-ASCII) and an ASCII `FileName` (`²` dropped). A `503` for a vault being re-keyed comes from the store as for other endpoints; nothing extra here.
- [ ] **Step 4: Run** the same command. Expected: PASS. Then `dotnet test tests/Kumbu.Server.Tests` for the whole suite: PASS.
- [ ] **Step 5: Commit** `Export vaults as ZIP folders of their notes`.

### Task 2: Export in the web app

**Files:**
- Create: `web/src/data/ExportDialog.tsx`, `web/src/data/exportUrl.ts`
- Modify: `web/src/shell/TreeDrawer.tsx` (the `vaultItems` list), `web/src/routes/Settings.tsx` (new `YourDataSection` after `VaultsSection`)
- Test: `web/src/data/ExportDialog.test.tsx`, `web/src/data/exportUrl.test.ts`, `web/src/routes/Settings.test.tsx`

**Interfaces:**
- Consumes: Task 1 routes.
- Produces: `exportUrl(vaults: string[]): string` (`/api/vaults/<enc>/export` for one, `/api/export?vault=…&vault=…` for several, each `encodeURIComponent`); `YourDataSection` in `Settings.tsx`, which Task 4 adds its button to.

- [ ] **Step 1: Write the failing tests:**
  - `exportUrl`: `["Work"]` → `/api/vaults/Work/export`; `["A b", "Ü"]` → `/api/export?vault=A%20b&vault=%C3%9C`.
  - Vault menu has `Export as ZIP`; selecting it starts a download of `exportUrl([activeVault])` (assert an `<a download>` click with that `href`, as a spy on `HTMLAnchorElement.prototype.click`).
  - Settings shows a section `Your data` with `Export my vaults…`; the dialog lists every vault of `useVaults()` with a ticked checkbox and its note count; unticking one leaves it out of the download URL; with none ticked the `Export` button is disabled.
- [ ] **Step 2: Run** `pnpm -C web test -- src/data src/routes/Settings.test.tsx`. Expected: FAIL.
- [ ] **Step 3: Implement.** The download is a same-origin `GET` with the session cookie, so a temporary `<a href download>` is enough (no fetch, no blob, no memory for large vaults). The menu item downloads at once, so its label has no ellipsis (plan's call: an ellipsis promises a dialog). The section's text: one sentence that the ZIP holds a folder per vault that opens in Obsidian, without history or trash.
- [ ] **Step 4: Run** the same command, then `pnpm -C web build`. Expected: PASS, build without errors.
- [ ] **Step 5: Commit** `Offer the vault export in the vault menu and Settings`.

### Task 3: Reading an import ZIP

**Files:**
- Modify: `web/package.json` (add `fflate` `~0.8.2`), lockfile
- Create: `web/src/data/readImport.ts`
- Test: `web/src/data/readImport.test.ts`

**Interfaces:**
- Produces:
  ```ts
  export interface ImportNote { path: string; content: string; version: string } // path below the vault, ".md"
  export interface ImportVault { name: string; notes: ImportNote[]; skipped: Skipped[] }
  export function readImport(zip: Uint8Array, zipName: string): Promise<ImportVault[]>;
  export function versionOf(text: string): Promise<string>; // 12 hex chars of SHA-256 after CRLF → LF
  ```
  `Skipped` from `web/src/upload/planUpload.ts`.

- [ ] **Step 1: Write the failing tests** (ZIPs built in the test with `fflate.zipSync`):
  - `each top-level folder is a vault`: `Work/a.md`, `Work/p/b.md`, `Ideas/c.md` → two vaults, paths `a.md`, `p/b.md`, `c.md`.
  - `a zipped vault without a top folder is one vault named after the ZIP`: `a.md`, `p/b.md` in `My notes.zip` → one vault `My notes`.
  - `.markdown becomes .md as the server stores it`: `Work/x.MARKDOWN` → `x.md`.
  - `unsafe and system entries are skipped`: `Work/../x.md`, `/abs.md`, `Work/a\\b.md`, `__MACOSX/Work/._a.md`, `Work/.DS_Store`, `Work/.obsidian/app.json`, `Work/.trash/t.md` → none imported; the ones inside a vault folder appear in its `skipped` with the server's sentences from `planUpload.ts` where one exists.
  - `entries that aren't notes are never inflated`: an entry `Work/big.png` whose data is not valid deflate; reading still succeeds and lists it as `not a Markdown file (.md or .markdown)`.
  - `a file that isn't UTF-8 is skipped, not mangled`: bytes `0xE4` → skipped `not UTF-8 text`.
  - `a note over 1 MiB is skipped` with the `TOO_LARGE` sentence.
  - `versionOf`: `"a\r\nb"` and `"a\nb"` give the same 12 hex characters, equal to the server's for `"a\nb"` (pin the value: compute once with `VersionToken.Of` in a C# REPL or the existing test data).
- [ ] **Step 2: Run** `pnpm -C web test -- src/data/readImport.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement** with `fflate.unzip(zip, { filter })`: the filter decides by entry name alone (Markdown, not under `.obsidian`/`.trash`/`__MACOSX`, not a dot file, original size ≤ 1 MiB), so other entries are never inflated. Decode with `new TextDecoder("utf-8", { fatal: true })`. The single-vault case applies when any accepted entry sits at the ZIP's root.
- [ ] **Step 4: Run** the same command. Expected: PASS.
- [ ] **Step 5: Commit** `Read import ZIPs in the browser`.

### Task 4: Import flow

**Files:**
- Create: `web/src/data/ImportDialog.tsx`, `web/src/data/runImport.ts`
- Modify: `web/src/routes/Settings.tsx` (`Import vaults…` in `YourDataSection`)
- Test: `web/src/data/runImport.test.ts`, `web/src/data/ImportDialog.test.tsx`

**Interfaces:**
- Consumes: `readImport`, `ImportVault` (Task 3); `toBatches` (`web/src/upload/planUpload.ts`); `GET /api/vaults`, `POST /api/vaults`, `POST /api/upload/{vault}`, `GET /api/vaults/{vault}/versions`.
- Produces:
  ```ts
  export interface VaultPlan { vault: ImportVault; exists: boolean; differing: string[]; unchanged: string[]; fresh: string[] }
  export function planImport(vaults: ImportVault[], existing: { name: string; versions: Map<string, string> | null }[]): VaultPlan[];
  export type Choice = "replace" | "skip";
  export interface ImportSummary { vaults: number; imported: number; replaced: number; unchanged: number; skipped: Skipped[]; failed: { vault: string; reason: string }[] }
  export function runImport(plans: VaultPlan[], choices: Map<string, Choice>, api: ImportApi, onProgress: (done: number, total: number) => void): Promise<ImportSummary>;
  ```
  `ImportApi` is a small interface over the three calls, so the test passes a fake.

- [ ] **Step 1: Write the failing tests:**
  - `planImport`: a new vault → all `fresh`; an existing vault with one equal and one different path → `unchanged` and `differing` split by version.
  - `runImport` creates a missing vault, then uploads in batches of at most 200 files / 5 MB (`toBatches`) with `onConflict: "skip"` for a vault without differing paths; with choice `replace` the batches carry `onConflict: "replace"`; unchanged notes are not sent at all.
  - `a second import of the same export changes nothing`: every path unchanged → no upload call, summary `imported 0`, `unchanged n`, no question asked (dialog test).
  - A failing `POST /api/vaults` (400 with the server's sentence, e.g. an invalid name) puts the vault in `failed` and the others still import.
  - A `409` "changed meanwhile" on a batch retries that batch once, then counts it in `failed`.
  - Dialog: picking a ZIP shows the preview (one row per vault: checkbox, name, note count, "exists: merge or skip" for existing ones); for an existing vault with differing notes, one question with the count, `Replace all` (sentence: the history keeps the old versions) and `Skip all`; a progress bar during the run; the summary sentence from Global Constraints, or the counts when something was replaced, skipped or failed; afterwards `vaults` and the tree queries are invalidated.
- [ ] **Step 2: Run** `pnpm -C web test -- src/data`. Expected: FAIL.
- [ ] **Step 3: Implement.** `versions` are read once per existing ticked vault, before anything is written (paths from the API come as `/Vault/a.md`: strip the vault segment). Uploads go to the vault's root (`folder` empty). The ZIP's bytes are read with `File.arrayBuffer()` once.
- [ ] **Step 4: Run** `pnpm -C web test` and `pnpm -C web build`. Expected: PASS.
- [ ] **Step 5: Manual check** against a local server (`dotnet run --project src/Kumbu.Server`, `pnpm -C web dev`): export two vaults, delete one, import the ZIP: the deleted one comes back, the other reports `✓ no conflicts`; open an exported folder in Obsidian as a vault: notes and folders as in Kumbu², "modified" sort by Kumbu²'s write times.
- [ ] **Step 6: Commit** `Import vaults from an export ZIP`.

### Task 5: Docs and pull request

**Files:**
- Modify: `README.md` (REST API paragraph: the two export routes; a short "Export and import" section: format, what isn't carried, sessions only)

- [ ] **Step 1:** Update `README.md`; the welcome vault's `Your data.md` belongs to the welcome-vault PR, which describes this feature as shipped.
- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Server.Tests`, `pnpm -C web test`, `pnpm -C web build`. Expected: all PASS.
- [ ] **Step 3: Commit** `Document export and import`, push the branch, open the PR "Export and import vaults" stating the merge order (after OAuth and the Obsidian sync server PR). Chris merges.
