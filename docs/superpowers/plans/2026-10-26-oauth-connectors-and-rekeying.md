# OAuth Connectors and Re-keying Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** claude.ai (web, Desktop, phone) and any other client that follows the MCP authorisation specification connect to Kumbu² through OAuth 2.1, one connector URL per connection (`/mcp/c/{id}`), with tokens that are key slots; afterwards user keys can be rotated and vaults re-keyed.

**Architecture:** Kumbu² becomes its own authorisation server on the same origin as `/mcp`, in its own code (no OpenIddict, see Rulings). A *connection* is an API key's sibling: named, per-vault rights, optional expiry, revocable. Consent (in the React app, signed in) wraps the chosen vault keys under a one-time code; the token endpoint re-wraps them under the refresh token and, with a MongoDB TTL, under each access token. `CallerMiddleware` resolves access tokens only on `/mcp/c/{id}` and answers a real HTTP 401 with an RFC 9728 challenge there; plain `/mcp` stays API-key only. Re-keying follows in three steps (user key, vault key, triggers) after its own short design document.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `ModelContextProtocol.AspNetCore` 2.3.0, MongoDB.Driver 3.12 (transactions on a replica set, TTL indexes), xUnit v3 on Microsoft.Testing.Platform with Testcontainers; React 18 + Vite + TanStack Query + openapi-fetch, Vitest; pnpm.

**Spec:** `docs/superpowers/specs/2026-10-25-oauth-connectors-design.md` (binding; §10 for re-keying). Research behind it: `docs/notes-oauth-research.md`. Background: `docs/concept.md` §9, `docs/security-architecture.md`.

## Where and how this runs

