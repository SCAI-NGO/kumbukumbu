# Obsidian Sync — Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The Kumbu² server and web app give the Obsidian plugin (`SCAI-NGO/kumbu-obsidian`) everything it syncs against: device keys from a pairing code, a version list that can answer "what changed since", a version check on delete, WebSocket hints, and a web app that tells a human's Obsidian writes from an agent's.

**Architecture:** Every committed change to a vault already passes through one place, `EncryptedNoteStore.WriteAsync`; it now records the commit time per path on the unlocked vault and raises a per-vault event. The versions endpoint answers from that in-memory record (or with the full list when its record doesn't reach back far enough), and a singleton WebSocket hub turns the event into hints for the connections whose key has the vault. Pairing codes live in process memory with a copy of the session's user key until redeemed. Key kinds (`agent`, `device`) are sealed in the API key's metadata and surface as a writer kind in the session's views.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs and `UseWebSockets`, MongoDB.Driver 3.x, xUnit v3 with Testcontainers (`KumbuAppFactory`, `RestClient`); React 18 + Vite + TanStack Query + openapi-fetch, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-26-obsidian-sync-design.md` (binding). The plugin's own plan is written separately and consumes the **API contract** below.

## Where and how this runs

- **Start only after the OAuth branch `step-10-oauth` is merged into `master`.** It changes `ApiKeys` (names through `WriterNames`, secrets through `Secrets`, user key rotation), adds `CallerKind.Connection`, `Connections`, `VaultBusyException` (503 for a vault being re-keyed) and `UnlockedVault.Pending`. This plan is written against that code; symbols are named rather than line numbers where the merge moves them.
- Work on one branch, `obsidian-sync`, from `master`; end with one pull request. Toolchain and MongoDB for the tests: as in `docs/superpowers/plans/2026-10-26-oauth-connectors-and-rekeying.md`, "Where and how this runs".
- Commands: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~<Class>"`, `pnpm -C web test`, `pnpm -C web build`. Regenerate the web app's API types after every server contract change: run the server (`dotnet run --project src/Kumbu.Server` with a local MongoDB, port 8083) and `pnpm -C web gen:api`.
- Commit messages: imperative, free-form (`Add the versions endpoint`), each ending with the session's attribution lines.

## Global Constraints

- Paths in every answer are vault-tree paths, `/Work/plan.md`, as `NoteView.path` already is.
- Versions are `VersionToken.Of(content)`: first 12 hex characters of SHA-256 over the UTF-8 text, after CRLF → LF.
- `since` overlap: **5 minutes**. Full answer when the cursor is older than **25 days**, older than the vault's in-memory horizon, unparsable, or in the future.
- WebSocket ticket: valid **30 seconds**, **one** connection, API keys only. Server ping every **30 s** (WebSocket keep-alive). Hints debounced to at most **one per vault per 500 ms** per connection. A connection whose key is revoked or expired is closed at the next 30-second check.
- Pairing code: **8 characters of Crockford Base32** (alphabet `0123456789ABCDEFGHJKMNPQRSTVWXYZ`: no I, L, O, U), 40 random bits, shown `XXXX-XXXX`; entry ignores case, spaces and dashes and reads I/L as 1 and O as 0. Valid **10 minutes**, **once**. A new code for the same user and vault replaces the open one.
- Pairing creates a key named **`<Vault> – Obsidian <device>`** (en dash, U+2013), with ` 2`, ` 3`, … when the name is taken; **write** on that one vault only; kind **device**; expiry as chosen: **never (default)**, 30, 90 or 365 days (`ApiKeys.ExpiryDays`).
- Pairing records live **in process memory only**, never in MongoDB or a log; the user key copy is zeroed when the record is dropped.
- Redeeming is anonymous, exempt from the Origin check, rate-limited: **10 attempts per minute per address**, **100 failed attempts per minute server-wide**; the answer to a wrong code says only `This pairing code is not valid (any more).`
- Writes from the plugin are stored verbatim (only CRLF → LF, as today); nothing in this plan reformats note text.
- British English in code, comments and docs; the repo's comment density (non-obvious reasons only).
- Web copy (exact): dialog title `Connect "<vault>" with Obsidian`; hint `Works with an empty Obsidian vault or one that already has notes.`; device bar `You changed this note in Obsidian (<device>) <ago>`; key label `Obsidian device`.

## Review Focus

1. **A cursor the server can't honour** (server restarted, vault locked and unlocked, vault re-keyed) must give `full: true`, never a short list that silently misses changes. Test in Task 4 (`Since_before_the_horizon_answers_the_full_list`).
2. **A vault being re-keyed** must answer `503` with `Retry-After` on every sync endpoint, never `404`, which the plugin would read as "the vault is gone". Test in Task 4 (`A_pending_vault_answers_503_not_404`).
3. **Two redeems of one code at the same time** must create exactly one key. Test in Task 7 (`A_code_redeemed_twice_at_once_makes_one_key`).
4. **A bad device name** (empty, too long, control characters, `" ("`) is a `400` with a sentence, and the code stays usable for a corrected retry. Test in Task 7 (`A_bad_device_name_keeps_the_code`).
5. **A key revoked, a vault deleted or re-keyed while a WebSocket is open:** the socket closes (revoked) or the next versions call answers `404`/`503`; no hint ever names a vault the key can't open. Tests in Task 6 (`A_revoked_key_is_disconnected`, `Hints_never_name_another_vault`).

## API contract (for the plugin plan)

All under the server's origin. Errors are `{"error": "<sentence>"}`. Every sync endpoint may answer `503` with `Retry-After` while a vault is re-keyed (retry later, don't treat as gone), `401` for a revoked, expired or unknown key (the plugin says "pair again"), and `429` with `Retry-After` under the wrong-key limit.

