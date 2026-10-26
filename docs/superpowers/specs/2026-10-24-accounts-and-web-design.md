# Accounts, the Web App and a Public Deployment — Design (steps 8 and 9a)

**Date:** 2026-10-24
**Scope:** build order step 8 (users, admin, invites, API keys) together with the first part of step 9 (the web app, mobile first) and a public deployment, for the first workplace presentation on Tuesday 2026-10-27. GitHub login, passkeys and OAuth for claude.ai are designed here but built after the must-haves.
**Status:** written with Chris on 2026-10-24 (brainstorming over chat while he was away). Decisions he made are marked *Chris*; the rest are Claude's calls, marked *call*, for review.

## 1. Intent

Kumbu² has to be usable by people other than its developer: invited, signed in, with their own vaults, in a web app on the phone and the laptop, and with Claude reading and writing their notes through MCP. The presentation shows exactly that, as a developer at a workplace would meet it:

1. Chris sends an invitation. The developer opens the link, creates an account, downloads a recovery key.
2. The (at first empty) web app: create a vault, upload Markdown (files or a whole Obsidian folder), read and edit notes.
3. Create an API key for chosen vaults, put it into Claude Code, and Claude works with the notes.
4. For the CTO: a document on encryption at rest and the security architecture.

Success on Tuesday (*Chris*): MCP with Claude works; uploading, viewing and editing Markdown works; admin and invitations work; the CTO document exists. Nice to have, in this order: OAuth so claude.ai can add Kumbu² as a connector; GitHub login; passkeys. The focus is "a great post-auth experience" (*Chris*).

Non-goals for this step: attachments (v1 stays text; *Chris*: upload means Markdown files and folders), team sharing of vaults (concept: personal notes), the desktop layout (mobile first works on the desktop too; *Chris*), Kubernetes manifests (after Tuesday, if an organisation wants an instance), Kanban (v2).

Anti-goals: an admin who can read users' notes; a public instance where the dev token still opens everything; a sign-up path that skips the recovery key.

## 2. Instances

| Instance | Where | Notes |
|---|---|---|
| `kumbu.tidesson.net` | Chris's VPS, Docker Compose behind Traefik with Let's Encrypt (*Chris*) | The community reference installation and the demo. Public. |
| `kumbu.example.internal` | An employer's Kubernetes with an ingress (*Chris*) | Only if the presentation goes well; publicly reachable, so people can work from anywhere (*Chris*). A later step: published image, manifests or a Helm chart. |

