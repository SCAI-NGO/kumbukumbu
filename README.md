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

The server also serves a web app, built from `web/` into the image. It works on a phone first and on a laptop too: sign in, browse vaults and folders, read and edit notes in Markdown (saved automatically, with a conflict prompt if an agent wrote in the meantime), search by words and meaning, upload Markdown files or folders, make API keys, and, for admins, manage users and invitations. It talks only to the REST API, with the same-origin session cookie. Notes are text only for now.

## Security

**Notes are encrypted at rest.** Every vault has its own random key. Notes, their history, the trash, move records and even paths and vault names are sealed with AES-256-GCM before they reach MongoDB. A database dump or a stolen backup shows random ids, keyed hashes of paths, revision numbers, sizes and times, nothing readable. The vault key itself is stored only **wrapped** in key slots: one for the dev token (HKDF-SHA256) and one for the vault's **recovery key**. An unlocked vault is held in memory while it's used, and locked again after 15 idle minutes.

Be clear about what this does not cover (concept §3):

- Vaults that open with the dev token (the seeded ones, a single-user setup) are only as safe as the server's configuration: whoever has **both** the database and `.env` can read them. A vault that belongs to a user opens only with that user's password or recovery key (see Accounts); the dev token does not open it. Encryption protects the database and its backups, not a machine where both are stored.
- The running server sees plaintext in memory; that's how search works. An operator of the running server is trusted. The embedding model runs inside the server process, so notes never go to an outside service to be embedded, unless the admin configured an external embedding provider **and** a vault's owner chose it for that vault (below); the embeddings are sealed like the notes either way.
- The AI provider of a connected client sees what the agent reads. This is not end-to-end encryption.

Every request under `/mcp` and `/api` needs a **caller**, one of: a **session** (cookie `kumbu_session`, accepted under `/api` only; `/mcp` takes bearer keys only), an **API key** (`Authorization: Bearer kumbu_…`), or the optional **dev token** (also a bearer). Without one, `/api` answers `401` with a JSON `{"error": …}`. `GET /healthz`, `GET /api/openapi.json`, the sign-in, join, recover and invitation-peek endpoints, and paths outside `/mcp` and `/api` need none. After 20 wrong bearer keys in a minute from one client network, every bearer key from it gets `429` until the minute has passed. Traffic is plain HTTP; put TLS in front (below). The dev token is optional: without it, it opens nothing, which is what a public instance wants. If set, the server refuses to start unless it is at least 16 characters; use a random one (`openssl rand -hex 32`), since the vault keys of seeded vaults are derived from it. Compose publishes port 8080 (or `KUMBU_PORT`) on **localhost only** (`127.0.0.1:${KUMBU_PORT:-8080}:8080` in `compose.yaml`). To reach it from other machines, remove the `127.0.0.1:` prefix deliberately, knowing that anyone on the network who learns a key can read and change what it opens.

### Accounts, API keys and the REST API

Users sign in with a username and a password (at least 12 characters, not a common one) and own their vaults. Each user has a **user key** that opens their vaults; the password and the **recovery key** shown once at sign-up are two ways to unwrap it. An admin sees who exists and how many vaults and bytes they hold, never a note, and cannot reset a password: lose both password and recovery key, and the vaults are gone. Deactivating a user ends their sessions and revokes their API keys at once. A new recovery key (`POST /api/recovery-key`, body `{"currentPassword": …}`) needs the current password, as a password change needs the old one, so a stolen session alone can't make a recovery key and use it to set a new password; a wrong password answers `400`, like a wrong old password at `POST /api/password`. **Changing the password revokes every API key** unless you untick "Also revoke all API keys" (`POST /api/password` with `"revokeApiKeys": false`; left out, it is `true`); it does not replace the recovery key. **A recovery** (`POST /api/recover`) always revokes every API key and replaces the recovery key: the used one stops working, and the answer, `{"recoveryKey": …}`, carries the new one, shown once. After a suspected compromise, change the password with the box ticked, give the agents you still use new keys, and make a new recovery key. How this works and why: CONCEPTS.md, chapter 10.

**The first admin.** On a public instance there is no seeded user. Set `KUMBU_PUBLIC_URL` (the address users reach the server at, e.g. `https://kumbu.example.org`, no path), start the server and run:

```sh
docker compose exec kumbu kumbu bootstrap-admin
```