- **Executor:** Claude Code on the web (Anthropic's cloud). Start from `master` after the overnight pull request (`overnight-2026-10-25`) is merged; work on one branch (the one the session gives you, or `oauth-connectors`); end with **one pull request to `master`** (Task 18). Line numbers in this plan are those of `overnight-2026-10-25` at `8f0539d`; if they shifted, find the place by the symbol named.
- **Acceptance with claude.ai** happens later on `https://kumbu.tidesson.net`, deployed by Chris through Komodo, with the checklist of Task 18. Nothing in this plan needs the public instance.
- **Toolchain check, once per session:**
  ```sh
  dotnet --version || { curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$HOME/dotnet-install.sh" && bash "$HOME/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"; }
  node --version   # 22.4 or later
  pnpm --version || { corepack enable && corepack prepare pnpm@10.32.1 --activate; } || npm install -g pnpm@10.32.1
  pnpm -C web install --frozen-lockfile
  ```
- **MongoDB for the tests.** Today every Mongo-backed test starts a `mongo:7.0` replica set through Testcontainers (`tests/Kumbu.Server.Tests/TestImages.cs:164`, used by `MongoFixture.cs:9` and `KumbuAppFactory.cs:41`). Check `docker info >/dev/null 2>&1 && echo docker || echo none`. With Docker, `dotnet test` just works. Without it, Task 1 adds `KUMBU_TEST_MONGO`, and you run a local `mongod` (7.0, as the tests use; 8.x crashes on some kernels, `TestImages.cs:154`):
  ```sh
  . /etc/os-release; echo "$VERSION_ID"                       # 22.04 or 24.04: the ubuntu2204 build runs on both
  mkdir -p "$HOME/mongo/data" && cd "$HOME/mongo"
  curl -fsSLO https://fastdl.mongodb.org/linux/mongodb-linux-x86_64-ubuntu2204-7.0.14.tgz
  tar xzf mongodb-linux-x86_64-ubuntu2204-7.0.14.tgz
  ldd mongodb-linux-x86_64-ubuntu2204-7.0.14/bin/mongod | grep 'not found' && sudo apt-get install -y libcurl4 libssl3   # only if something is missing (on 24.04: libcurl4t64)
  ulimit -n 64000                                             # a full run opens ~2000 WiredTiger files
  mongodb-linux-x86_64-ubuntu2204-7.0.14/bin/mongod --replSet rs0 --bind_ip 127.0.0.1 --port 27017 --dbpath "$HOME/mongo/data" --fork --logpath "$HOME/mongo/mongod.log"
  export KUMBU_TEST_MONGO='mongodb://127.0.0.1:27017/?directConnection=true'
  dotnet test                                                 # the fixture initiates the replica set on first use (Task 1)
  ```
  If `fastdl.mongodb.org` is not reachable from the sandbox, do not skip or disable Mongo tests: tell Chris that the environment's network settings need `fastdl.mongodb.org` allowed, keep working with `dotnet test tests/Kumbu.Core.Tests` and `pnpm -C web test`, and rely on CI (`.github/workflows/ci.yml` runs the whole suite with Docker on the pull request) until then. Each test uses its own database name, so one `mongod` serves the whole run.
- **Regenerating `web/src/api/schema.d.ts`** after any change to the REST API (`/api/...`), against a server you start yourself:
  ```sh
  Kumbu__Mongo__ConnectionString="${KUMBU_TEST_MONGO:-mongodb://127.0.0.1:27017/?directConnection=true}" \
  Kumbu__Mongo__Database=kumbu-gen Kumbu__Search__Enabled=false ASPNETCORE_URLS=http://127.0.0.1:8099 \
    dotnet run --project src/Kumbu.Server --no-launch-profile > "$HOME/kumbu-gen.log" 2>&1 &
  until curl -fsS http://127.0.0.1:8099/healthz >/dev/null; do sleep 1; done
  KUMBU_OPENAPI_URL=http://127.0.0.1:8099/api/openapi.json pnpm -C web gen:api
  kill %1
  ```
  With Docker but no local `mongod`, start one with `docker run -d --name kumbu-gen-mongo -p 127.0.0.1:27017:27017 mongo:7.0 --replSet rs0 --bind_ip_all` and `docker exec kumbu-gen-mongo mongosh --quiet --eval 'rs.initiate()'` first. The server does not need MongoDB for `/api/openapi.json`, but the hosted services log errors without it.

## Global Constraints

- British English in code, comments, docs and UI copy (`authorise`, `behaviour`, `colour`; protocol names stay as the RFCs spell them: `authorization_endpoint`, `/oauth/authorize`).
- SPDX header on every new source file: `// SPDX-License-Identifier: AGPL-3.0-or-later` (C#, TS, TSX). Match the surrounding style and comment density: comments say why, not what.
- Tests first: xUnit for the server, Vitest for the web app. Before every commit: the task's tests; before reporting a task done: `dotnet test`, `pnpm -C web test`, `pnpm -C web build`, all green.
- No new NuGet or npm packages. If one turns out unavoidable, update the lock files (`dotnet restore --force-evaluate`, CI restores with `--locked-mode`) and say why in the commit.
- Security first: validate at every boundary. Tokens, codes, PKCE verifiers, refresh tokens and client metadata bodies never in logs, URLs (other than the one-time `code`, `state` and `iss` of the redirect), browser storage or TanStack caches beyond what is needed (`gcTime: 0` on mutations that return secrets, as `web/src/routes/NewKeyDialog.tsx` does).
- `docs/security-architecture.md` must stay true to the code: a task that changes behaviour it describes updates it in the same commit, claims with `file:line`. Planned things live only in its §9.
- Every new environment variable goes into `compose.yaml` (as `Kumbu__…: ${KUMBU_…:-default}`), `.env.example` (commented, realistic placeholder) and the README. This plan adds exactly one: `KUMBU_OAUTH_ACCESS_TOKEN_MINUTES` → `Kumbu:OAuth:AccessTokenMinutes`.
- Text first: no attachments (`docs/concept.md`).
- Commits: imperative, free-form (`Add the token endpoint`), one per coherent change, each ending with exactly these lines:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01SoN5r1LrSSPZWEviJyt1nc
  ```
  Use `git commit -F- <<'EOF' … EOF` so the trailer survives. The task steps below give only the subject line.
- Tests that move the clock build the app with a `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`, already referenced): `PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s => { s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(fake); }))`. Expiry is always checked in code; MongoDB's TTL indexes only tidy up (they run once a minute, on real time).
- Filtered test runs (xUnit v3 on Microsoft.Testing.Platform): `dotnet test --project tests/Kumbu.Server.Tests --filter-class '*.ClassName'`; if your SDK insists, put `--` before `--filter-class`.

## Rulings on what the spec leaves open or contradicts

These are decided for this plan; the tasks implement them. Each is also recorded in the security document where it touches security.

1. **Own code, not OpenIddict** (spec §11): OpenIddict has no CIMD support and its stores can't share the MongoDB transaction that wraps slots at issuance and rotation; the endpoint set in spec §4 is small. Recorded in the security document (Task 6).
2. **Two kinds of "revoked".** The owner's **Revoke** in Kumbu²'s list is final (state `revoked`, name freed, the URL can't be renewed), like an API key, and so is a connection left with no vault after a vault deletion. Everything else that "revokes a connection" (refresh-token reuse, a reused code, `/oauth/revoke`, a password change that revokes API keys, a recovery, a deactivation, a rights change, re-keying) deletes its tokens and slots and leaves it **`needs_reconnect`** with a reason: Claude's "Reconnect" on the same URL renews it after a fresh consent. Expiry is final (`expired`). This reconciles spec §2 ("Reconnect after a revocation renews the connection") with §6 ("revoked and expired ones stay listed").
3. **The consent page has no choices**, only Allow and Deny (spec §2). "A requested `kumbu:read` preselects read only" (§4) becomes: a request whose scopes contain `kumbu:read` and not `kumbu:write` caps every right of this grant to read, and the page shows the capped rights. Missing, unknown, both, or `offline_access` scopes change nothing.
4. **Changing a connection's rights** in the list stores the new rights and deletes its tokens (`needs_reconnect`, reason `rights_changed`): the server never holds the refresh token, so it can't re-wrap; the list then never shows rights the tokens don't have.
5. **`resource` comparison:** both sides canonicalised (scheme and host lower-cased, default port dropped, one trailing slash dropped); the path is compared ordinally. The protected resource metadata echoes the form it was asked for (with or without trailing slash).
6. **Redirect URIs match exactly**, except an `http` loopback redirect (`localhost`, `127.0.0.1`, `[::1]`), where the port is ignored (RFC 8252 §7.3; Claude Code's metadata document lists `http://localhost/callback`).
7. **Errors on Kumbu²'s page, not redirected:** unknown client, unusable metadata document, redirect URI not registered — and also a missing `resource` or one that is no connection's URL (someone pasted the plain `/mcp` URL into claude.ai, whose fallback then finds our AS metadata on the origin): the page explains to create a connection and use its URL, with a "Back to {client host}" link that redirects with `error=invalid_target`. Protocol errors after the redirect URI is trusted (`response_type`, PKCE) are redirected with `error`, `state` and `iss`.
8. **The SameSite=Strict cookie is not sent on claude.ai's cross-site navigation to `/oauth/authorize`.** So `/oauth/authorize` is anonymous: it validates, stores the request server-side (10 minutes) and answers `303` to the React route `/consent?request={id}`; the page's own `fetch` calls are same-site and carry the cookie. Allow/Deny is a `POST` under `/api` (session-only, so the existing `Origin` check is the CSRF guard) that returns the redirect URL; the page navigates with `window.location.assign` (a form post would hit CSP `form-action 'self'` on the cross-origin redirect).
9. **Names are unique per user across API keys and connections:** both use the API keys' keyed name hash (`kumbu/v1/api-key-name-id`, unchanged, so existing keys keep theirs), each collection has its own partial unique index, and each creation checks the other collection inside its transaction, which also writes the user document (`keysCreated` / new `connectionsCreated`), so two concurrent creations conflict and the second sees the first.
10. **Connection ids:** 10 random bytes in lower-case Base32 without padding, 16 characters (80 bits). Tokens: `kumbu_ac_` (code), `kumbu_at_` (access), `kumbu_rt_` (refresh), each followed by 32 random bytes in Base62 (43 characters), as API keys.
11. **Grace period for a retried refresh: 30 s** (Claude's refresh timeout is 30 s, research §2). The new pair is kept sealed under a key derived from the *old* refresh token, so only its holder can get it back; after 30 s a reuse revokes the connection's tokens (`needs_reconnect`, `refresh_reused`).
12. **Expired access tokens stay recognisable** for 24 h (their record, never their slots, which vanish at expiry): they answer `error="invalid_token"` and don't count as wrong keys. Anthropic's traffic comes from one egress range (`160.79.104.0/21`), so an hourly expiry must not feed the per-address wrong-key limit (`Auth/WrongKeyLimiter.cs`, 20/min).
13. **Rate limits:** dynamic registration 30 per hour per address partition (`WrongKeyLimiter.Partition`, IPv6 per /64); failed token requests 20 per minute per `client_id` (not per address, see 12). The consent POST gets the session rules and nothing more.
14. **DCR clients** (`kumbu_client_` + 22 Base62 characters) expire after 24 h unless an authorisation succeeds; then they stay as long as the user's account. No allow-list (spec §3).
15. **Client metadata documents:** https only (http loopback only with the test/dev option `Kumbu:OAuth:AllowLoopbackClients`), **no redirects at all**, the resolved address pinned for the connection and refused if not public, 16 KiB, 5 s, cached per URL by `Cache-Control: max-age` clamped to 5 min – 24 h (1 h without one), failures cached 1 min.
16. **CORS** (`Access-Control-Allow-Origin: *`, no credentials) only on the two well-known documents, `/oauth/token`, `/oauth/register` and `/oauth/revoke` (browser-based clients such as the Inspector's web UI call them); none on `/mcp/c/{id}` (the Inspector reaches it through its proxy), none on `/oauth/authorize`.
17. **What is in the clear on a connection:** owner, created/last used/expiry/revocation times and reasons, and the **vault ids** it covers (slots show vault ids for API keys already; needed to cascade a vault deletion while the connection has no slots). Name, rights and the authorised client's name are sealed under the user key, like an API key's meta. The writer label, the bound resource and the connection's expiry are sealed into each token's *grant* blob.
18. **`/mcp/c/{id}` with an unknown id** answers 404 like its metadata URL; a known one (any state) answers 401 with the challenge.
19. **Per-vault re-keying lives in Settings next to "Delete vault"**, where the per-vault actions already are (`web/src/routes/Settings.tsx:241–255`); spec §10 says "its menu".
20. **Re-keying (spec §10 defaults, decided here, justified in the design doc of Task 13):** a password change does **not** rotate the user key (not among §10's triggers; the security document's §9 list is corrected). Rotating the user key outside a recovery issues a **new recovery key, shown once** (the old one isn't at hand and must stop opening the new user key). Stage 1 also re-seals what is keyed by the user key besides slots: the API keys' and connections' metas and name hashes, the vault name hashes in user slots and `users.keyId`. Re-keying a vault revokes **whole** API keys that had it (final, as spec §10) and sets connections that had it to `needs_reconnect` (`rekeyed`). Only user-owned vaults can be re-keyed (dev-token vaults have no owner session). A recovery's re-keying of several vaults runs one transaction per vault; the vaults still to do are recorded on the user (`rekeyPending`) and resumed at the next sign-in. The "re-key on revoking an API key with write rights" trigger of the security document's §9 is dropped (not in spec §10).

## Review Focus

1. **The connector URL in other spellings** — pasted with a trailing slash, an upper-case host, or as `http://`. Expected: trailing slash and host case work end to end (Ruling 5); `http://` on an `https` instance gets the proxy's redirect, which claude.ai can't follow (research §2), and the README says so. Test: Task 3, `Trailing_slash_and_host_case_reach_the_same_connection`.
2. **Anthropic's shared egress** — every claude.ai user of an instance arrives from one address range, so expired access tokens (hourly) must not lock all of them out through the wrong-key limiter. Test: Task 7, `Expired_access_tokens_do_not_count_as_wrong_keys`.
3. **Retried and parallel refreshes** — Claude retries on timeouts; two refreshes of the same token within the grace period must both get the same new pair and not revoke anything. Test: Task 8, `Parallel_refreshes_of_one_token_get_the_same_pair`.
4. **The sign-in detour on a phone** — the in-app browser has no Kumbu² session: sign-in comes first, then back to the consent with the request id intact; a request that expired meanwhile (10 min) says to start again in the app, not a bare error. Tests: Task 11, `Signs_in_first_and_comes_back_to_the_same_request` and `An_expired_request_says_to_start_again`.
5. **The plain `/mcp` URL pasted into claude.ai** — Claude falls back to the origin as authorisation server and reaches `/oauth/authorize` with `resource=…/mcp`. Expected: Kumbu²'s page explains to create a connection and use its URL (Ruling 7), never a consent for some connection. Test: Task 6, `Plain_mcp_as_resource_explains_connections`.

---

## File structure

New server files (all under `src/Kumbu.Server/`):

| File | Responsibility |
|---|---|
| `Accounts/WriterNames.cs` | The naming rules and keyed name hash shared by API keys and connections. |
| `Accounts/Connections.cs` | Connections: documents, create/list/revoke/rights, token revocation by reason, states. |
| `Accounts/Secrets.cs` | Prefixed Base62 secrets (new and well-formed checks) for OAuth tokens. |
| `OAuth/OAuthUrls.cs` | Origin, issuer, resource and metadata URLs; `resource` canonicalisation. |
| `OAuth/Discovery.cs` | RFC 9728 and RFC 8414 documents; CORS for the OAuth endpoints. |
| `OAuth/ClientMetadataFetcher.cs` | CIMD fetching with the SSRF guard, validation and cache. |
| `OAuth/OAuthClients.cs` | Resolves a `client_id` (CIMD or DCR), DCR storage, redirect matching. |
| `OAuth/AuthorizationRequests.cs` | Pending authorisations (10 min TTL). |
| `OAuth/OAuthTokens.cs` | Codes, access and refresh tokens, rotation, grace, reuse, resolution, revocation. |
| `OAuth/OAuthEndpoints.cs` | `/oauth/authorize`, `/oauth/token`, `/oauth/register`, `/oauth/revoke`. |
| `OAuth/OAuthLimits.cs` | DCR and token-failure rate limits. |
| `Api/ConnectionApi.cs` | `/api/connections` and the consent API `/api/oauth/requests/{id}`. |
| `Accounts/UserKeyRotation.cs` | Re-keying stage 1. |
| `Vaults/VaultCatalog.Rekey.cs` | Re-keying stage 2 (`VaultCatalog` becomes `partial`). |
| `Accounts/Rekeying.cs` | Re-keying triggers. |

Modified: `Auth/CallerMiddleware.cs`, `Auth/RequestCaller.cs`, `Vaults/Credentials.cs`, `Vaults/Documents.cs`, `Vaults/VaultCatalog.cs`, `Accounts/ApiKeys.cs`, `Accounts/UserCatalog.cs`, `Accounts/UserDocuments.cs`, `Accounts/Admin.cs`, `Accounts/Deletion.cs`, `Api/AccountApi.cs`, `Api/NoteApi.cs`, `KumbuOptions.cs`, `KumbuOptionsValidator.cs`, `Program.cs`. Web: `web/src/routes/Keys.tsx` (becomes "Access"), new `web/src/routes/Connections.tsx`, `NewConnectionDialog.tsx`, `ConnectorReveal.tsx`, `Consent.tsx`, `web/src/router.tsx`, `web/src/routes/Settings.tsx`. Tests under `tests/Kumbu.Server.Tests/OAuth/` and `tests/Kumbu.Server.Tests/Accounts/`.

Collections added: `connections`, `oauth_clients`, `oauth_requests`, `oauth_tokens`; slots of kind `connection` in `slots`.

---

### Task 1: MongoDB for the tests without Docker

**Files:**
- Modify: `tests/Kumbu.Server.Tests/TestImages.cs`, `tests/Kumbu.Server.Tests/MongoFixture.cs:7-19`, `tests/Kumbu.Server.Tests/KumbuAppFactory.cs:41-58`
- Modify: `README.md:186-193` (Development)

**Interfaces:**
- Produces: `TestImages.ExternalMongo` (`string?`, from `KUMBU_TEST_MONGO`), `TestImages.EnsureReplicaSetAsync(string connectionString, CancellationToken ct)`; `MongoFixture.ConnectionString` and `KumbuAppFactory`'s connection string come from the external server when set, else from the container as today.

No new xUnit test: this is test infrastructure; the whole suite is its test.

- [ ] **Step 1: Implement the switch.** With `KUMBU_TEST_MONGO` set, `MongoFixture` and `KumbuAppFactory` start no container; `InitializeAsync` calls `EnsureReplicaSetAsync`, which runs `hello` and, if it has no `setName`, `replSetInitiate` with `{ _id: "rs0", members: [{ _id: 0, host: <host:port of the connection string> }] }`, then polls `hello` until `isWritablePrimary` (at most 30 s). `DisposeAsync` drops nothing (databases are per test and random). Unset, behaviour is unchanged.
- [ ] **Step 2: Run with Docker absent** (commands in "Where and how this runs"): `KUMBU_TEST_MONGO='mongodb://127.0.0.1:27017/?directConnection=true' dotnet test`. Expected: all pass. With Docker present, also run plain `dotnet test`: all pass.
- [ ] **Step 3: README.** In "Development", one paragraph: `KUMBU_TEST_MONGO` (a MongoDB 7.0 replica set or a single `mongod --replSet rs0`, initiated by the tests if needed; `ulimit -n 64000`), with the example value above.
- [ ] **Step 4: Commit** `Let the tests use a running MongoDB instead of a container`

---

### Task 2: Connections — store and REST API

**Files:**
- Create: `src/Kumbu.Server/Accounts/WriterNames.cs`, `src/Kumbu.Server/Accounts/Connections.cs`, `src/Kumbu.Server/Api/ConnectionApi.cs`
- Modify: `src/Kumbu.Server/Accounts/ApiKeys.cs:95,116,133-153,325-346,379-380,461`, `src/Kumbu.Server/Accounts/UserDocuments.cs` (`ConnectionsCreated`), `src/Kumbu.Server/Program.cs:288-298,403`
- Test: `tests/Kumbu.Server.Tests/Accounts/ConnectionTests.cs`, `tests/Kumbu.Server.Tests/Api/ConnectionApiTests.cs`
- Regenerate: `web/src/api/schema.d.ts`

**Interfaces:**
- Produces:
  - `internal static class WriterNames { const int MaxLength = 64; static string Check(User user, string name, string noun); static byte[] NameId(byte[] userKey, string userId, string name); }` — `Check` is today's `ApiKeys.CheckName` (`ApiKeys.cs:325-346`) with the noun ("An API key" / "A connection") in its messages; `NameId` is today's `ApiKeys.NameId`, same label.
  - `public enum ConnectionState { Waiting, Connected, NeedsReconnect, Revoked, Expired }`
  - `public static class ConnectionReasons` with `Revoked = "revoked"`, `VaultDeleted = "vault_deleted"`, `PasswordChanged = "password_changed"`, `Recovery = "recovery"`, `Deactivated = "deactivated"`, `RefreshReused = "refresh_reused"`, `CodeReused = "code_reused"`, `ClientRevoked = "client_revoked"`, `RightsChanged = "rights_changed"`, `Rekeyed = "rekeyed"`.
  - `public sealed record ConnectedClient(string Name, string Host, bool Verified);` (`Verified`: identified by a metadata document, not self-registered)
  - `public sealed record ConnectionInfo(string Id, string Name, IReadOnlyDictionary<string, KeyRight> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? ExpiresAt, DateTime? RevokedAt, ConnectionState State, string? Reason, ConnectedClient? Client);`
  - `public sealed class Connections(IMongoDatabase db, VaultCatalog catalog, TimeProvider time, ILogger<Connections> log)` with
    - `Task<ConnectionInfo> CreateAsync(User user, byte[] userKey, DateTime credentialReadAt, string name, IReadOnlyDictionary<string, KeyRight> vaults, CancellationToken ct, int? expiresInDays = null)` — same refusals as `ApiKeys.CreateAsync` (`ApiKeys.cs:82-157`), `ConnectionNameTakenException` for a taken name.
    - `Task<IReadOnlyList<ConnectionInfo>> ListAsync(User user, byte[] userKey, CancellationToken ct)` (oldest first; `State` derived as below)
    - `Task<bool> RevokeAsync(User user, string id, CancellationToken ct)` (final; false if none that isn't revoked)
    - `Task<ConnectionInfo?> SetVaultsAsync(User user, byte[] userKey, string id, IReadOnlyDictionary<string, KeyRight> vaults, CancellationToken ct)` (Ruling 4)
    - `internal Task<int> RevokeAllTokensAsync(IClientSessionHandle session, string userId, string reason, CancellationToken ct)` and `Task<int> RevokeAllTokensAsync(User user, string reason, CancellationToken ct)` (own transaction, retried like `ApiKeys.RevokeAllAsync`)
    - `internal Task RevokeConnectionTokensAsync(IClientSessionHandle session, string connectionId, string reason, CancellationToken ct)`
    - `internal Task<ConnectionDocument?> FindAsync(string id, CancellationToken ct)`
    - `internal sealed record ConnectionMeta(string Name, Dictionary<string, string> Vaults, ConnectedClient? Client);` and `internal ConnectionMeta? OpenMeta(User user, byte[] userKey, ConnectionDocument doc)` (null if it doesn't open), and `internal Task SetClientAsync(User user, byte[] userKey, string id, ConnectedClient client, CancellationToken ct)` (re-seals the meta with the client, for consent)
    - `static string NewId()` (Ruling 10)
  - `internal static class ConnectionRevocation { static Task<int> RevokeTokensAsync(IMongoDatabase db, IClientSessionHandle session, FilterDefinition<ConnectionDocument> which, string reason, DateTime now, CancellationToken ct); }` — deletes the matching connections' `oauth_tokens` and their `connection` slots and sets `tokensRevokedAt/Reason`; a no-op on collections that are still empty in this task. Both `Connections` and `VaultCatalog` (Tasks 9 and 15) call it: `Connections` depends on `VaultCatalog`, so `VaultCatalog` can't call `Connections` (it already works on `api_keys` directly for the same reason, `VaultCatalog.cs:447-463`).
  - `ConnectionDocument` (`connections`): `_id`, `user`, `nameId?`, `meta` (sealed under `HKDF(userKey, "kumbu/v1/connection-meta", salt = userId)`, AAD `connection:{id}:meta`: `{ name, vaults: {vaultId: right}, client?: {name, host, verified} }`), `vaults` (vault ids, clear), `createdAt`, `lastUsedAt?`, `expiresAt?`, `revokedAt?`, `revokedReason?`, `tokensRevokedAt?`, `tokensRevokedReason?`, `authorisedAt?`.
  - State: `Revoked` if `revokedAt`; else `Expired` if `expiresAt <= now`; else `Connected` if a live refresh token exists in `oauth_tokens` (Task 7 writes them; until then none); else `NeedsReconnect` if `authorisedAt` is set; else `Waiting`. `Reason` = `revokedReason`, `"expired"`, or `tokensRevokedReason`.
  - REST, session-only (`SessionOnlyMetadata`): `GET /api/connections` → `ConnectionView[]`; `POST /api/connections` with `CreateConnectionRequest(string Name, IReadOnlyDictionary<string,string> Vaults, int? ExpiresInDays = null)` → 201 `ConnectionView`; `PUT /api/connections/{id}/vaults` with `SetConnectionVaultsRequest(IReadOnlyDictionary<string,string> Vaults)` → `ConnectionView`; `DELETE /api/connections/{id}` → 204. `ConnectionView(string Id, string Name, string Url, IReadOnlyDictionary<string,string> Vaults, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? ExpiresAt, DateTime? RevokedAt, string State, string? Reason, ClientView? Client)` with `ClientView(string Name, string Host, bool Verified)`; `State` in snake case (`waiting`, `connected`, `needs_reconnect`, `revoked`, `expired`); `Url` from `OAuthUrls.Resource` (Task 3 creates `OAuthUrls`; in this task build it as `{PublicUrl or request origin}/mcp/c/{id}` inline and switch to `OAuthUrls` in Task 3).

- [ ] **Step 1: Write the failing tests** in `ConnectionTests.cs` (`[Collection("mongo")]`, service-level like `ApiKeyTests.cs`):
  - `Creates_a_connection_with_rights_and_lists_it_as_waiting` — `State == Waiting`, `Vaults[vaultId] == Write`, `Id.Length == 16`, `Id` matches `^[a-z2-7]{16}$`.
  - `A_name_is_unique_across_api_keys_and_connections` — key "laptop" then connection "Laptop" → `ConnectionNameTakenException`; connection "cor.energy" then key "COR.ENERGY" → `ApiKeyNameTakenException`; after revoking the connection the key name is free.
  - `Two_concurrent_creations_with_one_name_leave_one` — `Task.WhenAll` of a key and a connection named alike: exactly one succeeds.
  - `Naming_rules_are_the_api_keys_rules` — the user's username, `dev`, `" ("`, a zero-width character: `ArgumentException`.
  - `A_connection_needs_a_vault_of_the_user` — empty map or another user's vault id: `ArgumentException`.
  - `Expiry_is_30_90_or_365_days` — 7 → `ArgumentException`; 30 → `ExpiresAt ≈ now + 30 d`; with a `FakeTimeProvider` advanced past it, `State == Expired`.
  - `Revoking_is_final_and_frees_the_name` — `State == Revoked`, `Reason == "revoked"`, `RevokeAsync` again → false.
  - `Nothing_readable_about_a_connection_reaches_the_database` — dump `connections`: neither the name nor `"write"` appears in the BSON.
  - `ApiConnection` tests in `ConnectionApiTests.cs` (`[Collection("app")]`, `RestClient`): create/list/rights/revoke round-trip; an API key gets 403 on all four; `Url == "https://kumbu.test/mcp/c/" + id`; vault given by name, unknown name → 400.
- [ ] **Step 2: Run** `dotnet test --project tests/Kumbu.Server.Tests --filter-class '*.ConnectionTests'` and `'*.ConnectionApiTests'`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** `WriterNames` and switch `ApiKeys` to it (messages keep "An API key …"; `ApiKeyNameTakenException`'s message becomes "You already have an API key or connection named '{name}'."). In `ApiKeys.CreateAsync`'s transaction (`ApiKeys.cs:133-147`), after the user update, refuse when `connections` has `{user, nameId}` with `nameId` set. Implement `Connections` with its own partial unique index `{user, nameId}` (as `ApiKeys.cs:400-406`), the reverse check, `users.connectionsCreated` incremented in the transaction, and the vault keys checked to be the user's through `VaultCatalog`'s user slots (as `ApiKeySlotsAsync` does; no slots are written here). Register `Connections` in `Program.cs`; map `MapConnectionApi()`.
- [ ] **Step 4: Run** both test classes, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Regenerate the schema** (commands above); `pnpm -C web build` still passes.
- [ ] **Step 6: Security document.** §5.3: connection names sealed and keyed-hashed like API keys'; §5.6 table: a `connections` row (Ruling 17, with `file:line`); §4 "The user" row: creates and revokes connections.
- [ ] **Step 7: Commit** `Add connections next to API keys, with names unique across both`

---

### Task 3: Discovery — the per-connection MCP endpoint and its metadata

**Files:**
- Create: `src/Kumbu.Server/OAuth/OAuthUrls.cs`, `src/Kumbu.Server/OAuth/Discovery.cs`
- Modify: `src/Kumbu.Server/Auth/CallerMiddleware.cs:38-142`, `src/Kumbu.Server/KumbuOptions.cs` (`OAuthOptions`), `src/Kumbu.Server/KumbuOptionsValidator.cs`, `src/Kumbu.Server/Program.cs:396-415`, `src/Kumbu.Server/Api/ConnectionApi.cs` (use `OAuthUrls`), `compose.yaml`, `.env.example`
- Test: `tests/Kumbu.Server.Tests/OAuth/DiscoveryTests.cs`

**Interfaces:**
- Consumes: `Connections.FindAsync` (Task 2).
- Produces:
  - `public sealed class OAuthOptions { int AccessTokenMinutes = 60; bool AllowLoopbackClients = false; }` as `KumbuOptions.OAuth`; validator: `AccessTokenMinutes` 5–1440, message `"Kumbu:OAuth:AccessTokenMinutes must be between 5 and 1440"`.
  - `public static class OAuthScopes { const string Read = "kumbu:read"; const string Write = "kumbu:write"; static readonly string[] Supported = [Read, Write]; const string Challenge = "kumbu:read kumbu:write"; }`
  - `public static class OAuthUrls { static string Origin(HttpContext http, string? publicUrl); static string Resource(string origin, string connectionId); static string ResourceMetadata(string origin, string connectionId, bool trailingSlash = false); static string Canonical(string url); static string? ConnectionIdOf(string origin, string resource); }` — `Origin` is `PublicUrl` without trailing slash, else `{scheme}://{host}` of the request; the issuer is the origin; `ConnectionIdOf` returns the id when the canonical `resource` is `{canonical origin}/mcp/c/{id}`, else null.
  - Routes: `app.MapMcp("/mcp/c/{id}")` next to `app.MapMcp("/mcp")`; `GET /.well-known/oauth-protected-resource/mcp/c/{id}` and `GET /.well-known/oauth-authorization-server` (both `AllowAnonymous().ExcludeFromDescription()`, served with `Cache-Control: public, max-age=300`).
  - PRM body: `{ "resource": <Resource, trailing slash as asked>, "authorization_servers": [<origin>], "bearer_methods_supported": ["header"], "scopes_supported": ["kumbu:read","kumbu:write"], "resource_name": "Kumbu²" }`; unknown id → 404 `{"error":"Not found."}`.
  - AS metadata body exactly: `issuer`, `authorization_endpoint` (`{origin}/oauth/authorize`), `token_endpoint` (`/oauth/token`), `registration_endpoint` (`/oauth/register`), `revocation_endpoint` (`/oauth/revoke`), `response_types_supported: ["code"]`, `grant_types_supported: ["authorization_code","refresh_token"]`, `code_challenge_methods_supported: ["S256"]`, `token_endpoint_auth_methods_supported: ["none"]`, `revocation_endpoint_auth_methods_supported: ["none"]`, `scopes_supported: ["kumbu:read","kumbu:write"]` (no `offline_access`), `client_id_metadata_document_supported: true`, `authorization_response_iss_parameter_supported: true`. No `/.well-known/openid-configuration`, no `/.well-known/oauth-protected-resource` or `…/mcp` (404).
  - `CallerMiddleware`: on a path `/mcp/c/{id}` (case-insensitive prefix, `id` = the third segment): unknown id → 404 JSON; otherwise without a valid access token → 401 with `WWW-Authenticate: Bearer resource_metadata="<ResourceMetadata>", scope="kumbu:read kumbu:write"` plus `, error="invalid_token"` when any bearer was presented. In this task no token is valid yet (Task 7 adds resolution); API keys and the dev token are **not** resolved on `/mcp/c/…`. Plain `/mcp` is unchanged (`WWW-Authenticate: Bearer`, `CallerMiddleware.cs:140`).
  - `internal static class OAuthCors` applied to the two well-known paths and `/oauth/token|register|revoke`: `Access-Control-Allow-Origin: *`, `Access-Control-Allow-Methods: GET, POST, OPTIONS`, `Access-Control-Allow-Headers: Content-Type, Authorization, MCP-Protocol-Version`, `Access-Control-Max-Age: 600`; an `OPTIONS` preflight answers 204 before `CallerMiddleware`.

- [ ] **Step 1: Write the failing tests** (`[Collection("app")]`, against `PublicApi`, a connection made through `Connections` for a `RestClient` user):
  - `Unauthenticated_connection_url_answers_401_with_the_challenge` — `POST /mcp/c/{id}` with an `initialize` body: 401, header exactly `Bearer resource_metadata="https://kumbu.test/.well-known/oauth-protected-resource/mcp/c/{id}", scope="kumbu:read kumbu:write"`.
  - `A_wrong_bearer_adds_invalid_token` — `Authorization: Bearer kumbu_at_x` → 401, header ends with `, error="invalid_token"`.
  - `An_api_key_is_refused_on_a_connection_url` — the user's write key → 401 with the challenge; the same key on `/mcp` → 200.
  - `Plain_mcp_points_nowhere` — `POST /mcp` without key → 401, `WWW-Authenticate` is exactly `Bearer`; `GET /.well-known/oauth-protected-resource` and `…/mcp` → 404.
  - `Protected_resource_metadata_names_the_connection` — body fields as above, `resource == "https://kumbu.test/mcp/c/{id}"`, `authorization_servers == ["https://kumbu.test"]`.
  - `Unknown_ids_have_no_metadata_and_no_endpoint` — random id: PRM 404, `POST /mcp/c/{random}` 404.
  - `Trailing_slash_and_host_case_reach_the_same_connection` — PRM at `…/mcp/c/{id}/` → `resource` ends with `/{id}/`; `OAuthUrls.ConnectionIdOf("https://kumbu.test", "HTTPS://KUMBU.TEST:443/mcp/c/{id}/") == id`; `…/mcp/c/{ID upper-cased}` → null.
  - `Authorization_server_metadata_is_complete` — every field above with its exact value; no `offline_access`.
  - `Oauth_documents_allow_cross_origin_reads` — `OPTIONS /oauth/token` with `Origin: http://localhost:6274` → 204 and the CORS headers; `OPTIONS /mcp/c/{id}` → no `Access-Control-Allow-Origin`.
  - `Access_token_minutes_are_validated` in `Startup/KumbuOptionsValidatorTests.cs` — 4 and 1441 fail with the message, 60 passes.
- [ ] **Step 2: Run** `--filter-class '*.DiscoveryTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces. `MapMcp("/mcp/c/{id}")` must register the same tools; confirm with the existing `McpClientTests` pattern in Task 7. `compose.yaml`: `Kumbu__OAuth__AccessTokenMinutes: ${KUMBU_OAUTH_ACCESS_TOKEN_MINUTES:-60}`; `.env.example`: a commented block ("Optional. Lifetime of an app connection's access token in minutes (default 60, 5 to 1440). Shorter means more refreshes; claude.ai shows 'Reconnect' if a refresh fails.").
- [ ] **Step 4: Run** the class, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Security document.** §7 "Fail-closed authentication" row: `/mcp/c/{id}` takes only its connection's access tokens, the well-known documents are anonymous (`file:line`); §6 "Sign-in and sessions": the cookie is still refused on `/mcp/c/…` too.
- [ ] **Step 6: Commit** `Serve a connector URL per connection with its OAuth metadata`

---

### Task 4: Client identification by metadata document (CIMD)

**Files:**
- Create: `src/Kumbu.Server/OAuth/ClientMetadataFetcher.cs`, `src/Kumbu.Server/OAuth/OAuthClients.cs`
- Modify: `src/Kumbu.Server/Program.cs:275` (named `HttpClient` "cimd")
- Test: `tests/Kumbu.Server.Tests/OAuth/ClientMetadataTests.cs`, `tests/Kumbu.Server.Tests/OAuth/MetadataServer.cs` (helper)

**Interfaces:**
- Produces:
  - `public sealed record OAuthClient(string ClientId, string Name, string Host, IReadOnlyList<string> RedirectUris, bool Verified);` — CIMD: `Host` = host of the `client_id` URL, `Verified = true`; DCR (Task 5): `Host` = host of the first redirect URI, `Verified = false`. `Name`: `client_name` trimmed, control/format characters removed, at most 100 characters.
  - `public sealed record ClientMetadataResult(OAuthClient? Client, string? Error);` with `Error` one of `"unreachable"`, `"not_public"`, `"too_large"`, `"not_json"`, `"client_id_mismatch"`, `"no_redirect_uris"`, `"bad_redirect_uri"`, `"not_public_client"`, `"redirected"`.
  - `public sealed class ClientMetadataFetcher(IHttpClientFactory http, IOptions<KumbuOptions> options, TimeProvider time, ILogger<ClientMetadataFetcher> log)` with `Task<ClientMetadataResult> FetchAsync(string clientId, CancellationToken ct)`, `const int MaxBytes = 16 * 1024`, `static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5)`, `internal static bool IsPublicAddress(IPAddress address)`.
  - `public sealed class OAuthClients(IMongoDatabase db, ClientMetadataFetcher fetcher, TimeProvider time)` with `Task<OAuthClient?> ResolveAsync(string clientId, CancellationToken ct)` (URL-shaped `https://…` with a path → CIMD; else Task 5's store) and `static bool RedirectMatches(OAuthClient client, string redirectUri)` (Ruling 6).
  - The "cimd" client: `SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = 5 s, ConnectCallback = resolve with Dns.GetHostAddressesAsync, keep only addresses where IsPublicAddress (or loopback when AllowLoopbackClients), connect to the first }`, no cookies, no proxy.
  - `IsPublicAddress` refuses: loopback, unspecified, link-local (169.254/16, fe80::/10), private (10/8, 172.16/12, 192.168/16, fc00::/7), CGNAT 100.64/10, 0/8, multicast, broadcast, documentation ranges (192.0.2/24, 198.51.100/24, 203.0.113/24, 2001:db8::/32), 198.18/15, and IPv4-mapped/compatible IPv6 forms of all of these.
  - Validation: JSON object; `client_id` equals the fetched URL ordinally; `client_name` a non-empty string; `redirect_uris` a non-empty array of absolute `https` URIs, or `http` with a loopback host, no fragments; `token_endpoint_auth_method` absent or `"none"`; unknown members and unknown `grant_types` (Claude's `jwt-bearer`) ignored; `logo_uri` ignored (never rendered: research §2, spec §8).
  - Cache per URL as Ruling 15, in memory (`ConcurrentDictionary`), bounded to 1000 entries (drop the oldest).

- [ ] **Step 1: Write the failing tests.** `MetadataServer` is a minimal Kestrel `WebApplication` on `127.0.0.1:0` serving configurable responses (body, status, headers, delay, redirect), used with `Kumbu:OAuth:AllowLoopbackClients=true` and the URL `http://127.0.0.1:{port}/client.json` (the fetcher accepts `http` only for loopback, only with the option). Tests:
  - `Fetches_claudes_document_shape` — Claude's document from research §2 (with `client_id` replaced by the local URL and the `jwt-bearer` grant kept) → `Client.Name == "Claude"`, `Host == "127.0.0.1"`, `RedirectUris == ["https://claude.ai/api/mcp/auth_callback"]`, `Verified`.
  - `Refuses_a_document_whose_client_id_differs` → `"client_id_mismatch"`.
  - `Refuses_redirects` — 302 to another local path → `"redirected"`.
  - `Refuses_oversized_documents` — 17 KiB body (also with chunked encoding and no `Content-Length`) → `"too_large"`.
  - `Gives_up_after_five_seconds` — 6 s delay → `"unreachable"` within 6 s.
  - `Refuses_bad_redirect_uris` — `http://example.org/cb`, `https://x/#f`, `[]` → `"bad_redirect_uri"` / `"no_redirect_uris"`.
  - `Refuses_confidential_clients` — `token_endpoint_auth_method: "client_secret_basic"` → `"not_public_client"`.
  - `Caches_by_max_age_within_bounds` — `max-age=10` → second fetch within 4 min hits the cache (server sees one request); after advancing a `FakeTimeProvider` by 6 min, fetches again.
  - `Without_the_option_loopback_is_refused` — option off → `"not_public"`, server sees no request.
  - `[Theory] Private_and_special_addresses_are_not_public` — `127.0.0.1`, `10.1.2.3`, `172.16.0.1`, `192.168.1.1`, `169.254.169.254`, `100.64.0.1`, `0.0.0.0`, `::1`, `fe80::1`, `fd00::1`, `::ffff:127.0.0.1`, `::ffff:10.0.0.1`, `224.0.0.1` → false; `160.79.104.10`, `1.1.1.1`, `2606:4700::1111` → true.
  - `Redirect_matching_is_exact_but_ignores_loopback_ports` — `https://claude.ai/api/mcp/auth_callback` vs itself true, with `/` appended false, upper-case path false; `http://localhost/callback` vs `http://localhost:53122/callback` true, vs `http://localhost:53122/other` false; `http://127.0.0.1:1/cb` registered vs `http://127.0.0.1:9/cb` true.
  - `Never_logs_the_document` — with `CapturedLog`, a document containing a marker string: the marker never appears.
- [ ] **Step 2: Run** `--filter-class '*.ClientMetadataTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces; read the body through a counting stream that stops at `MaxBytes + 1`.
- [ ] **Step 4: Run** the class, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Security document.** §7 "Outbound connections" row: client metadata documents, with every guard of Ruling 15 (`file:line`); README "Security notes for a public instance": the server fetches clients' metadata documents from the internet (egress needed for `claude.ai`).
- [ ] **Step 6: Commit** `Identify OAuth clients by their metadata documents, guarded against SSRF`

---

### Task 5: Dynamic client registration

**Files:**
- Create: `src/Kumbu.Server/OAuth/OAuthLimits.cs`, `src/Kumbu.Server/OAuth/OAuthEndpoints.cs` (with `POST /oauth/register` only; Tasks 6–8 add the rest)
- Modify: `src/Kumbu.Server/OAuth/OAuthClients.cs`, `src/Kumbu.Server/Program.cs`
- Test: `tests/Kumbu.Server.Tests/OAuth/RegistrationTests.cs`

**Interfaces:**
- Consumes: `OAuthClient`, `OAuthClients.ResolveAsync` (Task 4).
- Produces:
  - `OAuthClients.RegisterAsync(JsonElement body, CancellationToken ct) → Task<OAuthClient>` (throws `ArgumentException` with the RFC 7591 error description), `OAuthClients.MarkAuthorisedAsync(string clientId, CancellationToken ct)` (unsets `expiresAt`), `const string RegisteredPrefix = "kumbu_client_"`.
  - `OAuthClientDocument` (`oauth_clients`): `_id` (client id), `name`, `redirectUris`, `createdAt`, `expiresAt?` (TTL index, 24 h after creation).
  - `public sealed class OAuthLimits : IDisposable` with `TimeSpan? Registration(HttpContext http)` (30/h per `WrongKeyLimiter.Partition`), `TimeSpan? TokenReserve(string clientId)`, `void TokenFailed(string clientId)`, `void TokenRelease(string clientId)` (20 failures/min per client id, the sliding-window pattern of `Auth/WrongKeyLimiter.cs:39-60`; the key is the client id cut to 256 characters).
  - `POST /oauth/register` (`AllowAnonymous`, `ExcludeFromDescription`, CORS): JSON in; 201 with `client_id`, `client_id_issued_at`, `client_name`, `redirect_uris`, `grant_types: ["authorization_code","refresh_token"]`, `response_types: ["code"]`, `token_endpoint_auth_method: "none"`; 400 `{"error":"invalid_redirect_uri"|"invalid_client_metadata","error_description":…}`; 429 with `Retry-After`. `Cache-Control: no-store`.

- [ ] **Step 1: Write the failing tests** (`[Collection("app")]`, `PublicApi`, each test with its own `X-Forwarded-For`):
  - `Registers_a_public_client` — `{client_name:"Inspector", redirect_uris:["http://localhost:6274/oauth/callback"]}` → 201, `client_id` starts with `kumbu_client_`, `token_endpoint_auth_method == "none"`; `ResolveAsync(client_id)` → `Verified == false`, `Host == "localhost"`.
  - `Absent_auth_method_is_registered_as_none_and_secrets_are_refused` — absent → `"none"`; `"client_secret_post"` → 400 `invalid_client_metadata`.
  - `Refuses_unusable_redirect_uris` — none, `http://example.org/cb`, a relative URI, one with a fragment → 400 `invalid_redirect_uri`.
  - `Limits_registrations_per_network` — 31st from one address within the hour → 429; another address → 201.
  - `Unused_registrations_expire_after_a_day` — `ResolveAsync` after advancing a `FakeTimeProvider` 25 h → null (checked in code, not only by the TTL index); after `MarkAuthorisedAsync`, still resolvable at 25 h.
  - `Takes_no_body_larger_than_16_KiB` → 413 or 400, nothing stored.
- [ ] **Step 2: Run** `--filter-class '*.RegistrationTests'`. Expected: FAIL.
- [ ] **Step 3: Implement.** Register `OAuthClients`, `ClientMetadataFetcher`, `OAuthLimits`; `app.MapOAuthEndpoints()`.
- [ ] **Step 4: Run** the class, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Security document.** §7 "Rate limits" row: registrations and (for Task 8) token failures; §5.6: an `oauth_clients` row (name and redirect URIs of self-registered clients, in the clear).
- [ ] **Step 6: Commit** `Accept dynamic client registration for public clients`

---

### Task 6: The authorisation endpoint and the consent API

**Files:**
- Create: `src/Kumbu.Server/OAuth/AuthorizationRequests.cs`, `src/Kumbu.Server/OAuth/OAuthTokens.cs` (codes only in this task), `src/Kumbu.Server/Accounts/Secrets.cs`
- Modify: `src/Kumbu.Server/OAuth/OAuthEndpoints.cs`, `src/Kumbu.Server/Api/ConnectionApi.cs`, `src/Kumbu.Server/Vaults/Documents.cs:21-64`, `src/Kumbu.Server/Vaults/VaultCatalog.cs:261-305,762-781`
- Test: `tests/Kumbu.Server.Tests/OAuth/AuthorizeTests.cs`, `tests/Kumbu.Server.Tests/OAuth/OAuthFlow.cs` (helper)
- Regenerate: `web/src/api/schema.d.ts`

**Interfaces:**
- Consumes: `Connections.FindAsync/OpenMeta/SetClientAsync` (Task 2), `OAuthUrls` (Task 3), `OAuthClients.ResolveAsync/RedirectMatches/MarkAuthorisedAsync` (Tasks 4–5).
- Produces:
  - `internal static class Secrets { static string New(string prefix); static bool IsWellFormed(string presented, string prefix); }` (32 bytes, Base62, 43 characters, as `ApiKeys.cs:54-57,348-359`).
  - `public sealed record PendingAuthorisation(string Id, string ClientId, string RedirectUri, string? State, string CodeChallenge, string Resource, string? ConnectionId, bool ReadOnly, string? Problem, DateTime ExpiresAt);` in `oauth_requests` (TTL 10 min, checked in code too); `AuthorizationRequests` with `CreateAsync(PendingAuthorisation, ct)`, `GetAsync(string id, ct)`, `TakeAsync(string id, ct)` (`FindOneAndDelete`: single use). `Problem`: `"not_a_connection"` (Ruling 7) or null.
  - `SlotDocument`: `const string Connection = "connection"`; `[BsonElement("connection")] string? ConnectionId`, `[BsonElement("token")] string? TokenId`, `[BsonElement("expiresAt")] DateTime? ExpiresAt`; a TTL index on `slots.expiresAt` (`ExpireAfter = 0`) and a partial index on `token`.
  - `VaultCatalog.ConnectionSlotsFromUserAsync(string userId, byte[] userKey, string connectionId, string tokenId, string token, IReadOnlyDictionary<string, KeyRight> vaults, DateTime? expiresAt, CancellationToken ct) → Task<IReadOnlyList<SlotDocument>>` — KEK `HKDF(token, "kumbu/v1/oauth-kek", salt = vaultId)`, payload the API-key slot payload (`VaultCatalog.cs:706-708`: vault key and right), AAD `slot:{vaultId}:connection:{connectionId}:{tokenId}`. Not stored here.
  - `OAuthTokenDocument` (`oauth_tokens`): `_id` (token id, 16 random bytes hex), `kind` (`code`|`access`|`refresh`), `connection`, `user`, `client`, `lookup` (`HKDF(token, "kumbu/v1/oauth-token-lookup")`, unique), `grant` (sealed under `HKDF(token, "kumbu/v1/oauth-grant")`, AAD `oauth:{tokenId}:{kind}`: `{ name, resource, connectionExpiresAt }`), for codes also `redirectUri`, `codeChallenge`, `readOnly`; `parent?` (the token id it was issued from), `createdAt`, `expiresAt?`, `usedAt?`, `successor?` (Task 8), `purgeAt` (TTL index).
  - `public sealed class OAuthTokens(IMongoDatabase db, VaultCatalog catalog, Connections connections, OAuthClients clients, IOptions<KumbuOptions> options, TimeProvider time, ILogger<OAuthTokens> log)` with consts `CodePrefix = "kumbu_ac_"`, `AccessPrefix = "kumbu_at_"`, `RefreshPrefix = "kumbu_rt_"`, `static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60)`, and in this task `Task<string> IssueCodeAsync(User user, byte[] userKey, PendingAuthorisation request, CancellationToken ct)` — opens the connection's meta, caps rights to read when `ReadOnly`, wraps slots under the code (TTL = code expiry), stores the code record (`purgeAt` = expiry + 10 min, kept to detect reuse), seals the client into the meta (`SetClientAsync`), all in one transaction that also checks the connection is still neither revoked nor expired.
  - `GET /oauth/authorize` (anonymous; never reads the cookie):
    1. `client_id` unresolvable → 303 `/consent?error=unknown_client`; `redirect_uri` missing or not matching → 303 `/consent?error=invalid_redirect_uri`.
    2. From here errors redirect to `redirect_uri` with `error`, `error_description`, `state` (if sent) and `iss` = issuer: `response_type != code` → `unsupported_response_type`; `code_challenge` missing, or `code_challenge_method` not `S256` (missing counts as `plain`) → `invalid_request`.
    3. `resource` missing or `OAuthUrls.ConnectionIdOf` null or no such connection → pending with `Problem = "not_a_connection"`; else pending with the connection id. `ReadOnly` per Ruling 3. → 303 `/consent?request={id}`.
  - Consent API (session-only, under `/api`, in `ConnectionApi.cs`):
    - `GET /api/oauth/requests/{id}` → `ConsentView(string RequestId, ClientView Client, string RedirectHost, ConsentConnectionView? Connection, bool Renewal, string? Problem)` with `ConsentConnectionView(string Name, IReadOnlyDictionary<string,string> Vaults, DateTime? ExpiresAt, ClientView? CurrentClient)` (rights already capped); `Problem` ∈ `null`, `"not_a_connection"`, `"other_user"` (connection of another user: `Connection` null), `"revoked"`, `"expired"`; `Renewal` = the connection was authorised before. Unknown or expired request → 404 `"This request has expired; start the connection again in the app."`.
    - `POST /api/oauth/requests/{id}/decision` with `ConsentDecisionRequest(bool Allow)` → `ConsentDecisionView(string Redirect)`. Takes the request (single use). Deny (any problem or none) → `{redirect_uri}?error=access_denied&state=…&iss=…`; for `not_a_connection` the error is `invalid_target`. Allow with a problem → 409 and the request is not consumed; Allow → `{redirect_uri}?code=…&state=…&iss=…`, `MarkAuthorisedAsync`. Query built with `QueryHelpers.AddQueryString` (keeps any query the redirect URI has).
  - Test helper `OAuthFlow` (used by Tasks 7–12): `static Task<OAuthFlow> StartAsync(WebApplicationFactory<Program> api, RestClient owner, string connectionId, string clientId, string redirectUri, string? scope = null)` generates a verifier (43 chars) and S256 challenge, runs `/oauth/authorize` without following redirects; `Task<ConsentView> ConsentAsync()`; `Task<string> AllowAsync()` → the code (parsed from the redirect, `state` and `iss` asserted); `Task<HttpResponseMessage> TokenAsync(params (string, string)[] form)`; properties `Verifier`, `State`, `RequestId`. Plus `static Task<string> RegisterAsync(api, string redirectUri)` (DCR client id) for tests that don't need CIMD.

- [ ] **Step 1: Write the failing tests** (`[Collection("app")]`, `PublicApi`, a DCR client with redirect `https://claude.test/cb` unless stated):
  - `Allow_redirects_with_code_state_and_iss` — redirect starts with `https://claude.test/cb?`, has `code` starting `kumbu_ac_`, the sent `state`, `iss == "https://kumbu.test"`.
  - `Deny_redirects_with_access_denied` — `error=access_denied`, `state`, `iss`; no code record exists.
  - `Plain_pkce_is_refused` — `code_challenge_method=plain` and missing method → redirect `error=invalid_request`; missing `code_challenge` likewise.
  - `Unknown_client_and_wrong_redirect_stay_on_our_page` — unknown `client_id` → 303 `/consent?error=unknown_client`; registered client with `redirect_uri=https://evil.test/cb` → `/consent?error=invalid_redirect_uri`; neither redirects anywhere else.
  - `Plain_mcp_as_resource_explains_connections` — `resource=https://kumbu.test/mcp` → consent view `Problem == "not_a_connection"`, `Connection == null`; Allow → 409; Deny → `error=invalid_target`.
  - `Missing_resource_is_not_a_connection` — no `resource` → same as above.
  - `Another_users_connection_is_not_shown` — user B opens A's request → `Problem == "other_user"`, nothing of A's connection in the body (no name, no vault).
  - `Read_scope_caps_the_rights_shown` — `scope=kumbu:read` on a write connection → `Vaults` all `"read"`; `scope=kumbu:read kumbu:write offline_access` → `"write"`.
  - `A_request_is_used_once` — second decision → 404.
  - `The_decision_needs_our_origin_and_a_session` — POST without `Origin` → 403; with an API key → 403; without a cookie → 401.
  - `Revoked_and_expired_connections_cannot_be_allowed` — `Problem == "revoked"` / `"expired"`, Allow → 409.
  - `The_code_wraps_the_vault_keys_for_a_minute` — after Allow, `slots` has one `connection` slot per vault with `expiresAt ≈ now + 60 s`; the code record has `kind == "code"`; the code itself appears nowhere in the database (search the BSON of `oauth_tokens` and `slots` for it).
  - `Authorize_needs_no_cookie` — the authorize request carries no cookie and still gets its 303 (the cookie is never consulted).
- [ ] **Step 2: Run** `--filter-class '*.AuthorizeTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces; `/oauth/authorize` responses carry `Cache-Control: no-store` and `Referrer-Policy: no-referrer`; log one line per step (Task 12 tests it): `"OAuth {Step} {Outcome} for connection {ConnectionId}, client {ClientHost}, request {RequestId}"`.
- [ ] **Step 4: Run** the class, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Regenerate the schema**; `pnpm -C web build` passes.
- [ ] **Step 6: Security document.** New §6 subsection "Connections (OAuth)", first part: the authorisation request, why the cookie isn't read there (Ruling 8), consent bound to session and pending request, CSRF through the `Origin` rule, Ruling 7, the code's slots and lifetime; the library ruling (Ruling 1); §3 slot table: a "Code → vault key and right" row.
- [ ] **Step 7: Commit** `Authorise apps through a consent page that wraps the vault keys under a one-time code`

---

### Task 7: The token endpoint and connection callers on `/mcp/c/{id}`

**Files:**
- Modify: `src/Kumbu.Server/OAuth/OAuthTokens.cs`, `src/Kumbu.Server/OAuth/OAuthEndpoints.cs`, `src/Kumbu.Server/Auth/CallerMiddleware.cs`, `src/Kumbu.Server/Auth/RequestCaller.cs:9-75`, `src/Kumbu.Server/Vaults/Credentials.cs:34-55`, `src/Kumbu.Server/Vaults/VaultCatalog.cs`, `src/Kumbu.Server/Accounts/Connections.cs` (state `Connected`, `lastUsedAt`)
- Test: `tests/Kumbu.Server.Tests/OAuth/TokenTests.cs`

**Interfaces:**
- Consumes: `OAuthFlow` (Task 6), `IssueCodeAsync` records.
- Produces:
  - `public sealed record CodeExchange(string Code, string ClientId, string RedirectUri, string CodeVerifier, string? Resource);`
  - `public sealed record TokenPair(string AccessToken, string RefreshToken, int ExpiresIn, string Scope);`
  - `public sealed record TokenResult(TokenPair? Pair, string? Error, string? Description);` (`Error` ∈ `invalid_request`, `invalid_grant`, `invalid_client`, `invalid_target`, `unsupported_grant_type`)
  - `OAuthTokens.ExchangeCodeAsync(CodeExchange exchange, CancellationToken ct) → Task<TokenResult>` — one transaction: the code record matched by lookup, `kind == code`, unexpired, `usedAt == null` (set now), client, redirect URI (exact, as stored) and `BASE64URL(SHA256(verifier)) == codeChallenge` (fixed-time) and resource (if sent, canonical equal); a used code → revoke the connection's tokens (`code_reused`) and `invalid_grant`. On success: the connection's earlier tokens and their slots are deleted (one family per connection), a refresh token (`expiresAt` = connection expiry, slots with that TTL) and an access token (`expiresAt = min(now + AccessTokenMinutes, connection expiry)`, slots with that TTL, `purgeAt = expiresAt + 24 h`) are inserted with slots re-wrapped from the code's (`VaultCatalog.ConnectionSlotsFromTokenAsync`), the code's slots deleted, `authorisedAt` set, `tokensRevokedAt/Reason` unset. `Scope` in the answer: `"kumbu:read"` if every right is read, else `"kumbu:read kumbu:write"`.
  - `VaultCatalog.ConnectionSlotsFromTokenAsync(string connectionId, string fromTokenId, string fromToken, string toTokenId, string toToken, DateTime? expiresAt, CancellationToken ct) → Task<IReadOnlyList<SlotDocument>>`; `VaultCatalog.OpenForConnectionAsync(string connectionId, string tokenId, string token, CancellationToken ct) → Task<IReadOnlyList<(UnlockedVault Vault, KeyRight Right)>>` (opens every slot, as `OpenForApiKeyAsync`, `VaultCatalog.cs:312-337`; also refuses a slot past its `expiresAt` in code); `VaultCatalog.DeleteTokenSlotsAsync(IClientSessionHandle session, IReadOnlyCollection<string> tokenIds, CancellationToken ct)`; `VaultCatalog.DeleteConnectionSlotsAsync(IClientSessionHandle session, string connectionId, CancellationToken ct)`.
  - `public sealed record ResolvedConnection(User User, string ConnectionId, string TokenId, string Name);` `public sealed record AccessResolution(ResolvedConnection? Connection, bool Known);`
  - `OAuthTokens.ResolveAccessAsync(string presented, string connectionId, string resource, CancellationToken ct) → Task<AccessResolution>` — `Known` when a record with that lookup exists (expired or not); valid only for `kind == access`, unexpired, the same connection, connection neither revoked nor expired, user active, the grant opening and its `resource` canonical-equal to `resource`. Writes `connections.lastUsedAt` at most once a minute (as `ApiKeys.cs:302-310`).
  - `public interface IRightsCredential { bool CanWrite(string vault); }` implemented by `ApiKeyCredential` and the new `public sealed class ConnectionCredential(string connectionId, string tokenId, string token) : ICredential, IRightsCredential` (shape of `ApiKeyCredential`, `Credentials.cs:38-55`); `RequestCaller.CanWrite => Credential is not IRightsCredential r || r.CanWrite(vault)` — fail-closed for every credential with rights.
  - `CallerKind.Connection`; `RequestCaller.ForConnectionAsync(ResolvedConnection connection, string token, VaultCatalog catalog, CancellationToken ct)`; writer label = `connection.Name`.
  - `CallerMiddleware` on `/mcp/c/{id}`: a bearer starting `kumbu_at_` → `ResolveAccessAsync(token, id, OAuthUrls.Resource(origin, id))`; valid → caller; `Known` but invalid → 401 challenge with `invalid_token`, `wrongKeys.Release`; unknown → 401 challenge with `invalid_token`, `wrongKeys.Failed`. Access tokens on `/mcp` and `/api` are never resolved (401 there as any unknown bearer).
  - `POST /oauth/token` (anonymous, CORS): only `application/x-www-form-urlencoded` (else 400 `invalid_request`); `grant_type=authorization_code` with `code`, `redirect_uri`, `client_id`, `code_verifier`, optional `resource`; unknown `grant_type` → `unsupported_grant_type`; JSON answers `{access_token, token_type: "Bearer", expires_in, refresh_token, scope}` or `{error, error_description}` (400; `invalid_client` 401); always `Cache-Control: no-store` and `Pragma: no-cache`; `OAuthLimits.Token*` around failures (429 with `Retry-After`).

- [ ] **Step 1: Write the failing tests** (`[Collection("app")]`, `PublicApi`, a user with vaults `Arbeit` (write) and `Privat` (read) on one connection named `cor.energy`):
  - `Exchanges_a_code_for_tokens` — 200, `token_type == "Bearer"`, `expires_in == 3600`, prefixes `kumbu_at_`/`kumbu_rt_`, `scope == "kumbu:read kumbu:write"`, `Cache-Control: no-store`; the connection lists as `Connected`.
  - `The_access_token_works_on_its_connection_url` — SDK `McpClient` over `HttpClientTransport` to `https://kumbu.test/mcp/c/{id}` with the bearer: `sh` with `ls /` lists `Arbeit` and `Privat` only.
  - `Rights_are_exactly_as_consented` — `write` to `Privat/x.md` refused with the read-only error; `write` to `Arbeit/x.md` succeeds; `history` of it shows writer `cor.energy`.
  - `The_access_token_is_refused_elsewhere` — on `/mcp` → 401 `Bearer`; on `/api/vaults` → 401; on another connection's URL → 401 `invalid_token`.
  - `Wrong_verifier_resource_client_or_redirect_is_invalid_grant` — each variation on a fresh code → 400 `invalid_grant` (wrong resource: `invalid_target`).
  - `A_code_is_spent_by_the_first_request_that_presents_it` — a wrong verifier first, then the right one → both `invalid_grant` (a code is single-use whatever the outcome, so an intercepted code that is tried burns it).
  - `A_reused_code_revokes_what_it_issued` — second exchange → `invalid_grant`; the access token from the first now gets 401; the connection is `NeedsReconnect`, `Reason == "code_reused"`.
  - `An_expired_code_is_refused` — `FakeTimeProvider` +61 s → `invalid_grant`.
  - `Json_bodies_are_refused` → 400 `invalid_request`.
  - `Access_tokens_expire_and_their_slots_carry_the_ttl` — `slots` of the access token have `expiresAt == access expiry`; at +61 min the token gets 401 with `invalid_token`.
  - `Expired_access_tokens_do_not_count_as_wrong_keys` — 25 requests from one `X-Forwarded-For` with an expired access token → all 401, none 429; then 21 with an unknown `kumbu_at_…` → the 21st is 429.
  - `A_connection_expiry_caps_the_tokens` — connection expiring in 30 days, clock at +30 d − 10 min → new exchange gets `expires_in == 600`.
  - `Every_request_opens_the_slots_afresh` — delete the access token's slots in the database: the next call is refused although the vault is unlocked in memory.
  - `Token_failures_are_limited_per_client` — 21 bad codes for one client id → 429; another client id → 400.
- [ ] **Step 2: Run** `--filter-class '*.TokenTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces.
- [ ] **Step 4: Run** the class, `McpClientTests`, `CallerTests`, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Security document.** §3: the hierarchy diagram gets "Refresh token" and "Access token (expires)" → vault key; the slot table rows for refresh and access slots (KEK labels, AAD, TTL); §6 "Connections (OAuth)" continued: one token family per connection, the grant blob, per-request slot opening, access tokens only on their URL, Ruling 12; §4: a "Stolen access or refresh token" row (its connection's vaults and rights, until it expires or is revoked).
- [ ] **Step 6: Commit** `Issue access and refresh tokens as key slots and accept them on the connection's URL`

---

### Task 8: Refresh rotation, reuse detection and revocation by the client

**Files:**
- Modify: `src/Kumbu.Server/OAuth/OAuthTokens.cs`, `src/Kumbu.Server/OAuth/OAuthEndpoints.cs`
- Test: `tests/Kumbu.Server.Tests/OAuth/RefreshTests.cs`

**Interfaces:**
- Consumes: Task 7's records, slots and `TokenResult`.
- Produces:
  - `OAuthTokens.RefreshAsync(string refreshToken, string clientId, string? resource, CancellationToken ct) → Task<TokenResult>` — `static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30)`. Valid refresh (`usedAt == null`, unexpired, same client, connection live, user active): one transaction — conditional `usedAt = now` on the old record (`usedAt == null`), new refresh + access records with slots re-wrapped from the old refresh token's, the old refresh token's slots deleted, the new pair sealed into the old record's `successor` under `HKDF(oldRefresh, "kumbu/v1/oauth-successor")` (AAD `oauth:{oldId}:successor`), the *parent's* `successor` unset (it is no longer needed once its successor is used), `purgeAt` of the old record = now + 30 days. If the conditional update matched nothing (a parallel refresh won) or `usedAt` is within the grace period: open `successor` and return that pair if its refresh token is still unused and live; else `invalid_grant`. `usedAt` older than the grace period → `RevokeConnectionTokensAsync(…, RefreshReused)` and `invalid_grant`. `scope` and `resource` in the request are optional; a sent `resource` must match.
  - `OAuthTokens.RevokeAsync(string token, CancellationToken ct) → Task` (RFC 7009): a refresh token → all of its connection's tokens (`client_revoked`); an access token → that token and its slots; anything else → nothing. `POST /oauth/revoke` (form, `token`, optional `token_type_hint`, `client_id`): always 200, `Cache-Control: no-store`.
  - `/oauth/token` with `grant_type=refresh_token`.

- [ ] **Step 1: Write the failing tests:**
  - `Refreshing_rotates_both_tokens` — new pair differs from the old; the old access token still works until its expiry; the new one works; the old refresh token's slots are gone from `slots`.
  - `A_retry_within_the_grace_period_gets_the_same_pair` — refresh twice with the same token 5 s apart (`FakeTimeProvider`): equal pairs; the connection stays `Connected`.
  - `Parallel_refreshes_of_one_token_get_the_same_pair` — `Task.WhenAll` of 5 refreshes: all 200 with one and the same pair.
  - `Reuse_after_the_grace_period_revokes_the_connection` — +31 s → `invalid_grant`; the newest refresh and access tokens are refused; `NeedsReconnect`, `Reason == "refresh_reused"`; a new consent through the same URL (`OAuthFlow`) connects again with the same name and rights.
  - `A_refresh_token_belongs_to_its_client` — other `client_id` → `invalid_grant`, nothing revoked.
  - `Refresh_after_the_connection_expired_is_invalid_grant` — and its slots are gone (the TTL equals the expiry; assert the field).
  - `The_client_can_revoke` — `/oauth/revoke` with the refresh token → 200; both tokens refused; `Reason == "client_revoked"`. With a random string → 200, nothing changes.
  - `No_successor_outlives_its_use` — after the new refresh token is itself rotated, the first record has no `successor`.
- [ ] **Step 2: Run** `--filter-class '*.RefreshTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces.
- [ ] **Step 4: Run** the class, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Security document.** §6 "Connections (OAuth)": rotation, Ruling 11 with its reasoning, reuse detection, `/oauth/revoke`.
- [ ] **Step 6: Commit** `Rotate refresh tokens, detect their reuse and let clients revoke them`

---

### Task 9: Revocation wherever an API key is revoked

**Files:**
- Modify: `src/Kumbu.Server/Accounts/UserCatalog.cs:14,144,165,245,287`, `src/Kumbu.Server/Accounts/Admin.cs:75-79`, `src/Kumbu.Server/Accounts/Deletion.cs:98-126`, `src/Kumbu.Server/Vaults/VaultCatalog.cs:358-463`, `src/Kumbu.Server/Accounts/Connections.cs`, `src/Kumbu.Server/Api/AccountApi.cs:34-36` (doc comment), `web/src/routes/Settings.tsx:133-138`
- Test: `tests/Kumbu.Server.Tests/OAuth/RevocationCascadeTests.cs`

**Interfaces:**
- Consumes: `Connections.RevokeAllTokensAsync` (Task 2), `OAuthFlow` (Task 6).
- Produces:
  - `UserCatalog(IMongoDatabase db, Argon2Kdf kdf, ApiKeys keys, Connections connections, TimeProvider time, ILogger<UserCatalog> log)`; wherever `keys.RevokeAllAsync(user, ct)` runs (`UserCatalog.cs:245,287`), `connections.RevokeAllTokensAsync(user, recovery ? ConnectionReasons.Recovery : ConnectionReasons.PasswordChanged, ct)` runs right after.
  - `Admin.SetActiveAsync`: `connections.RevokeAllTokensAsync(session, userId, ConnectionReasons.Deactivated, ct)` in its transaction, both directions as for keys.
  - `VaultCatalog.DeleteVaultsAsync` (on the collections directly, with `ConnectionRevocation`): in its transaction, `$pull` the ids from `connections.vaults`; connections left with none: `revokedAt = now`, `revokedReason = vault_deleted`, `nameId` unset, their `oauth_tokens` and slots deleted (the slots on the deleted vaults go with the vault already, `VaultCatalog.cs:401`). Log `"Connection {ConnectionId} has no vault left and is revoked"`.
  - `Deletion.DeleteAccountAsync`: also deletes the user's `connections`, `oauth_tokens`, `oauth_requests` (by connection) and the connections' slots, in the same transaction; `DeletionSummary` gets `int Connections`.
  - `Connections.RevokeAsync` and `SetVaultsAsync` delete tokens and slots in their transaction (`revoked` final; `rights_changed` → `NeedsReconnect`).
  - Settings checkbox label: "Also revoke all API keys and disconnect apps".

- [ ] **Step 1: Write the failing tests** (each starts from a connected connection via `OAuthFlow` and checks the access token is refused afterwards, the refresh gives `invalid_grant`, and the connection's state and reason):
  - `Revoking_in_the_list_is_final` — `Revoked`/`revoked`; a new authorize on its URL → consent `Problem == "revoked"`.
  - `A_password_change_that_revokes_keys_disconnects_apps` — `NeedsReconnect`/`password_changed`; with `revokeApiKeys: false` the connection keeps working.
  - `A_recovery_disconnects_apps` — `recovery`.
  - `Deactivation_disconnects_apps_and_reactivation_revives_none` — `deactivated`; after reactivation still refused.
  - `Deleting_a_vault_keeps_a_connection_with_other_vaults` — two vaults, one deleted: the token still opens the other; the list shows only the remaining vault.
  - `Deleting_its_only_vault_revokes_a_connection` — `Revoked`/`vault_deleted`, name free.
  - `Deleting_the_account_removes_every_trace` — no document in `connections`, `oauth_tokens`, `oauth_requests` or `slots` for the user's connections.
  - `Changing_rights_disconnects_until_the_next_consent` — `NeedsReconnect`/`rights_changed`; the next consent shows and grants the new rights.
  - `An_access_token_in_flight_finishes_but_the_next_is_refused` — a call started before revocation may complete; the next one is 401 (mirrors the API-key rule, security §6 "Checks are per request").
- [ ] **Step 2: Run** `--filter-class '*.RevocationCascadeTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** as in Interfaces; update `Settings.test.tsx` for the new label.
- [ ] **Step 4: Run** the class, `UserCatalogTests`, `AdminTests`, `DeletionTests`, `Settings.test.tsx`, then `dotnet test` and `pnpm -C web test`. Expected: PASS.
- [ ] **Step 5: Security document.** §6 "API keys" bullets on password change, recovery and deactivation, and "Deleting vaults and accounts": connections alongside keys, with the two kinds of revocation (Ruling 2); §1 point 5 and §4 "What to do after a lost laptop": apps are disconnected too.
- [ ] **Step 6: Commit** `Disconnect apps wherever API keys are revoked`

---

### Task 10: Web app — connections on the access page

**Files:**
- Create: `web/src/routes/Connections.tsx`, `web/src/routes/NewConnectionDialog.tsx`, `web/src/routes/ConnectorReveal.tsx`, `web/src/routes/Connections.test.tsx`
- Modify: `web/src/routes/Keys.tsx:19-63` (title "Access", two sections), `web/src/routes/NewKeyDialog.tsx` (share the rights and expiry pickers: export `RightsPicker` and `ExpiryPicker` from it), the navigation entry that says "API keys" (find with `rg -n '"API keys"' web/src`)

**Interfaces:**
- Consumes: `GET/POST /api/connections`, `PUT /api/connections/{id}/vaults`, `DELETE /api/connections/{id}` (Task 2), `ConnectionView`.
- Produces: `Connections()` (section component, rendered by `Keys.tsx` below the API keys), `NewConnectionDialog({ onClose, onCreated(view: ConnectionView) })`, `ConnectorReveal({ connection, onDone })`.

UI copy (exact, British English):
- Section heading "Connections"; intro: "A connection lets an app such as Claude (claude.ai, Desktop and the mobile apps) read or write the vaults you choose. Each connection has its own address."
- Button "Connect an app"; dialog title "Connect an app"; name field placeholder "e.g. cor.energy"; the first suggestion chip "Claude" fills nothing but the instructions shown afterwards.
- Reveal: "Connector URL" with the URL in monospace and a "Copy" button; then the three lines: "1. In claude.ai: Settings → Connectors → Add custom connector." "2. Paste this URL. Leave client ID and secret empty." "3. Tip: name the connector like this connection, e.g. 'Kumbu {name}'." and one generic line: "Other apps: add a remote MCP server with this URL; it signs in with OAuth." Then "The address is not a secret: it only says which connection is meant. Access needs your consent in Kumbu²."
- List item: name, URL (copy button), client ("Claude · claude.ai", "· self-registered" when `verified` is false), vaults and rights, "Created … · Last used …/Never used · Expires …/Doesn't expire", status text: `waiting` "Waiting for the app", `connected` "Connected", `needs_reconnect` "Needs reconnect: {reason text}", `revoked` "Revoked: {reason text}", `expired` "Expired". Reason texts: `revoked` "you revoked it", `vault_deleted` "its vaults were deleted", `password_changed` "your password was changed", `recovery` "your account was recovered", `deactivated` "your account was deactivated", `refresh_reused` "a token was used twice, which can mean it was stolen", `code_reused` "a sign-in code was used twice", `client_revoked` "the app disconnected", `rights_changed` "its rights were changed", `rekeyed` "a vault was re-encrypted". For `needs_reconnect`: "Use Reconnect in the app." Buttons "Change rights" (dialog with the rights picker; note "The app has to reconnect after this.") and "Revoke" (dialog "Revoke {name}? The app loses access at once. Its address can't be used again; make a new connection instead.").
- Revoked and expired connections are inside a `<details>` "Revoked and expired ({n})", closed by default.

- [ ] **Step 1: Write the failing tests** (`Connections.test.tsx`, the `api({...})` mock pattern of `Keys.test.tsx`):
  - `creates a connection and shows its connector URL with instructions` — POST body `{ name: "cor.energy", vaults: { Arbeit: "write" }, expiresInDays: 90 }`; the reveal shows the URL and the three lines; "Copy" writes the URL to `navigator.clipboard`.
  - `lists states with their reasons` — one of each state; texts as above; revoked/expired inside the closed `<details>`.
  - `changing rights says the app has to reconnect` — PUT body; invalidates `["connections"]`.
  - `revoking asks first` — DELETE only after confirming.
  - `keeps nothing in browser storage` — after create and reveal, `localStorage` and `sessionStorage` are empty (as `Keys.test.tsx:187`).
- [ ] **Step 2: Run** `pnpm -C web test -- Connections`. Expected: FAIL.
- [ ] **Step 3: Implement.** Page title "Access" (API keys and connections); the existing API-key texts stay.
- [ ] **Step 4: Run** `pnpm -C web test` and `pnpm -C web build`. Expected: PASS.
- [ ] **Step 5: Commit** `Show connections next to API keys in the web app`

---

### Task 11: Web app — the consent page

**Files:**
- Create: `web/src/routes/Consent.tsx`, `web/src/routes/Consent.test.tsx`
- Modify: `web/src/router.tsx:62-80` (route `/consent` inside `RequireSession`, lazy), `web/src/routes/SignIn.test.tsx` (a `next` with `?request=` survives)

**Interfaces:**
- Consumes: `GET /api/oauth/requests/{id}`, `POST /api/oauth/requests/{id}/decision` (Task 6), `ConsentView`, `ConsentDecisionView`.
- Produces: `/consent?request={id}` and `/consent?error={code}` (the latter outside `RequireSession`: it needs no session — make `/consent` a route that renders the error variant without asking for a session and the request variant inside `RequireSession`, e.g. by checking `error` first in a small wrapper).

Behaviour and copy:
- Request variant: heading "Connect {client.name}?"; line "**{client.name}** ({client.host}) wants to use your connection **{connection.name}**:" then each vault "{vault}: read" / "read and write"; "Expires {date}" if set; "It returns to {redirectHost} afterwards." For `!verified`: "This app registered itself; Kumbu² can't confirm who made it." For `renewal`: heading "Renew {connection.name} for {client.name}?" and "This renews the connection with the same vaults and rights." Buttons "Allow" (primary) and "Deny". On success `window.location.assign(redirect)`; the buttons are disabled meanwhile.
- `problem` texts: `not_a_connection` "This address isn't a connection. In Kumbu², open Access → Connect an app, and use the connector URL it shows." with "Back to {redirectHost}" (Deny); `other_user` "This connection belongs to another Kumbu² account. Sign out and sign in as its owner." with "Sign out" (DELETE `/api/session`, then sign-in with `next` back here) and "Back to {redirectHost}"; `revoked` "This connection was revoked. Make a new connection in Kumbu² and add its URL in the app." with "Back"; `expired` likewise with "expired".
- 404: "This request has expired. Start the connection again in the app." (the server's message).
- Error variant texts: `unknown_client` "The app could not be identified, so Kumbu² can't connect it."; `invalid_redirect_uri` "The app asked to return to an address it didn't register. Nothing was connected."; anything else "The app's request could not be used. Nothing was connected."
- Not signed in: the existing `RequireSession` → `Root` sends to `/signin?next=/consent?request=…`; `safeNext` keeps the query.
- Nothing about the request goes into browser storage; the view's query uses `gcTime: 0`.

- [ ] **Step 1: Write the failing tests:**
  - `shows exactly what is being connected` — client "Claude · claude.ai", connection "cor.energy", "Arbeit: read and write".
  - `allow navigates to the redirect` — POST `{ allow: true }`; `window.location.assign` called with the returned URL.
  - `deny navigates with the denial` — POST `{ allow: false }`.
  - `Signs_in_first_and_comes_back_to_the_same_request` — the session query 401s → navigates to `/signin?next=%2Fconsent%3Frequest%3Dr1`; `safeNext("/consent?request=r1") === "/consent?request=r1"`.
  - `An_expired_request_says_to_start_again` — GET 404 → the text above, no buttons.
  - `the plain mcp address explains connections` — `problem: "not_a_connection"`: text, no Allow.
  - `another user's connection offers sign-out` — `problem: "other_user"`: "Sign out" calls DELETE `/api/session`.
  - `errors before the app is trusted stay here` — `/consent?error=invalid_redirect_uri` without a session renders its text and makes no request.
  - `keeps nothing in browser storage`.
- [ ] **Step 2: Run** `pnpm -C web test -- Consent`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `pnpm -C web test` and `pnpm -C web build`. Expected: PASS.
- [ ] **Step 5: Security document.** §7 "Browser-side" point in the §4 "Lost or stolen laptop" row: the consent page holds no token, nothing in storage (`file:line` of the tests).
- [ ] **Step 6: Commit** `Add the consent page apps send users to`

---

### Task 12: End to end with the SDK's OAuth client, flow logging and the README

**Files:**
- Create: `tests/Kumbu.Server.Tests/OAuth/SdkFlowTests.cs`
- Modify: `src/Kumbu.Server/OAuth/*.cs` (log lines where Task 6–8 missed one), `README.md:39-93` (Security; new subsection "Connecting claude.ai and other apps"; "Security notes for a public instance"), `docs/security-architecture.md` (§1, §2, §8, §9)

**Interfaces:**
- Consumes: everything above; the SDK client's OAuth support in `ModelContextProtocol` 2.3.0 (`HttpClientTransportOptions.OAuth` with `ClientOAuthOptions`: `RedirectUri`, `ClientMetadataDocumentUri`, `AuthorizationRedirectDelegate`; confirm the names against the 2.3.0 API before writing the test).
- Produces: nothing for later tasks.

- [ ] **Step 1: Write the failing tests:**
  - `The_sdk_client_connects_through_cimd` — `MetadataServer` (Task 4) serves a document for redirect `http://127.0.0.1:1/callback`; the app runs with `AllowLoopbackClients=true`; the SDK client connects to `https://kumbu.test/mcp/c/{id}` with `ClientMetadataDocumentUri`; the redirect delegate GETs the authorisation URL on the test server, follows the 303 to `/consent?request=…`, POSTs the decision with the owner's session and `Origin`, and returns the code from the redirect. Then `sh ls /` works; the server saw: the 401 challenge, the PRM, the AS metadata, the CIMD fetch, authorize, token.
  - `The_sdk_client_connects_through_registration` — the same with dynamic registration (no `ClientMetadataDocumentUri`).
  - `Both_handshakes_work_on_a_connection_url` — raw HTTP (as `Mcp/RawWireTests.cs`) with an access token: the legacy `initialize` (protocol `2025-06-18`) and the 2026-07-28 `server/discover` both answer 200 with `serverInfo.name == "kumbu"`.
  - `The_flow_is_logged_without_secrets` — `CapturedLog` over a full flow with refresh and revoke: lines for `authorize`, `consent`, `token`, `refresh`, `revoke` each with the connection id and the client host; none contains the code, the verifier, either token, the `state`, or any part of the metadata document.
  - `A_token_issued_but_never_used_can_be_followed` — after a token exchange without a later MCP call, the log has `token … issued` and the connection's `lastUsedAt` is null (what Chris needs to chase claude-ai-mcp #228-style reports).
- [ ] **Step 2: Run** `--filter-class '*.SdkFlowTests'`. Expected: FAIL where logging or wiring is missing; fix until it passes.
- [ ] **Step 3: README.** New subsection "Connecting claude.ai and other apps": what a connection is; Access → Connect an app; the connector URL; claude.ai steps (Settings → Connectors → Add custom connector, URL only, client ID and secret empty); available in Desktop and the mobile apps; Reconnect after a revocation, password change, recovery or rights change; plain `/mcp` is for API keys only (Claude Code, Hermes and other local agents) and has no OAuth; the refresh token lives at the app's provider (Anthropic, OpenAI, xAI…), which therefore holds key material to the chosen vaults — consistent with the threat model, since it sees what it reads anyway; `KUMBU_OAUTH_ACCESS_TOKEN_MINUTES`. In "Security notes for a public instance": an IPv4 `A` record; no redirect on `/mcp/c/…` or `/.well-known/…` (no `http`→`https` or apex→`www` for the URL users paste); the proxy, WAF or fail2ban must let `160.79.104.0/21` and the `Claude-User` user agent through; no strict `Origin` rule on `/mcp`; route `/.well-known/` and `/oauth/` to Kumbu²; metadata changes take about 5 minutes to reach claude.ai (cache).
- [ ] **Step 4: Security document.** §1 (agents and apps connect with API keys or connections), §2 "What is not protected" (the app's provider holds a refresh token, i.e. key material to the connection's vaults), §8 "AI providers" rewritten from "Later, with connectors" to as built, §9 item 2 removed (built), the scope line of the front matter and the title date updated.
- [ ] **Step 5: Run** `dotnet test`, `pnpm -C web test`, `pnpm -C web build`. Expected: PASS.
- [ ] **Step 6: Commit** `Test the OAuth flow with the SDK client end to end and document connections`

---

### Task 13: Re-keying design document

**Files:**
- Create: `docs/superpowers/specs/2026-10-26-rekeying-design.md`

No code, no tests. Written from the code as built after Task 12, not from memory.

- [ ] **Step 1: Inventory.** List every place keyed by a user key (`rg -n 'userKey' src/Kumbu.Server`) and by a vault key (`UnlockedVault.cs:90-202`, `VaultCatalog.cs:112,207,618`), with `file:line`: what is sealed, what is a keyed hash, which AAD.
- [ ] **Step 2: Write the design**, sections: 1 Why (security §3 "No key rotation", §9 item 1); 2 Stage 1 — rotate the user key: what is re-wrapped or recomputed (Ruling 20's list; current session re-wrapped from its cookie secret, other sessions end, `credentialsChangedAt` stamped), the recovery-key decision with its justification, one transaction, the `PrepareAsync`/`ApplyAsync` split and why recovery needs it (its write order, `UserCatalog.cs:234-290`); 3 Stage 2 — re-key a vault: lock (both gates and `_loading`, as `DeleteVaultsAsync`, `VaultCatalog.cs:358-441`), read everything with the old key, re-seal every document kind with the new key and recompute `pid` hashes, re-wrap the owner's user slot, delete and revoke API-key slots (whole keys) and connection tokens (`rekeyed`), one transaction, mark the old `UnlockedVault` retired so a request still holding it writes nothing (`VaultUnavailableException("This vault was just re-encrypted; try again.")`), drop it from the working set and re-index nothing (vectors are only re-sealed); tombstones respected; size and the 60 s transaction limit (measure with 2000 notes in a test); 4 Triggers — recovery (stage 1 inside the recovery, then stage 2 per vault, `rekeyPending` and resuming at sign-in), Settings "Re-encrypt my vaults" (password; stage 1 + stage 2 for all vaults; new recovery key shown once), per vault (password; stage 2; the list of keys and connections that will lose access shown first); 5 Crash points and what each leaves (before, inside, between vaults); 6 What it protects and what not (an old backup keeps what it had; an old credential plus an old backup can't open data written afterwards); 7 Tests (the ones in Tasks 14–16). Rulings that the code proves wrong are corrected here, with the reason, and the later tasks follow the document.
- [ ] **Step 3: Commit** `Design key rotation and re-keying from the code as built`

---

### Task 14: Re-keying stage 1 — rotate the user key

**Files:**
- Create: `src/Kumbu.Server/Accounts/UserKeyRotation.cs`
- Modify: `src/Kumbu.Server/Vaults/VaultCatalog.cs` (user-slot re-wrap helper), `src/Kumbu.Server/Accounts/ApiKeys.cs` and `Connections.cs` (re-seal helpers), `src/Kumbu.Server/Accounts/Sessions.cs` (re-wrap one session), `src/Kumbu.Server/Program.cs`
- Test: `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record PreparedRotation(string UserId, byte[] NewUserKey, string RecoveryKey, IReadOnlyList<WriteModel<SlotDocument>> VaultSlots, IReadOnlyList<WriteModel<UserSlotDocument>> UserSlots, IReadOnlyList<WriteModel<ApiKeyDocument>> ApiKeys, IReadOnlyList<WriteModel<ConnectionDocument>> Connections, byte[] UserKeyId);`
  - `public sealed class UserKeyRotation(IMongoDatabase db, Argon2Kdf kdf, VaultCatalog catalog, TimeProvider time, ILogger<UserKeyRotation> log)` with `Task<PreparedRotation> PrepareAsync(User user, byte[] oldUserKey, string password, string? keepSessionSecret, CancellationToken ct)`, `Task ApplyAsync(IClientSessionHandle session, PreparedRotation rotation, CancellationToken ct)` (fails the transaction if any slot changed since `PrepareAsync`, by matching each replaced document's old `wrapped`), and `Task<PreparedRotation> RotateAsync(User user, byte[] oldUserKey, string password, string? keepSessionSecret, CancellationToken ct)` (stamp, prepare, apply in its own transaction).
  - Test hook: `internal Func<string, Task>? FaultAt` called with `"before-apply"` and `"inside-apply"`.

- [ ] **Step 1: Write the failing tests:**
  - `After_rotation_password_and_new_recovery_key_open_the_new_user_key` — sign-in returns a key ≠ the old; `RecoverAsync` with the returned recovery key works; with the old recovery key → null.
  - `Vaults_api_keys_and_connections_keep_working` — the user's vaults open with the new key; an API key (its slots don't involve the user key) still opens its vault; the key list and the connection list still open (metas re-sealed), names still unique (create with a taken name → 409).
  - `Only_the_current_session_survives` — the kept session resolves to the new user key; another session → null.
  - `The_old_user_key_opens_nothing` — `OpenForUserAsync(userId, oldKey)` → empty; `ApiKeys.ListAsync` with the old key → empty.
  - `A_crash_before_or_inside_apply_leaves_the_old_key_working` — `FaultAt` throws at each point: old password signs in to the old key, every vault opens.
  - `A_concurrent_password_change_makes_apply_fail` — change the password between prepare and apply → `ApplyAsync` throws, nothing written.
- [ ] **Step 2: Run** `--filter-class '*.UserKeyRotationTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** per the design document.
- [ ] **Step 4: Run** the class, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit** `Rotate a user's key with every slot and keyed hash that hangs on it`

---

### Task 15: Re-keying stage 2 — re-key a vault

**Files:**
- Create: `src/Kumbu.Server/Vaults/VaultCatalog.Rekey.cs`
- Modify: `src/Kumbu.Server/Vaults/VaultCatalog.cs:20` (`partial`), `src/Kumbu.Server/Vaults/UnlockedVault.cs` (`Retired` flag), `src/Kumbu.Server/Vaults/EncryptedNoteStore.cs` and `src/Kumbu.Server/Search/SearchIndex.cs` (refuse writes and indexing for a retired vault, beside the `Deleted` checks)
- Test: `tests/Kumbu.Server.Tests/Vaults/RekeyTests.cs`

**Interfaces:**
- Consumes: `ConnectionRevocation.RevokeTokensAsync` (Task 2; not the `Connections` service, see there), `ConnectionReasons.Rekeyed`.
- Produces:
  - `public sealed record RekeySummary(long Documents, IReadOnlyList<string> RevokedApiKeys, IReadOnlyList<string> DisconnectedConnections);`
  - `internal Task<RekeySummary> RekeyAsync(string vaultId, string userId, byte[] userKey, CancellationToken ct)` on `VaultCatalog` — `VaultUnavailableException` for a vault without this user's user slot or a tombstoned one.
  - `internal Task<(IReadOnlyList<string> ApiKeyIds, IReadOnlyList<string> ConnectionIds)> AccessAsync(string vaultId, CancellationToken ct)` — who would lose access (for the UI).
  - Test hook `internal Func<string, Task>? RekeyFaultAt` with `"after-read"`, `"inside-transaction"`, `"after-commit"`.

- [ ] **Step 1: Write the failing tests** (a vault with notes, history, a trashed note, a move, embeddings, two checkpoints sharing a text, an external embedding choice):
  - `Everything_opens_with_the_new_key_and_reads_the_same` — before/after: `ls`, `cat` of every note, `history`, `trash`, checkpoints and their diff, `semsearch` results, the vault name and its provider choice are equal.
  - `Nothing_is_sealed_under_the_old_key_any_more` — opening any document of the vault with the old vault key fails; `pid` values all changed.
  - `Api_keys_with_the_vault_are_revoked_and_connections_disconnected` — a key with this and another vault: revoked entirely; a connection: `NeedsReconnect`/`rekeyed`; a reconnect through its URL opens the vault with the new key.
  - `An_old_credential_with_an_old_backup_cannot_read_later_writes` — copy the vault's documents and slots (the "backup"), re-key, write a note, then try the old API key against the backup slots plus the new note document → fails.
  - `A_crash_at_any_point_leaves_one_consistent_key` — `RekeyFaultAt` throws at each point: afterwards every document opens under exactly one key (old for the first two, new for the third) and the vault loads.
  - `A_request_holding_the_old_vault_writes_nothing` — hold the old `UnlockedVault`, re-key, write through it → `VaultUnavailableException`; nothing written.
  - `A_vault_being_deleted_is_not_rekeyed` — tombstoned id → `VaultUnavailableException`.
  - `Rekeys_2000_notes_within_the_transaction_limit` — 2000 notes with embeddings: completes, under 30 s on the test machine (assert < 50 s; log the time).
- [ ] **Step 2: Run** `--filter-class '*.RekeyTests'`. Expected: FAIL.
- [ ] **Step 3: Implement** per the design document.
- [ ] **Step 4: Run** the class, `EncryptedNoteStoreTests`, `SearchIndexTests`, `DeletionTests`, then `dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit** `Re-key a vault in one transaction, revoking the access that can't be re-wrapped`

---

### Task 16: Re-keying triggers — recovery, Settings, per vault

**Files:**
- Create: `src/Kumbu.Server/Accounts/Rekeying.cs`, `tests/Kumbu.Server.Tests/Accounts/RekeyingTests.cs`
- Modify: `src/Kumbu.Server/Accounts/UserCatalog.cs:122-147,234-290`, `src/Kumbu.Server/Accounts/UserDocuments.cs` (`RekeyPending`), `src/Kumbu.Server/Api/AccountApi.cs:96-108,123-141`, `src/Kumbu.Server/Api/NoteApi.cs:107-111`, `web/src/routes/Settings.tsx`, `web/src/routes/Settings.test.tsx`
- Regenerate: `web/src/api/schema.d.ts`

**Interfaces:**
- Consumes: `UserKeyRotation` (Task 14), `VaultCatalog.RekeyAsync/AccessAsync` (Task 15).
- Produces:
  - `public sealed class Rekeying(UserCatalog users, UserKeyRotation rotation, VaultCatalog catalog, IMongoDatabase db, ILogger<Rekeying> log)` with `Task<string?> ReencryptAccountAsync(User user, byte[] userKey, string password, string sessionSecret, CancellationToken ct)` (null for a wrong password; returns the new recovery key), `Task<RekeySummary?> RekeyVaultAsync(User user, byte[] userKey, string vaultName, string password, CancellationToken ct)` (null: no such vault; `ArgumentException` for a wrong password, checked first as `Deletion.cs:38`), `Task ResumePendingAsync(User user, byte[] userKey, CancellationToken ct)`.
  - `UserDocument.RekeyPending` (`[BsonElement("rekeyPending"), BsonIgnoreIfNull] List<string>?`).
  - Recovery: `UserCatalog.RecoverAsync` rotates the user key inside its transaction (`ApplyAsync`, keeping its write order) and then re-keys every vault of the user, each in its own transaction, recording `rekeyPending` in the rotation's transaction and removing each id on its commit. `AccountApi.SignInAsync` calls `ResumePendingAsync` after a successful sign-in when `rekeyPending` is non-empty (the answer waits for it).
  - REST (session-only, password-check limit as `AccountApi.cs:194-218`): `POST /api/account/reencrypt` with `ReencryptRequest(string Password)` → `RecoveryKeyView`; `GET /api/vaults/{vault}/access` → `VaultAccessView(string[] ApiKeys, string[] Connections)` (names); `POST /api/vaults/{vault}/rekey` with `RekeyVaultRequest(string Password)` → `RekeyVaultView(int RevokedApiKeys, int DisconnectedApps)`.
  - Web, Settings: a "Re-encrypt my vaults" section ("New keys for your account and every vault. API keys and connected apps lose access and need to be set up or reconnected; you get a new recovery key.") with password, then the new recovery key through `RecoveryKeyReveal`; per vault, next to "Delete vault", "Re-encrypt vault" with a dialog listing the API keys and connections that will lose access (from `/access`) and the password.

- [ ] **Step 1: Write the failing tests:**
  - `RekeyingTests`: `A_recovery_rotates_the_user_key_and_rekeys_every_vault` (old vault keys open nothing; the new recovery key works); `A_recovery_interrupted_between_vaults_resumes_at_sign_in` (fault after the first vault: `rekeyPending` holds the rest; the next sign-in completes it and clears it); `Reencrypt_needs_the_password_and_returns_a_new_recovery_key`; `Rekeying_one_vault_needs_the_password_before_the_name_is_looked_up` (wrong password with an unknown name → 400 like `Deletion`); `Access_lists_what_will_lose_access`.
  - `Settings.test.tsx`: `re-encrypting shows the new recovery key once`; `re-encrypting a vault lists the keys and apps that lose access first`.
- [ ] **Step 2: Run** the server class and `pnpm -C web test -- Settings`. Expected: FAIL.
- [ ] **Step 3: Implement**; regenerate the schema.
- [ ] **Step 4: Run** `dotnet test`, `pnpm -C web test`, `pnpm -C web build`. Expected: PASS.
- [ ] **Step 5: Commit** `Re-encrypt after a recovery, on request and per vault`

---

### Task 17: Re-keying in the security document and the README

**Files:**
- Modify: `docs/security-architecture.md` (§2 "Old copies of the database with old credentials", §3 "No key rotation" `:93`, §6 recovery and lost laptop `:126,262-266`, §7 backups `:317`, §9 item 1 and known gaps `:331-340`), `README.md` ("Recovery keys", "Accounts, API keys and the REST API")

- [ ] **Step 1: Rewrite** §3 "No key rotation" as "Key rotation and re-keying" (as built, `file:line`), §2's limit narrowed to data written before the last re-keying, §6 recovery (user key rotated, vaults re-keyed, `rekeyPending`), §4 "What to do after a lost laptop" (add "Re-encrypt my vaults"), §7 backups (an old backup with an old credential opens only what it held), §9 item 1 removed, its known-gap line replaced by what remains (a password change alone doesn't rotate; re-keying revokes API keys and needs reconnects; dev-token vaults can't be re-keyed). README: the two new actions and what they cost the user.
- [ ] **Step 2: Check** every `file:line` you wrote resolves (`sed -n` each).
- [ ] **Step 3: Commit** `Document key rotation and re-keying as built`

---

### Task 18: Acceptance checklist, OAuth flow diagram and the pull request

**Files:**
- Create: `docs/notes-test-phase10.md`
- Modify: `docs/security-architecture.md` (§6 "Connections (OAuth)": the diagram), `README.md` (link to the diagram)

- [ ] **Step 1: Diagram.** A Mermaid `sequenceDiagram` in §6 "Connections (OAuth)", participants: User (browser), claude.ai, Kumbu² (MCP `/mcp/c/{id}`), Kumbu² (OAuth), Kumbu² web app, MongoDB. Messages: probe POST → 401 + `resource_metadata`; GET PRM; GET AS metadata; (CIMD) Kumbu² fetches `https://claude.ai/oauth/mcp-oauth-client-metadata`; browser → `/oauth/authorize` (PKCE S256, `resource`, `state`) → 303 `/consent?request=…`; sign-in if needed; consent view; Allow → slots wrapped under the code (TTL 60 s) → redirect with `code`, `state`, `iss`; claude.ai → `/oauth/token` (code + verifier) → refresh and access slots re-wrapped, code slots deleted → tokens; MCP calls with the access token (slots opened per request); refresh → rotation; revoke → slots deleted → 401 → "Reconnect". Under it, a `flowchart` of the connection states (waiting → connected ⇄ needs reconnect; → revoked; → expired) with the reasons on the edges. Check it renders (`npx -y @mermaid-js/mermaid-cli@11.4.2 -i <extracted block> -o /tmp/x.svg` if the network allows; otherwise review the syntax by eye).
- [ ] **Step 2: Acceptance checklist** in `docs/notes-test-phase10.md`, for Chris on `https://kumbu.tidesson.net` after deploying through Komodo, each item a checkbox with an empty "Result" line:
  1. Before: the deployed image is this PR's build (`/healthz`, `serverInfo.version`); `curl -si https://kumbu.tidesson.net/.well-known/oauth-authorization-server` shows the fields of Task 3; `curl -si -X POST https://kumbu.tidesson.net/mcp/c/<id>` → 401 with `resource_metadata`; `curl -si -X POST https://kumbu.tidesson.net/mcp` → 401 `Bearer` only.
  2. claude.ai web (work account): Access → Connect an app "cor.energy", vault `cor.energy` read and write, 90 days → copy the URL → claude.ai Settings → Connectors → Add custom connector, URL only → Kumbu²'s consent shows "Claude (claude.ai) … cor.energy: read and write" → Allow → *Connected*; in a chat: list the vault, read a note, edit a section, write a new note; in Kumbu² the history shows writer `cor.energy`; the connection shows "Connected", "Claude · claude.ai", last used today.
  3. Deny once (a second connection): claude.ai reports the denial; nothing connected.
  4. Claude Desktop: the same connector is there without adding it; read and write work; sign-in (if asked) opens the system browser.
  5. Phone (iOS/Android Claude app): the connector is listed; a read works; if a sign-in is needed, the in-app browser shows Kumbu²'s sign-in first and then the same consent.
  6. A second Claude account (private) with its own connection and URL sees only its own vaults; pasting the work URL there and signing in as the same Kumbu² user shows the work connection's consent (the URL decides, as designed); signing in as another Kumbu² user shows "belongs to another Kumbu² account".
  7. Revoke in Kumbu² → the next tool call in claude.ai fails and the connector shows Reconnect → Reconnect shows "This connection was revoked" (final, Ruling 2) → make a new connection; and separately: change the connection's rights → Reconnect in claude.ai → consent "Renew …" → works with the new rights.
  8. After an hour (access-token expiry): a tool call still works without user action (refresh); if not, note the time and check the server log for `refresh` lines (claude-ai-mcp #228).
  9. Paste the plain `https://kumbu.tidesson.net/mcp` into claude.ai → Kumbu²'s page explains connections.
  10. Claude Code with the existing API key on `/mcp`: unchanged (`claude mcp list`, a read and a write).
  11. MCP Inspector locally (v2.12.0 or later) against a local instance (`KUMBU_PUBLIC_URL=http://localhost:8099`, `Kumbu__OAuth__AllowLoopbackClients=true`): `npx @modelcontextprotocol/inspector@2.12.0`, Streamable HTTP to `http://localhost:8099/mcp/c/<id>`, via proxy; "Quick OAuth Flow" with DCR; then CIMD with `~/.mcp-inspector/storage/client.json` `{ "cimd": { "enabled": true, "clientMetadataUrl": "http://127.0.0.1:8765/client.json" } }` and a document served by `python3 -m http.server 8765` (redirect `http://localhost:6274/oauth/callback`); tools list and a call work; Revoke in Kumbu² → the Inspector gets 401.
  12. Re-keying: Settings → Re-encrypt my vaults → new recovery key shown once; the claude.ai connector needs Reconnect and works after it; Claude Code's API key no longer works (expected; make a new one).
  13. Server log after all this: no token, code or verifier anywhere (`docker compose logs kumbu | grep -E 'kumbu_(at|rt|ac)_'` finds nothing).
- [ ] **Step 3: Final verification.** `dotnet test`, `pnpm -C web test`, `pnpm -C web build`, `git status` clean apart from intended files, every commit has the trailer (`git log master.. --format=%B | grep -c 'Claude-Session:'` equals `git rev-list --count master..`).
- [ ] **Step 4: Commit** `Add the OAuth flow diagram and the acceptance checklist`
- [ ] **Step 5: Pull request** to `master` (with `gh pr create` or the session's pull-request action), title "App connectors through OAuth, and re-keying", body: what it adds (connections, consent, tokens as slots, CIMD and DCR, re-keying), the Rulings list in one line each, how it was tested (and whether the Mongo tests ran locally or only in CI), "Acceptance: docs/notes-test-phase10.md, on kumbu.tidesson.net after deployment", ending with:
  ```
  🤖 Generated with [Claude Code](https://claude.com/claude-code)

  https://claude.ai/code/session_01SoN5r1LrSSPZWEviJyt1nc
  ```
