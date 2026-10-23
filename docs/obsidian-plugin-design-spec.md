# Obsidian Sync Plugin — Design

**Date:** 2026-10-22
**Scope:** The Obsidian plugin that keeps a local Obsidian vault in sync with a Kumbu² vault, and the server API it needs. Builds on [concept.md](concept.md) §13 "Obsidian sync" (build order step 11), which chose a sync plugin over mounting.
**Status:** Draft by Claude after discussing questions a) to d) with Chris. Conflict handling and licence are settled (§12); everything else is a proposal for review.

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
- Automatic merging of conflicting edits (decided against, §7).

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

A small REST API next to `/mcp`, sharing `NoteWriter` and its rules (path validation, version tokens, history, trash). The web UI (build order step 9) uses the same API, so it is written once and documented as an **OpenAPI** file in the Kumbu² repo; that file is the contract both clients build against.

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

`{path}` is the path inside the vault, each segment URL-encoded. Auth is `Authorization: Bearer <key>` like `/mcp`; until build order step 8 that is the dev token.

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

**Decided (Chris, 2026-10-22): keep both, as a conflict copy with a header callout.** No automatic merging and no git-style conflict markers: it is more transparent, and the few conflicts that happen are the user's to resolve.

- The **server version stays at the original path, unchanged**, so the note stays clean for agents and other devices.
- The **local text goes to a conflict copy** next to it, `<name> (conflict <device> 2026-10-22 14-03).md`, uploaded as a normal new note so agents see it too. It starts with a callout that says where each change came from and links to the original:

  ```markdown
  > [!warning] Kumbu² conflict
  > Change from **Laptop** at 2026-10-22 14:03.
  > The change from **server (dev)** at 14:01 is in [[projects/Plan|Plan]].
  > Merge what you need, then delete this copy.

  # Plan
  …local text…
  ```

  - "server (…)" names the writer from the server's history (today `dev` or `seed`; from step 8 the key's name, e.g. the agent or the other device).
  - The link is path-qualified, so it points to the right note even if another note has the same name.
  - If the local text starts with YAML frontmatter, the callout goes **after** it; frontmatter must stay the first thing in the file.
- An Obsidian notice names both files; the status view lists open conflict copies. Resolving means editing the original and deleting the copy; nothing special.
- Device names are set in the plugin settings (default: platform plus a short random suffix).
- The same applies on the first sync, when a path exists on both sides with different content.

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

- **Own repo**, required by Obsidian (`manifest.json`, `README.md`, `LICENSE` at the root of the default branch): proposed `SCAI-NGO/kumbu-obsidian`, licensed AGPL-3.0-or-later like the server.
- Plugin id `kumbu-sync`, name "Kumbu² Sync" (the id must not contain "obsidian").
- TypeScript (the Obsidian API ships as TypeScript types), esbuild, pnpm, Vitest; ESLint with Obsidian's recommended plugin rules, as the directory's automated review checks code quality.
- Releases from a GitHub Actions workflow on a tag: tag = `version` in `manifest.json`, format `x.y.z` without `v`; assets `main.js`, `manifest.json`, `styles.css`.
- Testing and first users: **BRAT** with the repo URL.
- Directory: sign in at community.obsidian.md, link GitHub, submit; automated review for quality, security and malware. The README states that the plugin needs network access and a Kumbu² server and account.

## 11. Order of work and parallelism

Decided on 2026-10-23: the plugin is build order step 11, the last one, after users and API keys (step 8), the web UI (step 9) and OAuth (step 10).

1. **Step 9, in the Kumbu² repo:** the REST API with ETags and the OpenAPI file comes with the web UI. Per-vault `seq` and the change feed are designed then, so step 11 adds to the API instead of reshaping it.
2. **Step 11, server:** change feed, ws ticket and WebSocket hub where step 9 didn't build them, tests.
3. **Step 11, plugin** (own repo, against the OpenAPI file and a local `docker compose` server). `mv` with `--keep-links` exists since step 4, so renames from the plugin use it from the start; per-device keys exist since step 8.

Testing: the sync engine with Vitest (decision table, CRLF, echo suppression, conflict copies, full resync, 412 retry); end to end against the compose stack: edit in a test vault, see it over MCP, and the other way round.

## 12. Decisions

Settled with Chris on 2026-10-22:

1. **Conflicts:** conflict copy with a header callout, no automatic merging (§7).
2. **Licence of the plugin repo:** AGPL-3.0-or-later, the same as the server.
