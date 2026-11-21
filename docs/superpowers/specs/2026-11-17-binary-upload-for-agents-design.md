# Binary upload for agents — design

Status: approved by Chris on 2026-11-17, for 0.6.0. Idea note: `/SWE/Kumbu/Ideas/Binary upload for agents 2026-11-17.md` (worked out together with a user of Kumbu² and their agent).

## Why

Agents can write Markdown into Kumbu² but can't put a file there. An agent that makes pictures (Grok Imagine, say) ends every note with "upload the photos yourself in the web app or Obsidian". Base64 through a tool argument works but is the last resort: it costs tokens, large files don't fit, and the bytes can be corrupted on their way through the model.

**Control channel MCP, data channel HTTP:** the agent asks over MCP for a short-lived upload URL and sends the bytes there with `curl`; the bytes never pass through the model.

## Goals

- An agent with a shell and network access (Claude Code, Claude in the cloud, Grok's sandbox) puts an image or PDF into a vault in two steps: one MCP call, one `curl`.
- The result tells the agent what to write into the note, so it doesn't have to build the link itself.
- The upload is the agent's write: same rights, same checks, same history entry as any other write of its key or connection.

## Non-goals

- Agents without a shell or network (a plain chat client): a later step.
- Base64 upload as a feature. It stays the emergency fallback it is today.
- Uploads surviving a server restart (see "Approaches").

## Anti-goals

- A URL that works more than once, for more than one path, or after its key or connection was revoked.
- A new way to open a vault that weakens what `docs/security-architecture.md` promises (every request checks its credential afresh).

## Approaches

| | How | Trade-off |
|---|---|---|
| A. Ride on the open vault | The `PUT` uses the vault while it happens to be unlocked in the working set (idle timeout 15 minutes, `KumbuOptions.cs:253`). | Smallest, but breaks the promise that every request opens its slots afresh, and fails at random once the vault was locked. |
| **B. A ticket in memory (chosen)** | The MCP call that asks for the URL carries its credential anyway; the ticket holds that credential in the process's memory for its lifetime, keyed by the token, and the `PUT` runs as that credential. | As small as A, honest. Lost on a restart. |
| C. A slot in MongoDB with a TTL | Like the OAuth code's slot (`slot:{vaultId}:connection:…`, 60 s). | Survives a restart; more crypto code and a new slot kind for a token that lives minutes. |

**Decided by Chris: B**, because a capable model simply asks again when an upload fails, at worst the user sees one failed upload, and restarts are rare: surviving one is not worth C's extra code ("kein Overengineering nötig"). There is precedent: an open pairing code holds a copy of its user's key in memory for at most eleven minutes (`Accounts/Pairings.cs`).

## The design

### MCP tool `upload`

- Arguments: `path` (absolute, the attachment's path including its vault, as `write` takes it) and optional `version` (the attachment's version token from `cat`, to overwrite an existing file; left out, the path must be free, as the existing `PUT` behaves).
- Checks at once, before a ticket exists, what can be checked without the bytes: the path is valid and of an allowed attachment format (by its extension), the caller may write there (read-only keys and connections are refused here, not later), the path is free or `version` matches the current one, the server keeps attachments.
- Answers, as text the agent can act on:
  - the upload URL, `<origin>/api/agent-upload/<token>`
  - the method (`PUT`) and that the body is the raw bytes
  - a ready `curl` line: `curl -sS -X PUT --data-binary @<local file> '<url>'`
  - when it expires (UTC) and that it works once
  - the limits: 15 MB, the formats Kumbu² keeps, the format is checked from the bytes.
- The tool's own description is enough for an agent that never reads the skill: what it is for (files, not notes), the two steps, that it needs a shell with network access to the Kumbu² host, and what to do when the `PUT` fails (ask for a new URL).
- Origin: `KUMBU_PUBLIC_URL`, else the request's origin, as OAuth does (`OAuth/OAuthUrls.cs`).

### The ticket

- **Token:** 32 random bytes from `RandomNumberGenerator`, Base62 or Base32 in the URL (builder's choice; URL-safe without escaping). Held only in memory, found by a hash of the token, never written to MongoDB or a log.
- **Holds:** the bearer credential the `upload` call came with (the API key, the connection's access token, or the dev token), the one path and the optional `version`. Nothing else: no vault key, no `UnlockedVault`, no rights.
- **Why the credential and not the opened vault** (Claude's call after the builder check, within Chris's B): an opened `VaultAccess` holds the shared `UnlockedVault`, which the sweep may drop and reload or a re-keying may replace within the ticket's minutes (`Vaults/VaultCatalog.cs:1111`), and a key's right is sealed in its slot, so only the credential itself can check it again. Holding the credential, the `PUT` is resolved exactly as a request with that bearer header: slots opened afresh, revocation, expiry, user status, membership and the current right all checked by the code that checks them today, the vault loaded as for any request. The price: a bearer secret in memory, usable for 10 minutes, its copy held at most 11 (a sweep every minute), as a pairing code holds a user key for eleven; it never leaves the process, and the upload URL only ever does one `PUT` at one path with it.
- **Lifetime: 10 minutes**; a credential that expires sooner (an access token) is refused by the PUT's own resolution (found in the final review: the access token's expiry isn't at hand where the tool runs). Long enough for an agent to save a generated image and run `curl`; short enough that a URL left in a log or a proxy is dead by the time anyone reads it.
- **Once:** the `PUT` takes the ticket out of memory before it reads the body. A failed upload (wrong format, too large, conflict) has used the ticket; the agent asks for a new URL. Why: one rule, no retry state to reason about.
- **Dropped** when it expires (a sweep, or on the next lookup), when it is used, when the process ends, and the key material zeroed then, as a pairing code's copy is.
- **Bounded:** at most a small number of open tickets per credential (builder's call, e.g. 20; the oldest goes). Why: a looping agent must not fill the server's memory.

### `PUT /api/agent-upload/{token}`

Not under `/api/upload/`: that is the web app's Markdown upload (`POST /api/upload/{vault}/{**folder}`, `Api/UploadApi.cs`), and a vault may be called anything, so no prefix shared with it (found while planning).

- **No other credential is read:** no cookie, no bearer key. The token alone authorises. CORS: none (agents call it from a shell). Rate-limited like wrong bearer keys: an unknown token counts against the wrong-key limit.
- **Before the body:** `CallerMiddleware` recognises the route, takes the ticket out of the store (unknown or expired: `401`), and resolves the caller from the ticket's credential through the same path as a bearer header, so `RequestCaller` is built as today and no new credential kind is needed. A credential that no longer resolves (revoked, expired, user inactive, vault re-keyed) answers `403`; a right below write on the ticket's vault, or a membership ended, the usual refusal. Every refusal says to ask for a new URL; so does a `503` while the vault is busy, since the ticket is already spent (found in the final review).
- **Then the existing write path:** the body is handled exactly as `PUT /api/files/{vault}/{**path}` handles it (`Api/FileApi.cs:142`): `ReceivedBody`, the 15 MB limit, `AttachmentFormat.Problem` against the bytes, `ImageHeader.Read`, the storage quota, the history entry, the `version` from the ticket. Factor that body out of `WriteAsync` so both routes run the same code; don't duplicate it.
- **The writer** is the key's or connection's label and reference, as if it had written the file itself (`WriterIn`, `ReferenceIn` of the caller resolved from the ticket). The web app's agent highlighting follows from that.
- **Connections:** the ticket holds the access token, not the refresh token. If an OAuth refresh within the 10 minutes ends that access token, the ticket ends with it and the agent asks again: the same accepted price as a restart (Chris's reason for B). The builder checks which way `OAuth/OAuthTokens.cs` behaves on a rotation and the test pins it; no extra code to keep a ticket alive across a refresh.
- **Answers** `201` (new) or `200` (overwritten) with `FileStateView` plus:
  - `markdown`: what to put into the note, `![name](/path/inside/vault.png)` for an image, `[name](/path/inside/vault.pdf)` otherwise, the path inside the vault without the vault's name, `%20` for spaces (the link form `Manual.cs` teaches);
  - `link`: the file's deep link (`docs/superpowers/specs/2026-11-17-note-deep-links-design.md`).
- Errors as the files API's: `400` format or cut-off body, `409` saying only that the file exists or changed (changed 2026-11-21: the URL's holder may have no account and is to learn nothing of the file there, not even its version; the agent reads it again with `cat`), a `404` naming no vault for the same reason, `413`, `507` quota.
- HTTPS only outside `Development`, as every credentialed route.

### System skill `kumbu-binary-upload`

In `src/Kumbu.Core/Skills/System/kumbu-binary-upload/SKILL.md`, beside `ingest-attachments`. It carries the flow: when to use it (a file the agent has or made: generated images, screenshots, PDFs), `upload` then `curl`, what to do with the answer (`markdown` into the note with `edit`), what to do on failure (a new URL; if the host can't be reached, the sandbox's allowlist, below; base64 is not the answer). Listed in the handshake instructions like the other system skills.

### Agent sandboxes: documented, not solved

A sandbox that only reaches package registries can't reach the Kumbu² host. Decided by Chris: the user allows the host in their client, which Claude and Grok both offer; Kumbu² documents how. Part of the deliverable: a README section per client (Claude Code on the web / claude.ai, Claude Code locally, Grok), each with where the setting is and that only the Kumbu² host is needed.

### Security architecture

`docs/security-architecture.md` gains the ticket: in §4's table (a stolen upload URL: one file at one path, once, within 10 minutes, as its credential; nothing else), in "How long plaintext lives in memory" / "Keys in memory" (a ticket holds its bearer credential for at most 10 minutes, zeroed when it goes), and in §6 beside pairing codes.

## Tests (first)

xUnit, server:
- `upload` refuses a read-only key, an invalid path, a path of a format Kumbu² doesn't keep, an existing file without `version`, a wrong `version`.
- A ticket works once: the second `PUT` with the same token is `401`.
- An expired ticket is `401` (with the clock moved).
- A revoked key, a revoked connection, an ended membership, a lowered right, a re-keyed vault between `upload` and `PUT`: refused, nothing stored.
- A vault locked by the sweep between `upload` and `PUT`: the upload still works (loaded as for any request).
- A connection's ticket after an OAuth refresh in between: pinned as built (works, or refused with "ask for a new URL").
- The `PUT` ignores a cookie and a bearer header; the token alone counts.
- The stored file carries the key's writer label in its history.
- Wrong bytes for the extension: `400`, and the ticket is used.
- The answer's `markdown` and `link` for an image and for a PDF, a path with spaces.
- The per-credential cap drops the oldest ticket.

## Left to the builder

The token's encoding, the cap's number, where the ticket store lives (a singleton beside `Pairings`), the exact wording of the tool's description and of the skill, within the rules above.

## Changelog

New: **Agents can upload files** — an agent with a shell asks Kumbu² for a one-time upload link and sends an image or PDF there with `curl`, without the bytes passing through the model; the README says how to let your agent's sandbox reach your server.