It prints a one-time link `https://kumbu.example.org/join#<code>` (on stdout, never in the log), valid 24 hours. Opening it creates the **primary admin**; running the command again replaces the link; once a primary admin exists the command is refused. Further users come in through invitations from the admin screen or `POST /api/admin/invitations` (single use, 7 days by default, at most 30).

**API keys** (`kumbu_…`, 32 random bytes in Base62) are made by a signed-in user, are **shown once**, and carry per-vault rights: read, or read and write. A key opens exactly the vaults it was given; a read-only key is refused every write, including `replace` and the folder `restore --checkpoint` preview. The key's name becomes the writer label in the history, so the name may not be the user's own username, `dev` or `seed`, contain " (", control characters or invisible formatting characters (zero-width spaces, bidi overrides), and has at most 64 characters. Use a key with Claude Code the way the dev token is used:

```sh
claude mcp add --transport http kumbu http://localhost:8080/mcp --header "Authorization: Bearer kumbu_…"
```

**The REST API** lives under `/api` and serves the web app and, later, the Obsidian plugin; notes are addressed by vault and path, and a write carries the version token from the read. `GET /api/openapi.json` describes it. The session cookie is `HttpOnly`, `Secure` and `SameSite=Strict`, lasts 30 idle minutes and 12 hours at most, and survives a server restart. A browser needs HTTPS for it (localhost counts as secure in browsers), so a public instance runs behind a TLS-terminating proxy.

**Security notes for a public instance:**

