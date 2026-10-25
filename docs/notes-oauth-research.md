# OAuth for Kumbu² as a claude.ai custom connector — research notes

Researched 2026-10-25. Every claim carries a source. "Docs" = official Anthropic / MCP documentation; "issue" = GitHub reports (dated, not authoritative).

Key sources (fetched 2026-10-25):

- [S1] MCP spec, Authorization, **revision 2026-07-28** (current `latest`): https://modelcontextprotocol.io/specification/latest/basic/authorization
- [S2] …/2026-07-28/basic/authorization/authorization-server-discovery
- [S3] …/2026-07-28/basic/authorization/client-registration
- [S4] …/2026-07-28/basic/authorization/security-considerations
- [A1] Anthropic, "Authentication for connectors": https://claude.com/docs/connectors/building/authentication (no date shown; links to spec 2025-11-25)
- [A2] Anthropic, "Lazy authentication": https://claude.com/docs/connectors/building/lazy-authentication
- [A3] Anthropic, "Troubleshoot your connector": https://claude.com/docs/connectors/building/troubleshooting
- [A4] Anthropic, "Add a connector that isn't in the directory": https://claude.com/docs/connectors/custom/add-unlisted
- [A5] Anthropic, "Get started with connectors": https://claude.com/docs/connectors/getting-started
- [A6] Anthropic, "Test your connector": https://claude.com/docs/connectors/building/testing
- [A7] Support article 11175166 (shown as "updated over 3 weeks ago"): https://support.claude.com/en/articles/11175166-get-started-with-custom-connectors-using-remote-mcp
- [A8] Claude Code MCP docs: https://code.claude.com/docs/en/mcp
- [C1] Claude's hosted CIMD, fetched live: https://claude.ai/oauth/mcp-oauth-client-metadata
- [C2] Claude Code's CIMD, fetched live: https://claude.ai/oauth/claude-code-client-metadata
- [SDK] modelcontextprotocol/csharp-sdk **v2.3.0 (released 2026-10-20)**, source at tag v2.3.0
- [INS] modelcontextprotocol/inspector **v2.12.0 (released 2026-10-21)**, `docs/test-servers.md`, `docs/mcp-server-configuration.md`, `docs/environment-variables.md`

---

## 1. MCP authorisation spec (revision 2026-07-28)

**Base standards** [S1]: OAuth 2.1 (draft-13), RFC 6750, RFC 8414, RFC 7591, RFC 8707, RFC 9728, **RFC 9207 (new: issuer identification)**, CIMD draft-00, OIDC Discovery.

**Protected Resource Metadata (RFC 9728)** [S1, S2]
- MCP servers MUST implement PRM; the document MUST contain `authorization_servers` with at least one entry.
- The server MUST offer at least one of: `WWW-Authenticate: Bearer resource_metadata="…"` on 401, or a well-known URI. Clients try the header first, then `/.well-known/oauth-protected-resource/<mcp-path>`, then `/.well-known/oauth-protected-resource`.
- Servers SHOULD include `scope="…"` in the 401 challenge. Client scope selection: (1) the challenge's `scope`; (2) otherwise all of PRM `scopes_supported`; (3) otherwise no scope parameter. `scopes_supported` should be the *minimal* set for basic functionality.
- Example: `WWW-Authenticate: Bearer resource_metadata="https://mcp.example.com/.well-known/oauth-protected-resource", scope="files:read"`.

**AS metadata: RFC 8414 vs OIDC** [S1, S2]
- The AS MUST provide **at least one** of RFC 8414 or OIDC Discovery; clients MUST support both.
- For an issuer without a path, clients try `/.well-known/oauth-authorization-server` and then `/.well-known/openid-configuration`.
- The `issuer` in the document MUST be identical to the issuer used to build the URL, otherwise it is rejected.
- PKCE: if `code_challenge_methods_supported` is absent, clients MUST refuse to proceed. Clients MUST use `S256` [S4].
- RFC 9207: the AS SHOULD return `iss` in authorisation responses (errors included), and if it does it MUST advertise `authorization_response_iss_parameter_supported: true`. The spec expects to raise this to MUST in a future revision [S1].

