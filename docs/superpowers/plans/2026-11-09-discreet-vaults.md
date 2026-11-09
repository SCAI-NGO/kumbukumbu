# Discreet vaults: out of sight in the web app until a PIN shows them — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A user marks vaults as discreet; in the web app only, they then don't exist until a separate PIN shows them for a chosen time. The server hides them; agents, sync, WebDAV, keys, devices and connections are untouched.

**Architecture:** The reveal is state on the web session's slot (`user_slots`, kind `session`), modelled on the remembered session's password window. `Sessions.ResolveAsync` hands it, with the user's sealed list of discreet vault ids, through `RequestCaller.ForSession` into `UserCredential`, which opens every vault as today and leaves the discreet ones out while the session hasn't revealed them. Every web endpoint that reads vaults through the credential then behaves as if they didn't exist. The few sources that don't go through the credential (found by a survey of the code, Decision 11) get their own filter by the hidden ids. A PIN verifier (Argon2id, own salt) and the sealed list live on the user document; the list is resealed in the user-key rotation's transaction. The web app adds "More vaults…", the PIN dialog, the amber frame and pill, a Settings section, hiding again over the socket, and a neutral URL `/discreet` done by a router `window` of its own.

**Tech Stack:** C# / .NET 10, ASP.NET Core minimal APIs, MongoDB, xUnit v3 on Microsoft.Testing.Platform; React 18 + React Router 6.30 + TanStack Query + zustand + Vite + Vitest. No new NuGet or npm package.

**Spec:** `docs/superpowers/specs/2026-11-09-discreet-vaults-design.md` (approved; do not edit). **Brief:** `docs/superpowers/overnighter/2026-11-09-discreet-vaults-brief.md`; its open points 1–11 are *OP n* below.

## Plan review

A fresh subagent reviewed brief, spec and the draft against the code (React Router 6.30.6 / `@remix-run/router` 1.23.4 source included). All 19 findings were accepted. **Where a ruling below and a task's text disagree, the ruling wins**; each implementer gets the rulings for its task.

