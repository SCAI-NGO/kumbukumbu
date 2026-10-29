# Monitoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The server measures itself with prometheus-net on a separate, unpublished port, and an optional compose file runs Prometheus and Grafana with a ready dashboard.

**Architecture:** All of Kumbu²'s own metrics are defined in one static class, `src/Kumbu.Server/Metrics/KumbuMetrics.cs`, whose label values come only from enums and fixed lists (the label guard). A hosted service, `MetricsServer`, runs a second, minimal Kestrel host on `Kumbu:Metrics:Url` that serves `/metrics` and nothing else; the app's own pipeline never maps `/metrics`. The shell reports what it ran through a small observer interface in Kumbu.Core, so Core takes no dependency on prometheus-net.

**Tech Stack:** .NET 10, prometheus-net 8.2.1 and prometheus-net.AspNetCore 8.2.1 (MIT), Prometheus v3.15.1, Grafana 13.2.5, xUnit v3.

**Spec:** `docs/superpowers/specs/2026-10-28-monitoring-design.md` (brief: `docs/superpowers/plans/2026-10-28-overnight.md`).

## Global Constraints

- British English in code, comments, docs and commits; `// SPDX-License-Identifier: AGPL-3.0-or-later` on new C# files; no organisation, customer or employer names anywhere.
- Commit messages imperative, no prefix, ending with the session trailer lines.
- No label value from user input: no user ids, vault ids or names, paths, file names, queries, note text, API key or client names, raw routes or command lines.
- `/metrics` is never served on the app's port (8080); `Kumbu:Metrics:Url` defaults to `http://127.0.0.1:9464`; `compose.yaml` sets `http://0.0.0.0:9464` and does not publish it. `Kumbu:Metrics:Enabled` (default true); false: no port, no HTTP middleware, no default collectors.
- New NuGet packages: `dotnet restore Kumbu.slnx --force-evaluate` so both `packages.lock.json` files change.
- Without `compose.monitoring.yaml`, Kumbu² runs exactly as before.
- Tests run with `KUMBU_TEST_MONGO` in this sandbox (Docker refuses the `nofile` ulimit the test containers set): `export PATH=/root/.dotnet:$PATH KUMBU_TEST_MONGO="mongodb://127.0.0.1:27017/?directConnection=true"`.

## Decisions left to the builder (with reasons)

- **Port 9464**: the port the OpenTelemetry/Prometheus exporters conventionally use; free in the compose network.
- **Own Kestrel host for `/metrics`** rather than `RequireHost` on the app: `RequireHost` matches the `Host` header, which a client on port 8080 can set to `x:9464`; a separate listener can't be reached through the app's port at all.
- **Default collectors**: prometheus-net's process metrics plus the `System.Runtime` meter (GC, threads, memory, CPU, exceptions) via the meter adapter; the event-counter adapter is off (it duplicates the runtime meter), and no other meter is exported (ASP.NET Core's and HttpClient's meters carry `server.address` and friends; HTTP is measured by prometheus-net.AspNetCore by route template instead). Exemplars off.
- **HTTP labels**: prometheus-net.AspNetCore's `UseHttpMetrics` after `UseRouting`, `endpoint` = route template, status reduced to its class (`2xx`, `4xx`, …). A separate `kumbu_http_rate_limited_total{endpoint}` counts 429 answers, since the class hides them.
- **"Each command once"**: a pipeline counts each distinct command name once (`cat a | head | head` → `cat`, `head`); unknown names count as `other`.
- **Buckets**: tool and command durations 5 ms … 30 s (`0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30`); embedding batch durations the same.
- **Backlog and unlocked vaults** are gauges read on scrape (`Metrics.DefaultRegistry.AddBeforeCollectCallback`), so nothing has to be kept in step.
- **Label guard**: enums and `KnownCommands` in `KumbuMetrics` (no `string` label parameter on any helper) plus a scrape test that checks every `kumbu_` series' labels against the allowed sets and looks for planted user input.