**Client registration: CIMD preferred, DCR deprecated** [S1, S3]
- AS and clients **SHOULD support CIMD**. DCR is **MAY** and explicitly **"deprecated … retained for backwards compatibility"** (2026-07-28).
- Client priority order: pre-registered, then CIMD (if the AS advertises `client_id_metadata_document_supported: true`), then DCR (if `registration_endpoint` exists), then ask the user.
- The AS SHOULD fetch the URL-shaped `client_id`. It MUST check that the document's `client_id` matches the URL exactly. It MUST validate `redirect_uri` against the document's `redirect_uris`. It MUST check that the document is JSON with the required fields (`client_id`, `client_name`, `redirect_uris`). It SHOULD cache per HTTP cache headers and SHOULD guard against SSRF. It MAY apply domain trust policies (allowlists) [S3, S4].
- On the consent screen the AS MUST clearly display the redirect URI hostname, and SHOULD warn when the client has only localhost redirects [S4].
- **What claude.ai actually uses: both** ([A1], see §2). Claude uses CIMD when it is advertised correctly and otherwise falls back to DCR.

**`resource` (RFC 8707) and audience** [S1, S4]
- Clients MUST send `resource` in **both** the authorisation and token requests. The value is the canonical server URI, e.g. `https://mcp.example.com/mcp`. Implementations SHOULD use the form without a trailing slash and SHOULD accept an uppercase scheme or host.
- Servers MUST validate that the token was issued for them (audience), MUST reject other tokens and MUST NOT pass tokens through. Invalid or expired tokens MUST get a 401.
- Tokens go only in the `Authorization: Bearer` header, on every request. Query strings are forbidden.

**Step-up** [S1]
- Insufficient scope: the server SHOULD reply `403` with `WWW-Authenticate: Bearer error="insufficient_scope", scope="…", resource_metadata="…"`. It SHOULD name all scopes the operation needs in a single challenge, and MUST honour scope hierarchies.
- Clients take the union of the previously requested scopes and the challenged scopes, and limit retries.

**Token lifetime and refresh** [S1, S4]
- The AS SHOULD issue **short-lived access tokens**, but the spec gives no number.
- For public clients the AS **MUST rotate refresh tokens** (OAuth 2.1 §4.3.1).
- Clients MUST NOT assume a refresh token will be issued. They SHOULD declare `refresh_token` in `grant_types` and MAY add `offline_access` when the **AS** metadata lists it.
- Servers SHOULD NOT put `offline_access` in the PRM `scopes_supported` or in the challenge.
- All AS endpoints MUST be HTTPS. Redirect URIs MUST be localhost or HTTPS. The AS MUST match redirect URIs exactly [S4].

## 2. claude.ai custom connectors today

**Redirect URI** [A1]
- Web, Desktop, mobile and Cowork all use exactly one redirect URI: **`https://claude.ai/api/mcp/auth_callback`**.
- No separate claude.com, Desktop or mobile URI is documented. A `claude.com` variant appears only in third-party guides and is unverified.
- Claude Code instead uses a loopback redirect on an ephemeral port. Its CIMD [C2] declares `http://localhost/callback` and `http://127.0.0.1/callback`, so the AS must match both with the port ignored [A1, A2].

**Claude's CIMD for the hosted apps** ([C1], fetched 2026-10-25):

```json
{"client_id":"https://claude.ai/oauth/mcp-oauth-client-metadata","client_name":"Claude","client_uri":"https://claude.ai",
 "redirect_uris":["https://claude.ai/api/mcp/auth_callback"],
 "grant_types":["authorization_code","refresh_token","urn:ietf:params:oauth:grant-type:jwt-bearer"],
 "response_types":["code"],"token_endpoint_auth_method":"none"}
```

Claude Code's [C2]: `client_id` `https://claude.ai/oauth/claude-code-client-metadata`, `client_name` "Claude Code", loopback redirects, `token_endpoint_auth_method: none`.

