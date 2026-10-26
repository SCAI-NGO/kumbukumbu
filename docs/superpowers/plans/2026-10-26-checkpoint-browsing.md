# Checkpoint Browsing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From the Checkpoints sheet, a reader opens a named checkpoint read-only (its tree, its notes rendered, links resolved inside it, a banner with "Back to today"), copies a note's reference for their AI, and exports the checkpoint as a ZIP.

**Architecture:** Two read-only endpoints in `HistoryApi` (tree, one note with its links) and one session-only export in `ExportApi`, each opening the checkpoint once per request through `Checkpoints.OpenAsync` → `CheckpointView` and reusing the tree builder, the link resolver (`NoteLinks.OfAsync` on the `CheckpointView`) and the ZIP writer that exist. The web app gets its own lazy route `/checkpoints/:vault/:name/*` with thin components that only call those GETs: it never mounts `MobileShell`, `NoteView`, the editor or the autosave, so it can't write by construction.

**Tech Stack:** .NET 10, minimal APIs, `System.IO.Compression`, xUnit with `KumbuAppFactory`; React 18, React Router 6, TanStack Query, openapi-fetch, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-26-checkpoint-browsing-design.md` (binding).

## Where and how this runs

- Worktree `/home/chris/workspace/kumbukumbu-checkpoints`, branch `checkpoint-browsing` (off `master` 1e4cd91); one pull request, Chris merges.
- Commands: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test`, `pnpm -C web test`, `pnpm -C web build` (runs `tsc --noEmit`; the web app has no separate lint script; the server's lint is the build itself: `TreatWarningsAsErrors` and the analysers, `dotnet build -c Release`).
- **Regenerating `web/src/api/schema.d.ts`** (generated, never edited by hand) after a REST change, against a server you start yourself:
  ```sh
  Kumbu__Mongo__ConnectionString="${KUMBU_TEST_MONGO:-mongodb://127.0.0.1:27017/?directConnection=true}" \
  Kumbu__Mongo__Database=kumbu-gen Kumbu__Search__Enabled=false ASPNETCORE_URLS=http://127.0.0.1:8099 \
    dotnet run --project src/Kumbu.Server --no-launch-profile > "$HOME/kumbu-gen.log" 2>&1 &
  until curl -fsS http://127.0.0.1:8099/healthz >/dev/null; do sleep 1; done
  KUMBU_OPENAPI_URL=http://127.0.0.1:8099/api/openapi.json pnpm -C web gen:api
  kill %1
  ```
- Every commit message imperative, free-form, ending with:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01QQATz5y8fF72SRR68e5igp
  ```

## Global Constraints

- **Nothing writes.** No change to the write path (`NoteWriter`, `PUT`/`DELETE`/`POST` note endpoints, `useAutosave`, `MarkdownEditor`), the sync, `Checkpoint`, `Checkpoints.MakeAsync/RemoveAsync/restore`, or `INoteStore`. The new endpoints are `GET` only.
- Reading follows vault read rights, as the shell does: any caller that sees the vault (session, read key, write key, connection) may read its checkpoints. No filtering inside a checkpoint (rights are per vault).
- Export is **session only** (`SessionOnlyMetadata`): a key, a connection or the dev token gets `403`.
- Unknown vault, unknown checkpoint, or a note the checkpoint doesn't hold: `404`. A bad path (`..`, empty segment, backslash, not `.md`): `400` (`ApiPaths.Parse`). A vault locked or being re-keyed: as other reading endpoints (`503` via `ApiErrors.Filter`).
- One `Checkpoints.OpenAsync` per request; no cache across requests.
- The view and the export show only what the `CheckpointView` holds, at its paths then; a folder or note checkpoint shows the folders above it and nothing beside it; its export has the vault's folder on top.
- ZIP layout as the normal export: one top-level folder named as the vault, notes verbatim at their paths below it, UTF-8 entry names. File name `<Vault> checkpoint <name> <yyyy-MM-dd>.zip`, the date the checkpoint was made, in the reader's `tz` (UTC without one).
- Copy for your AI, exact form: `From my Kumbu² vault <vault>, checkpoint <name> (<d MMM yyyy>): <path>. Read it with: cat --checkpoint "<name>" "<path>"` — name and path in double quotes, `\` → `\\` and `"` → `\"`.
- UI labels exactly: **Open**, **Export as ZIP**, **Copy for your AI**, **Back to today**.
- British English in code, comments and copy; SPDX header `// SPDX-License-Identifier: AGPL-3.0-or-later` on every new file; the repo's comment density (why, not what).

## Review Focus

1. **A note moved, renamed or removed since the checkpoint**: the view must show it at its path then, and links in the checkpoint must resolve to the checkpoint's notes, never to today's paths. Test in Task 1 (`Links_resolve_inside_the_checkpoint_even_after_a_move`).
2. **An embed (`![[plan]]`) or an ambiguous link inside a checkpoint note**: today's note must never be fetched or linked; an embed stays a link into the checkpoint, the ambiguity dialog lists checkpoint URLs. Test in Task 3 (`never asks for today's notes or links`).
3. **Non-ASCII and spaces in vault, folder and note names** (`My Diary`, `Bücher/Ü².md`): route, API URL, ZIP entry names and the copied command must keep them exactly. Tests in Task 3 (`encodes names in the route and the API`), Task 4 (`escapes quotes and backslashes`) and Task 2 (`A_checkpoint_exports_as_its_vault_folder_of_its_notes_then` uses `Bücher/Ü².md`).
4. **A checkpoint removed elsewhere while open, or a reload of a stale link**: a plain message with **Back to today**, never a blank page or today's vault; a later checkpoint of the same name must not show the old one's cached notes. Test in Task 3 (`a checkpoint that is gone says so`); queries are not `staleTime: Infinity`.
5. **Clipboard unavailable** (plain `http`, permission denied): **Copy for your AI** must say it couldn't copy instead of claiming success. Test in Task 4 (`says so when the clipboard refuses`).

## API contract

```
GET /api/vaults/{vault}/checkpoints/{name}/tree                 any caller that may read the vault
200 CheckpointTreeView { checkpoint: CheckpointEntryView, root: TreeFolder }
400 bad vault   404 vault or checkpoint unknown   503 vault unavailable

GET /api/vaults/{vault}/checkpoints/{name}/notes/{**path}       any caller that may read the vault
200 CheckpointNoteView { path, content, links: LinkView[] }     links resolved against the checkpoint
400 bad path   404 vault, checkpoint or note in it unknown   503

GET /api/vaults/{vault}/checkpoints/{name}/export?tz=…          session only
200 application/zip, Content-Disposition: attachment; filename="Ideas checkpoint before-cleanup 2026-10-19.zip"; filename*=UTF-8''…
403 not a session   404 vault or checkpoint unknown (before any byte)   503
```

---

### Task 1: Read a checkpoint's tree and notes over REST

**Files:**
- Modify: `src/Kumbu.Server/Api/NoteApi.cs` (lift the local `Folder` out of `TreeAsync`; lift the `LinkView` projection out of `LinksAsync`)
- Modify: `src/Kumbu.Server/Api/HistoryApi.cs` (records, two routes, `OpenCheckpointAsync`)
- Modify: `tests/Kumbu.Server.Tests/Api/RestClient.cs` (an `sh` helper over MCP, for folder checkpoints and the shell check)
- Create: `tests/Kumbu.Server.Tests/Api/CheckpointReadApiTests.cs`
- Modify: `tests/Kumbu.Server.Tests/Api/HistoryApiTests.cs` (`The_openapi_document_lists_the_history_trash_and_checkpoint_endpoints`)

**Interfaces:**
- Produces (NoteApi, behaviour of `/tree` and `/links` unchanged):
  - `internal static TreeFolder TreeOf(VaultPath dir, IEnumerable<Note> below)`
  - `internal static async Task<LinkView[]> LinkViewsAsync(INoteReader notes, VaultPath path, string content, CancellationToken ct)`
- Produces (HistoryApi):
  - `public sealed record CheckpointTreeView(CheckpointEntryView Checkpoint, TreeFolder Root);`
  - `public sealed record CheckpointNoteView(string Path, string Content, IReadOnlyList<LinkView> Links);`
  - `internal static async Task<CheckpointView> OpenCheckpointAsync(VaultPath root, string name, INoteStore store, Checkpoints checkpoints, CancellationToken ct)` — `RequireVaultAsync`, then `checkpoints.OpenAsync("cat", root.Segments[0], name, ct)`; no view → `ApiErrors.NotFound($"There is no checkpoint '{name}' in {vault}.")` (the sentence `CheckpointVaultAsync` already uses).
- Produces (tests): `RestClient.ShAsync(string command, string bearer)` → `Task<(bool IsError, string Text)>`, an MCP `sh` call over this client's own factory (`PublicApi`), built as `KumbuAppFactory.ConnectAsync` builds its client (`HttpClientTransport` on `api.Server.BaseAddress + "mcp"`, `api.CreateClient()`, `Authorization: Bearer`).

- [ ] **Step 1: Write the failing tests** in `CheckpointReadApiTests` (`[Collection("app")]`, `app.PublicApi`, `RestClient.NewAsync`; a vault checkpoint via `POST /api/vaults/{vault}/checkpoints {name}`, a folder checkpoint via `ShAsync("mkcp /Arbeit/p/q cp", writeKey)`):
  - `A_vault_checkpoint_opens_as_a_tree_of_its_notes_then`: notes `a.md` (`"then\n"`), `p/b.md`; checkpoint `cp`; afterwards `a.md` rewritten to `"now\n"` and `c.md` created. `GET …/checkpoints/cp/tree`: `checkpoint.name == "cp"`, `checkpoint.scope == "/Arbeit/"`, `checkpoint.notes == 2`; `root.path == "/Arbeit"`, `root.notes` names `["a.md"]`, one folder `p` with `["b.md"]`; no `c.md` anywhere. `GET …/checkpoints/cp/notes/a.md`: `content == "then\n"`, `path == "/Arbeit/a.md"`.
  - `A_folder_checkpoint_shows_the_folders_above_it_and_nothing_beside`: notes `a.md`, `p/x.md`, `p/q/b.md`; folder checkpoint of `/Arbeit/p/q`. Tree: `root.notes` empty, `root.folders` is exactly `p`, `p.notes` empty, `p.folders` exactly `q` with `["b.md"]`; `…/notes/a.md` → `404`.
  - `Links_resolve_inside_the_checkpoint_even_after_a_move`: `a.md` = `"[[b]] [[c]]\n"`, `p/b.md`; checkpoint; then `POST /api/move {from: "/Arbeit/p/b.md", to: "/Arbeit/z/b.md"}` and create `c.md`. `…/notes/a.md` links: `[{kind: "note", path: "/Arbeit/p/b.md", line: 1}, {kind: "not_found", target: "c"}]`.
  - `A_read_only_key_reads_a_checkpoint`: a `KeyRight.Read` key gets `200` for tree and note.
  - `Unknown_vault_checkpoint_or_note_is_not_found`: another user's vault → `404` (tree and note); unknown name `nope` → `404` with `"There is no checkpoint 'nope'"` in the error; `…/notes/missing.md` → `404`; `…/notes/x.txt` → `400` (other bad paths are `ApiPaths.Parse`'s, tested already).
  - `The_copied_shell_command_reads_the_checkpoint`: after the checkpoint and the rewrite above, `ShAsync("cat --checkpoint \"cp\" \"/Arbeit/a.md\"", readKey)` is not an error and its text is `"then\n"` (pins the form Task 4 copies).
  - In `HistoryApiTests.The_openapi_document_lists_…`: add `("get", "/api/vaults/{vault}/checkpoints/{name}/tree")`, `("get", "/api/vaults/{vault}/checkpoints/{name}/notes/{path}")`, and assert each of these two paths has **only** `get` (`paths[path].AsObject().Select(p => p.Key)` equals `["get"]`, ignoring `parameters`).
- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~CheckpointReadApiTests|FullyQualifiedName~HistoryApiTests"`. Expected: FAIL (`404` for the new routes, missing OpenAPI paths).
- [ ] **Step 3: Refactor NoteApi** to `TreeOf` and `LinkViewsAsync` (moved code, same behaviour; `TreeAsync` and `LinksAsync` call them).
- [ ] **Step 4: Implement the two routes** in `MapHistoryApi`, next to the other checkpoint routes:
  - `api.MapGet("/vaults/{vault}/checkpoints/{name}/tree", CheckpointTreeAsync).Errors(400, 401, 404, 503);` — `root = ApiPaths.Parse(vault, null, note: false)`, view via `OpenCheckpointAsync`, `new CheckpointTreeView(View(view.Checkpoint), NoteApi.TreeOf(root, await view.ReadNotesUnderAsync(root, ct)))`.
  - `api.MapGet("/vaults/{vault}/checkpoints/{name}/notes/{**path}", CheckpointNoteAsync).Errors(400, 401, 404, 503);` — `notePath = ApiPaths.Parse(vault, path, note: true)`, one open, `await view.ReadAsync(notePath, ct) ?? throw ApiErrors.NotFound($"Checkpoint '{name}' holds no note at {notePath.Value}.")`, links via `NoteApi.LinkViewsAsync(view, notePath, content, ct)`.
  - No `NoteApi.Refuse`: reading needs no write right. Extend the class summary by one sentence: opening and reading a checkpoint needs only the right to read the vault.
- [ ] **Step 5: Run** the filter from Step 2, then `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~NoteApiTests"` (the refactor). Expected: PASS.
- [ ] **Step 6: Commit** `Read a checkpoint's tree and notes over REST, links resolved inside it`.

### Task 2: Export a checkpoint as ZIP

**Files:**
- Modify: `src/Kumbu.Server/Api/ExportApi.cs`
- Modify: `tests/Kumbu.Server.Tests/Api/ExportApiTests.cs`
- Modify: `web/src/api/schema.d.ts` (regenerated)

**Interfaces:**
- Consumes: `HistoryApi.OpenCheckpointAsync` (Task 1).
- Produces: route `GET /api/vaults/{vault}/checkpoints/{name}/export` in the session-only group; `internal static string CheckpointZipName(string vault, string name, DateTime createdAtUtc, TimeZoneInfo zone)` → `"<vault> checkpoint <name> <yyyy-MM-dd>.zip"`, the date of `createdAtUtc` converted to `zone`.
- Refactor (internal to ExportApi): `WriteAsync` takes `IReadOnlyList<(string Vault, IReadOnlyList<ExportNote> Notes)>` with `private sealed record ExportNote(VaultPath Path, string Content, DateTime WrittenAt)`; the header-and-stream tail of `ExportAsync` becomes `private static IResult Zip(HttpContext http, string name, IReadOnlyList<(string Vault, IReadOnlyList<ExportNote> Notes)> content, TimeZoneInfo zone, CancellationToken ct)`. The vault export maps `StoredNote` → `ExportNote(note.Path, note.Content, note.LastWriteAt)`; its output is byte-for-byte as before.

- [ ] **Step 1: Write the failing tests** in `ExportApiTests` (helpers `ZipAsync`, `Read`, `AssertDated` exist):
  - `A_checkpoint_exports_as_its_vault_folder_of_its_notes_then`: notes `a.md` (`"then\n"`), `Bücher/Ü².md`; checkpoint `cp`; then `a.md` → `"now\n"`, `c.md` created. Entries exactly `["Arbeit/", "Arbeit/Bücher/Ü².md", "Arbeit/a.md"]` (ordinal); `a.md` reads `"then\n"`; `FileNameStar` is `Arbeit checkpoint cp {date}.zip` (`AssertDated`, no `tz`).
  - `A_folder_checkpoint_exports_with_the_vault_folder_on_top`: folder checkpoint of `/Arbeit/p` (via `RestClient.ShAsync`, Task 1) beside `a.md` → entries exactly `["Arbeit/", "Arbeit/p/b.md"]`.
  - `Checkpoint_entries_carry_the_time_it_was_made`: every note entry's DOS time within 2 s of the checkpoint's `createdAt` from `GET …/checkpoints` (reuse `DosTime`/`BytesAsync`).
  - `Checkpoint_export_is_for_sessions_only`: read key, write key and `KumbuAppFactory.Token` → `403`.
  - `Checkpoint_export_of_an_unknown_vault_or_checkpoint_is_not_found`: another user's vault and name `nope` → `404`, `application/json`.
  - `Checkpoint_zip_name_is_dated_in_the_readers_zone`: `CheckpointZipName("Ideas", "before-cleanup", 2026-10-19T23:30Z, Europe/Vienna)` == `"Ideas checkpoint before-cleanup 2026-10-20.zip"`; with `TimeZoneInfo.Utc` → `…2026-10-19.zip`.
- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~ExportApiTests"`. Expected: the new tests FAIL, the old ones PASS.
- [ ] **Step 3: Implement.** Refactor first (`ExportNote`, `Zip`), then the route in `MapExportApi` with `.Produces(StatusCodes.Status200OK, contentType: "application/zip").Errors(400, 401, 403, 404, 503)`: `root = ApiPaths.Parse(vault, null, note: false)`, view via `HistoryApi.OpenCheckpointAsync` (all checks and the read happen before the first byte), notes `view.ReadNotesUnderAsync(root)` ordered by `Path.Value` ordinal, each `WrittenAt = checkpoint.CreatedAt`, name `CheckpointZipName(root.Segments[0], name, checkpoint.CreatedAt, Zone(tz))`. Extend the class summary: a checkpoint exports the same way, its notes as it holds them.
- [ ] **Step 4: Run** the filter of Step 2, then `dotnet test tests/Kumbu.Server.Tests`. Expected: PASS.
- [ ] **Step 5: Regenerate** `web/src/api/schema.d.ts` (command above). Expected: the diff adds the three paths and `CheckpointTreeView`, `CheckpointNoteView`; nothing else changes. `pnpm -C web build` still passes.
- [ ] **Step 6: Commit** `Export a checkpoint as a ZIP that opens as a vault`.

### Task 3: The checkpoint view in the web app

**Files:**
- Modify: `web/src/api/paths.ts`, `web/src/api/history.ts`
- Modify: `web/src/note/MarkdownView.tsx`, `web/src/note/AmbiguousLinkDialog.tsx`
- Modify: `web/src/note/NoteView.tsx` (export `Properties` and `startsWithH1`; no other change)
- Modify: `web/src/shell/TreeDrawer.tsx` (export `rows` and `type Row`; no other change)
- Modify: `web/src/router.tsx`
- Create: `web/src/routes/Checkpoint.tsx` (page: banner, tree, note slot)
- Create: `web/src/note/CheckpointNote.tsx` (one note, read-only)
- Test: `web/src/routes/Checkpoint.test.tsx`

**Interfaces:**
- Consumes: the two GET routes of Task 1 (types from `schema.d.ts`).
- Produces:
  - `paths.ts`: `export const checkpointUrl = (vault: string, name: string, path?: string) => string` → `/checkpoints/<enc vault>/<enc name>` plus `/<each segment of inVault(path) encoded>` when `path` is given.
  - `history.ts`: `export type CheckpointTree = components["schemas"]["CheckpointTreeView"]`, `export type CheckpointNote = components["schemas"]["CheckpointNoteView"]`; `useCheckpointTree(vault: string, name: string)` (key `["checkpoint-tree", vault, name]`) and `useCheckpointNote(vault: string, name: string, path: string)` (key `["checkpoint-note", vault, name, path]`), both `refetchOnWindowFocus: false`, `retry: false`, default `staleTime` (a removed and re-made name must not show cached notes), outside `vaultChanged`'s keys.
  - `MarkdownView` prop `checkpointLink?: (path: string) => string`, carried in `NoteContext`. With it: a `note` link is a `<Link to={checkpointLink(path)}>`; an embed never becomes `EmbeddedNote` (it stays such a link); `not_found` renders a `<span>` with the label and a small chip `not in this checkpoint` (styled like the ambiguous chip), no button, `onCreateMissing` never called; `ambiguous` passes `hrefOf={checkpointLink}` to the dialog.
  - `AmbiguousLinkDialog` prop `hrefOf?: (path: string) => string`, default `noteUrl`.
  - `CheckpointNote({ vault, name, createdAt, path }: { vault: string; name: string; createdAt: string; path: string })`.
  - Route: `{ path: "/checkpoints/:vault/:name/*", lazy: lazyPage(() => import("./routes/Checkpoint")) }` among `RequireSession`'s children, beside `/keys`.

- [ ] **Step 1: Write the failing tests** in `Checkpoint.test.tsx` (`fakeApi`, `renderApp`, `json` from `../test/app`; a vault `Work`, checkpoint `before-cleanup` made `2026-10-19T12:02:00Z`, tree `/Work` → `ideas/xyz.md`, `plan.md`):
  - `shows the checkpoint's tree under a read-only banner`: `renderApp("/checkpoints/Work/before-cleanup")`; a region labelled `Checkpoint` contains `Checkpoint before-cleanup`, `19 Oct 2026` and `read only`, and a button `Back to today`; the tree lists `xyz` and `plan`; no button matching `/Actions for/`, `New note here`, `Read`/`Write` toggle or `Note actions`.
  - `opens a note read-only and keeps links inside the checkpoint`: note `plan.md` content `"# Plan\n\n- [ ] task\n\n[[xyz]] and [[gone]] and [[two]]\n\n![[xyz]]\n"`, links `[{line:5, raw:"[[xyz]]", kind:"note", path:"/Work/ideas/xyz.md"}, {line:5, raw:"[[gone]]", kind:"not_found", target:"gone"}, {line:5, raw:"[[two]]", kind:"ambiguous", candidates:["/Work/a/two.md","/Work/b/two.md"]}, {line:7, raw:"![[xyz]]", kind:"note", path:"/Work/ideas/xyz.md"}]`. Both `xyz` links have `href="/checkpoints/Work/before-cleanup/ideas/xyz.md"`; `gone` shows `not in this checkpoint` and there is no button `Create the note “gone”`; the ambiguous dialog's links point to `/checkpoints/Work/before-cleanup/a/two.md`; the task checkbox is disabled; there is no `textbox`.
  - `never asks for today's notes or links`: after the previous flow, every `fetch` call is a `GET` and none goes to `/api/notes/`, `/api/links/`, `/api/backlinks/`, `/api/version/` or `/api/vaults/Work/tree`.
  - `encodes names in the route and the API`: `renderApp("/checkpoints/My%20Diary/cp/B%C3%BCcher/%C3%9C%C2%B2.md")` requests `/api/vaults/My%20Diary/checkpoints/cp/notes/B%C3%BCcher/%C3%9C%C2%B2.md` and shows the note.
  - `a checkpoint that is gone says so`: tree answers `404 {error: "There is no checkpoint 'before-cleanup' in Work."}` → that text is shown with `Back to today`.
  - `Back to today leaves the checkpoint`: click it → the shell (`getByTestId("shell")`) is shown and the banner is gone.
- [ ] **Step 2: Run** `pnpm -C web test src/routes/Checkpoint.test.tsx`. Expected: FAIL (no route).
- [ ] **Step 3: Implement** `checkpointUrl`, the hooks, the `MarkdownView`/`AmbiguousLinkDialog` props (without the prop both behave exactly as today; `MarkdownView.test.tsx` and `NoteView.test.tsx` stay green), and the route.
- [ ] **Step 4: Implement `routes/Checkpoint.tsx`.** Reads `vault`, `name` and `*` from `useParams`; `path = rest ? pathFromRoute(vault, rest) : null`. Layout `flex h-dvh flex-col bg-bg`:
  - Banner: `<section aria-label="Checkpoint">` at the top, `bg-accent text-on-accent` (the shell uses the accent only for small marks, so the whole bar in it reads as "not today" in every theme), text `Checkpoint {name} · {timeText(createdAt)} · read only` (from the tree's `checkpoint`; while loading, only the name), and a `Back to today` button → `navigate("/")` (the shell then opens the last note). Browser back works by itself; the view has no vault switcher, so leaving it is the only way to another vault.
  - Without `path`: the tree, as a plain list from `rows(tree.root, expanded)` (exported from `TreeDrawer`) with local `expanded` state, every folder open at first; a note row is a `<Link to={checkpointUrl(vault, name, row.path)}>`; no menus. With `path`: a row `All notes in this checkpoint` (link back to `checkpointUrl(vault, name)`) with the crumb, then `CheckpointNote`.
  - Errors: `errorText(error)` and `Back to today`.
- [ ] **Step 5: Implement `note/CheckpointNote.tsx`.** `useCheckpointNote`; `splitFrontmatter(content)`; the title `h1` unless the body starts with one (as `NoteView`); `Properties` for front matter; `MarkdownView` with `links`, `tasksDisabled`, `onToggleTask={noop}`, `onCreateMissing={noop}`, `path`, `checkpointLink={(p) => checkpointUrl(vault, name, p)}`. No outline, no find, no backlinks (they would read today's vault).
- [ ] **Step 6: Run** `pnpm -C web test`. Expected: PASS (all files).
- [ ] **Step 7: Commit** `Open a checkpoint read-only in the web app, links kept inside it`.

### Task 4: Open, Export as ZIP and Copy for your AI

**Files:**
- Create: `web/src/note/copyForAi.ts`, `web/src/note/copyForAi.test.ts`
- Modify: `web/src/note/CheckpointNote.tsx`
- Modify: `web/src/data/exportUrl.ts`, `web/src/data/exportUrl.test.ts`
- Modify: `web/src/shell/CheckpointsSheet.tsx`
- Test: `web/src/routes/Checkpoint.test.tsx`, `web/src/shell/HistorySheets.test.tsx`

**Interfaces:**
- Consumes: `checkpointUrl`, `CheckpointNote` (Task 3); the export route (Task 2); `download`, `browserZone` (`data/exportUrl.ts`).
- Produces:
  - `export function aiPrompt(vault: string, name: string, createdAt: string, path: string): string` — date `new Date(createdAt).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" })`; quoting `"` + `s.replaceAll("\\", "\\\\").replaceAll('"', '\\"')` + `"` for name and path.
  - `export function checkpointExportUrl(vault: string, name: string, tz: string = browserZone()): string` → `/api/vaults/<enc vault>/checkpoints/<enc name>/export?tz=<enc tz>`.

- [ ] **Step 1: Write the failing tests:**
  - `copyForAi.test.ts` `builds the paragraph an AI can act on`: `aiPrompt("Ideas", "before-cleanup", "2026-10-19T12:02:00Z", "/Ideas/concepts/xyz.md")` === `From my Kumbu² vault Ideas, checkpoint before-cleanup (19 Oct 2026): /Ideas/concepts/xyz.md. Read it with: cat --checkpoint "before-cleanup" "/Ideas/concepts/xyz.md"`.
  - `copyForAi.test.ts` `escapes quotes and backslashes`: path `/Ideas/a "b" \c.md` ends the result with `"/Ideas/a \"b\" \\c.md"`.
  - `exportUrl.test.ts`: `checkpointExportUrl("My Diary", "before-cleanup", "Europe/Vienna")` === `/api/vaults/My%20Diary/checkpoints/before-cleanup/export?tz=Europe%2FVienna`.
  - `Checkpoint.test.tsx` `copies the note for your AI`: `navigator.clipboard.writeText` stubbed with `vi.fn().mockResolvedValue(undefined)`; click `Copy for your AI` on `/checkpoints/Work/before-cleanup/plan.md` → called once with `aiPrompt("Work", "before-cleanup", "2026-10-19T12:02:00Z", "/Work/plan.md")`; a `status` reads `Copied. Paste it into your AI's chat.`
  - `Checkpoint.test.tsx` `says so when the clipboard refuses`: `writeText` rejects (and, separately, `navigator.clipboard` undefined) → an `alert` reads `Couldn't copy. Select the text and copy it by hand.`; no `Copied` status.
  - `HistorySheets.test.tsx` `opens a checkpoint and exports it as ZIP`: open the vault menu → `Checkpoints`; `Export before-cleanup as ZIP` → `downloads` (anchor-click capture as in `ExportDialog.test.tsx`) equals `[checkpointExportUrl("Work", "before-cleanup")]` and no non-GET request was sent; `Open before-cleanup` → the sheet and drawer are gone and the `Checkpoint` banner shows (stub `GET /api/vaults/Work/checkpoints/before-cleanup/tree`).
- [ ] **Step 2: Run** `pnpm -C web test src/note/copyForAi.test.ts src/data/exportUrl.test.ts src/routes/Checkpoint.test.tsx src/shell/HistorySheets.test.tsx`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement** `aiPrompt`, `checkpointExportUrl`, and in `CheckpointNote` a `Copy for your AI` button (secondary style, under the title) that awaits `navigator.clipboard.writeText` and shows the status or the alert (a missing `navigator.clipboard` counts as a refusal).
- [ ] **Step 4: Implement the sheet actions.** In each list item of `ListStep`, a first button row `Open` (`aria-label={`Open ${c.name}`}`) and `Export as ZIP` (`aria-label={`Export ${c.name} as ZIP`}`, downloads at once, so no ellipsis), above the existing `Restore…`/`Remove…` row, same `smallButton` style. `Open`: `onClose()`, `useEditor.getState().setOverlay("none")`, `navigate(checkpointUrl(vault, c.name))`. Change the sheet's intro sentence to add: "Open one to read it as it was, or export it as a ZIP."
- [ ] **Step 5: Run** `pnpm -C web test`. Expected: PASS.
- [ ] **Step 6: Commit** `Open and export checkpoints from the sheet, copy a checkpoint note for your AI`.

### Task 5: Welcome text, README and final checks

**Files:**
- Modify: `src/Kumbu.Server/Welcome/Notes/Working by hand.md` (section `## Checkpoints`)
- Modify: `tests/Kumbu.Server.Tests/Welcome/WelcomeTextTests.cs`
- Modify: `README.md` (the web app paragraph, the export paragraph)
- Modify: `web/src/api/schema.d.ts` (regenerated, expected unchanged)

- [ ] **Step 1: Write the failing test** `The_checkpoints_section_names_open_export_and_the_ai`: `Section(Text("Working by hand.md"), "## Checkpoints")` contains `**Open**`, `**Export as ZIP**`, `**Copy for your AI**` and `--checkpoint`.
- [ ] **Step 2: Run** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~WelcomeTextTests"`. Expected: FAIL.
- [ ] **Step 3: Add the sentence** after the existing paragraph of the section, exactly:
  ```markdown
  **Open** shows a checkpoint as it was, read only, and **Export as ZIP** downloads it as a folder that opens in Obsidian. Your AI can read a checkpoint too: **Copy for your AI** on one of its notes copies what it needs, with the command (`cat --checkpoint`).
  ```
  Then README: in the web app paragraph after "remove a vault's named checkpoints", add "open one read-only (its tree and notes as they were, links kept inside it; \"Copy for your AI\" copies a note's reference and the `cat --checkpoint` command) or export it as ZIP"; in the export paragraph, add the checkpoint export (`GET /api/vaults/{vault}/checkpoints/{name}/export`, file `Ideas checkpoint before-cleanup 2026-10-19.zip`, the day it was made in the reader's zone, same layout, sessions only).
- [ ] **Step 4: Run** `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~WelcomeTextTests"`. Expected: PASS (including `The_menu_names_quoted_are_the_shared_labels`).
- [ ] **Step 5: Full checks.** `dotnet build -c Release` (no warnings), `dotnet test` (whole solution), `pnpm -C web test`, `pnpm -C web build`. Expected: all green. Regenerate `schema.d.ts` (command above); expected no diff (commit it if there is one).
- [ ] **Step 6: Try it once by hand** on a running stack (`pnpm -C web dev` against the server): make a checkpoint, change and move a note, open the checkpoint on a phone-width window, follow a link, copy for your AI and paste the command into an AI's shell, export and open the ZIP in Obsidian. Fix and rerun what fails.
- [ ] **Step 7: Commit** `Tell the welcome vault and the README about opening and exporting checkpoints`.
