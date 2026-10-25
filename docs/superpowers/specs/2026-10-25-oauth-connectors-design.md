# Claude and other app connectors through OAuth 2.1 — Design

Date: 2026-10-25. Decided with Chris in a brainstorming session, from a brief by Lyra (claude.ai) and research into the current MCP authorization specification and Anthropic's connector documentation (sources in §11). Implementation is planned for Claude Code on the web, starting from `master` once the overnight pull request of 2026-10-25 is merged (it brings API key expiry, deletion, embedding drivers and the editor work this design builds on), ending in a pull request to `master`. The public instance `https://kumbu.tidesson.net` runs under Komodo and can be deployed at any time; acceptance with claude.ai happens there. The same pull request also carries **key rotation and re-keying** (§10), moved here from the overnight plan's T3 because it has to cover the connections' slots too.

## 1. Why

Kumbu² works well from Claude Code with a static API key. claude.ai (web, Desktop and the mobile apps) can't send a static header to a custom connector: it expects OAuth. Chris wants to show Kumbu² on Tuesday 2026-10-27 to COR.energy's CTO and team in **Claude Desktop, claude.ai on the web and on the phone**, and Kumbu² is a general-purpose tool for everyone, so other app clients (Grok, ChatGPT and whatever comes) should connect the same way.

## 2. What the user sees (the demo, decided by Chris)

The connection starts **in Kumbu²**, like a Discord app authorisation that shows its rights (decided by Chris):

1. Chris has a Kumbu² account for his work and a vault `cor.energy`.
2. In Kumbu², **Connect an app** (with "Claude" as the first suggestion): he names the connection, e.g. "cor.energy", chooses **vaults and rights** per vault (none / read / read and write — exactly as for an API key) and optionally an **expiry** (as for API keys).
3. Kumbu² shows the connection's **own connector URL**, e.g. `https://kumbu.tidesson.net/mcp/c/k7f3q9`, with a copy button and three lines of instructions ("In claude.ai: Settings → Connectors → Add custom connector, paste this URL, leave client ID and secret empty; tip: name the connector like this connection, e.g. 'Kumbu cor.energy'"). The id in the URL is not a secret; it only says *which* connection is meant.
4. In claude.ai (his work account) he adds the connector with that URL. Claude sends him to **Kumbu²'s consent page** (a page of the React app, in its look). Not signed in to Kumbu² there (e.g. in the phone's in-app browser)? Kumbu²'s normal sign-in comes first, then back. The page says exactly what is being connected: "**Claude** (claude.ai) wants to use your connection **cor.energy**: vault cor.energy, read and write" — **Allow** or **Deny**. This page is Kumbu²'s own; Claude never sees the password.
5. Allow → back in Claude the connector shows *Connected*; a connector added on claude.ai is available in Desktop and the mobile apps too. Deny → Claude gets `access_denied`.
6. In Kumbu², the connection is listed next to the API keys: name, its connector URL (copyable again), client once authorised, vaults and rights, created, last used, expiry, **Revoke**.

**Why a URL per connection** (Chris's concern): OAuth from claude.ai carries no account identity — Kumbu² can't tell Chris's work Claude from his private one; both are just "Claude". With one shared URL, a reconnect could renew the wrong connection and hand a private account the work vaults. With a URL per connection, a **reconnect** (Claude's "Reconnect" after an expiry or a revocation) arrives on that same URL and renews exactly that connection: same name and rights, new tokens, no choice to get wrong.

**Any number of connections** per user (decided by Chris): a work and a private Claude account each get their own connection with their own URL and vaults.

**No OAuth on the plain `/mcp`** (decided by Chris: it would confuse): `/mcp` stays the endpoint for API keys (Claude Code, Hermes, other local agents) and answers a request without a valid key with `401` **without** pointing to OAuth metadata; the README says to create a connection in Kumbu² and use its URL.

## 3. Goals, non-goals, anti-goals

