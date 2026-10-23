# Kumbukumbu (Kumbu²)

*kumbukumbu* is Swahili for memory, remembrance, also records or minutes. It comes from *kumbuka*, "to remember". Repeating the word intensifies it: kumbu × kumbu = kumbu². The superscript also reads as "second", as in second brain.

Kumbu² is a self-hostable second brain for humans that AI agents may use. Notes are plain Markdown, kept in MongoDB, and agents reach them through an MCP server. It is your memory; the AI gets a key.

## Status: reading, searching, writing, encrypted at rest

The server offers three MCP tools. `sh` is a shell over your notes:

- `ls [path]` lists a directory (no path lists the vaults)
- `cat <path>` prints a note, with its **version token** in a separate block; `cat "<path>#Heading"` prints one section (nested: `#Parent#Child`)
- `outline <path>` lists a note's headings with line ranges and sizes
- `rg [options] <regex> [path...]` searches notes and prints `path:line:text`; without a path it searches every vault
- `rg --files [path...]` lists note paths, so `rg --files | rg -i kumbu` finds a note by name
- `links <path>` shows where a note's `[[wikilinks]]` lead (`links <directory>` lists the broken ones), `backlinks <path>` which notes link to it
- `head`, `tail`, `wc`, `sort` work on a note or on piped input; `sort`, `ls` and `rg` list in human order (Ü next to U)
- `history <path>` lists a note's versions, `restore <path> <version>` goes back to one
- `mv <path> <target>` moves or renames a note; links whose target the move would change are rewritten in the form the author used (`--keep-links` leaves them); the old path remembers where the note went for 30 days
- `rm <path>` moves a note to the trash (kept 30 days) and names the links that now lead nowhere; `trash` lists it with its version, `restore <path> [version]` brings it back
- `help [command]` lists the commands or explains one; `<command> --help` does the same

`write` creates a note or replaces its content, and `edit` replaces one unique passage or appends to the end, of the whole note or of one section (`path` = `/Work/plan.md#Goals`; `replace_section` replaces the section). Both take the version token from `cat`; if the note changed in the meantime, the write fails instead of overwriting someone else's change. The last 10 versions of each note are kept (writes by the same writer within 10 minutes count as one; a `restore` and the link rewrites of an `mv` always get their own version), so the state before an agent session can always be restored. New notes can only go into existing vaults.

Commands chain with `|`, for example `cat /Work/plan.md | rg -n TODO` or `rg -l Hecate | wc -l`. `|` is the only operator: redirection, `&&`, `;` and `$()` are rejected with an error that says so. Regexes follow ripgrep's syntax and never backtrack, so no pattern can stall the server (no look-around, no backreferences).

Paths are always absolute, for example `/Work/projects/plan.md`. Top-level folders are vaults. Links resolve like in Obsidian, within the note's vault: the note whose path ends with the link, case-insensitive; ambiguous links are reported, never guessed. Kumbu² never rewrites note content on its own; only `mv` changes links. Markdown links (`[text](note.md)`) are not followed yet. There is no web UI and no user management yet.

## Security

**Notes are encrypted at rest.** Every vault has its own random key. Notes, their history, the trash, move records and even paths and vault names are sealed with AES-256-GCM before they reach MongoDB. A database dump or a stolen backup shows random ids, keyed hashes of paths, revision numbers, sizes and times, nothing readable. The vault key itself is stored only **wrapped** in key slots: one for the dev token (HKDF-SHA256) and one for the vault's **recovery key**. An unlocked vault is held in memory while it's used, and locked again after 15 idle minutes.

Be clear about what this does not cover (concept §3):

- Until there are users and API keys (build order step 7), the dev token unlocks every vault, and it lives in the server's configuration (`.env`). Whoever has **both** the database and that file can read everything. Encryption protects the database and its backups, not a machine where both are stored.
- The running server sees plaintext in memory; that's how search works. An operator of the running server is trusted.
- The AI provider of a connected client sees what the agent reads. This is not end-to-end encryption.

Access is guarded by the **single shared dev token**, sent as `Authorization: Bearer <token>`. Every path requires it except `GET /healthz`. Traffic is plain HTTP. The server refuses to start unless the token is at least 16 characters; use a random one (`openssl rand -hex 32`), since the vault keys are derived from it. Compose publishes port 8080 (or `KUMBU_PORT`) on **localhost only** (`127.0.0.1:${KUMBU_PORT:-8080}:8080` in `compose.yaml`). To reach it from other machines, remove the `127.0.0.1:` prefix deliberately, knowing that anyone on the network who learns the token can read and change every note.

### Recovery keys

When the server creates a vault, it writes the vault's recovery key **once** to `/recovery` in the container (the `recovery-keys` volume), and nowhere else. Fetch it, keep it somewhere safe such as a password manager, and delete the file:

```sh
docker compose exec kumbu sh -c 'cat /recovery/*.recovery-key.txt'
docker compose exec kumbu sh -c 'rm /recovery/*.recovery-key.txt'
```

Without the dev token and without the recovery key, a vault can't be opened by anyone, including you. To change the dev token: set the new `KUMBU_DEV_TOKEN` and `KUMBU_RECOVERY_KEYS=<key>,<key>` in `.env`, start once (the log says `Recovery key opened vault …`), then remove `KUMBU_RECOVERY_KEYS` again. The old token keeps its slots until revoking comes with step 7.

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

- **MongoDB is the source of truth.** Agents' changes live there; Kumbu² never writes back to `./vaults`.
- Seeding is for starting from scratch: at startup each folder becomes an encrypted vault, or fills one that is still empty. A vault that already holds notes (or trash) is left alone, so later changes on disk are not loaded. To load the folders again, `docker compose down -v` wipes the database, **including every change agents made and the vaults' keys**; the next start creates new vaults with new recovery keys.
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

Configuration (environment variables use `__` for `:`): `Kumbu:DevToken` (required, at least 16 characters), `Kumbu:Mongo:ConnectionString` (required), `Kumbu:Mongo:Database` (default `kumbu`), `Kumbu:Seed:Path` (folder to load vaults from; if it does not exist the server logs an error and starts without seeding), `Kumbu:Recovery:Path` (directory new vaults' recovery keys are written to; without it vaults get none), `Kumbu:Recovery:Keys` (recovery keys to give the dev token access at startup), `Kumbu:Vaults:IdleMinutes` (default 15) and `Kumbu:Vaults:MemoryLimitMegabytes` (default 512) for unlocked vaults. With Compose, set `KUMBU_DEV_TOKEN` and optionally `KUMBU_VAULTS`, `KUMBU_MONGO_IMAGE`, `KUMBU_PORT` (host port, default 8080) and `KUMBU_RECOVERY_KEYS` in `.env`.

## Development

Any .NET 10 SDK works (`global.json` rolls forward from 10.0.100, so distribution builds in the 1xx band are fine).

```sh
dotnet test                        # all tests; the Mongo tests need Docker
dotnet test tests/Kumbu.Core.Tests # core only, no Docker
```

## More

- [docs/concept.md](docs/concept.md): the full concept and threat model
- [CONCEPTS.md](CONCEPTS.md): a growing glossary of what was learned while building this

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
