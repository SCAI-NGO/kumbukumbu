# Kumbukumbu (Kumbu²)

*kumbukumbu* is Swahili for memory, remembrance, also records or minutes. It comes from *kumbuka*, "to remember". Repeating the word intensifies it: kumbu × kumbu = kumbu². The superscript also reads as "second", as in second brain.

Kumbu² is a self-hostable second brain for humans that AI agents may use. Notes are plain Markdown, kept in MongoDB, and agents reach them through an MCP server. It is your memory; the AI gets a key.

## Status: reading, searching by words and meaning, writing, encrypted at rest

The server offers four MCP tools. `sh` is a shell over your notes:

- `ls [path]` lists a directory (no path lists the vaults, and says if vaults don't open with the token or notes are damaged)
- `cat <path>` prints a note, with its **version token** in a separate block; `cat "<path>#Heading"` prints one section (nested: `#Parent#Child`)
- `outline <path>` lists a note's headings with line ranges and sizes
- `rg [options] <regex> [path...]` searches notes and prints `path:line:text`; without a path it searches every vault
- `semsearch <query> [path]` finds the sections that best match a question or topic, ranked by words (BM25) and by meaning (embeddings, also across languages), merged with Reciprocal Rank Fusion; `--words` or `--meaning` uses one ranking only
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

Paths are always absolute, for example `/Work/projects/plan.md`. Top-level folders are vaults. Links resolve like in Obsidian, within the note's vault: the note whose path ends with the link, case-insensitive; ambiguous links are reported, never guessed. Kumbu² never rewrites note content on its own; only `mv` changes links. Markdown links (`[text](note.md)`) are not followed yet. There is no web UI and no user management yet.

## Security

**Notes are encrypted at rest.** Every vault has its own random key. Notes, their history, the trash, move records and even paths and vault names are sealed with AES-256-GCM before they reach MongoDB. A database dump or a stolen backup shows random ids, keyed hashes of paths, revision numbers, sizes and times, nothing readable. The vault key itself is stored only **wrapped** in key slots: one for the dev token (HKDF-SHA256) and one for the vault's **recovery key**. An unlocked vault is held in memory while it's used, and locked again after 15 idle minutes.

Be clear about what this does not cover (concept §3):

- Until there are users and API keys (build order step 8), the dev token unlocks every vault, and it lives in the server's configuration (`.env`). Whoever has **both** the database and that file can read everything. Encryption protects the database and its backups, not a machine where both are stored.
- The running server sees plaintext in memory; that's how search works. An operator of the running server is trusted. The embedding model runs inside the server process, so notes never go to an outside service to be embedded; the embeddings are sealed like the notes.
- The AI provider of a connected client sees what the agent reads. This is not end-to-end encryption.

Access is guarded by the **single shared dev token**, sent as `Authorization: Bearer <token>`. Every path requires it except `GET /healthz`. Traffic is plain HTTP. The server refuses to start unless the token is at least 16 characters; use a random one (`openssl rand -hex 32`), since the vault keys are derived from it. Compose publishes port 8080 (or `KUMBU_PORT`) on **localhost only** (`127.0.0.1:${KUMBU_PORT:-8080}:8080` in `compose.yaml`). To reach it from other machines, remove the `127.0.0.1:` prefix deliberately, knowing that anyone on the network who learns the token can read and change every note.

### Recovery keys

When the server creates a vault, it writes the vault's recovery key **once** to `/recovery` in the container (the `recovery-keys` volume), and nowhere else. Fetch it, keep it somewhere safe such as a password manager, and delete the file:

```sh
docker compose exec kumbu sh -c 'cat /recovery/*.recovery-key.txt'
docker compose exec kumbu sh -c 'rm /recovery/*.recovery-key.txt'
```

Without the dev token and without the recovery key, a vault can't be opened by anyone, including you. While some vaults don't open with the dev token, the server creates no new vaults: one of the locked ones might be the vault a folder belongs to. To change the dev token: set the new `KUMBU_DEV_TOKEN` and `KUMBU_RECOVERY_KEYS=<key>,<key>` in `.env`, start once (the log says `Recovery key opened vault …`), then remove `KUMBU_RECOVERY_KEYS` again. The old token keeps its slots until revoking comes with step 8.

## Quickstart

```sh
cp .env.example .env              # then set KUMBU_DEV_TOKEN (openssl rand -hex 32)
mkdir -p vaults
cp -r samples/* vaults/           # first try: the sample vaults Demo and Ideas
docker compose up --build
curl localhost:8080/healthz       # ok
docker compose exec kumbu sh -c 'cat /recovery/*.recovery-key.txt'   # store these keys safely
```

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

Configuration (environment variables use `__` for `:`): `Kumbu:DevToken` (required, at least 16 characters), `Kumbu:Mongo:ConnectionString` (required), `Kumbu:Mongo:Database` (default `kumbu`), `Kumbu:Seed:Path` (folder to load vaults from; if it does not exist the server logs an error and starts without seeding), `Kumbu:Recovery:Path` (directory new vaults' recovery keys are written to; without it vaults get none), `Kumbu:Recovery:Keys` (recovery keys to give the dev token access at startup), `Kumbu:Vaults:IdleMinutes` (default 15) and `Kumbu:Vaults:MemoryLimitMegabytes` (default 512) for unlocked vaults, `Kumbu:Vaults:CheckpointLimit` (default 10) for named checkpoints per vault, `Kumbu:Search:Enabled` (default true), `Kumbu:Search:ModelPath` (default `models`; `/models` in Compose) and `Kumbu:Search:Threads` (default 0: half the cores) for search by meaning. With Compose, set `KUMBU_DEV_TOKEN` and optionally `KUMBU_VAULTS`, `KUMBU_MONGO_IMAGE`, `KUMBU_PORT` (host port, default 8080), `KUMBU_RECOVERY_KEYS`, `KUMBU_CHECKPOINT_LIMIT` and `KUMBU_SEARCH` in `.env`.

## Development

Any .NET 10 SDK works (`global.json` rolls forward from 10.0.100, so distribution builds in the 1xx band are fine).

```sh
dotnet test                        # all tests; the Mongo tests need Docker
dotnet test tests/Kumbu.Core.Tests # core only, no Docker
KUMBU_MODEL_DIR=/path/to/models dotnet test   # also the tests against the real model and tokenizer
```

Without `KUMBU_MODEL_DIR` the tests use a stand-in embedder and skip the two tests that need the real model files (`tokenizer.json`, `model_fp16.onnx`).

## More

- [docs/concept.md](docs/concept.md): the full concept and threat model
- [CONCEPTS.md](CONCEPTS.md): a growing glossary of what was learned while building this

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
