# BYOK embeddings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On a server that embeds internally, people may save their own TensorX key and have their vaults embedded with it.

**Architecture:** A built-in provider `tensorx-byok` whose driver exists once per key (`ByokDrivers`, cached by the key's hash), so a refused key pauses only its own driver. The key is sealed on the user document (as the discreet list is) and copied into the sealed meta of every vault that uses it; at unlock the vault carries its key. The search index and the search route each vault by a *route* (the provider's name, or `tensorx-byok#<key hash>`), with a queue per route created on first use.

**Tech Stack:** C# / ASP.NET Core minimal APIs, MongoDB, xUnit; React 19, TanStack Query, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-18-byok-embeddings-design.md`

## Global Constraints

- Setting `Kumbu:Embeddings:Byok` (Compose `KUMBU_EMBEDDINGS_BYOK`), default `false`; effective only while `Kumbu:Embeddings:Default` is `internal`; otherwise ignored with one startup log line.
- Preset: BaseUrl `https://api.tensorx.ai/v1`, Model `qwen/qwen3-embedding-8b`, Dimensions `1024`, QueryPrompt `"Instruct: Given a search query, retrieve the note sections that answer it\nQuery: "`, Label `TensorX (your own key)`, Description `As TensorX states: EU (Ireland), zero data retention`.
- Provider name `tensorx-byok`; model key `tensorx-byok:qwen/qwen3-embedding-8b@1024`.
- Key: printable ASCII (0x21–0x7E), 8 to 512 characters. Never logged, never in an API answer (only its last four characters), never in history.
- Discreet vaults never use BYOK; only the vault's owner chooses it; agents can't set the key (sessions only).
- British English; `// SPDX-License-Identifier: AGPL-3.0-or-later` on new files.

## Review Focus

- A key saved while TensorX is unreachable: the save answers "couldn't check the key, try again" (503) and stores nothing. Test in Task 5.
- A vault on BYOK whose owner removed the key in another session meanwhile: it unlocks as `internal`, never as an unconfigured provider. Test in Task 3.
- Admin switches BYOK off, or makes the default external: BYOK vaults search as `internal` on their next unlock, and their meta keeps the key. Test in Task 3.
- A member of a shared BYOK vault searches: the owner's key embeds the query, after the member's consent, as for any external provider. Test in Task 5.
- A rotation of the user key (password reset by recovery): the account key is resealed, not lost. Test in Task 4.

---

### Task 1: The setting and the preset

**Files:** Modify `src/Kumbu.Server/KumbuOptions.cs`, `src/Kumbu.Server/Program.cs`, `compose.yaml`, `.env.example`; Create `src/Kumbu.Server/Search/Byok.cs`; Test `tests/Kumbu.Server.Tests/Search/ByokTests.cs`.

**Produces:** `EmbeddingsOptions.Byok: bool`; `EmbeddingsOptions.ByokOffered: bool` (`Byok && Default == internal`); `static class Byok { const string Provider = "tensorx-byok"; static EmbeddingProviderOptions Preset; static bool IsKey(string?) }`.

- [ ] Test `The_switch_counts_only_with_an_internal_default` (three deployments a, b, c), `A_key_is_printable_ascii_of_8_to_512` (accepts `sk-abc12345`, refuses 7 chars, 513 chars, a space, a newline, `ä`).
- [ ] Implement; Program.cs logs once at startup when `Byok && !ByokOffered`.
- [ ] compose.yaml: `Kumbu__Embeddings__Byok: ${KUMBU_EMBEDDINGS_BYOK:-false}`; `.env.example` a commented line.
- [ ] Run tests, commit.

### Task 2: A driver per key

**Files:** Modify `src/Kumbu.Server/Search/OpenAiCompatibleDriver.cs` (optional `Action? keyRefused` invoked on 401/403); Create `src/Kumbu.Server/Search/ByokDrivers.cs`; Test `tests/Kumbu.Server.Tests/Search/ByokDriversTests.cs`.

**Produces:** `sealed class ByokDrivers(IHttpClientFactory, ILoggerFactory, TimeProvider)` with `IEmbeddingDriver For(string key)`, `static string Route(string key)` (`tensorx-byok#` + first 16 hex of SHA-256), `DateTimeOffset? RefusedAt(string key)`, `Task<KeyCheck> CheckAsync(string key, CancellationToken)` with `enum KeyCheck { Accepted, Refused, Unreachable }`. HTTP client name `EmbeddingDrivers.HttpClientName("tensorx-byok")`.

- [ ] Tests: `The_same_key_gives_the_same_driver_another_key_another`, `A_refused_key_pauses_its_own_driver_only` (401 for key A, 200 for key B), `A_refused_key_is_remembered_with_its_time`, `The_check_tells_accepted_refused_and_unreachable`, `The_model_key_is_the_same_for_every_key`.
- [ ] Implement; commit.

### Task 3: The vault carries its key; index and search route by it

**Files:** Modify `src/Kumbu.Server/Vaults/UnlockedVault.cs` (`VaultMeta.EmbeddingsKey`, `UnlockedVault.EmbeddingKey`), `src/Kumbu.Server/Vaults/VaultCatalog.cs` (unlock fallback; `SetEmbeddingProviderAsync(vault, provider, key, ct)`), `src/Kumbu.Server/Search/IEmbeddingDriver.cs` (`EmbeddingProviders.Resolve(UnlockedVault)` and `RouteOf(UnlockedVault)`), `src/Kumbu.Server/Search/SearchIndex.cs` (queues per route, created lazily), `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` (group the search by route); Test `tests/Kumbu.Server.Tests/Search/ByokIndexTests.cs`.