## Review Focus

1. A request on the app port for `/metrics` (or with `Host: localhost:9464`) must not return metrics — test in Task 1.
2. A route with ids (`/mcp/c/{id}`, `/api/notes/{vault}/{**path}`) must be counted under its template, never the raw path — test in Task 2.
3. Two test hosts in one process (the shared registry, `ConfigureMeterAdapter` called once only) must not throw on the second build — covered by Task 1's `Configure_is_idempotent` test.
4. A `sh` call that fails to parse (unbalanced quote) must count nothing and still answer — test in Task 3.
5. A metric name in the dashboard that the server doesn't export (typo, rename) — test in Task 5.

---

### Task 1: Metrics port, options and default collectors

**Files:**
- Modify: `src/Kumbu.Server/Kumbu.Server.csproj` (add `prometheus-net` 8.2.1, `prometheus-net.AspNetCore` 8.2.1), both `packages.lock.json` (server, `tests/Kumbu.Server.Tests`)
- Modify: `src/Kumbu.Server/KumbuOptions.cs` (add `MetricsOptions Metrics`), `src/Kumbu.Server/KumbuOptionsValidator.cs`
- Create: `src/Kumbu.Server/Metrics/MetricsServer.cs`, `src/Kumbu.Server/Metrics/KumbuMetrics.cs` (skeleton: `Configure()`)
- Modify: `src/Kumbu.Server/Program.cs`, `compose.yaml`, `tests/Kumbu.Server.Tests/KumbuAppFactory.cs`
- Test: `tests/Kumbu.Server.Tests/Metrics/MetricsServerTests.cs`, `tests/Kumbu.Server.Tests/Startup/KumbuOptionsValidatorTests.cs`

**Interfaces:**
- Produces: `public sealed class MetricsOptions { bool Enabled = true; string Url = "http://127.0.0.1:9464"; }` under `KumbuOptions.Metrics`.
- Produces: `public sealed class MetricsServer : IHostedService` with `public Uri? Address { get; }` (the bound address once started, null before or when disabled). Registered as singleton plus hosted service only when `Enabled`.
- Produces: `public static class KumbuMetrics { public static void Configure(); }` — idempotent (a static `Lazy`/`Interlocked` guard): `Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions { SuppressEventCounters = true, SuppressMeters = true, SuppressDebugMetrics = true })`, then `Metrics.ConfigureMeterAdapter(o => o.InstrumentFilterPredicate = i => i.Meter.Name == "System.Runtime")`, and `Metrics.DefaultFactory.ExemplarBehavior = ExemplarBehavior.NoExemplars()`.
- Produces (tests): `tests/Kumbu.Server.Tests/Metrics/Scrape.cs` with `static Task<string> Scrape.TextAsync(KumbuAppFactory app)` (GETs `MetricsServer.Address + "metrics"` with a plain `HttpClient`) and `static double Scrape.Value(string text, string series)` (the sample value of an exact series line such as `kumbu_vault_unlocks_total`, 0 if absent).

- [ ] **Step 1: Write the failing tests**

`MetricsServerTests` (collection `app`):
- `The_metrics_port_serves_metrics`: `Scrape.TextAsync` contains `"# TYPE process_cpu_seconds_total"` and a `dotnet_gc` or `system_runtime` series (whatever the meter adapter names `dotnet.gc.collections`; assert on the prefix the first run shows and pin it).
- `The_app_port_serves_no_metrics`: `app.CreateClient().GetAsync("/metrics")` and the same with header `Host: localhost:9464` → body does not contain `"# TYPE"`.
- `Disabled_metrics_open_no_port`: `app.WithWebHostBuilder(b => b.UseSetting("Kumbu:Metrics:Enabled", "false"))` → `Services.GetService<MetricsServer>()` is null.
- `Configure_is_idempotent`: calling `KumbuMetrics.Configure()` twice does not throw.

