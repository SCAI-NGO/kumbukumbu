# Binary Upload for Agents Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An agent with a shell asks over MCP (`upload`) for a one-time URL and `PUT`s a file's raw bytes there with `curl`; the file lands as that agent's write.

**Architecture:** A singleton `UploadTickets` holds tickets in memory: the bearer credential of the `upload` call, one path, an optional version, 10 minutes, single use. `CallerMiddleware` recognises `PUT /api/agent-upload/{token}`, takes the ticket and resolves the caller from the held credential exactly as for a bearer header (or a connection's access token on its URL), so `RequestCaller` is built as today. The endpoint runs the body of today's `PUT /api/files/{vault}/{**path}`, factored out, and answers with the file, the Markdown to paste and the deep link.

**Tech Stack:** C# .NET 10, ASP.NET Core minimal APIs, ModelContextProtocol C# SDK, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-17-binary-upload-for-agents-design.md`
**Depends on:** `docs/superpowers/plans/2026-11-17-note-deep-links.md` Task 1 (`DeepLinks.Url`). Build that plan first.

## Global Constraints

- British English everywhere in the repo; SPDX header on new source files.
- Route: `PUT /api/agent-upload/{token}` (never under `/api/upload/`, the web app's Markdown upload).
- Ticket: 32 random bytes (`RandomNumberGenerator`), Base64Url without padding in the URL; found by SHA-256 of the token; lifetime **10 minutes**; **single use**, taken before the body is read, also when the upload then fails; at most **20** open tickets per credential, the oldest dropped.
- Held in memory only; never in MongoDB or a log. The held bearer is a `byte[]` (UTF-8), zeroed when the ticket goes (used, expired, dropped); a `string` of it exists only for the resolution call.
- The token alone authorises the `PUT`: no cookie, no `Authorization` header read on that route.
- Unknown or expired token: `401`. Credential no longer resolves: `403`. Every refusal's message ends "Ask for a new upload URL with the upload tool."
- Unknown tokens count against the wrong-key limit (`WrongKeyLimiter`), like a wrong bearer key.
- File limits and checks exactly as `PUT /api/files`: 15 MB, `AttachmentFormat.Problem`, `ImageHeader.Read`, quota, `version` semantics.
- Writer: the key's or connection's label and reference, as if it had written the file itself.
- Origin of URLs: `OAuthUrls.Origin(http, options.Value.PublicUrl)`.
- Tests first. Run: `dotnet test --solution Kumbu.slnx`.

## Review Focus

- `curl` without `Content-Type` or with `application/x-www-form-urlencoded` (curl's default for `--data-binary`): accepted, Content-Type ignored (Task 4 test `Curls_default_content_type_is_ignored`).
- A body larger than 15 MB with chunked transfer (no `Content-Length`): `413`, ticket used (Task 4 test).
- Two `PUT`s racing on one token: exactly one gets past the take (Task 1 test `Take_is_atomic`).
- A path with spaces or parentheses: the answer's `markdown`, pasted into a note, resolves to the file (`sh links`) (Task 4 test).
- An agent whose `upload` call went to `/mcp/c/{id}` (OAuth connection): its ticket resolves through the connection, not as a bearer key (Task 4 test).

---

### Task 1: The ticket store

**Files:**
- Create: `src/Kumbu.Server/Api/UploadTickets.cs`
- Modify: `src/Kumbu.Server/Program.cs` (`AddSingleton<UploadTickets>()` beside `Pairings`, line ~151)
- Test: `tests/Kumbu.Server.Tests/Api/UploadTicketsTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record UploadTicket(byte[] Bearer, string? ConnectionId, string Path, string? Version, string Holder, DateTime ExpiresAt)` — `Path` as the caller saw it (`/Vault/x.png`), `Holder` the cap's key (key id, connection id, or `"dev"`).
  - `public sealed class UploadTickets(TimeProvider time)` with
    - `public const int MaxPerHolder = 20;` `public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);`
    - `public (string Token, DateTime ExpiresAt) Issue(byte[] bearer, string? connectionId, string path, string? version, string holder)`
    - `public UploadTicket? Take(string token)` — removes and returns it if live; an expired one is removed, zeroed, null.
    - `public void Zero(UploadTicket ticket)` — clears `Bearer`.

- [ ] **Step 1: Failing tests** (`FakeTimeProvider` from `Microsoft.Extensions.Time.Testing` if the test project has it, else a small fake `TimeProvider`):
  - `Issued_ticket_is_taken_once` — second `Take` null.
  - `Expired_ticket_is_gone_and_zeroed` — advance 10 min + 1 s; `Take` null; the bearer array all zero.
  - `Twenty_first_ticket_drops_the_oldest` — 21 issues for one holder; the first token's `Take` null, its bearer zeroed; the 21st live; another holder unaffected.
  - `Take_is_atomic` — 50 parallel `Take`s of one token: exactly one non-null.
  - `Tokens_are_43_url_safe_characters` — `^[A-Za-z0-9_-]{43}$`, two issues differ.
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter UploadTicketsTests`; expected FAIL.
- [ ] **Step 3: Implement** with a `ConcurrentDictionary<string, UploadTicket>` keyed by hex SHA-256 of the token (`TryRemove` gives atomicity), a per-holder queue under a lock for the cap, and an opportunistic sweep of expired entries on `Issue`.
- [ ] **Step 4: Run** the filter; expected PASS.
- [ ] **Step 5: Commit** — `git commit -m "Keep one-time upload tickets in memory"`

### Task 2: One write path for files

**Files:**
- Modify: `src/Kumbu.Server/Api/FileApi.cs:142-185` (`WriteAsync`)
- Test: existing `tests/Kumbu.Server.Tests/Api/FileApiTests.cs`, `FileUploadMemoryTests.cs` (no new tests: a pure refactor)

**Interfaces:**
- Produces: `internal static Task<Results<Created<FileStateView>, Ok<FileStateView>, Conflict<FileConflictView>>> StoreAsync(VaultPath filePath, string? version, HttpRequest request, RequestCaller caller, INoteStore store, NoteWriter notes, WriterLabels labels, CancellationToken ct)` — everything `WriteAsync` does after `ApiPaths.Parse`; `WriteAsync` becomes `Parse` + `StoreAsync`.

- [ ] **Step 1: Run** `dotnet test --solution Kumbu.slnx --filter "FileApiTests|FileUploadMemoryTests"`; expected PASS (baseline).
- [ ] **Step 2: Extract** `StoreAsync`.
- [ ] **Step 3: Run** the same filter; expected PASS.
- [ ] **Step 4: Commit** — `git commit -m "Let one method store an uploaded file for both upload routes"`

### Task 3: The MCP tool `upload`

**Files:**
- Create: `src/Kumbu.Server/Mcp/UploadTool.cs`
- Modify: `src/Kumbu.Core/Sh/Manual.cs` (`UploadDescription`)
- Test: `tests/Kumbu.Server.Tests/Mcp/UploadToolTests.cs`

**Interfaces:**
- Consumes: `UploadTickets.Issue` (Task 1); `RequestCaller`, `INoteStore`, `IHttpContextAccessor`, `IOptions<KumbuOptions>`.
- Produces: tool `upload(path: string, version?: string)`; its text answer contains, each on its own line, the URL `{origin}/api/agent-upload/{token}`, `expires {ExpiresAt:yyyy-MM-ddTHH:mm:ssZ}`, and the line `curl -sS -X PUT --data-binary @<local file> '{url}'`.

- [ ] **Step 1: Failing tests** (MCP client as in `BoardToolTests`, own user, vault, write and read keys):
  - `Answers_a_url_and_a_curl_line` — matches `/api/agent-upload/[A-Za-z0-9_-]{43}`, contains `curl -sS -X PUT --data-binary @<local file> '`, `15 MB`, `once`.
  - `Refuses_a_read_only_key`, `Refuses_an_invalid_path`, `Refuses_a_format_Kumbu_does_not_keep` (`/V/x.docx`), `Refuses_an_existing_file_without_version`, `Refuses_a_wrong_version`, `Refuses_a_note_path` (`/V/x.md`) — each `IsError`, and no ticket issued (the store's count, via an internal `Count` for tests, unchanged).
  - `The_tool_is_listed_with_its_description` — mentions `curl`, `shell`, `once`.
- [ ] **Step 2: Run** `--filter UploadToolTests`; expected FAIL.
- [ ] **Step 3: Implement** `[McpServerTool(Name = "upload", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]`, wrapped in `ToolResult.RunAsync` with a new `KumbuMetrics.Tool.Upload`. Checks in order: `ApiPaths.Parse(…, PathKind.Attachment)` (as `FileApi` does) → `NoteApi.Refuse(caller, "upload", path)` → `AttachmentFormat.ForPath` → existing file vs `version` (`GetAttachmentAsync`). The bearer: `http.HttpContext.Request.Headers.Authorization` after `Bearer `; the connection id: route value `id` when the request is on `/mcp/c/{id}`; `Holder`: `ApiKeyCredential.KeyId`, `ConnectionCredential.ConnectionId`, else `"dev"`. `UploadDescription`: what it is for (files such as images and PDFs, not notes), the two steps, that it needs a shell with network access to this server, the limits, that a URL works once, and "if the PUT fails, call upload again".
- [ ] **Step 4: Run** the filter, then the whole suite; expected PASS.
- [ ] **Step 5: Commit** — `git commit -m "Give agents an upload tool that hands out a one-time URL"`

### Task 4: `PUT /api/agent-upload/{token}`

**Files:**
- Create: `src/Kumbu.Server/Api/AgentUploadApi.cs`
- Modify: `src/Kumbu.Server/Auth/CallerMiddleware.cs` (a branch before `NeedsCaller`, and the bearer/connection resolution extracted so both paths share it), `src/Kumbu.Server/Program.cs` (`app.MapAgentUploadApi()`)
- Test: `tests/Kumbu.Server.Tests/Api/AgentUploadApiTests.cs`

**Interfaces:**
- Consumes: `UploadTickets.Take`/`Zero`, `FileApi.StoreAsync` (Task 2), `DeepLinks.Url` (deep links plan, Task 1).
- Produces: `public sealed record AgentUploadView(FileStateView File, string Markdown, string? Link)`; the ticket in `context.Features` as `UploadTicket` for the endpoint.

- [ ] **Step 1: Failing tests** (each gets its URL from the `upload` tool first, then uses a plain `HttpClient` of the factory):
  - `Stores_the_file_as_the_keys_write` — `201`, `file.path` equals the asked path, history's writer is the key's name.
  - `Works_once` — second `PUT` same URL: `401`, message ends with the ask-again sentence.
  - `Expires_after_ten_minutes` (move the app's clock as other expiry tests do): `401`.
  - `Revoked_key_is_refused`, `Lowered_right_is_refused` (key changed to read), `Ended_membership_is_refused`, `Rekeyed_vault_is_refused` — `403` or the usual refusal, nothing stored.
  - `Ignores_cookie_and_bearer` — a `PUT` with another user's cookie and key but an unknown token: `401`; with a valid token plus another key's header: stored as the ticket's key.
  - `Wrong_bytes_use_the_ticket` — PNG name, PDF bytes: `400`, then the same URL `401`.
  - `Too_large_chunked_body_is_413` — 15 MB + 1 byte without `Content-Length`: `413`.
  - `Curls_default_content_type_is_ignored` — `Content-Type: application/x-www-form-urlencoded`: `201`.
  - `Overwrites_with_the_tickets_version` — `upload` with the current version: `200`, new version.
  - `Locked_vault_still_works` — evict the vault from the working set between `upload` and `PUT` (as the sweep tests do): `201`.
  - `Answers_markdown_and_link` — image `/V/My Shots/rose (1).png` → `markdown` is an image link whose target, written into a note with `edit`, `sh links` shows leading to that file; `link` is `{origin}/n/{vaultId}/My%20Shots/rose%20%281%29.png`; a PDF gets a plain `[…](…)` link.
  - `Connection_ticket_resolves_through_the_connection` — `upload` over `/mcp/c/{id}` with an access token: the `PUT` stores it as the connection's write.
  - `Connection_ticket_after_a_refresh` — pin the behaviour as built: after an OAuth refresh in between, assert whichever outcome the code gives (`201`, or `403` with the ask-again sentence) and name it in the test's name once known.
  - `Unknown_token_counts_as_a_wrong_key` — enough unknown tokens give `429`.
- [ ] **Step 2: Run** `--filter AgentUploadApiTests`; expected FAIL.
- [ ] **Step 3: Implement the middleware branch**: path `PUT /api/agent-upload/{token}` (case-insensitive prefix, as `IsUnder`) → reserve a wrong-key permit → `tickets.Take(token)`; null → `Failed(ip)`, `401`. Else resolve: with `ConnectionId`, `tokens.ResolveAccessAsync(bearer, id, OAuthUrls.Resource(origin, id), ct)` then `RequestCaller.ForConnectionAsync`; without, the existing `ResolveBearerAsync`. Zero the ticket's bearer right after. No caller → `403`. `VaultBusyException` → the existing `BusyAsync`. Set the caller and the ticket as features, `next`.
- [ ] **Step 4: Implement the endpoint** in `AgentUploadApi.cs`: `MapPut("/api/agent-upload/{token}", …)` with `ApiErrors.Filter`, `RequestSizeLimitAttribute(FileApi.MaxBody)`, the same OpenAPI body description and `.Errors(400, 401, 403, 404, 409, 413, 429, 503, 507)` as `FileApi`'s `PUT`. Parse the ticket's `Path` with `ApiPaths` (its first segment is the vault), call `FileApi.StoreAsync` with the ticket's `Version`, and wrap the result: `Markdown` is `![name](/inside/path)` for `AttachmentKind.Image`, `[name](/inside/path)` otherwise, the path inside the vault without the vault's name, spaces as `%20` and `(`/`)` as `%28`/`%29`; `Link` from `DeepLinks.Url(origin, caller access's Id, segments)`. A `409` passes through unchanged.
- [ ] **Step 5: Run** the filter, then the whole suite; expected PASS.
- [ ] **Step 6: Commit** — `git commit -m "Let an upload URL store a file as the agent that asked for it"`

### Task 5: The system skill

**Files:**
- Create: `src/Kumbu.Core/Skills/System/kumbu-binary-upload/SKILL.md`
- Modify: the handshake listing if system skills are listed by hand there (`src/Kumbu.Server/Mcp/HandshakeInstructions.cs`; otherwise the embedded resource is enough), `src/Kumbu.Server/Welcome/Notes/Skills.md` (one line, as for `ingest-attachments`)
- Test: extend `tests/Kumbu.Server.Tests/Mcp/HandshakeInstructionsTests.cs` (and the Core system-skills test if one lists the names)

- [ ] **Step 1: Failing test** `Lists_the_binary_upload_skill` — the handshake instructions name `kumbu-binary-upload`; `skills cat kumbu-binary-upload` prints it.
- [ ] **Step 2: Write `SKILL.md`** in the shape of `ingest-attachments/SKILL.md` (front matter `name`, `description` that starts "Put a file you have or made (a generated image, a screenshot, a PDF) into the user's vault…"): when to use it; `upload` then `curl`; paste the answer's `markdown` with `edit`; show the user the `link`; on failure call `upload` again; if the host can't be reached, the sandbox needs the Kumbu² host allowed (the user does that, the README says how); base64 is not the way.
- [ ] **Step 3: Run** the filter; expected PASS.
- [ ] **Step 4: Commit** — `git commit -m "Add the kumbu-binary-upload system skill"`

### Task 6: Docs

**Files:**
- Modify: `README.md` (a section "Agents uploading files": the two steps, then per client where to allow the Kumbu² host: Claude Code on the web / claude.ai (the environment's network access, allowed hosts), Claude Code locally (no sandbox by default; with the sandbox, its allowed domains), Grok (its sandbox's network setting) — each in one or two lines, naming only the Kumbu² host), `docs/security-architecture.md` (§4 table row "Stolen upload URL": one file at one path, once, within 10 minutes, as its credential; §2 "Keys in memory": a ticket holds its bearer credential for at most 10 minutes, zeroed when it goes; §6 a subsection beside pairing codes describing the ticket, the route and that the token alone authorises it)

- [ ] **Step 1:** Write the sections; check every claim against the code as built (file:line references as the doc uses them).
- [ ] **Step 2: Commit** — `git commit -m "Document agent uploads and their tickets"`
- [ ] **Step 3:** After merge, the changelog entry from the spec goes into `/SWE/Kumbu/Versions/0.6.0.md` (Kumbu², not the repo).