**Produces:** `EmbeddingProvider? EmbeddingProviders.Resolve(UnlockedVault vault)` (for `tensorx-byok`, an `EmbeddingProvider` named by the route, with the key's driver); `string EmbeddingProviders.RouteOf(UnlockedVault)`.

- [ ] Tests: `A_byok_vault_is_embedded_with_its_owners_key` (the handler sees `Authorization: Bearer <key>`), `Two_accounts_keys_embed_their_own_vaults`, `A_refused_key_leaves_other_vaults_indexed`, `With_byok_off_a_byok_vault_unlocks_as_internal_and_keeps_its_key`, `A_byok_vault_without_a_key_unlocks_as_internal`, `The_key_survives_a_rename_and_a_rekeying_of_the_vault`.
- [ ] Implement; the existing `SearchIndexTests` and `EmbeddingApiTests` stay green; commit.

### Task 4: The account's key

**Files:** Create `src/Kumbu.Server/Accounts/EmbeddingsKeys.cs`; Modify `src/Kumbu.Server/Accounts/UserDocuments.cs` (`EmbeddingsKey: byte[]?`), `src/Kumbu.Server/Accounts/UserKeyRotation.cs` (reseal as the discreet list); Test `tests/Kumbu.Server.Tests/Accounts/EmbeddingsKeysTests.cs`.

**Produces:** `sealed class EmbeddingsKeys(IMongoDatabase, ILogger<EmbeddingsKeys>)`: `Task<string?> GetAsync(userId, userKey, ct)`, `Task SetAsync(userId, userKey, string? key, ct)` (null removes); sealed under `VaultCrypto.Derive(userKey, "kumbu/v1/embeddings-key", userId)`, context `embeddings-key:{userId}`; conditional on the user's key id (`CredentialsChangedException` otherwise).

- [ ] Tests: round trip; another user's key doesn't open it; removed; `A_rotation_of_the_user_key_keeps_the_embeddings_key`.
- [ ] Implement; register in Program.cs; commit.

### Task 5: The API

**Files:** Modify `src/Kumbu.Server/Api/NoteApi.cs`, `src/Kumbu.Server/Api/AccountApi.cs` (or a new `src/Kumbu.Server/Api/EmbeddingsKeyApi.cs`), `src/Kumbu.Server/Api/DiscreetApi.cs`; Test `tests/Kumbu.Server.Tests/Api/ByokApiTests.cs`.

**Produces:**
- `GET /api/account/embeddings-key` → `ByokView(bool Offered, bool KeySaved, string? Last4, DateTimeOffset? RefusedAt)`.
- `PUT /api/account/embeddings-key` `{ key }` → `ByokView`: 400 for a malformed key or `Offered == false`, 400 "TensorX refused this key." for `Refused`, 503 for `Unreachable`; saves, then reseals the copy in every owned vault on `tensorx-byok`.
- `DELETE /api/account/embeddings-key` → 204; every owned vault on BYOK goes to `internal` (key copy removed), caught up.
- `GET /api/embeddings`: when offered, an entry `tensorx-byok` (external, label and description of the preset); `EmbeddingProviderView` gains `NeedsKey` (true for that entry while the caller has no key; `Available` false then).
- `PUT /api/vaults/{vault}/embeddings` with `tensorx-byok`: 400 unless offered, a key saved, and the vault not discreet for the caller; owner only as now; stores the key copy. Any other provider clears the copy.
- `POST /api/vaults`: with a saved key and BYOK offered, the new vault starts on `tensorx-byok`.
- Marking an owned BYOK vault discreet switches it to `internal`.
- All key endpoints: sessions only (an API key or connection answers 403 as the other session-only endpoints do).

- [ ] Tests for each line above, plus `The_key_never_appears_in_answers_or_the_log` and `A_member_searches_a_byok_vault_after_consent_with_the_owners_key`.
- [ ] Implement; commit.

### Task 6: The web app

**Files:** Modify `web/src/api/schema.d.ts` (the new view and endpoints, as openapi-typescript writes them), `web/src/api/notes.ts` (`useByok`), `web/src/routes/settings/Vaults.tsx` (section "Search by meaning", shown when offered), `web/src/shell/EmbeddingsDialog.tsx` (a `needsKey` provider is disabled, with "Save your TensorX key in Settings → Vaults first."), `web/src/shell/NewVaultDialog.tsx` (one line when the new vault will use TensorX); Create `web/src/routes/settings/ByokSection.tsx`; Tests `web/src/routes/settings/ByokSection.test.tsx`, `web/src/shell/EmbeddingsDialog.test.tsx` (extend or create), `web/src/shell/NewVaultDialog.test.tsx` (extend or create).

- [ ] Section: no key → a password-type field "TensorX key" and Save; saved → "TensorX key: saved, ends in …abcd", Replace and Remove; refused → "TensorX refused the key (time)". After a first save: the prompt "Move these N vaults to TensorX too?" with a checkbox per own non-discreet vault not on TensorX, all ticked, "Move" and "Not now"; Move PUTs each vault's provider.
- [ ] Tests first, then implement; `pnpm -C web test --run`, `pnpm -C web build`; commit.

### Task 7: Docs

**Files:** `README.md` (External embedding providers: the setting, the three deployments, privacy line), `docs/security-architecture.md` (§5.5 and the intro's limits), the spec's "Left to the builder".

- [ ] Write; commit.
