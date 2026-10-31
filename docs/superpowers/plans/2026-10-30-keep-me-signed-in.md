# Keep Me Signed In Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A "Keep me signed in on this device" box at sign-in gives a session of up to 30 days (7 idle) that reads and writes freely but needs the password again for anything that makes a new way in or takes data out in bulk once the normal window (30 minutes idle, 12 hours since the password) has closed.

**Architecture:** The session slot records `remembered`, `confirmedAt` and `windowUsedAt`; `Sessions.ResolveAsync` applies the outer limits to a remembered slot and says whether the normal window is open, and `RequestCaller` carries that. One endpoint marker, `PasswordWindowMetadata`, is checked centrally in `Security.UseCallerRules` (as `SessionOnlyMetadata` is), and a test makes every session-only endpoint either carry it or appear on a reasoned list of exceptions, so a new endpoint can't slip past unconsidered. Three endpoints that guard only their granting half (consent allow, admin user update, invitations) check inline with `Security.RequirePasswordWindow`. The web app answers the distinct 401 (`type: "password-required"`) in one openapi-fetch middleware that asks for the password in one dialog, confirms it and repeats the request once.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, MongoDB driver, xUnit v3 with `FakeTimeProvider`; React 18, openapi-fetch 0.17, zustand, TanStack Query, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-30-keep-me-signed-in-design.md` (binding).

## Where and how this runs

- Commands: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test --solution Kumbu.slnx`, `pnpm -C web install --frozen-lockfile` (once), `pnpm -C web test --run`, `pnpm -C web build` (runs `tsc --noEmit`, tests included).
- **Regenerating `web/src/api/schema.d.ts`** (generated, never edited by hand) after Tasks 2 and 3, against a server you start yourself:
  ```sh
  Kumbu__Mongo__ConnectionString="${KUMBU_TEST_MONGO:-mongodb://127.0.0.1:27017/?directConnection=true}" \
  Kumbu__Mongo__Database=kumbu-gen Kumbu__Search__Enabled=false ASPNETCORE_URLS=http://127.0.0.1:8099 \
    dotnet run --project src/Kumbu.Server --no-launch-profile > "$HOME/kumbu-gen.log" 2>&1 &
  until curl -fsS http://127.0.0.1:8099/healthz >/dev/null; do sleep 1; done
  KUMBU_OPENAPI_URL=http://127.0.0.1:8099/api/openapi.json pnpm -C web gen:api
  kill %1
  ```
- API tests that move the clock use `app.PublicApi.WithWebHostBuilder(...)` with a `FakeTimeProvider` replacing `TimeProvider`, starting at the real time in whole seconds (see `tests/Kumbu.Server.Tests/Api/HistoryApiTests.cs:351–360`; MongoDB's TTL monitor runs on the real clock).

## Global Constraints

- British English in code, comments, docs, UI copy and commits; `// SPDX-License-Identifier: AGPL-3.0-or-later` on new C# and TypeScript files; no organisation names in code, tests or examples (vault `Arbeit`/`Work`).
- Remembered session: at most **30 days**, ends after **7 days** idle. Normal window inside it: **30 minutes** idle, **12 hours** at most, counted from the last time the password was given. Normal sessions keep `Sessions.IdleTimeout` (30 min) and `Sessions.AbsoluteTimeout` (12 h) and behave exactly as today.
- All four times are constants in `Sessions`; nothing configurable.
- Nothing is stored for a remembered session but times and a flag: no password, no PIN, no second wrapping of the user key.
- The box is **unticked by default**; join and recovery always start normal sessions.
- A guarded action with the window closed answers **401** with body `{ "error": "<sentence>", "type": "password-required" }`, never 403.
- Revoking, deleting and reading are never guarded; `PUT /api/vaults/{vault}/embeddings` is deliberately not guarded.
- Background requests (`BackgroundMetadata`, today only `GET /api/version/{vault}/{**path}`) move no clock: not the outer idle clock, not the window, not the visit.
- The existing "Ask for my password when downloading several files" preference stays and applies on top.
- Commit messages imperative, free-form, ending with the attribution lines of the session that executes the plan.

## Decisions (taken by the planner, each with its reason)

- **Guard placement:** in `Security.UseCallerRules`, after the `SessionOnlyMetadata` check and after the Origin check (a forged cross-origin request stays a 403). Every guarded endpoint also carries `SessionOnlyMetadata`; a test asserts it.
- **Exports carry the marker too**, although their ticket already asks for the password: the ticket's password check opens the window, so a real export always passes, and the marker makes the export endpoints show up in the enumeration like every other bulk download. The **files ZIP** (`GET`/`POST /api/files/{vault}/zip`) does **not** carry the marker: it is a form post into a hidden frame, so it requires the ticket inline when the window is closed (`withPassword = preference || !caller.PasswordWindowOpen`), and the enumeration test lists it as "guarded inline".
- **Three endpoints are guarded inline, not by the marker** (decided by the coordinator after review), with a helper `Security.RequirePasswordWindow(RequestCaller)` that throws the same 401 `password-required`:
  - the consent decision (`POST /api/oauth/requests/{id}/decision`): only `allow: true`. Deny is cleaning up and never guarded.
  - the admin user update (`PATCH /api/admin/users/{id}`): only when it activates a user or changes a role; deactivating is cleaning up. Making someone an admin already checks the password in the body (`RequirePasswordAsync`): that check runs first and opens the window, so the admin types the password once.
  - creating an invitation (`POST /api/admin/invitations`): an admin invitation's password in the body is checked first and opens the window, as above; any other invitation needs the window.
  The enumeration test lists them as "guarded inline" with their reason.
