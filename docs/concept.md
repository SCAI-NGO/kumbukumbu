# Kumbukumbu (Kumbu²) — Concept

> *kumbukumbu* (Swahili): memory, remembrance — also records, minutes.
> From *kumbuka*, "to remember". The reduplication intensifies the word: **kumbu × kumbu = kumbu²**.
> The superscript ² also reads as "second" — a second brain.

**Status:** Concept of October 2026. Steps 1 to 11 of the build order (§12) are built and were released with v0.2.0; work continues along the roadmap (§12), v2.5 next.
**License:** AGPLv3 (it's a hosted network service — exactly the case the AGPL exists for).
**Command / short name:** `kumbu`

---

## 1. What it is

A self-hostable, encrypted-at-rest **second brain for humans that AI agents may use**.

- Notes are plain Markdown. Structure grows bottom-up from headings and `[[wikilinks]]`, not from an imposed schema.
- Agents access it via **MCP** (Claude, ChatGPT, Grok, Claude Code, opencode, Hermes, Chatsundere …).
- Humans read and manage it via a small web UI (works on mobile).
- Core idea "explanted" from Project Synthesis (virtual filesystem), but server-side.

**Not** an AI memory system in the MemPalace sense (where the AI decides what to store and where). Kumbu² is *your* memory; the AI gets a key.

## 2. Project goals

1. **Learning (≥ 50 %).** Understand agentic systems from a developer's perspective. Rule: *nothing gets built that Chris can't explain in his own words first.* Explanation → diagram → code. A living `CONCEPTS.md` grows alongside the code as a personal glossary.
2. A usable, private, mobile-accessible second brain for Chris, friends and colleagues.

## 3. Threat model

- **Protects against:** leaks — DB dumps, stolen backups, a compromised hoster's disk.
- **Does not protect against:** a malicious operator of the running server (plaintext exists in RAM during requests; search requires it). Users who don't trust the operator self-host.
- **Note:** the LLM provider of the connected client sees whatever the agent reads. This is not E2EE and must never be labeled as such. The README says all of this plainly.

## 4. Data model

### Vaults
- **Every top-level directory is a vault** = a cryptographic boundary with its own vault key.
- A user creates their vaults themselves after joining (in the web app or with `POST /api/vaults`).
- Permissions exist **only at vault level** (read / write). No per-subdirectory rights.
- Paths always include the vault name (`/Work/notes/x.md`) — consistent addressing across keys. No `cd` — paths are always absolute.

### Notes
- Notes **must** end in `.md`.
- UTF-8, LF line endings.
- Max note size: generous (~1 MB), purely as abuse protection. The limit that matters is **tool output** (see §5).
- Sizes computed internally in characters; messages show approximate tokens (German ≈ 1:3, English ≈ 1:4 — no client-independent tokenizer exists).
- **No attachments in v1.** Text only; images and PDFs come in v2.5 (§12).
- Filenames: accepted as they come on import (seeding, sync); Kumbu² itself never *creates* Windows-incompatible names. `write` checks new notes, `mv` checks its target; `mv` may rename a badly named note to a good name, so such notes can be repaired.
- Frontmatter, Dataview blocks, inline fields etc. are preserved **as is**.

### Sections
- The heading is the natural unit: for addressing, chunking, embeddings.
- Address format (matches Obsidian's `[[note#H1#H2]]`):
  `/Work/projects/hecate.md#Architecture#Gates`
  - The **first `#` after `.md`** separates file from outline (a `#` in the filename before `.md` is harmless).
  - Further `#` separate heading levels.
  - A literal `#` inside a heading is escaped as `\#`.
  - True duplicates (same text, same level, same parent) get an index: `#Notes[2]`. Indexes are unstable, but version tokens make stale addresses fail loudly instead of silently editing the wrong section.

### Links & backlinks
- `[[wikilinks]]` parsed on write; edges (source → target) stored per vault, encrypted.
- `backlinks X` = all edges with target X, **filtered to vaults the caller can see** (no existence leaks across vaults).
- **Content is never rewritten automatically** (decided Oct 22, 2026): not on import, not on `write`/`edit`. Writes are stored verbatim; the Obsidian sync plugin depends on it, as any rewrite would come back to Obsidian as a remote edit.
- Links **resolve at read time**, leniently, by Obsidian's rules (shortest unique name; a path where the name is ambiguous).
- Kumbu² writes links only where it must, in `mv`, and then in the most specific form. No normalize command was built, and none is planned: content is never rewritten automatically (above).
- Links inside code blocks / inline code are never touched.
- `mv` rewrites all links pointing to the moved note.

### Versions & trash
- Every read returns a **version token** (ETag-like), kept separate from content.
- Every write must send it → optimistic concurrency.
- **Coalescing:** edits by the same key within 10 minutes merge into one version, whichever tool wrote them; such a group of writes is a **changeset**, and each history entry records its changeset id (decided Oct 24, 2026, after the phase 6 test; undoing a changeset across notes may come later); the last **10** versions per note are kept (encrypted) → `history`, `restore`. "State before this agent session" is always recoverable.
- A changeset ends after 10 minutes without a write by its writer, and after **1 hour** at most, so a long agent session or the Obsidian sync can't make one endless changeset. `restore` is a **checkpoint**: it never joins a changeset, so a restore can be undone on its own. (Decided Oct 24, 2026.)
- For people (the web app's session, an Obsidian device key) a changeset already ends after **2 minutes** without a write, as their editor saves every few seconds; agents (API keys, app connections, the dev token) keep 10 minutes, and the 1-hour cap is for both. The state a note is **created** with is always a version of its own, never coalesced away, so a conflict copy's first state, the evidence of the conflict, stays in its history. (Decided Oct 26, 2026, after the v0.2.0 dry run, #55.)
- **Named checkpoints** (v1, decided Oct 24, 2026): `mkcp <path> <name>` marks the current state of a note, a folder or a vault under a name; `lscp [path]` lists checkpoints; `rmcp <path> <name>` removes one. A checkpoint is kept until `rmcp`, never pushed out of the history and never expired, so it pins the content it points at beyond the 10 versions of the history. The manual tells agents to set or remove them **only when the user asks**. In Git terms a changeset is a commit and a checkpoint a tag: a named pointer to a state, set on purpose; unlike Git, which keeps every commit, Kumbu² keeps 10 versions, so a checkpoint must hold on to its content itself.
  - **Browse a checkpoint like the current state:** `--checkpoint <name>` on reading commands, as `--version` for one note, but also on `ls` and `rg` over folders, since a checkpoint covers a tree. `diff` against a checkpoint; `restore` to one, for a folder with a preview and plan token as `replace`.
  - **Web UI, v2 (0.2.0):** browse a checkpoint like the current state, as GitHub shows a repository at a commit, and **export** it (a zip of the notes as they were). Step 9 built making, restoring and removing checkpoints only; looking into a checkpoint (open it read-only, copy a note for the AI, export it as ZIP) came with v0.2.0.
  - **Limit:** 10 named checkpoints per vault by default, configurable: `Kumbu:Vaults:CheckpointLimit`, in `.env` as `KUMBU_CHECKPOINT_LIMIT` (documented in `.env.example` and the README). `mkcp` beyond the limit fails and names `lscp` and `rmcp`.
  - Built in step 6b, together with changesets.
- `rm` is a soft delete into trash; **trash expiry 30 days**.

## 5. Agent interface (MCP) — four tools

| Tool | Purpose | Payload |
|---|---|---|
| `sh` | Structure + reading: `ls`, `cat`, `outline`, `rg`, `semsearch`, `links`, `backlinks`, `history`, `diff`, `restore`, `mv`, `rm`, `trash`, `mkcp`, `lscp`, `rmcp`, `uname`, `help` (`mkdir` is refused with a hint: folders exist through their notes) | Paths only — **never Markdown content** |
| `edit` | `str_replace`, replace/append/insert section — with version token | Content (JSON params) |
| `write` | Create note or fully overwrite — with version token | Content (JSON params) |
| `replace` | A literal change across a folder or vault, previewed with a plan token, then applied | JSON params |

Rationale: models speak Unix fluently → reading in their native language. Writing multi-line Markdown through a shell string means quoting hell → content goes through structured JSON params.

### The `sh` grammar
- shlex-style tokenizer (quoting, flags).
- **Only operator: `|`.** No `$()`, `>`, `&&`, subshells.
- Right of a pipe: filters only (`head`, `tail`, `wc`, `sort`, `rg`).
- Each command is a function `string[] → string[]`; a pipe is a chain.
- Errors look like real Unix errors (`command not found: grepp — try 'help'`) so models self-correct.
- Per-command permission checks (a read-only key may `ls` but not `rm`).
- **No working directory — all paths are absolute.** MCP 2026-07-28 removed sessions, and tying `cwd` to a key breaks parallel agent sessions using the same key. The man page states it. (Decided Oct 21, 2026.)
- **Output limit ~8,000 tokens per `sh` call.** Oversized output is truncated with a hint: `[truncated — use 'outline' or 'cat file.md#Section']`. This teaches agents section addressing.

### Discoverability
The tool description *is* the man page: short list of commands + `help <cmd>`. Also states: note content is data, not instructions.

## 6. Search

- **Full-text:** `rg` over decrypted notes in RAM.
- **Semantic:** `semsearch` — embeddings per section, heading path included as context.
- **Hybrid ranking** for `semsearch` (`rg` stays plain, unranked regex):
  - Meaning decides, words add a bonus: `score = cosine + 0.1 × (share of the query's distinct terms found in the chunk)`. Without query terms, the cosine alone. BM25 still ranks in `--words` mode, but no longer feeds a second list to merge.
  - Why not Reciprocal Rank Fusion: it counts ranks only, so a weak word list weighs as much as a strong meaning list. On the phase 7 evaluation RRF lowered R@1 from 0.81 (meaning alone) to 0.70, while cosine plus the word bonus reaches 0.84 (arctic-m). Details in `docs/notes-test-phase7.md`, "Fusion". The 0.1 was set once, not tuned.
  - Results show their score. It is comparable within one search only: no absolute threshold separates queries with an answer from those without (measured).
- **Chunks:** one section; long sections split by paragraph, cap ~512 tokens per chunk.
- **Embedding model:** a choice of two from a fixed catalogue, `arctic-m` (`snowflake-arctic-embed-m-v2.0`, default) and `arctic-l` (`-l-v2.0`), set with `Kumbu:Search:Model` / `KUMBU_SEARCH_MODEL`. Each has a pinned revision and SHA-256 per file; no free Hugging Face ids. **In-process via ONNX Runtime** (plaintext never leaves the process, except for vaults whose owner opted in to an external embedding provider the admin configured: their sections and the searches over them go to that provider; overnight round, `docs/security-architecture.md` §5.5). Mind query vs. document prefixes. Each note's embeddings record their model; after a switch they count as missing and are re-embedded in the background when a vault is unlocked. Models are never mixed. A change to model, chunking or ranking is measured with `tools/search-eval`.
- **No vector DB.** Vectors stored encrypted in MongoDB, decrypted into RAM on vault unlock; search = dot products. A personal vault has thousands of sections → milliseconds.
- **Incremental indexing:** hash per section, re-embed only what changed, embedding runs async.

## 7. Cryptography

- Each vault has a random **vault key**.
- Vault keys are **wrapped** by key slots (LUKS-style):
  - **API key slot:** API keys are high-entropy random → **HKDF**. Two derivations with different labels: a *lookup hash* (stored, for identification) and the *KEK* (never stored).
  - **Password slot:** passwords are low-entropy → **Argon2id**.
  - **Recovery key slot:** generated at registration, **must** be downloaded/confirmed.
  - **OAuth token slot:** see §9.
- Revoking a key = deleting its slot. Password change = rewrap. Since step 10, **re-keying** goes further: a new user key or a new vault key, because revoking a slot doesn't protect old copies of the database (`docs/security-architecture.md` §3).
- **An admin cannot reset a password or recover a vault.** No password + no recovery key = data gone. Documented honestly.
- Built in step 5 (Oct 23, 2026): vault keys, API-key slots (the dev token until step 8) and recovery slots; AES-256-GCM with the document's context as associated data; paths and vault names encrypted, with keyed path hashes for uniqueness. Password slots (Argon2id, 64 MiB, 3 passes, 4 lanes) came with logins in step 8. Recovery keys are written once to a file for the operator, never logged.
- **Unlocked vault = in-memory working set** (texts, vectors, link graph). Server restart = everything locked (feature).
  - Idle TTL: **15 min** for every unlocked vault, whoever opened it (`Kumbu:Vaults:IdleMinutes`); a web session itself ends after 30 idle minutes.
  - Global memory cap (configurable, default ~512 MB), LRU eviction of whole vaults.
  - A request may only use a cached unlocked vault if it presents a valid credential for *that* vault. The cache saves computation, never replaces authorization.
- **Never** in Redis, logs or URLs: plaintext content, vault keys, KEKs.

## 8. Users & admin

- **Bootstrap by command, not by log** (built in step 8, replacing the earlier idea of a token printed to the container log on first start): `docker compose exec kumbu kumbu bootstrap-admin` prints a one-time invitation link (`…/join#<code>`, 24 hours, replaced when run again, refused once a primary admin exists). Whoever opens it becomes **primary admin**; the race for the first account is won by the person with a shell on the container, not by whoever first finds the server. Not the log, because logs are collected, shipped and kept, and anyone who can read that line would become primary admin. The same reasoning as the recovery key's file (§7). Needs `KUMBU_PUBLIC_URL`.
- Admins can promote other admins. Primary admin cannot be removed.
- **Invites:** single-use, expiring, revocable links.
- **Deactivating a user** kills all their API keys and sessions immediately.
- Admins see metadata (who, storage used), never content.
- **API keys:** named, per-vault read/write rights, shown once at creation, afterwards only by prefix (`kumbu_ab12…`). A key without at least one right can't exist (no slot = can't decrypt anything).
- Rate limiting on login and invalid keys, in memory (one instance).
- **Later:** sharing vaults between users, which needs asymmetric crypto (v3); TOTP/2FA (v4).

**Login through GitHub** (direction from Chris, Oct 24, 2026; **postponed, not built**): in many companies every internal tool for developers (Rancher, Argo CD, Mongo Express …) signs in through GitHub, and Kumbu² should fit in. To settle when it is taken up:
- **GitHub as the identity provider, limited to an organisation and optionally a team** (`KUMBU_GITHUB_ORG`, `KUMBU_GITHUB_TEAM`), the pattern of oauth2-proxy and Argo CD. The first login of a member creates the user; leaving the organisation ends the access. Invites become an option for deployments without an organisation.
- **Admins through a GitHub team** (`KUMBU_GITHUB_ADMIN_TEAM`) instead of, or next to, the bootstrap token.
- **GitHub says who someone is, not how to open their vaults.** A vault opens with a secret of the user's (a passphrase or the recovery key, a passkey with PRF in v4); the identity provider never holds key material, so a GitHub login alone, or an admin, can't read anyone's notes. Once a vault is open, API keys and OAuth tokens carry slots as in §9.
- **Later (v4): any OIDC provider** (Entra ID, Keycloak), GitHub being the first. The MCP OAuth flow (§9, step 10) uses the same login at `/authorize`; Claude and other clients are OAuth clients and bring no identity of their own.

## 9. Client access

- **Bearer API keys:** Claude Code, opencode, Hermes, Chatsundere etc.
- **OAuth 2.1:** app connectors (Claude.ai, ChatGPT, Grok). Kumbu² is **its own authorization server** — no external identity provider needed (that's only the "Login with Google/GitHub" use case). A connection is just another key slot: its vaults and rights are chosen when the user creates it on the Access page, and the consent screen then only allows or denies — no second permission world.
- Bearer API keys + OAuth cover every client type; **no keys in URLs** (proxy logs, and here keys are key material).

### OAuth flow (for reference; as built in step 10 the client uses the connection's own address `/mcp/c/{id}`, the endpoints are `/oauth/authorize` and `/oauth/token`, and clients identify themselves by a client metadata document, with dynamic registration as a fallback; `docs/security-architecture.md` §6)
1. Client calls `/mcp` without token → `401` pointing to metadata.
2. Client reads `/.well-known/…` metadata → finds Kumbu² as authorization server.
3. Client identifies itself: its `client_id` is the HTTPS URL of a client metadata document, which Kumbu² fetches; a client without one registers itself (`POST /oauth/register`, dynamic client registration) and gets a `client_id`, never a secret.
4. Client opens the **user's browser** at `/authorize` with `client_id`, redirect URI and **PKCE** challenge.
5. User logs in **at Kumbu²** (client never sees the password) and consents: which vaults, read/write.
6. Kumbu² redirects back to the client's callback with a one-time **authorization code**.
7. Client's server exchanges code + PKCE verifier at `/token` → access token + refresh token.
8. Every MCP call carries `Authorization: Bearer <access token>`.

The browser is the "mediator" — trust comes from the registered redirect URI + PKCE, not from a third party.
**Consequence:** Kumbu² must be publicly reachable for app connectors (calls come from the providers' servers).

### Tokens as key slots
- **Refresh token carries the slot** (vault key wrapped via HKDF from the refresh token).
- **Access token gets an ephemeral slot:** on refresh, the server unwraps with the refresh token, issues a fresh random access token and wraps the vault key under it too; that slot has a Mongo TTL (~1 h) and is physically deleted on expiry.
- **Rotation:** each refresh issues a new refresh token → new slot, old slot deleted.
- **Revocation:** delete all slots of the grant — immediate.
- (Rejected: self-contained access tokens carrying the encrypted vault key — stateless, but not revocable before expiry.)
- **README note:** the refresh token lives at Anthropic / OpenAI / xAI, so they hold key material to the vault. Consistent with the threat model (they see content when reading anyway), but stated plainly.
- Implementation: OpenIddict was evaluated and decided against; Kumbu² has its own, small authorisation server, written against the MCP authorisation specification and tested end to end with the SDK's OAuth client (step 10).

## 10. Obsidian compatibility

- **Export:** near-trivial — it's Markdown. Link resolution must follow Obsidian's rules (shortest unique name) or be exported in fully-qualified form.
- **Import:** notes are stored as they come (content is never rewritten, §4). Plugin syntax preserved as is, otherwise ignored. A ZIP import of whole vaults came with v0.2.0; the browser unpacks it, and the notes go up through the ordinary upload.
- The work project (Obsidian + Nextcloud, plain files) is a **separate thing**. Import/export is the bridge.

## 11. Tech stack

- **Backend:** C# / .NET current LTS, the official MCP C# SDK (`ModelContextProtocol.AspNetCore`)
- **Persistence:** MongoDB (notes, versions, slots, vectors, link graph — all vault data encrypted)
- **Transient:** no Redis. Sessions and invitations live in MongoDB, rate limits and unlocked vaults in memory. Redis comes once there are several instances (hints, rate limits, pairing, the vault cache together).
- **Embeddings:** ONNX Runtime in-process
- **Frontend:** TypeScript + Vite + React + Tailwind, pnpm — admin (users, invites, keys, vaults) + Markdown reader
- **Deployment:** `compose.yaml`; on the VPS, Komodo follows the published image's `:latest`, which moves only on a release tag
- **Branding:** project name *Kumbukumbu*, wordmark *Kumbu²*, command `kumbu`. README explains the word's origin. No Africa clichés in the logo.

## 12. Build order (walking skeleton first)

1. MCP server in C# with `sh` (`ls`, `cat`), notes as plaintext in Mongo, wired into Claude Code → see how a model talks to the tool.
2. `sh` parser + pipes (unit-tested — deterministic, perfect test target).
3. `edit` / `write` with version tokens; history + trash.
4. Heading index, sections, links, backlinks (unit-tested). The link normalizer was dropped: content is never rewritten (§4).
5. Encryption: vaults, slots, HKDF/Argon2id, recovery key.
6. Agent tools for larger changes: view and diff earlier versions, changes across many notes.
6b. Changesets (with changeset ids in the history) and named checkpoints (`mkcp`, `lscp`, `rmcp`, `--checkpoint`), plus the fixes from the phase 6 test (`docs/notes-test-phase6.md`).
7. Embeddings + hybrid search; first, the decisions and fixes from the phase 6b test (`docs/notes-test-phase6b.md`): a folder restore moves moved notes back and rewrites their links; `replace` warns about links that would lead to another note and names the note named after the old text; small fixes.
8. Users, admin, invites, API keys. **Built** (users with a user key, sessions as slots, API keys with per-vault rights sealed into their slots, invitations, the `bootstrap-admin` command, admin tools, and the REST API under `/api`; `CONCEPTS.md` chapter 10).
9. Web UI and the REST API it shares with the Obsidian plugin (OpenAPI), designed with sync in mind. Direction (Chris, Oct 24, 2026): the mobile editor in `concepts/` (spec `MOBILE_EDITOR_v1.md` and a clickable prototype, made with Claude instead of Claude Design); the rest of the UI (admin, API keys, settings) is built around it. **Built** (`web/`, served from the image; `CONCEPTS.md` chapter 11). The prototype is the template for look and feel only; where it differs from this concept (links across vaults, per-chunk authorship, note ids, keys instead of the user), the concept wins. TypeScript. Colour schemes per user: the classic Linux schemes, Catppuccin and Tokyo Night among them, and light ones, each adjusted to the spec's contrast rules.
10. OAuth authorisation server for app connectors, and re-keying. **Built** (connections at `/mcp/c/{id}`; `docs/security-architecture.md` §3 and §6; `CONCEPTS.md` chapter 12).
11. Obsidian sync plugin. **Built** for v0.2.0 (the plugin in its own repository; on the server pairing codes and device keys, the version list with `since`, hints over a WebSocket; on the device conflict copies and the first-sync merge; `docs/superpowers/specs/2026-10-26-obsidian-sync-design.md`, `docs/security-architecture.md` §6 "Device pairing and sync", `CONCEPTS.md` chapter 13). Since attachments, part 1, it syncs PDFs and images too.

Renumbered on Oct 23, 2026 (decided by Chris): step 6 was added, and the Obsidian plugin moved from "later" to the end of the build order, after the core is done and deployable. Documents written before that date use the old numbers: their step 6 is now 7, 7 is 8, 8 is 9 and 9 is 10. Step 6b was added on Oct 24, 2026 without renumbering, so step numbers stay stable from here on.

**A plugin package for the user's agent** (decided by Chris, Oct 24, 2026) handles what the tools can't judge: changes that need context, like renaming a project whose notes describe the name without containing it (`docs/notes-test-phase6b.md`). Its skills, a rename skill first, read before they replace and ask where the user's intent is unclear. It lives in its own repository and is distributed through a plugin marketplace; with the Obsidian sync plugin, Kumbu² then spans three repositories.

**Roadmap** (decided by Chris, Oct 26, 2026; the versions are the release numbers; v3 and v4 swapped on Oct 28, 2026):
- **v1 (0.1.0):** the base, done; an internal test version.
- **v2 (0.2.0):** the first public release, released in October 2026. Obsidian sync with pairing codes and device keys, install as an app with self-update, export and import as ZIP, welcome vault, activity and search in earlier versions, GDPR deletion, looking into a checkpoint (open it read-only, copy it for your AI, export it as ZIP).
- **v2.5 (0.2.5):** Kanban boards, markdown links in `links`, `backlinks` and `mv`, attachments, and tags (read as Obsidian reads them, listed with `tags`, a filter on `rg`, `semsearch` and the web app's search).
- **v3 (0.3.0):** sharing vaults and collaboration ("think together"; concept in `concepts/collab-concept.md`). It extends the crypto model (asymmetric keys, slots for other users, revocation), gets its own section in `docs/security-architecture.md` and an independent review before it is built.
- **v4 (0.4.0):** TOTP/2FA, OIDC single sign-on, passkeys with PRF, and OKF bundles. Under discussion: Kumbu² as a skills repository.

Why the swap (Chris, Oct 28, 2026): sharing is the lower-hanging fruit with the larger effect at this stage, and the sign-in options get more feedback from real use first. OKF bundles move along with them: nobody asks for them yet; they are about data portability, so that people can leave Kumbu² for something better and never stay because their notes are locked in, which matters once there are users.
- **v5 (1.0.0):** "Time Machine": automatic checkpoints, and browsing a vault as it was on a date without a checkpoint made beforehand (today: 10 versions per note, 30 days of trash, at most 10 named checkpoints).

v1 is text only, on purpose. Attachments (images, PDFs) come in v2.5, to be designed with the same care as the text core, not added on the side. They are read by the AI as image parts plus extracted text, page by page; research in `docs/attachments-client-research.md`. Decided so far: the paragraphs below; still open: the size steps' numbers (from the test in §6 of the research). The way in, uploading an attachment, came with attachments, part 1.

**Attachments, part 1: built** (Oct 28, 2026; `docs/superpowers/specs/2026-10-27-attachments-foundation-design.md`, hands-on in `docs/notes-attachments-hands-on.md`). PDFs and images (PNG, JPEG, GIF, WebP, up to 15 MB) are stored as a small sealed record per file, kept and versioned where notes are, and their bytes as sealed blobs of their own, named by an HMAC under a key derived from the vault key and read on demand only, never into an unlocked vault's working set (`docs/security-architecture.md` §5.4). The Obsidian plugin syncs them both ways like notes, with conflict copies, and a notice for a file over 15 MB. Tickets turned out to be needed only for downloading several files at once as a ZIP, which is treated as an export; the web app and the plugin fetch single files with their session or key, so the question of tickets in URLs above is settled for people and programs. `img` and PDF text are the next two packages.

**Attachments reach the model as content it can fetch and interpret, never as hex or base64 in a tool's text output** (decided by Chris, Oct 24, 2026). Encoded bytes in text cost a fortune in tokens and mean nothing to the model. The precedent is an earlier expert system of Chris's: base64 was considered there first, then replaced by **download tickets**, which worked much better. **Revised on Oct 27, 2026:** that precedent fetched images to *show* them to a person, and so do Kumbu²'s tickets: downloads in the web app, Obsidian sync, export. For the *model*, the research found that no major chat client fetches a link by itself, while MCP's `image` content does reach the model as an image: base64 is only the packing on the wire, and the client hands it to the model API as an image block. So the model gets images as `image` content, and tickets serve people and programs. How a ticket squares with "no keys in URLs" (§9) was settled by attachments, part 1 (above): single files are fetched with the session or key, and a ticket is needed only for downloading several at once as a ZIP, which is treated as an export.

**Images in v2.5: `img`, an image viewer for the model** (decided by Chris, Oct 27, 2026). Kumbu² can't know a model's image limits (MCP names the client at most, never the model), so the agent chooses and the server tells it enough to choose well:

- `img` is a command in `sh`, "`cat` for images". It answers with an `image` block and a text block naming the file, its original size, the step and the delivered size, and what to do if the image doesn't arrive, so a model notices a silent loss (ChatGPT's connectors currently drop images, §2.6 of the research).
- **Size steps** `small`, `medium`, `large`, each a pixel budget (roughly a token budget) and a hard long edge. An image that doesn't fit is scaled down, keeping its aspect ratio; never rejected, never enlarged. `help img` gives the exact numbers, so a model that knows its limits picks the right step up front.
- **Crop** at full resolution for details, in the original's pixel coordinates, which the text block names. Very wide or tall images get a hint to crop.
- **No server-side de-duplication.** The server can't know what is still in the model's context (after a client compacts it, the pixels are gone and fetching again is right). The image's version travels with it, as a note's does, so an agent can check for a change itself.
- **No image library on the server yet.** ImageSharp was the plan (managed code, no native decoder), but its current versions need a licence key to build and the older ones have known vulnerabilities, so attachments, part 1 dropped it: the server reads an image's size from its header with a parser of its own, decodes no pixels, and refuses images over 50 megapixels or 16,384 px on a side before anything else (`docs/security-architecture.md` §7, "Attachments: untrusted bytes in"). `img` decodes pixels and still needs a decoder chosen (a licensed ImageSharp, or another library; `docs/notes-attachments-hands-on.md`, "Offen für Chris"), with these limits plus capped decoder memory and time, against decompression bombs. **Chosen in attachments, part 2 (below):** images are decoded in the PDF sidecar, not in the server.
- A model without vision gets the text block and its own error; Kumbu² is made for people who choose their tools.

**PDFs in v2.5: text per page and embeddings, no AI on the server** (decided by Chris, Oct 27, 2026). Most of what goes into a vault is text, and a simple path that ships beats an elaborate one that doesn't. The original file is kept and the extracted text is stored per page with the extractor's version, behind an exchangeable extraction step like the embedding provider, so a later, AI-assisted import ("attachments deluxe", part of a broader enterprise stage) adds a provider rather than replacing the design. Scanned PDFs without a text layer yield no text in v2.5, and the app says so. **Revised on Oct 28, 2026** (attachments, part 2, below): they still yield no text, but their pages can be seen.

**Attachments, part 2: the model reads attachments, built** (Oct 29, 2026; `docs/superpowers/specs/2026-10-28-attachments-model-reading-design.md`, plan `docs/superpowers/plans/2026-10-28-attachments-model-reading.md`). The two packages part 1 announced, `img` and PDF text, became one: they share so much that they are one package with two commands. A clean PDF with a text layer is the best case, not the normal one; scans and papers with charts matter from the start, so the model must be able to *look at* a page, not only read its text layer. The server still interprets nothing: it shows, and the model understands.

- **One pixel pipeline for both.** Size steps `small` (768 px long edge, 0.4 MP, a byte target of 150 KB), `medium`, the default (1568 px, 1.2 MP, 400 KB) and `large` (2576 px, 3.7 MP, 1 MB), in one place (`src/Kumbu.Core/Attachments/SizeSteps.cs`) and printed by `help img` and `help pdf`. An image is scaled down to fit both limits, never enlarged, never refused for its size; a PDF page or region is rendered to fit them, at most at 600 dpi. PNG when it fits the byte target, else JPEG from quality 85 down to 60. A crop is in the original's pixels for an image (EXIF orientation applied) and in points of the page as displayed for a PDF, where the region is rendered anew, so vector charts stay sharp. Every image comes with a text block naming the file, its version, the original and delivered sizes and the step, and the line that tells the model to say so if no image arrived. At most 4 images per call; `img` and `pdf` stand alone, never in a pipeline. The numbers stay provisional until the client test of research §6.
- **`img <path> [--size S] [--crop x,y,w,h]`**, as decided on Oct 27 (above); on a PDF it points to `pdf`.
- **`pdf`: a PDF is a list of pages, and every page has a type**, from a deterministic analysis on upload: `image` (one image covers at least 85 % of the page: a scan, with or without an invisible text layer), `mixed` (images and vector figures cover at least 25 %: charts) or `text` (anything else). `pdf info <path>` gives the page count, title, author, the types in runs and the figure regions in points, ready for `--crop`, of each non-text page and each text page whose images and drawings cover 5 % or more. `pdf <path> [pages]` reads text by default, each page under a header naming its type and what was found, with the hint to add `--images` when the page holds more than text (`--images=all` or `--crop` for such a text page, which `--images` leaves out): images are never spent unasked. `--images` adds the `mixed` and `image` pages as pictures, `--images=all` every page of the range, `--crop` a region of one page. At most 20 text pages per call, fewer within the output limit. `cat` on a PDF gives its page count and types. Scanned pages yield no text (no OCR, no AI on the server, unchanged), but can be seen with `--images`. `rg` finds text in PDFs (`/Work/paper.pdf:p7: …`) and `semsearch` ranks their pages among the notes' sections (`/Work/paper.pdf p7`), from the stored analysis; neither reads images or scans.
- **The analysis is sealed with the PDF's blob**, as the bytes are: page text and measurements in parts in `pdf_analyses`, never in the unlocked vault's working set, deleted with the blob, sealed again on re-keying; queued durably in `pdf_jobs` and run while the vault is unlocked, or by `pdf` itself when it is still pending (`docs/security-architecture.md` §5.4).
- **The PDF sidecar.** Untrusted PDFs are never parsed, and untrusted images never decoded, in the process that holds vault keys. Both happen in a Node service of its own (`pdf/`, image `ghcr.io/scai-ngo/kumbukumbu-pdf`): pdf.js for parsing (JavaScript, so memory-safe; the engine the web app shows PDFs with, so people and the model see the same pages) and `@napi-rs/canvas` (Skia) for drawing and decoding. It is stateless, gets one file per request, runs each job in a worker killed on a time limit, and sits on an internal compose network with the server alone, with no way out (`docs/security-architecture.md` §7, "The PDF sidecar"). PdfPig and PDFium were rejected: two parsers would disagree on page boxes and rotation, and PDFium is native throughout.
- **Images are decoded in the sidecar, not with ImageSharp** (a deviation from the design, which named ImageSharp): its current versions need a licence key to build and older ones have known vulnerabilities (above), while the sidecar already has Skia behind the same isolation, limits and time-outs as the PDF parser, a stricter isolation than any in-process decoder. One pipeline in one place. The server still checks an image's header (50 MP, 16,384 px on a side) before sending it, and checks what comes back. The cost: `img` works only while the sidecar is up.
- **A measuring instrument.** v2.5 is meant to show what agents ask for: the dashboard's "Attachments" row counts page types and coverages, `pdf` calls by mode, pages delivered as text and as images, and text pages rendered anyway, the sign that the 25 % threshold misses figures (README, "Monitoring").

v4 brings **OKF bundles as an opt-in per directory** (decided by Chris, Oct 24, 2026): a directory that declares itself an [Open Knowledge Format](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md) bundle gets OKF's rules and tooling; every other directory stays a plain Obsidian folder without them. Open questions in §13, analysis in `docs/okf-gap-analysis.md`. Writer identities (step 8) already tell humans from agents; markdown links (`[text](/path.md)`) in `links`, `backlinks` and `mv` come in v2.5 with the Kanban boards, ahead of the bundles.

v2.5 brings **Kanban boards** (decided by Chris, Oct 24, 2026, from community feedback): several boards, and API keys that can be assigned boards, so an agent works on the boards it was given. One vault carries at most one board, and cards are on a par with notes. A card is a note, with tools for atomic card operations (design of Oct 27, 2026, §13).

Each step = a chapter in `CONCEPTS.md`, numbered on its own (steps 8 to 11 are chapters 10 to 13).

## 13. Open questions

- *Resolved:* MCP authorisation details and OpenIddict → an own authorisation server with client metadata documents and dynamic registration (`docs/security-architecture.md` §6); the official MCP C# SDK is in use.

### Obsidian sync (build order step 11)

*Superseded by the design of 2026-10-26 (`docs/superpowers/specs/2026-10-26-obsidian-sync-design.md`): pairing through "Connect Obsidian…" instead of pasting a key, device keys, a first-sync merge. Kept for the record.*
- **Obsidian sync plugin (preferred, leaning decision Oct 22, 2026).** Humans keep a local copy of their vaults in Obsidian; a plugin syncs it with Kumbu². Chosen over mounting because it works offline and does not fight Obsidian's file watching over a network drive.
  - Own GitHub repo (TypeScript, pnpm, esbuild): Obsidian plugins are distributed from GitHub releases. Test installs via BRAT; listing in `obsidianmd/obsidian-releases` later.
  - Authenticates with an API key (§7) as `Authorization: Bearer <key>`; HTTP through Obsidian's `requestUrl` (no CORS, works on mobile).
  - **One key per device**, so a lost device is revoked by deleting its slot without touching the others.
  - **The key lives only in Obsidian's `SecretStorage`** (API since Obsidian 1.11.4), never in the plugin's `data.json`, which sits inside the vault and would be synced with it. The plugin refuses to run without it: `minAppVersion` `1.11.4` in `manifest.json`, a load-time check for `app.secretStorage`, and no plain-text key field in its settings.
  - Known limits of `SecretStorage` to verify before relying on it: the docs only promise local storage keyed to the vault, not OS-keychain encryption at rest; on mobile, secrets with the same name were reported to leak across vaults, so secret names include the vault (e.g. `kumbu-<vault>-<device>`).
  - Later: an OAuth login flow (§9) instead of pasting a key.
- **Mounting as an alternative** (not planned): **WebDAV** (mountable without extra software; ETags and locks map onto version tokens), **SFTP** (true sshfs; server-side SSH libraries for .NET are scarce) or an **own FUSE client** over the API. A mount keeps a vault unlocked, which works against the idle TTL (§7).
- To settle either way: writes must go through version tokens and update links, backlinks and the search index; Obsidian's non-note files (`.obsidian/`, temp files, attachments) need a policy; sync needs a conflict strategy for offline edits. Plaintext on the server adds no new exposure, as search needs it already.

### OKF bundles (v4, opt-in per directory)
Background and the full gap analysis: `docs/okf-gap-analysis.md`.
- **The flag is OKF's own marker:** a directory is a bundle when its `index.md` carries `okf_version` in its frontmatter (OKF §12). No Kumbu²-only metadata; the flag travels with export and the Obsidian sync, and the user sees and sets it in Obsidian. Leaning, not decided.
- **What enforcement means.** Refuse (writes without `type` fail with a teaching error), warn (writes pass, `okf check <path>` reports), or mixed: refuse agent writes, only warn on writes from the Obsidian sync, as refused sync writes would surface as conflicts the user can't make sense of. Leaning towards mixed.
- **Edge cases:** `mv` of a note without `type` into a bundle; `replace` across a bundle boundary; bundles nested in bundles; an existing `index.md` or `log.md` note that becomes reserved when the flag is set.
- **`index.md` and `log.md` are synthesised on read and on export, not stored** (OKF allows it: "consumers MAY synthesize one on the fly"). Stored, they would be server-written content that the sync carries back to Obsidian.
- Out of scope: Attested Computation (made for data catalogues; its attesters are code files, against text-only).

### Kanban boards (v2.5)
Designed on Oct 27, 2026: `docs/superpowers/specs/2026-10-27-kanban-design.md`. **One vault, one board** (decided by Chris, Oct 24, 2026): a vault carries at most one board, and several topics mean several vaults, a deliberate limit that nudges towards working with vaults actively (Kumbu² as a "chaos avoider"). The value behind the earlier leaning towards "its own data model" was atomic card operations with tools of their own; the design keeps that and stores the board as notes (Chris, Oct 27, 2026): **a card is a note** in `/Board/`, its column and rank in its frontmatter, the board defined by `/Board/Board.md` (free columns from a template, with instructions for the AI). So cards are on a par with notes without anything rebuilt: history, checkpoints, links and backlinks, `mv`, trash, search and the Obsidian sync apply as they are. Agents read the board with `board` in `sh` and change it with the `board` tool (add, move, update, create, rename a column); a move changes one note's frontmatter only. No import or export in the Obsidian Kanban format: the cards are notes Obsidian reads already. A board view in the Obsidian plugin comes later, alongside v4/v5. The general tag search the Kanban design left for later is built (`docs/superpowers/specs/2026-10-28-tags-design.md`): cards are notes, so `tags`, `rg --tag` and `semsearch --tag` cover them, and `board --tag` matches nested tags as they do.

### Resolved (Oct 21, 2026)
- Token lifecycle vs. key slots → §9 "Tokens as key slots".
- Static bearer headers for app connectors → moot, OAuth covers them.
- Duplicate-heading disambiguation → §4 "Sections".
- Sizes, versions, trash, working set → §4, §5, §7.
- Hybrid ranking → cosine plus a word bonus (not Reciprocal Rank Fusion), §6.
