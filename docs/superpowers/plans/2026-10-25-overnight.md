# Overnight 2026-10-25 — Implementation Plan

> **For agentic workers:** each track below is self-contained. Read the whole of your track, the Global Constraints, and the files it names. Do not work on another track.

**Goal:** round off Kumbu² 0.1 into the next version: colour schemes, external embedding providers as drivers, the editor gaps from the browser test, history/trash/checkpoints in the web app, re-keying, and small security items. Everything lands on the integration branch `overnight-2026-10-25` and goes to `master` as **one pull request**. No merge to `master`, no tag, no release tonight (Chris, 2026-10-25).

**Inputs:** `concepts/MOBILE_EDITOR_v1.md`, `docs/concept.md`, `docs/security-architecture.md` (§9 roadmap, known gaps), `docs/notes-test-phase9a.md`, the accounts spec `docs/superpowers/specs/2026-10-24-accounts-and-web-design.md`.

## Global Constraints

- British English in code, comments, docs and UI; SPDX header `// SPDX-License-Identifier: AGPL-3.0-or-later` on new files; match the surrounding style and comment density.
- Tests first (xUnit for the server, Vitest for the web app). Before reporting: `dotnet test`, `pnpm -C web test`, `pnpm -C web build`, all green.
- Security first: validate at boundaries; secrets never in logs, URLs, browser storage or the TanStack caches beyond what is needed.
- Text first: no attachments (`docs/concept.md`, memory "text first in v1").
- `docs/security-architecture.md` must stay true to the code: a track that changes behaviour it describes updates it (claims with file:line).
- API changes: regenerate `web/src/api/schema.d.ts` with `pnpm -C web gen:api` against your **own** stack, never the shared one on :8083: `COMPOSE_FILE=compose.yaml:compose.build.yaml KUMBU_PORT=<your port> KUMBU_SEARCH=false docker compose -p kumbu-<track> up -d --build` and `KUMBU_OPENAPI_URL=http://localhost:<port>/api/openapi.json pnpm -C web gen:api`; afterwards `docker compose -p kumbu-<track> down -v`. The web build needs the new schema, so build the image once with the old web app if needed (known chicken-and-egg). Ports: T2 8091, T3 8094, T4 8092, T5 8095, T6 8093.
- Commits: imperative, one per coherent change, each ending with the session trailer given in your dispatch. No push, no subagents.

---

## T1 — Colour schemes