- **Window idle and visits are one event.** While the window is open, `windowUsedAt` moves with `lastUsedAt`; the first touching request after more than 30 minutes without one both leaves the window closed and begins a visit.
- **Visits reuse the existing fields.** `users.lastSignInAt` / `previousSignInAt` (BSON and C# names unchanged) now hold the latest and the previous visit, a sign-in being a visit too. `SessionView.previousSignInAt` keeps its JSON name (32 web test fixtures use it; its doc comment says "previous visit"); `AdminUserView.lastSignInAt` is renamed `lastActiveAt` (one web file and its test), since the admin list now shows "Last active".
- **Opening the window on any password check:** a helper `AccountApi.PasswordGivenAsync` is called after every successful password check behind a session that keeps the session: export ticket, new recovery key, re-encrypt, preferences (turning the download password off), admin invitation/role password, admin user deletion, repeated deletions, vault deletion, vault re-key. A forgotten call costs comfort, never security.
- **Confirmation's key check:** `UserCatalog.CheckPasswordAsync` compares the key the password slot opens with the session's key; the password slot always holds the key in force (a rotation writes it with `users.keyId` in one transaction, `UserKeyRotation.cs:120–140`), so a match means the session's key is the one in force. A comment says so; no second lookup.
- **Logs:** sign-ins aren't logged today (only `KumbuMetrics.SignedIn`). A remembered sign-in logs `User {UserId} signed in and is kept signed in on this device`; a confirmation logs `User {UserId} confirmed their password`; both Information, ids only. Metrics unchanged.
- **Interrupted re-keying:** `Rekeying.ResumeOnce(User, byte[])` keeps a per-process set of user ids and calls `ResumePending` the first time a session of that user is resolved; `CallerMiddleware` calls it for every session caller.
- **Web retry:** the middleware keeps a `request.clone()` of each non-GET request by openapi-fetch's `id` until its answer arrives (every guarded call through `api` is a POST, PUT or PATCH); the clone of an upload costs memory only while it is in flight.

## Review Focus

- **A closed window must stay closed under use.** A remembered session used every few minutes after its window closed (idle, or 12 hours since the password) must not reopen it: `windowUsedAt` moves only while the window is open. Test in Task 1 (`Using_a_session_after_its_window_closed_does_not_reopen_it`).
- **Concurrent first requests of a visit.** Several tabs waking at once must start one visit, not shift `previousSignInAt` twice (which would empty the agent banner). The touch write is conditional on the `lastUsedAt` read; test in Task 1 (`Two_requests_at_the_start_of_a_visit_move_the_previous_visit_once`).
- **A rotation keeping a remembered session idle for more than 30 minutes.** `PrepareKeepAsync` checks idleness with today's 30 minutes; "Re-encrypt my vaults" from a remembered session after a coffee break would sign the user out. It must use the slot's own idle limit. Test in Task 1 (`A_rotation_keeps_a_remembered_session_idle_for_an_hour_and_its_flag`).
- **Two guarded requests at once with the window closed** (a page firing two creates, or a double tap) must show one dialog and repeat both after one confirmation. Test in Task 4 (`asks once for two guarded requests and repeats both`).
- **A 401 `password-required` must never sign the user out**, cancelled or not; a plain 401 still must. Test in Task 4 (`keeps the session when the password dialog is cancelled`).

---

### Task 1: Remembered sessions in `Sessions`

**Files:**
- Modify: `src/Kumbu.Server/Accounts/Sessions.cs` (constants, `StartAsync`, `ResolveAsync`, new `OpenWindowAsync`, `PrepareKeepAsync:205–218`)
- Modify: `src/Kumbu.Server/Accounts/UserDocuments.cs` (`UserSlotDocument` fields; doc comments of `LastSignInAt`/`PreviousSignInAt`: "the latest/previous visit")
- Test: `tests/Kumbu.Server.Tests/Accounts/SessionTests.cs`, `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs`

**Interfaces:**
- Produces:
  - `Sessions.RememberedIdleTimeout = TimeSpan.FromDays(7)`, `Sessions.RememberedAbsoluteTimeout = TimeSpan.FromDays(30)` (public static readonly, beside the existing two, which double as the window's limits).
  - `UserSlotDocument.Remembered` (`bool`, BSON `remembered`, `BsonIgnoreIfDefault`), `ConfirmedAt` (`DateTime?`, `confirmedAt`, `BsonIgnoreIfNull`), `WindowUsedAt` (`DateTime?`, `windowUsedAt`, `BsonIgnoreIfNull`). A normal session's slot sets none of them.
  - `Task<string> StartAsync(User user, byte[] userKey, DateTime credentialReadAt, CancellationToken ct, bool remember = false)`: remembered → `ExpiresAt = now + 30 d`, `Remembered = true`, `ConfirmedAt = WindowUsedAt = now`.
  - `Task<(User User, byte[] UserKey, DateTime CredentialReadAt, bool WindowOpen)?> ResolveAsync(string secret, CancellationToken ct, bool touch = true)`. Normal session: `WindowOpen` always true. Remembered: open iff `now < ConfirmedAt + 12 h` and `now - WindowUsedAt <= 30 min`. The returned `User` carries the moved visit fields when this request began a visit.
  - `Task OpenWindowAsync(string secret, CancellationToken ct)`: for a remembered slot found by lookup, sets `ConfirmedAt`, `WindowUsedAt` and `LastUsedAt` to now; does nothing for a normal or unknown one. Callers have verified the password.
  - `Sessions.IdleLimit(UserSlotDocument slot)` (private static): 7 d or 30 min; used by `ResolveAsync` and `PrepareKeepAsync`.

- [ ] **Step 1: Write the failing tests** in `SessionTests.cs` (all with `_time`, a remembered session from `StartAsync(..., remember: true)`):

```csharp
[Fact] A_remembered_session_lives_through_six_days_of_quiet()
// Advance 6 days → ResolveAsync not null.
[Fact] A_remembered_session_idle_for_seven_days_and_a_minute_is_refused_and_removed()
// Advance 7 d + 1 min → null; no session slot left.
[Fact] A_remembered_session_ends_after_30_days_despite_daily_use()
// Resolve once a day for 29 days → not null each; then +1 day → null. Slot's ExpiresAt == start + 30 d.
[Fact] The_window_is_open_after_sign_in_and_closes_after_31_idle_minutes()
// Right after start: WindowOpen true. +29 min: true. +31 min more: resolve returns non-null with WindowOpen false.
[Fact] The_window_closes_12_hours_after_the_password_despite_activity()
// Resolve every 20 minutes: WindowOpen true until 12 h; the first resolve at/after 12 h → false, session still resolves.
[Fact] Using_a_session_after_its_window_closed_does_not_reopen_it()
// +31 min → closed; then resolve every 5 min for an hour → WindowOpen false every time.
[Fact] Opening_the_window_restarts_both_window_clocks()
// +31 min → closed; OpenWindowAsync → true; +11 h 59 min with resolves every 20 min → true; slot.ConfirmedAt == time of OpenWindowAsync.
[Fact] Opening_the_window_of_a_normal_session_writes_nothing()
// Normal session: OpenWindowAsync; slot has no confirmedAt/windowUsedAt/remembered fields (BsonDocument check).
[Fact] Background_requests_move_no_clock_of_a_remembered_session()
// touch: false every 5 min for 35 min → WindowOpen false at the end; slot.LastUsedAt == start; slot.WindowUsedAt == start.
[Fact] A_normal_session_is_stored_and_limited_as_before()
// Normal: slot BsonDocument has no remembered/confirmedAt/windowUsedAt; WindowOpen true; idle 31 min → null (as the existing tests).
[Fact] The_first_request_after_half_an_hour_begins_a_visit()
// Remembered; user doc LastSignInAt = L (the sign-in). +45 min, resolve (touch) → user.LastSignInAt == now, PreviousSignInAt == L,
// and the returned User carries them. +10 min resolve → unchanged. touch:false after another 45 min → unchanged.
[Fact] Two_requests_at_the_start_of_a_visit_move_the_previous_visit_once()
// +45 min; two ResolveAsync in parallel (Task.WhenAll) → PreviousSignInAt == L, LastSignInAt == now (not shifted twice).
[Fact] Ending_all_sessions_ends_remembered_ones()
// Two remembered sessions; EndAllAsync(user, except: null) → both null. Same for the transaction overload.
[Fact] A_password_change_ends_a_remembered_session()
// Remembered; Users().ChangePasswordAsync(...) → ResolveAsync null.
```

In `UserKeyRotationTests.cs`:

```csharp
[Fact] A_rotation_keeps_a_remembered_session_idle_for_an_hour_and_its_flag()
// Remembered session, +1 h idle, rotation keeping its secret → kept: resolves with the new user key; slot.Remembered true,
// ExpiresAt and ConfirmedAt unchanged from before the rotation.
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~SessionTests|FullyQualifiedName~UserKeyRotationTests"`
Expected: build errors (no `remember` parameter, no `WindowOpen`, no `OpenWindowAsync`), then failures.

- [ ] **Step 3: Implement** the interfaces above in `Sessions.cs` and `UserDocuments.cs`. The touch write becomes one conditional update `s.Id == slot.Id && s.LastUsedAt == lastUsedAt` (exact value read, so of concurrent requests one writes) setting `LastUsedAt`, and `WindowUsedAt` too when the window is open; when it modified a document and `now - lastUsedAt > IdleTimeout`, the request began a visit: update the user (`PreviousSignInAt = LastSignInAt` as read, `LastSignInAt = now`) and return the moved values. Update the callers of `ResolveAsync` (`CallerMiddleware.cs:100–104`, deconstructing four values; the window value is used in Task 2) so the solution builds. The XML doc of `ResolveAsync` names both limits for both kinds and the visit.

- [ ] **Step 4: Run them and see them pass** (same command), then `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~Accounts"`. Expected: PASS, the existing session tests unchanged.

- [ ] **Step 5: Commit** "Let a session be remembered for 30 days with a normal window inside it"

---

### Task 2: Sign-in, confirmation and the caller

**Files:**
- Modify: `src/Kumbu.Server/Auth/RequestCaller.cs` (`ForSession`, new property), `src/Kumbu.Server/Auth/CallerMiddleware.cs:95–105` (window, `ResumeOnce`), `src/Kumbu.Server/Accounts/Rekeying.cs` (`ResumeOnce`), `src/Kumbu.Server/Api/AccountApi.cs` (`SignInRequest`, `SessionView`, `SignInAsync`, `Session`, `SetSessionCookie:459–465`, new confirm endpoint, `AdminUserView`), `src/Kumbu.Server/Accounts/Admin.cs` (`AdminUser.LastActiveAt`)
- Regenerate: `web/src/api/schema.d.ts`
- Test: `tests/Kumbu.Server.Tests/Api/RememberedSessionTests.cs` (create), `tests/Kumbu.Server.Tests/Accounts/RekeyingTests.cs`, `tests/Kumbu.Server.Tests/Accounts/AdminTests.cs`

**Interfaces:**
- Consumes: Task 1's `StartAsync(..., remember)`, `ResolveAsync` (4 values), `OpenWindowAsync`.
- Produces:
  - `RequestCaller.PasswordWindowOpen` (`internal bool`): the resolved value for a session, `false` for every other kind (fail closed); `RequestCaller.ForSession(User, byte[], DateTime, bool windowOpen, VaultCatalog)`.
  - `SignInRequest(string Username, string Password, bool Remember = false)` (JSON `remember`).
  - `SessionView(UserView User, DateTime? PreviousSignInAt, bool PasswordWindowOpen)`; doc: `previousSignInAt` is the previous visit (a sign-in, or for a remembered session the first request after 30 minutes without one), `passwordWindowOpen` whether guarded actions go ahead without the password. The sign-in answer has `true`.
  - `POST /api/session/confirm` with `ConfirmPasswordRequest(string Password)`, session-only, **not** guarded: rate limit `AccountLimits.PasswordCheck(username)` (429 + `Retry-After`), `UserCatalog.CheckPasswordAsync(user.Id, caller.UserKey!, password)` (400 "The password is wrong." when false, as the other password checks answer; a 401 would sign the web app out), then `Sessions.OpenWindowAsync(cookie)`, log, `204`. No `Set-Cookie`. `.Errors(400, 401, 403, 429)`.
  - `AccountApi.PasswordGivenAsync(HttpContext http, Sessions sessions, CancellationToken ct)` (internal static): `OpenWindowAsync` with the request's cookie; used here and in Task 3.
  - `AdminUserView(..., DateTime? LastActiveAt, ...)` in place of `LastSignInAt`; `Admin.AdminUser` likewise.
  - `Rekeying.ResumeOnce(User user, byte[] userKey)`: `ResumePending` the first time per user id and process (a `ConcurrentDictionary<string, byte>`).

- [ ] **Step 1: Write the failing tests** in `RememberedSessionTests.cs` (`[Collection("app")]`, a timed `PublicApi` per test where the clock matters, helpers in the style of `AccountApiTests.cs:29–83`; `SignInAsync(username, remember)` returns the secret and the `Set-Cookie` line):

```csharp
[Fact] An_unticked_sign_in_sets_a_12_hour_cookie_and_a_normal_session()
// Set-Cookie max-age=43200; GET /api/session → passwordWindowOpen true; +31 min → GET /api/session 401.
[Fact] A_remembered_sign_in_sets_a_30_day_cookie()
// body { username, password, remember = true } → Set-Cookie max-age=2592000; answer passwordWindowOpen true.
[Fact] A_remembered_session_still_reads_and_writes_after_its_window_closed()
// remembered, a vault and note; +31 min → GET /api/session 200 with passwordWindowOpen false; GET and PUT the note 200;
// GET /api/vaults/{v}/tree 200; DELETE /api/keys/{id} of a key made earlier 204.
[Fact] Confirming_the_password_opens_the_window_without_a_new_cookie()
// +31 min; POST /api/session/confirm { password } → 204, no Set-Cookie; GET /api/session → passwordWindowOpen true.
[Fact] A_wrong_password_leaves_the_window_closed()
// +31 min; confirm with a wrong password → 400 "The password is wrong."; passwordWindowOpen stays false.
[Fact] Confirmations_are_rate_limited_with_the_other_password_checks()
// 5 wrong confirmations → the 6th 429 with Retry-After; a right one in the same minute → 429 too.
[Fact] A_confirmation_needs_a_session()
// with an API key (bearer) → 403; without anything → 401.
[Fact] Confirming_in_the_session_kept_by_a_reencryption_works()
// remembered; POST /api/account/reencrypt { password } → 200; +31 min; confirm → 204; window open.
[Fact] A_remembered_sign_in_and_a_confirmation_are_logged_by_user_id()
// CapturedLog (tests/Kumbu.Server.Tests/CapturedLog.cs): one line each with the user id; no username, no password.
[Theory] Every_way_of_ending_sessions_ends_a_remembered_one(string way)
// ways: "sign-out", "end-others" (from a second session), "password", "recovery", "deactivation", "account-delete";
// afterwards GET /api/session with the remembered cookie → 401.
[Fact] The_session_view_counts_since_the_previous_visit()
// remembered sign-in at T0 (previousSignInAt null on a first sign-in); +45 min GET /api/session → previousSignInAt == T0;
// +10 min → still T0; a confirmation in between moves nothing.
```

In `RekeyingTests.cs`, beside `A_recovery_interrupted_between_vaults_resumes_after_sign_in`:

```csharp
[Fact] A_pending_rekeying_resumes_at_the_first_request_of_a_session()
// The same interrupted recovery, but no sign-in: a session started with Sessions.StartAsync (as after a restart, the session
// predates the process); GET /api/vaults with it → hold.Reached; AllRekeyedAsync; a second request doesn't start the runner again.
```

In `AdminTests.cs`: the user list's `LastActiveAt` equals the user's `LastSignInAt` after a visit (rename the existing assertion's property).

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~RememberedSessionTests|FullyQualifiedName~RekeyingTests|FullyQualifiedName~AdminTests"`
Expected: build errors, then failures.

- [ ] **Step 3: Implement** the interfaces above. `SetSessionCookie(HttpContext, string secret, bool remember)` sets `MaxAge` to `Sessions.RememberedAbsoluteTimeout` or `Sessions.AbsoluteTimeout`; join keeps `remember: false`. Map the confirm endpoint in the session-only `account` group (`AccountApi.cs:136`).

- [ ] **Step 4: Run them and see them pass** (same command), then `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~AccountApiTests|FullyQualifiedName~CallerTests"`. Expected: PASS.

- [ ] **Step 5: Regenerate the schema** (see "Where and how this runs"), fix the one web use of `lastSignInAt` only so far as `pnpm -C web build` passes (the copy follows in Task 6), and make `useSession().signedIn` accept `Pick<SessionView, "user" | "previousSignInAt">` so the existing fixtures without the new field still type-check.

- [ ] **Step 6: Commit** "Keep a session signed in on request and confirm the password inside it"

---

### Task 3: The guard

**Files:**
- Modify: `src/Kumbu.Server/Api/Security.cs` (new `PasswordWindowMetadata`, `UseCallerRules:121–153`), `src/Kumbu.Server/Api/ApiErrors.cs` (`ApiError`, constant), `AccountApi.cs:146, 150, 155` and the password-checking handlers, `ConnectionApi.cs:63–64, 71`, `PairingApi.cs:30`, `ExportApi.cs:41–60, 91–100, 216–217`, `NoteApi.cs` (`DeleteVaultAsync`, `RekeyVaultAsync`)
- Test: `tests/Kumbu.Server.Tests/Api/PasswordWindowTests.cs` (create)

**Interfaces:**
- Consumes: `RequestCaller.PasswordWindowOpen`, `AccountApi.PasswordGivenAsync` (Task 2).
- Produces:
  - `public sealed class PasswordWindowMetadata { public static readonly PasswordWindowMetadata Instance = new(); }`, doc: "Marks an endpoint that creates a credential, widens what one reaches, or takes vault data out in bulk: a remembered session needs its normal window open (keep-me-signed-in design)".
  - `public sealed record ApiError(string Error, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type = null)`; `ApiErrors.PasswordRequired = "password-required"`.
  - In `UseCallerRules`, after the Origin check: an endpoint with the marker and a caller that isn't `{ Kind: Session, PasswordWindowOpen: true }` → 401, `new ApiError("This needs your password again: confirm it with POST /api/session/confirm.", ApiErrors.PasswordRequired)`.
  - `public static void RequirePasswordWindow(RequestCaller caller)` in `Security.cs`, throwing the 401 above; used inline by the decision (allow only), the admin user update (activate or role change, after its own password check) and invitations (after the admin invitation's password check).
  - The marker on exactly: `POST /api/keys`, `POST /api/connections`, `PUT /api/connections/{id}/vaults`, `POST /api/vaults/{vault}/pairing`, `GET /api/vaults/{vault}/export`, `GET /api/export`, `GET /api/vaults/{vault}/checkpoints/{name}/export`.
  - Files ZIP: `withPassword = await users.AsksPasswordForDownloadsAsync(...) || !export.Caller.PasswordWindowOpen`.
  - `PasswordGivenAsync` after each successful password check in: `ExportApi.TicketAsync`, `RegenerateRecoveryKeyAsync`, `ReencryptAsync`, `SetPreferencesAsync` (only when it checked a password), `RequirePasswordAsync`, `DeleteUserAsync`, `RepeatDeletionsAsync`, `NoteApi.DeleteVaultAsync`, `NoteApi.RekeyVaultAsync`.

- [ ] **Step 1: Write the enumeration tests** in `PasswordWindowTests.cs`, reading `EndpointDataSource` as `HistoryApiTests.cs:343–348` does, each endpoint as `"{HttpMethodMetadata.HttpMethods[0]} {RoutePattern.RawText}"` (if a group's empty pattern prints as `/api/connections/`, fix the string, not the set):

```csharp
[Fact] Exactly_these_endpoints_need_the_password_window()
// Assert.Equal(the seven above, sorted ordinally, guarded endpoints sorted ordinally).
[Fact] Every_guarded_endpoint_is_session_only()
// every endpoint with PasswordWindowMetadata also has SessionOnlyMetadata.
[Fact] Every_session_only_endpoint_is_guarded_or_deliberately_open()
// Every SessionOnlyMetadata endpoint is either guarded or a key of this dictionary (value: the reason, kept in the test):
//   reads: GET /api/session, /api/keys, /api/admin/users, /api/admin/deletions, /api/admin/invitations, /api/account/preferences,
//          /api/connections, /api/oauth/requests/{id}, /api/vaults/{vault}/access, /api/search/earlier, /api/changes,
//          /api/vaults/{vault}/activity — "reading: the session reads anyway";
//   cleaning up: DELETE /api/session, DELETE /api/keys/{id}, DELETE /api/admin/invitations/{id}, DELETE /api/connections/{id},
//          POST /api/sessions/end-others — "revoking and signing out are never guarded";
//   own password each time: POST /api/password, /api/recovery-key, /api/account/delete, /api/account/reencrypt,
//          /api/admin/users/{id}/delete, /api/admin/deletions/repeat, /api/vaults/{vault}/delete, /api/vaults/{vault}/rekey,
//          /api/export/ticket, POST /api/session/confirm — "asks for the password itself, which opens the window";
//   PUT /api/account/preferences — "turning the download password off needs the password; on is safe";
//   POST /api/vaults — "a new empty vault reaches nothing new";
//   PUT /api/vaults/{vault}/embeddings — "deliberate exception, security-architecture.md";
//   GET and POST /api/files/{vault}/zip — "guarded inline: the ticket when the window is closed";
//   POST /api/oauth/requests/{id}/decision, PATCH /api/admin/users/{id}, POST /api/admin/invitations — "guarded inline: only granting needs the window".
// A new session-only endpoint fails this test until someone decides.
```

- [ ] **Step 2: Write the behaviour tests** in the same class: a timed `PublicApi`, a `RestClient`-style remembered user (admin where needed) with vault `Arbeit`; "closed" = 31 idle minutes after sign-in, "open" = right after `POST /api/session/confirm`. A theory over the rows below asserts, closed: `401` and `type == "password-required"`, and the effect absent (listed); open: the success status.

| Request (body) | Closed: also assert | Open |
|---|---|---|
| `POST /api/keys` `{ name, vaults: { Arbeit: "read" } }` | `GET /api/keys` lists none | 201 |
| `POST /api/connections` `{ name, vaults: { Arbeit: "read" } }` | `GET /api/connections` lists none | 201 |
| `PUT /api/connections/{id}/vaults` `{ vaults: { Arbeit: "write" } }` (connection made while open) | still `read` | 200 |
| `POST /api/oauth/requests/{id}/decision` `{ allow: true }` (request from `OAuthFlow.StartAsync`) | `GET /api/oauth/requests/{id}` still 200 | 200, redirect with `code` |
| `POST /api/vaults/Arbeit/pairing` `{}` | — | 201 |
| `POST /api/admin/invitations` `{ role: "user" }` (admin) | `GET /api/admin/invitations` lists none | 201 |
| `PATCH /api/admin/users/{id}` `{ active: true }` (admin, another user deactivated first) | the user still inactive | 204 |

And, with the window closed, these pass without a prompt: the decision with `allow: false` (request gone afterwards); `PATCH /api/admin/users/{id}` `{ active: false }` (204); `PATCH` `{ role: "admin", password: <right> }` (204, and the window is open afterwards); `POST /api/admin/invitations` `{ role: "admin", password: <right> }` (201).
| `GET /api/vaults/Arbeit/export` | — | 403 without a ticket, 200 with one |
| `GET /api/export?vault=Arbeit` | — | 403 without a ticket, 200 with one |
| `GET /api/vaults/Arbeit/checkpoints/{name}/export` (checkpoint made first) | — | 403 without a ticket, 200 with one |

and these facts:

```csharp
[Fact] The_window_closed_by_12_hours_since_the_password_guards_too()
// touch every 20 min up to 12 h → POST /api/keys 401 password-required; confirm → 201.
[Fact] A_normal_session_is_never_asked()
// unticked sign-in; +29 min → POST /api/keys 201 (no type in any answer).
[Fact] Api_keys_and_connections_still_get_403_on_guarded_endpoints()
// bearer API key → POST /api/keys 403 with the session-only sentence, no type.
[Fact] A_forged_origin_is_refused_before_the_window_is_asked()
// closed window, Origin https://evil.test → 403, not 401.
[Fact] The_files_zip_needs_the_ticket_while_the_window_is_closed()
// preference off (PUT /api/account/preferences with password, while open); +31 min; GET /api/files/Arbeit/zip?path=… → 403
// with the ticket sentence; with a ticket → 200; right after, without a ticket → 200 (the ticket's password opened the window).
// The same for POST /api/files/Arbeit/zip as a form.
[Fact] The_files_zip_without_the_preference_and_with_the_window_open_needs_no_ticket()
[Theory] Every_password_check_opens_the_window(string action)
// closed window; one of: export ticket, new recovery key, preferences off, vault rekey, vault delete (a second vault),
// admin user deletion, repeated deletions (admin); right password → afterwards POST /api/keys 201.
[Fact] A_wrong_password_on_a_ticket_leaves_the_window_closed()
[Fact] Switching_embeddings_is_not_guarded()
// closed window; PUT /api/vaults/Arbeit/embeddings with the local provider → 200.
```

- [ ] **Step 3: Run them and see them fail**

Run: `dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~PasswordWindowTests"`
Expected: build errors (no `PasswordWindowMetadata`), then failures.

- [ ] **Step 4: Implement** the interfaces above; the XML docs of `UseCallerRules` and of `ExportApi` say what the marker does and why the ZIP is guarded inline.

- [ ] **Step 5: Run them and see them pass** (same command), then `dotnet test --solution Kumbu.slnx`. Expected: PASS (4 skips as usual).

- [ ] **Step 6: Regenerate the schema** (`ApiError.type`); `pnpm -C web build` passes.

- [ ] **Step 7: Commit** "Ask a remembered session for the password before it makes a new way in"

---

### Task 4: The password prompt in the web app

**Files:**
- Create: `web/src/state/passwordPrompt.ts`, `web/src/components/PasswordPrompt.tsx`
- Modify: `web/src/api/client.ts` (new middleware, `sessionEnds`), `web/src/router.tsx` (`Root` mounts `<PasswordPrompt />`)
- Test: `web/src/api/client.test.ts`, `web/src/components/PasswordPrompt.test.tsx` (create)

**Interfaces:**
- Produces:
  - `usePasswordPrompt` (zustand): `{ open: boolean; ask(): Promise<boolean>; settle(confirmed: boolean): void }`. `ask` returns the one pending promise while the dialog is open (two guarded requests share one dialog); `settle` resolves it and closes.
  - `isPasswordRequired(response: Response): Promise<boolean>` (exported from `client.ts`): 401 and `type === "password-required"` in a clone of the body.
  - Middleware `passwordWindow`, registered **after** `sessionEnds` and `versionWatch` (openapi-fetch runs `onResponse` in reverse order, so it sees the answer first): `onRequest` keeps `request.clone()` for non-GET requests by `id`; `onResponse`, for a password-required answer with a kept clone: `if (await usePasswordPrompt.getState().ask()) return globalThis.fetch(clone)` (once: the repeated answer is returned as it is), else the original answer; the clone is dropped in `onResponse` and `onError`.
  - `sessionEnds` ignores a password-required 401.
  - `PasswordPrompt`: `ConfirmTyped` with `password`, title "Confirm your password", text "It's a while since you last gave your password on this device. Give it again to go on.", action "Confirm"; on submit `call(api.POST("/api/session/confirm", { body: { password } }))` then `settle(true)`; an error shows `errorText(e)` in the dialog (the server's "The password is wrong." and the 429's sentence); close/cancel `settle(false)`. The password is cleared whenever the dialog closes.

- [ ] **Step 1: Write the failing tests**

`client.test.ts`:
```ts
it("keeps the session when the password dialog is cancelled")
// POST /api/keys → 401 { error, type: "password-required" }; settle(false) → call() rejects with HttpError 401; useSession user unchanged, ended false.
it("repeats a guarded request once after the password is confirmed")
// first POST /api/keys 401 password-required, settle(true), second answers 201 → call() resolves with its data; fetch called twice
// for /api/keys, the second with the same body.
it("does not ask a second time when the repeated request is refused again")
// both answers 401 password-required → one ask, rejects with HttpError 401.
it("asks once for two guarded requests and repeats both")
// two POSTs at once → usePasswordPrompt.ask called twice but one dialog (open once), both repeated after settle(true).
it("still ends the session on a plain 401")
// existing behaviour, with the new middleware registered.
```
`PasswordPrompt.test.tsx` (render `<PasswordPrompt />` with `fakeApi`):
```ts
it("confirms the password and lets the request go on")   // POST /api/session/confirm 204 → settle(true), dialog gone
it("says so in the dialog when the password is wrong")    // 400 { error: "The password is wrong." } → text shown, still open
it("shows the wait of a rate limit")                      // 429 { error: "Too many attempts; try again in a minute." } → shown
it("cancels the action on Cancel")                       // settle(false), no confirm request sent
```

- [ ] **Step 2: Run and see them fail:** `pnpm -C web test --run src/api/client.test.ts src/components/PasswordPrompt.test.tsx`. Expected: FAIL (modules missing).

- [ ] **Step 3: Implement** the interfaces above.

- [ ] **Step 4: Run and see them pass** (same command), then `pnpm -C web test --run` and `pnpm -C web build`. Expected: PASS.

- [ ] **Step 5: Commit** "Ask for the password in one dialog and repeat the guarded request"

---

### Task 5: Sign-in box, files ZIP and "last active"

**Files:**
- Modify: `web/src/routes/SignIn.tsx`, `web/src/attachments/GalleryPage.tsx:175–189` and its `ZipDialog:445–470`, `web/src/routes/admin/Users.tsx:133`, `web/src/note/AgentBanner.tsx:30–33, 49–53` (doc comments only: "previous visit")
- Test: `web/src/routes/SignIn.test.tsx`, `web/src/attachments/GalleryPage.test.tsx`, `web/src/routes/admin/Users.test.tsx`

**Interfaces:**
- Consumes: `SignInRequest.remember`, `SessionView.passwordWindowOpen`, `AdminUserView.lastActiveAt` (Task 2), the ticket rule (Task 3).
- Produces:
  - Sign-in: a checkbox "Keep me signed in on this device", unticked, under the password; ticked, a warning below it in the danger colour the form uses for `FormError` (as `role="note"`, not an alert): "Comfortable, but less safe: anyone who gets into this device or browser gets into your notes until you sign out or 30 days are up. Use it only on a device of your own, with a screen lock." The body sends `remember`.
  - Gallery: before a ZIP, ask both afresh: preferences (as today) and `call(api.GET("/api/session"))` (not through the `["session"]` cache); `ask = askPasswordForDownloads || !passwordWindowOpen`; either fails → ask. `ZipDialog` gets `reason: "setting" | "window"`; for `"window"` the text is "They come as one ZIP. It's a while since you last gave your password on this device, so it confirms the download."
  - Admin list: "Last active {date}" / "Never signed in" from `lastActiveAt`.

- [ ] **Step 1: Write the failing tests**

```ts
// SignIn.test.tsx
it("offers to keep the user signed in, unticked, and warns only when ticked")
it("sends remember: true when the box is ticked")      // POST /api/session body has remember true; unticked: false
// GalleryPage.test.tsx
it("asks for the password for a ZIP when the window is closed, though the setting is off")
// preferences { askPasswordForDownloads: false }, session { passwordWindowOpen: false } → dialog with the "window" text;
// confirm → POST /api/export/ticket, then the form carries the ticket.
it("downloads a ZIP without asking when the setting is off and the window open")
// Users.test.tsx: existing "Last sign-in 20 Oct 2026" assertion becomes "Last active 20 Oct 2026" with lastActiveAt.
```

- [ ] **Step 2: Run and see them fail:** `pnpm -C web test --run src/routes/SignIn.test.tsx src/attachments/GalleryPage.test.tsx src/routes/admin/Users.test.tsx`.

- [ ] **Step 3: Implement** as above.

- [ ] **Step 4: Run and see them pass**, then `pnpm -C web test --run` and `pnpm -C web build`.

- [ ] **Step 5: Commit** "Offer to keep the user signed in and ask for the password for a ZIP when needed"

---

### Task 6: Security documentation

**Files:**
- Modify: `docs/security-architecture.md` (§6 "Sign-in and sessions" `:297–309`; §4 threat table rows "Lost or stolen laptop" `:190` and a new row; §5.6 `user_slots` row `:279` and the `users` row `:278` "last and previous visit"; §5.5 "Who can choose" `:264`: embeddings deliberately not guarded), `README.md` (one sentence where sessions are described, if they are)

- [ ] **Step 1:** §6 gets the remembered session: the box, 30 days / 7 idle, the normal window (30 min idle, 12 h since the password), what is guarded (the list and the rule from the spec, the marker `PasswordWindowMetadata` checked in `Api/Security.cs`, the enumeration test), the 401 `password-required`, `POST /api/session/confirm` under the password-check limit, the cookie's `Max-Age` of 12 hours or 30 days, visits, and that every way of ending sessions ends remembered ones. File/line references to the code as built.
- [ ] **Step 2:** The threat table row **Stolen cookie of a remembered session** — can: read and write the user's vaults for up to 30 days (7 idle), until sign-out, a password change or "sign out other sessions"; cannot, once 30 minutes idle or 12 hours have passed since the password was last given: create keys, connections or pairings, widen rights, export. With the reasoning: no new kind of access, a longer window, no persistence beyond it. Update "Lost or stolen laptop" to point at it.
- [ ] **Step 3:** `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run`, `pnpm -C web build`; all green. Commit "Document remembered sessions and the password window".

---

## Self-review notes

- Spec coverage: checkbox and warning (T5); 30 d / 7 d and the window (T1); window behaviour and guarded actions (T3); dialog, repeat once, cancel, wrong password, rate limit (T4); normal sessions unchanged (T1 `A_normal_session_is_stored_and_limited_as_before`, T3 `A_normal_session_is_never_asked`); confirm endpoint with rate limit and key in force (T2); cookie `MaxAge` (T2); touch moves both clocks, background neither (T1); every way of ending sessions (T1, T2); `PrepareKeepAsync` keeps the flag (T1); logs (T2); visits for banner and admin list (T1, T2, T5); re-keying resumed at the first request (T2); password checks open the window (T3); files ZIP ticket and `GET /api/session` field (T2, T3, T5); embeddings not guarded (T3, T6); security docs (T6).
- Names used across tasks: `PasswordWindowOpen` (caller, view), `OpenWindowAsync`, `PasswordGivenAsync`, `PasswordWindowMetadata`, `ApiErrors.PasswordRequired`, `lastActiveAt`, `usePasswordPrompt`.
