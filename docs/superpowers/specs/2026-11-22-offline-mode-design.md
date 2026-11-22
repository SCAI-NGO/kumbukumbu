# Offline mode for the web app (0.7.0) — Design

Date: 2026-11-22. Status: architecture approved by Chris on 2026-11-22; the UI of the mode (work package 0) gets its UX review before its part of the spec is written. Read against `master` of `kumbukumbu` (`7967a22`), `kumbu-obsidian` (`0b32b17`) and `kumbukumbu-security` (`584685f`).

**Decisions of 2026-11-22 (Chris):**
- 0.7.0 is this one feature, offline reading and writing; besides it only fixes and small UX improvements. Graph view stays in preview until at least 0.8.0 and link prediction waits, unless either runs alongside without hurting the goal (§9). Why: offline is the feature many users wait for, and the gateway to Eidolon in Chatsundere.
- The browser's data is treated exactly as an Obsidian device's: one sync behaviour, the existing sync API, conflict copies. Why: one behaviour to test and explain, and a much smaller project.
- Offline unlocking after a reload asks for the password, never a PIN. Why: a PIN of 4 to 6 digits is guessed in seconds against a copied disk.
- The client-side data layer is built as the **Eidolon core**, a package that can be taken out of Kumbu² later (§2a). Why: 1.0.0 extracts Eidolon as an LGPL library for Chatsundere's C2Duress, and a clean boundary now saves untangling it then.
- Development happens on the branch `release/0.7.0`; pull requests go there (§11).

Earlier thinking this builds on: `/SWE/Kumbu/Ideas/Offline mode 2026-11-13.md` (read-only first, discreet vaults never mirrored, search by meaning off while offline, a mode of its own that is always visible).

## 1. The short answer

**Yes: the browser can be "just another Obsidian device", and that shrinks the project a lot.** The server already speaks the sync protocol to web sessions, and the plugin's engine is pure TypeScript behind ports. What costs real work is not sync but three things around it:

1. **The web app is a thin client.** Links, backlinks, tags, search, calendar, board and graph are all computed on the server (`/api/links`, `/api/backlinks`, `/api/search`, `/api/tags`, `/api/graph/*` …). Offline, a small part of that has to run in the browser, and the rest is plainly marked "needs a connection".
2. **Plaintext in the browser.** Today the security architecture promises that the web app keeps no note content in browser storage and has no service worker (`docs/security-architecture.md`, "Lost or stolen laptop"). Offline breaks both promises, so the mirror needs its own encryption at rest and the document needs a new section.
3. **The app shell offline.** Without a service worker the app doesn't even load offline, and the update mechanism (`web/src/update/`) counts on there being none.

Embeddings are the smallest part: offline means words search in the browser, and what was written offline is embedded by the server when it is uploaded, as every write already is.

## 2. Can the web app reuse the Obsidian sync? (Chris's question)

### What already fits, unchanged

| Piece | Where | Works for a web session today? |
|---|---|---|
| Version list, full and `since` with 5-minute overlap, `ETag`/304, `damaged`, `attachments=1`, `text=1` | `src/Kumbu.Server/Api/SyncApi.cs:57`, `:130` | Yes: no caller restriction, it reads the caller's own unlocked vaults |
| WebSocket hints | `SyncApi.cs:59` ("API keys and web sessions"), `Sync/SyncHub.cs` | Yes: the web app already uses it (`web/src/hooks/useSyncSocket.ts:130`) |
| `PUT /api/notes` with `version`, `409` with the current text | existing | Yes: the editor uses it |
| `DELETE` with `version`, attachments `PUT/DELETE /api/files` with base | existing | Yes |
| Decision table, conflict copy with callout, first-sync merge | `kumbu-obsidian/src/engine/decide.ts`, `conflict.ts`, `firstSync.ts` | Pure functions, no Obsidian import |
| Engine (one queue per vault, check-before-write, `409` as information) | `kumbu-obsidian/src/engine/engine.ts` (1,072 lines) | Pure TS behind `RemoteApi`, `LocalFiles`, `StateStore`, `Clock` (`src/engine/ports.ts`) |
| HTTP client | `kumbu-obsidian/src/api/client.ts` | Takes an injected `HttpFn`; `fetch` with the cookie fits |
| Hints with backoff | `kumbu-obsidian/src/live/hints.ts` | Same protocol |