`KumbuOptionsValidatorTests`: `Kumbu:Metrics:Url` of `"ftp://x:1"`, `"http://127.0.0.1"` (no explicit port), `"http://127.0.0.1:9464/metrics"` (a path) each fail with `KumbuOptionsValidator.MetricsUrlMessage` = `"Kumbu:Metrics:Url must be an http URL with a host and a port and no path, e.g. http://127.0.0.1:9464"`; disabled metrics skip the check.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/Kumbu.Server.Tests -- --filter-class "*MetricsServerTests" --filter-class "*KumbuOptionsValidatorTests"` — expected: compile errors / FAIL.

- [ ] **Step 3: Implement**

- Packages, then `dotnet restore Kumbu.slnx --force-evaluate`.
- `MetricsServer.StartAsync`: `WebApplication.CreateSlimBuilder()`, `builder.WebHost.UseUrls(options.Url)`, logging filtered like the app's (`Microsoft.AspNetCore` at Warning), `app.MapMetrics("/metrics")`, nothing else (any other path 404). Read the bound address from `IServerAddressesFeature` after start (port 0 in tests). `StopAsync` stops and disposes it.
- `Program.cs`: when enabled, `KumbuMetrics.Configure()` before building, register `MetricsServer`. Comment why a separate listener (the `Host` header argument above).
- `compose.yaml` (service `kumbu`): `Kumbu__Metrics__Url: http://0.0.0.0:9464` with a comment: reachable from the compose network only (not published), for `compose.monitoring.yaml`.
- `KumbuAppFactory.ConfigureWebHost`: `Kumbu:Metrics:Url` = `http://127.0.0.1:0`.

- [ ] **Step 4: Run the tests to see them pass**, then the whole server suite once (other tests build hosts too: Review Focus 3).

- [ ] **Step 5: Commit** — "Serve Prometheus metrics on a port of their own"

---

### Task 2: HTTP metrics by route template, and 429s

**Files:**
- Modify: `src/Kumbu.Server/Program.cs`, `src/Kumbu.Server/Metrics/KumbuMetrics.cs`
- Create: `src/Kumbu.Server/Metrics/HttpMetrics.cs`
- Test: `tests/Kumbu.Server.Tests/Metrics/HttpMetricsTests.cs`

**Interfaces:**
- Consumes: `Scrape` (Task 1).
- Produces: `KumbuMetrics.RateLimited` = `Counter kumbu_http_rate_limited_total{endpoint}`; `public static IApplicationBuilder UseKumbuHttpMetrics(this IApplicationBuilder app)` in `HttpMetrics.cs`: `UseHttpMetrics(o => o.ReduceStatusCodeCardinality())` plus a middleware that, after `next`, increments `RateLimited` with the endpoint's `RouteEndpoint.RoutePattern.RawText` (or `"none"`) when the status is 429.

- [ ] **Step 1: Write the failing tests**
- `A_path_with_ids_is_counted_under_its_route_template`: GET `/api/notes/Samples/zz-route-marker.md` with the dev token; scrape contains `endpoint="/api/notes/{vault}/{**path}"` and not `zz-route-marker`; the `code` label of that series is a class (`code="2xx"` or `"4xx"`).
- `An_unknown_connection_id_is_counted_under_its_template`: POST `/mcp/c/zz-conn-marker` → scrape has `endpoint="/mcp/c/{id}"`, no `zz-conn-marker`.
- `A_429_is_counted`: drive the sign-in limit on `app.PublicApi` with one `X-Forwarded-For` address (look at `AccountLimits` for the count; wrong password until 429) → `kumbu_http_rate_limited_total{endpoint="/api/session"}` rose by at least 1.

- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement** — `app.UseKumbuHttpMetrics()` right after `app.UseRouting()` (so CallerMiddleware's 401/429s are counted with their endpoint), only when metrics are enabled.
- [ ] **Step 4: Run to see them pass.**
- [ ] **Step 5: Commit** — "Count HTTP requests by route template and rate-limited answers"

---

### Task 3: MCP tools and `sh` commands

**Files:**
- Create: `src/Kumbu.Core/Sh/IShellObserver.cs`
- Modify: `src/Kumbu.Core/Sh/Shell.cs` (optional observer; `public IReadOnlyCollection<string> CommandNames`), `src/Kumbu.Core/Notes/NoteWriter.cs` (`Conflict` sets `Kind = ShellErrorKind.Conflict`)
- Create: `src/Kumbu.Server/Metrics/ShellMetrics.cs` (the observer)
- Modify: `src/Kumbu.Server/Metrics/KumbuMetrics.cs`, `src/Kumbu.Server/Mcp/ToolResult.cs`, `ShTool.cs`, `WriteTools.cs`, `BoardTool.cs`, `src/Kumbu.Server/Api/NoteApi.cs` (`Conflict`), `src/Kumbu.Server/Api/FileApi.cs` (its conflict answers), `src/Kumbu.Server/Program.cs`
- Test: `tests/Kumbu.Core.Tests/Sh/ShellObserverTests.cs`, `tests/Kumbu.Server.Tests/Metrics/ToolMetricsTests.cs`

**Interfaces:**
- Produces (Core):
  ```csharp
  /// <summary>What the shell ran, for metrics; never given arguments.</summary>
  public interface IShellObserver
  {
      /// <summary>A parsed command line: each stage's command name as typed, in order.</summary>
      void Ran(IReadOnlyList<string> commands);
      /// <summary>One stage that ran (a known command), and how long it took.</summary>
      void Timed(string command, TimeSpan elapsed);
  }
  ```
  `Shell(..., IShellObserver? observer = null)` as the last constructor parameter; `Ran` after a successful parse with at least one stage, `Timed` around each `command.RunAsync`/checkpoint run. A parse failure calls neither.
- Produces (Server, `KumbuMetrics`):
  - `enum Tool { Sh, Write, Edit, Replace, Board }`, `enum ToolError { Invalid, Refused, NotFound, Conflict, Unavailable, Damaged, Other }` (label values snake_case: `not_found`), `enum WriteVia { Mcp, Api }`.
  - `kumbu_mcp_tool_calls_total{tool}`, `kumbu_mcp_tool_duration_seconds{tool}` (histogram), `kumbu_mcp_tool_errors_total{tool,kind}`, `kumbu_sh_commands_total{command}`, `kumbu_sh_command_duration_seconds{command}`, `kumbu_write_conflicts_total{via}`.
  - `public static readonly IReadOnlyList<string> KnownCommands` — the shell's command names, literally listed, plus nothing else; `public static IReadOnlyList<string> CommandLabels(IReadOnlyList<string> typed)` → distinct, each known name as itself, anything else `"other"`, in first-seen order.
  - `public static void WriteConflict(WriteVia via)`.
- `ToolResult.RunAsync(KumbuMetrics.Tool tool, Func<Task<ShellResult>> body)`: times the call, counts it, and on `IsError` counts `kind` from `ShellResult.Kind` (null → `other`; the caught `VaultUnavailableException`/`VaultDeletedException` → `unavailable`, `CryptographicException` → `damaged`); a `Conflict` kind also counts `WriteConflict(Mcp)`.
- `ShellMetrics : IShellObserver` (singleton): `Ran` increments `kumbu_sh_commands_total` for each of `CommandLabels(commands)`; `Timed` observes the duration for known names only.

- [ ] **Step 1: Write the failing tests**
- Core `ShellObserverTests` (a recording fake observer, the in-memory store the other `Sh` tests use): `cat /V/a.md | head -1` → `Ran(["cat","head"])`, two `Timed` calls named `cat`, `head`; `grep x` → `Ran(["grep"])`, no `Timed`; `cat "/V/a.md` (unbalanced quote) → neither.
- Server `ToolMetricsTests`:
  - `Command_labels_are_known_names_or_other`: `CommandLabels(["cat","head","head","grep","rm -rf"])` equals `["cat","head","other"]`.
  - `Known_commands_are_the_shells`: `KnownCommands` set-equals a `Shell`'s `CommandNames` (built from the app's scoped services).
  - `A_tool_error_is_counted_by_kind`: MCP `write` with a stale version on a sample note → `kumbu_mcp_tool_errors_total{tool="write",kind="conflict"}` and `kumbu_write_conflicts_total{via="mcp"}` each rose by ≥ 1; `sh` with `cat /Nope/x.md` → an `sh` error series exists.
  - `A_rest_conflict_is_counted`: PUT a note with a stale version through `/api/notes/...` → `kumbu_write_conflicts_total{via="api"}` rose by ≥ 1.
- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement**; register `ShellMetrics` as `IShellObserver` singleton only when metrics are enabled (the `Shell` takes it as optional, DI passes null otherwise). Check `NoteWriter.Conflict`'s callers still answer as before with `Kind` set (run the whole suite).
- [ ] **Step 4: Run to see them pass**, then both suites.
- [ ] **Step 5: Commit** — "Count MCP tool calls, sh commands and write conflicts"

---

### Task 4: Search, embeddings, vaults and sign-ins

**Files:**
- Modify: `src/Kumbu.Server/Metrics/KumbuMetrics.cs`, `src/Kumbu.Server/Search/SearchIndex.cs` (around `driver.EmbedAsync` in `IndexAsync`), `src/Kumbu.Server/Vaults/VaultCatalog.cs` (the three `Unlocked?.Invoke` sites, `SweepUntil`'s two lock sites; expose `public int UnlockedCount => _open.Count`), `src/Kumbu.Server/Api/AccountApi.cs` (`SignInAsync`), `src/Kumbu.Server/OAuth/OAuthEndpoints.cs` (`TokenAsync`), `src/Kumbu.Server/Program.cs` (before-collect callback)
- Test: `tests/Kumbu.Server.Tests/Metrics/ServerMetricsTests.cs`

**Interfaces:**
- Produces (`KumbuMetrics`): `enum ProviderKind { Internal, External }` (from `EmbeddingProvider.External`), `enum LockReason { Idle, Memory }`, `enum Outcome { Success, Failure }`, `enum Grant { AuthorizationCode, RefreshToken }`;
  `kumbu_embedding_batches_total{provider_kind}`, `kumbu_embedding_batch_duration_seconds{provider_kind}`, `kumbu_embedding_errors_total{provider_kind}` (an `EmbedAsync` that threw anything but cancellation), `kumbu_embedding_backlog` (gauge, `SearchIndex.Queued`), `kumbu_vaults_unlocked` (gauge, `VaultCatalog.UnlockedCount`), `kumbu_vault_unlocks_total`, `kumbu_vault_locks_total{reason}`, `kumbu_sign_ins_total{outcome}`, `kumbu_oauth_tokens_issued_total{grant}`.
  `public static void ReadOnScrape(SearchIndex index, VaultCatalog catalog)` registers the before-collect callback once (called from `Program.cs` after `Build()` when enabled).
- [ ] **Step 1: Write the failing tests** (`app` collection; deltas, ≥ 1, as other tests run in parallel):
  - `Unlocking_a_vault_is_counted`: a fresh user (the helpers `AccountApiTests` uses to create and sign in) → `kumbu_vault_unlocks_total` rose; `kumbu_vaults_unlocked` ≥ 1.
  - `Sign_ins_are_counted_by_outcome`: one wrong password, one right → both `outcome` series rose.
  - `Indexing_counts_batches_and_shows_the_backlog`: write a note over MCP, `app.IndexedAsync()` → `kumbu_embedding_batches_total{provider_kind="internal"}` rose; `kumbu_embedding_backlog` present.
  - `An_idle_vault_lock_is_counted`: the catalog's `Sweep()` with a `FakeTimeProvider` past `IdleMinutes`, as `VaultCatalogTests`/the sweeper tests do it → `kumbu_vault_locks_total{reason="idle"}` rose. (If no such test setup exists, unit-test `VaultCatalog` the same way its existing idle-lock test does.)
  - The OAuth token counter: increment on the success path of `TokenAsync`; covered by the label-guard test's allowed set rather than a flow test (the OAuth flow tests are long; add one assertion to the existing code-exchange test in `tests/Kumbu.Server.Tests/OAuth/` that `kumbu_oauth_tokens_issued_total{grant="authorization_code"}` rose).
- [ ] **Step 2: Run to see them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to see them pass.**
- [ ] **Step 5: Commit** — "Measure search, embeddings, vaults and sign-ins"

---

### Task 5: Label guard, monitoring compose file, dashboard and docs

**Files:**
- Test: `tests/Kumbu.Server.Tests/Metrics/LabelGuardTests.cs`, `tests/Kumbu.Server.Tests/Metrics/DashboardTests.cs`
- Create: `compose.monitoring.yaml`, `monitoring/prometheus/prometheus.yml`, `monitoring/grafana/provisioning/datasources/prometheus.yaml`, `monitoring/grafana/provisioning/dashboards/kumbu.yaml`, `monitoring/grafana/dashboards/kumbu.json`
- Modify: `README.md`, `.env.example`, `docs/security-architecture.md` (§7 "Built into the server" and "Deployment shape": the port, the label rule, Grafana's login; file:line references), `docs/concept.md` only if a decision there is touched (none expected).

**Interfaces:**
- Consumes: every metric of Tasks 1–4.
- `KumbuMetrics.AllowedLabels`: `IReadOnlyDictionary<string, IReadOnlySet<string>>` built from the enums, `KnownCommands` + `other`, and for `endpoint` the app's route templates (`EndpointDataSource` in the test) plus `none`; used by the guard test only.

- [ ] **Step 1: Write the failing tests**
  - `LabelGuardTests.No_label_carries_user_input`: plant a marker `zzsecretmarker` everywhere a person can type: a note path and content written over MCP, a `semsearch zzsecretmarker` and `rg zzsecretmarker /Samples` call, an unknown command `zzsecretmarker`, a sign-in with username `zzsecretmarker`, a GET `/api/notes/zzsecretmarker/x.md`. Scrape: the text does not contain `zzsecretmarker` (case-insensitive); every `kumbu_*` sample's label pairs are in `AllowedLabels` (an unknown label name fails too). `http_request*` series: `endpoint` in the allowed set.
  - `DashboardTests.Every_metric_the_dashboard_queries_exists`: parse `monitoring/grafana/dashboards/kumbu.json`, take every `targets[].expr`, extract identifiers matching `\b(kumbu_[a-z_]+|http_request[a-z_]*|process_[a-z_]+|dotnet_[a-z_]+|system_runtime_[a-z_]+)\b`, strip `_bucket|_sum|_count`, and assert each base name has a `# TYPE` line in a scrape (after exercising one `sh` call so lazily created series exist; histograms without samples still print `# TYPE` in prometheus-net only once a child exists, so the test makes one call of each kind it needs, or checks against `KumbuMetrics`' metric names for `kumbu_` ones).
- [ ] **Step 2: Run to see them fail** (no dashboard yet; `AllowedLabels` missing).
- [ ] **Step 3: Implement**
  - `compose.monitoring.yaml`: `prometheus` (`prom/prometheus:v3.15.1`, `--config.file=/etc/prometheus/prometheus.yml`, `--storage.tsdb.retention.time=90d`, config mounted read-only, volume `prometheus-data`, no ports) and `grafana` (`grafana/grafana:13.2.5`, `GF_SECURITY_ADMIN_PASSWORD: ${KUMBU_GRAFANA_ADMIN_PASSWORD:?Set KUMBU_GRAFANA_ADMIN_PASSWORD in .env}`, `GF_AUTH_ANONYMOUS_ENABLED: "false"`, `GF_USERS_ALLOW_SIGN_UP: "false"`, `GF_ANALYTICS_REPORTING_ENABLED: "false"`, `GF_ANALYTICS_CHECK_FOR_UPDATES: "false"`, `GF_SERVER_ROOT_URL: ${KUMBU_GRAFANA_ROOT_URL:-}`, provisioning and dashboards mounted read-only, volume `grafana-data`, `ports: ["127.0.0.1:${KUMBU_GRAFANA_PORT:-3000}:3000"]`, `depends_on: prometheus`). Both `restart: unless-stopped` like the others if they have it.
  - `prometheus.yml`: one job `kumbu`, `static_configs: [{targets: ["kumbu:9464"]}]`, `scrape_interval: 15s`.
  - Datasource provisioned with a fixed `uid: kumbu-prometheus`; dashboard provider pointing at `/var/lib/grafana/dashboards`, `allowUiUpdates: false`.
  - `kumbu.json`: title "Kumbu²", uid `kumbu`, rows **Traffic** (HTTP rate by endpoint, p95 latency, 4xx/5xx and 429 rate; MCP calls by tool, p95 by tool, errors by kind), **sh commands** (calls by command, p95 by command), **Search** (semsearch and rg p95, embedding batches and p95 by provider kind, backlog, errors), **Vaults** (unlocked, unlocks, locks by reason, write conflicts by via, sign-ins by outcome, OAuth tokens by grant), **Runtime** (CPU, working set, GC collections, heap, thread pool threads). Leave a comment-free JSON; the README says later packages add rows here or a second file in the same folder.
  - README: a "Monitoring" section (start: `docker compose -f compose.yaml -f compose.monitoring.yaml up -d`; the variables with format and example; the SSH tunnel `ssh -L 3000:127.0.0.1:3000 user@host` then `http://localhost:3000`; what Grafana behind the public proxy then needs: `KUMBU_GRAFANA_ROOT_URL`, its own login on the internet; what is measured and what deliberately not; `Kumbu__Metrics__Enabled=false`). `.env.example`: `KUMBU_GRAFANA_ADMIN_PASSWORD` (required with the monitoring file; `openssl rand -hex 24`), `KUMBU_GRAFANA_PORT`, `KUMBU_GRAFANA_ROOT_URL`.
  - `docs/security-architecture.md`: the metrics port (loopback default, unpublished in compose, no scrape credential: network isolation is the guard, an accepted limit), the label rule and its guard test, Grafana (admin password required, anonymous off, loopback port, SSH tunnel), with file:line references.
- [ ] **Step 4: Run to see them pass**; `docker compose -f compose.yaml -f compose.monitoring.yaml config -q` with and without `KUMBU_GRAFANA_ADMIN_PASSWORD` (without: refuses); `docker compose -f compose.yaml config -q` unchanged.
- [ ] **Step 5: Commit** — "Add the monitoring compose file, the Kumbu² dashboard and the label guard"

---

### After the tasks (the lead, not a subagent)

- Full verification (both suites; `pnpm -C web install --frozen-lockfile`, `pnpm -C web test --run`, `pnpm -C web build`).
- Hands-on: own compose project (`docker compose -p kumbu-monitoring -f compose.yaml -f compose.build.yaml -f compose.monitoring.yaml`, free ports), a few MCP `sh` calls, scrape through Prometheus' API from inside the network and Grafana's API through its loopback port (dashboard present, panels return data); `down -v`. Note in `docs/notes-monitoring-hands-on.md` with Chris's checklist (Grafana through the SSH tunnel from his machine).
- Whole-diff review (superpowers:requesting-code-review), fixes, draft PR against `master`.
