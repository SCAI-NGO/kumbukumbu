# Monitoring with Prometheus and Grafana (design)

Date: 2026-10-28. Brainstormed with Chris; built overnight (brief: `docs/superpowers/plans/2026-10-28-overnight.md`). Part of the omnibus release 0.2.5. Comes before attachments part 2 (`2026-10-28-attachments-model-reading-design.md`), which adds its own metrics on top.

## Why

Kumbu² has no monitoring at all today. Two things need it now:

- **Decisions from real use, not guesses.** Attachments part 2 sets thresholds (a page counts as "mixed" from 25 % figure area), limits and size steps from reasoning alone, because there is no corpus of real PDFs to calibrate against. Chris: "statistics first, then we decide." The measurements must come from the running server.
- **Running a server.** Load, latency, errors and the search's health are things a deployer wants to see on a dashboard, not in logs.

Chris has used prometheus-net for years (he introduced it at his current and previous employers); Prometheus brings Grafana along, and with it dashboards for many later questions.

## Goals

1. The server exposes Prometheus metrics with **prometheus-net**.
2. The metrics endpoint is **never public**.
3. An optional compose file runs Prometheus and Grafana, provisioned with a data source and a ready dashboard.
4. A base set of metrics for the server as it is: HTTP, MCP tools and `sh` commands, search and embeddings, vaults, the .NET runtime.
5. A pattern later packages follow to add their own metrics and dashboard panels (attachments part 2 is the first).

## Non-goals

- Alerting rules, Alertmanager, notifications.
- Tracing (OpenTelemetry traces) and log shipping (Loki).
- Per-user or per-vault analytics.
- Metrics in the web app or the Obsidian plugin.

## Anti-goals (a failure even if it works)

- **Anything that identifies a person or their content in a metric:** no user ids, vault ids or names, paths, file names, search queries, note text, API key names or client names as labels or values. Kumbu² is built to keep notes private; its metrics must not become a side channel.
- **Unbounded label values** (a route with an id in it, a raw command line): it breaks Prometheus and usually leaks content as well.
- `/metrics` reachable from the internet, or behind the same reverse proxy as the app.
- Monitoring that a deployer has to run: without the monitoring compose file, Kumbu² works exactly as before.

## Decided

- **Library: prometheus-net** (`prometheus-net`, `prometheus-net.AspNetCore`; MIT). Chris's choice from experience. Metrics the server defines are prometheus-net metrics; where .NET or a library already publishes `System.Diagnostics.Metrics` meters (ASP.NET Core, the runtime), prometheus-net's meter adapter exports them rather than re-measuring.
- **A separate port for `/metrics`** *(my call)*: Kestrel listens on a second port (e.g. 9464), which `compose.yaml` does **not** publish. Only containers on the compose network reach it. The app's port 8080 has no `/metrics` route, so a reverse proxy in front of it can't expose metrics by accident. Configurable as an address (`Kumbu:Metrics:Url`), **defaulting to loopback** (`http://127.0.0.1:9464`) so a server run outside compose, with host networking or a hand-written `docker run -p`, exposes nothing; `compose.yaml` sets it to listen on the container's network (`http://0.0.0.0:9464`), still unpublished. No scrape credential *(my call: network isolation plus loopback default is the guard; documented as such)*. `Kumbu:Metrics:Enabled` (default true; with false, no port and no collection).
- **A compose file of its own, `compose.monitoring.yaml`**, used with `-f compose.yaml -f compose.monitoring.yaml` *(my call: a required Grafana password in `compose.yaml` itself would stop compose from starting even without monitoring)*; production-ready for the Hetzner deployment:
  - `prometheus`: scrapes `kumbu:9464`, retention 90 days *(my call)*, data in a volume, port not published.
  - `grafana`: provisioned data source and dashboards from files in the repository (e.g. `monitoring/grafana/`), anonymous access off, admin password from an env variable or a file (`KUMBU_GRAFANA_ADMIN_PASSWORD`, no default password: `compose.monitoring.yaml` refuses to start without one, e.g. `${KUMBU_GRAFANA_ADMIN_PASSWORD:?}`), published on `127.0.0.1:${KUMBU_GRAFANA_PORT:-3000}` only. **Reached through an SSH tunnel** (`ssh -L 3000:127.0.0.1:3000 …`), which the README shows *(my call)*; putting Grafana behind the public reverse proxy is the deployer's own decision, and the README says what it then needs (root URL, its own login exposed to the internet).
  - Images pinned by version, as the other services are.
- Everything new goes into `README.md` (how to start monitoring, the variables, what is measured and what is deliberately not), `.env.example`, and `docs/security-architecture.md` (the port, the label rule, Grafana's login).

## Base metrics

Names follow Prometheus conventions (`kumbu_` prefix, `_total`, `_seconds`, `_bytes`). The builder settles the exact list; it must cover:

- **HTTP:** requests and duration by **route template** (never the raw path), method and status class. prometheus-net.AspNetCore's HTTP metrics configured for route templates, or ASP.NET Core's own meters.
- **MCP:** tool calls by tool (`sh`, `write`, `edit`, `replace`, `board`), duration, errors by `ShellErrorKind`.
- **`sh` commands:** calls by command name, taken from the fixed list of commands the shell knows (`ls`, `cat`, `rg`, `semsearch`, …), anything else as `other`; for a pipeline, each command once. Never arguments.
- **Search:** `semsearch` and `rg` duration; embedding batches and duration by provider *kind* (`internal`, `external`), the embedding backlog (notes waiting: the queue holds notes, `src/Kumbu.Server/Search/SearchIndex.cs`), embedding errors.
- **Vaults:** number of unlocked vaults, unlocks and locks (idle TTL), conflicts on writes.
- **Auth:** sign-ins and failures, OAuth token issues, rate-limit rejections (counts only).
- **Runtime:** the process and .NET runtime metrics prometheus-net or the runtime meters give (GC, threads, memory, CPU).

## Dashboard

One provisioned dashboard "Kumbu²" with rows: traffic (HTTP, MCP), `sh` commands, search, vaults, runtime. Later packages add rows (or a second dashboard) in the same provisioning folder; attachments part 2 adds "Attachments".

## Testing

- xUnit: the metrics port serves `/metrics` and the app port does not; a request with an id in its path is counted under its route template; an `sh` pipeline counts each command once and an unknown command as `other`; with `Kumbu:Metrics:Enabled=false` there is no metrics port.
- A test (or a check in the existing test style) that the label values the server emits come from fixed sets: no label takes a value from user input.
- Hands-on: start monitoring, open Grafana, see the dashboard fill while doing a few `sh` calls; note it in `docs/notes-monitoring-hands-on.md`.

## Left to the builder

Exact metric names and buckets, the port number, the dashboard's panels within the rows above, where the label guard lives. Each choice with a line of reason in the plan or the hands-on note.
