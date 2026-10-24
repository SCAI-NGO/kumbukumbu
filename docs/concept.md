# Kumbukumbu (Kumbu²) — Concept

> *kumbukumbu* (Swahili): memory, remembrance — also records, minutes.
> From *kumbuka*, "to remember". The reduplication intensifies the word: **kumbu × kumbu = kumbu²**.
> The superscript ² also reads as "second" — a second brain.

**Status:** Concept, October 2026. Nothing built yet.
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
- On user creation, one vault is created (the user names it).
- Permissions exist **only at vault level** (read / write). No per-subdirectory rights.
- Paths always include the vault name (`/Work/notes/x.md`) — consistent addressing across keys. No `cd` — paths are always absolute.

### Notes
- Notes **must** end in `.md`.
- UTF-8, LF line endings.
- Max note size: generous (~1 MB), purely as abuse protection. The limit that matters is **tool output** (see §5).
- Sizes computed internally in characters; messages show approximate tokens (German ≈ 1:3, English ≈ 1:4 — no client-independent tokenizer exists).
- **No attachments.** Text only.
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
- Kumbu² writes links only where it must, in `mv`, and then in the most specific form. An explicit normalize command may come later.
- Links inside code blocks / inline code are never touched.
- `mv` rewrites all links pointing to the moved note.

### Versions & trash
- Every read returns a **version token** (ETag-like), kept separate from content.
- Every write must send it → optimistic concurrency.
- **Coalescing:** edits by the same key within 10 minutes merge into one version, whichever tool wrote them; such a group of writes is a **changeset**, and each history entry records its changeset id (decided Oct 24, 2026, after the phase 6 test; undoing a changeset across notes may come later); the last **10** versions per note are kept (encrypted) → `history`, `restore`. "State before this agent session" is always recoverable.
- A changeset ends after 10 minutes without a write by its writer, and after **1 hour** at most, so a long agent session or the Obsidian sync can't make one endless changeset. `restore` is a **checkpoint**: it never joins a changeset, so a restore can be undone on its own. (Decided Oct 24, 2026.)
- **Named checkpoints** (v1, decided Oct 24, 2026): `mkcp <path> <name>` marks the current state of a note, a folder or a vault under a name; `lscp [path]` lists checkpoints; `rmcp <path> <name>` removes one. A checkpoint is kept until `rmcp`, never pushed out of the history and never expired, so it pins the content it points at beyond the 10 versions of the history. The manual tells agents to set or remove them **only when the user asks**. In Git terms a changeset is a commit and a checkpoint a tag: a named pointer to a state, set on purpose; unlike Git, which keeps every commit, Kumbu² keeps 10 versions, so a checkpoint must hold on to its content itself.
  - **Browse a checkpoint like the current state:** `--checkpoint <name>` on reading commands, as `--version` for one note, but also on `ls` and `rg` over folders, since a checkpoint covers a tree. `diff` against a checkpoint; `restore` to one, for a folder with a preview and plan token as `replace`.
  - **Web UI (step 9):** browse a checkpoint like the current state, as GitHub shows a repository at a commit, and **export** it (a zip of the notes as they were).
  - **Limit:** 10 named checkpoints per vault by default, configurable: `Kumbu:Vaults:CheckpointLimit`, in `.env` as `KUMBU_CHECKPOINT_LIMIT` (documented in `.env.example` and the README). `mkcp` beyond the limit fails and names `lscp` and `rmcp`.
  - Built in step 6b, together with changesets.
- `rm` is a soft delete into trash; **trash expiry 30 days**.

## 5. Agent interface (MCP) — three tools

| Tool | Purpose | Payload |
|---|---|---|
| `sh` | Structure + reading: `ls`, `cat`, `rg`, `outline`, `semsearch`, `backlinks`, `tags`, `history`, `restore`, `mkdir`, `mv`, `rm`, `help` | Paths only — **never Markdown content** |
| `edit` | `str_replace`, replace/append/insert section — with version token | Content (JSON params) |
| `write` | Create note or fully overwrite — with version token | Content (JSON params) |

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
  - Lexical list (BM25 — exact terms, names, abbreviations) + vector list (meaning).
  - Merged with **Reciprocal Rank Fusion**: `score(doc) = Σ 1 / (60 + rank_in_list)`. Uses ranks only, so incomparable scores (BM25 unbounded vs. cosine) need no normalization.