Goals:
- A claude.ai custom connector with only the URL reaches *Connected* and the tools work (read, edit, write) in claude.ai web, Desktop and mobile.
- A connection has **parity with an API key**: named (names are unique per user **across API keys and connections**, so the writer label in a note's history is unambiguous), per-vault rights, expiry optional, listed, revocable at once; it writes under its own writer label (the connection's name) like a key does.
- A reconnect renews the same connection without asking again for vaults and rights (they are shown on the consent page and can be changed in Kumbu²'s list before).
- **Any client** that follows the MCP authorization spec may connect (decided by Chris: no allow-list; YAGNI until someone needs one). Whether it gets anything is the user's consent.
- Static API keys (Claude Code, Hermes and other local agents) keep working unchanged.
- Encryption at rest keeps its promise: without a valid token (or key, password, recovery key) the data stays sealed; tokens are key material and treated so.

Non-goals (now):
- External identity providers for signing in (GitHub login is a separate step; see `docs/security-architecture.md` §9).
- An allow-list or admin policy for clients.
- ChatGPT-specific quirks beyond what the spec requires (note them; test claude.ai first).
- Re-keying of vaults (separate track; this design only has to stay compatible with it).

Anti-goals (a failure even if it works):
- A second permission system next to API keys.
- Tokens, codes, PKCE verifiers or keys in URLs (other than the one-time `code` and `state` the protocol puts in the redirect), logs or browser storage.
- A "Connected" that silently loses access later without the user being able to see why in Kumbu².

## 4. The protocol, as Kumbu² implements it

Built into Kumbu², **same origin** as `/mcp` (claude.ai ignores external authorization servers in practice; only the first entry of `authorization_servers` is used). Following the MCP authorization specification revision 2026-07-28.

- **One protected resource per connection:** the MCP endpoint of a connection is `/mcp/c/{id}` (same MCP server as `/mcp`, the caller resolved from the token). Its **protected resource metadata** (RFC 9728) is at the path-suffixed well-known URL `/.well-known/oauth-protected-resource/mcp/c/{id}`: `resource` = the exact public URL `{PublicUrl}/mcp/c/{id}`, `authorization_servers` = [the issuer], `bearer_methods_supported: ["header"]`, `scopes_supported`. An unknown or revoked-and-deleted id answers 404 (no metadata), so ids can't be probed for more than existence; ids are random (≥ 64 bits).
- **`/mcp/c/{id}` without a valid token** answers a real HTTP **401** with `WWW-Authenticate: Bearer resource_metadata="<url>", scope="<scopes>"` (and `error="invalid_token"` for an expired/revoked one). An access token is only valid on the resource it was issued for (its connection's URL); an API key is not accepted on `/mcp/c/{id}`, and an access token not on `/mcp`. Kumbu² doesn't use ASP.NET authentication: bearer resolution and the 401 happen in `src/Kumbu.Server/Auth/CallerMiddleware.cs`; extend it there (resolve access tokens for `/mcp/c/{id}`, add the `WWW-Authenticate` challenge with `resource_metadata` and `scope`). Whatever is used, the 401 must be a real HTTP 401 — the SDK's `[Authorize]` on tools yields HTTP 200 with a JSON-RPC error, which doesn't trigger sign-in.
- **Authorization server metadata** (RFC 8414) at `/.well-known/oauth-authorization-server`: issuer = the public origin without a path; `authorization_endpoint`, `token_endpoint`, `registration_endpoint`, `revocation_endpoint`; `response_types_supported: ["code"]`; `grant_types_supported: ["authorization_code", "refresh_token"]`; `code_challenge_methods_supported: ["S256"]`; `token_endpoint_auth_methods_supported: ["none"]` (public clients; required for Claude to use CIMD); `client_id_metadata_document_supported: true`; `authorization_response_iss_parameter_supported: true` (RFC 9207, `iss` in the redirect).
- **Client identification — both ways** (my call; Chris: no restriction on clients):
  - **Client ID Metadata Documents** (preferred by the spec; Claude uses it when the metadata above says so): the `client_id` is an https URL; Kumbu² fetches the JSON document there and takes `client_name`, `redirect_uris`, `logo_uri` (optional, not rendered as remote content — see §8) from it. Claude's is `https://claude.ai/oauth/mcp-oauth-client-metadata` (`client_name` "Claude", the single redirect `https://claude.ai/api/mcp/auth_callback`; it also lists a `jwt-bearer` grant Kumbu² must tolerate and ignore).
  - **Dynamic Client Registration** (RFC 7591, deprecated by the spec but still the only way for some clients): `POST /oauth/register`, public clients only, rate-limited per client network, registrations without a successful authorisation expire (e.g. after 24 h).
- **Authorization endpoint** `/oauth/authorize`: authorization code flow with **PKCE S256 required** (`plain` refused), `state` passed through, `resource` (RFC 8707) required and must equal a connection's resource exactly (claude.ai sends the URL the user entered); it selects the connection, which must belong to the signed-in user (otherwise the page says so and offers sign-out), `redirect_uri` must match a registered/declared one **exactly**. Errors before the redirect URI is trusted are shown on Kumbu²'s page, never redirected.
- **Token endpoint** `/oauth/token`, `application/x-www-form-urlencoded`: `authorization_code` (code single-use, about 60 s, bound to client, redirect URI, PKCE challenge and resource; a reused code revokes what was issued from it) and `refresh_token` (rotation on every use, see §5). Responses `Cache-Control: no-store`. Answer within claude.ai's 10 s timeout.
- **Revocation endpoint** (RFC 7009) `/oauth/revoke` for clients that call it.
- Scopes: `kumbu:read` and `kumbu:write` advertised; they are hints only — **the rights are what the user chose per vault at consent** (an unknown or missing scope doesn't fail; a requested `kumbu:read` preselects read only on the consent page). My call.

## 5. Tokens as key slots (from `docs/concept.md` §9, kept)

The whole point of Kumbu²'s encryption is that only a credential opens a vault key. A connection is a credential like an API key:

- At **Allow**, the user is signed in, so the server holds their user key: it opens the chosen vaults' keys and **wraps them for the connection**, exactly as creating an API key does (same slot kinds, same per-vault right sealed into the slot). No API key is involved.
- The **refresh token carries the connection's slots** (vault keys wrapped under a key derived via HKDF from the refresh token).
- Each **access token gets ephemeral slots**: at issue time the server opens the vault keys with the refresh token (or, for the first one, from the consent step) and wraps them under the new access token too; those slots carry a MongoDB TTL equal to the access token's lifetime and vanish physically when it ends.
- **Lifetimes** (decided with Chris: an occasional "Reconnect" in Claude is acceptable): access token **1 hour** (configurable), refresh token valid until the connection's expiry or revocation, rotated on every use.
- **Rotation:** each refresh issues a new refresh token, wraps the connection's slots under it and deletes the old ones in one transaction. A **reused old refresh token** means theft: the whole connection is revoked — except within a short grace period (a few seconds) in which a retried refresh of the same token returns the same new pair, since clients retry on timeouts.
- **Revocation** (in Kumbu²'s list, via `/oauth/revoke`, by expiry, by a password change that revokes API keys, by a recovery, by deleting a vault — wherever an API key would be revoked, a connection is too): delete all slots of the connection in one transaction; the next request gets 401 and Claude shows "Reconnect".
- Tokens are opaque random strings with a recognisable prefix (e.g. `kumbu_at_…`, `kumbu_rt_…`); only their hashes are stored and compared in constant time.
- Each request with an access token opens its slots afresh, as an API key does (no cache that would outlive a revocation).

The README and the security document say plainly: the refresh token lives at the client's provider (Anthropic, OpenAI, xAI…), so they hold key material to the chosen vaults — consistent with the threat model (they see what they read anyway), but stated.

## 6. In Kumbu²'s web app

- **Connect an app** on the Keys page (which gets a second section **Connections**, or becomes "Access" with two sections — builder's choice): name, vaults and rights, expiry → the connector URL with copy button and instructions for Claude (and a generic line for other clients).
- **Consent page** (`/oauth/authorize` renders the React app's consent route; the authorisation request is kept server-side under a short-lived id so nothing sensitive is in the page URL beyond what the client sent): client name and host ("Claude · claude.ai"), the connection's name, vaults and rights, Allow / Deny. For a renewal the same page, saying "renew". CSRF protection; the decision is bound to the signed-in session and the pending request.
- Not signed in → the existing sign-in with `next` back to the consent route (the existing safe-next rules).
- **The list:** each connection with name, connector URL, client (once authorised), vaults and rights (editable: changing them re-wraps on the next consent or applies at once by re-wrapping with the signed-in user's key — builder's choice, but the list must never show rights the tokens don't have), created, last used, expiry, status (waiting for the app / connected / needs reconnect / revoked / expired), Revoke. Revoked and expired ones stay listed with their reason, folded away.
- Writes made through a connection carry its name as writer label.

## 7. Claude Code and local agents

Unchanged: `Authorization: Bearer kumbu_…` with an API key on `/mcp`. The authentication scheme accepts either an API key or an access token (a policy scheme by prefix), and both resolve to the same caller model (user, vaults, rights, writer label).

## 8. Security requirements

- PKCE S256 only; exact redirect URI matching; `state` and `iss` in the redirect; `resource` required and checked; codes single-use with short TTL.
- **Fetching client metadata documents is outbound HTTP to a URL an attacker chooses:** https only, no redirects to other hosts, resolve and refuse private, loopback and link-local addresses (SSRF), a small size limit and a short timeout, cache per URL (respect cache headers within bounds), and validate the document (its `client_id` equals its URL; redirect URIs https, or loopback http for native clients).
- Rate limits: DCR per client network; `/oauth/token` failures per client; the consent POST as other session actions.
- Nothing secret in logs: no tokens, codes, verifiers, metadata document bodies; log the flow with a request id (client id host, step, outcome) so a "token issued but never used" case can be followed from our side.
- Browser-side: the consent page holds no token; nothing in localStorage/sessionStorage; responses `no-store`.
- The connection's slots follow the existing rules (right sealed into the slot, AAD contexts like API keys); deleting a vault removes its connection slots and revokes a connection left with no vault (as T6 does for API keys).

## 9. Testing

- Server integration tests for the full flow: discovery (401 + challenge → resource metadata → AS metadata) → client via CIMD (a test metadata document served locally) and via DCR → authorize with PKCE (consent through the API) → token → `/mcp` call with the access token → refresh with rotation → reuse detection → revoke → 401. Rights per vault exactly as consented; writer label; expiry.
- Negative tests: plain PKCE, wrong verifier, wrong/missing resource, redirect URI mismatch, code reuse, expired code, SSRF targets for metadata URLs, oversized documents.
- MCP Inspector against a local instance (it supports DCR, PKCE, refresh, and CIMD via its `client.json`).
- Manual acceptance on the public instance: claude.ai web → Connected → read, edit, write; the same connector in Desktop and on the phone; revoke in Kumbu² → next call fails → Reconnect works. Claude Code with an API key unchanged.

## 10. Key rotation and re-keying (moved here from the overnight plan, T3)

Implements `docs/security-architecture.md` §9 item 1, after the OAuth tasks, so it covers connections too. Write a short design first (`docs/superpowers/specs/2026-10-26-rekeying-design.md`) from the code as built; defaults:

- **Stage 1, rotate the user key:** new user key; re-wrap the user's vault slots and the password slot (the password is at hand during a password change and a recovery), the recovery slot (a recovery replaces the recovery key anyway; on a password change the recovery key isn't at hand — decide: issue a new recovery key shown once, or rotate only on recovery and on request with the password — and justify), and session slots (only the current session can be re-wrapped; others end, which a password change does already).
- **Stage 2, re-key a vault:** lock the vault, new vault key, re-seal everything of the vault (notes, history, trash, moves, checkpoints and their texts, embeddings, sealed paths and names, the per-vault embedding provider choice), recompute keyed lookup hashes, re-wrap the slots of every user with access. **API key and connection slots can't be re-wrapped** (the server never holds the key or refresh token): re-keying revokes the API keys and connections with access to that vault, and the UI says so first; a connection can then be renewed through its URL (reconnect), which wraps the new vault key at consent. Vaults have a single owner today; if that changes, re-key only when the server can re-wrap for every member. One MongoDB transaction, crash-safe; unlocked-vault caches dropped; T6's tombstones respected.
- **Triggers:** a recovery (rotate the user key and re-key the user's vaults; API keys and connections are revoked there already), on request in Settings ("Re-encrypt my vaults", with the password), per vault in its menu (with the password).
- Tests per stage, crash points, revoked access afterwards, an old credential plus an old backup no longer opens data written afterwards. Update `docs/security-architecture.md` (§3 "No key rotation", §6, §9, known gaps).

## 11. Left to the builder

- **Library or own code** for the authorisation server: `docs/concept.md` §9 suggested evaluating OpenIddict. Criteria: CIMD support (or a clean extension point for it), opaque tokens whose issuance and refresh can run Kumbu²'s slot wrapping in the same transaction, MongoDB storage, no Entity Framework. If OpenIddict doesn't meet them cleanly, implement the small set of endpoints in §4 directly, strictly by the RFCs, with the tests in §9. Record the choice and why.
- Storage layout (collections for clients, pending authorisations, codes, connections, token hashes; TTL indexes), exact endpoint paths under `/oauth/`, the page/section naming, the grace period length, the DCR expiry.
- The SDK version: already `ModelContextProtocol.AspNetCore` 2.3.0; claude.ai speaks protocol 2026-07-28 — test both handshakes.

## 12. Sources (from the research of 2026-10-25)

MCP specification, Authorization, revision 2026-07-28 (modelcontextprotocol.io); Anthropic: custom connectors on claude.ai and the remote MCP server guide (support.claude.com, docs.claude.com); Claude's client metadata document at `https://claude.ai/oauth/mcp-oauth-client-metadata` (fetched); modelcontextprotocol/csharp-sdk v2.3.0 (2026-10-20); MCP Inspector v2.12.0; known issues on refresh (claude-ai-mcp #228, open as of 2026-10-21) and CIMD (#1110, #1125). Details and links: the research notes, to be committed alongside as `docs/notes-oauth-research.md`.
