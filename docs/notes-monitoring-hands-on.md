# Test notes: monitoring, hands-on

Date: 2026-10-28. Hands-on test of monitoring (spec [2026-10-28-monitoring-design](superpowers/specs/2026-10-28-monitoring-design.md), plan [2026-10-28-monitoring](superpowers/plans/2026-10-28-monitoring.md)) by Claude, on the branch `monitoring`, before the pull request.

Set-up: a compose project of its own (`docker compose -p kumbu-monitoring -f compose.yaml -f compose.monitoring.yaml up -d`) with the server image built from this branch, the app on `127.0.0.1:18080`, Grafana on `127.0.0.1:13000`, a dev token, the repository's `samples/` as vaults and search off (`KUMBU_SEARCH=false`: the sandbox's containers can't download the model). The image was built from host-built output (web app and `dotnet publish`) on `mcr.microsoft.com/dotnet/aspnet:10.0`, because the sandbox's containers can't reach the package registries; the repository's `Dockerfile` itself was not changed and is built by CI. The agent side was MCP over plain HTTP (stateless `tools/call` requests with `curl`), the person's side Grafana in Playwright's Chromium (headless) and Prometheus' HTTP API from inside its container.

## Checks

| Suite | Command | Result |
|---|---|---|
| Server and core | `dotnet test --solution Kumbu.slnx` | 2224 tests: 2218 passed, 6 skipped (the model tests and a TensorX key), 0 failed |
| Web app | `pnpm -C web test --run` | 72 files, 1079 tests passed |
| Web build | `pnpm -C web build` | built |
| Compose files | `docker compose -f compose.yaml [-f compose.traefik.yaml] -f compose.monitoring.yaml config -q` | valid with `KUMBU_GRAFANA_ADMIN_PASSWORD`; refused without it; `compose.yaml` alone unchanged |

## What I did and saw

| Step | Result |
|---|---|
| `GET 127.0.0.1:18080/metrics` (the app's port) | the web app's `index.html`, no metrics |
| Prometheus' targets | `http://kumbu:9464/metrics` up, scraped every 15 s |
| Grafana without signing in (`/api/dashboards/uid/kumbu`) | 401 |
| Grafana as admin | the dashboard "Kumbu²" is provisioned, rows Traffic, sh commands, Search, Vaults, Auth, Runtime |
| 40 MCP `sh` calls (`cat … \| head -3`, `ls /Ideas`, `rg`, `semsearch`, `grep foo`, a `cat` of a missing note) and a `write` with a stale version | `kumbu_sh_commands_total` counted `cat`, `head`, `ls`, `rg`, `semsearch` and `grep` as `other`; the traffic, MCP and sh rows filled within a scrape; p95s of a few milliseconds |
| Every query of the dashboard, run against Prometheus | first round: 18 of 28 returned data, the other 10 were counters that had not seen their first event ("No data" in Grafana). After the fix below, all but the HTTP error panel return a series (at 0 where nothing happened) |
| The labels in Prometheus | only route templates (`/mcp/`, `/healthz`, …), tool and command names, enum values; no path, query or name of a note |

## Found in hands-on

Fixed on the branch:

- **Counters showed "No data" until their first event** (sign-ins, conflicts, OAuth tokens, locks, embedding counters): an operator reads that as broken, not as zero. Every enum-labelled series is now created at 0 when the server starts.
- **The embedding backlog stayed at 5 with search off**: true (the notes of vaults on the internal model wait for a model that never comes), but it read like a stuck queue. The panel's description now says so.
- **Rare events as per-second rates** were unreadable; they are now counts per hour, and sign-ins and OAuth tokens have an Auth row of their own.

Not fixed, for issues:

- **Most MCP errors have the kind `other`.** `cat` of a missing note and most other command errors carry no `ShellErrorKind`, so "MCP errors by tool and kind" shows mainly `other`. The spec asks for errors by `ShellErrorKind`, which is met; classifying every command's errors is a change of its own.
- **"HTTP errors and rate limits per hour" stays empty until the first 4xx/5xx or 429.** Those series come from prometheus-net's HTTP metrics (labelled by route template) and can't be created ahead without knowing which route will fail.
- **"MCP errors by tool and kind" has a long legend** now that all 35 tool × kind series exist at 0. A table panel or hiding zero series would read better.
- **The server logs "Overriding HTTP_PORTS '8080' … Binding to values defined by URLS instead"** at start. It comes from the base image's `HTTP_PORTS` next to the `ASPNETCORE_URLS` of the `Dockerfile`, and it is on `master` too; harmless.
- **About 20 file:line references in `docs/security-architecture.md` into `VaultCatalog.cs`, `AccountApi.cs`, `NoteApi.cs` and `KumbuOptionsValidator.cs` were already wrong on `master`** (they point at blank lines or braces). This branch shifted them with their code but didn't re-aim them.
- **Port 9464** is also the usual port of OpenTelemetry's Prometheus exporter. On a host where it's taken, set `Kumbu__Metrics__Url` to another port (the README says so).

## Not tested here: a checklist for Chris

- [ ] On the server: `KUMBU_GRAFANA_ADMIN_PASSWORD` in `.env` (e.g. `openssl rand -hex 24`), `COMPOSE_FILE=compose.yaml:compose.traefik.yaml:compose.monitoring.yaml` (behind Traefik), then `docker compose up -d`.
- [ ] From your own machine: `ssh -L 3000:127.0.0.1:3000 <user>@<server>`, open `http://localhost:3000`, sign in as `admin`; the dashboard "Kumbu²" is there and fills while you use claude.ai or the web app.
- [ ] Prometheus has its target up: in Grafana, Explore → `up{job="kumbu"}` is 1.
- [ ] From outside, `https://<host>/metrics` answers with the web app, not metrics; port 9464 is not reachable from the internet (`nc -vz <host> 9464` fails).
- [ ] After a `docker compose pull && docker compose up -d`, Prometheus still scrapes (the monitoring file must stay in `COMPOSE_FILE`).
- [ ] Optional: Grafana's own login page behind the public proxy is your decision; the README says what it then needs (`KUMBU_GRAFANA_ROOT_URL`, a strong password).