So the behaviour can be exactly the Obsidian one: per path `local`/`remote`/`base`, a real conflict becomes `<name> (conflict <device> <date> <time>).md` with the callout, Kumbu²'s version stays at the path. One behaviour, one set of race tests, one thing to explain.

### The gaps for a browser client

1. **Many vaults, not one.** A device key holds one vault; a session holds all of the user's vaults, shared ones included. The web host runs one engine per mirrored vault and keys the mirror by **vault id** (`VaultView.Id`, `src/Kumbu.Server/Api/NoteApi.cs:42`), never by name: names are per caller (a member's alias) and can be renamed while the device is offline. The version endpoint is addressed by name, so the host looks the name up from `GET /api/vaults` on every reconnect.
2. **A session, not a device key.** The browser must **not** pair itself and keep a device key: a long-lived bearer key in storage JavaScript can read is exactly what an XSS would take. The session cookie is `HttpOnly` and `SameSite=Strict`, and sync already accepts it. Consequences: a session ends (30 minutes idle, 12 hours; a remembered one 7 days idle, 30 in all, `Accounts/Sessions.cs:36`), so a week offline usually comes back to a sign-in. The engine's `AuthError` ("pair again" in the plugin) becomes "Sign in to upload your 4 changes"; local changes stay in the mirror until then. Web writes show as the user's own (`writerKind: user`), as today.
3. **The editor is the "file".** `LocalFiles.read` must return the editor's unsaved text, and `writeIf` must refuse while the editor holds a newer draft, as Obsidian's `vault.process` does. That is one adapter over the editor store (`web/src/state/editor.ts`) and the mirror.
4. **Renames offline.** The web app moves with `keepLinks: false` (`web/src/api/notes.ts:390`): the server rewrites links. The engine moves with `keepLinks: true`, because Obsidian rewrites links itself. A browser offline can't do either honestly without a local link rewriter. **Proposal: no move or rename while offline in 0.7.0**; create, edit and trash are enough.
5. **Rights.** A read-only member's mirror never queues a write (the editor is read-only offline too). A vault whose access is lost, or that becomes discreet, is purged on the next contact (§4.4).
6. **Attachments on demand.** The engine downloads every attachment under its limit and already knows "too large in Kumbu²" (`engine.ts:754`). It needs one more port, a fetch policy (`wantBytes(path, size)`), so the host decides which bytes to fetch (§6).
7. **Case.** Browser storage is case-sensitive: `caseInsensitive: false`, the simple case.

None of these needs a new server endpoint for notes. The server work is the mirror key slot and the wipe signals (§4).

### Where the engine lives

Today it is only in `kumbu-obsidian`. Two consumers want one copy. It moves into the Eidolon core (§2a), consumed by `web/` directly; the plugin takes its sync part through a vendoring script with a CI check that its copy matches a tagged commit. Why: the server and the engine's protocol change together in this repository, and the plugin's release cadence stays its own (it only changes version when its code changes). Publishing to npm would also work but is an outward step for a package that isn't public yet.

## 2a. The Eidolon core: built to be taken out

Everything the browser keeps and does with notes lives in one package, `packages/eidolon-core` in this repository (a pnpm workspace with `web/`), so that 1.0.0 can move it into its own repository as a library without untangling it from the web app.

**What it holds:**
- **Sync:** the engine, decision table, conflict copies and first sync from the plugin; the REST client and the hints over injected ports.
- **The sealed store:** records, the mirror key and its wraps, schema version and the carrying over of unsynced changes (§4, §5).
- **The local index:** link resolution by Obsidian's rules, backlinks, tags, words search.
- **The tools:** the operations over the mirror in the shape of the VAFS commands, `ls`, `cat` (with sections), `rg`, `links`, `backlinks`, `tags`, and `write`, `edit`, `replace`, `rm`. The offline UI uses them, and they are what Chatsundere's agent calls later. Same names and semantics as the server's `sh` and MCP tools, as far as a mirror can answer (no history, no `semsearch`).