- **Chunks:** one section; long sections split by paragraph, cap ~512 tokens per chunk.
- **Embedding model:** `snowflake-arctic-embed-m-v2.0`, 16-bit, **in-process via ONNX Runtime** (plaintext never leaves the process). Mind query vs. document prefixes; Matryoshka truncation available if storage matters.
- **No vector DB.** Vectors stored encrypted in MongoDB, decrypted into RAM on vault unlock; search = dot products. A personal vault has thousands of sections → milliseconds.
- **Incremental indexing:** hash per section, re-embed only what changed, embedding runs async.

## 7. Cryptography

- Each vault has a random **vault key**.
- Vault keys are **wrapped** by key slots (LUKS-style):
  - **API key slot:** API keys are high-entropy random → **HKDF**. Two derivations with different labels: a *lookup hash* (stored, for identification) and the *KEK* (never stored).
  - **Password slot:** passwords are low-entropy → **Argon2id**.
  - **Recovery key slot:** generated at registration, **must** be downloaded/confirmed.
  - **OAuth token slot:** see §9.
- Revoking a key = deleting its slot. Password change = rewrap.
- **An admin cannot reset a password or recover a vault.** No password + no recovery key = data gone. Documented honestly.
- Built in step 5 (Oct 23, 2026): vault keys, API-key slots (the dev token until step 8) and recovery slots; AES-256-GCM with the document's context as associated data; paths and vault names encrypted, with keyed path hashes for uniqueness. Password slots (Argon2id) come with logins in step 8. Recovery keys are written once to a file for the operator, never logged.
- **Unlocked vault = in-memory working set** (texts, vectors, link graph). Server restart = everything locked (feature).
  - Idle TTL: **15 min** for API/OAuth access, **30 min** for web sessions.
  - Global memory cap (configurable, default ~512 MB), LRU eviction of whole vaults.
  - A request may only use a cached unlocked vault if it presents a valid credential for *that* vault. The cache saves computation, never replaces authorization.
- **Never** in Redis, logs or URLs: plaintext content, vault keys, KEKs.

## 8. Users & admin

- **Bootstrap token** printed to the container log on first start (or set via env). Whoever presents it becomes **primary admin**. (Prevents the "first bot wins" race.)
- Admins can promote other admins. Primary admin cannot be removed.
- **Invites:** single-use, expiring, revocable links.
- **Deactivating a user** kills all their API keys and sessions immediately.
- Admins see metadata (who, storage used), never content.
- **API keys:** named, per-vault read/write rights, shown once at creation, afterwards only by prefix (`kumbu_ab12…`). A key without at least one right can't exist (no slot = can't decrypt anything).
- Rate limiting on login and invalid keys (Redis).
- **Later:** sharing vaults between users (needs asymmetric crypto), TOTP/2FA.

**Login through GitHub** (direction from Chris, Oct 24, 2026, for the COR.energy pitch): at COR.energy every internal tool for developers (Rancher, Argo CD, Mongo Express …) signs in through GitHub, and Kumbu² should fit in. To settle with step 8:
- **GitHub as the identity provider, limited to an organisation and optionally a team** (`KUMBU_GITHUB_ORG`, `KUMBU_GITHUB_TEAM`), the pattern of oauth2-proxy and Argo CD. The first login of a member creates the user; leaving the organisation ends the access. Invites become an option for deployments without an organisation.
- **Admins through a GitHub team** (`KUMBU_GITHUB_ADMIN_TEAM`) instead of, or next to, the bootstrap token.
- **GitHub says who someone is, not how to open their vaults.** A vault opens with a secret of the user's (a passphrase or the recovery key, later a passkey with PRF); the identity provider never holds key material, so a GitHub login alone, or an admin, can't read anyone's notes. Once a vault is open, API keys and OAuth tokens carry slots as in §9.
- **Later: any OIDC provider** (Entra ID, Keycloak), GitHub being the first. The MCP OAuth flow (§9, step 10) uses the same login at `/authorize`; Claude and other clients are OAuth clients and bring no identity of their own.