Consequences (*call*):
- **The dev token is off on a public instance.** `Kumbu:DevToken` becomes optional; without it, only user sessions and API keys open vaults. Compose for Traefik doesn't set it.
- **Passkeys and the GitHub OAuth app are bound to the domain**, so the domain must be final before people create passkeys. Each instance has its own GitHub OAuth app.
- **`compose.traefik.yaml`**, an override next to `compose.yaml`: Traefik labels (host rule from `KUMBU_HOST`, TLS with the Let's Encrypt resolver named in `KUMBU_TRAEFIK_CERTRESOLVER`), the external Traefik network (`KUMBU_TRAEFIK_NETWORK`), no host port. `compose.yaml` keeps working locally without Traefik.
- **One container serves both** the MCP endpoint (`/mcp`), the REST API (`/api`) and the web app (static files, `/`): one host, one certificate, same-origin cookies.

## 3. Accounts and keys

### The user key

Each user has a random **user key** (*call*). It wraps the keys of the user's vaults, and is itself wrapped by the user's credentials: their password (Argon2id), their recovery key, later each passkey (PRF) and each GitHub-plus-passphrase login. Changing a password rewraps one slot; adding a vault wraps its key once under the user key. API keys and OAuth grants get slots on the vaults they were given, not on the user key: a key for one vault opens only that vault (concept §7).

Why a user key and not a slot per credential per vault: a user with five vaults and three credentials would have fifteen slots to keep in step on every change. With a user key, credentials and vaults are independent.

Vaults created through the dev token (local development, step 1–7 data) stay dev-token vaults; there is no migration (*call*; local test data can be wiped).

### Sign-up with an invitation

| Topic | Decision |
|---|---|
| Invitation | Created by an admin: a single-use link with a random code (only its keyed hash is stored), an expiry (default 7 days), a role (`user` or `admin`), an optional note ("for Lea"), revocable until used. Shown once as link and QR code. *Chris*: invitations, from chatsundere's model. |
| First admin | A CLI command, `docker compose exec kumbu kumbu bootstrap-admin`, prints a one-time invitation link for the **primary admin**, and refuses once a primary admin exists (*call*, following chatsundere's ADR: logs get collected, so no bootstrap secret in the log; this replaces concept §8's "printed to the log"). |
| Ways in | Username and password (*Chris*: stays, for minimal set-ups and people without GitHub), with a warning that it is less safe than GitHub plus passkey. GitHub and passkeys later (§6). |
| Password | At least 12 characters, checked against a short list of the most common passwords; Argon2id with OWASP's current parameters (*call*). |
| Recovery key | Generated at sign-up, shown once, must be downloaded or copied and confirmed before the account exists (concept §7). It opens the user key. **Forgot password** (*call*): a screen takes username and recovery key and sets a new password (new password slot; all sessions end). Regenerating the recovery key in the settings makes the old one invalid at once. Vaults of users get no recovery slot of their own; the user's recovery key reaches them through the user key. Recovery keys of existing dev-token vaults (step 5) stay valid. |
| Roles | `primary_admin` (exactly one, can't be removed), `admin`, `user` (concept §8). |

### Sessions

The web app signs in with username and password. The server derives the password's key, opens the user key, and creates a **session**: a random secret in an `HttpOnly`, `Secure`, `SameSite=Strict` cookie, and on the server a session record with the user key wrapped under that secret (*call*: the same ephemeral-slot idea as OAuth access tokens, concept §9). So a request can only use an unlocked vault if it brings the secret (concept §7: the cache never replaces authorisation), and a server restart doesn't sign people out.

- Idle timeout 30 minutes, absolute 12 hours (concept §7 says 30 minutes for web sessions; the absolute limit is a *call*).
- Signing out or a password change deletes the session records.
- State-changing requests check the `Origin` header as well (CSRF; `SameSite=Strict` is the first line).
- Rate limits on sign-in, sign-up and wrong API keys, in memory (one instance; Redis when there are several; *call*, concept §8 named Redis).

### API keys

| Topic | Decision |
|---|---|
| What | Named, `kumbu_` + 32 random bytes in Base62; shown once; listed by name, prefix, vaults, rights, created, last used (concept §8). |
| Rights | Per vault: read, or read and write. A key without a vault can't exist (no slot, nothing to open). |
| Slot | The vault key wrapped via HKDF from the key (as the dev token today, step 5). Creating a key needs the vaults open, i.e. a signed-in session. |
| Writer | The key's name becomes the writer label (`dev` today), so changesets of two agents never merge, and the history says which agent wrote (concept §4, step 6b). Writes from the web app are written as the user's name. |
| Revoke | Deletes its slots at once; deactivating a user revokes all their keys and sessions (concept §8). |

The MCP endpoint keeps taking `Authorization: Bearer <key>`; read-only keys get a refusal from `write`, `edit`, `replace`, `mv`, `rm`, `restore` and checkpoint commands that names the missing right.

### Admin

Admins see users (name, role, created, last seen, number of vaults and storage used), invitations, and can deactivate or reactivate a user, change a role (not the primary admin's), and create or revoke invitations. **Never content**, and no password reset: without password and recovery key, a vault is gone (concept §7, said plainly in the UI).

## 4. The web app (step 9a)

Built after `concepts/MOBILE_EDITOR_v1.md` (the look and the interactions; the prototype is the template for look and feel, the concept wins on the backend — see that file's header). TypeScript, Vite, React, Tailwind v4, pnpm, in `web/`; built into the server image and served by ASP.NET (*call*).

Screens for Tuesday:

| Screen | Content |
|---|---|
| Join | From the invitation link: username, password (with the warning), recovery key (download or copy, confirm). |
| Sign in | Username and password; "forgot password" with the recovery key. |
| Vaults | The vault switcher (at the bottom of the tree drawer, as in the spec), create a vault. |
| Note | Read mode (rendered Markdown; wiki links resolved within the vault as `links` does, missing ones dashed; tasks tickable; backlinks below) and Write mode (textarea with the Markdown toolbar, autosave with the version token, the conflict toast on 409). The agent banner from the spec: changes by an API key **since the user's previous sign-in**, from the history (*call*: one timestamp on the user, no per-note read records). |
| Tree | Drawer with folders and notes, "New note here", edge swipe. |
| Search | The search sheet with Hybrid / Meaning / Exact, results with path, snippet, score and reason, from `semsearch` and `rg`. |
| Upload | Drop Markdown files or a folder (or pick them) into a folder of a vault. Folder structure is kept; anything that isn't `.md` is skipped and listed; a note that exists already is not overwritten silently: the upload lists such notes and the user chooses replace (a new version, recoverable from history) or skip, for all or one by one (*call*). Large uploads in batches, with progress. |
| Keys | API keys: create (name, vaults, read or read/write), shown once with a copy button and the line for Claude Code (`claude mcp add --transport http kumbu https://…/mcp --header "Authorization: Bearer …"`), list, revoke. |
| Settings | Colour scheme (the token system from the spec; for Tuesday the default Kumbu scheme and one light scheme, the full list later; *call*), password change, recovery key regenerate, sessions (sign out others). |
| Admin | Users, invitations (create with expiry and role, shown once as link and QR), deactivate, roles. |

Reused from chatsundere (*call*, after the licence question in §8): the view/edit pattern of `apps/user-client/src/routes/app/projects/file.tsx`, the `react-markdown` + `remark-gfm` setup, the admin invitation screens (`create-modal`, `reveal-screen` with QR), `ConfirmTyped` and `StepUpModal`, the Tailwind v4 `@theme` setup. Built new: folder upload (`webkitGetAsEntry` / `webkitdirectory`), API keys, the light scheme.

### The REST API

Shared later with the Obsidian plugin (concept §12 step 9), so designed as an OpenAPI document from the start (*call*). Paths, not ids; version tokens on every read and write; the same code paths as the `sh` commands, so the web app and agents see the same notes, links, history and search (concept; the spec's §7 corrected). Roughly (**updated after the build of step 8, Oct 25, 2026**: this is what exists under `/api`; `GET /api/openapi.json` is the authority):

- `POST /api/session` (sign in), `GET/DELETE /api/session`, `POST /api/join`, `POST /api/invitations/peek` (what a join link offers, before the user commits), `POST /api/recover`; invitation links are `/join#{code}` (the code in the fragment)
- `POST /api/password`, `POST /api/recovery-key`, `POST /api/sessions/end-others`
- `GET/POST /api/vaults`, `GET /api/vaults/{vault}/tree`
- `GET/PUT/DELETE /api/notes/{vault}/{**path}` (`PUT` with `version`, `409` on conflict), `POST /api/move` (note or folder, in the body)
- `GET /api/links|backlinks|history/{vault}/{**path}`; `GET /api/search?q=&mode=hybrid|meaning|exact&path=&n=`; `GET /api/changes`
- `POST /api/upload/{vault}/{**folder}` (a batch of files with their paths, the conflict policy in the body; at most 200 files and 5 MB of text per request)
- `GET/POST /api/keys`, `DELETE /api/keys/{id}`; `GET /api/admin/users`, `PATCH /api/admin/users/{id}`; `GET/POST /api/admin/invitations`, `DELETE /api/admin/invitations/{id}`

Bearer callers (API keys, the dev token) reach the notes, search, upload and `GET /api/vaults` endpoints; creating a vault, `GET /api/changes`, and the account, key and admin endpoints take a session only (`403` for others). Sign-in, join, peek, recover and the OpenAPI document are anonymous.

## 5. The document for the CTO

`docs/security-architecture.md`, in English (*call*: the artefact language of the project), for a technical reader who decides whether personal notes of employees may live there:

- what is protected against what (concept §1's threat model, honestly including "the running server sees plain text in RAM during a request; search needs it");
- keys and slots (vault key, user key, password, recovery key, API keys, sessions, later passkeys and OAuth), with a diagram;
- what an admin, the operator, a database dump, a stolen API key, a lost laptop can and can't do;
- encryption at rest in detail (AES-256-GCM with context, encrypted paths and names, keyed hashes, sealed embeddings and checkpoints);
- revocation and recovery; no password reset, by design;
- deployment hardening (TLS, cookies, rate limits, dev token off, no keys in URLs or logs);
- where the AI providers fit (Claude reads what it is given; the refresh token of a connector lives with the provider; concept §9).

A rendered version for the meeting (PDF or a page) is a *call* for Monday.

## 6. After the must-haves

In this order (*Chris*):

1. **OAuth for claude.ai** (step 10, pulled forward if time allows): Kumbu² as its own authorisation server, consent screen = choose vaults and rights = create slots (concept §9). Needs the public instance.
2. **GitHub login**: identity only; organisation and team checks for an instance that asks for them (`KUMBU_GITHUB_ORG`, `KUMBU_GITHUB_TEAM`, `KUMBU_GITHUB_ADMIN_TEAM`); the community instance without (concept §8, direction of 2026-10-24).
3. **Passkeys with PRF** as the bridge to the user key (*Chris*'s idea): GitHub says who someone is, the passkey opens their vaults. PRF is broadly supported as of 2026 (iCloud Keychain, Google Password Manager, 1Password, security keys; Windows Hello only with the February 2026 update; Bitwarden partly). A passkey without PRF can't unlock: then the user sets a passphrase for the user key instead, and the UI says why (*call*; chatsundere refuses PRF-less passkeys outright). After creating a passkey, one sign-in at once, as some authenticators return PRF only then.

## 7. Testing

- Core and server tests as before (accounts, slots, sessions, invitations single-use and expiry, rights, revocation, rate limits, the writer label per key).
- Web app: Vitest for the units the spec lists (task toggle, toolbar, conflict toast, upload conflict policy), and a manual pass on a phone and a laptop.
- Live on `kumbu.tidesson.net`: invitation to a second account, upload a real Obsidian folder, Claude Code with a key, a blind run with a fresh agent on the public instance.

## 8. Answered by Chris after the builder check

- **Code from chatsundere may be taken over** (*Chris*, 2026-10-24): he is its author and allows its use in Kumbu² under AGPL-3.0-or-later (chatsundere's apps are AGPL-3.0-only, its `packages/crypto` LGPL-3.0-only; the copied files get Kumbu²'s SPDX header and a line naming their origin).
- **The VPS has 16 cores and 32 GB of RAM** (*Chris*). So the demo instance runs **arctic-l** (about 3 GB measured, the better model in the evaluation; *call*), the working-set cap goes to 2 GB (*call*), and indexing a large upload needs no throttling beyond the existing half-the-cores default.