- Without `KUMBU_TRUSTED_PROXY`, the server sees the proxy's address for every client. Behind Traefik that means all clients share one IP, so one attacker's wrong keys lock **all** bearer users out for a minute. A CDN in front of Traefik has the same effect, since the server then sees the CDN's addresses. Set `KUMBU_TRUSTED_PROXY=true` only when the proxy is the container's only way in (no published port): the server then takes the client address from the last `X-Forwarded-For` entry the proxy appended, and whether the request came over https from `X-Forwarded-Proto` (for `Strict-Transport-Security`). It refuses to start with it unless `KUMBU_PUBLIC_URL` is set.
- Sign-in is limited to 5 attempts a minute per username and 20 per client network. That also means anyone who knows a username can lock that user out of signing in for a minute. This is a deliberate trade-off against guessing passwords; bearer keys and sessions already issued are not affected.
- Checking the current password, for a password change or a new recovery key, is limited to 5 attempts a minute per username (the two share the budget, apart from sign-in's), so a stolen session can't guess the password faster than signing in could.
- Argon2 (64 MiB and a core per run) runs at most once per core at a time across the server; further sign-ins wait their turn, so many sign-ins from many addresses at once slow down instead of exhausting memory.
- The limiters sit in memory and count per client network (an IPv6 address by its /64), each for about a minute after its last failure, so their memory grows with the number of distinct networks that failed within that minute.
- Invitation codes travel in the link's fragment (`#…`), which browsers do not send, so they never reach the server's or the proxy's logs.
- A reverse proxy's **access log** holds what the server's own log leaves out: request lines under `/api` name vaults and note paths (`/api/notes/Work/plan.md`) and carry search queries (`/api/search?q=…`). On a public instance, disable the access log for `/api` or filter those paths and the query string out. The server's own log names vaults by id and logs no note path; only seeding names the folders and files it reads from `Kumbu:Seed:Path`.
- Without a dev token, the server logs a warning at every start (`Kumbu:DevToken is not set: neither Kumbu:Seed:Path nor Kumbu:Recovery:Keys is applied`) and seeds nothing. On a public instance this is expected.

### Recovery keys

When the server creates a vault, it writes the vault's recovery key **once** to `/recovery` in the container (the `recovery-keys` volume), and nowhere else. Fetch it, keep it somewhere safe such as a password manager, and delete the file:

```sh
docker compose exec kumbu sh -c 'cat /recovery/*.recovery-key.txt'
docker compose exec kumbu sh -c 'rm /recovery/*.recovery-key.txt'
```

Without the dev token and without the recovery key, a vault can't be opened by anyone, including you. While some vaults don't open with the dev token, the server creates no new vaults: one of the locked ones might be the vault a folder belongs to. To change the dev token: set the new `KUMBU_DEV_TOKEN` and `KUMBU_RECOVERY_KEYS=<key>,<key>` in `.env`, start once (the log says `Recovery key opened vault …`), then remove `KUMBU_RECOVERY_KEYS` again. The old token keeps its slots.

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
| `Label`, `Description` | What owners see before they choose; the description is the provider's promise in a few words | `TensorX`, `EU (Ireland), zero data retention by policy` |
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
      Kumbu__Embeddings__Providers__tensorx__Description: EU (Ireland), zero data retention by policy
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

- **Search by meaning needs the embedding model** ([snowflake-arctic-embed-m-v2.0](https://huggingface.co/Snowflake/snowflake-arctic-embed-m-v2.0), 16-bit, about 630 MB). The server downloads it once on the first start into the `models` volume, pinned to one revision and checked against its SHA-256, and runs it in-process with ONNX Runtime; after that it works offline. Until it is loaded, and with `KUMBU_SEARCH=false`, `semsearch` ranks by words and says so. Indexing runs in the background after every write and when a vault is unlocked; a note takes some tens of milliseconds per section on a CPU.
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

Configuration (environment variables use `__` for `:`): `Kumbu:DevToken` (optional; if set, at least 16 characters; without it the dev token opens nothing, and seeding and recovery keys are skipped), `Kumbu:PublicUrl` (`KUMBU_PUBLIC_URL`; an absolute http(s) address without a path; invitation links are built from it, `bootstrap-admin` needs it, and it is required when `Kumbu:Proxy:Trusted` is set; if it is https, every response carries `Strict-Transport-Security: max-age=31536000; includeSubDomains`, so pointing it at an apex domain such as `https://example.org` pins every subdomain of it to https for a year), `Kumbu:Proxy:Trusted` (`KUMBU_TRUSTED_PROXY`, default false; behind a reverse proxy such as Traefik, take the client IP for rate limits from the last `X-Forwarded-For` entry and the scheme from `X-Forwarded-Proto`; set it only when the proxy is the server's only way in, since any peer is trusted as the proxy; the server refuses to start with it unless `Kumbu:PublicUrl` is set, since the web app's requests are checked against that address), `Kumbu:Mongo:ConnectionString` (required), `Kumbu:Mongo:Database` (default `kumbu`), `Kumbu:Seed:Path` (folder to load vaults from; if it does not exist the server logs an error and starts without seeding), `Kumbu:Recovery:Path` (directory new vaults' recovery keys are written to; without it vaults get none), `Kumbu:Recovery:Keys` (recovery keys to give the dev token access at startup), `Kumbu:Vaults:IdleMinutes` (default 15) and `Kumbu:Vaults:MemoryLimitMegabytes` (default 512) for unlocked vaults, `Kumbu:Vaults:CheckpointLimit` (default 10) for named checkpoints per vault, `Kumbu:Search:Enabled` (default true), `Kumbu:Search:ModelPath` (default `models`; `/models` in Compose), `Kumbu:Search:Model` (`arctic-m`, the default, or `arctic-l`: 613 MB and a server of about 1 GB at about 26 ms per section, or 1.13 GB and about 3 GB at about 49 ms, measured on the test stack; each is kept in its own folder below the model path, and switching re-embeds every note when its vault is next unlocked, while search ranks the notes not yet re-embedded by words; the other model's folder stays, so switching back needs no download: `docker compose exec kumbu rm -r /models/arctic-l` frees it) and `Kumbu:Search:Threads` (default 0: half the cores) for search by meaning (`Kumbu:Search:Enabled=false` switches off the internal model only), and `Kumbu:Embeddings:Providers:<name>:…` for external embedding providers (see "External embedding providers"). With Compose, set `KUMBU_DEV_TOKEN` (may be empty) and optionally `KUMBU_PUBLIC_URL`, `KUMBU_VAULTS`, `KUMBU_IMAGE` (server image, default `ghcr.io/scai-ngo/kumbukumbu:latest`; e.g. a pinned version), `KUMBU_MONGO_IMAGE`, `KUMBU_PORT` (host port, default 8080), `KUMBU_RECOVERY_KEYS`, `KUMBU_TRUSTED_PROXY`, `KUMBU_CHECKPOINT_LIMIT`, `KUMBU_SEARCH` and `KUMBU_SEARCH_MODEL` in `.env`.

## Development

Any .NET 10 SDK works (`global.json` rolls forward from 10.0.100, so distribution builds in the 1xx band are fine).

```sh
dotnet test                        # all tests; the Mongo tests need Docker
dotnet test tests/Kumbu.Core.Tests # core only, no Docker
KUMBU_MODEL_DIR=/path/to/models dotnet test   # also the tests against the real model and tokenizer
```

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