**Pairing (anonymous)**

```
POST /api/pairing/redeem
{ "code": "k7qm-4xda", "device": "Laptop" }
201 { "key": "kumbu_…43 chars…", "vault": "Work", "keyName": "Work – Obsidian Laptop" }
400 { "error": "…device name…" }            code stays valid
404 { "error": "This pairing code is not valid (any more)." }
429 Retry-After
```
`device`: 1 to 32 characters after trimming, no control or invisible formatting characters, no `" ("`.

**Vaults (API key)**

```
GET /api/vaults
200 [ { "name": "Work", "notes": 128, "embeddings": "internal", "pending": false, "canWrite": true } ]
```

**Version list (API key or session)**

```
GET /api/vaults/{vault}/versions                       full list
GET /api/vaults/{vault}/versions?since=<cursor>        changes since cursor minus 5 minutes
If-None-Match: "<state>"                               only without since → 304 when unchanged
200 ETag: "<state>"
{
  "full": true,                     false only for an answer to since
  "cursor": "2026-10-27T10:15:03.1234567Z",   opaque; send it back as since
  "state": "3f2a9c0d1e4b5a67",      hash over the whole vault's paths, versions and damaged count
  "damaged": 0,                     stored notes that don't open (paths unknown)
  "notes":   [ { "path": "/Work/plan.md", "version": "a1b2c3d4e5f6" } ],
  "deleted": [ "/Work/old.md" ]     only when full is false: touched paths with no note now
}
404 vault unknown to the key    503 vault being re-keyed
```
In a `since` answer, `notes` and `deleted` hold the *current* state of every path touched since `cursor − 5 min` (a write, a move's source and target, a trash, a restore); a path touched twice appears once. Order: path, text order.

**Notes (existing, unchanged unless noted)**

```
GET    /api/notes/{vault}/{path}            200 NoteView { path, content, version, lastWriter, lastWriteAt, current, lastWriterKind }
PUT    /api/notes/{vault}/{path}            { "content": "…", "version": "<base>" | omitted to create }
                                            201 created · 200 replaced · 409 ConflictView { error, current: { content, version } } · 413 > 1 MiB · 400 path rules
DELETE /api/notes/{vault}/{path}?version=<base>   NEW query parameter
                                            204 trashed · 409 ConflictView (changed since) · 404 gone
POST   /api/move  { "from": "/Work/a.md", "to": "/Work/b.md", "keepLinks": true }
                                            200 MovedView { message, path, version } · 409 target exists or changed
```
`lastWriterKind` is `null` for API key callers (only sessions can see the user's keys); see below.

**WebSocket (API key)**

```
POST /api/ws-ticket                   API keys only (sessions, the dev token and connections: 403)
200 { "ticket": "kumbu_wst_…43 chars…", "expiresAt": "…" }
GET  /api/ws?ticket=<ticket>          WebSocket upgrade; 401 for an unknown, used or expired ticket; 400 without an upgrade
server → client  {"type":"changed","vault":"Work"}
close 4401 "key revoked or expired"   close 1001 on shutdown
```
Messages from the client are ignored. Keep-alive pings every 30 s.

**Writer kinds (sessions only)** — `lastWriterKind` on `NoteView` and `NoteVersionView`, `writerKind` on `HistoryEntryView`: `"user"` (the user themself), `"device"` (a device key), `"agent"` (an agent key or a connection), `"server"` (`dev`, `seed`), or `null` (unknown name). `kind` on `ApiKeyView`: `"agent"` or `"device"`. `GET /api/changes` lists agent writes only.

**Pairing (session, web app only)**

```
POST /api/vaults/{vault}/pairing  { "expiresInDays": null | 30 | 90 | 365 }
201 { "code": "K7QM-4XDA", "expiresAt": "…" }   404 not the user's vault · 400 expiry · 503 pending
```

## Findings: how each change path is timed today

Checked in the code for the `since` query (spec §5, "every way a note changes must leave a time the query finds"):

| Change | Where | Time it leaves |
|---|---|---|
| write, edit, replace, restore from history | `NoteWriter.Next` | `LastWriteAt = now` |
| `mv` of a note whose text doesn't change | `NoteMover` (`current with { Path = to, Revision = … }`) | **keeps the old `LastWriteAt`**; only the `MoveRecord.MovedAt` (inserted in `EncryptedNoteStore.TryCommitAsync`) has the time |
| `mv` with link rewrites | `NoteMover` → `NoteWriter.Next` | `now` on rewritten notes |
| `rm` | `NoteWriter.RemoveAsync` → `TryTrashAsync` | trash `DeletedAt` |
| restore from trash | `EncryptedNoteStore.TryRestoreFromTrashAsync` | `LastWriteAt = at` |
| checkpoint restore | `Checkpoints` (`Next`, new notes with `at`; moved notes like `mv`, keep their time; trashes) | mixed, as above |
| upload | `UploadApi` batch | `now` |
| seeding | `Seeder` (`TryCreateAsync`) | `now` |
| vault deletion | `VaultCatalog.DeleteVaultsAsync` | none per note: the vault is gone |
| a document repaired outside Kumbu² | `VaultCatalog.RefreshAsync` | none |

Persisted times would miss the plain `mv` unless moves are joined in, and every future write path would have to remember. So this plan does **not** query persisted times: `EncryptedNoteStore.WriteAsync`, which every one of the paths above (except vault deletion and `RefreshAsync`) commits through, records `catalog.Now` after the commit for every path in its `paths` argument. That record lives on the `UnlockedVault`, starting at the moment the object was created (its *horizon*); a cursor older than that gets the full list. Vault deletion raises the event without a record (the next call is a 404). `RefreshAsync` is not a change made by Kumbu² and is caught by the plugin's hourly full list.

---

### Task 1: Key kinds sealed in the API key

**Files:**
- Modify: `src/Kumbu.Server/Accounts/ApiKeys.cs` (`ApiKeyMeta`, `ApiKeyInfo`, `CreateAsync`, `Info`)
- Modify: `src/Kumbu.Server/Api/AccountApi.cs` (`ApiKeyView`, `KeyView`)
- Test: `tests/Kumbu.Server.Tests/Accounts/ApiKeyTests.cs`, `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs`

**Interfaces:**
- Produces: `public enum ApiKeyKind { Agent, Device }`; `ApiKeyInfo(..., ApiKeyKind Kind = ApiKeyKind.Agent)`; `ApiKeys.CreateAsync(User user, byte[] userKey, DateTime credentialReadAt, string name, IReadOnlyDictionary<string, KeyRight> vaults, CancellationToken ct, int? expiresInDays = null, ApiKeyKind kind = ApiKeyKind.Agent)`; `ApiKeyView.Kind` (`"agent"` | `"device"`).

- [ ] **Step 1: Write the failing tests** in `ApiKeyTests`:
  - `A_device_key_is_listed_as_a_device`: create with `kind: ApiKeyKind.Device`; `ListAsync` returns it with `Kind == ApiKeyKind.Device`.
  - `A_key_made_before_kinds_is_an_agent`: insert a key whose sealed meta has no `kind` (create one, then reseal its `Meta` with the old two-field JSON through the test's access to the collection, as existing tests do for damaged metas) → `Kind == ApiKeyKind.Agent`.
  - In `UserKeyRotationTests`: `A_rotation_keeps_a_keys_kind`: a device key is still `Device` after a rotation.
- [ ] **Step 2: Run them**, expect FAIL (no `ApiKeyKind`). `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~ApiKeyTests|FullyQualifiedName~UserKeyRotationTests"`
- [ ] **Step 3: Implement.** `ApiKeyMeta` gets `[property: JsonPropertyName("kind")] string? Kind = null`, written only for `device` (an agent key's meta stays byte-for-byte what it is today). `PrepareResealAsync` reseals the plaintext as it is, so the kind survives without change; check that. `ApiKeyView` gains `string Kind`; `GET /api/keys` and `POST /api/keys` answer it. `POST /api/keys` keeps creating agent keys (no request field; the keys page offers no choice, spec §5a leaves it to the builder).
- [ ] **Step 4: Run the tests**, expect PASS; run all of `Accounts` too.
- [ ] **Step 5: Commit** `Seal a kind, agent or device, into every API key`.

### Task 2: Writer kinds in the session's views, agent writes only in `/api/changes`

**Files:**
- Create: `src/Kumbu.Server/Api/WriterKinds.cs`
- Modify: `src/Kumbu.Server/Api/NoteApi.cs` (`NoteView`, `ReadAsync`, `WriteAsync` answers, `HistoryEntryView`, `HistoryAsync`, `ChangesAsync`), `src/Kumbu.Server/Api/HistoryApi.cs` (`NoteVersionView`, `VersionAsync`), `src/Kumbu.Server/Program.cs` (register scoped)
- Test: `tests/Kumbu.Server.Tests/Api/WriterKindTests.cs`

**Interfaces:**
- Consumes: `ApiKeys.ListAsync` (Task 1, with `Kind`), `Connections.ListAsync(User, byte[] userKey, CancellationToken)`.
- Produces: `public sealed class WriterKinds(RequestCaller caller, ApiKeys keys, Connections connections)` with `Task<string?> OfAsync(string writerLabel, CancellationToken ct)` (memoised per request; `null` for every non-session caller); `NoteView.LastWriterKind`, `NoteVersionView.LastWriterKind`, `HistoryEntryView.WriterKind` (all `string?`, last positional parameter, defaulting to `null`).

- [ ] **Step 1: Write the failing tests** in `WriterKindTests` (`[Collection("app")]`, `RestClient`):
  - `A_device_write_is_a_device_for_the_session`: a device key (created through `ApiKeys.CreateAsync(..., kind: Device)`) writes a note; the session's `GET /api/notes/…` has `lastWriterKind == "device"`, `GET /api/version/…` too, and `GET /api/history/…` has `writerKind == "device"` on the current entry.
  - `An_agent_key_and_a_connection_are_agents`: writes by an agent key and by a connection (use the OAuth test helper `OAuthFlow` for a connection token on `/mcp/c/{id}` with a `write` tool call) → `"agent"`.
  - `The_users_own_write_is_user_and_seed_is_server`: the session's own write → `"user"`; a seeded note → `"server"`.
  - `An_api_key_sees_no_writer_kinds`: the same reads with the key as bearer → `lastWriterKind` is `null`.
  - `Changes_list_agent_writes_only`: a device write and an agent write after `since` → `GET /api/changes` lists only the agent's path, and lists a connection's write (today it lists API keys only).
- [ ] **Step 2: Run them**, expect FAIL.
- [ ] **Step 3: Implement `WriterKinds`.** For a session: one map per request from the user's keys (revoked included, they mark old writes) and connections, by name, case-insensitively, as `WriterNames` compares; `Writers.Identity(label)` first ("Laptop (mv)" is "Laptop"); the username → `user`; `dev`/`seed` → `server`. `ChangesAsync` keeps its loop and filters on `agent`.
- [ ] **Step 4: Run them**, expect PASS; run `NoteApiTests` and `HistoryApiTests`.
- [ ] **Step 5: Commit** `Tell device writes from agent writes in the session's views`.

### Task 3: Web app — "you, in Obsidian", and agent highlights without device writes

**Files:**
- Modify: `web/src/api/schema.d.ts` (regenerated), `web/src/note/ChangedElsewhere.tsx` (`ChangeText`), `web/src/note/AgentBanner.tsx` (`lastSeen`), `web/src/note/HistorySheet.tsx`, `web/src/routes/Keys.tsx`
- Test: `web/src/note/ChangedElsewhere.test.tsx`, `web/src/note/AgentBanner.test.tsx` (create if absent), `web/src/routes/Keys.test.tsx`

**Interfaces:**
- Consumes: Task 2's `lastWriterKind` / `writerKind`, Task 1's `kind`.
- Produces: `export function deviceOf(writer: string): string` in `ChangedElsewhere.tsx`: the text after the last `"Obsidian "` in the writer label, or the whole label if there is none.

- [ ] **Step 1: Write the failing tests:**
  - `ChangedElsewhere.test.tsx`: `says_you_changed_it_in_Obsidian_for_a_device_write`: a polled version with `lastWriter: "Work – Obsidian Laptop"`, `lastWriterKind: "device"` renders `You changed this note in Obsidian (Laptop) just now`; an agent's write keeps today's `<writer> changed this note …`.
  - `deviceOf("Work – Obsidian Android-7Q") === "Android-7Q"`, `deviceOf("liz") === "liz"`.
  - `AgentBanner.test.tsx`: `lastSeen_counts_a_device_write_as_the_users`: a history `[agent (current), device, agent]` after `since` → `lastSeen` returns the device entry's version.
  - `Keys.test.tsx`: a key with `kind: "device"` shows the label `Obsidian device`.
  - `HistorySheet`: an entry with `writerKind: "device"` shows `Obsidian device` after the writer.
- [ ] **Step 2: Run** `pnpm -C web test`, expect FAIL.
- [ ] **Step 3: Implement.** Regenerate `schema.d.ts`. `lastSeen` treats `writerKind === "device"` like the user's own. Read mode's automatic refresh stays as it is for every writer.
- [ ] **Step 4: Run** `pnpm -C web test && pnpm -C web build`, expect PASS.
- [ ] **Step 5: Commit** `Say "you, in Obsidian" for a device's write`.

### Task 4: Change record, hints event and the versions endpoint

**Files:**
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs`, `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (`WriteAsync`), `src/Kumbu.Server/Vaults/VaultCatalog.cs` (event; raise in `DeleteVaultsAsync`)
- Create: `src/Kumbu.Server/Api/SyncApi.cs` (`MapSyncApi`, `VersionsView`, `VersionEntry`)
- Modify: `src/Kumbu.Server/Program.cs` (`app.MapSyncApi()`)
- Test: `tests/Kumbu.Server.Tests/Vaults/ChangeRecordTests.cs`, `tests/Kumbu.Server.Tests/Api/SyncApiTests.cs`

**Interfaces:**
- Produces:
  - `UnlockedVault.Horizon` (`DateTime`, set in the constructor to `now`); `UnlockedVault.ChangedAt` (`ImmutableDictionary<VaultPath, DateTime>`, set under `Gate`).
  - `VaultCatalog`: `public event Action<string>? NotesChanged;` (vault id) and `internal void RaiseNotesChanged(string vaultId)`; handlers must not throw into the writer (catch and log inside `RaiseNotesChanged`).
  - `public sealed record VersionEntry(string Path, string Version);`
  - `public sealed record VersionsView(bool Full, string Cursor, string State, int Damaged, IReadOnlyList<VersionEntry> Notes, IReadOnlyList<string> Deleted);`
  - `public static class SyncApi { public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5); public static readonly TimeSpan MaxCursorAge = TimeSpan.FromDays(25); }`

- [ ] **Step 1: Write the failing store tests** in `ChangeRecordTests` (through the services, as `Vaults` tests do): after each of write, edit, pure `mv` (source and target), `mv` with link rewrites (the rewritten note too), `rm`, restore from trash, restore from history, checkpoint restore, upload batch, replace → each touched path is in `ChangedAt` with a time ≥ the moment before the call, and `NotesChanged` fired **once per commit** with the vault's id. An aborted write (stale version) records nothing and fires nothing. `DeleteVaultsAsync` fires once.
- [ ] **Step 2: Write the failing API tests** in `SyncApiTests`:
  - `The_full_list_has_every_note_and_the_state`: three notes → `full: true`, three entries in text order, `damaged: 0`, `ETag` equals `"<state>"`.
  - `If_none_match_answers_304_until_a_note_changes`.
  - `Since_lists_the_current_state_of_touched_paths`: after the cursor: one edit, one `rm`, one pure `mv` → `full: false`, `notes` has the edited note and the move target with their current versions, `deleted` has the removed path and the move source.
  - `Since_overlaps_by_five_minutes`: write a note, advance the `FakeTimeProvider` 4 minutes, take a cursor from a full list, ask `since` it → the note is listed; the same with 6 minutes → it is not.
  - `Since_before_the_horizon_answers_the_full_list` (Review Focus 1): lock the vault (advance the clock past `IdleMinutes`, run the sweep) and ask with an old cursor → `full: true`.
  - `A_cursor_older_than_25_days_or_in_the_future_or_garbled_answers_the_full_list`.
  - `Damaged_notes_are_counted`: corrupt one note's blob in MongoDB (as the existing damage tests do) → `damaged: 1`, and `state` differs from before.
  - `A_pending_vault_answers_503_not_404` (Review Focus 2): during a re-keying's two-key period (helpers in `RekeyTests`) → `503` with `Retry-After`.
  - `A_key_for_another_vault_gets_404`: a key of user A for vault A asks for vault B of user B, and for vault B2 of user A it wasn't given → `404`, same body as an unknown vault.
- [ ] **Step 3: Run them**, expect FAIL.
- [ ] **Step 4: Implement.** In `WriteAsync`, after `apply()`: set `ChangedAt` for every path in `paths` to `catalog.Now`, then `catalog.RaiseNotesChanged(vault.Id)` (still under the gate, so records of one vault are in commit order; the event handler only enqueues). Skip both when `paths` is empty (checkpoint add/remove change no note). The endpoint (`GET /api/vaults/{vault}/versions`, `.Errors(400, 401, 404, 503)`, any caller) parses `since` with `DateTime.TryParse(..., RoundtripKind)`; the cursor is `catalog.Now` taken **before** reading the snapshot, formatted `"O"`. `state`: first 16 hex characters of SHA-256 over the lines `"<path>\t<version>\n"` in ordinal order, then `"damaged\t<n>\n"`. `If-None-Match` is honoured only without `since`.
- [ ] **Step 5: Run them**, expect PASS.
- [ ] **Step 6: Commit** `Record when each path changed and list a vault's versions since then`.

### Task 5: Delete with a version, `canWrite` on vaults

**Files:**
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs` (`RemoveAsync`), `src/Kumbu.Server/Api/NoteApi.cs` (`DeleteAsync`, `VaultView`, `ListVaultsAsync`, the other `VaultView` constructions)
- Test: `tests/Kumbu.Core.Tests/Notes/NoteWriterTests.cs` (or the file holding `RemoveAsync` tests), `tests/Kumbu.Server.Tests/Api/NoteApiTests.cs`

**Interfaces:**
- Produces: `NoteWriter.RemoveAsync(VaultPath path, CancellationToken ct, string? version = null)`; with a version that isn't the note's current one it returns `ShellResult.Error("rm: <path> has changed since version <v> (it is now <current>)")` and trashes nothing. `VaultView(..., bool Pending, bool CanWrite)`.

- [ ] **Step 1: Write the failing tests:** core: `Remove_with_a_stale_version_trashes_nothing`; API: `Delete_with_the_current_version_trashes_it` (204), `Delete_with_a_stale_version_answers_409_with_the_note` (body `current.content` and `current.version` as `PUT` does), `Delete_without_a_version_works_as_before`, `Vaults_say_whether_the_caller_can_write` (read key → `false`, write key → `true`, session → `true`).
- [ ] **Step 2: Run**, expect FAIL.
- [ ] **Step 3: Implement.** The version check happens inside `RemoveAsync` on the note it then trashes (`TryTrashAsync` compares the revision), so a write between the check and the trash can't be lost. `DeleteAsync` takes `string? version` from the query and maps the error to `NoteApi.Conflict(now, version)` after reading the note again.
- [ ] **Step 4: Run**, expect PASS (Core and Server).
- [ ] **Step 5: Commit** `Trash a note only at the version the caller saw`.

### Task 6: WebSocket tickets, hub and hints

**Files:**
- Create: `src/Kumbu.Server/Sync/SyncTickets.cs`, `src/Kumbu.Server/Sync/SyncHub.cs`
- Modify: `src/Kumbu.Server/Api/SyncApi.cs` (`POST /api/ws-ticket`, `GET /api/ws`), `src/Kumbu.Server/Accounts/ApiKeys.cs` (`IsActiveAsync`), `src/Kumbu.Server/Program.cs` (`UseWebSockets` with `KeepAliveInterval = 30 s` before routing; singletons; hosted hub)
- Test: `tests/Kumbu.Server.Tests/Api/SyncSocketTests.cs`

**Interfaces:**
- Consumes: `VaultCatalog.NotesChanged` (Task 4).
- Produces:
  - `SyncTickets` (singleton, `TimeProvider`): `string Issue(string keyId, IReadOnlyList<(string VaultId, string VaultName)> vaults)`; `TicketGrant? Take(string ticket)` (removes it; null when unknown or older than 30 s). `public sealed record TicketGrant(string KeyId, IReadOnlyList<(string VaultId, string VaultName)> Vaults);` Tickets via `Secrets.New("kumbu_wst_")`; stored by SHA-256 of the ticket, never the ticket.
  - `SyncHub` (singleton, `IHostedService`): `Task RunAsync(WebSocket socket, TicketGrant grant, CancellationToken ct)`; subscribes to `NotesChanged`; per connection a 500 ms trailing debounce per vault; every 30 s checks `ApiKeys.IsActiveAsync(keyId)` and drops vaults `catalog.IsDeleted`; closes with `4401` when inactive.
  - `ApiKeys.IsActiveAsync(string keyId, CancellationToken ct)`: not revoked, not expired, owner active.

- [ ] **Step 1: Write the failing tests** (`app.PublicApi.Server.CreateWebSocketClient()`):
  - `A_write_reaches_the_socket_as_a_hint`: ticket for a write key on vault A, connect, the session writes in A → receive `{"type":"changed","vault":"<A>"}` within 2 s.
  - `Hints_never_name_another_vault` (Review Focus 5): the same socket while user B writes in vault B and user A writes in a vault the key wasn't given → nothing within 1.5 s; then a write in A arrives.
  - `Thirty_writes_make_few_hints`: 30 writes in 100 ms → at most 2 hints.
  - `A_ticket_works_once_and_for_30_seconds`: second connect → 401; a fresh ticket after advancing the clock 31 s → 401.
  - `Only_api_keys_get_tickets`: session → 403, dev token → 403.
  - `A_revoked_key_is_disconnected` (Review Focus 5): revoke the key, advance the hub's 30-second check (make the interval a constructor parameter for tests) → close status 4401.
  - `A_request_without_an_upgrade_is_400`.
- [ ] **Step 2: Run**, expect FAIL.
- [ ] **Step 3: Implement.** `POST /api/ws-ticket`: `caller.Kind == CallerKind.ApiKey` else 403; vaults from `caller.OpenVaultsAsync` (ids and names). `GET /api/ws` is `AllowAnonymous()`: the ticket is the credential, so no Origin check applies (no cookie is involved); the ticket in the URL is single-use and worthless after 30 s. Send messages as UTF-8 text frames. Shutdown closes sockets with 1001.
- [ ] **Step 4: Run**, expect PASS.
- [ ] **Step 5: Commit** `Hint WebSocket clients when a vault of their key changes`.

### Task 7: Pairing codes

**Files:**
- Create: `src/Kumbu.Server/Accounts/Pairings.cs`, `src/Kumbu.Server/Api/PairingApi.cs`
- Modify: `src/Kumbu.Server/Api/Security.cs` (`OriginExemptMetadata`, honoured in `UseCallerRules`; `AccountLimits.Redeem`, `AccountLimits.RedeemFailed`), `src/Kumbu.Server/Program.cs`, `web/src/api/client.ts` (`POST /api/pairing/redeem` in `anonymous`)
- Test: `tests/Kumbu.Server.Tests/Accounts/PairingTests.cs`, `tests/Kumbu.Server.Tests/Api/PairingApiTests.cs`

**Interfaces:**
- Consumes: `ApiKeys.CreateAsync(..., kind: ApiKeyKind.Device)` (Task 1).
- Produces:
  - `Pairings` (singleton, `TimeProvider`): `PairingCode Create(User user, byte[] userKey, DateTime credentialReadAt, string vaultId, string vaultName, int? expiresInDays)`; `Task<RedeemedKey?> RedeemAsync(string code, string device, CancellationToken ct)` (null for an unknown or expired code; throws `ArgumentException` for a bad device name *before* taking the code). `public sealed record PairingCode(string Code, DateTime ExpiresAt);` `public sealed record RedeemedKey(string Key, string Vault, string KeyName);`
  - `public static string Normalise(string code)`: uppercase, drop spaces and `-`, I/L → 1, O → 0; `public static string Format(string raw)`: `XXXX-XXXX`.
  - `public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);`

- [ ] **Step 1: Write the failing unit tests** in `PairingTests`: `Codes_use_crockford_without_I_L_O_U` (1,000 codes, alphabet check, 8 characters); `Normalise_reads_lower_case_dashes_spaces_and_lookalikes` (`"k7qm 4xda"` and `"K7QM-4XDA"` → `"K7QM4XDA"`; `"o0Il-1LIo"` → `"00111110"`); `A_new_code_replaces_the_open_one`; `A_code_expires_after_ten_minutes` (`FakeTimeProvider`); `A_code_redeemed_twice_at_once_makes_one_key` (Review Focus 3: two `RedeemAsync` in parallel → one non-null, one null; `ListAsync` has one key); `A_bad_device_name_keeps_the_code` (Review Focus 4: `""`, 33 characters, `"a (b"`, `"x​y"` → `ArgumentException`; then `"Laptop"` succeeds); `The_key_is_named_after_vault_and_device_and_numbered_when_taken` (`"Work – Obsidian Laptop"`, then `"Work – Obsidian Laptop 2"`); `The_key_writes_its_one_vault_only_and_is_a_device`; `Expiry_is_as_chosen_and_never_by_default`; `A_rotated_user_key_invalidates_the_code` (rotate between create and redeem → null, no key).
- [ ] **Step 2: Write the failing API tests** in `PairingApiTests`: `The_session_creates_a_code_for_its_own_vault` (201, `XXXX-XXXX`); `An_api_key_cannot_create_codes` (403); `Another_users_vault_is_404`; `Redeem_needs_no_origin_and_no_credential` (no `Origin`, no cookie → 201 with `key`, `vault`, `keyName`); `The_redeemed_key_works_on_the_versions_endpoint`; `A_wrong_code_says_only_that_it_is_not_valid` (404, exact sentence); `Redeem_is_limited_per_address` (11th attempt in a minute from one address → 429 with `Retry-After`).
- [ ] **Step 3: Run them**, expect FAIL.
- [ ] **Step 4: Implement.** Records in a `ConcurrentDictionary` keyed by the normalised code; redeem validates the device, then `TryRemove`s (single use under concurrency), then checks expiry, zeroes the user key copy in `finally` (`CryptographicOperations.ZeroMemory`). Name: `WriterNames.Check` on the full name; on `ApiKeyNameTakenException` try ` 2` … ` 20`, then answer 409. `CredentialsChangedException` (password change, recovery, rotation since the code was made) → null. A sweep of expired records runs on each `Create` and `RedeemAsync`. Codes never appear in logs; a redeem logs user and key id. `POST /api/vaults/{vault}/pairing` is session-only; `POST /api/pairing/redeem` is `AllowAnonymous()` with `OriginExemptMetadata`, checks `AccountLimits.Redeem(context)` (10/min/address) first and counts a failure towards a global 100/min limit, after which every redeem answers 429 until it eases.
- [ ] **Step 5: Run them**, expect PASS.
- [ ] **Step 6: Commit** `Pair a device with a short code instead of a pasted key`.

### Task 8: Web app — "Connect Obsidian"

**Files:**
- Create: `web/src/shell/ConnectObsidianDialog.tsx`, `web/src/shell/ConnectObsidianDialog.test.tsx`
- Modify: `web/src/shell/TreeDrawer.tsx` (vault menu item), `web/src/api/schema.d.ts`

**Interfaces:**
- Consumes: `POST /api/vaults/{vault}/pairing` (Task 7); `CopyButton` from `web/src/components/shownOnce.tsx`; `ExpiryPicker` from `web/src/routes/NewKeyDialog.tsx` (0 = never).
- Produces: `export const pluginRepo = "SCAI-NGO/kumbu-obsidian";` in the dialog file.

- [ ] **Step 1: Write the failing tests** (`ConnectObsidianDialog.test.tsx`, mocking the API as the other dialog tests do): opens from the vault menu item `Connect Obsidian…`; shows the title `Connect "Work" with Obsidian`, the hint sentence, `SCAI-NGO/kumbu-obsidian` and `window.location.origin` each with a copy button; the expiry picker starts at *Never*; the code appears large with a countdown `valid for 9:59 · single use` that counts down (fake timers); choosing 90 days and *New code* posts `{ expiresInDays: 90 }`; at 0:00 the code is replaced by *New code* only; the line `Each device gets its own key, named "Work – Obsidian <device>". You can revoke it under API keys.`
- [ ] **Step 2: Run** `pnpm -C web test`, expect FAIL.
- [ ] **Step 3: Implement** after the approved mockup (`.superpowers/brainstorm/71940-1792993721/content/onboarding.html`, step 1): BRAT step, plugin repo, server URL, expiry, code. The code is requested when the dialog opens and again on *New code* or a changed expiry; it is never stored outside component state.
- [ ] **Step 4: Run** `pnpm -C web test && pnpm -C web build`, expect PASS.
- [ ] **Step 5: Commit** `Connect a vault with Obsidian from its menu`.

### Task 9: Documentation

**Files:**
- Modify: `README.md` ("Accounts, API keys and the REST API": sync endpoints, pairing, WebSocket, device keys; "Security": the reverse proxy must pass WebSocket upgrades on `/api/ws`, Traefik does by default, and its access log shows `?ticket=`, which is single-use and expires in 30 s), `docs/security-architecture.md` (a section "Device pairing and sync": what a pairing record holds and for how long, why not in MongoDB or Redis, rate limits, ticket design, hints carry vault names only), `docs/obsidian-plugin-design-spec.md` stays superseded.
- [ ] **Step 1:** Write the sections; every endpoint of the API contract appears with its status codes.
- [ ] **Step 2:** `dotnet test` and `pnpm -C web test` once more, all green; `curl -s localhost:8083/api/openapi.json | jq '.paths | keys'` lists `/api/vaults/{vault}/versions`, `/api/ws-ticket`, `/api/vaults/{vault}/pairing`, `/api/pairing/redeem`.
- [ ] **Step 3: Commit** `Document pairing, the version list and sync hints`.

### Task 10: Isolation review, then the pull request

**Files:** whatever the review's fixes touch, each with its test.

- [ ] **Step 1:** Dispatch a fresh reviewer (most capable model) with this brief and nothing about expected results: "Review every place in `src/Kumbu.Server` where a credential (session, API key, dev token, connection token, WebSocket ticket, pairing record) meets vault access: `CallerMiddleware`, `Credentials`, `EncryptedNoteStore`, `VaultCatalog.Open*`, `NoteApi`, `HistoryApi`, `UploadApi`, `SyncApi`, `SyncHub`, `SyncTickets`, `Pairings`, `PairingApi`, `ConnectionApi`, `AccountApi`. Find any way for one credential to read, write, list, count, or learn the existence or activity of a vault it wasn't given. Report findings with a concrete request sequence."
- [ ] **Step 2:** For each confirmed finding: a failing test in the style of Task 4's `A_key_for_another_vault_gets_404`, the fix, the test passing, a commit per finding.
- [ ] **Step 3:** All tests green; open the pull request `Obsidian sync: pairing, version list, hints and device keys` with the API contract summary, the review's findings and their fixes, and a manual checklist: pair from the web app with `curl` against `/api/pairing/redeem`, `websocat` on `/api/ws` shows a hint after an MCP write, the web app says "You changed this note in Obsidian (…)" for a write with the device key.