**Note:** the `jwt-bearer` grant in [C1] made OCI IAM reject the document (issue anthropics/claude-ai-mcp#1148, opened 2026-10-15, still open). Our AS must ignore grant types it does not know.

**Registration selection** [A1, A4]
- Claude selects CIMD **only if** the AS metadata contains `"client_id_metadata_document_supported": true` **and** `"none"` in `token_endpoint_auth_methods_supported`. Otherwise it falls back to DCR (`registration_endpoint`).
- In the "Add custom connector" dialog the user can choose "Use Claude's published identity" (CIMD, recommended), "Register automatically" (DCR) or "Use your own OAuth client".
- If the client secret is left blank, Claude acts as a public client. If the client ID is left blank, the fallback order is Anthropic-held credentials, then CIMD, then DCR.
- DCR creates a new client on every fresh connection [A1].
- Authentication settings cannot be edited later. You have to remove the connector and add it again [A4].

**PKCE and scopes** [A1, A2]
- Every authorisation request carries `code_challenge_method=S256`. The AS must also advertise `code_challenge_methods_supported: ["S256"]`.
- Claude requests the `scope` from the 401 challenge, or else the PRM `scopes_supported`. It appends `offline_access` if the **AS** metadata lists it.

**Discovery quirks** [A1, A2, A3]
- **A 401 is required.** Claude ignores `WWW-Authenticate` on a 200, and a `200` with `isError: true` never triggers sign-in.
- A `403` triggers re-authorisation only with `error="insufficient_scope"`. Any other 403 is terminal.
- **PRM `resource` must equal the URL as the user entered it, path included.** Claude sends the canonical form as `resource`: lowercase scheme and host, no trailing slash, no default port. The server should accept the canonical value for `aud` [A3].
- **Only the first entry of `authorization_servers` is used.** There is no fallback to later entries.
- If PRM cannot be read, Claude falls back to treating the MCP origin as the AS. This is the cause of issue anthropics/claude-ai-mcp#900, "redirects to the MCP server's own domain" (Entra, opened 2026-08-20, closed not_planned 2026-10-18) [A3].
- A redirect (3xx) of the MCP URL to another host drops the `Authorization` header and the flow fails. Register the final URL [A3].
- Timeouts: discovery, registration and token endpoints get 10 s; refresh gets 30 s [A1].
- `/token` must accept `application/x-www-form-urlencoded` [A1].
- Discovery documents are cached globally for each URL for about **5 minutes** and served stale when a refresh fails [A2].
- Claude sends one token-less probe `POST /mcp` on purpose, to read the challenge. A `missing_bearer` entry in our logs is expected. This explains the reports of "/token 200 but no bearer". In issue anthropics/claude-ai-mcp#1094 (2026-10-01, closed 2026-10-11) the real cause was the reporter's firewall blocking the Claude bot with 403.
- `clientInfo.name` varies ("claude-ai", "Anthropic", "claude-code"). Never base authorisation on it [A6].

**Network** [A1, A3, A7]
- Requests come from Anthropic's cloud, **egress `160.79.104.0/21`**, and this applies to all surfaces including Desktop and mobile.
- The hostname must resolve to a public **IPv4** address (`A` record; AAAA-only does not work). Private, CGNAT, split-horizon and VPN-only addresses are rejected before any request is sent.
- WAFs and bot rules must allow the traffic.
- An overly strict `Origin` check breaks `initialize` [A6].
- For private networks Anthropic points to "MCP tunnels" [A4].

**Surfaces and plans** [A4, A5, A7]
- Custom connectors by URL work on **Free (exactly 1), Pro, Max, Team and Enterprise**.
- On Team and Enterprise only an Owner (or, on Enterprise, a member with the right custom role) can add one. Members then connect individually.
- A connector added on claude.ai is **available on web, Desktop and mobile**, and also inside Claude Code when it is signed in with a Claude account [A5].
- On Desktop the sign-in page opens in the system browser [A5].
- Mobile sign-in is evidenced only by issue anthropics/claude-ai-mcp#1125 (Safari on iOS, 2026-10-08). No document says whether mobile can *add* connectors; the old support article said it could not.

**Token refresh: documented versus reported**
- Docs [A1]: Claude refreshes **reactively on a 401** and **proactively up to 5 minutes before expiry**.
  - Return `invalid_grant` for dead refresh tokens.
  - Rotate refresh tokens for public clients, and return the new one in the same response.
- Open reports:
  - anthropics/claude-ai-mcp#228 ("token refresh never attempted for custom connectors via mcp-proxy.anthropic.com", opened 2026-04-24, **open**, latest comment 2026-10-21 reports drops exactly at access-token expiry). Some comments show the proxy *does* send `grant_type=refresh_token`, without `scope`, which Entra rejects.
  - anthropics/claude-ai-mcp#247 (Enterprise, opened 2026-05-01, open).
  - anthropics/claude-ai-mcp#1077 (`offline_access` not requested when the AS lists *only* `offline_access`, opened 2026-09-26, open).
  - anthropics/claude-code#65036 (opened 2026-06-03, open).
  - Most of the evidence involves Entra and other external IdPs. Nobody reports a clean failure with a simple self-built AS.
- **On a later 401**: Claude first tries to refresh. If that fails, the connector shows **"Reconnect"** and the user signs in again [A5]. During a conversation an inline **Connect** card appears and the tool call is retried after sign-in [A2]. There is no separate "re-authenticate" button; disconnect and reconnect is the only option (anthropics/claude-code#81020, closed not_planned 2026-10-09).
- **Other open CIMD reports:**
  - anthropics/claude-ai-mcp#1110 (opened 2026-10-05, open): OAuth via CIMD and `tools/list` succeed, but the connector stays on "Connect". The 2026-10-13 comment shows claude.ai speaking **protocol 2026-07-28 (`server/discover`)**, repeated every 2–5 s.
  - anthropics/claude-ai-mcp#1125 (opened 2026-10-09, open): Claude never calls `/authorize` after fetching the AS metadata.

**Consent screen** [A2, S4]: show the **host of the `client_id` URL** (`claude.ai`) as the requesting app, not the self-asserted `client_name` ("Claude"). Also show the redirect host `claude.ai` clearly.

## 3. Claude Desktop

- Desktop uses the **same claude.ai connectors**, brokered remotely through Anthropic's cloud. It does not connect locally [A1, A7].
- It uses the same redirect URI, `https://claude.ai/api/mcp/auth_callback`. No `claude://` or loopback redirect is documented for remote connectors [A1].
- Local servers in `claude_desktop_config.json` and desktop extensions (MCPB) are a separate local mechanism [A4, A7].
- Only Claude Code runs its own local OAuth, with a loopback redirect and its own CIMD [A1, A8].

## 4. ChatGPT (note only, out of scope)

- Redirect: per app `https://chatgpt.com/connector/oauth/{callback_id}`, shown in the app management page. Already-published apps keep the legacy `https://chatgpt.com/connector_platform_oauth_redirect`.
- ChatGPT supports **CIMD** (with a per-MCP-server CIMD URL as `client_id`) and **DCR** (once per connector instance).
- It requires PKCE S256 and `resource`, and uses RFC 9207 `iss` for stable redirects.
- Source: https://developers.openai.com/apps-sdk/build/auth (no date shown, fetched via search 2026-10-25). Not verified in depth.

## 5. MCP C# SDK (v2.3.0, 2026-10-20) — resource-server side only

**What exists** [SDK]:
- `ModelContextProtocol.AspNetCore` provides `AuthenticationBuilder.AddMcp(o => …)` (scheme `"McpAuth"`, `src/ModelContextProtocol.AspNetCore/Authentication/McpAuthenticationExtensions.cs`).
  - It **serves PRM**. By default the path mirrors the resource, so `/.well-known/oauth-protected-resource/mcp` yields `resource = https://host/mcp`. You can also set an explicit `ResourceMetadataUri` (`McpAuthenticationHandler.cs`).
  - `McpAuthenticationOptions.ResourceMetadata` is a `ProtectedResourceMetadata` (`resource`, `authorization_servers`, `scopes_supported`, `bearer_methods_supported`, …), with an `Events.OnResourceMetadataRequest` hook.
  - **Challenge**: it appends `WWW-Authenticate: Bearer resource_metadata="…"` and nothing more — **no `scope` and no `error=`** (`HandleChallengeAsync`).
  - **Authentication**: `HandleAuthenticateAsync` returns `NoResult`. `ForwardAuthenticate` defaults to `"Bearer"`, i.e. you plug in your own scheme (JwtBearer or a custom handler).
- Sample `samples/ProtectedMcpServer/Program.cs` uses `DefaultChallengeScheme = McpAuth` with JwtBearer (`ValidAudience` = server URL) and `app.MapMcp().RequireAuthorization()`. Stateless HTTP is the default in 2.x.
- `AddAuthorizationFilters()` allows `[Authorize]` / `[AllowAnonymous]` on tools, prompts and resources. **But** a denied call becomes an `McpProtocolException`, i.e. a **JSON-RPC error inside HTTP 200**, so it does **not** trigger the claude.ai sign-in (`AuthorizationFilterSetup.cs`, `docs/concepts/filters.md`).
- v2.0.0 (2026-07-28) aligned the SDK with spec 2026-07-28: stateless by default, `server/discover` with `initialize` fallback, client-side RFC 9207, strict S256 checks. v2.2.0 added hybrid stateful/stateless serving so 2025-11-25 and 2026-07-28 clients can share one endpoint.

**What does not exist**: no authorisation server (no `/authorize`, `/token`, AS metadata, CIMD fetching, consent or token store). `tests/ModelContextProtocol.TestOAuthServer` is a test fixture only. The client-side `ClientOAuthOptions` does support `ClientMetadataDocumentUri` (CIMD) and DCR, which is useful for our own integration tests.

## 6. MCP Inspector (v2.12.0, 2026-10-21)

- Supports interactive OAuth: discovery via `resource_metadata`, PKCE, DCR, pre-configured client ID, refresh, step-up (`onInsufficientScope`) and RFC 7009 revocation [INS mcp-server-configuration.md].
- **CIMD** is configured install-wide, not per server: `~/.mcp-inspector/storage/client.json` contains `cimd: { enabled: true, clientMetadataUrl }` (UI: Client settings).
  - The URL must be HTTPS, except for `localhost`, `127.0.0.1` and `[::1]` (supported since #2305/#2326, 2026-09-11).
  - Connection Info then shows "Client registration — Client ID Metadata (CIMD)" [INS test-servers.md].
- Redirects: web UI `http://localhost:6274/oauth/callback`; CLI/TUI default `http://127.0.0.1:6276/oauth/callback` (`MCP_OAUTH_CALLBACK_URL`) [INS environment-variables.md].
- The Inspector does not host its own CIMD. You publish the JSON document yourself, e.g. as a static file of the Kumbu dev server or on the loopback interface.
- The Inspector does **not** reproduce claude.ai behaviour: no redirect-header loss, no Anthropic egress, no CIMD of `claude.ai`. The only test against claude.ai itself is a real custom connector [A6].
- CLI for CI: `npx @modelcontextprotocol/inspector --cli <url> --transport http --method tools/list`, `--use-stored-auth`, `--stored-auth-only` [A6, INS cli-smoke-testing.md].

---

## Implications for the design

**Endpoints (single origin `https://<host>`, issuer = `https://<host>` without a path):**
1. `POST/GET /mcp`: without a valid token, an **HTTP 401** with `WWW-Authenticate: Bearer resource_metadata="https://<host>/.well-known/oauth-protected-resource/mcp", scope="kumbu"`.
   - Do the gate at endpoint level, `MapMcp().RequireAuthorization()`. **Do not** rely on per-tool `[Authorize]` alone (it yields 200 plus a JSON-RPC error).
   - The SDK challenge has no `scope`. Either accept that (Claude then takes `scopes_supported`) or write our own `IAuthenticationHandler.HandleChallengeAsync` / middleware that sets the header. Our own is a few lines; the SDK handler is still useful for serving PRM.
2. PRM at `/.well-known/oauth-protected-resource/mcp` **and** `/.well-known/oauth-protected-resource`: `resource: "https://<host>/mcp"` (exactly what the user types, no trailing slash), `authorization_servers: ["https://<host>"]` (one entry), `scopes_supported: ["kumbu"]` (no `offline_access`). Use `AddMcp` with an explicitly set `Resource`.
3. `/.well-known/oauth-authorization-server` (RFC 8414 is enough; OIDC is not needed). It must contain:
   - `issuer`, `authorization_endpoint`, `token_endpoint`
   - `response_types_supported: ["code"]`
   - `grant_types_supported: ["authorization_code","refresh_token"]`
   - `code_challenge_methods_supported: ["S256"]`
   - `token_endpoint_auth_methods_supported: ["none"]`
   - `client_id_metadata_document_supported: true`
   - `scopes_supported: ["kumbu","offline_access"]`. List a real scope alongside `offline_access`, never `offline_access` alone (#1077).
   - `authorization_response_iss_parameter_supported: true`
   - optionally `revocation_endpoint`
4. `/authorize`:
   - Allow only `response_type=code`, PKCE S256 and `state`.
   - Require `resource` and check it equals the canonical value. Compare scheme and host case-insensitively and ignore a trailing slash.
   - Check `redirect_uri` exactly against the CIMD document.
   - Then login and consent ("claude.ai requests access to Kumbu² — returns to claude.ai"), and redirect back with `code`, `state` and `iss`.
5. `/token` (form-urlencoded, fast):
   - `authorization_code` + `code_verifier` + `resource`. One-time codes with a short lifetime (≤ 60 s).
   - `refresh_token` with **rotation**. Errors use RFC 6749 codes (`invalid_grant`).

**Client registration:** **CIMD only, with an allowlist** (spec trust policy [S4]).
- Accept `client_id` values only from a configured list: by default `https://claude.ai/oauth/mcp-oauth-client-metadata`, and optionally `…/claude-code-client-metadata` plus a dev Inspector URL.
- This removes SSRF almost entirely and means no client table.
- Fetch the document, check that `client_id` equals the URL, cache it per HTTP headers and tolerate unknown `grant_types`. Optionally pin the content for the known Claude client as a fallback.
- **Skip DCR** (deprecated, and Claude uses CIMD once both flags are advertised). Keep a minimal DCR as a contingency only if the open CIMD issues (#1110, #1125) hit us. Then restrict it to the redirect `https://claude.ai/api/mcp/auth_callback`.

**Tokens (no JWT needed, because AS and RS share one process):**
- Opaque random tokens (≥ 256 bit), stored **hashed** in MongoDB. Each record holds `user`, `client_id`, `scope`, `resource` (= audience), `exp` and the refresh-token family.
- This gives trivial revocation and audience binding through the stored `resource`.
- Access token 1 h (configurable). Refresh token with a sliding lifetime (e.g. 30–90 days) and rotation.
- On reuse of an already-rotated refresh token, accept it within a short grace window (e.g. 30–60 s) and return the same successor. This covers timeouts and retries (the 30 s refresh timeout) without revoking the family; outside the window, revoke the family.
- Log every refresh attempt (client, result). That is how we find out whether claude.ai really refreshes for us (#228 is open).

**Claude Code** stays on the static API key. On `/mcp`, use an `AddPolicyScheme` that selects the API-key scheme or the OAuth token scheme depending on the token format. Both must be accepted.
- Note: claude.ai connectors also appear in Claude Code. If Claude Code has its own entry with the same URL, that entry wins and the duplicate is ignored [A8]. No conflict.

**Deployment:**
- Public HTTPS on the Hetzner VPS with an A record (IPv4).
- No redirect on `/mcp` (no apex→www, no HTTP→HTTPS for the registered URL itself).
- Reverse proxy, WAF or fail2ban must not block `160.79.104.0/21` or the "Claude-User" user agent.
- Do not set a strict `Origin` check on `/mcp`.

**Testing:**
- Locally: the Inspector with CIMD (our own `client-metadata.json` on loopback, entered in Inspector's `client.json`), plus xUnit tests against the AS using `ClientOAuthProvider` (SDK, `ClientMetadataDocumentUri`).
- End to end: a real custom connector on claude.ai (Free is enough for 1 connector), then a check on Desktop and the iOS/Android app.
- After changing discovery metadata, wait about 5 minutes (cache).

**Open risks**
- Refresh reliability on claude.ai is documented but still disputed (#228, #247, #65036 open as of 2026-10-21). Mitigation: observe through logs, keep the access-token lifetime configurable, and fall back to "Reconnect".
- Open CIMD issues (#1110, #1125): flow succeeds or stalls, but the connector state never settles. Not reproducible on our side; keep DCR as plan B.
- claude.ai speaks protocol 2026-07-28 (`server/discover`), so we need C# SDK ≥ 2.2 (hybrid) and must test both handshakes.
- The CIMD content can change (e.g. the `jwt-bearer` grant was added). Validate tolerantly; never pin the exact document.
- No documented separate redirect for mobile or claude.com. If Anthropic adds one, the CIMD `redirect_uris` will reflect it, which is another reason to validate against the fetched document and not against a hard-coded URI.
- Auth settings cannot be edited on the connector; changes mean remove and re-add, and the user has to reconnect.
- The login on the consent page (how a Kumbu user authenticates in the browser) is outside this research and needs a design decision.
