# Command palette and favourites Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Ctrl+P palette in the web app that jumps to commands, vaults and notes by name, with favourites on top that follow the person between devices.

**Architecture:** The server keeps favourites on the user document in two parts: command ids in the clear, vault-bound entries sealed under a key derived from the user key (as `calendarViews`), served through a session-only `GET`/`PUT /api/account/favourites` that only ever shows and replaces the entries of vaults the session lists. The web app builds the palette's rows on the client from routes, the vault list, the trees and the favourites, filters them with a pure function, and renders them in one component opened from Ctrl+P and a star in the top bar.

**Tech Stack:** C# .NET 10, ASP.NET minimal APIs, MongoDB, xUnit; React 19, TypeScript, Tailwind, Zustand, TanStack Query, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-18-command-palette-design.md` (read it first; this plan argues from it).

## Global Constraints

- British English in code, comments, docs and UI copy; `// SPDX-License-Identifier: AGPL-3.0-or-later` at the top of every new source file.
- The pinning feature is called **Favourites** everywhere (never "pins": that word names another topic).
- Copy fixed by the spec: tooltip and accessible name "Favourites and commands (Ctrl+P)" (`(⌘P)` on a Mac); the search row "Search notes for ‘…’"; the empty hint "Star anything here to keep it on top."; note menu "Add to favourites" / "Remove from favourites".
- Shortcut: Ctrl+P, Cmd+P on a Mac only (Ctrl+P stays CodeMirror's there); never over a dialog (`topDialog()`, `web/src/hooks/useDialog.ts`); Ctrl+D (Cmd+D) toggles a favourite only while the palette is open.
- At most 50 note rows; 200 favourites in all; Recent shows 5 (`recentPaths`).
- No semantic search, no attachments, no notes under `_archive/`, no reordering of favourites.
- A hidden discreet vault leaves no row, no favourite, no hint and no count anywhere, server or web.
- No vault name or note path in the clear on the user document.
- Test commands: `dotnet test --solution Kumbu.slnx`; `pnpm -C web test --run`; `pnpm -C web build`.

## Review Focus

1. A note favourite whose note is gone or renamed outside Kumbu² (WebDAV, Obsidian sync): the row is not shown, the palette doesn't error, and the next write drops it (Task 5).
2. Two tabs or devices starring at once: neither loses the other's favourite (conditional write, Task 1 test `Concurrent_writes_keep_both`).
3. A favourite in a vault whose access was taken away (a shared vault left): not shown, kept out of the answer like a hidden vault (Task 1 test `A_vault_no_longer_listed_is_left_out`).
4. Ctrl+P pressed while the palette's own field, the editor, or a menu has focus, and Esc returning focus to where it was (Task 4 tests).
5. Typing fast in a large vault (thousands of notes): filtering stays synchronous and capped; no request per keystroke (Task 3 test `caps_notes_at_50` and no fetch in the filter).

---

### Task 1: Favourites on the server

**Files:**
- Create: `src/Kumbu.Server/Accounts/Favourites.cs`
- Create: `src/Kumbu.Server/Api/FavouritesApi.cs`
- Modify: `src/Kumbu.Server/Accounts/UserDocuments.cs` (beside `CalendarViews`, line ~165)
- Modify: `src/Kumbu.Server/Program.cs:113` (register) and `:327` (map)
- Test: `tests/Kumbu.Server.Tests/Accounts/FavouritesTests.cs` (pattern: `tests/Kumbu.Server.Tests/Calendar/CalendarSettingsTests.cs`; discreet helpers in `tests/Kumbu.Server.Tests/Discreet/`)

**Interfaces:**
- Produces, over HTTP (session only, `SessionOnlyMetadata`, a key or connection gets 403 "Only a signed-in person keeps favourites."):
  - `GET /api/account/favourites` → `200 { favourites: Favourite[] }`, ordered by `added` ascending.
  - `PUT /api/account/favourites` body `{ favourites: FavouriteInput[] }` → `200 { favourites: Favourite[] }` (the visible list after the write); `400` invalid, `409` after three conflicting attempts.
  - `FavouriteInput = { kind: "command", id: string } | { kind: "vault" | "board" | "calendar" | "gallery", vault: string } | { kind: "note", vault: string, path: string }` (`path` inside the vault, e.g. `projects/plan.md`, `vault` by name).
  - `Favourite = FavouriteInput & { added: string }` (ISO 8601).
- Stored: `UserDocument.Favourites` (`string[]?`, entries `"<id>@<added ticks>"` or a small record list, the builder's choice, plain) and `UserDocument.FavouritesSealed` (`byte[]?`, sealed JSON of `{ kind, vaultId, path?, added }[]`), label `kumbu/v1/favourites`, context `favourites:{userId}`.

- [ ] **Step 1: Write the failing tests** in `FavouritesTests`, each through the REST client as a signed-in person:
  - `Starts_empty`: GET → `favourites` is `[]`.
  - `Round_trips_in_the_order_added`: PUT command `settings.appearance`, then PUT the same plus note `Work`/`plan.md` → GET answers both, command first, each with `added`.
  - `A_kept_entry_keeps_its_added`: PUT A; PUT A, B → A's `added` unchanged.
  - `Vault_entries_are_sealed`: after PUT note `Work`/`plan.md` and board `Work`, the raw user document contains neither the vault id, `"Work"` nor `"plan.md"` in any field; the command id is in the clear.
  - `Commands_survive_a_key_rotation_vault_entries_do_not`: after rotating the user key (as `DiscreetRotationTests` does), GET answers the command only.
  - `A_hidden_discreet_vault_leaves_no_trace`: favourites in `Work` and in discreet `Secret`, then hide → GET answers only `Work`'s; PUT a new command while hidden; reveal → GET answers all three, `Secret`'s with its old `added`.
  - `A_vault_no_longer_listed_is_left_out`: a favourite in a shared vault, then the share ends → GET doesn't answer it.
  - `Refuses_what_it_cannot_take` (theory): a vault the session doesn't list or hides → 400 "There is no vault 'X'." (the same sentence for both); a bad path (`../x.md`, a non-`.md` path) → 400; an unknown `kind` → 400; a command id not matching `^[a-z][a-z0-9-]*(\.[a-z0-9-]+){0,3}$` or longer than 64 → 400; more than 200 entries in all, hidden ones counted → 400 "Your favourites are full (200)." with no other count.
  - `Keys_and_connections_get_403`: GET and PUT with an API key → 403.
  - `Concurrent_writes_keep_both`: with `BeforeWrite` set (as `CalendarSettings.BeforeWrite`), a second write lands between read and write → after the retry both changes are present.

- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter FullyQualifiedName~FavouritesTests` → FAIL (404 on the route).

- [ ] **Step 3: Implement `Favourites` in `Accounts/Favourites.cs`**, modelled on `CalendarSettings` (`src/Kumbu.Server/Accounts/CalendarSettings.cs:108–161`): `Task<IReadOnlyList<StoredFavourite>> ReadAsync(string userId, byte[] userKey, CancellationToken ct)` and `Task<IReadOnlyList<StoredFavourite>> ReplaceVisibleAsync(string userId, byte[] userKey, IReadOnlySet<string> listedVaultIds, IReadOnlyList<StoredFavourite> visible, CancellationToken ct)`, with `record StoredFavourite(string Kind, string? CommandId, string? VaultId, string? Path, DateTimeOffset Added)`. `ReplaceVisibleAsync` opens the sealed part (a list that doesn't open counts as empty and is logged as `OpenViews` does), keeps every entry whose `VaultId` is not in `listedVaultIds` unchanged, replaces the rest with `visible`, keeps `Added` of entries matched by (kind, command id or vault id + path) and stamps new ones with `TimeProvider` now, enforces the cap over the whole, and writes conditionally on `KeyId` and both stored fields (three attempts, then `FavouritesConflictException`). It never drops an entry as stale.

- [ ] **Step 4: Implement `FavouritesApi.MapFavouritesApi`** in `Api/FavouritesApi.cs` as `CalendarApi.MapCalendarApi`'s account group (`src/Kumbu.Server/Api/CalendarApi.cs:107–109`): map vault names to ids through `caller.OpenVaultsAsync(ct)` (the session's list, discreet filter included, as `VaultIdAsync` at `CalendarApi.cs:513`), validate a note path with `ApiPaths.Parse(vault, path, note: true)` (`src/Kumbu.Server/Api/NoteApi.cs:1047`), refuse paths under `_archive/`, and answer only entries whose vault id is listed, translated back to names. Register `Favourites` as a singleton and map the API in `Program.cs`.

- [ ] **Step 5: Run** the filter again → PASS; then `dotnet test --solution Kumbu.slnx` → all green (4 skips without the model).

- [ ] **Step 6: Document.** README: the endpoints in the REST API section, one paragraph. `docs/security-architecture.md`: a paragraph after the calendar's (line 301) on favourites: the two parts, the rule (what names no vault may be plain), the merge that keeps hidden vaults' entries, the cap message without counts, session-only.

- [ ] **Step 7: Commit** `git commit -m "Keep a person's favourites, sealed where they name a vault"`.

### Task 2: Favourites in the web app's API layer

**Files:**
- Modify: `web/src/api/schema.d.ts` (`pnpm -C web gen:api` against a server from Task 1 on port 8083; where no server can run, add the two paths and schemas by hand, matching the OpenAPI the server emits)
- Create: `web/src/api/favourites.ts`
- Test: `web/src/api/favourites.test.ts`

**Interfaces:**
- Consumes: Task 1's endpoints.
- Produces:
  - `type Favourite = components["schemas"]["Favourite"]`, `type FavouriteInput`.
  - `favouriteKey(f: FavouriteInput): string` — `command:<id>`, `<kind>:<vault>`, `note:<vault>/<path>`.
  - `useFavourites(): { list: Favourite[]; has(f: FavouriteInput): boolean; toggle(f: FavouriteInput): void; drop(keys: string[]): void }` — query key `["favourites"]`; `toggle` and `drop` PUT the whole visible list (optimistic, rolled back on error, invalidated on 409); queries the vault list's discreet changes invalidate it (hook into `followVaults` in `web/src/discreet/state.ts` or invalidate `["favourites"]` where `["vaults"]` is).

- [ ] **Step 1: Write the failing tests:** `toggle adds and removes by key`, `PUT carries the whole visible list without added`, `rolls back on a failed write`, `refetches when the vaults change` (using the `api()` fetch mock pattern of `web/src/routes/Skills.test.tsx`).
- [ ] **Step 2: Run** `pnpm -C web test --run src/api/favourites` → FAIL.
- [ ] **Step 3: Implement** the schema entries and `favourites.ts`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `git commit -m "Read and write favourites from the web app"`.

### Task 3: The palette's rows and filter

**Files:**
- Create: `web/src/palette/rows.ts`, `web/src/palette/filter.ts`
- Test: `web/src/palette/rows.test.ts`, `web/src/palette/filter.test.ts`

**Interfaces:**
- Consumes: `VaultView` and `TreeFolder` (`web/src/api/notes.ts`), `schemes` (`web/src/theme/schemes.ts:49`), Task 2's `FavouriteInput`, `favouriteKey`.
- Produces:
  - `type Row = { key: string; group: "Commands" | "Vaults" | "Notes"; name: string; place: string; favourite: FavouriteInput; run: { to: string } | { scheme: string } }`.
  - `commandRows(opts: { admin: boolean }): Row[]` — ids and labels from the settings nav (`web/src/routes/settings/SettingsWindow.tsx:8–15`), the Access and Skills tabs (`web/src/routes/Access.tsx`, `web/src/routes/Skills.tsx`), Admin Users and Invitations (admins only), Open: Recent, What's on, Console, and "Colour scheme: <name>" per scheme (id `scheme.<id>`, runs as Appearance's `choose`, `web/src/routes/settings/Appearance.tsx:33`). Ids dotted, e.g. `settings.appearance`, `settings.access.keys`, `admin.users`, `open.recent`.
  - `vaultRows(vaults: VaultView[]): Row[]` — per vault: the vault, "Board", "Calendar", "Gallery" (place = vault name).
  - `noteRows(vault: string, root: TreeFolder): Row[]` — notes only (`fileKindOfEntry === "note"`), none under `_archive/`; name = title without `.md`, place = `Vault / folder / sub`.
  - `filterRows(rows: Row[], query: string, favourites: ReadonlySet<string>): Row[]` — words split on whitespace, each must occur case-insensitively in `place + " " + name`; order: favourites first, then Commands, Vaults, Notes; within a group word-start matches before others, then `localeCompare` by name; at most 50 Notes rows.

- [ ] **Step 1: Write the failing tests:** `"set vau" finds Settings: Vaults`, `"plan work" finds /Work/plan.md`, `case-insensitive`, `every word must match`, `favourites before commands before vaults before notes`, `word-start matches first, then alphabetical`, `caps_notes_at_50` (1,000 notes, query `"n"`, exactly 50 Notes rows), `no archived notes and no attachments`, `admin rows only for admins`, `one colour-scheme row per scheme`.
- [ ] **Step 2: Run** `pnpm -C web test --run src/palette` → FAIL.
- [ ] **Step 3: Implement** both modules, pure, no React.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `git commit -m "Build and filter the palette's rows"`.

### Task 4: The palette

**Files:**
- Create: `web/src/palette/Palette.tsx`, `web/src/palette/usePaletteShortcut.ts`
- Modify: `web/src/state/editor.ts:8` (`Overlay` gains `"palette"`), `web/src/shell/TopBar.tsx` (the star button), `web/src/shell/MobileShell.tsx` (render the palette lazily as `SearchSheet` is, line 67–68/470; install the shortcut)
- Test: `web/src/palette/Palette.test.tsx`

**Interfaces:**
- Consumes: Tasks 2 and 3; `useVaults` and `useTree` (`web/src/api/notes.ts:142,159`, the trees of every listed vault, cached by TanStack Query); `useEditor` (`recentPaths`, `setSearchQuery`, `setOverlay`); `topDialog`; `useSession((s) => s.user?.role)` for admin.
- Produces: `useEditor.getState().setOverlay("palette")` opens it.

- [ ] **Step 1: Write the failing tests:**
  - `Ctrl+P opens it, also from the editor`; `Cmd+P opens it on a Mac and Ctrl+P does not`; `not over a dialog`; `Ctrl+P again closes it`; `Esc closes and returns focus`.
  - `the star in the top bar opens it` (accessible name "Favourites and commands (Ctrl+P)").
  - `empty: favourites, then Recent (5)`; `without favourites: "Star anything here to keep it on top."`.
  - `Enter on a row navigates`; `arrow keys move the highlight`.
  - `Ctrl+D toggles the highlighted row's favourite`; `the row's star toggles it`.
  - `the last row is "Search notes for ‘plan’" and hands over the text` (search overlay open with `searchQuery` `plan`).
  - `a hidden discreet vault leaves no row` (its notes and vault rows absent after the hide).
- [ ] **Step 2: Run** `pnpm -C web test --run src/palette/Palette` → FAIL.
- [ ] **Step 3: Implement.** Desktop: a centred panel near the top, field above the list; phone (the shell's existing breakpoint): a sheet from below with the field at the bottom, as `SearchSheet`. A `role="combobox"` field with a `listbox`; the place muted on the right (below the name on the phone); the star at the row's end, on hover/focus/set on the desktop, always on the phone. The shortcut listens on `document` in the capture phase with `preventDefault` (as `MobileShell.tsx:266–277` does for Ctrl+\).
- [ ] **Step 4: Run** → PASS; `pnpm -C web build` → OK.
- [ ] **Step 5: Commit** `git commit -m "Open a command palette with Ctrl+P and the star in the top bar"`.

### Task 5: Favourites from the note menu, and stale entries

**Files:**
- Modify: `web/src/shell/MobileShell.tsx:441–455` (`noteActions`)
- Modify: `web/src/palette/Palette.tsx` (drop stale)
- Test: `web/src/palette/Palette.test.tsx`, `web/src/shell/MobileShell` tests where `noteActions` are tested

**Interfaces:**
- Consumes: Task 2's `useFavourites`, Task 3's rows.

- [ ] **Step 1: Write the failing tests:** `the note menu offers "Add to favourites" and then "Remove from favourites"`; `a note favourite whose note isn't in its vault's tree is not shown`; `the next write drops it` (the PUT body lacks it); `a favourite of a vault whose tree hasn't loaded is kept` (never dropped for want of data).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement:** the menu entry with lucide `Star`; in the palette, a note favourite is shown only when its note is in the loaded tree; on the next `toggle`, `drop` the keys of note favourites whose vault's tree is loaded and lacks the note.
- [ ] **Step 4: Run** `pnpm -C web test --run` → all PASS; `pnpm -C web build` → OK.
- [ ] **Step 5: Commit** `git commit -m "Star a note from its menu, and let go of favourites whose note is gone"`.