## 9. Client access

- **Bearer API keys:** Claude Code, opencode, Hermes, Chatsundere etc.
- **OAuth 2.1:** app connectors (Claude.ai, ChatGPT, Grok). Kumbu² is **its own authorization server** — no external identity provider needed (that's only the "Login with Google/GitHub" use case). The consent screen is just another way to create a key slot (choose vaults + rights) — no second permission world.
- Bearer API keys + OAuth cover every client type; **no keys in URLs** (proxy logs, and here keys are key material).

### OAuth flow (for reference)
1. Client calls `/mcp` without token → `401` pointing to metadata.
2. Client reads `/.well-known/…` metadata → finds Kumbu² as authorization server.
3. Client registers itself automatically → `client_id` (verify current MCP spec mechanism).
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
- Implementation: use a proven library for the protocol edge cases (evaluate OpenIddict); understand the flow, don't hand-roll the security details.

## 10. Obsidian compatibility

- **Export:** near-trivial — it's Markdown. Link resolution must follow Obsidian's rules (shortest unique name) or be exported in fully-qualified form.
- **Import:** lenient parsing → mechanical normalization. Plugin syntax preserved as is, otherwise ignored.
- The work project (Obsidian + Nextcloud, plain files) is a **separate thing**. Import/export is the bridge.

## 11. Tech stack

- **Backend:** C# / .NET current LTS, official MCP C# SDK (evaluate first)
- **Persistence:** MongoDB (notes, versions, slots, vectors, link graph — all vault data encrypted)
- **Transient:** Redis (sessions, rate limits, invite tokens — no plaintext, no keys)
- **Embeddings:** ONNX Runtime in-process
- **Frontend:** TypeScript + Vite + React + Tailwind, pnpm — admin (users, invites, keys, vaults) + Markdown reader
- **Deployment:** `compose.yaml`, later integrated into Komodo
- **Branding:** project name *Kumbukumbu*, wordmark *Kumbu²*, command `kumbu`. README explains the word's origin. No Africa clichés in the logo.

## 12. Build order (walking skeleton first)

1. MCP server in C# with `sh` (`ls`, `cat`), notes as plaintext in Mongo, wired into Claude Code → see how a model talks to the tool.
2. `sh` parser + pipes (unit-tested — deterministic, perfect test target).
3. `edit` / `write` with version tokens; history + trash.
4. Heading index, sections, links, backlinks, link normalizer (unit-tested).
5. Encryption: vaults, slots, HKDF/Argon2id, recovery key.
6. Agent tools for larger changes: view and diff earlier versions, changes across many notes.
6b. Changesets (with changeset ids in the history) and named checkpoints (`mkcp`, `lscp`, `rmcp`, `--checkpoint`), plus the fixes from the phase 6 test (`docs/notes-test-phase6.md`).
7. Embeddings + hybrid search; first, the decisions and fixes from the phase 6b test (`docs/notes-test-phase6b.md`): a folder restore moves moved notes back and rewrites their links; `replace` warns about links that would lead to another note and names the note named after the old text; small fixes.
8. Users, admin, invites, API keys.
9. Web UI and the REST API it shares with the Obsidian plugin (OpenAPI), designed with sync in mind. Direction (Chris, Oct 24, 2026): the mobile editor in `concepts/` (spec `MOBILE_EDITOR_v1.md` and a clickable prototype, made with Claude instead of Claude Design); the rest of the UI (admin, API keys, settings) is built around it. The prototype is the template for look and feel only; where it differs from this concept (links across vaults, per-chunk authorship, note ids, keys instead of the user), the concept wins. TypeScript. Colour schemes per user: the classic Linux schemes, Catppuccin and Tokyo Night among them, and light ones, each adjusted to the spec's contrast rules.
10. OAuth authorization server for app connectors.
11. Obsidian sync plugin.

Renumbered on Oct 23, 2026 (decided by Chris): step 6 was added, and the Obsidian plugin moved from "later" to the end of the build order, after the core is done and deployable. Documents written before that date use the old numbers: their step 6 is now 7, 7 is 8, 8 is 9 and 9 is 10. Step 6b was added on Oct 24, 2026 without renumbering, so step numbers stay stable from here on.

**A plugin package for the user's agent** (decided by Chris, Oct 24, 2026) handles what the tools can't judge: changes that need context, like renaming a project whose notes describe the name without containing it (`docs/notes-test-phase6b.md`). Its skills, a rename skill first, read before they replace and ask where the user's intent is unclear. It lives in its own repository and is distributed through a plugin marketplace; with the Obsidian sync plugin, Kumbu² then spans three repositories.

v1 is text only, on purpose. Attachments (images, PDFs, canvas) are a v2 topic, to be designed with the same care as the text core, not added on the side.

**Attachments reach the model as content it can fetch and interpret, never as hex or base64 in a tool's text output** (decided by Chris, Oct 24, 2026). Encoded bytes in text cost a fortune in tokens and mean nothing to the model. The precedent is cor.metis, Chris's expert system at COR.energy: base64 was considered there first, then replaced by **download tickets**, which worked much better. For Kumbu² that means a tool answers with a short-lived, single-use ticket for one attachment, which the client fetches. To settle in the v2 design: how a ticket squares with "no keys in URLs" (§9; a ticket is a bearer secret for one file, so short-lived, single-use, scoped, and kept out of logs); what MCP offers today (`resource_link` results, and image content, which travels base64 on the wire but reaches the model as an image, not as text); and the way back, uploading an attachment.

v2 also brings **OKF bundles as an opt-in per directory** (decided by Chris, Oct 24, 2026): a directory that declares itself an [Open Knowledge Format](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md) bundle gets OKF's rules and tooling; every other directory stays a plain Obsidian folder without them. Open questions in §13, analysis in `docs/okf-gap-analysis.md`. Two groundworks belong in v1 because they are needed with or without the flag: markdown links (`[text](/path.md)`) in `links`, `backlinks` and `mv`, and writer identities in step 8 that tell humans from agents.

v2 also brings **Kanban boards** (decided by Chris, Oct 24, 2026, from community feedback): several boards, and API keys that can be assigned boards, so an agent works on the boards it was given. One vault carries at most one board, and cards are on a par with notes. Leaning: a board is its own data model with import and export, with MCP tools for card operations. Details and open questions in §13.

Each step = a chapter in `CONCEPTS.md`.

## 13. Open questions

- Verify the current MCP authorization spec details (client registration mechanism, metadata endpoints) before implementing §9.
- Evaluate the official MCP C# SDK and OpenIddict.

### Obsidian sync (build order step 11, once the core is done and deployable)
- **Obsidian sync plugin (preferred, leaning decision Oct 22, 2026).** Humans keep a local copy of their vaults in Obsidian; a plugin syncs it with Kumbu². Chosen over mounting because it works offline and does not fight Obsidian's file watching over a network drive.
  - Own GitHub repo (TypeScript, pnpm, esbuild): Obsidian plugins are distributed from GitHub releases. Test installs via BRAT; listing in `obsidianmd/obsidian-releases` later.
  - Authenticates with an API key (§7) as `Authorization: Bearer <key>`; HTTP through Obsidian's `requestUrl` (no CORS, works on mobile).
  - **One key per device**, so a lost device is revoked by deleting its slot without touching the others.
  - **The key lives only in Obsidian's `SecretStorage`** (API since Obsidian 1.11.4), never in the plugin's `data.json`, which sits inside the vault and would be synced with it. The plugin refuses to run without it: `minAppVersion` `1.11.4` in `manifest.json`, a load-time check for `app.secretStorage`, and no plain-text key field in its settings.
  - Known limits of `SecretStorage` to verify before relying on it: the docs only promise local storage keyed to the vault, not OS-keychain encryption at rest; on mobile, secrets with the same name were reported to leak across vaults, so secret names include the vault (e.g. `kumbu-<vault>-<device>`).
  - Later: an OAuth login flow (§9) instead of pasting a key.
- **Mounting as an alternative** (not planned): **WebDAV** (mountable without extra software; ETags and locks map onto version tokens), **SFTP** (true sshfs; server-side SSH libraries for .NET are scarce) or an **own FUSE client** over the API. A mount keeps a vault unlocked, which works against the idle TTL (§7).
- To settle either way: writes must go through version tokens and update links, backlinks and the search index; Obsidian's non-note files (`.obsidian/`, temp files, attachments) need a policy; sync needs a conflict strategy for offline edits. Plaintext on the server adds no new exposure, as search needs it already.

### OKF bundles (v2, opt-in per directory)
Background and the full gap analysis: `docs/okf-gap-analysis.md`.
- **The flag is OKF's own marker:** a directory is a bundle when its `index.md` carries `okf_version` in its frontmatter (OKF §12). No Kumbu²-only metadata; the flag travels with export and the Obsidian sync, and the user sees and sets it in Obsidian. Leaning, not decided.
- **What enforcement means.** Refuse (writes without `type` fail with a teaching error), warn (writes pass, `okf check <path>` reports), or mixed: refuse agent writes, only warn on writes from the Obsidian sync, as refused sync writes would surface as conflicts the user can't make sense of. Leaning towards mixed.
- **Edge cases:** `mv` of a note without `type` into a bundle; `replace` across a bundle boundary; bundles nested in bundles; an existing `index.md` or `log.md` note that becomes reserved when the flag is set.
- **`index.md` and `log.md` are synthesised on read and on export, not stored** (OKF allows it: "consumers MAY synthesize one on the fly"). Stored, they would be server-written content that the sync carries back to Obsidian.
- Out of scope: Attested Computation (made for data catalogues; its attesters are code files, against text-only).

### Kanban boards (v2)
Decided: several boards; boards can be assigned to API keys. **One vault, one board** (decided by Chris, Oct 24, 2026): a vault carries at most one board, and several topics mean several vaults. A deliberate limit, to nudge users towards working with vaults actively, the way game rules steer players (Chris thought of EVE Online, where many mechanics nudge): Kumbu² as a "chaos avoider". Leaning (Chris, Oct 24, 2026):
- **Its own data model, with import and export**, not a note. Agents get MCP tools that say what should happen, such as add a card, move it to another lane, annotate it, instead of editing a board's text. Import and export in the Obsidian Kanban format (frontmatter `kanban-plugin: board`, a `##` heading per lane, a `- [ ]` item per card) keep the way to Obsidian open.
- **A board belongs to a vault, optionally.** The vault "Kumbu²" carries a board, the vault "Kochrezeptideen" doesn't. That answers "assign a board to a key" with what exists: a key with a slot in the vault (§9) reaches its board, sealed with the vault key like its notes, and several boards are several vaults.

- **Cards are on a par with notes** (decided by Chris, Oct 24, 2026). The ground rule for everything the data model has to bring along: wherever notes take part, cards do too. So:
  - a named checkpoint of the vault includes its board, and restoring it restores the cards;
  - a card can link to a note of the same vault (`[[…]]`, resolved like note links), and `backlinks` of that note lists the card ("very much in the users' interest", Chris);
  - `mv` of a linked note rewrites the link in the card, as in notes; `rm` names cards whose links now lead nowhere;
  - by the same rule, still to confirm in the design: history and undo per card, changesets, the trash for removed cards, search over cards, and whether a note can link to a card.

Open:
- **Concurrency.** Card operations instead of a whole-board version token, so several agents on one board don't block each other; what a conflict on one card looks like (both move the same card).
- **The Obsidian sync (step 11)** doesn't carry the board as a note; whether it exports a read-only Kanban note, or the board stays server-side only.
- **The web UI (step 9) shows boards**, with drag and drop.

### Resolved (Oct 21, 2026)
- Token lifecycle vs. key slots → §9 "Tokens as key slots".
- Static bearer headers for app connectors → moot, OAuth covers them.
- Duplicate-heading disambiguation → §4 "Sections".
- Sizes, versions, trash, working set → §4, §5, §7.
- Hybrid ranking → Reciprocal Rank Fusion, §6.