1. **Task 12, hiding again clears everything in memory:** every TanStack query is removed except `["session"]` (then refetched), `useConsole.getState().reset()` (`web/src/console/store.ts`), `useSyncStatus` `editors` cleared, the editor store's discreet state forgotten; each tested.
2. **Task 9, `df`'s total:** `OwnFigures` (`Kumbu.Core`) gains `long? Total`; `Df.cs` prints it when set instead of the row sum; `ShellStorageFigures` sets it from `figure.ByVault` (all owned vaults) for a session only, so keys and MCP print exactly as before (tested over MCP).
3. **Task 5, the tenth entry:** `AccountLimits`' internal test constructor gains `pinChecksPerUser`; the tenth-entry test uses a factory replacing the `AccountLimits` singleton with a high limit. The rate-limit test uses the real limit.
4. **Task 3:** `ForSession` takes a trailing optional `SessionDiscreet? discreet = null` (nothing hidden), so the ~12 existing test call sites keep compiling.
5. **Test filters:** use `*InvitationApiTests`, `*AliasApiTests`, `*MembershipApiTests`, `*VaultEventTests`, `*MemberViewTests`, `*SkillsVaultClashTests`, `*SkillsVaultCreationTests`; never `*SharingApiTests` or `*SkillsVaultsTests`. Check a filter selects tests (`ls tests/Kumbu.Server.Tests/**`) before relying on it.
6. **Task 8 also filters `GET /api/skills`' `without`** (`SkillsApi.ListAsync:170-174`): a key or connection covering only hidden vaults isn't named; tested.
7. **Task 1, `SetAsync`:** the write is also conditional on the user's `KeyId == UserCatalog.KeyId(userKey, userId)`; with another key in force it refuses (`CredentialsChangedException`), never writes a list under an old key; tested.
8. **Task 10, the socket, made simpler and stricter:** the grant records whether the ticket was issued during a reveal (`TicketGrant.Revealed`, `RevealUntil`). Hide (✕), a time-out, and a check that finds the session no longer revealed send `{"type":"hidden"}` and then **close** every socket of that session whose grant was revealed, with a new code `4412` (`Hidden`), which the client treats as "reconnect at once": the new ticket comes from the filtered credential. The hub arms a timer at `RevealUntil` (via `TimeProvider.CreateTimer`) so a time-out acts at once, not at the 30 s check; the regular check is the backstop. `RunAsync`'s first recompute checks the state too. A mark (`DiscreetChanged`) closes, with `4412` and no `hidden` frame, every socket of the user's sessions that holds the vault and is not revealed now, and for revealed ones records nothing more: their grant is revealed, so they close at the end anyway. No per-vault `Discreet` flag is needed on `TicketVault`.
9. **Phase 1 alone is safe:** Task 8 (lists, consent, export name, Skills' `without`) and Task 10 (socket) move into phase 1, after Task 6; phase 2 keeps Tasks 7 and 9. Task 5's hide already calls the hub (Task 10 fills it in before the phase gate).
10. **Task 13, late save:** when the let-through write doesn't answer 2xx, the marker is put back (conditional on its absence). A tab waking after the time-out does its late save **before** it calls `hide`. `SessionDiscreet` carries `LateSave` and `RevealUntil` for `CallerMiddleware`.
11. **Task 12, a failed save on hiding:** the draft of a discreet note isn't dropped: kept in memory out of view (not in the visible `draft`) and retried once within the late-save window; a test covers the offline case.
12. **Task 14, neutral URL details:** `globalHistory.pushState(state, "", history.createHref(location))` uses the history's own `createHref`, so the custom `history.pushState/replaceState` rewrite the URL themselves; `router.createHref` is wrapped (assigned before `RouterProvider` renders) for links. History state: the custom history keeps `idx` and `key` in the real `history.state` and moves `usr` (the app puts vault paths there through `LightboxState`: `TreeDrawer:499`, `GalleryPage:170`, `Lightbox:100`, `TrashSheet`, `ActivitySheet`, `ArchiveSheet`, `HistorySheet`) into memory for discreet entries, served back through its `state` getter. The custom window forwards `document`, `sessionStorage`, `addEventListener`/`removeEventListener` (`popstate`, `pagehide`), `location.origin`, `href`, `assign`, `replace`, with methods bound to the real window. When a vault becomes discreet while its URL is the current entry, that entry is replaced. The auto-update reload (`web/src/update/apply.ts`) waits while a discreet view is open.
13. **Decision 11, order of checks:** delete and rekey get the hidden ids passed into `Deletion.DeleteVaultAsync`/`Rekeying.RekeyVaultAsync`/`Rekeying.AccessAsync` (and the invitation create), so "not found" is decided where `FindForUserAsync` is today, after confirm and password; the "whichever" wording is dropped.
14. **Task 3, comparing with `Nowhere`:** compare status and body after replacing the vault's name by the other one.
15. **Task 3 coverage:** add tags, earlier search, `/api/files`, links/backlinks, `/api/publications` and `/api/vaults/X/publications`, and `semsearch` and `publish ls` in the console; and writes (not only reads) by a device key and a WebDAV share while hidden.
16. **Decision 15, the export name:** a discreet vault's ZIP is named `Kumbu² export {date}.zip`, the existing several-vaults form and date. The README's limits say that the download's URL still names the vault in the browser's downloads list.
17. **Small points:** PIN pattern `\A[0-9]{4,6}\z`; the mark endpoint's 404 is the literal sentence the store gives for a vault the session doesn't have (find it: `RequireVaultAsync` / `ApiErrors`), the same for a hidden and a missing vault; the PIN field is `type="password"` with `inputmode="numeric"`.
18. **Tests:** "Never after 11 hours" uses a remembered session (or touches the session every 25 minutes); `FaultAt("before-apply")` is made one-shot in the conflict test.
19. **Accepting an invitation to a hidden vault she is already a member of** answers as for a vault she isn't a member of would fail neutrally: 409 "This name can't be used here." is wrong here, so it answers the existing "already a member" only for a visible vault and, for a hidden one, the invitation's generic refusal used for an invitation that is no longer valid (find it in `VaultInvites`), so it tells nothing. Tested in Task 9.

## Global constraints

- No new NuGet or npm package. `web/src/api/schema.d.ts` is edited by hand, as generated output would have it.
- British English in code, comments, docs, commits. New C# and TypeScript files start with `// SPDX-License-Identifier: AGPL-3.0-or-later`. Match the surrounding idiom and comment density.
- No organisation, customer or employer names; examples use Alice, Bob, Carol, `Work`, `Garten`, `Tagebuch`, `Familie`.
- **Never log** a PIN, a verifier, a password, a vault key or a discreet vault's name. Log ids and exception types only. No secret in a test assertion message.
- Agents, API keys, device keys, connections, WebDAV shares, MCP, `sh` over MCP and sync behave exactly as before, also for a discreet vault; no stored grant is ever dropped. Only `UserCredential` built by `RequestCaller.ForSession` filters; `WelcomeVault`, `SkillsVaults` and every other internal opener keep seeing every vault.
- Every answer around the PIN is the same whether or not a PIN or discreet vaults exist.
- Time through the injected `TimeProvider`. Test clocks start at the real time: `new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))` (MongoDB's TTL monitor sweeps slots made at a past fake time). Wait for a condition, never a fixed delay.
- Server tests: `KUMBU_TEST_MONGO='mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true'`, container `kumbu-test-mongo` (`mongo:7.0`, replica set `rs0`, `--ulimit nofile=20000:20000`), `TESTCONTAINERS_RYUK_DISABLED=true`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`. Node 26 at `/opt/node26/bin` (prefix `PATH`). The coordinator's env script: `source /tmp/claude-0/-home-user-kumbukumbu/d59089d8-1237-54d1-8e6d-6b8d23a85c31/scratchpad/env.sh`.
- Test filter: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*ClassName"` (repeatable; `FullyQualifiedName~` doesn't work, nor a glob in mid-name). Web: `pnpm -C web test --run <file>`. The web's type check is `pnpm -C web build`.
- Server API tests follow `tests/Kumbu.Server.Tests/Api/RestClient.cs` (`RestClient.NewAsync(app.PublicApi, vault: …)`) or the `SendAsync` pattern of `Api/RememberedSessionTests.cs`, in `[Collection("app")]` with `KumbuAppFactory`; a fake clock through `WithWebHostBuilder` replacing `TimeProvider` as `RememberedSessionTests.Timed()` does. A new test class goes into `tests/Kumbu.Server.Tests/Discreet/`.
- Commit after each task, imperative free-form message ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. **Subagents never push, merge or switch branches**; the coordinator pushes `feature/discreet-vaults`.
- Mutations: after the commit, apply each listed mutation alone, run the named test, see it fail, restore with `git checkout -- <file>`, and report each with the failing test's name.
- Don't touch the chunker or `semsearch`'s output format (another run's work); `semsearch` follows the vault list through the store anyway.

## Decisions (settling the open points)

Each is logged in the report and the pull request under "Decisions made without Chris".

1. **The reveal on the session slot (OP 1).** `UserSlotDocument` gains `revealedAt` (`DateTime?`), `revealUntil` (`DateTime?`; null with `revealedAt` set means Never, "until the mode ends"), `pinEntries` (`int`, `BsonIgnoreIfDefault`) and `lateSave` (`byte[]?`, Task 13). Revealed now = `revealedAt != null && (revealUntil == null || now < revealUntil)`. Sign-out deletes the slot, so the reveal ends with the session; a new session starts hidden. `Sessions.ResolveAsync` returns a fifth element `SessionDiscreet Discreet` (`record SessionDiscreet(bool Revealed, DateTime? Until, byte[]? SealedList)`; the sealed list read from the user document `LiveAsync` already loads). The kept session of a rotation (`PrepareKeepAsync`) changes only `wrapped`, so its reveal survives.
2. **The sealed list (OP 2).** `UserDocument.discreetVaults` (`byte[]?`): a JSON array of vault ids, sealed with `VaultCrypto.Seal(VaultCrypto.Derive(userKey, "kumbu/v1/discreet-vaults", UTF8(userId)), json, $"discreet-vaults:{userId}")`, as `SkillsNotice` is. It keeps ids of vaults that are gone. Marks use a conditional update on the exact bytes read (or the field's absence), retried up to 3 times. **A list that doesn't open** (only database tampering can cause it, since rotation and recovery reseal it) **counts every vault but the vault Skills as discreet**: the web app shows less, and the user, after the PIN, sees all of them marked and unmarks what she wants; the next mark writes a fresh list. Logged by exception type only.
3. **Rotation and recovery (OP 2a).** `PreparedRotation` gains `DiscreetList` (`ListReseal(byte[]? Old, byte[]? New)`), prepared in `UserKeyRotation.PrepareAsync` and written in `ApplyAsync` only where `discreetVaults` is still `Old` (absent for null), else `RotationConflictException`. Recovery uses the same `PrepareAsync`/`ApplyAsync` (`UserCatalog.cs:350`), so it is covered and tested separately. A list that didn't open with the old key is left as it is.
4. **The PIN verifier (OP 3).** `UserDocument.discreetPin`: `{ salt (16 bytes), verifier (32 bytes) }`, `verifier = Argon2Kdf.DeriveAsync(pin, salt)` with the server's `Argon2Kdf` (the same as passwords, `UserCatalog`'s `kdf`). Checking compares with `CryptographicOperations.FixedTimeEquals`. No PIN set: the KDF runs on the given PIN with a process-wide dummy salt against a random dummy verifier. A PIN not matching `^[0-9]{4,6}$`: the KDF runs on `"000000"` with the dummy salt, and the answer is wrong. Never a key, never decrypts anything. The check returns an enum `PinMatch { None, Reveal }`, so a second PIN (the 0.5.0 kill switch) can be added without changing callers.
5. **Counting entries (OP 4).** `Sessions.PinEnteredAsync(secret)` increments `pinEntries` atomically (`FindOneAndUpdate`, `ReturnDocument.After`); the tenth (`PinEntryLimit = 10`) deletes the slot. The reveal endpoint then answers exactly as any request of an ended session does (401, the cookie cleared), after `hub.SubjectsEndedAsync`. Every account, normal or remembered session, PIN or not. A right PIN doesn't reset the count.
6. **Rate limit (OP 4a).** `AccountLimits.PinCheck(string userId)`, its own limiter of `SignInsPerUsername` (5) per minute, keyed by user id. Over it: `ApiErrors.TooMany(retryAfter)`, "Too many attempts; try again in a minute.", for everyone. Order in the endpoint: rate limit, then the count, then the KDF.
7. **Routes (OP 5).** All in a new `src/Kumbu.Server/Api/DiscreetApi.cs`, session only (`SessionOnlyMetadata`), with `ApiErrors.Filter`:
   - `POST /api/account/discreet/pin` `{ password, pin }` → 204. `limits.PasswordCheck`, `users.CheckPasswordAsync`, `WrongPasswordException("The password is wrong.")`, `AccountApi.PasswordGivenAsync`, as `ExportApi.TicketAsync` (`ExportApi.cs:116-124`). A PIN not of 4 to 6 digits: 400 "A PIN has 4 to 6 digits." (checked after the password).
   - `POST /api/account/discreet/reveal` `{ pin, minutes }` (`minutes`: 5, 10, 30, 60 or null for Never; anything else 400 "Choose 5, 10, 30 or 60 minutes, or never.", checked first, before the limit) → 200 `DiscreetView { revealed: true, until }`; wrong PIN, no PIN, malformed PIN: 400 `{"error":"You have no discreet vaults."}` (an `ApiException`, not `WrongPasswordException`), byte-identical.
   - `POST /api/account/discreet/hide` → 204, always (revealed or not).
   - `PUT /api/account/discreet/vaults/{vault}` `{ discreet }` → 204. Not revealed: 404 with exactly the sentence a session gets for a vault it doesn't have (`caller.ReadOnlySentence` for an unknown name: "you have no vault named {vault}"), whether or not the vault exists. The vault Skills: 400 "The vault Skills can't be discreet."
   - `GET /api/session` gains `discreet: { revealed, until }` (`DiscreetView`), nothing else; `GET /api/vaults`' items gain `discreet: bool` (true only for a discreet vault, so only ever while revealed).
8. **The socket (OP 6).** `TicketVault` gains `Discreet` (set from the credential when the ticket is issued during a reveal). `Sessions.AliveAsync` stays; a new `Sessions.SocketStateAsync(subject)` answers `(string? UserId, bool Revealed)` by the same rules; the hub's check of a session subject uses it, and `RecomputeAsync` drops the connection's `Discreet` vaults while the session isn't revealed and then sends that connection `{"type":"hidden"}`. `POST …/hide` calls `hub.DiscreetHiddenAsync(subjectId)`: every connection of that session subject gets `{"type":"hidden"}` and is recomputed at once. A right PIN calls `hub.DiscreetRevealed(subjectId)`: the session's connections get `{"type":"revealed"}`, so other tabs ask for the session and the vault list again (and so a new ticket). Marking calls `hub.DiscreetChanged(userId, vaultId, discreet)`, which sets the flag on every session connection of the user holding the vault, so a vault marked during the mode is dropped at its end, and the next check drops it from the user's sessions on other devices that aren't revealed. The new frames carry no vault; `useSyncSocket.ts`'s `onFrame` handles types without one.
9. **Keys, shares, connections (OP 7).** Connections: `PUT /api/connections/{id}/vaults` merges the stored grants on hidden vaults into the edit (phase 1, Task 4); "A connection needs at least one vault" judges the merged set. Lists (`GET /api/keys`, `/api/dav-shares`, `/api/connections`): an item whose every vault is hidden is not listed (a device key's name carries its vault's name, `Pairings.cs:48`); one covering both kinds lists its visible vaults only. Revoking stays whole. The consent page leaves hidden vaults out and keeps the grants; Allow issues the code as today (spec §4, §6).
10. **Neutral URL (OP 8).** `createBrowserRouter(routes, { window: neutralWindow })` with a `window` of our own (`web/src/discreet/neutralWindow.ts`): its `history.pushState/replaceState` write `/discreet` into the browser for a URL whose vault is discreet (routes `/notes/:vault/*`, `/board/:vault`, `/gallery/:vault`, `/view/:vault/*`, `/checkpoints/:vault/…`), and keep the real URL in a `Map` in memory keyed by the entry's router key; its `location` returns the real URL from that map while the address bar shows `/discreet`. `router.createHref` is wrapped so every `href` of such a URL renders as `/discreet`. Nothing goes into `history.state` but React Router's own key and index. Reloading `/discreet` (empty memory, or the reveal over) redirects to `/`. `Root`'s `next=` is `/` for a discreet path and for `/discreet`. Discreet names come from the vault list (`discreet` flag) in a zustand store, in memory only.
11. **Sources outside the credential (survey, OP 7, 9, 10).** Phase 1 (Task 3) closes the ones that act on or answer about a hidden vault by name or id without the credential: `GET /api/vaults/{vault}/access`, the owner's membership endpoints (`SharingApi.OwnVaultIdAsync`), `POST /api/vaults/{vault}/delete`, `…/rekey`, `…/invitations`, `PUT /api/memberships/{id}/alias`, `POST /api/memberships/{id}/leave` and `…/provider-seen`: a hidden vault answers exactly as a vault name (or membership id) the user doesn't have. Phase 2 does notices, events, Skills notice, lists, consent, export name, `df`, names and the socket.
12. **Late save (OP 8a).** While revealed, the tab tells the server which discreet note is open: `PUT /api/account/discreet/open-note` `{ vault, path }` (null vault clears). The server stores it on the slot as `lateSave`, sealed under a key derived from the user key (no clear name or path in the database), with the vault's id. When the reveal ended by time-out, `CallerMiddleware` lets exactly one `PUT /api/notes/{vault}/{**path}` of that very note through, from that session, until `revealUntil + 5 min`: the marker is removed atomically first (conditional on its bytes), and that one request's credential includes the vault. ✕ (`hide`) clears the marker: the tab saved before. A reveal clears it too.
13. **Skills (OP 9).** The vault Skills can't be marked. The session's Skills notice is left out while it names a hidden vault: its `NewName`, normalised (`VaultCatalog.NormaliseVaultName`), equal to a hidden vault's or alias's name. `MakeRoomAsync`'s naming is unchanged: it still counts hidden names as taken, so a new name can skip one ("Skills (renamed 2)"). Changing that would change the naming: left out, recorded in `docs/superpowers/open-items.md`.
14. **Names (OP 10).** Creating, renaming a vault, renaming an alias, and accepting an invitation with an alias that clashes with a hidden vault's (or alias's) name answer 409 "This name can't be used here." The clash with a visible vault keeps "You already have a vault named '…'.". The invitation's peek reports such an alias as free (the clash is only met on accept), so the peek tells nothing.
15. **Export.** Only reachable for a shown vault; while revealed, a ZIP containing a discreet vault (vault export, several vaults, a checkpoint, files of a vault) is named `kumbu-export-<yyyy-MM-dd>.zip` (local date as the other names), never the vault's name. While hidden, the export of a hidden vault answers as an unknown vault (credential).
16. **`df`.** `allOwned` treats the hidden owned vaults as owned (`ShellStorageFigures.cs:30`), so the sentence doesn't change; the totals stay the true totals (spec §3.4). `GET /api/account/storage` stays the true total. The admin's counts stay as they are.
17. **Wrong-PIN status.** 400 with the `ApiError` shape. The right PIN 200 with `DiscreetView`.
18. **The time-out choice** in `localStorage` `kumbu.discreetTimeout`: `"5"`, `"10"`, `"30"`, `"60"` or `"never"`; default `"10"`, read and written inside try/catch.

---

## Task 0: Baseline

**Files:** none changed.

- [ ] **Step 1:** Fresh `kumbu-test-mongo`; run `dotnet restore --locked-mode`, `dotnet build --configuration Release --no-restore`, `dotnet test --configuration Release --no-build`; `pnpm -C web install --frozen-lockfile && pnpm -C web test --run && pnpm -C web build`; `pnpm -C pdf install --frozen-lockfile && pnpm -C pdf test && pnpm -C pdf build`.
- [ ] **Step 2:** Compare with the brief's known state (.NET total 5333, skipped 4, no failures expected at `a4c899b`). A failing known-flaky class is run alone three times and recorded.

---

## Phase 1: the server — PIN, reveal, credential filter, the single answer

### Task 1: The sealed list and the PIN verifier

**Files:**
- Create: `src/Kumbu.Server/Accounts/DiscreetVaults.cs`
- Modify: `src/Kumbu.Server/Accounts/UserDocuments.cs:139-145` (after `SkillsNotice`: `DiscreetVaults`, `DiscreetPin`; new `PinVerifierDocument` class with `[BsonElement("salt")] byte[] Salt`, `[BsonElement("verifier")] byte[] Verifier`)
- Modify: `src/Kumbu.Server/Program.cs` (register `DiscreetVaults` as singleton next to `UserCatalog`)
- Test: `tests/Kumbu.Server.Tests/Discreet/DiscreetVaultsTests.cs`

`DiscreetVaults(IMongoDatabase db, Argon2Kdf kdf, ILogger<DiscreetVaults> log)`:
- `public const string NoneSentence = "You have no discreet vaults.";`
- `internal static IReadOnlySet<string>? OpenList(string userId, byte[] userKey, byte[]? sealedList)`: empty set for null; null when it doesn't open or doesn't parse (`CryptographicException`, `JsonException`).
- `internal static byte[] SealList(string userId, byte[] userKey, IEnumerable<string> ids)` (sorted, distinct, ordinal).
- `public async Task SetAsync(string userId, byte[] userKey, string vaultId, bool discreet, IReadOnlyCollection<string> allIds, CancellationToken ct)`: reads `discreetVaults`, opens it (a list that doesn't open starts from `allIds`, Decision 2), adds or removes, writes conditionally on the bytes read (`Eq` or `Exists(false)`), 3 attempts, then `InvalidOperationException`.
- `public async Task SetPinAsync(string userId, string pin, CancellationToken ct)`: `ArgumentException("A PIN has 4 to 6 digits.")` unless `^[0-9]{4,6}$`; new 16-byte salt, `kdf.DeriveAsync(pin, salt)`, `Set(u => u.DiscreetPin, …)`.
- `public async Task<PinMatch> CheckPinAsync(string userId, string pin, CancellationToken ct)`: Decision 4; exactly one KDF run in every case.
- `public static bool IsPin(string? pin)`.

Steps:
- [ ] **Step 1: Red.** Tests (MongoFixture or `KumbuAppFactory.NewDatabase()`, `new Argon2Kdf(new Argon2Parameters(8, 1, 1))` for speed): a sealed list opens to its ids and not with another user key nor another user id; null opens to empty; garbage opens to null; `SetAsync` adds and removes and keeps other ids; two concurrent `SetAsync` on different vaults both land (run 10 times); an unreadable list plus `SetAsync(…, discreet:false, allIds)` gives `allIds` minus that vault; `SetPinAsync` refuses `"123"`, `"1234567"`, `"12a4"`, `"١٢٣٤"` (Arabic-Indic digits); `CheckPinAsync` is `Reveal` for the right PIN and `None` for a wrong one, no PIN, and a malformed one, and `kdf.Runs` grows by exactly 1 in each of the four cases; the stored document has no field holding the PIN in clear (the verifier differs from the PIN's UTF-8 bytes). Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*DiscreetVaultsTests"`: FAIL (build errors count as red).
- [ ] **Step 2: Green.** Implement; same command PASS.
- [ ] **Step 3: Commit** "Keep a sealed list of discreet vaults and a PIN verifier on the user".
- [ ] **Step 4: Mutations** (same command, each must fail): (a) skip the KDF when no PIN is set; (b) `CheckPinAsync` compares with `SequenceEqual` on a verifier from another salt (use the dummy salt for a set PIN); (c) `SetAsync` writes unconditionally (the concurrency test fails); (d) `OpenList` returns an empty set for an unreadable list.

### Task 2: The reveal on the session slot

**Files:**
- Modify: `src/Kumbu.Server/Accounts/UserDocuments.cs:149-208` (`UserSlotDocument`: `RevealedAt`, `RevealUntil`, `PinEntries`, `LateSave`, each `BsonIgnoreIfNull`/`IfDefault`)
- Modify: `src/Kumbu.Server/Accounts/Sessions.cs` (`ResolveAsync` :105-144 returns `SessionDiscreet`; new `RevealAsync`, `HideAsync`, `PinEnteredAsync`, `PinEntryLimit = 10`, `SocketStateAsync`; `LiveAsync` unchanged in its rules)
- Modify: every caller deconstructing `ResolveAsync` (`Auth/CallerMiddleware.cs:115-119`; `grep -rn "ResolveAsync(" src tests`)
- Test: `tests/Kumbu.Server.Tests/Discreet/RevealTests.cs`

Behaviour:
- `public sealed record SessionDiscreet(bool Revealed, DateTime? Until, byte[]? SealedList);` (in `Sessions.cs`).
- `RevealAsync(string secret, TimeSpan? duration, CancellationToken ct)`: on the live slot of the secret: `revealedAt = now`, `revealUntil = now + duration` or unset, `lateSave` unset. Returns the `until` written.
- `HideAsync(string secret, CancellationToken ct)`: unsets `revealedAt`, `revealUntil`, `lateSave`.
- `PinEnteredAsync(string secret, CancellationToken ct)`: Decision 5; returns `true` when the session was ended by it (and its user id via an out-like tuple `(bool Ended, string? UserId)`).
- `SocketStateAsync(string subject, CancellationToken ct)`: `(string? UserId, bool Revealed)` by `LiveAsync`'s rules.

Steps:
- [ ] **Step 1: Red.** Tests with a fake clock (Global constraints) against `Sessions` directly (`app.Services`) or a timed factory: a new session resolves `Revealed == false`; after `RevealAsync(…, 5 min)` it is revealed with `Until == now+5min`, and at +5 min not; Never (`null`) stays revealed after 11 hours (normal session) until `HideAsync`; `HideAsync` ends a timed reveal at once; a session started after another's reveal starts hidden; the 9th `PinEnteredAsync` keeps the session, the 10th ends it (resolves null) for a normal and for a remembered session; a reveal doesn't reset the count; a kept session of a user-key rotation (`UserKeyRotation.RotateAsync` with `keepSessionSecret`) is still revealed afterwards; `SealedList` carries the user document's `discreetVaults` bytes. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*RevealTests" --filter-class "*SessionTests" --filter-class "*RememberedSessionTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command PASS.
- [ ] **Step 3: Commit** "Record a reveal of discreet vaults on the web session".
- [ ] **Step 4: Mutations:** (a) `now <= revealUntil` instead of `<` (the +5 min test fails); (b) `PinEntryLimit` 11; (c) `RevealAsync` resets `pinEntries`; (d) `HideAsync` unsets only `revealUntil` (a Never reveal then stays revealed: the Never-then-hide test fails).

### Task 3: Leave discreet vaults out of the web session's credential

**Files:**
- Modify: `src/Kumbu.Server/Vaults/Credentials.cs:25-44` (`UserCredential`)
- Modify: `src/Kumbu.Server/Auth/RequestCaller.cs:142` (`ForSession` takes `SessionDiscreet`), `src/Kumbu.Server/Auth/CallerMiddleware.cs:115-119`
- Modify: `src/Kumbu.Server/Api/NoteApi.cs` (`ListVaultsAsync`'s `VaultView` gains `Discreet`; `AccessAsync` :398, `DeleteVaultAsync` :379-391, `RekeyVaultAsync` :410-421 resolve the vault through `caller.OpenVaultsAsync` first)
- Modify: `src/Kumbu.Server/Sharing/SharingApi.cs` (`OwnVaultIdAsync` :143, `CreateAsync` invitations :105, alias :245, leave :235, provider-seen :267)
- Test: `tests/Kumbu.Server.Tests/Discreet/HiddenVaultTests.cs`

Behaviour:
- `UserCredential(string userId, byte[] userKey, DiscreetFilter? discreet = null)`; `public sealed record DiscreetFilter(bool Revealed, IReadOnlySet<string>? Ids)` (`Ids` null: the list didn't open, Decision 2). `OpenAsync` opens all (`OpenForUserAsync`), keeps them as `_all`, and `_opened` = all minus the hidden ones (discreet and not revealed; Skills never hidden). New members: `bool IsDiscreet(string vaultId)` (marked, whether revealed or not; for an unreadable list every vault but Skills), `IReadOnlyList<VaultAccess> Hidden` (after `OpenAsync`; empty before), `IReadOnlySet<string> HiddenIds` (the list's ids while not revealed, gone vaults included, plus for an unreadable list every opened id but Skills), `bool Revealed`. `Level`/`Access` answer from `_opened` only.
- `RequestCaller` gains `internal UserCredential? Session => Credential as UserCredential;` and `internal async Task<bool> HidesAsync(string vaultId, CancellationToken ct)` (opens first; false for any non-session caller).
- `ForSession(User, byte[], DateTime, bool, SessionDiscreet, VaultCatalog)` opens the sealed list with `DiscreetVaults.OpenList` and passes the filter.
- Every endpoint of Decision 11, phase 1 part, answers a hidden vault (or a membership of one) **exactly** as a name or id the user doesn't have: same status, same body. Implement by resolving through `caller.OpenVaultsAsync` (owned access with `Membership is null` where the endpoint wants an own vault) before the existing service call, or by checking `caller.HidesAsync(id)` right after `FindForUserAsync` and treating a hidden id as not found, whichever keeps the existing order of checks (the password check stays where it is).

Steps:
- [ ] **Step 1: Red.** Tests: Alice (session) owns `Work`, `Tagebuch`; Bob shares `Familie` with her (use the sharing helpers of `tests/Kumbu.Server.Tests/Sharing`); mark `Tagebuch` and `Familie` by writing `discreetVaults` with `DiscreetVaults.SealList` straight into the user document. While hidden: `GET /api/vaults` lists `Work` and Skills only; tree, note read, note write, search (`/api/search?q=…`), `/api/changes`, trash, history, activity, archive, board, gallery files, checkpoints of `Tagebuch` answer as for `Nowhere` (a name she never had: compare status and body); `POST /api/console` `ls /` lists no `Tagebuch`, `rg <word in Tagebuch>` finds nothing, `history` names nothing; `GET /api/vaults/Tagebuch/access`, membership GET with a made-up id on `Tagebuch`, delete and rekey with the right password, invitations with the right password, alias rename and leave of `Familie`'s membership id: each equal to the answer for `Nowhere` (or an unknown membership id). After `Sessions.RevealAsync` on her session: `GET /api/vaults` lists all with `discreet: true` on `Tagebuch` and `Familie`, the note reads. An unreadable list (random bytes) hides `Work` and `Tagebuch` too but not Skills. **Agents untouched:** an API key for `Tagebuch` reads and writes it over MCP (`app.ConnectAsync(key)`, `cat`/`write` tools), a device key (`Pairings` redeem, see `PairingApiTests`) reads `/api/vaults/Tagebuch/versions`, a WebDAV share for `Tagebuch` lists it (`PROPFIND /dav/…`, see `Dav/` tests), all while her session is hidden. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*HiddenVaultTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command PASS; then `--filter-class "*NoteApiTests" --filter-class "*SharingApiTests" --filter-class "*MembershipApiTests" --filter-class "*ConsoleApiTests" --filter-class "*RekeyingTests" --filter-class "*DeletionTests"` PASS.
- [ ] **Step 3: Commit** "Hide discreet vaults from a web session until it reveals them".
- [ ] **Step 4: Mutations:** (a) `_opened = _all` (no filter); (b) `Revealed` ignored (always hide); (c) an unreadable list counts as empty; (d) drop the check in `OwnVaultIdAsync`; (e) drop it in `AccessAsync`; (f) filter in `ForApiKeyAsync` too (the MCP test fails) — each alone, same command.

### Task 4: Keep hidden grants when a connection is edited

**Files:**
- Modify: `src/Kumbu.Server/Api/ConnectionApi.cs:224-233` (`SetVaultsAsync`), `src/Kumbu.Server/Accounts/Connections.cs:246-280` (`SetVaultsAsync` takes `IReadOnlySet<string> keep`: vault ids whose stored grant, level and publish right stay as stored; "A connection needs at least one vault" (`:250`) judges the merged set)
- Test: `tests/Kumbu.Server.Tests/Discreet/HiddenGrantsTests.cs`

Steps:
- [ ] **Step 1: Red.** A connection for `Work` (write) and `Tagebuch` (read, May publish if possible); hide `Tagebuch`; `PUT /api/connections/{id}/vaults` naming only `Work` at read: afterwards the stored connection (read through `Connections.ListAsync` with the unfiltered user key, or the connection's token over MCP) still has `Tagebuch` at read with its publish right, and `Work` at read. A connection for `Tagebuch` only, edited while hidden with `{ vaults: {} }`: the merged set holds `Tagebuch`, so the edit succeeds and keeps it; with a visible vault named, the hidden grant is kept beside it. Revoking the connection while hidden revokes it whole (its token fails over MCP). Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*HiddenGrantsTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command and `--filter-class "*ConnectionApiTests" --filter-class "*ConnectionTests"` PASS.
- [ ] **Step 3: Commit** "Keep a connection's grants on hidden vaults when it is edited".
- [ ] **Step 4: Mutations:** (a) `keep` empty; (b) judge "at least one vault" on the edit alone.

### Task 5: The PIN, reveal, hide and mark endpoints

**Files:**
- Create: `src/Kumbu.Server/Api/DiscreetApi.cs` (Decision 7), mapped in `Program.cs` next to `MapAccountApi`
- Modify: `src/Kumbu.Server/Api/Security.cs:330-400` (`_pinCheckByUser`, `PinCheck`)
- Modify: `src/Kumbu.Server/Api/AccountApi.cs:30` (`SessionView` gains `DiscreetView Discreet`; `Session` :333 and `SignInAsync` fill it; sign-in: not revealed)
- Modify: `web/src/api/schema.d.ts` (paths and schemas: `DiscreetView`, `SetPinRequest`, `RevealRequest`, `MarkDiscreetRequest`, `OpenNoteRequest` later in Task 13, `SessionView.discreet`, `VaultView.discreet`)
- Test: `tests/Kumbu.Server.Tests/Discreet/DiscreetApiTests.cs`

The hub calls of Decision 8 (`DiscreetRevealed`, `DiscreetHiddenAsync`, `DiscreetChanged`) are added as no-op stubs on `SyncHub` here and filled in Task 10.

Steps:
- [ ] **Step 1: Red.** Tests: set a PIN with the right password (204), wrong password (400 "The password is wrong.", and a remembered session counts it), a 3-digit PIN (400); without a password field (400 wrong password); reveal with the right PIN: 200, `revealed: true`, `until` = now + minutes (fake clock), `GET /api/session` says the same, `GET /api/vaults` lists the discreet ones; `minutes: null` → `until: null`; `minutes: 7` → 400; **byte-identical**: the raw body and status of a wrong PIN for a user with PIN and discreet vaults, a user with PIN and none, a user without PIN, and `"12a4"` are equal (`Assert.Equal` on strings and status codes) and equal `{"error":"You have no discreet vaults."}` modulo the `ApiError` shape; the same six requests per minute give the same 429 at the sixth for a user with and one without a PIN, with the same body; the tenth entry (right PIN) ends the session: 401 and later requests 401; hide → `GET /api/session` not revealed and vaults hidden; marking while hidden: 404 equal to the body for `Nowhere`, also for an existing visible vault; marking Skills while revealed 400; marking `Tagebuch` while revealed then hiding: hidden; unmarking while revealed then hiding: visible; `GET /api/session` while hidden says `{"revealed":false,"until":null}` for a user with PIN and one without, byte-identical. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*DiscreetApiTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command PASS; `--filter-class "*AccountApiTests" --filter-class "*RememberedSessionTests"` PASS; `pnpm -C web build` PASS.
- [ ] **Step 3: Commit** "Set a PIN and show or hide discreet vaults with it".
- [ ] **Step 4: Mutations:** (a) answer a missing PIN with "You have no PIN." (identity test fails); (b) skip the KDF for a malformed PIN (assert through the app's `Argon2Kdf` singleton's `Runs`, which grows by one per entry in all four cases); (c) rate-limit only users with a PIN; (d) count only wrong PINs; (e) let marking pass while hidden.

### Task 6: Reseal the list on key rotation and recovery

**Files:**
- Modify: `src/Kumbu.Server/Accounts/UserKeyRotation.cs` (`PreparedRotation.DiscreetList`, `PrepareAsync` :124-170, `ApplyAsync` :240-273)
- Test: `tests/Kumbu.Server.Tests/Discreet/DiscreetRotationTests.cs`

Steps:
- [ ] **Step 1: Red.** Alice marks `Tagebuch`; "Re-encrypt my vaults" (`POST /api/account/reencrypt` with the password, keeping her session): afterwards the list opens with the new user key to the same ids, and her session still hides `Tagebuch`; a recovery (`POST /api/recover`) then a new sign-in: `Tagebuch` still hidden; a mark written between prepare and apply (use `FaultAt("before-apply")` to write a new list) makes the rotation conflict and, retried, keeps the new mark; a user without a list rotates as before (no field written). Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*DiscreetRotationTests" --filter-class "*UserKeyRotationTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command PASS; `--filter-class "*RekeyingTests" --filter-class "*UserCatalogTests"` PASS.
- [ ] **Step 3: Commit** "Reseal the list of discreet vaults when the user key changes".
- [ ] **Step 4: Mutations:** (a) don't reseal (the list no longer opens and counts as unreadable, hiding `Work` too: the test asserts `Work` visible); (b) apply unconditionally (conflict test fails).

### Phase 1 gate

- [ ] Full verification as CI (Global constraints): `dotnet restore --locked-mode; dotnet build --configuration Release --no-restore; dotnet test --configuration Release --no-build`; web install/test/build; pdf install/test/build. Counts against Task 0. Push. From here, a hidden vault is absent from every response that goes through the credential.

---

## Phase 2: the other sources of spec §3.4

### Task 7: Member notices, vault events and the Skills notice

**Files:**
- Modify: `src/Kumbu.Server/Sharing/SharingApi.cs:302-330` (member notices list and dismiss), `:226` (vault events), `src/Kumbu.Server/Sharing/VaultEvents.cs:176` (`OfMemberEverywhereAsync` takes `IReadOnlySet<string> hidden`)
- Modify: `src/Kumbu.Server/Api/AccountApi.cs:333-339` (`Session`: the Skills notice left out while it names a hidden vault, Decision 13); `SignInAsync` (new session: hidden, so the same rule)
- Test: `tests/Kumbu.Server.Tests/Discreet/HiddenNoticesTests.cs`

Steps:
- [ ] **Step 1: Red.** Bob deletes `Familie` (a notice for Alice) and re-keys another shared vault `Garten` (Bob's, shared) marked discreet by Alice: while hidden, `GET /api/member-notices` names neither; dismissing such a notice answers as an unknown notice; `GET /api/vault-events` holds no event of `Familie`'s or `Garten`'s membership; revealed: both appear. A Skills notice whose `NewName` is `Skills (renamed)` (write it with `SkillsVaults`' seal, or rename a vault named `Skills` before the Skills vault is ensured, as `SkillsVaultsTests` does) for a vault Alice then marks: absent from `GET /api/session` while hidden, present revealed. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*HiddenNoticesTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command and `--filter-class "*SharingApiTests" --filter-class "*SkillsVaultsTests"` PASS.
- [ ] **Step 3: Commit** "Leave hidden vaults out of notices and events".
- [ ] **Step 4: Mutations:** (a) no filter on notices; (b) no filter on events; (c) Skills notice compared without normalising (`skills (renamed)` alias case, test it).

### Task 8: Access lists, OAuth consent and the export's name

**Files:**
- Modify: `src/Kumbu.Server/Api/AccountApi.cs:452-460` (`ListKeysAsync`), `:484-490` (`ListDavSharesAsync`), `src/Kumbu.Server/Api/ConnectionApi.cs:205-210` (list), `:173-185` (consent view)
- Modify: `src/Kumbu.Server/Api/ExportApi.cs` (zip names, Decision 15: `ZipName`, `CheckpointZipName`, files zip)
- Test: `tests/Kumbu.Server.Tests/Discreet/HiddenAccessListsTests.cs`

Steps:
- [ ] **Step 1: Red.** While hidden: a key for `Tagebuch` only, a device key of `Tagebuch` (its name holds "Tagebuch"), a share for `Tagebuch` only and a connection for `Tagebuch` only are not listed; a key for `Work` and `Tagebuch` is listed with `Work` only; the response bodies contain no "Tagebuch" anywhere (`Assert.DoesNotContain("Tagebuch", body)`); the consent view of a connection for `Tagebuch` and `Work` lists `Work` only, of one for `Tagebuch` only lists no vault but the name; revoking a hidden-only key by id still works (204) and the key stops working. Revealed: all listed as before. Export while revealed: `GET /api/vaults/Tagebuch/export` (with a ticket) has `Content-Disposition` without "Tagebuch", `kumbu-export-…zip`; `Work`'s export keeps its name. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*HiddenAccessListsTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command plus `--filter-class "*ApiKeyTests" --filter-class "*DavShareApiTests" --filter-class "*ConnectionApiTests" --filter-class "*ExportApiTests"` PASS.
- [ ] **Step 3: Commit** "Leave hidden vaults out of the access lists and the consent page, and name discreet exports neutrally".
- [ ] **Step 4: Mutations:** (a) list hidden-only keys with an empty map; (b) keep the vault name in the export file name; (c) drop the consent filter.

### Task 9: `df` and name clashes

**Files:**
- Modify: `src/Kumbu.Server/Accounts/ShellStorageFigures.cs:25-31`
- Modify: `src/Kumbu.Server/Api/NoteApi.cs` (create :370, rename :304), `src/Kumbu.Server/Sharing/SharingApi.cs` (alias :245, peek :117, accept :131), `src/Kumbu.Server/Sharing/VaultInvites.cs:124-160` (peek's `AliasFree` true for a hidden clash)
- Create: a helper `internal static class HiddenNames` in `src/Kumbu.Server/Api/VaultNames.cs` (`public const string Refused = "This name can't be used here."`; `bool ClashesWithHidden(UserCredential, string name)` by `VaultCatalog.NormaliseVaultName`)
- Test: `tests/Kumbu.Server.Tests/Discreet/HiddenNamesTests.cs`

Steps:
- [ ] **Step 1: Red.** With a storage limit set for Alice (as `QuotaApiTests` does): `df` in the console prints the same sentence with `Tagebuch` hidden as for a user with only `Work` and Skills (no "other vaults count" line); creating `tagebuch` (case differs), renaming `Work` to `Tagebuch`, renaming a visible shared vault's alias to `Tagebuch`, accepting an invitation with alias `Tagebuch`: 409 "This name can't be used here."; the same against the visible `Work`: the existing "You already have a vault named 'Work'."; the peek with alias `Tagebuch` reports free. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*HiddenNamesTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command plus `--filter-class "*QuotaApiTests" --filter-class "*NoteApiTests" --filter-class "*SharingApiTests"` PASS.
- [ ] **Step 3: Commit** "Answer a name of a hidden vault neutrally and keep df's sentence".
- [ ] **Step 4: Mutations:** (a) the old message for a hidden clash; (b) `allOwned` without the hidden ids.

### Task 10: The socket drops discreet vaults at the end

**Files:**
- Modify: `src/Kumbu.Server/Sync/SyncTickets.cs` (`TicketVault.Discreet`), `src/Kumbu.Server/Api/SyncApi.cs:56-86` (set it), `src/Kumbu.Server/Sync/SyncHub.cs` (`VaultState` carries `Discreet`; `CheckAsync` for a session uses `sessions.SocketStateAsync`; `RecomputeAsync` drops discreet vaults while not revealed and sends `{"type":"hidden"}`; `DiscreetHiddenAsync`, `DiscreetRevealed`, `DiscreetChanged` of Decision 8)
- Modify: `src/Kumbu.Server/Api/DiscreetApi.cs` (calls, if not already)
- Modify: `web/src/hooks/useSyncSocket.ts:26-34` (`onFrame` accepts `hidden` and `revealed` without `vault`; Task 12 acts on them)
- Test: `tests/Kumbu.Server.Tests/Discreet/DiscreetSocketTests.cs` (the helpers of `Api/SyncSockets.cs`)

Steps:
- [ ] **Step 1: Red.** A ticket asked while hidden holds no `Tagebuch` (`hub.Held`); a write to `Tagebuch` by an API key sends no `changed` frame for it to the hidden session's socket; revealed: a new ticket holds `Tagebuch` with `Discreet`, a write sends `changed`; `POST …/hide`: the socket gets `{"type":"hidden"}` and `hub.Held` no longer has `Tagebuch` for it; a timed reveal past its end (fake clock) and `hub.CheckAsync` drops it; a vault marked during the reveal is dropped at hide too; a second socket of the same session gets `hidden` as well; an API key's socket keeps `Tagebuch` throughout. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*DiscreetSocketTests"`: FAIL.
- [ ] **Step 2: Green.** Implement; same command plus `--filter-class "*SyncSocketTests" --filter-class "*SyncHubReviewTests" --filter-class "*PresenceTests"` PASS; `pnpm -C web test --run src/hooks/useSyncSocket.test.tsx` PASS.
- [ ] **Step 3: Commit** "Drop discreet vaults from the session's sockets when they are hidden again".
- [ ] **Step 4: Mutations:** (a) no `hidden` frame; (b) don't drop on the regular check; (c) drop for key subjects too (the key test fails).

### Phase 2 gate

- [ ] Full verification as CI. Counts. Push.

---

## Phase 3: the web app

### Task 11: "More vaults…" and the PIN dialog

**Files:**
- Create: `web/src/discreet/state.ts` (zustand, memory only: `revealed`, `until`, `names: Set<string>` from the vault list's `discreet`; `setFromSession`, `setFromVaults`), `web/src/discreet/api.ts` (`useReveal`, `useHide`, `useSetPin`, `useMarkDiscreet` with TanStack Query; reveal success invalidates `["session"]` and `["vaults"]`), `web/src/discreet/PinDialog.tsx`, `web/src/discreet/timeout.ts` (`kumbu.discreetTimeout`, Decision 18)
- Modify: `web/src/shell/VaultSwitcher.tsx:96-121` ("More vaults…" as the last `Action`, always), `web/src/router.tsx` (`RequireSession` feeds `discreet` state from the session)
- Test: `web/src/discreet/PinDialog.test.tsx`, `web/src/shell/VaultSwitcher.test.tsx`

The dialog (mockup 3A in words): title "More vaults", a PIN field (`inputmode="numeric"`, `autocomplete="off"`, 4–6 digits), "Hide again after" with 5 min, 10 min, 30 min, 60 min, Never (radio group or segmented buttons in the app's style), preselected from `localStorage`, and **Open**. A 400 shows "You have no discreet vaults." and clears the field; a 429 shows the server's sentence; success closes the dialog. The choice is written to `localStorage` on Open. Built on the existing dialog pattern (`useDialog`, `Sheet` or the form classes in `components/form.tsx`).

Steps:
- [ ] **Step 1: Red.** Tests: the menu ends with "More vaults…" with and without vaults and discreet state; the dialog preselects `30` when `localStorage` says `"30"`, `10` with nothing stored and with storage throwing; a wrong PIN (mocked 400) shows the sentence; Open sends `{ pin, minutes }` (null for Never) and stores the choice; nothing else is written to `localStorage` or `sessionStorage` (spy). Run `pnpm -C web test --run src/discreet src/shell/VaultSwitcher.test.tsx`: FAIL.
- [ ] **Step 2: Green.** Implement; same command PASS; `pnpm -C web build` PASS.
- [ ] **Step 3: Commit** "Ask for the PIN behind More vaults…".
- [ ] **Step 4: Mutations:** (a) hide the entry without discreet vaults; (b) ignore the stored choice; (c) store the PIN (storage spy fails).

### Task 12: Frame, pill, Settings, and hiding again

**Files:**
- Create: `web/src/discreet/DiscreetPill.tsx` ("👁 Discreet · 9 min · ✕", the eye as the lucide `Eye` icon if the app uses lucide icons for such marks; remaining minutes from `until`, none for Never; ✕ calls hide), `web/src/discreet/hideAgain.ts` (`hideAgain(queryClient, navigate)`: `beforeRead` of the open editor saves first, then for every discreet name `forgetVault`, `queryClient.removeQueries` for queries whose key contains it, the active vault becomes the last ordinary one, navigate to it (`/` if none); a timer per tab fires at `until` minus 15 s to save, and at `until` hides; the `hidden` frame and a 404 for a vault that was discreet do the same; the `revealed` frame invalidates `["session"]`, `["vaults"]`)
- Modify: `web/src/index.css` or the theme tokens file (`web/src/theme/`): two tokens `--color-discreet` and `--color-discreet-soft` (amber) in light and dark; `web/src/shell/TreeDrawer.tsx` (frame around the sidebar while revealed; the pill where the switcher sits, the switcher beside or below it as fits), `web/src/shell/MobileShell.tsx` (the same in the mobile layout), `web/src/hooks/useSyncSocket.ts` (`hidden`, `revealed`)
- Modify: `web/src/routes/Settings.tsx` (section "Discreet vaults", the same for everyone: "Set or change PIN" opens a dialog with the current password and the new PIN twice; while revealed, a list of the vaults with a "Discreet" switch each, Skills excepted)
- Test: `web/src/discreet/DiscreetPill.test.tsx`, `web/src/discreet/hideAgain.test.tsx`, `web/src/routes/Settings.test.tsx`

Steps:
- [ ] **Step 1: Red.** Tests: frame and pill shown only while revealed, with "9 min" for 9 minutes left (fake timers), no time for Never; ✕ calls hide, the open discreet note's pending edit is saved first (mock `beforeRead`), the store forgets the vault, and the view goes to `Work`; the timer saves before the end and hides at it; a `hidden` frame in a second tab's hook does the same; Settings shows "Set or change PIN" for a user with and without PIN alike, and the switches only while revealed, none for Skills; the set-PIN dialog sends the password and PIN; mobile: frame and pill in `MobileShell` at phone width (`useDesktop` false). Run `pnpm -C web test --run src/discreet src/routes/Settings.test.tsx src/shell/MobileShell.test.tsx`: FAIL.
- [ ] **Step 2: Green.** Implement; same command PASS; `pnpm -C web test --run` PASS; `pnpm -C web build` PASS. Look at light and dark mode and 390 px width with Playwright against `pnpm -C web dev` with mocked API if feasible (screenshots for the report), else note it as a hand test.
- [ ] **Step 3: Commit** "Show discreet mode with a frame and pill, and close everything when it ends".
- [ ] **Step 4: Mutations:** (a) hide before saving; (b) don't forget the vault in the store; (c) show the switches while hidden.

### Task 13: The late save

**Files:**
- Modify: `src/Kumbu.Server/Api/DiscreetApi.cs` (`PUT /api/account/discreet/open-note`), `src/Kumbu.Server/Accounts/Sessions.cs` (`SetLateSaveAsync`, `TakeLateSaveAsync`), `src/Kumbu.Server/Auth/CallerMiddleware.cs` (Decision 12), `src/Kumbu.Server/Vaults/Credentials.cs` (`DiscreetFilter.AlsoOpen` vault id)
- Modify: `web/src/discreet/api.ts`, `web/src/shell/MobileShell.tsx` (tell the open discreet note while revealed), `web/src/api/schema.d.ts`
- Test: `tests/Kumbu.Server.Tests/Discreet/LateSaveTests.cs`, web `web/src/discreet/hideAgain.test.tsx`

Steps:
- [ ] **Step 1: Red.** Server: revealed for 5 min, open-note `Tagebuch/a.md`; at +6 min (fake clock) one `PUT /api/notes/Tagebuch/a.md` succeeds, a second fails as unknown vault; `b.md` instead fails; at +11 min it fails; after ✕ it fails; open-note while hidden answers 204 and records nothing (no difference visible); the slot's `lateSave` doesn't contain "Tagebuch" or "a.md" in clear. Web: on opening a discreet note the tab sends open-note; on a sleep past the end (fake timers jump), the tab's save goes out once before hiding. Run `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*LateSaveTests"` and `pnpm -C web test --run src/discreet`: FAIL.
- [ ] **Step 2: Green.** Implement; same commands PASS.
- [ ] **Step 3: Commit** "Accept one late save of the open discreet note".
- [ ] **Step 4: Mutations:** (a) don't consume the marker; (b) no time limit; (c) store the path in clear.

### Task 14: The neutral URL

**Files:**
- Create: `web/src/discreet/neutralWindow.ts` (Decision 10; exports `neutralWindow`, `wrapCreateHref(router)`, `discreetUrl(pathname): boolean`), `web/src/discreet/neutralWindow.test.ts`
- Modify: `web/src/App.tsx:9` (router with `window: neutralWindow`, wrapped `createHref`), `web/src/router.tsx` (route `/discreet` → `<Navigate to="/" replace />` when memory has no entry; `Root`'s `next=`), `web/src/routes/SignIn.tsx:39-46` (never follows a `next` of `/discreet`)
- Test: `web/src/discreet/neutralWindow.test.ts`, `web/src/router.test.tsx` if it exists, else in the new test

Steps:
- [ ] **Step 1: Red.** Tests with `createMemoryRouter`-free setup: a fake `window` (jsdom's) and the neutral window: pushing `/notes/Tagebuch/a.md` while `Tagebuch` is discreet writes `/discreet` to `window.history` and `location` reads the real path; back and forward restore it from memory; `/notes/Work/a.md` passes through unchanged; `createHref` of a discreet note URL is `/discreet`; after `forget()` (hide) `/discreet` resolves to `/`; `history.state` holds no vault name (`JSON.stringify(history.state)` doesn't contain "Tagebuch"); a session that ends on `/discreet` or a discreet path goes to `/signin?next=%2F`; nothing reaches `localStorage`/`sessionStorage`; `document.title` stays "Kumbu²". Run `pnpm -C web test --run src/discreet/neutralWindow.test.ts`: FAIL.
- [ ] **Step 2: Green.** Implement; same command PASS; `pnpm -C web test --run` PASS; `pnpm -C web build` PASS.
- [ ] **Step 3: Commit** "Keep discreet paths out of the address bar, links and history".
- [ ] **Step 4: Mutations:** (a) pass discreet URLs through; (b) leave `createHref` unwrapped; (c) keep the memory on hide.

### Phase 3 gate

- [ ] Full verification as CI. Counts. Push.

---

## Phase 4: documentation and the security review

### Task 15: README, help texts, open items

**Files:**
- Modify: `README.md` (a section "Discreet vaults": what it does, the known limits of spec §6, that a full backup includes discreet vaults only while they are shown, that agents and synced devices see them as before)
- Modify: web help texts if the app has a help page or tooltips for Settings (`grep -rn "Ask for my password" web/src` to find the pattern)
- Modify: `docs/superpowers/open-items.md` (Decision 13's naming point)
- Steps: write; `pnpm -C web build`; commit "Describe discreet vaults and their limits".

### Task 16: Security review

- [ ] A fresh subagent reviews the whole branch (`git diff master...HEAD -- src web/src`) against spec §3, §5 and §6: leaks of a hidden vault's name (responses, frames, logs, URLs, storage, titles), differences between the PIN answers (status, body, timing paths, rate limit, counting), authentication and authorisation, rotation. Every finding goes into the report, fixed or not; fixes as tasks of their own with red test, green, commit.

### Task 17: Final gate, pull request, report

- [ ] Full verification as CI, plus CI's litmus job (WebDAV) if it can be run here (`.github/workflows/ci.yml`), else say so.
- [ ] Draft pull request, base `master`, title "Discreet vaults: out of sight in the web app until a PIN shows them"; body as the brief says. **Do not merge, tag or deploy; don't change the version.**
- [ ] `docs/superpowers/overnighter/2026-11-09-discreet-vaults-report.md` and the row in `docs/superpowers/open-items.md`, as the last commit; push; PushNotification.
