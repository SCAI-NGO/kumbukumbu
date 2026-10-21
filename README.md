# Kumbukumbu (Kumbu²)

*kumbukumbu* is Swahili for memory, remembrance, also records or minutes. It comes from *kumbuka*, "to remember". Repeating the word intensifies it: kumbu × kumbu = kumbu². The superscript also reads as "second", as in second brain.

Kumbu² is a self-hostable second brain for humans that AI agents may use. Notes are plain Markdown, kept in MongoDB, and agents reach them through an MCP server. It is your memory; the AI gets a key.

## Status: walking skeleton

The thinnest end-to-end slice that works. The server offers one MCP tool, `sh`, a read-only shell over your notes:

- `ls [path]` lists a directory (no path lists the vaults)
- `cat <path>` prints a note

Paths are always absolute, for example `/Work/projects/plan.md`. Top-level folders are vaults. Notes are loaded into MongoDB from `./vaults` when the server starts. There is no search, no writing, no web UI and no user management yet.

## Security

Be clear about what this is. There is **no encryption yet**: notes are stored as plaintext in MongoDB. Access is guarded by a **single shared dev token**, sent as `Authorization: Bearer <token>`. Every path requires it except `GET /healthz`. Traffic is plain HTTP. Do not put real secrets in it and do not expose it to an untrusted network.

## Quickstart

```sh
cp .env.example .env              # then set KUMBU_DEV_TOKEN (openssl rand -hex 32)
mkdir -p vaults/MyVault           # put your Markdown notes in ./vaults/<VaultName>/
docker compose up --build
curl localhost:8080/healthz       # ok
```

Connect Claude Code:

```sh
claude mcp add --transport http kumbu http://localhost:8080/mcp --header "Authorization: Bearer <token>"
```

Configuration (environment variables use `__` for `:`): `Kumbu:DevToken` (required), `Kumbu:Mongo:ConnectionString`, `Kumbu:Mongo:Database` (default `kumbu`), `Kumbu:Seed:Path` (folder to load vaults from).

## Development

```sh
dotnet test                        # all tests; the Mongo tests need Docker
dotnet test tests/Kumbu.Core.Tests # core only, no Docker
```

## More

- [docs/concept.md](docs/concept.md): the full concept and threat model
- [CONCEPTS.md](CONCEPTS.md): a growing glossary of what was learned while building this

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
