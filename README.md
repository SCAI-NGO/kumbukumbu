# Kumbukumbu (Kumbu²)

*kumbukumbu* is Swahili for memory, remembrance, also records or minutes. It comes from *kumbuka*, "to remember". Repeating the word intensifies it: kumbu × kumbu = kumbu². The superscript also reads as "second", as in second brain.

Kumbu² is a self-hostable second brain for humans that AI agents may use. Notes are plain Markdown, kept in MongoDB, and agents reach them through an MCP server. It is your memory; the AI gets a key.

## Status: reading, searching by words and meaning, writing, encrypted at rest

The server offers four MCP tools. `sh` is a shell over your notes:

- `ls [path]` lists a directory (no path lists the vaults, and says if vaults don't open with the token or notes are damaged)
- `cat <path>` prints a note, with its **version token** in a separate block; `cat "<path>#Heading"` prints one section (nested: `#Parent#Child`)
- `outline <path>` lists a note's headings with line ranges and sizes
- `rg [options] <regex> [path...]` searches notes and prints `path:line:text`; without a path it searches every vault
- `semsearch <query> [path]` finds the sections that best match a question or topic, ranked by words (BM25) and by meaning (embeddings, also across languages), combined into one score that each result shows; `--words` or `--meaning` uses one ranking only
- `rg --files [path...]` lists note paths, so `rg --files | rg -i kumbu` finds a note by name
- `links <path>` shows where a note's `[[wikilinks]]` lead (`links <directory>` lists the broken ones), `backlinks <path>` which notes link to it
- `head`, `tail`, `wc`, `sort` work on a note or on piped input; `sort`, `ls` and `rg` list in human order (Ü next to U)
- `history <path>` lists a note's versions, `restore <path> <version>` goes back to one; `history <folder> --since 2h` lists the latest writes below a folder or vault, with lines added and removed
- `diff <path>` shows what the last write changed, `diff <path> <version> [<version>]` compares versions, `diff <path> <other>` two notes; `--word` marks changed words inside lines
- `--version <token>` reads an earlier version with `cat`, `outline`, `links`, `head`, `tail`, `wc`, `sort`, and `rg` on one note
- `mv <path> <target>` moves or renames a note, `mv <folder>/ <target>/` a whole folder in one transaction; links whose target the move would change are rewritten in the form the author used (`--keep-links` leaves them); the old path remembers where the note went for 30 days
- `rm <path>` moves a note to the trash (kept 30 days) and names the links that now lead nowhere; `trash` lists it with its version, `restore <path> [version]` brings it back
- `mkcp <path> <name>` keeps the current state of a note, a folder or a vault as a **named checkpoint**, like a Git tag: never pushed out of the history, never expired, until `rmcp <path> <name>` removes it; `lscp [path]` lists them (10 per vault by default). Reading commands take `--checkpoint <name>` (`ls`, `cat`, `rg` over folders too), `diff --checkpoint <name> <path>` shows what changed since, and `restore --checkpoint <name> <path>` goes back: a folder with a preview and plan token, after which it matches the checkpoint exactly (notes made since go to the trash, notes moved since move back and the links outside follow them). The manual tells agents to set or remove checkpoints only when the user asks
- `help [command]` lists the commands or explains one; `<command> --help` does the same

`write` creates a note or replaces its content, and `edit` replaces one unique passage or appends to the end, of the whole note or of one section (`path` = `/Work/plan.md#Goals`; `replace_section` replaces the section, `replace_all` every occurrence). Both take the version token from `cat`; if the note changed in the meantime, the write fails instead of overwriting someone else's change. The last 10 versions of each note are kept. Writes by one writer close together in time form a **changeset** (it ends after 10 minutes without a write, or after an hour) and give each note one version, whatever tool wrote them, so the state before an agent session can always be restored; a `restore` always gets its own version. New notes can only go into existing vaults.

`replace` changes a text in every note below a folder or vault: the first call previews every note it would change and returns a plan token, the second call with that token applies exactly that preview in one transaction, or nothing if a note changed in between. The replace is literal, also inside `[[links]]`; the preview names the notes named after the old text, the links that would lead nowhere afterwards and those that would lead to another note, with the hint to `mv` a note before renaming it. Changes that need judgement, like a sentence that depends on a name without containing it, are for the agent, not the tool. A plan is applied once; a second apply says when it was applied, or why it failed.

Commands chain with `|`, for example `cat /Work/plan.md | rg -n TODO` or `rg -l Hecate | wc -l`. `|` is the only operator: redirection, `&&`, `;` and `$()` are rejected with an error that says so. Regexes follow ripgrep's syntax and never backtrack, so no pattern can stall the server (no look-around, no backreferences).

Paths are always absolute, for example `/Work/projects/plan.md`. Top-level folders are vaults. Links resolve like in Obsidian, within the note's vault: the note whose path ends with the link, case-insensitive; ambiguous links are reported, never guessed. Kumbu² never rewrites note content on its own; only `mv` changes links. Markdown links (`[text](note.md)`) are not followed yet. The web app (below) and the REST API under `/api` sit on top of this; accounts and API keys are described further down.

### The web app

The server also serves a web app, built from `web/` into the image. It works on a phone first and on a laptop too: sign in, browse vaults and folders, read and edit notes in Markdown (saved automatically, with a conflict prompt if an agent wrote in the meantime), see a note's earlier versions and restore one (the note's ⋯ menu, "History"), bring back removed notes from a vault's trash and make, restore (after a preview of exactly what changes) or remove a vault's named checkpoints (the vault's ⋯ menu in the drawer, "Trash" and "Checkpoints"; removing a checkpoint is for good), search by words and meaning, upload Markdown files or folders, export vaults as ZIP (Settings, "Your data", for several at once) and import such a ZIP, see when the open note changes elsewhere (an agent, another tab or device; a bar offers the new version, which never replaces the text on screen by itself unless "Refresh Read mode automatically" is on in Settings, and never in Write mode), make API keys, and, for admins, manage users and invitations. It talks only to the REST API, with the same-origin session cookie. Notes are text only for now.

### Updates and installing as an app

**Updates arrive by themselves.** The web app and the server are built with the same version, the image's (`0.2.0+a6362ec`: the release or `<version.txt>-pre.<run number>`, then the short commit). The server sends its own as `X-Kumbu-Version` on every `/api` response, and answers `GET /api/version` (`{ "version": "0.2.0+a6362ec" }`, no sign-in needed). The app compares it with its own on every response and whenever the page becomes visible again (a phone app back from the background). When the server runs a different build, the app reloads itself at the next navigation or when it becomes visible, **on the same page**: the address stays, Read or Write mode and the scroll position come back, and a short notice says "Updated to 0.2.0". In Write mode, under the top bar, a bar offers "Kumbu² 0.2.0 is available · Update": the app saves first and reloads only once everything is saved (a conflict or a failed save keeps the bar and the text). Nothing reloads while a reload would lose something else either: a secret shown once and not yet copied or downloaded (an API key, a recovery key, an invitation link), an upload that is running, text typed into a field still on the page, unsaved text that a session end kept for after signing in again, or a search hit's section about to be shown; the next navigation or return to the page tries again. It reloads at most once per version: if the page is still the old version afterwards (a caching proxy, say), it stops and says "Kumbu² couldn't load version 0.2.0. Reload the page, or ask your admin." (with Dismiss; the sentence goes by itself once the server runs this app's build again) Settings, "About", shows the app's version, and the server's when they differ. This works on the sign-in screens too. The first release with this code updates the way the old one did (a reload by hand); from then on every tab picks up the next release by itself.

**No service worker**, deliberately: a service worker serves the app from its own cache and waits until every tab is closed before a new version takes over, the most common reason a web app stays on an old version. Without one, `index.html` is always revalidated (`Cache-Control: no-cache`) and the hashed files under `/assets` are cached for good, as before. Kumbu² has no offline mode; notes live encrypted on the server.

**Installing as an app** needs only the web app manifest (`web/public/manifest.webmanifest`): Chrome installs from its menu without a service worker. On a phone that hasn't installed it, the app shows a hint "Install Kumbu² as an app" on the first visit only; Settings, "Install as an app", has the steps:

- **Android (Chrome):** menu `⋮` → `Install app` (or `Add to Home screen`).
- **iPhone and iPad (Safari):** Share → `Add to Home Screen`.
- **Desktop (Chrome or Edge):** the install icon at the right of the address bar, or the menu → `Install Kumbu²`.

**The icon** is a placeholder "K²". Every icon (favicon, 192 and 512 px, the maskable variant with its safe zone, the 180 px `apple-touch-icon`) is generated from one SVG, `web/icons/k2.svg`, whose glyphs are paths, not text. To replace it, edit or replace that file (keep the glyph in `<g id="glyph">` and the background in `<rect id="background">`) and run `pnpm -C web icons`, then commit the files it writes under `web/public`.

## Security

**Notes are encrypted at rest.** Every vault has its own random key. Notes, their history, the trash, move records and even paths and vault names are sealed with AES-256-GCM before they reach MongoDB. A database dump or a stolen backup shows random ids, keyed hashes of paths, revision numbers, sizes and times, nothing readable. The vault key itself is stored only **wrapped** in key slots: one for the dev token (HKDF-SHA256) and one for the vault's **recovery key**. An unlocked vault is held in memory while it's used, and locked again after 15 idle minutes.

Be clear about what this does not cover (concept §3):

- Vaults that open with the dev token (the seeded ones, a single-user setup) are only as safe as the server's configuration: whoever has **both** the database and `.env` can read them. A vault that belongs to a user opens only with that user's password or recovery key (see Accounts); the dev token does not open it. Encryption protects the database and its backups, not a machine where both are stored.
- The running server sees plaintext in memory; that's how search works. An operator of the running server is trusted. The embedding model runs inside the server process, so notes never go to an outside service to be embedded, unless the admin configured an external embedding provider **and** a vault's owner chose it for that vault (below); the embeddings are sealed like the notes either way.
- The AI provider of a connected client sees what the agent reads. This is not end-to-end encryption.

Every request under `/mcp` and `/api` needs a **caller**, one of: a **session** (cookie `kumbu_session`, accepted under `/api` only; `/mcp` takes bearer keys only), an **API key** (`Authorization: Bearer kumbu_…`), or the optional **dev token** (also a bearer); a connection's own URL, `/mcp/c/{id}`, takes only that connection's OAuth access tokens and nothing else (see "Connecting claude.ai and other apps"). Without one, `/api` answers `401` with a JSON `{"error": …}`. `GET /healthz`, `GET /api/openapi.json`, the sign-in, join, recover and invitation-peek endpoints, and paths outside `/mcp` and `/api` need none. After 20 wrong bearer keys in a minute from one client network, every bearer key from it gets `429` until the minute has passed. Traffic is plain HTTP; put TLS in front (below). The dev token is optional: without it, it opens nothing, which is what a public instance wants. If set, the server refuses to start unless it is at least 16 characters; use a random one (`openssl rand -hex 32`), since the vault keys of seeded vaults are derived from it. Compose publishes port 8080 (or `KUMBU_PORT`) on **localhost only** (`127.0.0.1:${KUMBU_PORT:-8080}:8080` in `compose.yaml`). To reach it from other machines, remove the `127.0.0.1:` prefix deliberately, knowing that anyone on the network who learns a key can read and change what it opens.

### Accounts, API keys and the REST API

Users sign in with a username and a password (at least 12 characters, not a common one) and own their vaults. Each user has a **user key** that opens their vaults; the password and the **recovery key** shown once at sign-up are two ways to unwrap it. An admin sees who exists and how many vaults and bytes they hold, never a note, and cannot reset a password: lose both password and recovery key, and the vaults are gone. Deactivating a user ends their sessions and revokes their API keys at once. **Deleting is for good**: a user deletes a vault in Settings with the password and its name typed out (`POST /api/vaults/{vault}/delete`, body `{"password": …, "confirm": "<vault name>"}`), or their whole account with the password and the username typed out (`POST /api/account/delete`, body `{"password": …, "confirm": "<username>"}`; not the primary admin); an admin deletes any other user, active or not, in one step, with the username typed out and the admin's own password (`POST /api/admin/users/{id}/delete`, body `{"password": …, "confirm": "<username>"}`; it answers the deletion receipt, see "Privacy requests"; not the primary admin, and not the admin themselves, who use Settings). Each removes, in one transaction, the vaults with every note, their history, trash, checkpoints and search vectors, every slot, and for an account the user's sessions, API keys and app connections (an API key left with no vault is revoked); the invitations the user created stay. Backups taken before still hold the (sealed) data. A new recovery key (`POST /api/recovery-key`, body `{"currentPassword": …}`) needs the current password, as a password change needs the old one, so a stolen session alone can't make a recovery key and use it to set a new password; a wrong password answers `400`, like a wrong old password at `POST /api/password`. **Changing the password revokes every API key** unless you untick "Also revoke all API keys" (`POST /api/password` with `"revokeApiKeys": false`; left out, it is `true`); it does not replace the recovery key. **A recovery** (`POST /api/recover`) always revokes every API key, disconnects every app and replaces the recovery key: the used one stops working, and the answer, `{"recoveryKey": …}`, carries the new one, shown once. It also gives the account a new user key at once and then **re-encrypts every vault** with a new key in the background, one after the other: the answer doesn't wait for it, the vault list shows a vault as "Being re-encrypted" until it is done, and a request for that vault answers `503` with `Retry-After` meanwhile. How this works and why: CONCEPTS.md, chapter 10.

**Re-encrypting.** Revoking a key or changing the password changes no encryption key, so a copy of the database taken before, together with a credential that worked then, still opens what that copy held. Two actions in Settings give new keys. Each vault gets its new key in turn, one after another, and shows as "Being re-encrypted" until it is done; from then on, what you write to that vault is out of an old credential's reach. A copy taken before keeps what it held, and a note that was already damaged and unreadable may stay under the old key:

- **Re-encrypt my vaults** (`POST /api/account/reencrypt`, body `{"password": …}`): a new key for the account and a new recovery key, shown once (the old one stops working), and every vault re-encrypted with a new key in the background, as after a recovery. What it costs: every API key is revoked and every app disconnected before the answer, so agents need new keys and apps a Reconnect, and every other session is signed out; this one stays.
- **Re-encrypt vault**, next to a vault's Delete (`POST /api/vaults/{vault}/rekey`, body `{"password": …}`): a new key for that one vault; the request waits until it is done, which takes a while for a large vault. What it costs: every API key with access to the vault is revoked, also for its other vaults, and every app connected to it must reconnect; the dialog lists them first (`GET /api/vaults/{vault}/access`). The answer says how many keys were revoked and apps disconnected.

Both need the password and count against the same limit as a password change. Creating a key or a connection for a vault, or changing a connection's rights, answers `503` while that vault is being re-encrypted. A password change alone does not re-encrypt anything. **After a suspected compromise**, use "Re-encrypt my vaults", and remember that each vault is protected only once it no longer shows as "Being re-encrypted"; if the password itself may have leaked, change it too; then give the agents you still use new keys and reconnect your apps. Vaults that open with the dev token can't be re-encrypted. The details and the limits: `docs/security-architecture.md`, §3 "Key rotation and re-keying".

**The first admin.** On a public instance there is no seeded user. Set `KUMBU_PUBLIC_URL` (the address users reach the server at, e.g. `https://kumbu.example.org`, no path), start the server and run:

```sh
docker compose exec kumbu kumbu bootstrap-admin
```

It prints a one-time link `https://kumbu.example.org/join#<code>` (on stdout, never in the log), valid 24 hours. Opening it creates the **primary admin**; running the command again replaces the link; once a primary admin exists the command is refused. Further users come in through invitations from the admin screen or `POST /api/admin/invitations` (single use, 7 days by default, at most 30).

**The welcome vault.** A new account gets a vault *Welcome* with notes about Kumbu²: working by hand, with an AI and with Obsidian, connecting an AI, security and privacy, and the user's data. It is the user's like any other: they may edit it or delete it, and it is never created again. The texts live in `src/Kumbu.Server/Welcome/Notes/`; `{{server}}` is replaced by `KUMBU_PUBLIC_URL` (without it, by a placeholder address and a pointer to the browser's address bar). Only joining (an invitation or the `bootstrap-admin` link) creates it; existing accounts get nothing.

**API keys** (`kumbu_…`, 32 random bytes in Base62) are made by a signed-in user, are **shown once**, and carry per-vault rights: read, or read and write. A key can **expire** after 30, 90 or 365 days (`"expiresInDays"` in `POST /api/keys`; the web app proposes 90) or never; from then on it answers `401` like a revoked one, and the key list shows when. A key opens exactly the vaults it was given; a read-only key is refused every write, including `replace` and the folder `restore --checkpoint` preview. The key's name becomes the writer label in the history, so the name may not be the user's own username, `dev` or `seed`, contain " (", control characters or invisible formatting characters (zero-width spaces, bidi overrides), and has at most 64 characters. Use a key with Claude Code the way the dev token is used:

```sh
claude mcp add --transport http kumbu http://localhost:8080/mcp --header "Authorization: Bearer kumbu_…"
```

**The REST API** lives under `/api` and serves the web app and the Obsidian plugin (see "Syncing with Obsidian"); notes are addressed by vault and path, and a write carries the version token from the read. `GET /api/openapi.json` describes it. The session cookie is `HttpOnly`, `Secure` and `SameSite=Strict`, lasts 30 idle minutes and 12 hours at most, and survives a server restart. A browser needs HTTPS for it (localhost counts as secure in browsers), so a public instance runs behind a TLS-terminating proxy.

**Export and import.** A user takes their notes out as a ZIP: the vault's ⋯ menu in the drawer, "Export as ZIP" (`GET /api/vaults/{vault}/export`, file `Work 2026-10-26.zip`), or Settings → "Your data" → "Export my vaults…", which lists every vault, all ticked (`GET /api/export?vault=Work&vault=Ideas`, each vault once, file `Kumbu² export 2026-10-26.zip`; the date is the server's, in UTC). Inside is one folder per vault, named as the vault, with its notes as `.md` files at their paths, verbatim, each dated with the note's last write (ZIP times have no zone, so the web app sends the browser's, `tz=Europe/Vienna`; without one they are UTC); unpacked, each folder opens in Obsidian as a vault ("Open folder as vault"). Only the current notes are in it: no history, trash, checkpoints or vault settings. The server streams it from the session's unlocked vaults; an unknown vault answers `404` and one being re-encrypted `503` before anything is sent. Export is for sessions only: an API key, a connected app or the dev token gets `403`. **Import** is Settings → "Your data" → "Import vaults…": the browser unpacks the ZIP itself (the server parses no ZIPs) and uses only `POST /api/vaults` and `POST /api/upload/{vault}`, in batches of at most 200 files and 5 MB. Each top-level folder becomes a vault; a ZIP with notes at its root (an Obsidian vault zipped directly) becomes one vault named after the file. A preview lists the vaults with their note counts and the files skipped, as the upload skips them (anything but `.md`/`.markdown`, `.obsidian/` and `.trash/`, notes over 1 MiB, files that aren't UTF-8 text; attachments are never unpacked). For a vault that exists already, the browser compares every note with the vault's version list (`GET /api/vaults/{vault}/versions`) before anything is written and asks once: replace the differing notes (the history keeps the old versions) or skip them all; notes that are already there as they are aren't sent, so importing the same ZIP twice changes nothing. Not carried: history, trash, checkpoints, vault settings.