All schemes decided in `concepts/MOBILE_EDITOR_v1.md` §6 "Colour schemes": Kumbu (exists), Catppuccin Mocha and Latte (Latte exists), Tokyo Night and Tokyo Night Day, Gruvbox Dark and Light, Nord, Dracula, Solarized Dark and Light, Everforest Dark and Light, Rosé Pine and Rosé Pine Dawn, One Dark and One Light. Each as a Kumbu variant per §6 (background, surfaces and accent hue are the scheme's own; `muted`/`dim`/`accent-text`/`on-accent` adjusted until `contrast.test.ts` passes at 4.5:1). Files: `web/src/theme/schemes.ts`, `web/src/index.css`, the existing tests (`schemes.test.ts` keeps CSS and TS in step), the scheme picker in Settings (grouped dark/light, a small swatch per scheme). Use each scheme's published palette (cite the source in a comment per scheme).

## T2 — Embedding providers as drivers, with external providers

Ruling (Chris): providers are configured **per instance** by the admin; each **vault opts in** to an external provider; default stays internal. Design after chatsundere's `packages/llm-unified` (adapter registry, provider definitions, catalogue: read `../chatsundere/packages/llm-unified/src/adapter-registry.ts`, `adapter-contract.ts`, `types.ts`, `catalogue/`), in C#:

- An `IEmbeddingDriver` contract (embed a batch of texts → vectors; dimensions; max batch; a stable model key used to tag sealed embeddings) and a registry of drivers by name. Built-in drivers: `onnx` (today's arctic-m/arctic-l, unchanged behaviour) and `openai-compatible` (`POST {baseUrl}/embeddings`, `model`, `input[]`, optional `dimensions`; bearer key). Adding a provider = configuration; adding a protocol = a driver class.
- Configuration (`Kumbu:Embeddings:Providers:<name>:…` = `Driver`, `BaseUrl`, `Model`, `ApiKeyFile` (path to a file; never the key itself in env), optional `Dimensions`, `Label`, `Description` (e.g. "EU, zero data retention")). `Kumbu:Search:Model` keeps working for the internal model. Validate at startup (https only for external base URLs except loopback; key file readable; fail closed with a clear message).
- Per vault: the owner chooses `internal` or one configured external provider (REST: extend the vault endpoints; web: the vault's ⋯ menu in the drawer, a dialog explaining in plain words that the text of every section and every search query of this vault is sent to that provider, showing its label/description). Switching re-embeds the vault (the existing model-key mechanism); search uses the vault's provider for the query. Cross-vault search must still rank sensibly when vaults use different models (each vault's scores come from its own model; check how hybrid fusion combines them and state it).
- Robustness: timeouts, retries with backoff on 429/5xx, batch limits, no note text in logs, errors surfaced as "search by meaning unavailable" while word search keeps working.
- Test provider: TensorX, base URL `https://api.tensorx.ai/v1`, model `qwen/qwen3-embedding-8b`, key file `~/keys/.tensorx-api-key` (read only by tests you run locally, behind an env var such as `KUMBU_TEST_TENSORX_KEY_FILE`; integration tests skip without it; the key never enters the repo, logs or output). Use a fake HTTP handler for unit tests.
- Evaluation: run the search evaluation from step 7b (`docs/superpowers/plans/2026-10-24-search-tuning.md`, find its script) with qwen3-embedding-8b against arctic-l; record results in `docs/notes-search-external.md`.
- Docs: README (configuration, an example for TensorX), `.env.example`, and a new section in `docs/security-architecture.md` (what leaves the server, when, to whom; opt-in per vault; the provider's retention is the provider's promise, not something Kumbu² can enforce; the API key's handling).

## T3 — Key rotation and re-keying (after T2 and T6 are merged)

Implements `docs/security-architecture.md` §9 item 1. Write a short design first (`docs/superpowers/specs/2026-10-25-rekeying-design.md`) from the code as built — it must settle these, with the rulings below as defaults:

- Stage 1, rotate the user key: new user key; re-wrap the user's vault slots and the password slot (needs the password: available during a password change and recovery), the recovery slot (a recovery replaces the recovery key anyway; a password change keeps the recovery key → re-wrap needs it: so on a password change, rotation also issues a new recovery key shown once, or rotation happens only on recovery and on request with password — decide and justify), and session slots (only the current session can be re-wrapped; others end, which a password change does already).
- Stage 2, re-key a vault: lock the vault, new vault key, re-seal everything of the vault (notes, history, trash, moves, checkpoints and their texts, embeddings, sealed paths and names), recompute keyed lookup hashes, re-wrap the slots of every user with access. API key slots cannot be re-wrapped (the server never holds the key): re-keying revokes the API keys with access to that vault, and the UI says so first. If a vault can have several members, check how the code handles that and rule (re-key only if the server can re-wrap for every member, else refuse with a clear message). One MongoDB transaction; crash-safe; idle-lock/unlocked-vault caches dropped.
- Triggers: on recovery (rotate the user key and re-key the user's vaults; API keys are revoked there already), on request in Settings ("Re-encrypt my vaults", with password), and per vault in its ⋯ menu.
- Tests for each stage, crash points, revoked access afterwards, old credential + old backup no longer opens newly written data. Update `docs/security-architecture.md` (§3 "No key rotation", §6, §9, known gaps).

## T4 — Editor and navigation gaps

In `web/` (and the server only where an endpoint is missing):

1. `[[` completion in Write mode: typing `[[` opens a list of the vault's notes filtered as you type (name, then folder); Enter/Tab/tap inserts `[[name]]` (shortest unambiguous path when names repeat); Escape closes; works with the on-screen keyboard (list above the toolbar).
2. Rename and move a note and a folder (REST `/api/move` exists): in the note's ⋯ menu and the tree's ⋯ per folder/note; a dialog with the new name/folder; links: say whether the server rewrites links (check `/api/move`) and show that.
3. Obsidian callouts `> [!note] Title` (note, tip, info, warning, danger, quote; foldable `+`/`-`) rendered as styled boxes with tokens only.
4. Front matter shown as a compact properties block (key/value) instead of a code block; still part of the source in Write mode.
5. Empty search shows the last 5 opened notes (concept §4 Search) — check what exists.
6. The search sheet has **no back/close button** (Chris, 2026-10-25): add a visible close control in its header (and keep Escape and the dock behaviour).
7. Outline: a list of the note's headings to jump to (from the title bar), and find in the note (Read mode: highlight matches, next/previous).
8. Embeds `![[note]]` and `![[note#heading]]` of notes render the embedded note's content (one level deep, cycle-safe, with a link to open it); embeds of non-notes stay links.
9. Deferred minors from step 9a: on iOS the lazy search sheet's first open may not raise the keyboard (focus synchronously inside the tap); the note GET from `beforeRead` racing the sign-out DELETE; edge-swipe open/close of the drawer per concept §4 Tree.
Each item its own commit with tests.

## T5 — History, trash and checkpoints in the web app (after T4 is merged)

REST endpoints where missing (reuse Kumbu.Core's code paths the MCP tools use; same rights; check what `/api/history` already returns):
- A note's history: list versions (writer, time, size), view a version (rendered, with the line diff to the current one — `web/src/note/lineDiff.ts` exists), restore a version (a new write, so it is itself in the history).
- Trash per vault: list deleted notes (path, deleted at, by), restore, and the 30-day expiry stated.
- Checkpoints per vault: list, create with a name, restore (with a clear warning what it changes), remove (permanent — say so).
Entry points: note ⋯ menu ("History"), drawer vault ⋯ menu ("Trash", "Checkpoints").

## T6 — Small security items

1. Optional expiry for API keys: at creation none / 30 / 90 / 365 days; an expired key answers 401 like a revoked one; the list shows expiry; README and document updated.
2. Base images pinned by digest in `Dockerfile` (keep the tag in a comment) and Dependabot for `docker` and `nuget` added to `.github/dependabot.yml`; NuGet lock files (`RestorePackagesWithLockFile`, locked restore in CI and the Dockerfile).
3. MongoDB authentication in `compose.yaml`, optional: when `KUMBU_MONGO_PASSWORD` is set, MongoDB starts with a root user and auth (a single-node replica set with auth needs a key file: generate it in an init step inside the volume, not in the repo) and the server connects with credentials; unset keeps today's behaviour. Document how to enable it on an existing data volume. Note: the VPS stack in Chris's `vps-gitops` repo has its own compose and is out of scope; list in the report what it would need.
4. Account deletion: a user deletes their own account in Settings (password + typing the username); an admin deletes a deactivated user (typed confirmation). Deletion removes the user's slots, sessions, API keys and their slots, invitations created by them stay (state why), and the vaults they own with all their data (notes, history, trash, checkpoints, embeddings). Same for deleting a single vault (owner, typed vault name). Log without content. Update the document's "no deletion" statements.

## Order

Wave 1 in parallel worktrees: T1, T2, T4, T6. Wave 2 after they are merged into the integration branch: T3 and T5 in parallel. Then a final whole-branch review, one fix wave, and the pull request.
