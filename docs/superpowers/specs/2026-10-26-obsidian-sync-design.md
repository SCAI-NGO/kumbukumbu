# Obsidian sync plugin and pairing — Design

Date: 2026-10-26. Decided with Chris in a brainstorming session on the morning before the COR.energy demo (Tuesday 2026-10-27). It replaces the draft [obsidian-plugin-design-spec.md](../../obsidian-plugin-design-spec.md) of 2026-10-22: what that draft settled (conflict copies with a callout, AGPL, the secret in `SecretStorage`) is carried over here, and the rest is updated to the server as it is now (steps 7 to 10 have landed since). Where this document says *Claude's call*, the decision was not discussed with Chris and is open to review.

Plugin code lives in [SCAI-NGO/kumbu-obsidian](https://github.com/SCAI-NGO/kumbu-obsidian) (created 2026-10-26, licence, README and an `obisdian/` vault only); the server, web app and this document live here.

## 1. Why, and what success looks like

A person writes in Obsidian as they always do, on a laptop or a phone. Their notes are in Kumbu², where their agents read and write them. A change on either side shows up on the other within seconds while online, and on the next start otherwise. Nobody loses an edit: a real conflict produces two visible versions, never a silent overwrite.

At COR.energy this is a personal second brain per employee; sharing between people is out of scope.

### The demo (decided by Chris)

After registering and a short tour of the web app (human and Claude working on notes together), Chris says "and yes, there's a sync with Obsidian, it needs a plugin" and shows:

1. **Connecting** an Obsidian vault that already has notes to a Kumbu² vault, with the **automatic merge** of the first sync. Chris will show this in any case; it is the headline feature.
2. Editing a note in Obsidian on the laptop, then asking Claude in claude.ai: "look in Kumbu², when is my appointment about the car?" Claude finds it with the text just typed.
3. Asking Claude to move it from 14:00 to 16:00. Claude edits the note over MCP.
4. The change in the Kumbu² web app (opening or reloading the note is enough; the note view already refreshes itself).
5. The change in Obsidian on the laptop **within a few seconds, without touching anything**, then in Obsidian on an Android phone (Samsung, a second app profile via Private Space or Secure Folder), which catches up when opened.

### Anti-goals: what would make it a failure even if it technically works (Chris)

- **Sync that stalls, misses updates, or produces conflicts that aren't real** because of race conditions in our code. This is the worst outcome.
- **A vault showing up that wasn't granted.** Isolation between API keys must hold for every new way in, and gets its own code review (§9).

### Non-goals (v1)

- Attachments (images, PDFs, …): Kumbu² is text only in v1 and attachments come in a carefully designed v2, never bolted on. They stay local and are counted as "not synced".
- `.obsidian/`, `.trash/` and every other dot folder: never synced.
- Creating vaults from the plugin; sharing vaults; syncing a subfolder only.
- Merging conflicting text automatically.
- Live updates in the Kumbu² web app beyond what it already does (its note view polls the open note's version), apart from telling device writes from agent writes (§5a).
- An `obsidian://` link or a QR code for pairing (possible later, §4).

## 2. Shape

```
Obsidian (laptop / phone)                            Kumbu² server (single instance)
┌───────────────────────────────┐   REST, Bearer key   ┌───────────────────────────────────┐
│ kumbu-sync plugin             │ ───────────────────▶ │ /api/notes, /api/move (existing)  │
│  sync engine (pure TS)        │                      │ /api/vaults/{v}/versions (new)    │
│  one queue per vault          │   WebSocket, ticket  │ /api/ws-ticket, /api/ws (new)     │
│  base versions in data.json   │ ◀─────────────────── │ "vault changed" hints             │
│  API key in SecretStorage     │                      │ /api/pairing… (new)               │
└───────────────────────────────┘                      └───────────────────────────────────┘
```

One Obsidian vault maps to exactly one Kumbu² vault; the Obsidian vault root is `/<Vault>/` on the server.

## 3. Prerequisites and connecting (decided by Chris)

- **The plugin never creates a vault.** The Kumbu² vault must exist, and the plugin needs a key with **write** rights on it. Without both it does nothing. This keeps the plugin's rights narrow.
- It works the same with an **empty** Obsidian vault and with one that **already has notes**: the first sync merges both sides (§6).
- **Each device has its own key**, so a lost phone is revoked by revoking its key.

### The flow (mockup approved by Chris; HTML in `.superpowers/brainstorm/…/onboarding.html`, not committed)

1. **Web app, a vault's menu → "Connect Obsidian".** A dialog shows:
   - the plugin to add in BRAT, `SCAI-NGO/kumbu-obsidian`, with a copy button (a constant in the web app; when the plugin is in the community directory, the text becomes "search *Kumbu² Sync* in Community plugins");
   - the server URL, with a copy button: `window.location.origin`, since the page the user is on *is* the server (*Claude's call*: no configuration);
   - a choice "key expires: never / 30 / 90 / 365 days", default never;
   - a **pairing code**, large, e.g. `K7QM-4XDA`, with a countdown ("valid for 9:41 · single use · new code");
   - a line saying each device gets its own key, named "Work – Obsidian <device>", revocable under API keys.
2. **Obsidian, Settings → Kumbu² Sync** (not connected): server URL, device name (default: platform plus a short random suffix, e.g. `Android-7Q`), pairing code, **Connect**. Below, folded away: "I have an API key already" (the fallback, §4).
3. **First sync** runs straight after connecting. Only if there are conflicts, it asks once (§6).
4. **Result**, always shown, also when nothing went wrong: "Connected to *Work*", "✓ Done — no conflicts" (or the number of conflict copies), and counts: uploaded, downloaded, unchanged, not synced (with the reasons, e.g. "2 images, 1 PDF"). Chris values the explicit "no conflicts" as a user.

Disconnecting (a button in the connected settings) removes the secret and the plugin's sync state and leaves every file where it is; connecting again runs a first sync again (*Claude's call*).

## 4. Pairing

A pairing code is exchanged once for a new API key, so no person ever sees, copies or stores the key (Chris: "ich liebe Kopplungskeys").

- **Code:** 8 characters of **Crockford Base32** (no I, L, O, U; decided by Chris), shown as `XXXX-XXXX`, 40 random bits. Entry ignores case, spaces and dashes and reads I/L as 1 and O as 0. Valid **10 minutes**, **once**; "new code" replaces it.
- **Creating** (`POST /api/vaults/{vault}/pairing`, sessions only): the vault must be the user's. A new code invalidates the previous open code for the same vault and user.
- **Redeeming** (`POST /api/pairing/redeem`, anonymous, body `{ code, device }`): creates the key and returns `{ key, vault, keyName }`. The key: name `<Vault> – Obsidian <device>` (with ` 2`, ` 3`, … if a key of that name exists, since names are unique per user), **write** on that one vault and nothing else. Expiry as chosen in the dialog: **never** (the default, decided by Chris: Kumbu² is for prosumers and professionals who take responsibility for their keys), 30, 90 or 365 days, the choices `ApiKeys.ExpiryDays` already offers. The plugin reads a `401` as "this key expired or was revoked: pair again" and says so, instead of failing quietly.
- **Why the code holds the session's user key:** `ApiKeys.CreateAsync` needs the user key and the time the session read the password (`src/Kumbu.Server/Accounts/ApiKeys.cs:91`), and a key's name is sealed under both the user key and the key itself, so it can't be renamed after the fact. So the pairing record keeps the user id, a copy of the session's user key, its credential-read time and the vault, **in process memory only** (never in MongoDB: 40 bits don't protect a user key against an offline guess), and drops it on use, on expiry, or on a restart. `CreateAsync` already refuses if the credentials changed in the meantime. This assumes the single server instance the hints and vault cache assume as well. Redis was considered and left for later (decided with Chris): it would put a user key guarded by 40 bits into a second process, possibly on disk, to gain only codes that survive a restart. Redis comes when there are several instances, for hints, rate limits, pairing and the vault cache together.
- **Abuse:** redeeming is rate-limited per client address like sign-in (`AccountLimits`, e.g. 10 failures per 10 minutes); a wrong code says only "this code is not valid (any more)". A wrong code can't tell which code it was aimed at, so there is no per-code count; instead 10 attempts per minute per address and 100 failures per minute server-wide (*Claude's call*, from planning). Redeeming is logged with the user and key id, never the code.
- **Origin check:** `UseCallerRules` refuses a state-changing anonymous request whose `Origin` isn't the server's own (`src/Kumbu.Server/Api/Security.cs:68`). Redeeming must be exempt (new endpoint metadata): it carries its own secret and no ambient credential, so CSRF does not apply, and Obsidian's `requestUrl` sends no matching `Origin`.
- **Fallback:** the plugin also accepts a key made by hand on the API keys page, picked with Obsidian's `SecretComponent`, then a vault picked from `GET /api/vaults` among those the key can write (add `canWrite` to `VaultView`; *Claude's call*).
- **Later, not now:** an `obsidian://kumbu-sync?server=…&code=…` link from the dialog (same machine, nothing to type) and a QR code for the phone.

## 5. Server API

Existing endpoints are reused as they are; the API is described in `GET /api/openapi.json`, and the plugin builds against it.

| Method and path | Status | Use |
|---|---|---|
| `GET /api/notes/{vault}/{path}` | existing | Read content and `version` |
| `PUT /api/notes/{vault}/{path}` | existing | Create (no `version`) or replace (`version`); `409` carries the current content and version, which spares a second request |
| `DELETE /api/notes/{vault}/{path}?version=…` | **extend** | Trash the note only if it is still at that version, else `409` with the current state. Without `version` it behaves as today |
| `POST /api/move` with `keepLinks: true` | existing | A rename in Obsidian: Obsidian rewrites the links itself and uploads them as edits, so the server must not rewrite them too |
| `GET /api/vaults` | extend | Add `canWrite` for the caller (fallback picker) |
| `GET /api/vaults/{vault}/versions` | **new** | Without `since`: every note's `path` and `version`, plus `state`, a hash over the list (`If-None-Match: "<state>"` → `304`). With `since`: only what changed, see below. Every answer carries a `cursor`. Plus `damaged`: the count of stored notes that don't open (`CountDamagedAsync`; their paths are sealed with them, so they can't be listed) |
| `POST /api/ws-ticket` | **new** | API keys only. A random ticket for one WebSocket connection, valid 30 s, bound to the key |
| `GET /api/ws?ticket=…` | **new** | WebSocket; server pings every 30 s; messages `{"type":"changed","vault":"Work"}` |
| `POST /api/vaults/{vault}/pairing` | **new** | Sessions only, §4 |
| `POST /api/pairing/redeem` | **new** | Anonymous, rate-limited, origin-exempt, §4 |

Writes from the plugin are stored **verbatim** (only CRLF becomes LF, as `NoteWriter` already does). The server must not reformat anything, or the change would come back to Obsidian and loop.

### Asking for changes: since a time, with overlap (decided with Chris)

The draft proposed a per-vault sequence number and a `changes?since=N` feed. Its hardest part is a race: a feed must never show sequence 6 before a still-running write with 5 has landed, or a client skips 5 forever. Chris's idea instead: ask only for what is new, with some slack for safety, while the newest state is always included. Version tokens are content hashes without an order, so the slack is measured in **server time**:

- Every answer of the versions endpoint carries a `cursor`: the server's time when it was built. The plugin keeps it.
- `GET …/versions?since=<cursor>` returns everything changed after **cursor minus 5 minutes** (the overlap): notes by their last write time (`path`, `version`), notes gone by their trash time (`path`, `deleted: true`), and moves by their move record (the old path as deleted, the new path with its version). The overlap catches writes stamped just before the cursor but committed just after it. Duplicates are harmless: the decision for a path that didn't change is "nothing".
- **Every way a note changes must leave a time the query finds**: writes, `mv` (old and new path), restore from history or trash, checkpoint restore, batches, uploads, seeding, deletion of a vault's notes. The plan checks each one; a change without a time would be missed by `since` until the next full list.
- **The full list stays the safety net** and the source of truth: on the first sync, on plugin start, when the app becomes visible again, once an hour, and whenever the cursor is older than 25 days (the trash keeps deletes for 30). It is also where damaged notes are counted.
- Hints and "Sync now" ask with `since`; the full list is cheap anyway (vaults are decrypted into memory when used; 2,000 notes are about 120 KB of JSON), but most answers are now a few lines.
- No sequence counters, no gap rule, no retention logic of its own on the server.

### Hints

- Every committed change to a vault's notes or trash raises an in-process event naming the vault, **whatever path it took**: `NoteWriter`, `NoteMover`, restore, checkpoint restore, batches, uploads, seeding, deletion. The safest place is the store, after a successful commit (builder's choice where exactly, but no write path may be missed).
- The WebSocket hub sends `changed` to connections whose key has a slot on that vault, and to no other. Hints carry the vault name only; they are a nudge to ask for the list, so lost or duplicate hints are harmless.
- Hints are debounced per connection (e.g. at most one per vault per 500 ms), so an agent writing 30 notes doesn't trigger 30 lists.
- A connection whose key is revoked or expires is closed at the next ping.
- The connection does not keep a vault unlocked: a hint needs only the vault id, and the list request that follows unlocks it like any request.
- Deployment: Traefik passes WebSockets through without extra config; check it on kumbu.tidesson.net during the dry run.

## 5a. Device keys and agent keys (decided by Chris)

A key from pairing is an ordinary API key, so without a change everything Chris types in Obsidian would show in the web app as an *agent's* change. That blurs the demo's story of a human and Claude working together.

- **Every API key has a kind: `agent` or `device`.** It is sealed in the key's metadata (`ApiKeyMeta`), since a key's name and metadata can't be changed after creation without the user key. Pairing always creates `device` keys; keys made on the API keys page are `agent` keys, and existing keys count as `agent`. (Whether the keys page offers a choice is left to the builder; not needed for the demo.)
- **The API says what kind each writer is**, next to the writer label, wherever a writer is reported: the open note's version poll, the history, `/api/changes`, and `lastWriter` on a note (e.g. `writerKind`: `user`, `device`, `agent`, or `system` for seed and the dev token; exact shape is the builder's).
- **"Changed elsewhere" bar** (`web/src/note/ChangedElsewhere.tsx`), shown when the open note changes: for a device write it says *"You changed this note in Obsidian (Laptop) just now"* with **Refresh** (and Read mode's automatic refresh as today). Agents keep today's wording. The device name is the part of the key name after "Obsidian ".
- **Agent banner** (`web/src/note/AgentBanner.tsx`, "what did my agents do since I was last here", with line highlights): device writes are left out; `/api/changes` returns agent writes only.
- **History and the API keys page** mark device keys, e.g. a small "Obsidian device" label next to the name.
- In the demo: typing in Obsidian shows as *you, in Obsidian*; Claude's edit shows as an agent's, with highlights.

## 6. Sync engine (plugin)

Plain TypeScript without Obsidian imports, so it runs in Vitest against a fake server and a fake file system, and in Node against the real server (§9). A thin Obsidian layer feeds it events and does the file operations.

### Rules against races (the first anti-goal)

1. **One queue per vault.** Uploads, downloads, deletes, renames and list checks run one after another, never in parallel. Events only enqueue work; a list check that is already queued is not queued twice.
2. **Decisions are made per path from three versions:** `local` (hash of the file now), `remote` (from the list, or from a `409`), `base` (the version both sides last agreed on). Versions are computed as the server does: first 12 hex characters of SHA-256 over the UTF-8 text, after CRLF → LF.
3. **Check before writing locally.** A download is written only if the file is still at `base` at that moment (Obsidian's `vault.process` reads and writes in one step). If the user typed meanwhile, nothing is overwritten and the path is decided again.
4. **A `409` is new information, not an error:** take the current version from its body and decide again. If it equals `local`, just record the new base (this also covers a retried upload whose first response was lost).
5. **Own changes coming back** (an upload shows up in the next list) find `local = remote`, so they cause nothing. No flags, no "ignore the next event" tricks.
6. **Uploads are debounced** 2 s after the last change per file, so typing doesn't upload every keystroke. History stays readable anyway: writes by the same key within 10 minutes form one changeset.

### Decision table

| local vs base | remote vs base | Action |
|---|---|---|
| same | same | nothing |
| changed | same | upload (`version: base`; create without one if there is no base) |
| same | changed | download; if remote is gone, move the local file to Obsidian's trash |
| changed | changed, local = remote | record the new base |
| changed | changed | conflict (§7) |
| deleted locally | same | delete on the server with `version: base` (the note goes to Kumbu²'s trash) |
| deleted locally | changed | the remote edit wins and is downloaded again; nothing is lost |
| changed | deleted remotely | the local edit wins and is uploaded again as a new note at its path; nothing is lost (*Claude's call*, the mirror of the row above; an agent's rename while the user types therefore leaves both paths, visibly, and not as a conflict) |
| same | missing, while the list reports `damaged > 0` | nothing: the missing note may be a damaged one, so no local file is trashed because of absence while the vault has damaged notes; the settings say so (*Claude's call*) |
| new locally, no base | missing | upload as a new note |
| missing, no base | new remotely | download |

Renames in Obsidian go to `/api/move` with `keepLinks: true` when the old path has a base and the file is unchanged; otherwise (or on a `409`) as delete plus create through the table. Renames by an agent arrive as one path gone and one new.

### Triggers

Obsidian's `create`, `modify`, `delete` and `rename` for `.md` files outside dot folders; WebSocket hints; plugin start; the app becoming visible again (phone resume); "Sync now". WebSocket reconnects use exponential backoff (1 s to 5 min, with jitter) and are followed by a list check. After three failed connections in a row the plugin also checks the list every 2 minutes, and keeps retrying the WebSocket.

### First sync (decided by Chris)

Runs on connecting, with no base for any path:

- A path on one side only is copied to the other. A path on both sides with the **same** text becomes the base. No question for any of these.
- **Only if** paths exist on both sides with **different** text, the plugin asks **once, for all of them** (Chris: after the third file nobody reads per-file questions any more):
  - title with the count: "7 notes differ between this vault and *Work*", plus how many merge by themselves;
  - **Keep both (safest)**, highlighted: Kumbu²'s version stays at the path, the local text becomes a conflict copy (§7);
  - **Keep local**: the local text replaces Kumbu²'s version, which stays in Kumbu²'s history;
  - **Keep Kumbu²**: Kumbu²'s text replaces the local file, which first goes to Obsidian's `.trash` (decided by Chris), so nothing is lost on either side;
  - buttons **Continue** and **Show the 7 notes** (a read-only list, no per-file choice).
- The result screen of §3 follows in every case.

### Not synced

Files that can't go to Kumbu² are skipped with a reason, never retried in a loop: not `.md` (attachments), larger than 1 MiB, rejected by the server's path rules (the server's `400` message is the reason shown). The status bar shows `Kumbu² ● live · 3 not synced` (or `polling`, `offline`), and the settings list the files and reasons.

### Local state

In the plugin's `data.json`, never a secret: server URL, vault, device name, the secret's name, and per path the base version. The time of the last successful sync is shown in the settings.

## 7. Conflicts during normal use (carried over from the draft, decided 2026-10-22)

When both sides changed after the first sync, Kumbu²'s version stays at the path unchanged, and the local text becomes a **conflict copy** next to it, uploaded like any note so agents see it too:

`<name> (conflict <device> 2026-10-27 10-15).md` (a suffix, not a prefix, so it sits next to the original; decided by Chris), starting with:

```markdown
> [!warning] Kumbu² conflict
> Change from **Laptop** at 2026-10-27 10:15.
> The change from **Work – Obsidian Android-7Q** at 10:14 is in [[projects/Plan|Plan]].
> Merge what you need, then delete this copy.
```

The writer name comes from the note's `lastWriter`. The link is path-qualified. If the local text starts with YAML frontmatter, the callout goes after it. An Obsidian notice names both files; the settings list open conflict copies. Resolving means editing the original and deleting the copy.

## 8. The secret

- The key lives **only in Obsidian's `SecretStorage`**, under `kumbu-<vault>-<device>` (vault in the name because secrets with equal names were reported to leak across vaults on mobile); `data.json` holds the secret's name only.
- `minAppVersion` `1.11.4`; if `app.secretStorage` is missing, a notice and no sync. No plain-text key field anywhere.
- HTTP through Obsidian's `requestUrl` (no CORS, works on mobile); the WebSocket is the standard one, authorised by the ticket, so its `Origin` needs no check.
- Server URL must be `https://`, except `http://localhost` and `http://127.0.0.1`.
- Before the README claims anything about storage at rest, check what `SecretStorage` does per platform.

## 9. Making sure it holds (the anti-goals)

**Races.**
- Vitest for the engine against a fake server that reorders and delays on purpose: an upload while a hint arrives, typing during a download, duplicate and lost hints, a lost response followed by a retry, a `409` on delete, a rename on one side while the other edits.
- An end-to-end run in Node (the engine with a file-system adapter) against the compose stack: the engine and MCP writes edit the same vault at the same time; at the end both sides are identical, and conflict copies exist only for real conflicts.

**Isolation.**
- xUnit tests for every new endpoint in the pattern "a key for vault A asks for vault B → `404`", including: the versions list, `ws-ticket`, hints (a connection for A never hears about B), pairing (a code is bound to its user and vault; the key it creates has exactly that vault, write), and `DELETE` with `version`.
- A **code review of isolation** before the merge, by a reviewer who is given the code, not the conclusions: every place where a key, a session, a ticket or a pairing record meets vault access, including the existing endpoints.

## 10. Repository, release, order of work

- Plugin repo as created: id `kumbu-sync`, name "Kumbu² Sync", TypeScript, esbuild, pnpm, Vitest, ESLint with Obsidian's recommended plugin rules; release workflow on a tag `x.y.z` (no `v`) with `main.js`, `manifest.json`, `styles.css`; first users install with BRAT. The README states that the plugin needs network access and a Kumbu² server and account.
- **Server first** (here): key kinds and the web app's wording (§5a; before the first pairing, so no key needs migrating), the versions list, `DELETE` with `version`, `canWrite`, hints and the WebSocket hub, ws tickets, pairing, the web dialog; tests. **Then the plugin** against the OpenAPI file and a local compose stack. The isolation review closes the server part.
- **Deadline:** a release `0.1.0` installable through BRAT and the server deployed on kumbu.tidesson.net before the dry run on the evening of 2026-10-26.
- Out of the old draft and no longer needed: the sequence counter and its gap rule, `If-Match` headers (the existing body `version` and `409` serve the same purpose).
