# Kumbukumbu (Kumbu²)

*kumbukumbu* is Swahili for memory, remembrance, also records or minutes. It comes from *kumbuka*, "to remember". Repeating the word intensifies it: kumbu × kumbu = kumbu². The superscript also reads as "second", as in second brain.

Kumbu² is a self-hostable second brain for humans that AI agents may use. Notes are plain Markdown, kept in MongoDB, and agents reach them through an MCP server. It is your memory; the AI gets a key.

## Status: read-only shell with search

The server offers one MCP tool, `sh`, a read-only shell over your notes:

- `ls [path]` lists a directory (no path lists the vaults)
- `cat <path>` prints a note
- `rg [options] <regex> [path...]` searches notes and prints `path:line:text`; without a path it searches every vault
- `rg --files [path...]` lists note paths, so `rg --files | rg -i kumbu` finds a note by name
- `head`, `tail`, `wc`, `sort` work on a note or on piped input
- `help [command]` lists the commands or explains one; `<command> --help` does the same

Commands chain with `|`, for example `cat /Work/plan.md | rg -n TODO` or `rg -l Hecate | wc -l`. `|` is the only operator: redirection, `&&`, `;` and `$()` are rejected with an error that says so. Regexes follow ripgrep's syntax and never backtrack, so no pattern can stall the server (no look-around, no backreferences).

Paths are always absolute, for example `/Work/projects/plan.md`. Top-level folders are vaults. Notes are loaded into MongoDB from `./vaults` (or `KUMBU_VAULTS`) when the server starts. There is no writing, no link resolution, no web UI and no user management yet.

## Security

Be clear about what this is. There is **no encryption yet**: notes are stored as plaintext in MongoDB. Access is guarded by a **single shared dev token**, sent as `Authorization: Bearer <token>`. Every path requires it except `GET /healthz`. Traffic is plain HTTP. Do not put real secrets in it and do not expose it to an untrusted network.

The server refuses to start unless the token is at least 16 characters (`openssl rand -hex 32` gives 64). Compose publishes port 8080 (or `KUMBU_PORT`) on **localhost only** (`127.0.0.1:${KUMBU_PORT:-8080}:8080` in `compose.yaml`). To reach it from other machines, remove the `127.0.0.1:` prefix deliberately, knowing that anyone on the network who learns the token can read every note.

## Quickstart

```sh
cp .env.example .env              # then set KUMBU_DEV_TOKEN (openssl rand -hex 32)
mkdir -p vaults
cp -r samples/* vaults/           # first try: the sample vaults Demo and Ideas
docker compose up --build
curl localhost:8080/healthz       # ok
```

Your own notes go in `./vaults/<VaultName>/`, one folder per vault. Good to know:

- Notes are read **at startup only**. After changing files, run `docker compose restart kumbu` to load them again.
- Seeding adds and updates, it never deletes. A note you deleted or renamed on disk stays in the database until you wipe it with `docker compose down -v` (this removes the Mongo volume; the next start seeds from scratch).
- Symlinked vault folders inside `./vaults` do not resolve inside the container. Point `KUMBU_VAULTS` in `.env` at the real folder instead, for example `KUMBU_VAULTS=/home/me/Notes`.
- The container runs as an unprivileged user (UID 1654). Your files must be readable by it; unreadable files are skipped with a warning in the log.
- If the `mongo` container exits with "MongoDB cannot start: Linux kernel versions 6.19 and newer has a known incompatibility", your kernel is in the range 6.19–7.0.13, where MongoDB 8.x refuses to start ([SERVER-125742](https://jira.mongodb.org/browse/SERVER-125742)). Set `KUMBU_MONGO_IMAGE=mongo:7.0` in `.env`. The tests use `mongo:7.0` for the same reason.

Connect Claude Code:

```sh
claude mcp add --transport http kumbu http://localhost:8080/mcp --header "Authorization: Bearer <token>"
```

Configuration (environment variables use `__` for `:`): `Kumbu:DevToken` (required, at least 16 characters), `Kumbu:Mongo:ConnectionString` (required), `Kumbu:Mongo:Database` (default `kumbu`), `Kumbu:Seed:Path` (folder to load vaults from; if it does not exist the server logs an error and starts without seeding). With Compose, set `KUMBU_DEV_TOKEN` and optionally `KUMBU_VAULTS`, `KUMBU_MONGO_IMAGE` and `KUMBU_PORT` (host port, default 8080) in `.env`.

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
