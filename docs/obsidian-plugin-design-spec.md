# Obsidian Sync Plugin — Design

**Date:** 2026-10-22
**Scope:** The Obsidian plugin that keeps a local Obsidian vault in sync with a Kumbu² vault, and the server API it needs. Builds on [concept.md](concept.md) §13 "Later (v2)", which chose a sync plugin over mounting.
**Status:** Draft by Claude after discussing questions a) to d) with Chris. Two decisions are open (§12); everything else is a proposal for review.

## 1. Intent

A person writes in Obsidian as they always do, on desktop or mobile, online or offline. Their notes are in Kumbu², where their agents read and write them. Changes on either side show up on the other within seconds while online, and after reconnecting otherwise. Nobody loses an edit, ever: a conflict produces two visible versions, never a silent overwrite.

At COR.energy this is a personal second brain per employee; sharing between people is out of scope (handled by another tool).

### Success criteria
- Edit a note in Obsidian → an agent can `cat` the new text a few seconds later.
- An agent `edit`s a note → it changes in the open Obsidian vault within seconds, without the user doing anything.
- Offline edits on a phone sync on the next start or resume.
- Simultaneous edits on two sides never lose either text.
- The API key never lands in a file inside the vault.
- The plugin can be installed with BRAT from its GitHub repo; later from the Obsidian community directory.

### Out of scope (v1)
- **Attachments** (images, PDFs, …): concept §4 keeps Kumbu² text only. They stay local; the plugin shows how many files it does not sync.
- `.obsidian/` and other dot folders: never synced (settings, workspace, plugin data).
- Sharing a vault between people; selective sync of subfolders.
- Automatic merging of conflicting edits (see §7 and §12).

## 2. Shape

```
Obsidian (desktop / mobile)                         Kumbu² server
┌─────────────────────────────┐                     ┌──────────────────────────────┐
│ kumbu-sync plugin           │  REST (Bearer key)  │ /api/v1/…  notes, changes    │
│  sync engine (pure TS)      │ ──────────────────▶ │ NoteWriter (tokens, history) │
│  local state (base tokens,  │                     │ per-vault sequence counter   │
│   cursor) in plugin data    │  WebSocket (ticket) │ /api/v1/ws  "vault X at N"   │
│  SecretStorage: API key     │ ◀────────────────── │                              │
└─────────────────────────────┘                     └──────────────────────────────┘
```

One Obsidian vault maps to one Kumbu² vault: the Obsidian vault root is `/<Vault>/` on the server.

## 3. Server API

A small REST API next to `/mcp`, sharing `NoteWriter` and its rules (path validation, version tokens, history, trash). The web UI (build order step 8) uses the same API, so it is written once and documented as an **OpenAPI** file in the Kumbu² repo; that file is the contract both clients build against.

Version tokens map onto HTTP `ETag` / `If-Match`.

| Method and path | Purpose |
|---|---|
| `GET /api/v1/vaults` | Vaults the key can see |
| `GET /api/v1/vaults/{vault}/notes` | All notes: `path`, `version`, `size`, plus the vault's current `seq` (for the first sync and full resyncs) |
| `GET /api/v1/vaults/{vault}/notes/{path}` | Content; `ETag: "<version>"` |
| `PUT /api/v1/vaults/{vault}/notes/{path}` | Create with `If-None-Match: *`, replace with `If-Match: "<version>"`. `412` on a stale version, body carries the current version |
| `DELETE /api/v1/vaults/{vault}/notes/{path}` | Move to the trash, `If-Match` required |
| `GET /api/v1/vaults/{vault}/changes?since=N&limit=500` | Change feed (§4) |
| `POST /api/v1/ws-ticket` | Short-lived ticket for the WebSocket (§5) |
| `GET /api/v1/ws?ticket=…` | WebSocket for change notifications |

`{path}` is the path inside the vault, each segment URL-encoded. Auth is `Authorization: Bearer <key>` like `/mcp`; until build order step 7 that is the dev token.

Plugin writes are stored **verbatim**: no link normalisation, no reformatting. Anything the server changed would come back to Obsidian as a remote edit and loop.

### Renames
`mv` arrives in step 4 and rewrites links pointing to the moved note. **Obsidian rewrites those links itself** when a user renames a file, and the plugin uploads them as ordinary edits. If the server rewrote them as well, both sides would edit the same notes. So a rename coming from the plugin calls `mv` with link rewriting **off**; agent renames through MCP keep it on (their link edits reach Obsidian through the feed). Until step 4 the plugin sends a rename as create plus delete.