**Boundary rules** (checked by a lint rule and the package's own build):
- No React, no import from `web/`, no Kumbu² UI or routing. The DOM only through ports: storage (IndexedDB behind an interface), crypto (WebCrypto behind an interface), HTTP, clock, the WebSocket.
- Kumbu²-specific things stay in `web/`: sign-in and the session, mode detection and its bar, which vaults to mirror, the editor adapter, the service worker.
- The protocol it speaks is the server's sync API as it is; a Kumbu²-only call (the mirror slot, §4.2) goes through a port the host fills, so another host can fill it differently.
- Its own Vitest suite, run in CI with the web app's; the engine's race tests move with it.

**Licence:** AGPL-3.0-or-later like the rest, until the extraction. Relicensing to LGPL then needs the copyright holder (Second Circuit) and no third-party AGPL or GPL code inside the package; dependencies are kept to permissive licences (MiniSearch is MIT) so the move stays possible.

## 3. Shape

```
Browser (one origin = one Kumbu² server)
┌───────────────────────────────────────────────────────────────┐
│ UI (React)                                                    │
│   online: react-query against /api, as today                  │
│   offline: OfflineSource (same hook shapes, a subset)         │
│                                                               │
│ Mirror host: one sync engine per mirrored vault (shared pkg)  │
│   RemoteApi → fetch + session cookie                          │
│   LocalFiles/StateStore → IndexedDB, AES-GCM sealed           │
│   local index in memory: link resolution, backlinks, tags,    │
│   words search (MiniSearch)                                   │
│                                                               │
│ Service worker: precached app shell only, never /api          │
└───────────────────────────────────────────────────────────────┘
```

**Online stays as it is.** The app reads from the server as today; the mirror follows in the background through the hints. Only offline does the UI read from the mirror. Why not local-first everywhere: it would rewrite the data layer of every screen at once and put the whole online app at risk for a release that should only add a mode. The cost is two read paths for the screens that work offline, kept small by giving `OfflineSource` the same shapes as the hooks it stands in for.

**The mode** (from the idea note, a must): detected automatically (failed requests plus the socket's state, `navigator.onLine` only as a hint), always visible (a bar and a mark in the sidebar), with "N changes waiting to upload" until the engine's queues are empty. The app stays in offline mode until the queues are flushed, so it never shows the server's older text right after reconnecting.

### What works offline in 0.7.0

| Works | Needs a connection (shown, not silently failing) |
|---|---|
| Vault tree, reading notes, rendering with wiki links resolved locally, embeds | Search by meaning, `semsearch`-style ranking |
| Words search across mirrored vaults | Console (`sh`), history, blame, diffs, checkpoints, restore |
| Backlinks and tags (from the local index) | Graph, calendar views, board |
| Editing, new notes, trashing | Move and rename, sharing, publishing, keys, admin, settings |
| Images and PDFs that are in the mirror | Attachments not fetched yet |

Calendar and board read-only offline are possible later from the same local index; left out to keep 0.7.0 tight.

## 4. Security

### 4.1 What is in the mirror, and sealed

Everything about notes is sealed: content, paths, vault names, attachment bytes, the engine's state (bases are keyed by path). In the clear only a header: schema version, the user id's hash, salts and wrapped keys. Why paths too: the discreet design already established that a name alone can give a person away.

The local index (links, tags, words search) is built in memory after unlocking and not stored in 0.7.0: a vault of 2,000 notes indexes in well under a second, so there is no reason to add a second sealed thing.

### 4.2 The mirror key

- A random **mirror key** (AES-GCM 256) per user and browser encrypts every record (a fresh nonce per record).
- **Wrapped twice:**
  - **Online:** by the server, in a new per-device **mirror slot** sealed under the user key (`POST/GET/DELETE /api/account/mirror-slots`, sessions only). A signed-in tab unwraps without asking.
  - **Offline:** under a key derived from the password, so the app opens offline after a reload. The web app has the password at every sign-in, so it re-wraps then, and a password change needs no special path.
- **KDF:** PBKDF2-SHA-256 from WebCrypto, about 1,000,000 iterations (tuned to ~1 s on a mid-range phone). Argon2id would be stronger against GPU guessing, but needs WebAssembly, and the CSP has no `'wasm-unsafe-eval'` (`src/Kumbu.Server/Api/Security.cs:59`); the app runs pdf.js without WebAssembly for the same reason. Opening the CSP is a security decision of its own, for the 0.7.0 audit.
- The key lives in memory only, per tab. A reload offline asks for the password; online the slot answers.
- **No PIN for offline unlocking:** 4 to 6 digits against a copied disk are guessed in seconds.

### 4.3 Discreet vaults

**Never mirrored**, revealed or not. This is the anti-goal of the discreet design ("a hidden discreet vault's name or content reaching the browser in any … storage") taken literally. A vault marked discreet on another device is purged here at the next contact.

### 4.4 Wipe and revocation

The server can only reach a device that comes online. On contact the client purges:

| Signal | Purges |
|---|---|
| Vault access lost (membership revoked, vault deleted) | That vault's mirror |
| Vault marked discreet | That vault's mirror |
| Mirror slot deleted ("Remove offline copy" in the device list), account deleted, "end other sessions" | The whole mirror |
| Account or vault re-encrypted | The affected mirrors (they are re-synced under a new mirror key) |

A purge also removes changes that haven't uploaded yet, so the device list says beforehand when a device had changes waiting (its slot records that at each contact). A device that never comes online again can't be wiped; the security document says so plainly, as it does for the Obsidian plugin.

**The kill switch** (backlog, not built) gets room here: a duress password at the offline unlock that destroys the mirror is a natural extension, decided in its own design.

### 4.5 What changes in the security architecture

- The "Lost or stolen laptop" row: the web app now keeps sealed note content in IndexedDB on devices where offline is on; what a finder can do (guess the password against PBKDF2 offline) and can't.
- "There is no service worker": now there is one, caching the built assets only; API answers stay `no-store` and are never cached.
- A new §6 entry "Offline mirror", and the 0.7.0 audit (Mythos, blue team) covers it, the service worker and the mirror slot endpoints.

## 5. Browser storage

- **IndexedDB only**, attachment bytes as Blobs in it. OPFS would be faster for big files, but one store is simpler, and on-demand fetching keeps the mirror small. OPFS stays an option if measurement says so.
- **`navigator.storage.persist()`** on turning offline on, `estimate()` shown in Settings, Offline. Chrome grants it to installed apps and engaged sites; Firefox asks the user; Safari evicts script-written storage of sites not added to the home screen after seven days without use, home-screen apps are exempt (behaviour as I know it, to verify on current Safari). So the install hint (`web/src/update/InstallHint.tsx`) gains a line for Safari users with offline on.
- **Eviction loses only unsynced changes**, everything else is re-synced. Hence: upload as soon as possible, and keep "N changes waiting" in sight.
- **Schema migrations:** the mirror is a rebuildable cache, so a new schema drops it and syncs again, **except** paths whose local version differs from their base (the unsynced changes), which a migration carries over. Only that part ever needs migration code.

## 6. Attachments on demand and "Keep offline"

- The version list always brings every attachment's path, kind, size and version (it already does with `attachments=1`).
- Bytes: images and PDFs up to 1 MB are fetched with the notes; larger ones when opened online, then kept in a budget (LRU, default 500 MB, in Settings).
- **Keep offline** per vault or folder (per device): fetches every attachment below it, whatever its size, up to the engine's 15 MB limit.
- **Which vaults are mirrored:** a per-device choice in Settings, Offline, per vault, with its size; "all my vaults" as one switch. Discreet vaults aren't listed there while hidden.

## 7. Embeddings

- **Offline: words search only**, in the browser (MiniSearch over the decrypted mirror, in memory). Decided already on 2026-11-13; it holds.
- **Offline writes need no embedding queue of their own:** the engine uploads them as ordinary writes, and the server's incremental indexing embeds them as it does every write.
- **TensorX from the client: not for 0.7.0.** Why:
  - Offline usually means no network at all, so TensorX is out of reach as well. It would only help while the Kumbu² server is down and the internet is up.
  - Searching by meaning in the browser also needs the vault's vectors there: about 4 KB per section at 1,024 floats, so 80 MB for 20,000 sections (20 MB as int8), all derived from the plaintext and to be sealed too.
  - Short-lived scoped tokens need TensorX to issue them; I found nothing that says it does. A server that mints them is a proxy again, which is down in exactly the case this is for.
  - BYOK in the browser would work (the key sealed under the mirror key, sent only from the client), if TensorX answers CORS preflights. **Unverified:** this sandbox's proxy doesn't reach `api.tensorx.ai`. One check from your machine settles it:
    ```sh
    curl -si -X OPTIONS https://api.tensorx.ai/v1/embeddings -H "Origin: https://example.org" \
      -H "Access-Control-Request-Method: POST" -H "Access-Control-Request-Headers: authorization,content-type" | head -20
    ```
  - Better fit later: Chatsundere's local embedding engine once Eidolon is a library (1.0.0).

## 8. Work packages

Sizes: S up to a day, M two to three days, L about a week, for one agent track including tests and review.

| # | Package | Size | Needs | Repo |
|---|---|---|---|---|
| 0 | UX review and spec: the offline mode's bar, Settings, Offline, the sign-in-again flow, "needs a connection" states | S | nothing | kumbukumbu |
| 1 | Eidolon core package: workspace, boundary lint, the engine moved in with its tests, the fetch-policy port and multi-vault host; the plugin vendors the sync part | M | nothing | both |
| 2 | Service worker for the app shell, tied into the update flow (new release: bar, reload, the worker updates with it) | M | 0 | kumbukumbu |
| 3 | Mirror slot endpoints and wipe signals on the server (codes on `401`/`403`, a socket frame), with isolation tests | M | 0 | kumbukumbu |
| 4 | Sealed mirror store: IndexedDB schema, mirror key, both wraps, PBKDF2, `LocalFiles` and `StateStore` adapters, persist and quota | L | 1, 3 | kumbukumbu |
| 5 | Offline reading: `OfflineSource` for tree and note, local link resolution (Obsidian's rules, a TS port of the server's), embeds, attachments from the mirror | L | 4 | kumbukumbu |
| 6 | Local index and tools: backlinks, tags, words search, the VAFS-shaped tools over the mirror (§2a) | M | 4 | kumbukumbu |
| 7 | Offline writing: editor as `LocalFiles`, create and trash, read-only members, "changes waiting", conflict copies shown | M | 4, 5 | kumbukumbu |
| 8 | Mode detection, the bar, switching back only after flushing | M | 0, 5 | kumbukumbu |
| 9 | Attachments on demand, Keep offline, vault choice | M | 4 | kumbukumbu |
| 10 | Playwright e2e with `context.setOffline`, the engine's race tests against the browser adapters, security architecture update, Mythos audit | M | all | kumbukumbu, security |

**Order:** 0, 1, 2 and 3 start at once. 4 is the narrow point. Then 5, 6, 7, 8 and 9 run in parallel, 10 throughout and at the end.

**Realistic time:** with parallel agent tracks the coding fits in about a week; your UX reviews (one by one, per CLAUDE.md), the hand-test list and the audit are the bottleneck, so I'd plan about two weeks from the start, not one.

## 9. Graph view and link prediction alongside

- **Graph view out of preview: realistic as a parallel track.** It sits on the finished graph backend (`/api/graph/*`) and lives in the note view's graph column and dialog (#274, #276), far from the data layer offline changes. It is "needs a connection" offline. Take the open fix "link graph memory" (L6-27, in the backlog) with it. Size: M. The cost is your review time, not code conflicts.
- **Link prediction: I'd leave it for 0.8.0.** It needs its own spec and UX review (suggestions, the feedback loop, the hub trap), a server part over embeddings that differs between internal and BYOK vaults, and its UI rides on the graph view leaving preview first. Running it as a third track would compete for exactly the bottleneck above. If you want a piece of it anyway: the server side as a `sh` command (`backlinks --predicted` or similar) is cleanly separable, S to M.

## 10. Risks

| Risk | Mitigation |
|---|---|
| Lost offline changes (Safari eviction, sign-out, purge) | Upload at once; "N changes waiting" always visible; warn before sign-out and in the device list before removing a copy |
| Races between the online UI and the mirror around reconnect | Stay in offline mode until the queues are empty; one engine per vault, as in the plugin |
| The service worker serving an old app forever | Precache by content hash, the worker updates with each release through the existing update bar; tested in e2e |
| A weak password guessed against a copied disk | PBKDF2 at ~1 s; stated in the security document; Argon2id as an audit question |
| Two copies of the engine drifting | One package, the plugin vendors a tagged copy with a CI check |
| Scope creep into "everything offline" | The table in §3 is the scope; anything else says "needs a connection" |

## 11. Branches until 0.6.0 is out

- **`release/0.7.0`**, cut from `master` on 2026-11-22, takes every pull request of this work, squash-merged as usual. CI runs on pull requests into it; the image build runs only for `master`, so it first runs on the final merge.
- **`master` stays 0.6.0's** until its release; its fixes go there as today. `master` is merged into `release/0.7.0` after each such fix (a merge, not a rebase), so the two never drift far apart.
- **At 0.6.0's release:** the tag goes on `master`, `maintenance/0.6` is cut from it (step 4 of Releasing), then `release/0.7.0` comes into `master` with one pull request, merged with a merge commit rather than squashed, so its pull requests stay readable in the history. Why not rename the branches: `master` keeps its protection rules, required checks and the image workflow's triggers, and nothing that points at it breaks.
