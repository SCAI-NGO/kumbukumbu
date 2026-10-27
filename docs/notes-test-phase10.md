# Acceptance checklist: phase 10 (OAuth connections and re-keying)

Branch `step-10-oauth`. For Chris, on `https://kumbu.tidesson.net` after deploying this PR's build through Komodo. Each item has a checkbox and an empty "Result:" line to fill in. The flow itself is drawn in `docs/security-architecture.md`, §6 "Connections (OAuth)".

## Demo path for Tuesday

On the phone, about five minutes. Do items 2 and 5 once beforehand, so the connector is already there.

Don't run **Re-encrypt my vaults** right before the demo: it disconnects every app, and the connector would need a reconnect first.

- [ ] 1. In Kumbu² (phone browser): Access → **Connect an app**, name `demo`, one vault read and write, 90 days. Show the connector URL and **Copy**.
- [ ] 2. Beforehand, in claude.ai on the web: Settings → Connectors → **Add custom connector**, paste the URL, leave client ID and secret empty. It then appears in Claude Desktop and in the Claude app on the phone.
- [ ] 3. Kumbu²'s consent page opens: point at the app name, the vault and "read and write". **Allow**.
- [ ] 4. Back in Claude, ask "list my Kumbu vaults", then "write a note `demo` with three bullet points".
- [ ] 5. In Kumbu²: the note is there, the history shows the writer `demo`, and the Access page shows "Connected" and "last used just now".
- [ ] 6. **Revoke** the connection in Kumbu². Ask Claude to list the vaults again: it fails and offers Reconnect.
- [ ] 7. Optional: Settings → Vaults → **Re-encrypt** on a small vault, and show the dialog that lists what loses access.

## Checklist

1. - [ ] **Before.** The deployed image is this PR's build (`/healthz`, `serverInfo.version`). `curl -si https://kumbu.tidesson.net/.well-known/oauth-authorization-server` shows the fields of Task 3 (issuer, endpoints, `S256`, `client_id_metadata_document_supported`). `curl -si -X POST https://kumbu.tidesson.net/mcp/c/<id>` answers 401 with `resource_metadata`. `curl -si -X POST https://kumbu.tidesson.net/mcp` answers 401 with a bare `Bearer`.
   Result:

2. - [ ] **claude.ai web (work account).** Access → Connect an app "Work", vault `Work` read and write, 90 days; copy the URL. In claude.ai: Settings → Connectors → Add custom connector, URL only. Kumbu²'s consent shows "Claude (claude.ai)", `Work`, "read and write". Allow: the connector shows *Connected*. In a chat: list the vault, read a note, edit a section, write a new note. In Kumbu² the history shows the writer `Work`; the connection shows "Connected", "Claude · claude.ai", last used today.
   Result:

3. - [ ] **Deny.** With a second connection, Deny at the consent: claude.ai reports the denial, nothing is connected, the Access page still shows the connection as "Waiting for the app".
   Result:

4. - [ ] **Claude Desktop.** The same connector is there without adding it; read and write work. If a sign-in is asked for, it opens in the system browser.
   Result:

5. - [ ] **Phone (iOS and Android Claude app).** The connector is listed and a read works. If a sign-in is needed, the in-app browser shows Kumbu²'s sign-in first and then the same consent.
   Result:

6. - [ ] **A second Claude account (private).** Its own connection and URL see only its own vaults. Pasting the work URL there and signing in as the same Kumbu² user shows the work connection's consent (the URL decides, as designed). Signing in as another Kumbu² user shows "This connection belongs to another Kumbu² account", with nothing of the connection.
   Result:

7. - [ ] **Revoke and rights.** Revoke in Kumbu²: the next tool call in claude.ai fails and the connector shows Reconnect; Reconnect ends on "This connection was revoked" (final), so make a new connection. Separately: change another connection's rights (it shows "Use Reconnect in the app"), Reconnect in claude.ai: the consent says "Renew …", and it works with the new rights.
   Result:

8. - [ ] **After an hour** (access-token expiry): a tool call still works without user action (refresh). If not, note the time and check the server log for `OAuth refresh` lines (claude-ai-mcp #228). Also check that no `refresh_reused` shows on the connection after a few hours of use.
   Result:

9. - [ ] **The plain URL.** Paste `https://kumbu.tidesson.net/mcp` into claude.ai: Kumbu²'s page says the address isn't a connection and where to make one.
   Result:

10. - [ ] **Claude Code with the existing API key** on `/mcp` is unchanged (`claude mcp list`, a read and a write).
    Result:

11. - [ ] **MCP Inspector locally** (v2.12.0 or later) against a local instance (`KUMBU_PUBLIC_URL=http://localhost:8099`, `Kumbu__OAuth__AllowLoopbackClients=true`): `npx @modelcontextprotocol/inspector@2.12.0`, Streamable HTTP to `http://localhost:8099/mcp/c/<id>`, via proxy. "Quick OAuth Flow" with DCR works. Then CIMD: `~/.mcp-inspector/storage/client.json` with `{ "cimd": { "enabled": true, "clientMetadataUrl": "http://127.0.0.1:8765/client.json" } }` and a document served by `python3 -m http.server 8765` (redirect `http://localhost:6274/oauth/callback`). Tools list and a call work. Revoke in Kumbu²: the Inspector gets 401 `invalid_token`.
    Result:

12. - [ ] **Re-keying.**
    - Settings → **Re-encrypt my vaults**: the answer is immediate and shows the new recovery key once. All API keys and app tokens are revoked at once; the claude.ai connector needs Reconnect (the connection says "a vault was re-encrypted") and works after it; Claude Code's old API key no longer works (expected; make a new one).
    - The vaults are then re-encrypted one by one in the background; each shows "Being re-encrypted" in Settings → Vaults until it is done. While one does, a new key or connection for it answers 503, and a consent opened for it answers 503 and works again with Allow once it is done.
    - Per vault: Settings → Vaults → **Re-encrypt** on one vault opens a dialog that first lists the API keys and connected apps that will lose access (or says none has), then asks for the password; **Re-encrypt vault** ends with "… is re-encrypted. N API keys were revoked and N apps were disconnected." Notes, history and search still work in that vault afterwards, and only the apps and keys that had it need setting up again.
    Result:

13. - [ ] **Server log after all this.** No token, code or verifier anywhere: `docker compose logs kumbu | grep -E 'kumbu_(at|rt|ac)_'` finds nothing.
    Result:

14. - [ ] **Menu names in the README.** On the production host, check that the claude.ai menu path the README gives ("Settings → Connectors → Add custom connector", and "Reconnect" on the connector) still matches the live product, in the browser and in the apps. Fix the README if it does not.
    Result:

15. - [ ] **Optional: MongoDB 8 under a small cache.** On the production `mongo:8`, re-key a large vault (Settings → Vaults → Re-encrypt) and watch the mongod log (`docker compose logs mongo`) for "rolled back for eviction" or `WriteConflict`. The small-cache measurement was only made on 7.0.14.
    Result:
