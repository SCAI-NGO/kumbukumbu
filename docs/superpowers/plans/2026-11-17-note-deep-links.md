# Note Deep Links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every note and file gets a clickable `/n/<vault id>/<path>` URL that the MCP tools hand out next to every version block, and that the web app resolves to the reader's own name for the vault.

**Architecture:** A scoped server service `DeepLinks` turns a caller-visible path into a URL (origin from `KUMBU_PUBLIC_URL` or the request, vault id from the caller's opened access). `ToolResult.From` prints a `link to …` block after each version block when given that service. The web app gets a route `/n/:vaultId/*` that finds the vault by `VaultView.id`, checks the target exists and redirects, or shows one "not here" page.

**Tech Stack:** C# .NET 10, ModelContextProtocol C# SDK, xUnit; React 19, React Router 7, TanStack Query, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-17-note-deep-links-design.md`

## Global Constraints

- British English in code, comments, docs and UI copy; source files start with `// SPDX-License-Identifier: AGPL-3.0-or-later` where their siblings do.
- URL form: `<origin>/n/<vault id>/<path inside the vault>`, each path segment percent-encoded (`Uri.EscapeDataString` per segment, `/` between).
- Origin: `KUMBU_PUBLIC_URL` (trailing slash dropped), else the request's scheme and host — `OAuthUrls.Origin(http, options.Value.PublicUrl)`.
- One rule: wherever a version block is printed, the link block follows it. Not in `rg`/`semsearch` results (those print no version block).
- Link block text: `\nlink to {caller path}: {url}` (own `TextContentBlock`, after that path's version block).
- The not-here page's sentence, exactly: "This link leads to a note that isn't here: it was moved or removed, or it's in a vault you can't see right now."
- A link never tells whether a vault or note exists that the reader can't see: one page for every failure.
- No section anchors; a `cat` of a section links the note.
- Tests first. Run: `dotnet test --solution Kumbu.slnx`, `pnpm -C web test --run`, `pnpm -C web build`.

## Review Focus

- A member whose alias differs from the owner's name: the link carries the same id for both, and resolves to each one's own name (Task 1 and Task 3 tests).
- A path with spaces, umlauts, `#` or `%` in a name: encoded so the web route decodes back to the exact path (Task 1 test `Encodes_each_segment`, Task 3 test with `Garten Ü/Rosen #1.md`).
- The dev token or any credential without opened access for the vault: no link block, no exception (Task 1 test `No_link_without_access`).
- A file (not a note) behind `/n/`: goes to `/view/`, not `/notes/` (Task 3).
- A user who opens the link signed out: comes back to `/n/…` after sign-in (Task 3).

---

### Task 1: Deep links in the MCP results

**Files:**
- Create: `src/Kumbu.Server/Mcp/DeepLinks.cs`
- Modify: `src/Kumbu.Server/Mcp/ToolResult.cs` (`From`, `RunAsync`), `src/Kumbu.Server/Mcp/WriteTools.cs`, `src/Kumbu.Server/Mcp/ShTool.cs`, `src/Kumbu.Server/Mcp/BoardTool.cs` (pass the service; board prints versions too), `src/Kumbu.Server/Program.cs` (register `AddScoped<DeepLinks>()`), `src/Kumbu.Core/Sh/Manual.cs` (descriptions)
- Test: `tests/Kumbu.Server.Tests/Mcp/DeepLinkTests.cs`, extend `tests/Kumbu.Server.Tests/Mcp/ImageContentTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class DeepLinks(RequestCaller caller, IHttpContextAccessor http, IOptions<KumbuOptions> options)` with `public string? For(string callerPath)` — null when the path doesn't parse, there is no `HttpContext`, or `(caller.Credential as IRightsCredential)?.Access(vaultName)` is null.
  - `public static string Url(string origin, string vaultId, IEnumerable<string> segmentsInsideVault)` on `DeepLinks` (Task 2 of the upload plan uses it).
  - `ToolResult.From(ShellResult result, Func<string, string?>? linkOf = null)`; `ToolResult.RunAsync(KumbuMetrics.Tool tool, Func<Task<ShellResult>> body, DeepLinks? links = null)`.

- [ ] **Step 1: Write the failing unit tests** in `ImageContentTests.cs` (pure `ToolResult.From`):
  - `A_link_follows_each_version_block`: `ShellResult.Ok("x") with { Versions = [new("/V/a.md","t1"), new("/V/b.md","t2")] }`, `linkOf = p => "https://h/n/id" + p[2..]`; content texts in order end with `"\nversion of /V/a.md: t1"`, `"\nlink to /V/a.md: https://h/n/id/a.md"`, `"\nversion of /V/b.md: t2"`, `"\nlink to /V/b.md: https://h/n/id/b.md"`.
  - `No_link_block_when_linkOf_gives_null` and `No_link_block_without_linkOf`: count unchanged from today.
- [ ] **Step 2: Write the failing end-to-end tests** in `DeepLinkTests.cs` (`[Collection("app")]`, MCP client as `BoardToolTests` does, own user and vault and a write key):
  - `Write_returns_the_link_with_the_vault_id`: `write` `/{vault}/Plan.md` → a text block equals `$"\nlink to /{vault}/Plan.md: {origin}/n/{vaultId}/Plan.md"`, vaultId from `CreateForUserAsync(...).Id`.
  - `Edit_and_cat_and_cat_of_a_section_return_it` (section path `/{vault}/Plan.md#Goals` links `…/Plan.md`).
  - `Replace_returns_one_link_per_changed_note` (two notes).
  - `A_member_gets_the_same_id_under_her_alias` (share the vault, member's key, alias differs; link path starts `/n/{vaultId}/`).
  - `Encodes_each_segment`: `/{vault}/Garten Ü/Rosen #1.md` → `…/n/{id}/Garten%20%C3%9C/Rosen%20%231.md`.
  - `PublicUrl_wins_over_the_request_origin` (factory with `Kumbu:PublicUrl=https://kumbu.example.org`).
  - `No_link_without_access` (dev token: no `link to` block).
- [ ] **Step 3: Run them** — `dotnet test --solution Kumbu.slnx --filter "FullyQualifiedName~DeepLink|FullyQualifiedName~ImageContent"`; expected: FAIL (no `DeepLinks`, no block).
- [ ] **Step 4: Implement** `DeepLinks` and the `ToolResult` change; add `DeepLinks links` as a parameter to each tool method (the SDK injects scoped services as it does `NoteWriter`) and pass it to `RunAsync`. `From` adds, right after a version's block, `linkOf(version.Path)` when non-null. The vault name is the path's first segment (`VaultPath.Parse` or the `ApiPaths` helper the server already uses); the segments after it, without a `#section`, go to `Url`.
- [ ] **Step 5: Descriptions** — in `Manual.cs`, one sentence each in `WriteDescription`, `EditDescription`, `ReplaceDescription` and the `cat` line of the shell manual: "The answer carries the note's link into the web app (it opens after sign-in): show it to the user." Update any test that pins these texts.
- [ ] **Step 6: Run the whole server suite** — `dotnet test --solution Kumbu.slnx`; expected: all pass (4 skip without the model).
- [ ] **Step 7: Commit** — `git commit -m "Return a note's deep link next to every version block"`

### Task 2: The vault's id in the vault list

**Files:**
- Modify: `src/Kumbu.Server/Api/NoteApi.cs:41` (`VaultView` gains `string Id` as its first parameter), `:385` and `:408` (pass `access.Id`, and the created vault's id), `web/src/api/schema.d.ts` (`VaultView.id: string`), and any web fixture building `VaultView`s (`rg -l "embeddings:" web/src --glob '*.test.*'` finds them; add `id`)
- Test: extend `tests/Kumbu.Server.Tests/Api/NoteApiTests.cs`

**Interfaces:**
- Produces: `GET /api/vaults` items carry `id`, equal for owner and member of one vault.

- [ ] **Step 1: Failing test** `Vault_list_carries_the_vault_id` — owner's and member's lists show the same `id` for the shared vault, equal to the catalog's id; `POST /api/vaults` answers with the new vault's `id`.
- [ ] **Step 2: Run** `dotnet test --solution Kumbu.slnx --filter Vault_list_carries_the_vault_id`; expected FAIL.
- [ ] **Step 3: Implement**; regenerate `schema.d.ts` with `pnpm -C web gen:api` against a running server if one can be started, else add the property by hand in the generated shape.
- [ ] **Step 4: Run** server tests, `pnpm -C web test --run`, `pnpm -C web build`; expected: pass.
- [ ] **Step 5: Commit** — `git commit -m "Name each vault's id in the vault list"`

### Task 3: The `/n/:vaultId/*` route

**Files:**
- Create: `web/src/routes/DeepLink.tsx`, `web/src/routes/DeepLink.test.tsx`
- Modify: `web/src/router.tsx` (inside `RequireSession`: `{ path: "/n/:vaultId/*", lazy: lazyPage(() => import("./routes/DeepLink")) }`), `web/src/routes/SignIn.tsx:55` (`if (next.startsWith("/notes/") || next.startsWith("/n/")) next = "/";`)

**Interfaces:**
- Consumes: `vaultsQuery` (`web/src/api/notes.ts:135`) with `id`; the notes and files read endpoints as `web/src/api/notes.ts` and `web/src/api/attachmentUrl` call them; `useEditor().setSearchQuery`, `setOverlay("search")` (as `web/src/search/SearchSheet.tsx` uses them).

- [ ] **Step 1: Failing Vitest tests** in `DeepLink.test.tsx` (MSW or the fetch mock the neighbouring route tests use):
  - `resolves_to_the_owners_name`: vaults `[{ id: "v1", name: "gartenarbeit", … }]`, note exists → location `/notes/gartenarbeit/Roses.md`.
  - `resolves_to_a_members_alias`: same id, name `garten` → `/notes/garten/Roses.md`.
  - `a_file_goes_to_the_lightbox`: `/n/v1/rose.png` → `/view/gartenarbeit/rose.png`.
  - `decodes_the_path`: `/n/v1/Garten%20%C3%9C/Rosen%20%231.md` → the note `Garten Ü/Rosen #1.md` is asked for and opened.
  - `unknown_id_shows_the_one_page` and `missing_path_shows_the_one_page`: the exact sentence from Global Constraints; a "Search for it" button that sets the search query to the note's name without `.md` (`Roses`) and opens the search.
  - `the_address_is_replaced` (no history entry for `/n/…`).
- [ ] **Step 2: Run** `pnpm -C web test --run DeepLink`; expected FAIL.
- [ ] **Step 3: Implement** `DeepLink.tsx`: read `vaultId` and `*` from params, wait for `vaultsQuery`, find by `id`, check existence (a note: `.md` ending, read via the notes API; anything else: the file read endpoint, a `HEAD` or the tree, whichever `web/src/api` already offers), then `navigate(target, { replace: true })`; otherwise render the page (heading "Not here", the sentence, the button). Match the styling of a neighbouring simple page (`routes/Join.tsx`).
- [ ] **Step 4: Sign-in round trip test** in `web/src/routes/SignIn.test.tsx`: `next=/n/v1/Roses.md` is followed after sign-in; for another user than the previous one it becomes `/`.
- [ ] **Step 5: Run** `pnpm -C web test --run` and `pnpm -C web build`; expected pass.
- [ ] **Step 6: Commit** — `git commit -m "Open deep links in the web app under the reader's own vault name"`

### Task 4: Docs and changelog

**Files:**
- Modify: `README.md` (where MCP tools are described: one paragraph on the link), `docs/security-architecture.md` §5.6 (the vault id is visible in deep links; it opens nothing)

- [ ] **Step 1:** Write both paragraphs.
- [ ] **Step 2: Commit** — `git commit -m "Document note deep links"`
- [ ] **Step 3:** After merge, the changelog entry from the spec goes into `/SWE/Kumbu/Versions/0.6.0.md` (Kumbu², not the repo).