**Security notes for a public instance:**

- Without `KUMBU_TRUSTED_PROXY`, the server sees the proxy's address for every client. Behind Traefik that means all clients share one IP, so one attacker's wrong keys lock **all** bearer users out for a minute. A CDN in front of Traefik has the same effect, since the server then sees the CDN's addresses. Set `KUMBU_TRUSTED_PROXY=true` only when the proxy is the container's only way in (no published port): the server then takes the client address from the last `X-Forwarded-For` entry the proxy appended, and whether the request came over https from `X-Forwarded-Proto` (for `Strict-Transport-Security`). It refuses to start with it unless `KUMBU_PUBLIC_URL` is set.
- Sign-in is limited to 5 attempts a minute per username and 20 per client network. That also means anyone who knows a username can lock that user out of signing in for a minute. This is a deliberate trade-off against guessing passwords; bearer keys and sessions already issued are not affected.
- Checking the current password, for a password change, a new recovery key, re-encrypting or deleting, is limited to 5 attempts a minute per username (they share the budget, apart from sign-in's), so a stolen session can't guess the password faster than signing in could.
- Argon2 (64 MiB and a core per run) runs at most once per core at a time across the server; further sign-ins wait their turn, so many sign-ins from many addresses at once slow down instead of exhausting memory.
- **App connections and the limits.** Every claude.ai user of an instance arrives from Anthropic's one range, so the OAuth limits are built not to lump them together: failed token requests count per client id **and** client network together (20 a minute), so a stranger who names claude.ai's client id can't block claude.ai's real requests, and Anthropic's range can't block other apps; dynamic client registrations are limited to 30 an hour per network. Access tokens expire hourly, and an expired or revoked one is kept for 24 hours as a tombstone (its keys deleted, only its lookup value left), so it is recognised and never counts as a wrong key against that shared range. A refresh token is replaced at every use; one presented again after 30 seconds, even one already revoked when the connection has new tokens, disconnects the app (`refresh_reused`), since someone else holds a copy.
- The limiters sit in memory and count per client network (an IPv6 address by its /64), each for about a minute after its last failure, so their memory grows with the number of distinct networks that failed within that minute.
- **For claude.ai and other apps** (see "Connecting claude.ai and other apps"), Anthropic's servers must reach the instance directly:
  - **An IPv4 `A` record** for the instance's name; don't rely on IPv6 alone.
  - **No redirect** on `/mcp/c/…` or `/.well-known/…`: no `http`→`https` and no apex→`www` for the URL users paste. Give users the final `https://` address, exactly as `KUMBU_PUBLIC_URL`.
  - **Route `/.well-known/` and `/oauth/` to Kumbu²**, not only `/mcp` and `/api`: the app finds the metadata there (`/.well-known/oauth-protected-resource/mcp/c/…`, `/.well-known/oauth-authorization-server`) and signs in through `/oauth/authorize`, `/oauth/token`, `/oauth/register` and `/oauth/revoke`.
  - **Let Anthropic through** a proxy, WAF or fail2ban: its requests come from `160.79.104.0/21` with the user agent `Claude-User`. Every claude.ai user arrives from that one range, so a rule that bans an address for failed requests bans them all.
  - **No strict `Origin` rule on `/mcp`**: the server checks what it needs itself; an `Origin` filter in the proxy would refuse requests that are fine.
  - **Changes to the metadata take about 5 minutes to reach claude.ai**: both documents are cacheable for 5 minutes (`Cache-Control: public, max-age=300`), so after changing `KUMBU_PUBLIC_URL` or the routing, wait before testing again.
  - **Always set `KUMBU_PUBLIC_URL` on a public instance.** Without it, the OAuth addresses (issuer, endpoints, the connection's URL) are taken from the request's `Host` header and served in cacheable documents, so a request with a forged `Host` could leave a wrong address in a cache in front of the server.
- To identify an app connection's client (for example Claude), the server fetches the client's metadata document from the URL the client names: a plain `https` request from the server to the internet, so a public instance needs outbound access (for `claude.ai`, `https://claude.ai/oauth/mcp-oauth-client-metadata`). The fetch follows no redirects, refuses any host that does not resolve to a public address, and reads at most 16 KiB in 5 seconds; a firewall that blocks it makes the consent page report that the app can't be identified.
- **WebSockets for the Obsidian sync**: the proxy must pass the upgrade on `/api/ws` (Traefik does without extra configuration) and keep an idle connection open for at least a minute (the server pings every 30 seconds). Its access log shows `/api/ws?ticket=…`: a ticket works for one connection and only within 30 seconds of being made, so a logged one is worthless. Redeeming a pairing code is limited to 10 attempts a minute per client network, and refused for everyone while the server has seen 100 wrong codes in the last minute (logged as a warning): an attack can delay pairing, not speed up guessing.
- Invitation codes travel in the link's fragment (`#…`), which browsers do not send, so they never reach the server's or the proxy's logs.
- A reverse proxy's **access log** holds what the server's own log leaves out: request lines under `/api` name vaults and note paths (`/api/notes/Work/plan.md`) and carry search queries (`/api/search?q=…`). On a public instance, disable the access log for `/api` or filter those paths and the query string out. The server's own log names vaults by id and logs no note path; only seeding names the folders and files it reads from `Kumbu:Seed:Path`.
- Without a dev token, the server logs a warning at every start (`Kumbu:DevToken is not set: neither Kumbu:Seed:Path nor Kumbu:Recovery:Keys is applied`) and seeds nothing. On a public instance this is expected.
- **MongoDB authentication** is off unless you set `KUMBU_MONGO_PASSWORD` (a long random value, e.g. `openssl rand -hex 32`). With it, MongoDB requires a login: on its first start with the password it generates a key file inside its data volume (`/data/db/kumbu.keyfile`, which a replica set with authentication needs), and its health check creates the root user `kumbu` with that password; the server signs in as `kumbu` (`Kumbu:Mongo:User`, `Kumbu:Mongo:Password`). The password stays in the containers' environment. **On an existing data volume** nothing else is needed: set `KUMBU_MONGO_PASSWORD` in `.env` and run `docker compose up -d`; both containers are recreated, MongoDB restarts with the key file, and since the database has no user yet, MongoDB's localhost exception lets the health check create `kumbu` (your data stays as it is). To change the password later, change it in MongoDB first (`docker compose exec mongo mongosh -u kumbu -p --authenticationDatabase admin --eval "db.getSiblingDB('admin').changeUserPassword('kumbu', passwordPrompt())"`), then in `.env`, then `docker compose up -d`. Removing the variable starts MongoDB without authentication again, users or not. Traffic to MongoDB is still unencrypted (no TLS) inside the Compose network.
- **The MongoDB password from a file** instead of the environment, as the embedding providers' keys are read: leave `KUMBU_MONGO_PASSWORD` unset and give both containers a Docker secret, the server with `Kumbu:Mongo:PasswordFile` and MongoDB with `KUMBU_MONGO_PASSWORD_FILE` (a line break at the end of the file is ignored; setting both `Kumbu:Mongo:Password` and `Kumbu:Mongo:PasswordFile` keeps the server from starting). In a `compose.override.yaml`:

  ```yaml
  services:
    mongo:
      environment:
        KUMBU_MONGO_PASSWORD_FILE: /run/secrets/kumbu-mongo-password
      secrets:
        - kumbu-mongo-password
    kumbu:
      environment:
        Kumbu__Mongo__PasswordFile: /run/secrets/kumbu-mongo-password
      secrets:
        - kumbu-mongo-password

  secrets:
    kumbu-mongo-password:
      file: ~/keys/.kumbu-mongo-password   # e.g. openssl rand -hex 32; readable by the server's user (uid 1654, "app")
  ```

  Everything else, the key file, the user and a later password change, is as above.

### Syncing with Obsidian

The Obsidian plugin [Kumbu² Sync](https://github.com/SCAI-NGO/kumbu-obsidian) keeps one Obsidian vault and one Kumbu² vault the same, both ways. A vault's menu has **Connect Obsidian…**: it shows the plugin to add in BRAT, this server's address, how long the device's key lasts (never, the default, or 30, 90 or 365 days) and a **pairing code** such as `K7QM-4XDA`, valid once for ten minutes. Typed into the plugin, the code becomes an API key the device keeps in Obsidian's secret storage; nobody sees or copies it. Each device gets its own key, named `<Vault> – Obsidian <device>`, with write on that one vault, listed under API keys as an **Obsidian device** and revoked there like any key. A device's writes are the user's own in the web app ("You changed this note in Obsidian (Laptop) just now"); they don't count as an agent's.

What the plugin uses, all under `/api` and in `GET /api/openapi.json` (errors are `{"error": "<sentence>"}`; every sync endpoint answers `401` for a revoked, expired or unknown key and `503` with `Retry-After` while the vault is being re-encrypted, which a client retries rather than reading the vault as gone. Re-encrypting a vault revokes every API key that has it, device keys included: once it starts, the plugin gets `401` and has to be paired again, which the Connect dialog says):

| Request | Answers |
|---|---|
| `POST /api/vaults/{vault}/pairing` (session), `{"expiresInDays": null \| 30 \| 90 \| 365}` | `201 {"code", "expiresAt"}` · `400` expiry · `403` not a session · `404` not the user's vault · `503`. A new code replaces the user's open one for the vault. |
| `POST /api/pairing/redeem` (anonymous), `{"code", "device"}` | `201 {"key", "vault", "keyName"}` · `400` device name (1 to 32 characters, nothing invisible, no parentheses; the code stays valid; a long vault name is shortened with `…` in the key's name, parentheses become brackets) · `404 This pairing code is not valid (any more).` · `409` every numbered name taken · `429` with `Retry-After` (also for every redeem while the server has seen 100 wrong codes in the last minute) · `503` while the vault is being re-encrypted (the code stays valid). The code ignores case, spaces and dashes, and reads I and L as 1 and O as 0. |
| `GET /api/vaults` | each vault with `canWrite` for the caller |
| `GET /api/vaults/{vault}/versions` | `200 {"full", "cursor", "state", "damaged", "notes": [{"path", "version"}], "deleted": []}` with `ETag: "<state>"` · `304` for `If-None-Match: "<state>"` while nothing changed · `404` · `503` |
| `GET /api/vaults/{vault}/versions?since=<cursor>` | `full: false`: every path touched since the cursor minus five minutes, at its version now in `notes` or, with no note there now, in `deleted`. A cursor the server can't answer for (from before a restart or before the vault was last unlocked, older than 25 days, in the future, garbled) gets the full list. |
| `DELETE /api/notes/{vault}/{path}?version=<v>` | `204` trashed · `409` with the note as it is now, as a stale `PUT` · `404` |
| `POST /api/ws-ticket` (API keys only) | `200 {"ticket": "kumbu_wst_…", "expiresAt"}`, for one connection within 30 seconds · `403` for a session, the dev token or a connection |
| `GET /api/ws?ticket=<ticket>` | the WebSocket: `{"type":"changed","vault":"Work"}` after each change to one of the key's vaults (at most one per vault per 500 ms), pings every 30 seconds, closed with `4401` once the key is revoked or expired (checked every 30 seconds) and `1001` when the server stops · `401` for an unknown, used or expired ticket · `400` without an upgrade |

A hint carries a vault's name and nothing else: it is a nudge to ask for the versions, so a lost or doubled one does no harm. The plugin keeps the `cursor` of each answer and asks with `since` on a hint, and for the full list on start, when Obsidian comes back to the foreground, once an hour, and when the cursor is old. Notes from the plugin are stored as written (only CRLF becomes LF), so nothing it uploads comes back changed.

### Connecting claude.ai and other apps

A **connection** is how an app that signs in with OAuth reaches your notes: claude.ai on the web, Claude Desktop and the Claude mobile apps, and other apps that add a remote MCP server by URL. Like an API key, it is made by you, for the vaults and rights you choose, and can expire; unlike a key, there is nothing secret to copy. You get a **connector URL** of its own, `https://<your instance>/mcp/c/<id>`, paste it into the app, and allow the app once on Kumbu²'s consent page. The app never sees your password.

1. **In Kumbu²:** Access → **Connect an app**. Give it a name (it becomes the writer label in the history, e.g. `claude`), tick the vaults it may read, and where it may also write, and pick an expiry or none. **Connect an app** shows the connector URL; **Copy** it. The Access page lists it from then on, with the same URL and Copy button.
2. **In claude.ai** (in a browser, also on the phone): Settings → Connectors → **Add custom connector**. Paste the URL, give it a name (e.g. `Kumbu claude`), and leave **client ID and client secret empty**: Claude identifies itself by its own metadata document.
3. Click **Connect**. The browser goes to Kumbu²'s consent page (sign in first if asked): it shows the app, where it sends you back to and the vaults with their rights. **Allow**, and you are back in Claude, connected. **Deny** sends you back without access.
4. **Use it** in a chat: turn the connector on in the chat's tools menu if it isn't, then ask, e.g. "list my Kumbu vaults". The connector belongs to your Claude account, so it is in **Claude Desktop and the mobile apps** (iOS, Android) too, with no extra step on the phone.

The Access page then shows the connection as connected, with when it was last used. **Revoke** there disconnects the app for good (its URL stops working; make a new connection to connect again). **Change rights** stores new rights and disconnects the app until it reconnects, so it never holds rights you didn't consent to.

**Reconnect.** A connection whose app must sign in again is listed with "Use Reconnect in the app": after the app revoked its own tokens, after a password change (unless you untick "Also revoke all API keys and disconnect apps"), a recovery, a re-encryption of one of its vaults or of the account, a deactivation and reactivation, a rights change, or when a refresh token turned up twice (a sign that it was copied). In claude.ai: Settings → Connectors → the connector → **Reconnect**, then Allow again. Same URL, same name, same rights (the new ones after a rights change).

Good to know:

- **Plain `/mcp` is for API keys only** (Claude Code, Hermes and other agents on your machine, as above). It has no OAuth: pasted into claude.ai, the consent page explains that you need a connection's URL. A connection's URL in turn takes no API key.
- **The app's provider holds the refresh token.** The token exchange happens between Kumbu² and the provider's servers (Anthropic for Claude; OpenAI, xAI… for theirs), so the provider keeps a refresh token that, with Kumbu²'s database, opens exactly the connection's vaults. That is consistent with what you already accept: the provider sees whatever it reads anyway. Give a connection only the vaults you'd let that provider read, and read-only where writing isn't needed. Revoke ends it at once.
- **Access tokens last an hour** by default and are refreshed by the app; `KUMBU_OAUTH_ACCESS_TOKEN_MINUTES` (5 to 1440) changes that. Every refresh replaces the refresh token too.
- The details, from the consent to the tokens' slots: `docs/security-architecture.md`, §6 "Connections (OAuth)", with a diagram of the flow and of a connection's states.

### Recovery keys

When the server creates a vault, it writes the vault's recovery key **once** to `/recovery` in the container (the `recovery-keys` volume), and nowhere else. Fetch it, keep it somewhere safe such as a password manager, and delete the file:

```sh
docker compose exec kumbu sh -c 'cat /recovery/*.recovery-key.txt'
docker compose exec kumbu sh -c 'rm /recovery/*.recovery-key.txt'
```

These are the recovery keys of vaults that open with the dev token; a user's own recovery key is shown at sign-up (see "Accounts, API keys and the REST API"), and a recovery with it also re-encrypts that user's vaults in the background. Vaults of the dev token can't be re-encrypted.

Without the dev token and without the recovery key, a vault can't be opened by anyone, including you. While some vaults don't open with the dev token, the server creates no new vaults: one of the locked ones might be the vault a folder belongs to. To change the dev token: set the new `KUMBU_DEV_TOKEN` and `KUMBU_RECOVERY_KEYS=<key>,<key>` in `.env`, start once (the log says `Recovery key opened vault …`), then remove `KUMBU_RECOVERY_KEYS` again. The old token keeps its slots.

### Privacy requests

When someone asks for their data to be deleted (GDPR Art. 17, say), the request reaches your privacy contact, and an admin carries it out in Admin → Users. This is not legal advice: your data protection officer decides what your process is.

1. **Check the requester's identity**: make sure the request really comes from the person whose account it is. Answer within **one month** (GDPR Art. 12(3)).
2. Find the user and choose **Delete for a privacy request…**. Type their username and your own password. Active users are deleted in one step: their sessions end, and their next request is refused. Everything of the account goes in one transaction: every vault and note, the history, trash and checkpoints, their API keys and app connections. The invitation they joined with stays as a used one, but loses its note and its link to them.
3. The **deletion receipt** appears. **Download receipt** saves a small text file (`kumbu-deletion-<username>-<date>.txt`) to keep as your record: the time, the username and user id, the admin who deleted, the counts of vaults, notes, API keys and app connections, the external embedding providers this instance has configured (they may have received text from vaults that used them; no copy was deleted there), and that backups are not reached. It holds no content and no vault or note names. Neither does the server log: the deletion's line names the user by id only, the admin and the counts (earlier lines, from while the user existed, such as sign-ins, keep their username).
4. **Copy reply** copies a neutral English email to send from your privacy contact: the account and its notes were deleted on that date; copies that AI tools or other apps read are outside Kumbu² and must be deleted there; backups are kept as your privacy notice describes; and, next time, a user with access can export first and delete their own account in Settings. It promises to delete the account again after a restore only when the id is on the deletion list (see below); otherwise the receipt warns you and the reply leaves that sentence out. It is signed with `KUMBU_PRIVACY_CONTACT` (`Kumbu:Privacy:Contact`, optional, e.g. `privacy@example.org`); without it, the reply ends in a placeholder to fill in.

**Deletions don't reach backups.** A backup taken before still holds the user's (sealed) data; it is kept as your privacy notice describes. So that a restore can't quietly bring a deleted user back, the server lists the ids of deleted users (random ids only, no names; an admin's deletion and a user's own) in `/deletions/deleted-users`, on the `deletions` volume, outside the database (`Kumbu:Deletions:Path`; Compose sets `/deletions`). **Keep this volume out of database restores**: restored with the database, it would forget the same deletions. The receipt says if an id couldn't be listed (the path isn't set or can't be written), and the users page says when the path isn't set; the deletion itself goes ahead either way.

**After restoring a backup**, the server compares the list with the users in the database, at start (a warning in the log: `… deleted users are back after a restore …`, ids only) and whenever an admin opens Admin → Users. If the restore brought deleted users back, the page says `A backup restore brought back <n> deleted user(s): <usernames>. Delete them again?`; **Delete again…** deletes them again in one step with your password, and gives a receipt for each (`GET /api/admin/deletions`, `POST /api/admin/deletions/repeat` with `{"password": …}`).

### External embedding providers

Search by meaning runs on the server by default (`KUMBU_SEARCH_MODEL`, arctic-m or arctic-l). An admin can also offer **external embedding providers**: services with the OpenAI embeddings API (`POST {BaseUrl}/embeddings`). Configuring one sends nothing anywhere. Each vault's owner then chooses, in the web app (the vault's ⋯ menu in the drawer, "Search by meaning…") or with `PUT /api/vaults/{vault}/embeddings` (body `{"provider": "tensorx"}`, sessions only; `GET /api/embeddings` lists the providers), whether that vault uses one. From then on **the text of every section of that vault and every search query over it is sent to the provider**; the vault is embedded again in the background, and found by words until it is. What the provider keeps is its promise, not something Kumbu² can enforce; the web app says so before the switch. Switching back to `internal` stops the sending. Details: `docs/security-architecture.md` §5.5.

A provider is configuration under `Kumbu:Embeddings:Providers:<name>` (environment: `Kumbu__Embeddings__Providers__<name>__<Setting>`), with `<name>` of lowercase letters, digits and `-` (not `internal`):

| Setting | Meaning | Example |
|---|---|---|
| `Driver` | The protocol; `openai-compatible` | `openai-compatible` |
| `BaseUrl` | The API's base; `https://` (plain `http://` only to a loopback address) | `https://api.tensorx.ai/v1` |
| `Model` | The model to ask for | `qwen/qwen3-embedding-8b` |
| `ApiKeyFile` | A file holding the API key (a Docker secret); never the key itself in the environment. Optional only for a loopback provider. Read once at startup | `/run/secrets/tensorx-api-key` |
| `Dimensions` | Optional: the vectors' length to ask for; unset, the model's own | `1024` |
| `QueryPrompt` | Optional: put before every search query, for models that want an instruction | see below |
| `Label`, `Description` | What owners see before they choose; the description is the provider's promise in a few words, as the provider states it | `TensorX`, `As TensorX states: EU (Ireland), zero data retention` |
| `MaxBatch`, `TimeoutSeconds` | Optional: texts per request (default 32), seconds per request (default 30) | `32`, `30` |

The server refuses to start if a provider's URL isn't https, its key file is missing or empty, or a setting is out of range. Failed requests are retried (timeouts, 429, 5xx, with backoff; a search waits at most 5 s for a provider, asks all providers at once, and otherwise ranks by words); a provider that keeps failing pauses for a minute; its vaults are searched by words meanwhile (in a search across vaults, merged by rank with the others' results by meaning), and the search says so. Switching or removing a provider re-embeds; a provider removed from the configuration leaves its vaults without search by meaning until their owner picks another. `KUMBU_SEARCH=false` switches off the internal model only. Across vaults on different models, each vault's sections are scored by their own model and ranked together; measured on the evaluation corpus, that costs about one query in 37 against the better model alone (`docs/notes-search-external.md`).

**Example: TensorX** with Qwen3-Embedding 8B, as evaluated (`docs/notes-search-external.md`: as good as arctic-l with the query prompt, a quarter worse without it). Put the key in a file outside the repository, then add a `compose.override.yaml` next to `compose.yaml` (Compose reads it automatically; with `COMPOSE_FILE` set, append `:compose.override.yaml`):

```yaml
services:
  kumbu:
    environment:
      Kumbu__Embeddings__Providers__tensorx__Driver: openai-compatible
      Kumbu__Embeddings__Providers__tensorx__BaseUrl: https://api.tensorx.ai/v1
      Kumbu__Embeddings__Providers__tensorx__Model: qwen/qwen3-embedding-8b
      Kumbu__Embeddings__Providers__tensorx__Dimensions: "1024"
      Kumbu__Embeddings__Providers__tensorx__QueryPrompt: "Instruct: Given a search query, retrieve the note sections that answer it\nQuery: "
      Kumbu__Embeddings__Providers__tensorx__ApiKeyFile: /run/secrets/tensorx-api-key
      Kumbu__Embeddings__Providers__tensorx__Label: TensorX
      Kumbu__Embeddings__Providers__tensorx__Description: "As TensorX states: EU (Ireland), zero data retention"
    secrets:
      - tensorx-api-key

secrets:
  tensorx-api-key:
    file: ~/keys/.tensorx-api-key   # readable by the container's user (uid 1654, "app"), e.g. mode 644 or group-readable
```

`docker compose up -d` again; the providers appear in the vault's menu. A new key in the file needs a restart (`docker compose restart kumbu`).

## Quickstart

```sh
cp .env.example .env              # then set KUMBU_DEV_TOKEN (openssl rand -hex 32), or leave it empty and use accounts
mkdir -p vaults
cp -r samples/* vaults/           # first try: the sample vaults Demo and Ideas
docker compose up --build
curl localhost:8080/healthz       # ok
docker compose exec kumbu sh -c 'cat /recovery/*.recovery-key.txt'   # store these keys safely
```

Building from source needs the override `compose.build.yaml`: `.envrc` selects it (`export COMPOSE_FILE=compose.yaml:compose.build.yaml`; run `direnv allow`), and without direnv put the same `COMPOSE_FILE=…` line in `.env` (it is in `.env.example`). Without it, `compose.yaml` alone pulls the published image `ghcr.io/scai-ngo/kumbukumbu:latest` (see Images and releases), and `--build` has nothing to build. The commands below that say `--build` assume the override.

For the web app, set `KUMBU_PUBLIC_URL` in `.env` to the address you open it at (e.g. `http://localhost:8080`; run `docker compose up -d` again so it takes effect), then create the first admin and open the link it prints, `…/join#<code>`:

```sh
docker compose exec kumbu kumbu bootstrap-admin
```

After joining you see the recovery key once; keep it safe. Then open `http://localhost:8080/` (or your `KUMBU_PORT`) and sign in.

Your own notes go in `./vaults/<VaultName>/`, one folder per vault. Good to know:

- **Search by meaning needs the embedding model** ([snowflake-arctic-embed-m-v2.0](https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v2.0), 16-bit, about 630 MB). The server downloads it once on the first start into the `models` volume, pinned to one revision and checked against its SHA-256, and runs it in-process with ONNX Runtime; after that it works offline. Until it is loaded, and with `KUMBU_SEARCH=false` (which switches off the internal model only: vaults opted in to an external provider keep searching by meaning), `semsearch` ranks by words and says so. Indexing runs in the background after every write and when a vault is unlocked; a note takes some tens of milliseconds per section on a CPU.
- **MongoDB runs as a single-node replica set**, since it has transactions only then. `compose.yaml` sets this up and needs no manual step: a fixed host name and a health check that initiates the set on the first start. Running MongoDB yourself, start it with `--replSet rs0`, initiate the set, and add `?directConnection=true` to the connection string; a standalone MongoDB is refused with a message that says so.
- **MongoDB is the source of truth.** Agents' changes live there; Kumbu² never writes back to `./vaults`.
- Seeding is for starting from scratch: at startup each folder becomes an encrypted vault (an empty folder an empty vault), or fills one that is still empty. A vault that already holds notes (or trash) is left alone, so later changes on disk are not loaded. One catch: a vault whose notes were all removed, with the trash expired after 30 days, counts as empty and is filled again on the next start. To load the folders again, `docker compose down -v` wipes the database, **including every change agents made and the vaults' keys**; the next start creates new vaults with new recovery keys.
- Upgrading from before encryption: the server refuses plaintext data from earlier versions and says so. Wipe it with `docker compose down -v`.
- Symlinked vault folders inside `./vaults` do not resolve inside the container. Point `KUMBU_VAULTS` in `.env` at the real folder instead, for example `KUMBU_VAULTS=/home/me/Notes`.
- The container runs as an unprivileged user (UID 1654). Your files must be readable by it; unreadable files are skipped with a warning in the log.
- If the `mongo` container exits with "MongoDB cannot start: Linux kernel versions 6.19 and newer has a known incompatibility", your kernel is in the range 6.19–7.0.13, where MongoDB 8.x refuses to start ([SERVER-125742](https://jira.mongodb.org/browse/SERVER-125742)). Set `KUMBU_MONGO_IMAGE=mongo:7.0` in `.env`. The tests use `mongo:7.0` for the same reason.

Connect Claude Code:

```sh
claude mcp add --transport http kumbu http://localhost:8080/mcp --header "Authorization: Bearer <token>"
```

MCP clients read the tool descriptions when a session starts. To upgrade, keep this order:

```sh
git pull
docker compose up --build -d     # rebuild the image; a plain restart keeps running the old server
```

Then start a new client session. Without the rebuild, a restarted client loads the old server's tool descriptions; without the new session, the agent keeps working from the old command list.

Configuration (environment variables use `__` for `:`): `Kumbu:DevToken` (optional; if set, at least 16 characters; without it the dev token opens nothing, and seeding and recovery keys are skipped), `Kumbu:PublicUrl` (`KUMBU_PUBLIC_URL`; an absolute http(s) address without a path; invitation links are built from it, `bootstrap-admin` needs it, and it is required when `Kumbu:Proxy:Trusted` is set; if it is https, every response carries `Strict-Transport-Security: max-age=31536000; includeSubDomains`, so pointing it at an apex domain such as `https://example.org` pins every subdomain of it to https for a year), `Kumbu:Proxy:Trusted` (`KUMBU_TRUSTED_PROXY`, default false; behind a reverse proxy such as Traefik, take the client IP for rate limits from the last `X-Forwarded-For` entry and the scheme from `X-Forwarded-Proto`; set it only when the proxy is the server's only way in, since any peer is trusted as the proxy; the server refuses to start with it unless `Kumbu:PublicUrl` is set, since the web app's requests are checked against that address), `Kumbu:Mongo:ConnectionString` (required), `Kumbu:Mongo:Database` (default `kumbu`), `Kumbu:Mongo:User` (default `kumbu`) and `Kumbu:Mongo:Password` (optional; set, the server authenticates as that user against MongoDB's `admin` database, without the password in the connection string) or `Kumbu:Mongo:PasswordFile` (a file holding that password, e.g. `/run/secrets/kumbu-mongo-password`; see above), `Kumbu:Seed:Path` (folder to load vaults from; if it does not exist the server logs an error and starts without seeding), `Kumbu:Recovery:Path` (directory new vaults' recovery keys are written to; without it vaults get none), `Kumbu:Recovery:Keys` (recovery keys to give the dev token access at startup), `Kumbu:Deletions:Path` (directory the ids of deleted users are listed in, outside the database; `/deletions` in Compose, on its own volume; without it deletions aren't listed and a restore that brings a deleted user back goes unnoticed), `Kumbu:Vaults:IdleMinutes` (default 15) and `Kumbu:Vaults:MemoryLimitMegabytes` (default 512) for unlocked vaults, `Kumbu:Vaults:CheckpointLimit` (default 10) for named checkpoints per vault, `Kumbu:OAuth:AccessTokenMinutes` (`KUMBU_OAUTH_ACCESS_TOKEN_MINUTES`, default 60, 5 to 1440) for the lifetime of an app connection's access token, `Kumbu:Privacy:Contact` (`KUMBU_PRIVACY_CONTACT`, optional; an address or a name, e.g. `privacy@example.org`) that the reply to a privacy request is signed with (see "Privacy requests"), `Kumbu:OAuth:AllowLoopbackClients` (default false; **tests and development only**: lets a client metadata document come over plain `http` from this machine, from any host that resolves to a loopback address, which switches off part of the fetch's protection against reaching internal addresses; the server refuses to start with it in the `Production` environment, which the image runs in unless `ASPNETCORE_ENVIRONMENT` says otherwise), `Kumbu:Search:Enabled` (default true), `Kumbu:Search:ModelPath` (default `models`; `/models` in Compose), `Kumbu:Search:Model` (`arctic-m`, the default, or `arctic-l`: 613 MB and a server of about 1 GB at about 26 ms per section, or 1.13 GB and about 3 GB at about 49 ms, measured on the test stack; each is kept in its own folder below the model path, and switching re-embeds every note when its vault is next unlocked, while search ranks the notes not yet re-embedded by words; the other model's folder stays, so switching back needs no download: `docker compose exec kumbu rm -r /models/arctic-l` frees it) and `Kumbu:Search:Threads` (default 0: half the cores) for search by meaning (`Kumbu:Search:Enabled=false` switches off the internal model only), and `Kumbu:Embeddings:Providers:<name>:…` for external embedding providers (see "External embedding providers"). With Compose, set `KUMBU_DEV_TOKEN` (may be empty) and optionally `KUMBU_PUBLIC_URL`, `KUMBU_VAULTS`, `KUMBU_IMAGE` (server image, default `ghcr.io/scai-ngo/kumbukumbu:latest`; e.g. a pinned version), `KUMBU_MONGO_IMAGE`, `KUMBU_PORT` (host port, default 8080), `KUMBU_MONGO_PASSWORD` (MongoDB authentication, see above), `KUMBU_RECOVERY_KEYS`, `KUMBU_TRUSTED_PROXY`, `KUMBU_CHECKPOINT_LIMIT`, `KUMBU_OAUTH_ACCESS_TOKEN_MINUTES`, `KUMBU_PRIVACY_CONTACT`, `KUMBU_SEARCH` and `KUMBU_SEARCH_MODEL` in `.env`.

## Development

Any .NET 10 SDK works (`global.json` rolls forward from 10.0.100, so distribution builds in the 1xx band are fine).

```sh
dotnet test                        # all tests; the Mongo tests need Docker or KUMBU_TEST_MONGO
dotnet test tests/Kumbu.Core.Tests # core only, no Docker
KUMBU_MODEL_DIR=/path/to/models dotnet test   # also the tests against the real model and tokenizer
```

Without Docker, point the Mongo tests at a MongoDB 7.0 replica set, or a single `mongod --replSet rs0` (the tests initiate the set if it is not yet), with `KUMBU_TEST_MONGO='mongodb://127.0.0.1:27017/?directConnection=true' dotnet test`. Raise the open-files limit first (`ulimit -n 64000`): every test database is a set of files that mongod keeps open. `directConnection=true` is required, since a node that is not yet initiated cannot be discovered otherwise.

`KUMBU_MODEL_DIR` holds one folder per model, as the server keeps them below `Kumbu:Search:ModelPath`:

```
$KUMBU_MODEL_DIR/
  arctic-m/tokenizer.json
  arctic-m/model_fp16.onnx
  arctic-l/tokenizer.json
  arctic-l/model_fp16.onnx
```

Without it the tests use a stand-in embedder and skip the three tests that need the real files: the tokenizer test of each model and the ranking test against arctic-m. A directory in the layout of step 7 (arctic-m's files directly in it) is moved into `arctic-m/` when the server starts (`ModelFiles.AdoptStep7Layout`); the tests don't move it and skip until then.

### The web app

Needs Node 22.4+ (the test script uses a flag from that version) and pnpm. Details are in [web/README.md](web/README.md).

```sh
pnpm -C web install
pnpm -C web dev       # http://localhost:5173, proxies /api and /mcp to the server on :8083
pnpm -C web test      # Vitest
pnpm -C web build     # type check and build into web/dist
pnpm -C web gen:api   # regenerate src/api/schema.d.ts from a running server
```

The dev server proxies to `http://localhost:8083`, so start the stack with `KUMBU_PORT=8083` (or change `server` in `web/vite.config.ts`). The server refuses a POST, PUT or DELETE from a browser unless its `Origin` header matches `KUMBU_PUBLIC_URL` (or, when that is unset, the request's own host). The proxy keeps the browser's host, so the Vite page at `:5173` fails with `403` ("This request didn't come from Kumbu's own pages") if `KUMBU_PUBLIC_URL` points elsewhere. For development either leave `KUMBU_PUBLIC_URL` empty or set it to `http://localhost:5173`; `bootstrap-admin` needs it, so for the first admin use the second and open the printed link as `http://localhost:5173/join#…`. Rebuild with `docker compose up -d --build` after changing it. `gen:api` reads `http://localhost:8083/api/openapi.json`; set `KUMBU_OPENAPI_URL` to use another address.

The web build takes its version from `KUMBU_VERSION` and `KUMBU_GIT_SHA` (`KUMBU_VERSION=0.2.0 KUMBU_GIT_SHA=a6362ec pnpm -C web build`), as the Dockerfile passes them with the server's `-p:Version` and `-p:SourceRevisionId`. Give an image build both `VERSION` and `GIT_SHA` (the release workflow does): with `VERSION` alone the server reports it, but the web app counts as a development build. Without both, as in `pnpm -C web dev` and local builds, the app reports `0.0.0-dev`, and a version without a commit or starting with `0.0.0-dev` on either side turns update detection off; so does a server without the header. A local image build (`docker compose -f compose.yaml -f compose.build.yaml up -d --build`) reports `0.0.0-dev` on both sides.

## Images and releases

GitHub Actions (`.github/workflows/docker.yml`) builds the image from the root `Dockerfile` and publishes it to `ghcr.io/scai-ngo/kumbukumbu`:

| Event | Result | Tags |
| --- | --- | --- |
| Pull request | built, not pushed | none |
| Push to `master` | built, pushed, signed | `master`, `sha-<commit>` |
| Tag `vX.Y.Z` | built, pushed, signed | `X.Y.Z`, `X.Y`, `X`, `sha-<commit>`, `latest` |

`:latest` moves **only** on a `v*.*.*` tag, never on a push to `master`, so a server that follows `:latest` (Komodo on the VPS does) updates on a deliberate release, not on every merge. Builds that are not a tag report themselves as `<version.txt>-pre.<run number>`; `version.txt` holds the next release's base version. The image carries its version in `/app/VERSION`: `docker run --rm --entrypoint cat ghcr.io/scai-ngo/kumbukumbu:latest /app/VERSION`.

To release, set `version.txt` to the next version if it has changed, merge to `master`, and tag:

```sh
git tag v0.1.0 && git push origin v0.1.0
```

A new GHCR package is private. For a server to pull it without credentials, make it public once (GitHub, the organisation's Packages, `kumbukumbu`, Package settings, Change visibility).

Images are signed with cosign (keyless, through GitHub's OIDC identity). To verify one, with the tag or digest you pull:

```sh
cosign verify ghcr.io/scai-ngo/kumbukumbu:latest \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github.com/SCAI-NGO/kumbukumbu/\.github/workflows/docker\.yml@'
```

## More

- [docs/concept.md](docs/concept.md): the full concept and threat model
- [CONCEPTS.md](CONCEPTS.md): a growing glossary of what was learned while building this

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
