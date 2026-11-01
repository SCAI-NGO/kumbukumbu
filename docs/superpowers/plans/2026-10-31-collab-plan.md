# Vault Sharing ("Collab") Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An owner shares a vault with people she trusts as `read`, `write` or `maintain`, and can change or withdraw that at any moment with effect on the next request. Members' agents, devices and sessions work on the shared vault as on their own, under the member's alias, without ever learning who the other members are. Release 0.3.0.

**Architecture:** A new `memberships` collection holds the sharing graph in plain fields the server can read per request without a key. A member reaches a vault through a slot of the new kind `member`, wrapped to her P-256 public key (phase 2; a stand-in under her user key in phase 1), and her own API keys and connections get ordinary `key`/`connection` slots marked `GrantedVia: membershipId`. Every opening method of `VaultCatalog` (`OpenForUserAsync`, `OpenForApiKeyAsync`, `OpenConnectionSlotsAsync`) checks the memberships behind those slots in one batched query **before** `LoadAsync`, so the in-memory cache never stands in for authorisation. The opening methods return an access record per vault (vault, the caller's name for it, effective level, membership, `isOwner`), from which `EncryptedNoteStore` builds the caller's namespace and `ICaller.Level(vault)` answers. Levels become `Read < Write < Maintain < Owner`; `ICaller.Refuse` is the single choke point for `read-only` and `structural` refusals, each with a machine-readable `code`. Every stored writer label is accompanied by a sealed writer reference; members' writes store an empty label and only a per-membership writer id, and one server service (`WriterLabels`) renders writers per viewer. Re-keying wraps the new vault key only to public keys pinned, sealed under the vault key, at acceptance.

**Tech stack:** .NET 10, ASP.NET Core minimal APIs, MongoDB driver (transactions on a replica set), xUnit v3 with `FakeTimeProvider` and Testcontainers; `System.Security.Cryptography.ECDiffieHellman` (P-256) and the existing `VaultCrypto` (HKDF, AES-256-GCM); React 18, openapi-fetch, TanStack Query, Vitest.

**Spec:** `docs/superpowers/specs/2026-10-31-collab-design.md` (binding; "design §n" below). Requirements: `concepts/collab-concept.md` (the design wins on conflict). Brief: `docs/superpowers/overnighter/2026-10-31-collab-brief.md` (wins over the design on scope and process).

## Branch and pull request

- Branch **`feature/collab`** off the commit this run starts from. It stands in for the design's long-lived `v0.3.0` branch: **one draft pull request** from `feature/collab` to `master` for all phases ("Vault sharing (collab) for 0.3.0"), **never merged** in this run. No pull request per phase.
- No version bumps (`version.txt`, `Kumbu.Server.csproj`, `web/package.json`), no tags, no pushes to `master`.

## How to work

For every task, in this order:

1. **Red:** write the test(s) named in the task; run only them (`dotnet test tests/Kumbu.Server.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test tests/Kumbu.Core.Tests --filter …`, `pnpm -C web test --run <file>`) and see them fail for the stated reason (not a compile error elsewhere).
2. **Green:** the smallest change that passes them; then the class's whole test file and its neighbours.
3. **Commit** locally: imperative, free-form message ("Refuse structural files below maintain"), no Conventional Commits prefix, ending with the session's attribution lines.
4. **Mutations**, one at a time: make the breakage, run the task's tests, **see at least one fail**, then restore with `git checkout -- <file>` (back to the green commit). A mutation that survives means the test is too weak: strengthen it, amend the commit, repeat. Record each mutation and its outcome for the report.
5. **Push** (`git push -u origin feature/collab` the first time).

Tasks labelled **HARD RULE** pin one of the brief's five security rules. They have their own test class, are never weakened to make another task green, and their mutations are mandatory.

**Phase gate** (end of every phase): `dotnet test --solution Kumbu.slnx` all green (4 skipped expected); if the phase touched `web/`: `pnpm -C web test --run` and `pnpm -C web build`. Then update the security architecture as the phase's last task says, commit, push. If the run has to stop, stop at a phase boundary with everything green, or at least at a task boundary with everything green.

**Regenerating `web/src/api/schema.d.ts`** (generated, never edited by hand) after a task that changes REST shapes, against a server started for it:

```sh
Kumbu__Mongo__ConnectionString="${KUMBU_TEST_MONGO:-mongodb://127.0.0.1:27017/?directConnection=true}" \
Kumbu__Mongo__Database=kumbu-gen Kumbu__Search__Enabled=false ASPNETCORE_URLS=http://127.0.0.1:8099 \
  dotnet run --project src/Kumbu.Server --no-launch-profile > "$HOME/kumbu-gen.log" 2>&1 &
until curl -fsS http://127.0.0.1:8099/healthz >/dev/null; do sleep 1; done
KUMBU_OPENAPI_URL=http://127.0.0.1:8099/api/openapi.json pnpm -C web gen:api
kill %1
```

(With Testcontainers only, start a throwaway `mongo:7.0 --replSet rs0` container for it and `rs.initiate()` it.)

## Global constraints

- British English in code, comments, docs, UI copy and commit messages. `// SPDX-License-Identifier: AGPL-3.0-or-later` on new C# and TypeScript files. Match the surrounding comment density.
- No organisation, customer or employer names anywhere. People: Alice (owner), Bob and Carol (members). Vaults: `Gartenarbeit`, `Work` (existing tests use `Arbeit`; keep that where a test already does).
- No secrets in logs, assertions, test data or the report: tests compare invite secrets and keys by equality, never print them.
- Paths below: `src/Kumbu.Server/…`, `src/Kumbu.Core/…` (the design writes `Kumbu.Core/…` for the latter), `tests/Kumbu.Server.Tests/…`, `tests/Kumbu.Core.Tests/…`, `web/src/…`.

## Test fixtures to know

- **`tests/Kumbu.Server.Tests/MongoFixture.cs`** (`[Collection("mongo")]`): `fixture.NewDatabase()` gives a fresh database per test class or test.
- **`tests/Kumbu.Server.Tests/Vaults/VaultFixture.cs`**, class `VaultWorld`: users `u1`, `u2`, `user-lea` pre-inserted; `world.Catalog(time: …)` is "one server process" (a second call is a second process); `VaultWorld.Store(catalog, token)`; `P("/Work/a.md")`. Use it for catalog-level tests (`UserVaultTests`, `RekeyTests`).
- **`tests/Kumbu.Server.Tests/KumbuAppFactory.cs`** (`[Collection("app")]`): the whole server; `app.PublicApi` (public instance, cheap Argon2, per-test client IPs); `app.Database` to change MongoDB "behind the server's back"; `app.ConnectAsync(token)` for an MCP client.
- **`tests/Kumbu.Server.Tests/Api/RestClient.cs`**: `RestClient.NewAsync(app.PublicApi, vault: "Gartenarbeit")` = a new signed-in user with a vault; `.KeyAsync(name, KeyRight, kind)`, `.ShAsync(command, bearer)`, `.SendAsync(...)`, `.CreateAsync(path, content)`, `.ReadAsync(path)`, `RestClient.JsonAsync/ErrorAsync`. `.User`, `.UserKey`, `.Session` are public.
- **Core:** `tests/Kumbu.Core.Tests/Fakes/InMemoryNoteStore.cs` (`new InMemoryNoteStore(("/Work/a.md", "…"))`, `InMemoryNoteStore.Seeded`), `TestShell.Of(store, time)`, `FixedCaller(writer, canWriteVault, human)`; see `tests/Kumbu.Core.Tests/Notes/CallerTests.cs` for a shell per caller.
- Timed API tests: `app.PublicApi.WithWebHostBuilder(...)` replacing `TimeProvider` with a `FakeTimeProvider` starting at the real time in whole seconds (`tests/Kumbu.Server.Tests/Api/PasswordWindowTests.cs:34-40`).

**New in phase 1, used from then on** (task 1.3): `tests/Kumbu.Server.Tests/Sharing/Share.cs` with

- `Share.GrantAsync(VaultCatalog catalog, Memberships memberships, (string Id, byte[] Key) owner, string vaultId, (string Id, byte[] Key) member, string alias, MemberRole role)` → `membershipId`, for `VaultWorld` tests;
- `RestClient.ShareWithAsync(RestClient member, string alias, MemberRole role = MemberRole.Write)` → `membershipId` (an extension in the same file, resolving `Memberships` from `api.Services`), for API and MCP tests;
- `Share.SetStatusAsync(IMongoDatabase db, string membershipId, string status)` / `SetRoleAsync(...)`: a revocation or role change written straight to MongoDB, as a revoke route will do it in phase 3 (the atomic step is one `UpdateOne`, design §6.1).

## Decisions of the planner (where the design leaves the call to the builder)

1. **Where the code lives.** A new folder `src/Kumbu.Server/Sharing/` (`MembershipDocument.cs`, `Memberships.cs`, `MemberKeys.cs`, `SharingApi.cs`, `VaultEvents.cs`, `MembershipSweeper.cs`), tests in `tests/Kumbu.Server.Tests/Sharing/`. Catalog methods that touch slots go into a new partial `src/Kumbu.Server/Vaults/VaultCatalog.Members.cs`, beside `VaultCatalog.Rekey.cs`. The existing `Accounts/Invitations.cs` (account invitations) keeps its name; vault invitations are `Sharing/VaultInvites` methods on `Memberships`.
2. **Levels in Core.** `src/Kumbu.Core/Notes/AccessLevel.cs`: `enum AccessLevel { Read, Write, Maintain, Owner }` (ordered). `KeyRight` (server, persisted) gains `Maintain` and stays the credential's sealed right; `KeyRights.ToLevel` maps it.
3. **Refusal codes.** `src/Kumbu.Core/Sh/ShellResult.cs` gains `string? Code { get; init; }`; constants in `src/Kumbu.Core/Notes/Refusals.cs` (`ReadOnly = "read-only"`, `Structural = "structural"`, `Closing = "closing"`, `OwnerInactive = "owner-inactive"`). `ApiError` (`Api/ApiErrors.cs:11`) gains `code` beside the existing `type` (JsonIgnore when null); `type` stays for `password-required`.
4. **Refusal sentence for sessions.** "this key can only read vault X" stays for keys and connections; a member's session gets "you can only read vault X" (a session is not a key). Same `code`.
5. **Phase 1 member slots before keypairs exist.** The design gives phase 1 member slots but keypairs only in phase 2. In phase 1 a `member` slot wraps the vault key exactly as a `user` slot does (`UserKek(memberUserKey, vault)`, AAD `slot:{vault}:member`) under the member's lookup, so the existing rotation code (`PrepareUserSlotsAsync`) rewraps it unchanged. Phase 2 (task 2.3) replaces it by the public-key format with algorithm byte `1` and deletes the stand-in code; a test then shows a stand-in slot opens nothing. The branch is never released between phases, so no second format reaches a database outside tests.
6. **Member writer references come in phase 1, not 3.** Members can write from phase 1 on, so their writes must already store no name (hard rule 4) and render as "another user" (hard rule 5). Phase 0 builds the reference and `WriterLabels` with only "self" and "owner"; phase 1 adds the member columns of design §8.2's table.
7. **Owner-only gaps that open with phase 1.** Once members reach vaults through the open store, `PUT /api/vaults/{vault}/embeddings` gets its `isOwner` check in phase 1 (task 1.14), and creating a pairing code for a shared vault is refused until phase 5 brings the plugin check (task 1.14; lifted in 5.8). Member export works from phase 1 as the design wants; its owner event comes in phase 3.
8. **Writer kinds.** Today `writerKind: "user"` means the user themself (`Api/WriterKinds.cs:17`, `web/src/api/activity.ts` `writerText`). Design §8.2 uses `user` for "another user" and `self` for the viewer. Phase 0 renames: `self` (the viewer), `device`, `agent`, `server`, and from phase 1 `user` (another person). The web app is changed in the same task.
9. **"Last use on this vault"** (design §8.6, L5): nothing records it today. A `GrantedVia` slot gets a plain `usedAt`, set at most once a minute per slot by the opening methods (one `UpdateOne` with a `usedAt < now − 1 min` filter, fire and forget).
10. **Legacy rights in connection metas.** Design §3.3 names the API key's display copy (`Accounts/ApiKeys.cs:468`); connections seal rights text too (`ConnectionMeta`, `Accounts/Connections.cs:108,233`, read at `OAuth/OAuthTokens.cs:735`). Both metas get the same `v: 2` rule as slot payloads.
11. **Member limit setting:** `KumbuOptions.Vaults.MemberLimit` (default 20), `KUMBU_VAULT_MEMBER_LIMIT` mapped in `compose.yaml` and documented in `README.md`; pending limit 10 is a constant (`Memberships.PendingLimit`).
12. **The duplicate-name check** in `EncryptedNoteStore.VaultsAsync` throws `InvalidOperationException` (500, logged with vault ids only). It applies to every credential, the dev token included; a database that already has two dev-token vaults of one name fails loudly instead of hiding one (the safer reading of design §3.2).
13. **`security-architecture.md` §9 "What comes next"** keeps sharing until the release (design §12); this run updates §2, §3, §4, §5.6, §6 and §8 phase by phase and leaves §9 alone.

## Test counts (estimates)

Baseline as briefed: `dotnet test --solution Kumbu.slnx` 2703 passed, 4 skipped; web 74 files, 1128 tests. **Measured in this environment:** 2701 passed, 6 skipped (the two extra skips are `SeedScannerTests.Skips_unreadable_files_with_a_warning` and `ModelFilesTests.AReadOnlyModelFolder…`, which skip when run as root), against an external MongoDB (`KUMBU_TEST_MONGO`); web 74 files, 1128 tests. The estimates below are against the briefed baseline; here every dotnet figure is 2 lower, with 6 skipped.

| After phase | dotnet passed (est.) | web files / tests (est.) |
|---|---|---|
| 0 | 2703 + 104 = **2807** | 74 / 1128 + 63 = **1191** |
| 1 | 2807 + 84 = **2891** | 74 / **1191** (no web) |
| 2 | 2891 + 25 = **2916** | 74 / **1191** (no web) |
| 3 | 2916 + 108 = **3024** | 80 / 1191 + 38 = **1229** |
| 4 | 3024 + 22 = **3046** | 80 / 1229 + 3 = **1232** |
| 5 | 3046 + 27 = **3073** | 82 / 1232 + 14 = **1246** |

Skipped stays at 4 throughout. Per-task estimates are given as "**+n**" (theories count their cases).

---

## Phase 0 — Groundwork, no sharing yet

Each task is useful and testable in today's single-owner world (design §12, phase 0).

### Task 0.1 — Ordered levels with `maintain`

**Goal:** design §3.3: `KeyRight` ordered `Read < Write < Maintain`, text `read`/`write`/`maintain`; `AccessLevel` in Core.

**Files:**
- Modify: `src/Kumbu.Server/Accounts/ApiKeys.cs:13-29` (`KeyRight`, `KeyRights.ToText`, `KeyRights.Parse`, new `KeyRights.ToLevel(KeyRight)`)
- Create: `src/Kumbu.Core/Notes/AccessLevel.cs`
- Test: create `tests/Kumbu.Server.Tests/Accounts/KeyRightTests.cs` (plain xUnit, no fixture)

**Red:**
- `KeyRightTests.Levels_are_ordered_read_write_maintain`: `KeyRight.Read < KeyRight.Write < KeyRight.Maintain`; `AccessLevel.Maintain < AccessLevel.Owner`.
- `KeyRightTests.Maintain_round_trips_as_text` (`[Theory]` read/write/maintain): `Parse(ToText(r)) == r`.
- `KeyRightTests.An_unknown_right_parses_to_null` (`"admin"`, `"Write"`, `""`).

**Green:** add `Maintain` as the third enum member (numeric order is the order); `ToText` as a `switch`; `Parse` gains `"maintain"`; `ToLevel` maps one to one.

**Mutations:** (1) `ToText` maps `Maintain` to `"write"`; (2) swap `Write` and `Maintain` in the enum; (3) `Parse` case-insensitive.

**Tests:** +6.

### Task 0.2 — Payload version: legacy `write` reads as `maintain`

**Goal:** design §3.3 "Existing write keys become maintain": slot payloads `{vaultKey, rights, v: 2}` from now on; a payload without `v` and with `write` is `Maintain`. Decision 10: the same for the API key's and the connection's sealed metas.

**Files:**
- Modify: `src/Kumbu.Server/Vaults/VaultCatalog.cs:1155` (`ApiKeySlotPayload` gains `[JsonPropertyName("v")] int? V`), `:403-411` (`ApiKeySlotsAsync` writes `V = 2`), `:472-486` (`WrapConnectionSlots`), `:456-458` (`OpenConnectionSlotsAsync`) and `:527-528` (`OpenForApiKeyAsync`) read through a new `private static KeyRight RightOf(ApiKeySlotPayload p)`
- Modify: `src/Kumbu.Server/Accounts/ApiKeys.cs:468` (`ApiKeyMeta` gains `v`; `ListAsync` reads legacy `write` as `maintain`), `src/Kumbu.Server/Accounts/Connections.cs:108,233` (`ConnectionMeta` likewise), `src/Kumbu.Server/OAuth/OAuthTokens.cs:735`
- Modify: `src/Kumbu.Server/Vaults/Credentials.cs:63,84` (`CanWrite`: `Right >= KeyRight.Write`, not `== Write`)
- Test: `tests/Kumbu.Server.Tests/Accounts/ApiKeyTests.cs`, `tests/Kumbu.Server.Tests/Accounts/ConnectionTests.cs`

**Red:**
- `ApiKeyTests.A_legacy_write_slot_opens_as_maintain`: create a key with `Write`; rewrite its slot in MongoDB with a payload sealed as before 0.3.0 (no `v`; use a test helper `LegacySlot.Seal(apiKey, vaultId, keyId, vaultKey, "write")` in the test file that reproduces `ApiKeyKek`/`ApiKeySlotContext` via `VaultCrypto`); `OpenForApiKeyAsync` gives `KeyRight.Maintain`.
- `ApiKeyTests.A_new_write_slot_stays_write`: a new `Write` key opens as `Write` (payload has `v: 2`).
- `ApiKeyTests.A_maintain_key_may_write`: `ApiKeyCredential.CanWrite` true for a `Maintain` key.
- `ApiKeyTests.The_list_shows_a_legacy_write_key_as_maintain`: meta without `v` → `ListAsync` shows `Maintain`.
- `ConnectionTests.A_legacy_write_connection_slot_opens_as_maintain` and `…_meta_lists_as_maintain`.

**Green:** `RightOf`: `Parse(rights)`, and `Write` with `V is null` → `Maintain`. Every writer of a payload or meta sets `V = 2`.

**Mutations:** (1) `RightOf` ignores `V` (legacy stays `Write`); (2) new payloads omit `V` (a new `write` key becomes `maintain`); (3) `CanWrite` back to `== KeyRight.Write`; (4) the meta reader skips the legacy rule.

**Tests:** +6.

### Task 0.3 — Own devices pair as `maintain`

**Goal:** design §10.2: a device paired to an own vault gets `maintain` (it is the person in Obsidian), so a 0.1.x device never meets a structural refusal it can't read.

**Files:** Modify `src/Kumbu.Server/Accounts/Pairings.cs:138` (`KeyRight.Write` → `KeyRight.Maintain`). Test: `tests/Kumbu.Server.Tests/Api/PairingApiTests.cs`.

**Red:** `PairingApiTests.A_device_paired_to_an_own_vault_may_change_the_board`: pair, then `PUT` `/Gartenarbeit/Board/Board.md` with the device key → 200 (fails from task 0.5 on without this change; write the test now, it is green today and becomes the guard). Plus `…_key_is_listed_as_maintain`: `GET /api/keys` lists the device with `maintain` (red now).

**Green:** the one-line change.

**Mutations:** (1) back to `Write` (the list test and, after 0.5, the board test fail).

**Tests:** +2.

### Task 0.4 — `ICaller.Level(vault)`

**Goal:** design §3.3: `ICaller` gets `Level(vault)`; `CanWrite` stays as `Level >= Write`. Effective levels in today's world: session and dev token `Owner`; key or connection its sealed level.

**Files:**
- Modify: `src/Kumbu.Core/Notes/ICaller.cs` (`AccessLevel? Level(string vault)`; default `CanWrite(vault) => Level(vault) >= AccessLevel.Write`; `FixedCaller` gains `Func<string, AccessLevel?>? LevelOf` and keeps its `CanWriteVault` constructor by mapping it to `Write`/`Read`)
- Modify: `src/Kumbu.Server/Vaults/Credentials.cs` (`IRightsCredential.Level(string vault)` in place of `CanWrite`, fail-closed `null` before opening; the all-of-same-name rule kept as `Min`)
- Modify: `src/Kumbu.Server/Auth/RequestCaller.cs:64-66` (`Level`: no rights credential → `Owner`)
- Test: `tests/Kumbu.Core.Tests/Notes/CallerTests.cs`, `tests/Kumbu.Server.Tests/Auth/CallerTests.cs`

**Red:**
- Core `CallerTests.A_fixed_caller_reads_where_its_level_is_read` and `…_maintains_where_its_level_is_maintain` (CanWrite true, Level Maintain).
- Server `CallerTests.A_session_is_owner_of_its_vaults`, `CallerTests.A_key_has_the_level_of_its_slot` (read, write, maintain theory), `CallerTests.A_key_has_no_level_before_its_vaults_are_opened` (null, CanWrite false).

**Green:** as in Files; `RequestCaller.CanWrite` becomes `Level(vault) >= Write`.

**Mutations:** (1) `RequestCaller.Level` returns `Owner` for every caller; (2) `IRightsCredential.Level` returns `Max` instead of `Min` over same-named slots; (3) `CanWrite` default `>= Read`.

**Tests:** +7.

### Task 0.5 — Structural files: `StructuralFiles` and the structural refusal

**Goal:** design §3.3: `Refuse` refuses below `Maintain` for a structural path by any command, and for the commands `mkcp`, `rmcp`, `board create`, `board rename_column`. One static list.

**Files:**
- Create: `src/Kumbu.Core/Notes/StructuralFiles.cs` (`IsStructural(VaultPath)`: `BoardPaths.Definition(vault)`; `StructuralCommands = ["mkcp", "rmcp", "board create", "board rename_column"]`; `ContainsStructural(VaultPath folder)`)
- Create: `src/Kumbu.Core/Notes/Refusals.cs` (codes, decision 3)
- Modify: `src/Kumbu.Core/Sh/ShellResult.cs` (`Code`), `src/Kumbu.Core/Notes/ICaller.cs` (`Refuse`: read-only first, then structural, each with its `Code`; sentence "Only those who may maintain *{vault}* can change its board's columns." for the board, "…can make or remove its checkpoints." for `mkcp`/`rmcp`)
- Test: create `tests/Kumbu.Core.Tests/Notes/StructuralTests.cs`

**Red** (shell per caller as in `CallerTests.ShellFor`, a `write`-level `FixedCaller` and a `maintain`-level one; store with `/Work/Board/Board.md` from `BoardTemplate`):
- `StructuralTests.A_writer_is_refused_every_write_to_Board_md` (`[Theory]`: `write`, `edit`, `replace`, `rm`, upload via `AttachmentWriter` to `/Work/Board/Board.md`): error, `Code == "structural"`, store unchanged.
- `StructuralTests.A_writer_is_refused_mkcp_and_rmcp`.
- `StructuralTests.A_maintainer_may_do_all_of_it`.
- `StructuralTests.A_reader_gets_read_only_not_structural` (order of checks).
- `StructuralTests.A_writer_may_write_other_notes_in_the_board_folder` (`/Work/Board/card.md` stays `write`).

**Green:** `ICaller.Refuse` default implementation as in Files; `NoteWriter`, `Replacer`, `AttachmentWriter`, `Checkpoints` already call it with their command names (`src/Kumbu.Core/Notes/NoteWriter.cs:82,131,263,313`, `Replacer.cs:32`, `Checkpoints.cs:73,150`, `Attachments/AttachmentWriter.cs:51,114,152,192,227`).

**Mutations:** (1) `IsStructural` compares with `StringComparison.Ordinal` against a hard-coded `"/Board/Board.md"` suffix instead of `BoardPaths.Definition(vault)` (a note `/Work/Old/Board/Board.md` then counts as structural: add that case to `A_writer_may_write_other_notes_in_the_board_folder`); (2) drop `mkcp` from `StructuralCommands`; (3) structural check before read-only (reader gets `structural`); (4) `Refuse` checks `Level < Write` for structural instead of `< Maintain`.

**Tests:** +10.

### Task 0.6 — `BoardService` names the operation

**Goal:** design §3.3: `BoardService` passes `board create`, `board rename_column`, `board add`, `board move`, `board update` instead of one `Tool = "board"` (`src/Kumbu.Core/Board/BoardService.cs:34`, calls at `:45, 95, 151, 191, 256`), so `Refuse` tells structure from cards.

**Files:** Modify `src/Kumbu.Core/Board/BoardService.cs`. Test `tests/Kumbu.Core.Tests/Board/BoardServiceTests.cs`, `tests/Kumbu.Server.Tests/Api/BoardApiTests.cs`, `tests/Kumbu.Server.Tests/Mcp/BoardToolTests.cs`.

**Red:**
- `BoardServiceTests.A_writer_may_add_move_and_update_cards` (write-level caller).
- `BoardServiceTests.A_writer_may_not_create_a_board_or_rename_a_column` (`Code == "structural"`, `Kind == Refused`).
- `BoardApiTests.Renaming_a_column_with_a_write_key_is_403_structural` (`RestClient.KeyAsync("w", KeyRight.Write)`; body has `code: "structural"`).

**Green:** a `Refuse(op, path)` helper in `BoardService` with the operation string; error texts keep the `board:` prefix.

**Mutations:** (1) `rename_column` passes `board update`; (2) `create` passes `board add`.

**Tests:** +4.

### Task 0.7 — Moves and removals of structural files and their folders

**Goal:** design §3.3: `mv` to or from a structural file, `mv`/`rm` of a folder that contains one, `restore` of one (from the trash) are structural.

**Files:** Modify `src/Kumbu.Core/Notes/NoteMover.cs:17,44` (refuse also on `target`, and with `ContainsStructural(source)` for folders; today only the source is checked), `src/Kumbu.Core/Notes/NoteWriter.cs:263` (folder `rm`), `:313` (`restore`). Test: `tests/Kumbu.Core.Tests/Notes/StructuralTests.cs`.

**Red:**
- `StructuralTests.A_writer_may_not_move_a_note_onto_Board_md`, `…_move_Board_md_away`, `…_move_or_remove_the_board_folder` (`mv /Work/Board /Work/Old`, `rm /Work/Board/`), `…_move_a_folder_holding_Board_md_into_place` (a folder `/Work/Draft/Board/Board.md` moved to `/Work/`), `…_restore_Board_md_from_the_trash`.
- `StructuralTests.A_writer_may_move_other_folders`.

**Green:** `NoteMover` calls `caller.Refuse("mv", target)` after resolving the target; for folders it asks `StructuralFiles` about every source and target path in the move map before `CommitAsync`.

**Mutations:** (1) remove the target check; (2) folder check only on the folder path itself, not its contents; (3) `rm` of a folder skips the structural check.

**Tests:** +6.

### Task 0.8 — `restore --checkpoint`: whole or nothing for structure

**Goal:** design §3.3: a checkpoint restore whose checkpoint holds a structural file that differs from the current one (or either side lacks it) is refused whole below `maintain`, with "This checkpoint changes the board's structure; only those who may maintain *Work* can restore it."; equal structural files restore with `write`.

**Files:** Modify `src/Kumbu.Core/Notes/Checkpoints.cs:224` (`RestoreAsync`), `:452-470` (`PreviewWholeAsync`, `ApplyWholeAsync`). Test: `tests/Kumbu.Core.Tests/Sh/CheckpointTests.cs`, `tests/Kumbu.Server.Tests/Api/HistoryApiTests.cs`.

**Red:**
- `CheckpointTests.A_writer_restores_a_checkpoint_whose_board_is_unchanged`.
- `CheckpointTests.A_writer_may_not_restore_a_checkpoint_with_another_board` (columns renamed after `mkcp`; nothing restored, cards included).
- `CheckpointTests.…_with_a_board_where_there_is_none_now` and `…_without_the_board_there_is_now`.
- `HistoryApiTests.The_whole_restore_preview_refuses_a_changed_board_for_a_write_key` (403, `code: structural`).

**Green:** a private `StructuralChangeAsync(view, scope)` comparing each structural path's checkpoint version with the current version; called before any plan is made.

**Mutations:** (1) compare only presence, not content; (2) refuse only in `ApplyWholeAsync`, not in `PreviewWholeAsync`; (3) apply the non-structural part and skip the board.

**Tests:** +5.

### Task 0.9 — Refusal codes on REST and MCP

**Goal:** design §3.5: every REST 403 from `Refuse` carries `code`; MCP keeps the sentence.

**Files:** Modify `src/Kumbu.Server/Api/ApiErrors.cs:11,15` (`ApiError.Code`, `ApiException(…, code)`), `src/Kumbu.Server/Api/NoteApi.cs:668-671` (`Refuse` passes `refused.Code`); every route that calls it (`NoteApi.cs:337,380,411`, `UploadApi.cs:64`, `FileApi.cs:141,232`, `HistoryApi.cs:168,300,416`, `BoardApi.cs`). Test: create `tests/Kumbu.Server.Tests/Api/RefusalCodeTests.cs`.

**Red:**
- `RefusalCodeTests.Every_write_route_refuses_a_read_key_with_code_read_only` (`[Theory]` over the routes: PUT note, DELETE note, POST move, PUT file, DELETE file, upload, restore, mkcp, rmcp, board card add): 403, `code == "read-only"`.
- `RefusalCodeTests.Every_route_to_Board_md_refuses_a_write_key_with_code_structural` (`[Theory]`: PUT, DELETE, move from, move to, restore, whole checkpoint restore, file upload at the path).
- `RefusalCodeTests.A_refusal_over_MCP_keeps_its_sentence` (`ShAsync("write /Gartenarbeit/Board/Board.md …", writeKey)` → error text with "maintain").

**Green:** as in Files.

**Mutations:** (1) `NoteApi.Refuse` drops the code; (2) `FileApi` PUT path skips `Refuse` for an existing file (replace); (3) `ApiError` serialises `code` even when null (a body-shape test in the class pins `code` absent on a 404).

**Tests:** +18.

### Task 0.10 — Writer references and the payload version `v`

**Goal:** design §8.1: every place that stores a label also stores a sealed writer reference; every sealed payload that carries a writer gets `v` (absent = before 0.3.0). In phase 0 only the owner's reference `{owner: true, kind, id}`.

**Files:**
- Create: `src/Kumbu.Core/Notes/WriterRef.cs` (`sealed record WriterRef(bool Owner, string Kind, string? Id, string? Writer)`; `ForOwner(kind, id)`, `ForMember(writerId, kind)`; `ChangesetKey` (task 0.12))
- Modify: `src/Kumbu.Core/Notes/ICaller.cs` (`WriterRef Reference { get; }`; `FixedCaller` gets one, default `ForOwner("agent", null)`)
- Modify: `src/Kumbu.Core/Notes/StoredNote.cs` (`StoredNote`, `HistoryEntry`, `TrashedNote`, `TrashedAttachment` gain `WriterRef? Ref` / `LegacyWriter` flag), `src/Kumbu.Core/Attachments/StoredAttachment.cs`, `src/Kumbu.Core/Notes/Moves.cs` (`MoveRecord`), `src/Kumbu.Core/Notes/Checkpoint.cs`
- Modify: `src/Kumbu.Server/Vaults/UnlockedVault.cs:488-571` (`HistoryPayload`, `NotePayload`, `TrashPayload`, `MovePayload`, `AttachmentVersionPayload`, `AttachmentPayload`, `AttachmentTrashPayload`, `CheckpointPayload` gain `v` and `ref`, JsonIgnore when null; `Payload.From`, `ToNote`, `ToAttachment` carry them)
- Modify: `src/Kumbu.Server/Auth/RequestCaller.cs` (`Reference`: session `ForOwner("session", null)`, key `ForOwner(kind == Device ? "device" : "agent", keyId)`, connection `ForOwner("agent", connectionId)`, dev token `ForOwner("server", null)`)
- Modify: `src/Kumbu.Core/Notes/NoteWriter.cs`, `NoteMover.cs`, `Replacer.cs`, `Checkpoints.cs`, `src/Kumbu.Core/Attachments/AttachmentWriter.cs`, `FolderMarker.cs`, `src/Kumbu.Core/Board/BoardService.cs`, `src/Kumbu.Server/Notes/Seeder.cs` (import/seed: `ForOwner("server", null)`): pass `caller.Reference` wherever `caller.Writer` is stored
- Test: create `tests/Kumbu.Server.Tests/Vaults/WriterRefTests.cs` (`VaultWorld`), `tests/Kumbu.Core.Tests/Notes/WriterRefTests.cs`

**Red:**
- Core `WriterRefTests.A_write_stores_the_callers_reference_beside_its_label` (note, history entry after a second write, trash, move, checkpoint, attachment).
- Server `WriterRefTests.A_written_note_payload_carries_v_2_and_the_reference` (open the sealed `notes` document with the vault key; JSON has `v: 2` and `ref.owner: true`).
- `WriterRefTests.A_payload_without_v_reads_as_the_owners` (a payload sealed by a helper without `v`/`ref` → `Ref` is the owner's, legacy).
- `WriterRefTests.A_v2_payload_without_a_reference_reads_as_unknown` (`v: 2`, no `ref` → `Ref` null and `Unknown` true; never the owner).

**Green:** as in Files; `Payload` maps `v` absent → `WriterRef.Legacy` (owner), `v == 2 && ref == null` → `WriterRef.Unknown`.

**Mutations:** (1) `ToNote` reads a missing `ref` as owner regardless of `v`; (2) `HistoryPayload` drops `ref` on rewrap (history entries lose it after the next write); (3) `MovePayload` not given `v`.

**Tests:** +8.

### Task 0.11 — Fail closed: the store refuses an entry without a reference

**Goal:** design §8.1 "Fail closed": from 0.3.0 the store refuses a write that has no reference, on every write path.

**Files:** Modify `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (the batch commit, `:120-260`: `RequireRef(entry)` before each insert/replace; throws `InvalidOperationException("A write without a writer reference")`); mirror in `tests/Kumbu.Core.Tests/Fakes/InMemoryNoteStore.cs` so Core tests catch a path that forgets it. Test: `tests/Kumbu.Server.Tests/Vaults/WriterRefTests.cs`.

**Red:** `WriterRefTests.Every_write_path_stores_a_reference` (`[Theory]` over `write`, `edit`, `replace`, `mv` (note and folder: move records), `rm` (trash), `restore` from trash, `restore --checkpoint`, `board add`, `board move`, `board rename_column`, `mkcp`, attachment upload, attachment replace, attachment `rm`, folder marker, import via `Seeder`): run through the MCP shell with a key (`app.ConnectAsync`) or `VaultWorld.Store`; then open every sealed document the write touched and assert `ref` present. Plus `WriterRefTests.A_caller_without_a_reference_is_refused` (a `FixedCaller` whose `Reference` is null; nothing stored).

**Green:** `RequireRef` in the store; fix each path the theory finds.

**Mutations:** (1) remove `RequireRef` from the attachment insert; (2) `Seeder` passes `null`; (3) `TrashInAsync` builds a `TrashPayload` without `ref`.

**Tests:** +17.

### Task 0.12 — Changesets keyed by the writer reference

**Goal:** design §8.1: `Changeset.ForAsync` and `Latest` (`src/Kumbu.Core/Notes/StoredNote.cs:40-60`) are keyed by the reference (owner + credential, or session; a member's writer id), not by `Writers.Identity(label)`, else every member, storing `""`, would share one changeset.

**Files:** Modify `src/Kumbu.Core/Notes/StoredNote.cs` (`WriterRef.ChangesetKey`: `owner:{kind}:{id}`, `member:{writer}`, legacy `label:{identity}`), `src/Kumbu.Core/Notes/INoteStore.cs:14` (`LatestChangesetAsync(vault, string changesetKey, ct)`), `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs:61-65`, `InMemoryNoteStore.cs:98`. Test: `tests/Kumbu.Core.Tests/Notes/ChangesetRecordTests.cs`.

**Red:**
- `ChangesetRecordTests.Two_writers_with_the_same_label_get_two_changesets` (two `FixedCaller("Claude")` with different references).
- `ChangesetRecordTests.Two_members_storing_no_label_get_two_changesets` (`ForMember("w1")`, `ForMember("w2")`, label `""`).
- `ChangesetRecordTests.A_legacy_entry_continues_its_writers_changeset_by_label`.

**Green:** as in Files.

**Mutations:** (1) `ChangesetKey` for members returns `"member"` without the id; (2) `Latest` still filters by `Writers.Identity(label)`.

**Tests:** +4.

### Task 0.13 — `WriterLabels`: one renderer per viewer, on the server

**Goal:** design §8.2 with only "self" and "owner" in play: one service maps `(viewer, label, reference)` to `(display, kind)` and absorbs `Api/WriterKinds.cs`; called at every place that emits a writer, the shell included. Decision 8: kind `self` replaces `user` for the viewer.

**Files:**
- Create: `src/Kumbu.Server/Api/WriterLabels.cs` (from `WriterKinds.cs`, which is deleted; registrations in `Program.cs`)
- Create: `src/Kumbu.Core/Notes/IWriterLabels.cs` (Core interface so the shell can render: `ValueTask<(string Display, string? Kind)> RenderAsync(string label, WriterRef? reference, CancellationToken ct)`; default identity implementation for tests)
- Modify: `src/Kumbu.Server/Api/NoteApi.cs` (history `:478`, earlier versions `:540`, version, `ChangeView` `:643`), `HistoryApi.cs` (trash `:222`, activity `:245`, checkpoints, version), `FileApi.cs` (file state), `src/Kumbu.Core/Sh/Commands/History.cs`, `Trash.cs`, `CheckpointCommands.cs` (the design calls the last one `Checkpoints.cs`), `DiffCommand.cs`
- Test: rename `tests/Kumbu.Server.Tests/Api/WriterKindTests.cs` → `WriterLabelTests.cs` and extend; `tests/Kumbu.Core.Tests/Sh/VersionCommandsTests.cs`

**Red:**
- `WriterLabelTests.The_viewers_own_writes_have_kind_self` (was `…user`).
- `WriterLabelTests.A_post_0_3_0_entry_without_reference_shows_as_unknown` (sealed fixture from task 0.10; history shows `writer: "unknown"`, never the owner's name).
- `WriterLabelTests.History_and_activity_and_trash_render_through_one_service` (same writer, same display across the three endpoints).
- `VersionCommandsTests.History_renders_writers_through_the_labels_service` (Core: a fake `IWriterLabels` that upper-cases; `history`, `trash`, `lscp` outputs show it).

**Green:** `WriterLabels.RenderAsync`: reference owner → today's `WriterKinds.OfAsync` logic with `self` for the viewer's own; `Unknown` → `("unknown", null)`. Shell commands get `IWriterLabels` from the `Shell` constructor (default identity).

**Mutations:** (1) activity endpoint bypasses the service and returns the raw label; (2) `Unknown` falls back to the owner; (3) `lscp` prints `Checkpoint.CreatedBy` directly.

**Tests:** +10.

### Task 0.14 — Web: `self` from the server, not `=== username`

**Goal:** design §8.2: the web app's self-detection moves to the server's `kind: self`. The design points at `web/src/api/activity.ts:56`; the actual comparisons are `web/src/note/AgentBanner.tsx:51` (`writerOf(v.writer) === username`), `web/src/note/NoteView.tsx:311` (`updatedBy === username`) and `web/src/note/ChangedElsewhere.tsx:67` (`writer === username`); `writerText` in `web/src/api/activity.ts` switches on `"user"`.

**Files:** regenerate `web/src/api/schema.d.ts`; modify `web/src/api/activity.ts`, `web/src/note/AgentBanner.tsx`, `NoteView.tsx`, `ChangedElsewhere.tsx`; server: `NoteApi` version view gains `lastWriterKind` (so `ChangedElsewhere` has it). Tests: `web/src/note/AgentBanner.test.tsx`, `ChangedElsewhere` tests in `web/src/note/`, `web/src/shell/ActivitySheet.test.tsx`; server `tests/Kumbu.Server.Tests/Api/VersionTests.cs`.

**Red:** web `writerText` renders `"You"` for `writerKind: "self"`; `ChangedElsewhere` treats a writer with the user's name but `lastWriterKind: "agent"` as someone else (red today: it compares names); `AgentBanner` same. Server `VersionTests.The_version_names_the_kind_of_its_last_writer`.

**Green:** as in Files; fixtures in tests use `"self"` where they used `"user"`.

**Mutations:** (1) `writerText` keeps `case "user"` only; (2) `ChangedElsewhere` falls back to `writer === username` when the kind is missing.

**Tests:** server +1; web +6.

### Task 0.15 — `maintain` in the key, connection, consent and device dialogs; the `warning` token

**Goal:** concept §7, design §10.1: a fourth level `maintain` in the warning colour with "Can change the vault's structure: the board's columns, checkpoints."; new keys and connections suggest `write`; a `warning` token in all 17 schemes, checked by the contrast test.

**Files:** `web/src/theme/schemes.ts` (`Tokens.warning`, 17 values), `web/src/index.css` (`--warning` per scheme), `web/src/theme/apply.ts`, `web/src/theme/contrast.test.ts` (`textTokens` gains `"warning"`), `web/src/routes/NewKeyDialog.tsx:12-17` (`Right` gains `"maintain"`), `NewConnectionDialog.tsx`, `Connections.tsx:21`, `Keys.tsx:19`, `Consent.tsx:15` (`rightText`), `web/src/shell/ConnectObsidianDialog.tsx`. Tests: their `.test.tsx` files.

**Red:** `contrast.test.ts` (warning × 3 grounds × 17 schemes = 51 cases, fail until values exist); `Keys.test.tsx` "lists a maintain key as 'read, write and maintain'"; `NewKeyDialog` (in `Keys.test.tsx`) "offers maintain in the warning colour with its sentence" and "suggests write for a new key"; `Connections.test.tsx`, `Consent.test.tsx` "shows maintain".

**Green:** choose warning colours per scheme that pass 4.5:1 on `bg`, `surface`, `surface2` (amber family on dark schemes, a dark ochre on light ones); `rightText` gains `maintain`.

**Mutations:** (1) remove `"warning"` from `textTokens` (the count drops; reported); (2) one light scheme gets the dark scheme's warning value (contrast fails); (3) `NewKeyDialog` default back to `"read"`.

**Tests:** web +57.

### Task 0.16 — Security architecture for phase 0

**Files:** `docs/security-architecture.md` §4 "Who can do what" (levels `read`/`write`/`maintain`, structural files on every route, legacy write keys read as `maintain`, the payload version), §5.4 (writer references beside labels, fail closed).

**Red/green:** documentation only; `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run`, `pnpm -C web build` (**phase gate**).

**Expected after phase 0:** dotnet ≈ 2807 passed, 4 skipped; web 74 files, ≈ 1191 tests.

---

## Phase 1 — Memberships, member slots, per-request authorisation, per-caller view, aliases

Design §2, §3; phase 1 of §12. Memberships are created through `Memberships.GrantAsync` (the core that acceptance calls in phase 3) and changed in tests through MongoDB directly. **Until phase 4, re-keying refuses a shared vault** (task 1.13).

### Task 1.1 — `MembershipDocument` and the `memberships` collection

**Goal:** design §2.1: fields, statuses, indexes.

**Files:**
- Create: `src/Kumbu.Server/Sharing/MembershipDocument.cs` (`MembershipDocument` with `[BsonElement]` names as in the table: `_id` string, `vault`, `owner`, `member` (nullable), `role`, `status`, `confirm`, `label` (byte[]), `inviteLookup`, `expiresAt`, `createdAt`, `acceptedAt`, `revokedAt`, `roleChangedAt`, `pin`, `writerIds`, `providerSeen`, `redeemedAt`, `writers`, `revision`; `MemberRole` enum `Read, Write, Maintain` with text; `MembershipStatus` constants `pending`, `active`, `revoked`, `left`)
- Create: `src/Kumbu.Server/Sharing/Memberships.cs` (`Memberships(IMongoDatabase db, VaultCatalog catalog, IOptions<KumbuOptions> options, TimeProvider time, ILogger<Memberships> log)`, `internal IMongoCollection<MembershipDocument> Docs`, `EnsureReadyAsync` creating: unique `(vault, member)` partial on `member` `$type: string`; `(member, status)`; `(vault, status)`; unique `inviteLookup` partial on `$type: binData`); registered in `Program.cs`
- Test: create `tests/Kumbu.Server.Tests/Sharing/MembershipTests.cs` (`[Collection("mongo")]`, `VaultWorld`)

**Red:**
- `MembershipTests.A_member_has_one_membership_per_vault` (two documents `(v, u2)` → duplicate key).
- `MembershipTests.Pending_memberships_without_a_member_do_not_collide` (two `member: null`).
- `MembershipTests.An_invite_lookup_is_unique_while_it_exists`.
- `MembershipTests.Statuses_only_move_forward` (`Memberships.TransitionFilter(from, to)` returns null for `active → pending`, `revoked → active`, `left → active`).

**Green:** as in Files.

**Mutations:** (1) partial filter of `(vault, member)` on `$exists` (null counts → second pending collides); (2) allow `revoked → active` in `TransitionFilter`.

**Tests:** +4.

### Task 1.2 — Slot kinds `member` and `invite`, `GrantedVia`; ownership stays `user`

**Goal:** design §2.2: two kinds that never reuse `user` or `Owner`; derived slots carry `GrantedVia`. Everything that treats a `user` slot with `Owner` as ownership is pinned against member slots.

**Files:** Modify `src/Kumbu.Server/Vaults/Documents.cs:26-…` (`SlotDocument.Member = "member"`, `Invite = "invite"`, fields `Membership` (`membership`), `GrantedVia` (`grantedVia`), `Alias` (`alias`, byte[]), `UsedAt` (`usedAt`)); index on `GrantedVia` (partial) and on `Membership` (partial) in `VaultCatalog.EnsureReadyAsync` (`VaultCatalog.cs:1239-…`). Test: create `tests/Kumbu.Server.Tests/Sharing/OwnershipTests.cs` (`VaultWorld`).

**Red** (insert a `member` slot for `u2` on `u1`'s vault directly):
- `OwnershipTests.A_member_slot_is_not_ownership_for_account_deletion` (`Deletion.OwnedVaultsAsync` via `DeleteOwnAccountAsync` of `u2` leaves `u1`'s vault and its notes).
- `OwnershipTests.The_admin_list_counts_only_owned_vaults` (`Admin.ListUsersAsync`, `Accounts/Admin.cs:32`).
- `OwnershipTests.Deleting_a_shared_vault_marks_only_its_owner` (`MarkOwnersAsync`, `VaultCatalog.cs:670`: `u2`'s user document unchanged).
- `OwnershipTests.A_member_slot_does_not_count_as_a_locked_vault_of_the_dev_token` (`CountLockedAsync`).

**Green:** constants and fields only; the tests guard the existing filters on kind `user` (they pass once the slot can be written; a mutation that widens a filter must fail them).

**Mutations:** (1) `OwnedVaultsAsync` filters on `Owner` only, not on kind; (2) `Admin.ListUsersAsync` on `Exists(Owner)` only.

**Tests:** +4.

### Task 1.3 — `Memberships.GrantAsync`: membership and member slot in one transaction

**Goal:** design §2.1, §2.3, §13 M8: the core of acceptance. One transaction: the membership (`active`, sealed `label`, sealed empty `writers` and `writerIds`, `revision = 0`), the `member` slot (decision 5: wrapped as a user slot of kind `member`; `Lookup` = the member's user-slot lookup; `NameId` = MAC of the alias under the member's name-id key; `Alias` sealed under `HKDF(memberKey, "kumbu/v1/alias", vaultId)`, AAD `alias:{vault}:{membershipId}`), a write to the vault document's `membershipsChanged` counter, the member limit (`Vaults.MemberLimit`, default 20) checked inside. Refuses the owner as member.

**Files:** Create `src/Kumbu.Server/Vaults/VaultCatalog.Members.cs` (`internal SlotDocument MemberSlot(...)`, `internal byte[] SealForVault(byte[] vaultKey, string vaultId, string purpose, byte[] plain, string aad)` and `Open…`, `internal async Task<byte[]> OwnerVaultKeyAsync(ownerId, ownerKey, vaultId)` via `UserVaultKeysAsync`); `Sharing/Memberships.cs` (`GrantAsync(string ownerId, byte[] ownerKey, string vaultId, string memberId, byte[] memberKey, string alias, string label, MemberRole role, CancellationToken ct)`); `Vaults/Documents.cs` (`VaultDocument.MembershipsChanged`); `KumbuOptions.cs` (`VaultOptions.MemberLimit`); `compose.yaml`, `README.md` (`KUMBU_VAULT_MEMBER_LIMIT`). Test helper: create `tests/Kumbu.Server.Tests/Sharing/Share.cs` (see "Test fixtures"). Test: `tests/Kumbu.Server.Tests/Sharing/MembershipTests.cs`.

**Red:**
- `MembershipTests.Granting_writes_the_membership_and_its_member_slot_together` (both exist; the slot's `Membership` is the id; nothing in plain names the alias or label: scan the BSON for the UTF-8 bytes of "Gartenarbeit" and "Bob").
- `MembershipTests.The_21st_membership_is_refused` (limit 20; `MemberLimit = 2` in the test's options to keep it fast) and `…_two_concurrent_grants_at_the_limit_admit_one` (`Task.WhenAll`).
- `MembershipTests.An_owner_cannot_be_a_member_of_her_own_vault`.
- `MembershipTests.A_failed_grant_leaves_neither_document` (alias taken → no membership).

**Green:** as in Files; the transaction uses `VaultCatalog.StartSessionAsync` and `WithTransactionAsync` as `CreateForUserAsync` does (`VaultCatalog.cs:295-309`).

**Mutations:** (1) count the limit outside the transaction (concurrent test admits two); (2) skip the `membershipsChanged` write; (3) store the alias in plain; (4) insert the slot before the transaction (failed grant leaves a slot).

**Tests:** +5.

### Task 1.4 — HARD RULE 1 (a): sessions — membership checked per request before the vault is loaded (§3.1)

**Goal:** design §3.1, §5.3: `OpenForUserAsync` (`VaultCatalog.cs:195`) also takes kind `member`, reads their memberships in one query (`_id in [...]`, `status == active`, and from task 3.15 the owner's `active`), skips any whose membership isn't active **before** `LoadAsync`, and opens the slot only then. The cache never replaces authorisation (comments at `:127-128`, `:139`).

**Files:** Modify `src/Kumbu.Server/Vaults/VaultCatalog.cs:195-240` (filter `Kind in [user, member]`; `ActiveMembershipsAsync(IEnumerable<string> ids)` in `VaultCatalog.Members.cs` reading `memberships` directly (the catalog holds `db`); the busy retry at `:214-218` keeps the kind and membership); `OpenUserSlot` for kind `member` uses the AAD of decision 5. Test: create `tests/Kumbu.Server.Tests/Sharing/MembershipCheckTests.cs` — **the hard-rule class** for rule 1.

**Red:**
- `MembershipCheckTests.A_member_session_opens_the_shared_vault` (catalog-level, `VaultWorld`).
- `MembershipCheckTests.A_revoked_membership_refuses_an_already_unlocked_vault_on_the_next_request` (same `VaultCatalog` instance: member opens (vault now in `_open`), `Share.SetStatusAsync(revoked)`, member opens again → vault absent; the owner's open still has it).
- `MembershipCheckTests.A_left_or_pending_membership_opens_nothing`.
- `MembershipCheckTests.A_member_slot_without_a_membership_opens_nothing` (slot inserted by "someone with database access", membership absent).
- `MembershipCheckTests.One_membership_query_per_request` (a counting `IMongoDatabase` is overkill: assert via MongoDB profiler level 2 on the test database that `memberships` saw exactly one `find` for two shared vaults).

**Green:** as in Files.

**Mutations (mandatory):** (1) drop the `status == active` filter in `OpenForUserAsync`'s membership query; (2) check the membership after `LoadAsync` and only when the vault isn't in `_open` (cache hit skips the check); (3) skip the membership query when the membership id is in a per-process cache; (4) treat a missing membership as active.

**Tests:** +5.

### Task 1.5 — Members' own keys and connections: derived slots with `GrantedVia`, against revocation

**Goal:** design §3.3 (last paragraphs), §3.4: `UserVaultKeysAsync` (`VaultCatalog.cs:358`) gets a second path through active `member` slots; the derived slot gets `GrantedVia`; its creation is a transaction that also writes the membership (`revision++`, filter `status == active`) and adds the credential's display name (device part only for devices) to the sealed `writers` and a fresh random writer id to `writerIds`. Members may give their credentials any level, up to `maintain`.

**Files:** Modify `src/Kumbu.Server/Vaults/VaultCatalog.cs:358-434` (`UserVaultKeysAsync` returns `(vaultKey, membershipId?)` per vault; `ApiKeySlotsAsync`, `ConnectionSlotsFromUserAsync` set `GrantedVia`), `src/Kumbu.Server/Accounts/ApiKeys.cs` (the key-creation transaction calls `Memberships.RecordCredentialAsync(session, membershipId, credentialId, displayName, vaultKey)`), `src/Kumbu.Server/OAuth/OAuthTokens.cs` (consent transaction, beside the `Wraps` sentinel `:550`), `src/Kumbu.Server/Accounts/Pairings.cs`. Test: create `tests/Kumbu.Server.Tests/Sharing/DerivedCredentialTests.cs` (`[Collection("app")]`, `RestClient`).

**Red:**
- `DerivedCredentialTests.A_member_may_give_a_key_the_shared_vault` (Bob, write member, creates a `maintain` key for `Gartenarbeit` under his alias; the slot has `GrantedVia`).
- `DerivedCredentialTests.A_key_created_while_the_membership_is_revoked_fails` (revoke between `ApiKeySlotsAsync` and the insert via `catalog.IssueFaultAt = "after-wrap"`; the key's creation throws; no slot).
- `DerivedCredentialTests.The_membership_records_the_credentials_display_name_sealed` (open `writers` with the vault key: `"Claude"`; for a device only `"Laptop"`, never `"<vault> – Obsidian Laptop"`).
- `DerivedCredentialTests.A_member_connection_gets_GrantedVia_slots` (consent flow from `tests/Kumbu.Server.Tests/OAuth/` helpers).

**Green:** as in Files.

**Mutations:** (1) creation transaction without the `status == active` filter on the membership update; (2) `GrantedVia` not set; (3) `writers` stores the full device key name.

**Tests:** +5.

### Task 1.6 — HARD RULE 1 (b): keys, connections and sync — membership checked per request before the vault is loaded (§3.1)

**Goal:** design §3.1: `OpenForApiKeyAsync` (`:515`) and `OpenConnectionSlotsAsync` (`:444`) run the same batched query for every slot with `GrantedVia` and skip inactive ones before `LoadAsync`; a connection token that opens nothing is refused (`RequestCaller.ForConnectionAsync`, `Auth/RequestCaller.cs:90`). `UsedAt` set per decision 9.

**Files:** Modify `src/Kumbu.Server/Vaults/VaultCatalog.cs:444-540`; `VaultCatalog.Members.cs`. Test: `tests/Kumbu.Server.Tests/Sharing/MembershipCheckTests.cs` (same hard-rule class).

**Red:**
- `MembershipCheckTests.A_revoked_membership_refuses_a_live_API_key_on_the_next_request` (Bob's key `cat`s a note over MCP; revoke in MongoDB; next `cat` → "No such file" / vault absent from `ls /`; Bob's key still opens his own vault).
- `MembershipCheckTests.A_revoked_membership_refuses_a_live_OAuth_token` (token with only the shared vault → 401 on `/mcp/c/{id}`).
- `MembershipCheckTests.A_lowered_role_caps_a_live_key_on_the_next_request` (role `read` in MongoDB → next write 403 `read-only`).
- `MembershipCheckTests.A_ws_ticket_for_a_revoked_membership_lists_no_shared_vault` (`POST /api/ws-ticket` with Bob's key).

**Green:** as in Files.

**Mutations (mandatory):** (1) `OpenForApiKeyAsync` skips the query when `GrantedVia` is null on the *first* slot only; (2) `OpenConnectionSlotsAsync` checks after building the result but before `LoadAsync` only for vaults not yet loaded; (3) drop `status == active` in the shared helper; (4) `ForConnectionAsync` lets an empty token through.

**Tests:** +4.

### Task 1.7 — Access records and the per-caller view

**Goal:** design §3.2: the opening methods return `VaultAccess(UnlockedVault Vault, string Name, AccessLevel Level, MembershipDocument? Membership, bool IsOwner)`; `EncryptedNoteStore.VaultsAsync` (`Vaults/EncryptedNoteStore.cs:886`) builds the namespace from `Name` and **throws** on a duplicate (decision 12); rights lookups keyed by vault id; everything downstream that carries a vault name uses the caller's (sync hints, `TicketGrant`, `AccountApi.VaultNamesAsync`, exports, activity).

**Files:** Create `src/Kumbu.Server/Vaults/VaultAccess.cs`. Modify `Vaults/Credentials.cs` (`ICredential.OpenAsync` → `IReadOnlyList<VaultAccess>`), `VaultCatalog.cs` (`OpenAsync`, `OpenForUserAsync`, `OpenForApiKeyAsync`, `OpenForConnectionAsync` build records; the member's name is the alias opened from the slot), `EncryptedNoteStore.cs` (`_vaults` holds records; `UnlockedVaultsAsync` callers get the caller's name), `Auth/RequestCaller.cs` (`OpenVaultsAsync`), `Api/PairingApi.cs:41`, `Api/SyncApi.cs`, `Api/AccountApi.cs`, `Api/ExportApi.cs`, `Api/NoteApi.cs:200-218`. Test: `tests/Kumbu.Server.Tests/Sharing/AliasTests.cs` (create), `tests/Kumbu.Server.Tests/Vaults/EncryptedNoteStoreTests.cs`.

**Red:**
- `AliasTests.A_member_sees_the_shared_vault_under_her_alias` (Alice's `Gartenarbeit`, Bob's alias `garten`: Bob's `ls /` lists `garten`, `cat /garten/a.md` works, `/Gartenarbeit` doesn't exist for him).
- `AliasTests.The_owner_still_sees_her_name_while_the_vault_is_open_for_a_member` (shared `UnlockedVault`, per-caller names).
- `EncryptedNoteStoreTests.Two_vaults_of_one_name_fail_loudly` (two dev-token vaults inserted with the same name → `InvalidOperationException`; today one is hidden).
- `AliasTests.A_sync_hint_for_the_member_names_the_alias` (deferred assertion over `SyncHub` message capture in `SyncSocketTests` style).

**Green:** as in Files.

**Mutations:** (1) `VaultsAsync` keyed by `Vault.Name` again; (2) the duplicate check back to `GroupBy().First()`; (3) `SyncApi` ticket uses `UnlockedVault.Name`.

**Tests:** +6.

### Task 1.8 — Effective levels for members

**Goal:** design §3.3 table: member session = role; member credential = `min(role, sealed)`; `Refuse` uses it (read-only and structural codes from phase 0 apply unchanged).

**Files:** Modify `src/Kumbu.Server/Auth/RequestCaller.cs` (`Level(vault)` from the access records), `Vaults/Credentials.cs`. Test: create `tests/Kumbu.Server.Tests/Sharing/MemberLevelTests.cs` (`RestClient`).

**Red:**
- `MemberLevelTests.A_read_member_session_is_refused_writes_with_read_only` (REST PUT → 403 `read-only`, sentence "you can only read vault garten", decision 4).
- `MemberLevelTests.A_write_member_may_write_notes_but_not_the_board` (403 `structural`).
- `MemberLevelTests.A_maintain_key_of_a_read_member_acts_as_read` and `…_of_a_write_member_acts_as_write`.
- `MemberLevelTests.A_raised_role_lifts_the_cap_on_the_next_request`.

**Green:** as in Files.

**Mutations:** (1) member credential level = sealed level (no `min`); (2) member session = `Owner`; (3) `min` over `role` and `Owner`.

**Tests:** +6.

### Task 1.9 — Aliases share the vault-name index, in both directions; renaming an alias

**Goal:** design §2.3: the unique `(Lookup, NameId)` index (`VaultCatalog.cs:1266`) arbitrates own vault names and aliases; renaming an alias is one transactional update of `NameId` and `Alias`.

**Files:** `VaultCatalog.Members.cs` (`RenameAliasAsync(memberId, memberKey, membershipId, newAlias)`), `Sharing/Memberships.cs`. Test: `tests/Kumbu.Server.Tests/Sharing/AliasTests.cs`.

**Red:**
- `AliasTests.Creating_an_own_vault_named_like_an_alias_is_refused` (`VaultNameTakenException`, case-insensitive).
- `AliasTests.Granting_with_an_alias_named_like_an_own_vault_is_refused`.
- `AliasTests.Renaming_an_alias_to_a_taken_name_is_refused_and_keeps_the_old_one`.
- `AliasTests.Renaming_an_alias_moves_the_vault_in_the_members_namespace`.

**Green:** as in Files; `CreateForUserAsync`'s orphan replacement (`VaultCatalog.cs:276-280`) must not treat a member slot as an orphan: its holder check looks up `Kind == User` only, so a member slot holding the name throws `VaultNameTakenException` — pin it.

**Mutations:** (1) member slot written with `NameId` under the *owner's* name-id key; (2) the orphan path in `CreateForUserAsync` deletes the member slot holding the name (filter on kind removed).

**Tests:** +4.

### Task 1.10 — A member's user-key rotation keeps her aliases

**Goal:** design §2.3 last paragraph: `PrepareUserSlotsAsync` (`VaultCatalog.cs:1310`) recomputes `Lookup`, `NameId` and reseals `Alias` for `member` slots (in phase 1 it also rewraps `Wrapped`, decision 5; phase 2 stops that); `ApplyVaultSlotsAsync`' check (`Accounts/UserKeyRotation.cs:243-266`) then covers them.

**Files:** `src/Kumbu.Server/Vaults/VaultCatalog.cs:1297-1358` (`SlotRewrap` gains `NewAlias`), `src/Kumbu.Server/Accounts/UserKeyRotation.cs:14,243-266`. Test: `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs`.

**Red:**
- `UserKeyRotationTests.A_members_password_change_keeps_the_shared_vault_under_its_alias`.
- `UserKeyRotationTests.A_members_recovery_keeps_the_shared_vault`.
- `UserKeyRotationTests.After_a_members_rotation_the_alias_still_blocks_an_own_vault_of_that_name`.

**Green:** as in Files: for kind `member`, the name comes from opening `Alias` with the old key.

**Mutations:** (1) `NameId` not recomputed for member slots; (2) `Alias` resealed under the old key.

**Tests:** +3.

### Task 1.11 — HARD RULE 4: members' writes store no names (§8.1)

**Goal:** design §8.1: a member's write stores label `""` and the reference `{writer: <per-membership writer id>, kind}` and nothing else (no user id, no credential id). The writer id of a session is one per membership (`writerIds["session"]`), of a credential the one task 1.5 recorded.

**Files:** `src/Kumbu.Server/Auth/RequestCaller.cs` (`Writer` and `Reference` per vault: `ICaller` gains `Writer(string vault)` / `Reference(string vault)` with today's properties as the defaults for own vaults; Core write paths ask per vault), `src/Kumbu.Core/Notes/ICaller.cs`, every Core write path touched in 0.10. Test: create `tests/Kumbu.Server.Tests/Sharing/NoNamesTests.cs` — **the hard-rule class** for rule 4.

**Red** (Bob writes through every path of `WriterRefTests.Every_write_path_stores_a_reference`, as session, as key and as device):
- `NoNamesTests.A_members_write_stores_no_label_and_no_identity` (`[Theory]` over the paths): open each touched sealed document with the vault key; the label fields (`lastWriter`, `writtenBy`, `deletedBy`, `movedBy`, `createdBy`) are `""`; `ref` has only `writer` and `kind`; the JSON contains neither Bob's username, user id, key id, connection id, key name nor device name (byte search for each).
- `NoNamesTests.The_owners_write_still_stores_her_label`.
- `NoNamesTests.Two_members_writer_ids_differ_and_change_with_a_new_membership`.

**Green:** as in Files.

**Mutations (mandatory):** (1) `RequestCaller.Writer(vault)` returns the key name for members; (2) `Reference` for a member includes the credential id; (3) one path (trash: `TrashInAsync`) uses `caller.Writer` instead of `caller.Writer(vault)`; (4) the session writer id is the user id.

**Tests:** +18.

### Task 1.12 — HARD RULE 5: the marker test, and the member columns of `WriterLabels` (§8.2)

**Goal:** design §8.2: owner sees "Bob · Claude" (membership `label` + `writers`, also from a tombstone: "Bob (left) · Claude"), a former member as "a former member"; a member sees "You", the owner by display name ("Alice's agent", "Alice in Obsidian"), and every other member or former member as **"another user"** (kind `user` or `agent` only, no device names). Display names crossing users are stripped of control/format characters and newlines, capped at 64, shown quoted (L6). Activity row keys opaque per changeset.

**Files:** `src/Kumbu.Server/Api/WriterLabels.cs` (resolves `writerIds` and `writers` through the vault key; `Sanitise(displayName)`), `src/Kumbu.Core/Notes/Activity.cs` (row keys from the changeset id hashed per viewer). Test: create `tests/Kumbu.Server.Tests/Sharing/MarkerTests.cs` — **the hard-rule class** for rule 5; `tests/Kumbu.Server.Tests/Sharing/WriterViewTests.cs`.

**Red:**
- `MarkerTests.A_label_written_by_member_A_never_reaches_member_B` — the marker test. Carol (member) names a key `MARKER-c4f1` and a device `MARKER-dev`, writes, edits, moves, removes, restores, makes a checkpoint (as maintain), uploads an attachment and adds a board card in `Gartenarbeit`. Then Bob (member) fetches **every** REST answer that carries writers or text about the vault (tree, note, history, earlier versions, version, changes, activity, trash, checkpoints, file state, attachment history, board, access-denied bodies) and runs `history`, `diff`, `trash`, `lscp`, `ls /`, `cat`, `rg MARKER` over MCP: no response body contains `MARKER`, Carol's username, display name or ids. (Phase 3 adds events and notices to this test; phase 5 adds socket messages.)
- `MarkerTests.Writer_names_are_never_written_into_note_content` (Carol's writes; scan all sealed `notes`/`checkpoint_texts` contents for `MARKER`).
- `WriterViewTests.The_owner_sees_Bob_dot_Claude` and `…_Bob_left_dot_Claude_from_a_tombstone`.
- `WriterViewTests.A_member_sees_the_owners_agents_as_Alices_agent`.
- `WriterViewTests.A_member_sees_another_member_and_a_former_member_alike_as_another_user`.
- `WriterViewTests.A_display_name_with_newlines_and_control_characters_is_cleaned_and_capped`.

**Green:** as in Files.

**Mutations (mandatory):** (1) `WriterLabels` returns the membership label to a member viewer; (2) device kind passed through for another member; (3) activity row key = writer id; (4) `lscp` shell output bypasses `WriterLabels` (task 0.13's hook removed for checkpoints).

**Tests:** +7.

### Task 1.13 — Re-keying refuses a shared vault until phase 4

**Goal:** design §12 phase 1: "Re-encrypt vault", "Re-encrypt my vaults" and recovery refuse a vault with any membership or invitation ("Re-encrypting a shared vault comes with a later step; nothing was changed."); recovery re-keys the other vaults and leaves shared ones pending. Otherwise `StartRekeyAsync` (`Vaults/VaultCatalog.Rekey.cs:401`) deletes member slots.

**Files:** `src/Kumbu.Server/Vaults/VaultCatalog.Rekey.cs:333` (`StartRekeyAsync` checks `memberships` for the vault, any status but `revoked`/`left`, inside its transaction → `VaultSharedException`), `src/Kumbu.Server/Accounts/Rekeying.cs:34,71`, `src/Kumbu.Server/Vaults/RekeyRunner.cs` (a refused vault stays in `rekeyPending`, logged), `Api/ApiErrors.cs` (409 with the sentence). Test: `tests/Kumbu.Server.Tests/Sharing/SharedRekeyRefusalTests.cs` (create).

**Red:**
- `SharedRekeyRefusalTests.Re_encrypting_a_shared_vault_is_refused_and_changes_nothing` (member slot and `GrantedVia` slots still there; Bob still reads).
- `SharedRekeyRefusalTests.Re_encrypt_my_vaults_re_keys_the_others_and_leaves_the_shared_one_pending`.
- `SharedRekeyRefusalTests.Recovery_leaves_the_shared_vault_pending_and_its_members_in`.
- `SharedRekeyRefusalTests.A_pending_invitation_counts_as_shared`.

**Green:** as in Files.

**Mutations:** (1) check only `active`; (2) check outside the start transaction (a grant racing the start: use `RekeyFaultAt` to grant between); (3) the runner drops the vault from `rekeyPending` after the refusal.

**Tests:** +4.

### Task 1.14 — Owner-only gaps: embedding provider and pairing

**Goal:** decision 7; design §3.3 "Owner-only actions": `PUT /api/vaults/{vault}/embeddings` (`Api/NoteApi.cs:200-218`) checks `IsOwner` from the access record; `POST /api/vaults/{vault}/pairing` (`Api/PairingApi.cs:38-45`) refuses a shared vault until task 5.8.

**Files:** `Api/NoteApi.cs`, `Api/PairingApi.cs`. Test: create `tests/Kumbu.Server.Tests/Sharing/OwnerOnlyTests.cs`.

**Red:**
- `OwnerOnlyTests.A_member_may_not_change_the_embedding_provider` (404 as other owner-only endpoints, design §3.5).
- `OwnerOnlyTests.Owner_only_endpoints_answer_a_member_with_404` (`[Theory]`: delete vault, rekey, access list, embeddings).
- `OwnerOnlyTests.A_member_cannot_pair_a_device_to_the_shared_vault_yet` (and the owner can't either while it is shared: "Pairing a shared vault needs the Kumbu² plugin 0.2.0, which comes with a later step.").

**Green:** as in Files.

**Mutations:** (1) the `IsOwner` check reads `Membership is null` on the wrong record (the first vault); (2) the pairing refusal only for members.

**Tests:** +6.

### Task 1.15 — A member's account deletion: memberships go, the owner's vault stays

**Goal:** design §12 phase 1 test "a member's account deletion never deletes the owner's vault"; minimal §7.2 now: in `Deletion.DeleteAccountAsync` (`Accounts/Deletion.cs:159`), every membership with `member == user`, its member slot and its `GrantedVia` slots are deleted (full receipt counts in task 3.16).

**Files:** `Accounts/Deletion.cs:159-213`, `Sharing/Memberships.cs` (`DeleteOfMemberAsync(session, userId)`). Test: `tests/Kumbu.Server.Tests/Accounts/DeletionTests.cs`.

**Red:**
- `DeletionTests.A_members_account_deletion_never_deletes_the_owners_vault` (Alice's notes, slots, attachments intact; Bob's own vault gone).
- `DeletionTests.A_members_account_deletion_removes_his_memberships_and_slots_on_shared_vaults`.
- `DeletionTests.After_a_members_deletion_his_writes_show_as_a_former_member_to_the_owner_and_another_user_to_members`.

**Green:** as in Files.

**Mutations:** (1) leave the membership (tombstone kept: owner still sees "Bob"); (2) delete by `owner == user` instead of `member == user`.

**Tests:** +3.

### Task 1.16 — Security architecture for phase 1

**Files:** `docs/security-architecture.md` §3 "The slots as built" (`member`, `GrantedVia`, the per-request membership check before the load), §4 (effective levels table, `min(role, level)`), §5.6 (`memberships`: who shares with whom, roles, times — in the clear), §2 (roles, anonymity among members and labels are enforced by the server since every member holds the vault key; a shared vault reaches every member's AI providers and devices).

**Phase gate:** `dotnet test --solution Kumbu.slnx` (no web change in phase 1).

**Expected after phase 1:** dotnet ≈ 2891 passed, 4 skipped; web unchanged (74 / ≈ 1191).

---

## Phase 2 — Keypairs and member slots under public keys

Design §5; phase 2 of §12.

### Task 2.1 — The member-slot primitive: DHKEM(P-256, HKDF-SHA256) with AES-256-GCM

**Goal:** design §5.2 pinned details: ephemeral P-256, ECDH with the recipient's public key, IKM = raw x-coordinate, `HKDF-SHA256(ikm, info "kumbu/v1/member-slot" ‖ alg, salt = ephemeral public ‖ recipient public)`, AES-256-GCM over the vault key, AAD `slot:{vault}:member:{membershipId}` ‖ alg; slot = `alg(1) ‖ ephemeral public (65, uncompressed SEC1) ‖ sealed`; public keys validated on the curve on every use.

**Files:** Create `src/Kumbu.Core/Crypto/MemberSlotCrypto.cs` (`static byte[] Wrap(ReadOnlySpan<byte> recipientPublic, byte[] vaultKey, string aad)`, `static byte[] Unwrap(ECDiffieHellman recipientPrivate, byte[] slot, string aad)`, `static ECDiffieHellman NewKeyPair()`, `static byte[] PublicOf(ECDiffieHellman)`, `static void RequireOnCurve(ReadOnlySpan<byte>)`). Test: create `tests/Kumbu.Core.Tests/Crypto/MemberSlotCryptoTests.cs`.

**Red:**
- `MemberSlotCryptoTests.A_wrapped_vault_key_unwraps_with_the_recipients_private_key`.
- `…Another_private_key_opens_nothing` (`CryptographicException`).
- `…An_off_curve_public_key_is_refused_on_wrap` (a point with y + 1).
- `…A_changed_algorithm_byte_opens_nothing` (byte in info and AAD).
- `…The_aad_binds_the_vault_and_the_membership`.
- `…A_compressed_or_short_public_key_is_refused`.

**Green:** `ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)`, `ImportParameters` (OpenSSL validates the point; also check explicitly with `ECParameters.Validate()`), `DeriveRawSecretAgreement` (.NET 8+) for the x-coordinate, `VaultCrypto`'s HKDF and AES-GCM helpers.

**Mutations:** (1) leave the algorithm byte out of the AAD; (2) salt without the recipient public; (3) skip `RequireOnCurve` on unwrap's import; (4) use `DeriveKeyFromHash` (SHA-256 of the secret) instead of the raw x-coordinate (a fixed test vector computed once pins the IKM).

**Tests:** +7.

### Task 2.2 — A keypair per user, created lazily and only once

**Goal:** design §5.1: `UserDocument.PublicKey` (plain), `PrivateKey` sealed under `HKDF(userKey, "kumbu/v1/user-private-key")` with AAD `user-private:{userId}:{sha256(publicKey)}`; created at sign-in (`UserCatalog.TrySignInAsync`, `Accounts/UserCatalog.cs:95`), at account creation (`:50`) and on acceptance; set-if-absent conditioned on `keyId`.

**Files:** Create `src/Kumbu.Server/Sharing/MemberKeys.cs` (`EnsureAsync(userId, userKey, ct)`, `OpenPrivate(UserDocument, userKey)` → `ECDiffieHellman`, `PublicFromPrivate`). Modify `Accounts/UserDocuments.cs` (`publicKey`, `privateKey`), `Accounts/UserCatalog.cs:50,95`. Test: create `tests/Kumbu.Server.Tests/Sharing/KeypairTests.cs` (`[Collection("mongo")]`, `UserCatalog` built as in `DeletionTests.Users()`).

**Red:**
- `KeypairTests.Signing_in_creates_a_keypair_once` (second sign-in same public key).
- `KeypairTests.Two_concurrent_sign_ins_create_one_keypair` (`Task.WhenAll` of two `TrySignInAsync`; one `publicKey`, private opens and matches).
- `KeypairTests.A_private_key_sealed_for_another_public_key_does_not_open` (swap `publicKey` in MongoDB → `OpenPrivate` throws).
- `KeypairTests.A_new_account_has_a_keypair`.
- `KeypairTests.The_private_key_is_never_in_the_clear` (BSON scan for the PKCS#8 bytes).

**Green:** as in Files; the set-if-absent filter `{_id, keyId, publicKey: {$exists: false}}`.

**Mutations:** (1) filter without `publicKey $exists: false` (the concurrent test sees two writes); (2) AAD without the public key hash; (3) created only at account creation (old accounts never get one).

**Tests:** +5.

### Task 2.3 — Member slots under public keys; the stand-in goes

**Goal:** design §5.3, decision 5: `GrantAsync` derives the member's public key from the private key it unwraps with her user key (never from `users.publicKey`) and wraps with `MemberSlotCrypto`; `OpenForUserAsync` unwraps the private key once per request, then each member slot; the phase-1 stand-in code is deleted.

**Files:** `VaultCatalog.Members.cs`, `VaultCatalog.cs:195-240`, `Sharing/Memberships.cs`. Test: `tests/Kumbu.Server.Tests/Sharing/MembershipCheckTests.cs` (stays green), `KeypairTests.cs`.

**Red:**
- `KeypairTests.A_member_slot_is_wrapped_to_the_members_public_key` (first byte `1`; unwraps with her private key; `UserKek` opens nothing).
- `KeypairTests.A_stand_in_member_slot_opens_nothing` (a phase-1-format slot inserted → vault absent, warning logged via `CapturedLog`).
- `KeypairTests.A_member_slot_survives_the_members_sign_out` (a new session after sign-in opens it).

**Green:** as in Files.

**Mutations:** (1) `GrantAsync` reads the public key from `users.publicKey`; (2) keep the stand-in unwrap as a fallback.

**Tests:** +3.

### Task 2.4 — HARD RULE 3: re-keys wrap only to pinned keys (§5.4)

**Goal:** design §5.4: at acceptance (here: `GrantAsync`) the public key derived from the session's private key is written into the membership's sealed `pin` `{membershipId, vault, member, alg, publicKey}` (AAD `membership-pin:{vault}:{id}`) under the current vault key. One helper, `Memberships.PinnedKeyAsync(membership, vaultKey)`, is the only source of a key to wrap to: it opens the pin under the vault key and checks `membershipId`, `vault`, `member`; anything else → null, and the caller deletes the member slot and logs it as damaged. Used by the member's rotation (2.5) and the re-key finish (4.3).

**Files:** `Sharing/Memberships.cs` (`SealPin`, `PinnedKeyAsync`, `WrapToPinAsync(session, membership, vaultKey, newVaultKey?)`), `VaultCatalog.Members.cs`. Test: create `tests/Kumbu.Server.Tests/Sharing/PinTests.cs` — **the hard-rule class** for rule 3.

**Red** (through `WrapToPinAsync`, the shared seam both callers use; task 4.3 adds finish cases to this class):
- `PinTests.A_rewrap_uses_the_pinned_key_not_the_users_document` (replace `users.publicKey` with an attacker's key in MongoDB; rewrap; the slot opens with Bob's private key, not the attacker's).
- `PinTests.A_forged_membership_with_a_member_slot_gets_nothing` (insert a membership and member slot naming the attacker's key, with a pin sealed under a random key → no slot written, the forged slot deleted, a warning "damaged" logged).
- `PinTests.A_pin_for_another_membership_or_vault_is_refused` (copy Bob's valid pin into Carol's membership).
- `PinTests.A_pin_whose_member_field_differs_is_refused`.
- `PinTests.Nothing_but_WrapToPinAsync_wraps_a_member_slot` (reflection/grep-style guard: `MemberSlotCrypto.Wrap` has exactly two callers, `GrantAsync` and `WrapToPinAsync`; a source scan in the test of `src/Kumbu.Server/**/*.cs`).

**Green:** as in Files.

**Mutations (mandatory):** (1) `PinnedKeyAsync` falls back to `users.publicKey` when the pin doesn't open; (2) skip the `member` field check; (3) skip the membership-id check; (4) on a bad pin, keep the member slot instead of deleting it.

**Tests:** +5.

### Task 2.5 — Every rotation makes a new keypair and rewraps the member slots to it

**Goal:** design §5.1 second paragraph, §2.3: in the rotation's transaction, open each `member` slot with the old private key, wrap the same vault key to the new public key, update the pin (it can: the vault key is in hand) with `revision++`; `PrepareUserSlotsAsync` now leaves `Wrapped` of member slots to this step and only recomputes `Lookup`, `NameId`, `Alias`.

**Files:** `Accounts/UserKeyRotation.cs:80-195`, `VaultCatalog.cs:1310-1358`, `Sharing/MemberKeys.cs`. Test: `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs`, `PinTests.cs`.

**Red:**
- `UserKeyRotationTests.A_password_change_makes_a_new_keypair_and_the_shared_vault_still_opens`.
- `UserKeyRotationTests.After_a_rotation_the_old_private_key_opens_no_member_slot` (keep the old private key from before; unwrap fails on every member slot).
- `UserKeyRotationTests.Recovery_and_re_encrypt_my_vaults_rotate_the_keypair_too`.
- `UserKeyRotationTests.A_rotation_racing_a_role_change_conflicts` (role change committing between prepare and apply via `rotation.FaultAt` → `RotationConflictException`, retried).

**Green:** as in Files; the rotation's member-slot rewrap goes through `WrapToPinAsync` with the new public key, then reseals the pin with it.

**Mutations:** (1) keep the old keypair; (2) rewrap without `revision++`; (3) `PrepareUserSlotsAsync` still rewraps `Wrapped` with `UserKek` (slot then opens nothing).

**Tests:** +4.

### Task 2.6 — Accepting creates the keypair if missing

**Goal:** design §5.1: an invitation never waits for a keypair; `GrantAsync` (and later accept) calls `MemberKeys.EnsureAsync` inside its transaction.

**Files:** `Sharing/Memberships.cs`. Test: `KeypairTests.cs`.

**Red:** `KeypairTests.Granting_to_a_user_without_a_keypair_creates_one` (user document stripped of `publicKey`/`privateKey` first).

**Green/Mutations:** call `EnsureAsync`; mutation: call it after the transaction (a crash leaves a slot under no keypair: fault seam).

**Tests:** +1.

### Task 2.7 — Security architecture for phase 2

**Files:** `docs/security-architecture.md` §3 (keypairs, member slots under public keys, pins, rotation makes a new keypair), §5.1 (P-256 DHKEM-like construction, pinned details), §5.6 (`publicKey` in the clear), §2 (ECDH is the one primitive that isn't quantum-resistant: member slots in old backups are a harvest-now target; the ex-member-plus-database residual of §5.4).

**Phase gate:** `dotnet test --solution Kumbu.slnx`.

**Expected after phase 2:** dotnet ≈ 2916 passed, 4 skipped; web unchanged.

---

## Phase 3 — Invitations, acceptance, withdrawal, revocation, leaving, role changes, the sharing UI, events and notices, the closing period

Design §4, §6, §7.2–7.4, §8.3–8.6, §10.1, §10.3; "Also in 0.3.0": metrics and the gallery for members. Coarser than phases 0–1, but task by task. REST endpoints live in `src/Kumbu.Server/Sharing/SharingApi.cs` (`MapSharingApi`, `ApiErrors.Filter`, `Errors(...)` as `PairingApi` does), tests in `tests/Kumbu.Server.Tests/Sharing/`. Regenerate `schema.d.ts` after each task that changes REST shapes.

### Task 3.1 — Creating an invitation

**Goal:** design §4.1: `POST /api/vaults/{vault}/invitations` (session-only, owner-only via `FindForUserAsync`, password in the body + password window, `Security.RequirePasswordWindow`); body label, role, `expiresInDays` 1–30 (default 7), `confirm`. One transaction: membership `pending` with `inviteLookup = HMAC(HKDF(ServerSecret, "kumbu/v1/vault-invite"), secret)` (as `Accounts/Invitations.cs:270`), the `invite` slot (`HKDF(secret, "kumbu/v1/invite-slot", salt=vaultId)`, AAD `slot:{vault}:invite:{membershipId}`, `ExpiresAt` for the TTL index), limits (10 pending incl. redeemed-unconfirmed, 20 non-revoked) via `membershipsChanged`. Refused while the owner's vault is closing. Response: link `…/share#<secret>` once.

**Files:** `Sharing/SharingApi.cs`, `Sharing/Memberships.cs` (`InviteAsync`), `VaultCatalog.Members.cs` (`InviteSlot`), `Program.cs`. Tests: create `InvitationApiTests.cs`; extend `tests/Kumbu.Server.Tests/Api/PasswordWindowTests.cs`.

**Red:** `Creating_an_invitation_returns_the_link_once_and_stores_no_secret` (BSON scan for the secret); `The_11th_pending_invitation_is_refused`; `A_member_cannot_invite` (404); `An_invitation_needs_the_password` (wrong → 403/401 as vault deletion); `PasswordWindowTests` row for the endpoint; `Expiry_beyond_30_days_is_refused`.

**Mutations:** (1) limits counted outside the transaction; (2) `inviteLookup` as plain SHA-256 (no server secret); (3) the secret in a query string of the link.

**Tests:** +7.

### Task 3.2 — Peek

**Goal:** design §4.2: `POST /api/invitations/peek` (session-only): vault name (through the invite slot, meta only, no load), owner's display name (sanitised) and username as separate fields, role, embedding provider, alias free; wrong/used secret → uniform 404 with its own rate limit, per account and per network only (`PairingApi.cs:48-51` pattern, no server-wide block).

**Files:** `Sharing/SharingApi.cs`, `VaultCatalog.Members.cs` (`PeekMetaAsync`), `Accounts/AccountLimits` (new budget). Test: `InvitationApiTests.cs`.

**Red:** `Peek_shows_vault_owner_role_and_provider_without_loading_the_vault` (`catalog.UnlockedCount` unchanged); `A_wrong_and_a_used_secret_answer_the_same_404`; `Peek_is_rate_limited_per_account_and_network_not_server_wide`; `Peek_says_whether_the_alias_is_free`; `Peek_is_refused_while_the_owner_is_inactive_or_closing`.

**Mutations:** (1) peek calls `LoadAsync`; (2) a distinct message for a used secret; (3) a server-wide counter.

**Tests:** +6.

### Task 3.3 — Accepting

**Goal:** design §4.2: `POST /api/invitations/accept` (secret, alias): one transaction — claim (`FindOneAndUpdate` on `inviteLookup`, `status == pending`, `expiresAt > now`, `member == null`), `MemberKeys.EnsureAsync`, member slot via `GrantAsync`'s core with the pin (a taken alias aborts the whole transaction, link stays usable), delete the invite slot, unset `inviteLookup`/`expiresAt`, set `member`, `redeemedAt`, `providerSeen`, and `active` + `acceptedAt` (or `pending` with `member` when `confirm`); counters on the accepting user's document and the vault document (M8). Own vault and a second membership refused.

**Files:** `Sharing/SharingApi.cs`, `Sharing/Memberships.cs` (`AcceptAsync`). Test: `InvitationApiTests.cs`.

**Red:** `Accepting_makes_Bob_a_member_under_his_alias`; `A_taken_alias_keeps_the_link_usable`; `Two_concurrent_accepts_admit_one`; `Alice_cannot_accept_her_own_invitation`; `Bob_cannot_hold_two_memberships_of_one_vault`; `An_expired_invitation_is_refused_even_before_the_TTL_monitor` (FakeTimeProvider); `With_confirm_the_membership_waits_for_the_owner`; `Accept_racing_Bobs_account_deletion_conflicts`.

**Mutations:** (1) claim without `expiresAt > now`; (2) claim outside the transaction (taken alias burns the link); (3) no counter on the user document.

**Tests:** +8.

### Task 3.4 — Confirm, decline, withdraw

**Goal:** design §4.2, §4.3: `POST …/memberships/{id}/confirm` (owner, password window; shows "Bob Example (username `bob`) accepted *Bob*'s invitation — grant access?" data from the redeemer's user document), `…/decline` (open), `DELETE …/memberships/{id}` (withdraw a pending one: `revoked`, invite slot deleted, one transaction).

**Files:** `Sharing/SharingApi.cs`, `Sharing/Memberships.cs`. Tests: `MembershipApiTests.cs` (create), `PasswordWindowTests.cs`.

**Red:** `Confirming_activates_and_shows_the_redeemers_username`; `Confirming_needs_the_password_window`; `Declining_needs_no_password_window_and_deletes_the_member_slot`; `Withdrawing_deletes_the_invite_slot`.

**Mutations:** (1) confirm without the window; (2) withdraw leaves the invite slot.

**Tests:** +5.

### Task 3.5 — Revoke and leave: the atomic step, then cleanup

**Goal:** design §6.1, §6.2: `POST …/memberships/{id}/revoke` and `POST /api/memberships/{id}/leave`: one `UpdateOne` (`revoked|left`, `revokedAt`, `revision++`); the sync hub told synchronously (`SyncHub.MembershipChanged(vaultId, memberId, membershipId)`, which for now drops the vault from the `GrantedVia` keys' sockets; sessions come in phase 5); then one idempotent cleanup transaction: member slot, every `GrantedVia` slot, `RevokeKeysWithoutVaultsAsync` (`VaultCatalog.cs:701`, made internal and counting `GrantedVia` slots as vaults), `ConnectionRevocation.RemoveVaultsAsync` (`Accounts/Connections.cs:529`). A tombstone stays with `label` and `writers`.

**Files:** `Sharing/SharingApi.cs`, `Sharing/Memberships.cs` (`RevokeAsync`, `LeaveAsync`, `CleanupAsync`), `Vaults/VaultCatalog.cs:701`, `Sync/SyncHub.cs`. Test: `MembershipApiTests.cs`.

**Red:** `Revoking_ends_every_access_at_once` (session, key, connection, sync socket of a GrantedVia key closes within the request); `Revoking_revokes_Bobs_keys_left_with_no_vault_and_keeps_the_others`; `Leaving_is_revoking_by_the_member`; `A_failed_cleanup_is_finished_by_the_sweep` (fault seam, then `MembershipSweeper.SweepAsync`); `The_tombstone_keeps_the_owners_view_of_Bob`.

**Mutations:** (1) skip the synchronous hub call; (2) `RevokeKeysWithoutVaultsAsync` ignores `GrantedVia` slots (revokes keys still holding Bob's own vaults); (3) cleanup deletes the tombstone.

**Tests:** +6.

### Task 3.6 — Role changes

**Goal:** design §6.3: `PUT …/memberships/{id}/role` (owner, session-only): one update (`role`, `roleChangedAt`, `revision++`); raising needs the password window, lowering doesn't; hub told; activity entry for the member.

**Files:** `Sharing/SharingApi.cs`, `Sharing/Memberships.cs`. Tests: `MembershipApiTests.cs`, `PasswordWindowTests.cs`.

**Red:** `Raising_a_role_needs_the_password_window`; `Lowering_a_role_needs_none_and_caps_Bobs_key_at_once`; `A_role_change_is_an_event_Bob_sees`.

**Mutations:** (1) window check on lowering too (test of lowering under a closed window fails); (2) no window check on raising.

**Tests:** +4.

### Task 3.7 — HARD RULE 2: membership writes bound to vault and caller (§6.0)

**Goal:** design §6.0: every route that changes a membership filters on `{_id, vault: <the vault id resolved from the owner's slot>}` (confirm, decline, withdraw, revoke, role) or `{_id, member: <caller>}` (leave), and on the status it may move from; nothing matched → 404, nothing changed.

**Files:** `Sharing/Memberships.cs` (one `BoundFilter(id, vaultId | memberId, fromStatuses)` used by every write). Test: create `tests/Kumbu.Server.Tests/Sharing/BoundMembershipWriteTests.cs` — **the hard-rule class** for rule 2.

**Red:**
- `BoundMembershipWriteTests.An_owner_cannot_touch_a_membership_of_another_vault` (`[Theory]` over confirm, decline, withdraw, revoke, role: Alice owns `Gartenarbeit`, Carol owns `Work` with Bob as member; Alice calls each route on her own vault's URL with Bob's `Work` membership id → 404; the membership byte-for-byte unchanged).
- `BoundMembershipWriteTests.An_owner_cannot_raise_her_own_role_in_someone_elses_vault` (Alice is a read member of Carol's vault; `PUT /api/vaults/Gartenarbeit/memberships/{her Carol-vault membership}/role` maintain → 404).
- `BoundMembershipWriteTests.A_member_cannot_leave_for_someone_else` (Bob posts leave with Carol's membership id → 404).
- `BoundMembershipWriteTests.A_route_may_only_move_from_its_statuses` (`[Theory]`: confirm an active one, withdraw an active one, revoke a revoked one → 404).

**Green:** as in Files; each route resolves the vault id with `FindForUserAsync` first.

**Mutations (mandatory):** (1) drop `vault` from the revoke filter; (2) the role route filters on `_id` only; (3) leave filters on `_id` and status only; (4) confirm filters on status `pending|active`.

**Tests:** +13.

### Task 3.8 — The membership sweep

**Goal:** design §2.1, §2.4: `MembershipSweeper` (`VaultSweeper`'s pattern, `Vaults/VaultSweeper.cs`): expired `pending` → `revoked`, stray `invite` slots removed, redeemed-unconfirmed after 7 days from `redeemedAt` → `revoked` with its member slot deleted, unfinished cleanups retried; idempotent.

**Files:** create `Sharing/MembershipSweeper.cs`, `Program.cs`. Test: create `MembershipSweepTests.cs`.

**Red:** `An_expired_invitation_is_revoked_and_its_slot_removed`; `An_unconfirmed_redemption_lapses_after_7_days`; `The_sweep_is_idempotent`; `The_sweep_finishes_a_cleanup`.

**Mutations:** (1) lapse counted from `createdAt`; (2) the sweep leaves the member slot of a lapsed redemption.

**Tests:** +4.

### Task 3.9 — The owner's access list

**Goal:** design §8.6: `GET /api/vaults/{vault}/access` (`Accounts/Rekeying.cs:135`, `Api/NoteApi.cs` `AccessAsync`) gains memberships (label, role, status, accepted, derived credentials by name and kind from `writers`, and **last use on this vault** from `usedAt`, decision 9).

**Files:** `Accounts/Rekeying.cs`, `Api/NoteApi.cs:25-28` (`VaultAccessView`), `Vaults/VaultCatalog.Members.cs` (`usedAt` throttle). Test: `MembershipApiTests.cs`.

**Red:** `The_access_list_shows_Bob_his_role_and_his_credentials_by_name`; `Last_use_is_the_use_on_this_vault_not_the_keys_global_one` (Bob's key used on his own vault after; the list keeps the older time); `A_member_gets_404_for_the_access_list`.

**Mutations:** (1) last use from the key document; (2) `usedAt` written on every request (a second request within a minute must not write: profiler count).

**Tests:** +3.

### Task 3.10 — What members see: `VaultView`, `ls /`, `help`, "last written by another user"

**Goal:** design §8.6, §10.3: `VaultView` (`Api/NoteApi.cs:25`) gains `owner` (display name, null for own), `level`, `shared`; `canWrite` derived. `ls /` lists `gartenarbeit (shared by Alice, read)`; `help` mentions it; in a shared vault `cat`, `rg` and `semsearch` mark a note last written by someone other than the caller's own user ("last written by another user").

**Files:** `Api/NoteApi.cs`, `src/Kumbu.Core/Sh/Commands/Ls.cs`, `Help.cs`, `Cat.cs`, `Rg.cs`, `Semsearch.cs`, `src/Kumbu.Core/Notes/INoteStore.cs` (`VaultInfoAsync(vault)` → owner display, level, shared), `EncryptedNoteStore.cs`, `InMemoryNoteStore.cs`. Tests: `tests/Kumbu.Core.Tests/Sh/LsTests.cs`, `CatTests.cs`, `RgTests.cs`, `HelpTests.cs`; server `MemberViewTests.cs` (create).

**Red:** `LsTests.A_shared_vault_shows_its_owner_and_the_callers_level`; `CatTests.A_note_last_written_by_another_user_is_marked`; `CatTests.The_callers_own_agents_writes_are_not_marked`; `RgTests.…marked`; `MemberViewTests.VaultView_carries_owner_level_and_shared`; `HelpTests.Help_mentions_shared_vaults`.

**Mutations:** (1) mark compares labels instead of references (members store `""`: everything or nothing marked); (2) `ls /` prints the sealed level instead of the effective one.

**Tests:** +7.

### Task 3.11 — Events (`vault_events`)

**Goal:** design §8.3: plain event documents (vault, kind, membership, at), text built at read time with `WriterLabels`; visible to the owner and the member concerned; deleted with the vault. Kinds: invitation created/withdrawn/accepted/confirmed, membership revoked/left, role changed, member export, provider changed, re-key finished, closing started, owner inactive.

**Files:** create `Sharing/VaultEvents.cs`; `Api/HistoryApi.cs` activity endpoint merges them; `Vaults/VaultCatalog.cs:564` (`DeleteVaultsAsync` deletes them). Test: create `VaultEventTests.cs`; extend `MarkerTests.cs`.

**Red:** `The_owner_sees_invited_accepted_and_revoked`; `Bob_sees_his_role_change_as_Alice_changed_your_role_to_write`; `Carol_does_not_see_Bobs_events`; `Events_go_with_the_vault`; `MarkerTests.Events_never_name_another_member_to_a_member`.

**Mutations:** (1) member sees all events of the vault; (2) event text stored instead of built at read.

**Tests:** +5.

### Task 3.12 — One-time notices to agents

**Goal:** design §8.4: a `CallToolFilter` beside `RequiredArguments.Filter` (`Mcp/RequiredArguments.cs:24`, registered at `Program.cs:160`) appends a `TextContentBlock` once per (credential, event); marks in `notice_marks` (TTL 30 days).

**Files:** create `src/Kumbu.Server/Mcp/Notices.cs`; `Program.cs`. Test: create `tests/Kumbu.Server.Tests/Mcp/NoticeTests.cs`.

**Red:** `A_role_change_reaches_Bobs_agent_once`; `Two_agents_of_Bob_each_get_it_once`; `The_owners_agents_get_no_member_notice`; `A_notice_mark_expires_after_30_days` (TTL index present).

**Mutations:** (1) mark per user instead of per credential; (2) mark written before the result is returned and lost on error (append before marking).

**Tests:** +4.

### Task 3.13 — Embedding provider: the owner's sentence and the member's consent

**Goal:** design §8.5: the owner's dialog data states the member count; members get the event, a banner ("OK", "Leave this vault") and the agents' notice; while `providerSeen` differs from the vault's provider the shared vault is left out of the member's searches (`semsearch /`, web search) with the existing "left out" notice naming why; `POST /api/memberships/{id}/provider-seen` sets it (bound to the caller as rule 2).

**Files:** `Api/NoteApi.cs:200-218`, `src/Kumbu.Server/Search/SearchIndex.cs` (filter by access record), `Sharing/SharingApi.cs`. Test: create `ProviderConsentTests.cs`.

**Red:** `A_changed_provider_leaves_the_shared_vault_out_of_Bobs_searches`; `Bobs_OK_brings_it_back`; `Reading_and_writing_go_on_meanwhile`; `The_owners_answer_counts_her_members`; `provider_seen_is_bound_to_the_caller`.

**Mutations:** (1) left out only for `semsearch /`, not for a search of the vault itself; (2) `provider-seen` filters on `_id` only.

**Tests:** +5.

### Task 3.14 — Export by members

**Goal:** design §3.3: all three export routes (vault export, checkpoint export, `/api/files/{vault}/zip`; `Api/ExportApi.cs:45-65`) treat a member like the owner, with the same password ticket; each member export is an event ("Bob exported *Gartenarbeit*").

**Files:** `Api/ExportApi.cs`, `Sharing/VaultEvents.cs`. Test: `tests/Kumbu.Server.Tests/Api/ExportApiTests.cs`.

**Red:** `The_three_export_routes_treat_a_member_like_the_owner` (`[Theory]`, one test pins them alike); `A_member_export_is_an_event_for_the_owner`; `A_member_export_needs_the_password_ticket`.

**Mutations:** (1) the files ZIP route without the event; (2) member export without the ticket.

**Tests:** +5.

### Task 3.15 — Owner deactivated: read-only for members, export allowed

**Goal:** design §7.4: the opening methods read the owner's `active` with the membership (one indexed lookup); effective level `Read` for every member; writes refused with `code: owner-inactive` through `CheckWritable` (`Vaults/VaultCatalog.Rekey.cs:123`), indexing stops (`MayIndex`); export allowed; banner data and the agents' notice; reactivation lifts it on the next request.

**Files:** `VaultCatalog.Members.cs`, `VaultCatalog.Rekey.cs:123-132` (`CheckWritable(vault, access)` gains the reason → `VaultReadOnlyException(code, sentence)`, 403 in `ApiErrors.Filter`), `Accounts/Admin.cs:56-100` (event, hub). Test: create `OwnerInactiveTests.cs`.

**Red:** `A_deactivated_owners_shared_vault_is_read_only_for_members_on_every_route` (`[Theory]` routes, `code: owner-inactive`); `Members_may_export_it`; `Indexing_stops`; `Reactivation_lifts_it_on_the_next_request`.

**Mutations:** (1) check only in the REST layer, not in `CheckWritable` (MCP write passes); (2) `MayIndex` unchanged.

**Tests:** +8.

### Task 3.16 — A member's account deletion: the receipt and "a former member"

**Goal:** design §7.2: on top of task 1.15, the receipt (`DeletionReceiptView`, `Api/AccountApi.cs:110`) gains counts of memberships ended and shared vaults closed, and the sentence "Notes you wrote in vaults shared with you stay in those vaults; your name was removed from their history."; no vault names.

**Files:** `Accounts/Deletion.cs:12` (`DeletionSummary`), `Api/AccountApi.cs:110`. Test: `tests/Kumbu.Server.Tests/Api/AccountApiTests.cs`.

**Red:** `The_receipt_counts_ended_memberships_and_names_no_vault`; `After_deletion_nothing_in_the_vault_names_Bob` (byte scan of all of the vault's documents opened with the vault key, memberships included).

**Mutations:** (1) tombstone kept; (2) receipt lists vault names.

**Tests:** +2.

### Task 3.17 — The owner deletes her account: the closing period

**Goal:** design §7.3: "Give members 7 days" (default) sets `closingAt` on the vault document in the deletion transaction; owner slot, account and credentials go at once; the vault survives through member slots and is excluded from `OwnedVaultsAsync`; writes refused with `code: closing` via `CheckWritable`; `MayIndex` false; members may export; a sweep deletes at `closingAt` (`DeleteVaultsAsync`); `DeletionList` records the vault ids and the choice so `RepeatAsync` (`Deletion.cs:120`) re-applies it after a restored backup; the admin's deletion (`Deletion.DeleteUserAsync`, `:92`) offers the same choice once for all; "Delete it for everyone now" runs today's deletion and leaves a `member_notices` document per member (alias ciphertext copy, dismissed or 30 days); her open invitations are withdrawn.

**Files:** `Vaults/Documents.cs:21` (`ClosingAt`), `Accounts/Deletion.cs`, `Accounts/DeletionList.cs`, `Sharing/MembershipSweeper.cs`, `Sharing/SharingApi.cs` (notices, dismiss), `Api/AccountApi.cs`. Test: create `ClosingPeriodTests.cs`; extend `tests/Kumbu.Server.Tests/Accounts/DeletionListTests.cs`.

**Red:** `Give_members_7_days_keeps_the_vault_read_only_and_exportable`; `The_sweep_deletes_it_at_closingAt`; `A_restored_backup_deletes_a_vault_whose_closing_passed_and_closes_one_whose_didnt` (`RepeatAsync`); `The_admins_deletion_applies_its_choice_to_every_shared_vault`; `Delete_for_everyone_now_leaves_a_notice_under_the_members_alias_naming_no_owner`; `The_owners_open_invitations_are_withdrawn`; `Writes_are_refused_with_code_closing_on_every_route`; `Accepting_an_invitation_to_a_closing_vault_is_refused`.

**Mutations:** (1) `closingAt` set after the transaction; (2) `OwnedVaultsAsync` still includes the closing vault (deleted at once); (3) `DeletionList` without the choice (repeat deletes at once).

**Tests:** +8.

### Task 3.18 — Sharing metrics

**Goal:** design §12 "Also in 0.3.0": `kumbu_invitations_total{outcome}` (created, accepted, declined, withdrawn, expired, refused), `kumbu_membership_changes_total{event}` (revoked, left, role_raised, role_lowered), gauges `kumbu_shared_vaults`, `kumbu_memberships{status}` read on scrape (`KumbuMetrics.ReadOnScrape`); enum labels only.

**Files:** `src/Kumbu.Server/Metrics/KumbuMetrics.cs`, `Sharing/Memberships.cs`. Tests: `tests/Kumbu.Server.Tests/Metrics/ServerMetricsTests.cs`, `LabelGuardTests.cs` (label values from the fixed sets only).

**Red:** `ServerMetricsTests.An_accepted_invitation_counts_once`; `ServerMetricsTests.Gauges_count_shared_vaults_and_memberships`; `LabelGuardTests` row: no label value equals a vault id, user id or label used in the test.

**Mutations:** (1) outcome label from an exception message; (2) gauge counts revoked memberships as active.

**Tests:** +3.

### Task 3.19 — Gallery, lightbox and attachment history for members (server)

**Goal:** design §12 "Also in 0.3.0", concept §13: attachments follow the level like notes (no upload, replace or delete below `write`, `code: read-only`); attachment history writers through `WriterLabels`.

**Files:** `Api/FileApi.cs`, `Api/UploadApi.cs`, `Api/HistoryApi.cs`. Test: `tests/Kumbu.Server.Tests/Api/FileApiTests.cs`, `UploadApiTests.cs`.

**Red:** `A_read_member_may_view_but_not_upload_replace_or_delete_files` (`[Theory]`); `Attachment_history_shows_another_member_as_another_user`.

**Mutations:** (1) replace (PUT on an existing file) skips `Refuse`; (2) attachment history bypasses `WriterLabels`.

**Tests:** +5.

### Task 3.20 — Web: API client, vault list "Your vaults" / "Shared with me"

**Goal:** design §10.1: grouping by owner in `VaultOverview` (`web/src/shell/MobileShell.tsx:56-98`), `VaultSwitcher.tsx`, `TreeDrawer.tsx`; `level` in place of `canWrite`.

**Files:** `web/src/api/schema.d.ts` (regenerated), `web/src/api/notes.ts`, `web/src/shell/MobileShell.tsx`, `VaultSwitcher.tsx`, `TreeDrawer.tsx`. Tests: `web/src/shell/MobileShell.test.tsx`, `TreeDrawerFolders.test.tsx`.

**Red:** "groups shared vaults under 'Shared with me' by owner"; "opens a read-only shared vault without write actions"; "shows Board.md read-only below maintain".

**Mutations:** (1) group by `shared` instead of `owner`; (2) write actions keyed on `canWrite` only (maintain-only actions shown for write).

**Tests:** web +6.

### Task 3.21 — Web: share dialog and access list

**Goal:** design §10.1: share dialog (label, role with `maintain` in the warning colour and its sentence, expiry, "Ask me to confirm", password; the link once with copy and the safe-channel hint; the two sentences about deletions and old notes); access list (role change, confirm with the redeemer's username, revoke with the re-encrypt option off by default (enabled in phase 4) and its sentence, derived credentials).

**Files:** create `web/src/shell/ShareDialog.tsx`, `web/src/shell/AccessList.tsx`, `web/src/api/sharing.ts`, their tests.

**Red:** `ShareDialog.test.tsx`: "shows the link once and forgets it on close", "shows maintain in the warning colour", "needs the password"; `AccessList.test.tsx`: "raises a role after the password dialog", "confirms showing the username", "revokes".

**Mutations:** (1) link kept in the query cache after close; (2) the role select sends the raise without going through `api` (the password-window middleware), so a closed window shows an error instead of the password dialog.

**Tests:** web +10, files +2.

### Task 3.22 — Web: accept page `/share`

**Goal:** design §4.2, §10.1: reads the fragment, keeps the secret only in `sessionStorage` across sign-in, returns to plain `/share`; peek, then alias (prefilled, must change on collision), accept.

**Files:** create `web/src/routes/Share.tsx` and test; `web/src/router.tsx`.

**Red:** "keeps the secret in sessionStorage and never in the URL after sign-in" (assert `location.search`/`hash` empty after redirect); "asks for another alias on collision"; "shows owner display name and username separately".

**Mutations:** (1) put the secret into a `?next=` return URL; (2) clear `sessionStorage` before accept succeeds.

**Tests:** web +6, files +1.

### Task 3.23 — Web: member's side, banners, credential dialogs

**Goal:** design §10.1, §8.5, §7.3, §7.4: role, owner, provider, "Leave this vault", alias rename; banners (provider changed with OK/Leave, closing date, owner inactive); key, connection and device dialogs list shared vaults, show a capped credential ("write, acts as read while you can only read *Gartenarbeit*") and warn for mixed credentials ("Others can write into *Gartenarbeit*. …").

**Files:** create `web/src/shell/MemberPanel.tsx`, `web/src/shell/SharedBanner.tsx` and tests; modify `web/src/routes/NewKeyDialog.tsx`, `Keys.tsx`, `Connections.tsx`, `NewConnectionDialog.tsx`, `web/src/shell/ConnectObsidianDialog.tsx`.

**Red:** "leaves after confirmation"; "renames the alias"; "shows the provider banner until OK"; "shows a capped key as acts as read"; "warns when one key covers a shared writable vault and a private one"; "shows the closing date".

**Mutations:** (1) warning shown only when every vault is shared; (2) capped text uses the sealed level.

**Tests:** web +12, files +2.

### Task 3.24 — Web: gallery and lightbox follow the level

**Goal:** design §12 "Also in 0.3.0": no upload, replace or delete controls below `write` in the gallery, lightbox and attachment history; writer names from the server.

**Files:** `web/src/attachments/*` (gallery, lightbox), their tests; `web/src/upload/*`.

**Red:** "a read member sees no upload, replace or delete"; "attachment history shows 'another user'".

**Mutations:** (1) delete button keyed on `canWrite` of the wrong vault.

**Tests:** web +4, files +1.

### Task 3.25 — Security architecture for phase 3

**Files:** `docs/security-architecture.md` §5.6 (`vault_events`, `notice_marks`, `member_notices`), §6 (new subsection "Sharing a vault": invitations, the link in the fragment, peek, accept, confirm, revoke, leave, role changes and the password window, the closing period, the owner deactivated, member export with ticket and event), §2 (the prompt-injection path across shared and private vaults, M6, and its mitigations), §8 (a shared vault's notes reach the providers of every member's agents; the member's search consent).

**Phase gate:** `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run`, `pnpm -C web build`.

**Expected after phase 3:** dotnet ≈ 3024 passed, 4 skipped; web ≈ 80 files, ≈ 1229 tests.

---

## Phase 4 — Re-keying shared vaults, with recovery and "Re-encrypt my vaults"

Design §7.1, §13 M8, L1, L2, L9.

### Task 4.1 — The start keeps member slots, ends invitations, declines unconfirmed redeemers

**Goal:** design §7.1: `StartRekeyAsync` (`Vaults/VaultCatalog.Rekey.cs:333`) no longer refuses (task 1.13's check removed); it keeps `member` slots; deletes `invite` slots and sets their memberships `revoked`; declines redeemed-unconfirmed memberships; deletes only members' `GrantedVia` slots on this vault and revokes only their keys left with no vault (`RevokeKeysWithoutVaultsAsync`); bumps every membership's `revision`. The owner's own credentials go whole, as today.

**Files:** `VaultCatalog.Rekey.cs:333-405`, `Accounts/Rekeying.cs`, `Sharing/Memberships.cs`. Test: create `tests/Kumbu.Server.Tests/Sharing/SharedRekeyTests.cs`; delete the refusal tests of `SharedRekeyRefusalTests.cs` (replaced).

**Red:** `The_start_keeps_member_slots_and_ends_open_invitations`; `The_start_declines_an_unconfirmed_redeemer`; `The_start_revokes_only_Bobs_keys_left_without_a_vault`; `The_start_bumps_every_memberships_revision`.

**Mutations:** (1) `Nin` list without `member` (members' slots deleted at the start); (2) unconfirmed memberships kept; (3) Bob's key revoked whole.

**Tests:** +4.

### Task 4.2 — Only the owner's key drives a run

**Goal:** design §7.1: a member's session meeting the placeholder must not start or resume a run (`RekeyPendingFound`, `VaultCatalog.cs:223`; `RekeyRunner.cs:70`); members see "being re-encrypted".

**Files:** `VaultCatalog.cs:220-224` (only for kind `user` slots). Test: `SharedRekeyTests.cs`.

**Red:** `A_member_meeting_the_placeholder_does_not_resume_the_run` (runner's start count unchanged; no user key of Bob reaches `RekeyRunner`); `Members_see_being_re_encrypted`.

**Mutations:** (1) raise `RekeyPendingFound` for member slots.

**Tests:** +2.

### Task 4.3 — The finish wraps to pins (HARD RULE 3 continued)

**Goal:** design §7.1 finish, §5.4: in the finish transaction (`VaultCatalog.Rekey.cs:413`), for every `active` membership, re-read with a filter on status and `revision`, `WrapToPinAsync` with the old key (pin) and the new key, reseal `pin`, `label`, `writers`, `writerIds` under the new key (L1); a member slot without a valid pin is deleted and logged; the finish never creates member slots; a revoke/leave/role change during the run conflicts with the finish.

**Files:** `VaultCatalog.Rekey.cs:413-470`, `Sharing/Memberships.cs`. Test: `tests/Kumbu.Server.Tests/Sharing/PinTests.cs` (the rule-3 class) and `SharedRekeyTests.cs`.

**Red:**
- `PinTests.The_finish_wraps_the_new_key_to_the_pinned_key_not_the_users_document`.
- `PinTests.The_finish_creates_no_member_slot_for_a_forged_membership` (and deletes the forged slot, logged).
- `PinTests.The_finish_reseals_pin_label_writers_and_writerIds_under_the_new_key`.
- `SharedRekeyTests.Bob_reads_the_vault_after_the_owners_re_key`.
- `SharedRekeyTests.A_revoke_during_the_run_conflicts_with_the_finish_and_Bob_gets_no_new_key` (fault seam `RekeyFaultAt("before-finish")`, revoke, finish retried; Bob's slot gone).

**Mutations (mandatory):** (1) the finish reads the public key from `users.publicKey`; (2) finish without the `revision` filter; (3) `label` not resealed (opens nothing after); (4) the finish inserts a member slot for an active membership that had none.

**Tests:** +6.

### Task 4.4 — The old-key guard covers member-derived slots

**Goal:** design §13 M8 last bullet: a `GrantedVia` slot wrapped from a vault key other than the current one is refused, as for slots derived from user slots (`RequireSameVaultKeysAsync`, `VaultCatalog.Rekey.cs:149`).

**Files:** `VaultCatalog.Rekey.cs:149-175`, `Accounts/ApiKeys.cs`, `OAuth/OAuthTokens.cs`. Test: `SharedRekeyTests.cs`.

**Red:** `A_member_key_wrapped_with_the_old_key_during_a_finish_is_refused` (`IssueFaultAt("after-wrap")` while the finish commits).

**Mutations:** (1) `RequireSameVaultKeysAsync` reads only `user` slots.

**Tests:** +2.

### Task 4.5 — "Re-encrypt my vaults", recovery and deletions during a run

**Goal:** design §7.1 last paragraphs: they reach shared vaults through the owner's slots (`PendingVaultIdsAsync`, `VaultCatalog.Rekey.cs:188`) and run the same steps; a member's recovery touches only her keypair and member slots; deleting a shared vault or the owner's account while a run is in progress is refused ("A re-encryption is still running; try again in a few minutes.").

**Files:** `Accounts/Rekeying.cs`, `Accounts/Deletion.cs`. Test: `SharedRekeyTests.cs`, `tests/Kumbu.Server.Tests/Accounts/RekeyingTests.cs`.

**Red:** `Re_encrypt_my_vaults_re_keys_the_shared_vault_and_Bob_keeps_access`; `The_owners_recovery_re_keys_the_shared_vault`; `A_members_recovery_re_keys_no_vault`; `Deleting_a_shared_vault_during_a_run_is_refused`; `Deleting_the_owners_account_during_a_run_is_refused`.

**Mutations:** (1) recovery still skips shared vaults (leftover of 1.13); (2) deletion check only for the vault route.

**Tests:** +5.

### Task 4.6 — Events, notices and the re-encrypt offer after revoke and leave

**Goal:** design §6.4, §8.3: re-key finished and credentials revoked are events and agent notices for members; the revoke dialog's and the leave event's re-encryption offer, off by default, with the concept's sentence.

**Files:** `Sharing/VaultEvents.cs`, `Mcp/Notices.cs`, `web/src/shell/AccessList.tsx` and test. Test: `VaultEventTests.cs`, `NoticeTests.cs`.

**Red:** `Members_are_told_to_give_their_agents_the_vault_again`; web "offers re-encryption after revoke, off by default, with its sentence".

**Mutations:** (1) offer ticked by default.

**Tests:** +3 server, web +3.

### Task 4.7 — Security architecture for phase 4

**Files:** `docs/security-architecture.md` §3 "Key rotation and re-keying" (shared vaults: members kept and rewrapped to pins, invitations withdrawn, unconfirmed redeemers declined, members' derived slots only; stated residuals: an open link plus a backup taken during its lifetime gives the vault key; vault keys a compromised member held stay exposed until the owner re-keys).

**Phase gate:** `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run`, `pnpm -C web build`.

**Expected after phase 4:** dotnet ≈ 3046 passed, 4 skipped; web ≈ 80 files, ≈ 1232 tests.

---

## Phase 5 — Live updates and presence; the server side of plugin 0.2.0

Design §9, §10.2, §3.5. The plugin itself is out of scope (brief).

### Task 5.1 — Tickets for sessions; subjects

**Goal:** design §9.1: `POST /api/ws-ticket` (`Api/SyncApi.cs:54`) accepts sessions; `TicketGrant` (`Sync/SyncTickets.cs:10`) carries a subject `{kind: key|session, id, user}`; `SyncHub.MaxPerKey` (`Sync/SyncHub.cs:29`) per subject.

**Files:** `Api/SyncApi.cs`, `Sync/SyncTickets.cs`, `Sync/SyncHub.cs:64-105`. Test: `tests/Kumbu.Server.Tests/Api/SyncSocketTests.cs`, `SyncApiTests.cs`.

**Red:** `A_session_gets_a_ticket_and_hears_hints_under_its_names`; `A_fifth_tab_replaces_the_oldest_of_that_session`; `Two_sessions_of_one_user_are_two_subjects`.

**Mutations:** (1) cap per user instead of per subject; (2) session tickets bound to the user only.

**Tests:** +4.

### Task 5.2 — The 30-second check recomputes the vault set; sessions aren't kept alive

**Goal:** design §9.1, L7: `SyncHub.CheckAsync` (`:112`) validates the subject (key active; session alive and user active, resolved with `Sessions.ResolveAsync(touch: false)`), recomputes the vault set from slots and active memberships, drops lost vaults and updates levels; closes 4401 for a dead subject and at the session's idle end.

**Files:** `Sync/SyncHub.cs`. Test: `SyncSocketTests.cs`.

**Red:** `The_check_drops_a_vault_whose_membership_was_revoked_behind_the_servers_back`; `An_open_tab_does_not_keep_its_session_alive` (FakeTimeProvider: idle 31 min → 4401; `lastUsedAt` unchanged); `A_deactivated_users_session_socket_closes_with_4401`.

**Mutations:** (1) resolve with `touch: true`; (2) vault set from the ticket only.

**Tests:** +5.

### Task 5.3 — Synchronous hub notifications on every access change

**Goal:** design §6.1, §9.1, M1: revoke, leave, role change, deactivation, owner inactive call the hub before answering; it drops or re-levels the vault for the affected subjects (sessions now too), for hints and presence.

**Files:** `Sync/SyncHub.cs` (`MembershipChanged`, `UserDeactivated`), `Sharing/Memberships.cs`, `Accounts/Admin.cs`. Test: `SyncSocketTests.cs`.

**Red:** `Revoking_drops_the_vault_from_Bobs_tab_before_the_answer`; `Lowering_to_read_stops_Bobs_presence_frames_at_once`; `Deactivating_the_owner_re_levels_members_to_read`.

**Mutations:** (1) notify after the response (fire and forget, test with a gate); (2) drop only key subjects.

**Tests:** +4.

### Task 5.4 — Presence

**Goal:** design §9.2: inbound `{"type":"editing","vault","path","on"}` ≤ 1 KiB, only from a session subject with level ≥ `write`; `(vaultId, path) → connections`, cleared on `on:false`, close, vault drop, 60 s without refresh; outbound `{"type":"presence",…,"editors":[…]}` rendered per receiving connection by `WriterLabels` (owner sees names; a member sees the owner by name and "another user"s collapsed into a count); own channel per connection, last state wins per path.

**Files:** `Sync/SyncHub.cs`, create `src/Kumbu.Server/Sync/Presence.cs`. Test: create `tests/Kumbu.Server.Tests/Api/PresenceTests.cs`.

**Red:** `The_owner_sees_Bob_editing`; `Bob_sees_Alice_and_one_other_user`; `A_read_member_cannot_announce_editing`; `An_entry_expires_after_60_seconds`; `An_off_is_never_lost_in_a_burst_of_hints` (1000 hints, then off → last presence state empty); `A_frame_over_1_KiB_is_ignored`.

**Mutations:** (1) presence on the hint channel (drop-oldest loses the off); (2) names rendered once for all receivers; (3) level check `>= Read`.

**Tests:** +7.

### Task 5.5 — The marker test reaches the socket (HARD RULE 5 continued)

**Goal:** design §8.2: no socket message to member B carries anything of member A.

**Files:** `tests/Kumbu.Server.Tests/Sharing/MarkerTests.cs`.

**Red:** `MarkerTests.A_label_written_by_member_A_never_reaches_member_B_over_the_socket` (Carol, display name `MARKER-…`, edits in the web mode and writes; Bob's socket transcript has no `MARKER`, username or ids).

**Mutations (mandatory):** (1) presence editors carry the display name for every receiver; (2) hints include the writer.

**Tests:** +2.

### Task 5.6 — Web: one socket per tab

**Goal:** design §9.1: `useSyncSocket` in the shell (`web/src/shell/MobileShell.tsx`) invalidates `["tree", v]`, `["vaults"]`, `["trash", v]`, `["checkpoints", v]`, `["changes"]`, history and the open note's version (`vaultChanged()`, `web/src/api/history.ts:22`, `notes: false`); `useVersionPoll` (`history.ts:56`) runs only while the socket is down. Closes kumbukumbu#116.

**Files:** create `web/src/hooks/useSyncSocket.ts` and test; `web/src/shell/MobileShell.tsx`, `web/src/api/history.ts`.

**Red:** "invalidates the tree on a hint"; "stops the version poll while connected and resumes it when the socket drops"; "reconnects with a fresh ticket"; "maps the caller's vault name".

**Mutations:** (1) poll keeps running while connected; (2) reconnect reuses the old ticket.

**Tests:** web +6, files +1.

### Task 5.7 — Web: the editor names writers and shows presence

**Goal:** design §9.3: "Updated by Bob just now", "Bob saved a newer version" from the server's writer display; presence line above the note in Write mode; Write mode refused for `/Board/Board.md` below `maintain`, below `write`, and during `closing`, with the refusal's sentence (branching on `code`).

**Files:** `web/src/note/MarkdownEditor.tsx:194-205`, `ChangedElsewhere.tsx`, `NoteView.tsx`, create `web/src/note/PresenceLine.tsx` and test.

**Red:** "shows 'Bob, Carol and 2 other users are editing this note'"; "refuses Write mode on Board.md for a write member with the structural sentence"; "names the writer of a newer version from the server"; "sends editing on/off and refreshes every 30 s".

**Mutations:** (1) branch on the message text instead of `code`; (2) no `on:false` on leaving Write mode.

**Tests:** web +8, files +1.

### Task 5.8 — Plugin 0.2.0, server side: `plugin` on redemption

**Goal:** design §10.2, §3.5: refusal codes exist since phase 0; `RedeemPairingRequest(Code, Device)` (`Api/PairingApi.cs:17`) gains `plugin` (version string); a shared vault is refused without `plugin >= 0.2.0` ("Update the Kumbu² plugin to 0.2.0 or newer to sync a shared vault."), **before the code is consumed**; task 1.14's refusal of creating a pairing code for a shared vault is lifted; own vaults pair as before (maintain).

**Files:** `Api/PairingApi.cs`, `Accounts/Pairings.cs` (peek the record's vault before consuming). Test: `tests/Kumbu.Server.Tests/Api/PairingApiTests.cs`.

**Red:** `Pairing_a_shared_vault_without_a_plugin_version_is_refused_and_the_code_stays_usable`; `…_with_plugin_0_1_9_is_refused`; `…_with_plugin_0_2_0_pairs_with_GrantedVia`; `An_own_vault_pairs_without_a_plugin_version`; `A_malformed_plugin_version_is_refused_as_too_old`.

**Mutations:** (1) check after consuming the code; (2) string comparison of versions (`"0.10.0" < "0.2.0"`); (3) the check only for members, not for the owner of a shared vault.

**Tests:** +5.

### Task 5.9 — Security architecture for phase 5

**Files:** `docs/security-architecture.md` §6 "Device pairing and sync" (sessions on the socket, subjects, the 30-second recomputation, synchronous drops, presence rendered per receiver, sessions not kept alive; plugin 0.2.0 required for shared vaults).

**Phase gate:** `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run`, `pnpm -C web build`.

**Expected after phase 5:** dotnet ≈ 3073 passed, 4 skipped; web ≈ 82 files, ≈ 1246 tests.

---

## After phase 5

The report (`docs/superpowers/overnighter/2026-10-31-collab-overnighter-report.md`), the draft pull request and the row in `docs/superpowers/open-items.md`, as the brief says. No version bump, no tag.
