# Test notes: phase 1 (walking skeleton)

Date: 2026-10-22. First hands-on test of the walking skeleton (build order step 1 in [concept.md](concept.md#12-build-order-walking-skeleton-first)), run locally with Claude Code as the MCP client.

## Setup

- Arch Linux (CachyOS), kernel 7.2.10, Docker 29.9, .NET SDK 10.0.113 from the distribution.
- Stack started with `docker compose up --build`, published on `127.0.0.1:8083` via `KUMBU_PORT`.
- Vaults: the samples (`Demo`, `Ideas`), seeded as 5 notes in 2 vaults, 0 skipped.
- Client registered with `claude mcp add --transport http kumbu http://localhost:8083/mcp --header "Authorization: Bearer <token>"`.

### Problems found while setting up (all fixed in `0d8ac8b`)

| Problem | Cause | Fix |
|---|---|---|
| `dotnet test` refused to start: SDK 10.0.402 not found | Distribution builds of .NET (source-build) only ship the 1xx SDK band | `global.json` pins `10.0.100` with `rollForward: latestFeature` |
| Port 8080 already taken on the host | Another local service | Host port is configurable with `KUMBU_PORT` (default 8080) |
| Build failed with `NETSDK1226` for `Microsoft.AspNetCore.App` | Package `aspnet-targeting-pack` was not installed | Installed it; local environment issue only, no repo change |

After that, `dotnet test`: 122 passed, 0 failed, 0 skipped (about 13 s, Mongo tests included).

## Results

Everything below came back instantly; latency was not noticeable.

| Call | Result |
|---|---|
| `ls` | `Demo/`, `Ideas/` |
| `ls /Demo`, `ls /Demo/projects` | Entries listed, directories end with `/` |
| `cat /Demo/welcome.md` | Correct content |
| `cat "/Demo/Meeting Notes.md"` | Correct; quoting works |
| `cat /Ideas/Überlegungen.md` | Correct; umlauts in path and content intact |
| `cat /Demo/../Ideas/retrofuturism.md` | Resolved to the right note |
| `cat welcome.md` | `paths must be absolute (there is no working directory)` |
| `cat /Demo/nope.md` | `No such file or directory` |
| `cat /Demo/Meeting Notes.md` (unquoted) | `cat: too many arguments` |
| `rm /Demo/welcome.md` | `command not found: rm` |
| `POST /mcp` without token | `401` |

The tool description was enough for the agent to use the tool without further explanation. All error messages were clear enough to correct the next call without guessing.

## Observations from the agent's side

1. **Wikilinks cannot be followed.** `welcome.md` links to `[[kumbu]]`, but nothing says where that note lives (`/Demo/projects/kumbu.md`). The agent had to walk the tree with `ls`. Fine for 5 notes, unworkable for hundreds.
2. **No way to search.** Without `rg` (or a link resolver) the only strategy in a large vault is blind traversal, which burns calls and context.
3. **No `help`.** The concept promises `command not found: rm — try 'help'`; today the hint and the command are missing. Not a problem yet with two commands, but it will be once there are more.

## Suggested next step

Build order step 2 from [concept.md](concept.md#the-sh-grammar): the `sh` parser with pipes, together with the first filters. Concretely:

- shlex-style tokeniser, `|` as the only operator; `$()`, `>`, `&&` and subshells rejected with a clear error.
- Filters on the right of a pipe: `head`, `tail`, `wc`, `sort`, `rg`.
- `rg` also as a standalone command over notes (plain regex, unranked), which addresses observations 1 and 2 well enough until links and backlinks arrive in step 4.
- `help` and `help <cmd>`, plus the `try 'help'` hint on unknown commands.
- Unit tests for the parser and every command (deterministic, the concept calls this the perfect test target), plus one end-to-end test over MCP.
- New `CONCEPTS.md` chapter for the step.

Out of scope for this step: writing (`edit`, `write`, `mv`, `rm`), link resolution, backlinks, encryption.