## 4. Change feed

Every vault has a **sequence number** that grows with every change. Every note and every trash entry stores the `seq` of its last change.

- `changes?since=N` returns, per path, the newest change with `seq > N`: `{ seq, path, op: "upsert" | "delete", version }`, ordered by `seq`, plus the new cursor and `more: true` if `limit` cut it off. `delete` comes from the trash collection; a later restore or re-create at the same path wins by its higher `seq`.
- **No gaps:** a client must never see `seq` 6 before a still-running write with `seq` 5 has landed, or it would skip 5 forever. With one server instance, allocating the `seq` and the write run under a per-vault lock in the process, so they commit in order. Several instances later need a different mechanism (MongoDB transactions or change streams on a replica set); not needed now.
- **Retention:** delete markers live as long as the trash (30 days). The plugin does not rely on the server to detect a cursor that is too old: if its last successful sync is older than **25 days**, it does a full resync (§6) instead of asking the feed.
- The feed carries metadata only (paths, tokens). With encryption (step 5), serving it does not need to unlock the vault, so an open sync connection does not keep a vault unlocked against the idle TTL (§7 of the concept).

## 5. Notifications

The notification is only a **hint**: "vault Work is at seq 1235". The plugin then reads the feed. Lost or duplicate hints are harmless, and the plugin needs the same feed call on start and on resume anyway, because mobile systems drop connections in the background.

- **WebSocket** (preferred). The browser WebSocket API can't send an `Authorization` header, and the key must not appear in a URL (concept §9). So the plugin first calls `POST /api/v1/ws-ticket` with its key and gets a random ticket, valid for **30 seconds** and **one** connection, then opens `wss://…/api/v1/ws?ticket=…`. The ticket is worthless once used, so it does no harm in a proxy log.
- Messages: `{"type":"changed","vault":"Work","seq":1235}`; the server pings every 30 s. The plugin reconnects with exponential backoff (1 s up to 5 min, with jitter) and catches up through the feed after every reconnect.
- **Polling fallback:** if the WebSocket fails three times in a row (for example behind a strict proxy), the plugin polls the feed every **2 minutes** and keeps trying the WebSocket every 15 minutes. Polling is the same feed call, so it costs no extra code.
- Inside the server, `NoteWriter` raises an in-process event after each committed write; the WebSocket hub fans it out to the connections of that vault. Redis pub/sub only once there is more than one instance.

## 6. Sync engine

The engine is plain TypeScript without Obsidian imports, so it can be tested with Vitest against a fake server and a fake file system.

### Local state
Stored in the plugin's `data.json` (no secrets in it): the cursor (`seq`), the time of the last successful sync, and per path the **base version**, the version token both sides last agreed on.

The plugin computes tokens itself: `hex(SHA-256(content))[..12]`, the same as the server. **Before hashing and uploading, CRLF becomes LF**, as the server stores LF; otherwise a Windows file would never match its own upload and loop forever. Downloads are written with LF.

### Three-way decision per path
`local` = token of the file on disk (or none), `remote` = token on the server (or none), `base` = last agreed token (or none).

| local vs base | remote vs base | Action |
|---|---|---|
| same | same | nothing |
| changed | same | upload (`If-Match: base`; create with `If-None-Match: *` if there is no base) |
| same | changed | download (or delete locally, if remote is gone) |
| changed | changed, and local = remote | just record the new base |
| changed | changed | **conflict** (§7) |
| deleted locally | same | delete on the server (goes to the trash) |
| deleted locally | changed | conflict: the remote edit wins and comes back, nothing is lost |

A `412` on upload means the server changed in the meantime: fetch, decide again.

### Triggers
- Obsidian events `create`, `modify`, `delete`, `rename` on `.md` files outside dot folders, debounced per file (**2 s** after the last keystroke-driven save).
- Feed changes from §5.
- On start, on resume (mobile), and when the user presses "Sync now".
- **Echo suppression:** when the plugin writes a downloaded note, Obsidian fires `modify`. The local token then equals the new base, so the decision table says "nothing"; no special flag is needed.

### Full resync
Used on the first sync and when the cursor is older than 25 days: list all remote notes (`GET …/notes`), walk all local `.md` files, and run the decision table for every path. On the very first sync there is no base; a path present on both sides with different content is a conflict, never an overwrite.

