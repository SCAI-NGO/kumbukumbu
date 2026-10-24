# Accounts Backend (step 8) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Users with accounts (invitation, username and password, recovery key), sessions, API keys with per-vault rights, an admin, and the REST API the web app needs — with the dev token optional.

**Architecture:** Every request resolves to a **caller**: a session (cookie), an API key or the dev token. A caller opens vaults through key slots, as the dev token does today, and carries a writer label and per-vault rights. A user has a random **user key**, wrapped by password (Argon2id), recovery key and each session; the user key wraps the user's vault keys. API keys get slots on chosen vaults only, with their rights sealed into the slot. The REST API under `/api` calls the same Core code as the `sh` commands.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB driver, `Microsoft.AspNetCore.OpenApi`, `Konscious.Security.Cryptography.Argon2` (managed Argon2id), xUnit v3, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-24-accounts-and-web-design.md` (§3 accounts and keys, §4 the REST API). Concept §7–§9.

## Global Constraints

- British English; SPDX `// SPDX-License-Identifier: AGPL-3.0-or-later` on new files; test names in the style of the file they go into.
- Argon2id: memory 64 MiB, 3 iterations, parallelism 4, 32-byte output, 16-byte random salt per slot (RFC 9106's second recommended setting).
- Passwords: at least 12 characters; refused if in the bundled list of 10,000 most common passwords (`src/Kumbu.Server/Accounts/common-passwords.txt`, from SecLists `10k-most-common.txt`, MIT).
- API keys: `kumbu_` + 32 random bytes in Base62; only HKDF-derived values stored; shown once.
- Sessions: secret = 32 random bytes, Base64Url, cookie `kumbu_session`, `HttpOnly; Secure; SameSite=Strict; Path=/`; idle 30 minutes, absolute 12 hours.
- Invitations: 32 random bytes Base64Url in the link fragment `/join#{code}` (changed in Task 8's review: never in a URL path, so never in logs), stored as HMAC-SHA256 under a server key; default expiry 7 days; single use; roles `user` or `admin`.
- Roles: `primary_admin` (exactly one; can't be removed, demoted or deactivated), `admin`, `user`.
- Rate limits (in memory): sign-in 5 per minute per username and 20 per minute per IP; join and recovery 5 per minute per IP; wrong bearer keys 20 per minute per IP. Behind Traefik, the client IP comes from `X-Forwarded-For` only when `Kumbu:Proxy:Trusted` is set (`KUMBU_TRUSTED_PROXY`).
- Nothing secret in logs, URLs (except the single-use invitation code) or responses after creation.
- Never content in admin views.
- `dotnet test` green after every task.

## Review Focus

1. A read-only API key calling `write`, `edit`, `replace`, `mv`, `rm`, `restore`, `mkcp`, `rmcp` or a REST write: refused with the missing right named, nothing written. Test in Task 1 (Core) and Task 6 (end to end).
2. A session cookie after sign-out, after a password change, after deactivation, and after the idle or absolute timeout: 401, and no cached unlocked vault is reachable with it. Tests in Task 4 and Task 7.
3. An invitation used twice, used after expiry, used after revocation, or two sign-ups racing on one code: exactly one account at most. Test in Task 7.
4. A deactivated user's API keys: refused at once, also for a vault still in the memory cache. Test in Task 7.
5. A tampered slot document (rights changed from read to write in MongoDB): the slot doesn't open. Test in Task 6.

---

### Task 1: Callers in Core

**Files:**
- Create: `src/Kumbu.Core/Notes/ICaller.cs`
- Modify: every use of `Writers.Dev` (`rg -n 'Writers\.Dev' src`: `Sh/Commands/Rm.cs`, `Mv.cs`, `Restore.cs`, `CheckpointCommands.cs`, `src/Kumbu.Server/Mcp/WriteTools.cs`, …), `src/Kumbu.Core/Notes/NoteWriter.cs`, `NoteMover.cs`, `Replacer.cs`, `Checkpoints.cs` where they write; the composition in `src/Kumbu.Server/Program.cs`; test fakes.
- Test: `tests/Kumbu.Core.Tests/Notes/CallerTests.cs` (create), existing tests adapted.

**Interfaces:**
- Produces: `public interface ICaller { string Writer { get; } bool CanWrite(string vault); }`; `public sealed record FixedCaller(string Writer, Func<string, bool>? CanWriteVault = null) : ICaller` (all vaults writable when null) for tests and the dev token; the refusal text `"{command}: this key can only read vault {Vault}"`.

- [ ] **Step 1: Failing tests**: `WritesCarryTheCallersWriterLabel` (a `write` through `NoteWriter` with `FixedCaller("claude-laptop")` → `history` shows `claude-laptop`); `AReadOnlyCallerIsRefusedEveryWrite` (for each of `write`, `edit`, `replace` apply, `mv`, `rm`, `restore`, `mkcp`, `rmcp`: the refusal text, nothing changed); `ReadingStillWorksForAReadOnlyCaller`.
- [ ] **Step 2: Run** `dotnet test --filter CallerTests`; expected FAIL.
- [ ] **Step 3: Implement**: inject `ICaller` where the writer label is needed and check `CanWrite(path.Vault)` before any write in the Core write paths (one check per entry point, before the store is touched). `Writers.Dev` stays as the dev token's label.
- [ ] **Step 4: Run** `dotnet test`; expected PASS.
- [ ] **Step 5: Commit** `Introduce callers: the writer label and per-vault write rights`.

### Task 2: Users, the user key, password and recovery slots

**Files:**
- Create: `src/Kumbu.Server/Accounts/UserCatalog.cs`, `UserDocuments.cs`, `PasswordPolicy.cs`, `common-passwords.txt` (embedded resource), `Argon2Kdf.cs`
- Modify: `src/Kumbu.Server/Kumbu.Server.csproj` (Argon2 package, embedded resource), `src/Kumbu.Server/Vaults/VaultCrypto` (if a helper is needed; reuse `Seal`/`Open`/`Derive`)
- Test: `tests/Kumbu.Server.Tests/Accounts/UserCatalogTests.cs`, `PasswordPolicyTests.cs`

**Interfaces:**
- Produces:
  - Collections `users` (`_id` random hex, `username` lowercase unique, `display` as typed, `role`, `active`, `createdAt`, `lastSignInAt`, `previousSignInAt`) and `user_slots` (`user`, `kind` = `password` | `recovery` | `session`, `salt`, `lookup`, `wrapped`, `createdAt`, `expiresAt`, `lastUsedAt`).
  - `UserCatalog.CreateAsync(string username, string password, string role, CancellationToken) → (User user, string recoveryKey)`; `TrySignInAsync(username, password) → (User, byte[] userKey)?`; `RecoverAsync(username, recoveryKey, newPassword) → bool` (new password slot, all sessions deleted); `ChangePasswordAsync(userId, userKey, oldPassword, newPassword)`; `RegenerateRecoveryKeyAsync(userId, userKey) → string`; `PasswordPolicy.Check(string) → string?` (null = fine, else the reason).
  - `public sealed record User(string Id, string Username, string Display, string Role, bool Active, DateTime CreatedAt, DateTime? LastSignInAt, DateTime? PreviousSignInAt)`.

- [ ] **Step 1: Failing tests**: create and sign in; wrong password → null (constant work: Argon2 runs also for an unknown username); username case-insensitive and unique (two creates racing → one wins); recovery sets a new password and ends sessions; regenerating the recovery key invalidates the old one; password policy (11 chars, a common password, a good one); the password slot's salt differs per user; `previousSignInAt` moves on each sign-in.
- [ ] **Step 2: Run**; expected FAIL.
- [ ] **Step 3: Implement** with the Global Constraints' parameters; the user key wrapped with `VaultCrypto.Seal` under the Argon2id output (password) or the recovery key (formatted as `RecoveryKey`), with the user id and slot kind as associated data.
- [ ] **Step 4: Run** `dotnet test`; expected PASS.
- [ ] **Step 5: Commit** `Add users with a user key, password and recovery slots`.

### Task 3: Vaults of a user

**Files:**
- Modify: `src/Kumbu.Server/Vaults/VaultCatalog.cs`, `Documents.cs` (`SlotDocument.User` kind)
- Test: `tests/Kumbu.Server.Tests/Vaults/UserVaultTests.cs`

**Interfaces:**
- Consumes: the user key (Task 2).
- Produces: `VaultCatalog.CreateForUserAsync(string name, string userId, byte[] userKey, CancellationToken) → UnlockedVault` (no recovery slot of its own; vault names unique per user); `OpenForUserAsync(string userId, byte[] userKey, CancellationToken) → IReadOnlyList<UnlockedVault>`; user slots have `lookup` = HKDF(userKey, "kumbu/v1/user-slot", userId).

- [ ] **Step 1: Failing tests**: a user's vault opens with the user key and not with another user's; two users may both have a vault `Arbeit`; one user can't create two; dev-token vaults keep working with the dev token.
- [ ] **Step 2–4**: run, implement, run `dotnet test`.
- [ ] **Step 5: Commit** `Give users their own vaults through the user key`.

### Task 4: Sessions

**Files:**
- Create: `src/Kumbu.Server/Accounts/Sessions.cs`
- Test: `tests/Kumbu.Server.Tests/Accounts/SessionTests.cs`

**Interfaces:**
- Produces: `Sessions.StartAsync(User, byte[] userKey) → string secret` (a `session` user slot: the user key wrapped under HKDF(secret), `lookup` = HKDF(secret, "kumbu/v1/session-lookup"), `expiresAt` = now + 12 h, TTL index on `expiresAt`); `Sessions.ResolveAsync(string secret) → (User, byte[] userKey)?` (refuses an inactive user, an idle gap over 30 minutes; refreshes `lastUsedAt` at most once a minute); `EndAsync(secret)`; `EndAllAsync(userId, exceptSecret?)`.

- [ ] **Step 1: Failing tests**: resolve after start; after `EndAsync`; after 31 idle minutes (`TimeProvider` fake); after 12 hours despite activity; after the user is deactivated; after `EndAllAsync`; a session survives a server restart (new `Sessions` on the same database).
- [ ] **Step 2–4**: run, implement, run `dotnet test`.
- [ ] **Step 5: Commit** `Add sessions that carry the user key`.

### Task 5: Resolving the caller of a request

**Files:**
- Create: `src/Kumbu.Server/Auth/CallerMiddleware.cs` (replaces `DevTokenMiddleware.cs`), `src/Kumbu.Server/Auth/RequestCaller.cs`
- Modify: `src/Kumbu.Server/Vaults/Credentials.cs` (`ICredential` becomes the resolved caller's way to open vaults), `EncryptedNoteStore.cs`, `Program.cs`, `KumbuOptions.cs` + validator (`DevToken` optional; `Kumbu:Proxy:Trusted`)
- Test: `tests/Kumbu.Server.Tests/Auth/CallerTests.cs` (and adapt `DevTokenTests.cs`)

**Interfaces:**
- Consumes: Sessions (Task 4), user vaults (Task 3), API keys (Task 6 adds the third kind; this task handles session and dev token and leaves a clear place for keys).
- Produces: `RequestCaller : ICaller` with `Kind` (`Session` | `ApiKey` | `DevToken`), `User?`, `OpenVaultsAsync(CancellationToken)`; anonymous paths: `/healthz`, `POST /api/session`, `POST /api/join`, `GET /api/invitations/{code}`, `POST /api/recover`, and everything that isn't `/mcp` or `/api` (the web app's static files). `/mcp` without a caller: 401 with `WWW-Authenticate: Bearer`; `/api`: 401 JSON `{"error":"…"}`. The dev token works only when configured. Wrong bearer keys count against the IP's rate limit.

- [ ] **Step 1: Failing tests**: the case-insensitive path bypass stays closed (`/MCP` without a caller → 401, as `DevTokenTests` checks today); a session cookie opens exactly that user's vaults; dev token off → refused; dev token on → as today; 21 wrong keys in a minute → 429.
- [ ] **Step 2–4**: run, implement, run `dotnet test` (all MCP tests keep using the dev token through the test factory).
- [ ] **Step 5: Commit** `Resolve every request to a caller: session, API key or dev token`.

### Task 6: API keys

**Files:**
- Create: `src/Kumbu.Server/Accounts/ApiKeys.cs`
- Modify: `CallerMiddleware.cs`/`RequestCaller.cs` (the `kumbu_` bearer kind), `VaultCatalog.cs` (key slots with sealed rights)
- Test: `tests/Kumbu.Server.Tests/Accounts/ApiKeyTests.cs`, `tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs` (a read-only key end to end)

**Interfaces:**
- Produces: collection `api_keys` (`_id`, `user`, `lookup`, `prefix` (first 8 characters after `kumbu_`), `meta` sealed under the user key: name and `{vault id: "read" | "write"}`, `createdAt`, `lastUsedAt`, `revokedAt`); a slot per vault of kind `key` whose sealed payload is `{vaultKey, rights}`; `ApiKeys.CreateAsync(User, byte[] userKey, string name, IReadOnlyDictionary<string, KeyRight> vaults) → (string key, ApiKeyInfo)`; `ListAsync(User, byte[] userKey) → IReadOnlyList<ApiKeyInfo>`; `RevokeAsync(User, string keyId)` (deletes its slots in the same transaction); writer label = the key's name; `lastUsedAt` at most once a minute.

- [ ] **Step 1: Failing tests**: a key opens only its vaults; read-only key refused writes over MCP with the Task 1 text; revoked key → 401 at once, also with its vault in the cache; a slot whose stored rights were edited in MongoDB doesn't open; a key without vaults can't be created; two keys of one user write as two writers (separate changesets).
- [ ] **Step 2–4**: run, implement, run `dotnet test`.
- [ ] **Step 5: Commit** `Add API keys with per-vault rights sealed into their slots`.

### Task 7: Invitations, the first admin, and the admin's tools

**Files:**
- Create: `src/Kumbu.Server/Accounts/Invitations.cs`, `src/Kumbu.Server/Accounts/Admin.cs`, `src/Kumbu.Server/Cli/BootstrapAdmin.cs`
- Modify: `Program.cs` (when `args[0] == "bootstrap-admin"`, run the command and exit), `Dockerfile` (a `kumbu` wrapper script on the PATH: `exec dotnet /app/Kumbu.Server.dll "$@"`)
- Test: `tests/Kumbu.Server.Tests/Accounts/InvitationTests.cs`, `AdminTests.cs`

**Interfaces:**
- Produces: collection `invitations` (`_id`, `codeHash`, `role`, `note`, `createdBy`, `createdAt`, `expiresAt`, `usedAt`, `usedBy`, `revokedAt`); `Invitations.CreateAsync(createdBy, role, note, expiry) → (string code, InvitationInfo)`; `PeekAsync(code) → InvitationInfo?` (for the join page: role, expiry, valid or why not); `RedeemAsync(code, username, password) → (User, string recoveryKey)` (atomic: `usedAt` set with a conditional update; a second redeem fails); `RevokeAsync`; `BootstrapAdmin.RunAsync(baseUrl) → string link` (refuses when a `primary_admin` exists; the first redeem of its invitation creates the primary admin); `Admin.ListUsersAsync()` (metadata: name, role, active, created, last seen, number of vaults, bytes stored), `SetActiveAsync(userId, bool)` (deactivating deletes sessions and revokes keys), `SetRoleAsync`.
- `Kumbu:PublicUrl` (`KUMBU_PUBLIC_URL`, e.g. `https://kumbu.tidesson.net`) for links.

- [ ] **Step 1: Failing tests**: redeem once; twice → refused; expired; revoked; two concurrent redeems → one account; bootstrap twice → refused the second time; deactivate kills sessions and keys (Review Focus 2 and 4); the primary admin can't be deactivated or demoted; admin listing contains no vault names or content.
- [ ] **Step 2–4**: run, implement, run `dotnet test`.
- [ ] **Step 5: Commit** `Add invitations, the bootstrap command and admin tools`.

### Task 8: The REST API: accounts

**Files:**
- Create: `src/Kumbu.Server/Api/AccountApi.cs`, `src/Kumbu.Server/Api/ApiErrors.cs`, `src/Kumbu.Server/Api/Security.cs` (Origin check, security headers, rate limiter policies)
- Modify: `Program.cs` (`AddOpenApi`, `MapOpenApi` at `/api/openapi.json`)
- Test: `tests/Kumbu.Server.Tests/Api/AccountApiTests.cs`

**Endpoints** (JSON; errors `{"error": "<sentence>"}` with the status):
- `POST /api/session` `{username, password}` → 200 + cookie, `{user}`; 401 on failure (same message for unknown user and wrong password); `DELETE /api/session`; `GET /api/session` → `{user, previousSignInAt}`.
- `GET /api/invitations/{code}` → `{role, expiresAt, valid, reason?}`; `POST /api/join` `{code, username, password}` → `{user, recoveryKey}` + cookie.
- `POST /api/recover` `{username, recoveryKey, newPassword}`; `POST /api/password` `{oldPassword, newPassword}`; `POST /api/recovery-key` → `{recoveryKey}`; `POST /api/sessions/end-others`.
- `GET/POST /api/keys`, `DELETE /api/keys/{id}`.
- `GET /api/admin/users`, `PATCH /api/admin/users/{id}` `{active?, role?}`, `GET/POST /api/admin/invitations`, `DELETE /api/admin/invitations/{id}`.
- Security headers on every response: `Strict-Transport-Security` (when `PublicUrl` is https), `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, `Content-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'`.
- State-changing requests with a session need `Origin` equal to `PublicUrl` (or absent `Origin` refused).

- [ ] **Step 1: Failing tests** (WebApplicationFactory, real Mongo): the join flow end to end; sign in, out; wrong password and unknown user give the same answer; rate limit → 429; a POST with a foreign `Origin` → 403; non-admin on `/api/admin/*` → 403; the OpenAPI document lists every endpoint.
- [ ] **Step 2–4**: run, implement, run `dotnet test`.
- [ ] **Step 5: Commit** `Add the account REST API`.

### Task 9: The REST API: vaults and notes

**Files:**
- Create: `src/Kumbu.Server/Api/NoteApi.cs`, `src/Kumbu.Server/Api/UploadApi.cs`
- Test: `tests/Kumbu.Server.Tests/Api/NoteApiTests.cs`, `UploadApiTests.cs`

**Endpoints** (paths are note paths below the vault, URL-encoded; every response that returns a note carries `version`):
- `GET /api/vaults` → `[{name, notes}]`; `POST /api/vaults` `{name}` (sessions only).
- `GET /api/vaults/{vault}/tree` → nested folders and notes (names, sizes), as `ls`.
- `GET /api/notes/{vault}/{*path}` → `{path, content, version, lastWriter, lastWriteAt}`; `PUT` `{content, version?}` (create without version; 409 `{error, current}` on a stale version); `DELETE` (to the trash); `POST /api/move` `{from, to, keepLinks}`.
- `GET /api/links/{vault}/{*path}`, `/api/backlinks/…`, `/api/history/…` (from the same code as `links`, `backlinks`, `history`; JSON rows, not text).
- `GET /api/search?q=&mode=hybrid|meaning|exact&path=&n=` → `[{path, address, startLine, endLine, snippet, score, reason}]`: `exact` = `rg -F -i` lines grouped by section; `reason` = `title` (query in the note name), `exact` (all terms present), `meaning` (otherwise).
- `GET /api/changes?since=` → notes whose last writer is an API key, written after `since` (for the agent banner).
- `POST /api/upload/{vault}/{*folder}` with `{files: [{path, content}], onConflict: "ask" | "replace" | "skip"}`, at most 200 files or 5 MB per request: `ask` returns `{conflicts: [...]}` without writing; non-`.md` files and paths below `.obsidian/` are skipped and listed; writes as one changeset of the user.

- [ ] **Step 1: Failing tests**: tree, read, create, update with version, stale version → 409 with the current content, delete, move with link rewrite; search modes; upload with nested folders, a conflict under `ask`, `replace` (the old content in history), skipped non-Markdown; an API key's read-only right applies to REST too.
- [ ] **Step 2–4**: run, implement, run `dotnet test`.
- [ ] **Step 5: Commit** `Add the notes REST API with upload`.

### Task 10: Documentation

**Files:**
- Modify: `README.md` (accounts, the bootstrap command, `KUMBU_PUBLIC_URL`, `KUMBU_TRUSTED_PROXY`, the dev token optional), `.env.example`, `compose.yaml`, `CONCEPTS.md` (a chapter: callers, the user key, sessions as slots, API keys with sealed rights, invitations; the why, in the house style), `docs/concept.md` §8 (the bootstrap is a command, not the log, with the reason).

- [ ] **Step 1: Edit**; numbers and names exactly as built.
- [ ] **Step 2: Commit** `Describe accounts, sessions and API keys`.