### What is not synced, and why
Files the server would reject are skipped with a visible reason in the plugin's status view, never retried in a loop: not `.md`, larger than 1 MiB, names Windows can't store, dot folders. The status bar shows a small count ("3 not synced").

## 7. Conflicts

The decision is open (§12). The proposal for v1: **keep both, make a conflict copy**, like Dropbox and Obsidian Sync.

- The server version stays at the original path; the local text is saved next to it as `<name> (conflict <device> 2026-10-22 14-03).md` and uploaded as a normal new note, so agents see it too.
- A notice in Obsidian names both files. Resolving means editing one and deleting the other; nothing special.
- Device names are set in the plugin settings (default: platform plus a short random suffix).

Why not merging now: line-based three-way merge (`diff3`) works well for appends and edits in different places, but produces conflict markers or wrong results when the same paragraph changed, and a wrong merge is a silent loss. It can come later, on top of conflict copies (merge when clean, copy otherwise). The base text it would need is in the server's history already.

## 8. Authentication and the secret

As in concept §13:

- The key lives **only in Obsidian's `SecretStorage`**; plugin settings store the secret's name only.
- The plugin **refuses to run without it**: `minAppVersion` `1.11.4` in `manifest.json`, a check for `app.secretStorage` on load (if missing: a notice and no sync), and no plain-text key field anywhere.
- **One key per device**, so a lost phone is revoked by deleting its slot. Secret names include the vault, `kumbu-<vault>-<device>`, because on mobile secrets with the same name were reported to leak across vaults.
- Before release, check what `SecretStorage` actually does at rest on each platform (the docs only promise local storage keyed to the vault) and state it in the README.
- HTTP goes through Obsidian's `requestUrl` (no CORS issues, works on mobile). The WebSocket is the standard browser one; the server's CORS/origin check for `/api/v1/ws` allows Obsidian's origins (`app://obsidian.md`, `capacitor://localhost`, `http://localhost`).
- Later: an OAuth login (concept §9) instead of pasting a key.

## 9. Settings

Server URL (must be `https://` except for `localhost`), vault name (picked from `GET /api/v1/vaults`), secret (via `SecretComponent`), device name, "Sync now", and a status view: last sync, connection mode (WebSocket / polling / offline), files not synced with reasons, conflicts.

## 10. Repository and release

- **Own repo**, required by Obsidian (`manifest.json`, `README.md`, `LICENSE` at the root of the default branch): proposed `SCAI-NGO/kumbu-obsidian`.
- Plugin id `kumbu-sync`, name "Kumbu² Sync" (the id must not contain "obsidian").
- TypeScript (the Obsidian API ships as TypeScript types), esbuild, pnpm, Vitest; ESLint with Obsidian's recommended plugin rules, as the directory's automated review checks code quality.
- Releases from a GitHub Actions workflow on a tag: tag = `version` in `manifest.json`, format `x.y.z` without `v`; assets `main.js`, `manifest.json`, `styles.css`.
- Testing and first users: **BRAT** with the repo URL.
- Directory: sign in at community.obsidian.md, link GitHub, submit; automated review for quality, security and malware. The README states that the plugin needs network access and a Kumbu² server and account.

## 11. Order of work and parallelism

1. **Server, in the Kumbu² repo:** REST API with ETags, per-vault `seq` and change feed, ws ticket and WebSocket hub, OpenAPI file, tests. Needs step 3 (done).
2. Then **in parallel**: the plugin (own repo, against the OpenAPI file and a local `docker compose` server) and the web UI (step 8, same API).
3. Step 4 adds `mv` with optional link rewriting; the plugin switches renames from create plus delete to `mv` without rewriting.
4. Step 7 replaces the dev token with per-device keys; the plugin needs no change, as it already reads the key from `SecretStorage`.

Testing: the sync engine with Vitest (decision table, CRLF, echo suppression, conflict copies, full resync, 412 retry); end to end against the compose stack: edit in a test vault, see it over MCP, and the other way round.

## 12. Open decisions (Chris)

1. **Conflict strategy:** conflict copies (proposed, §7), or merge when clean plus copy otherwise from the start.
2. **Licence of the plugin repo:** GPL-3.0 (Chris's default for own open-source projects), AGPL-3.0 (same as the server), or MIT (common for Obsidian plugins).
