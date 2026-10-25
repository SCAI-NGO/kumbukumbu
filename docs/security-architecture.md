---
title: "Kumbu² — Security Architecture and Encryption at Rest"
subtitle: "For a decision on personal notes of employees"
date: "25 October 2026, as built after step 9a, with the security fixes of step 9b and the security items of the overnight round"
---

# Kumbu² — Security Architecture and Encryption at Rest

**Audience:** a technical decision-maker who decides whether employees' personal notes may live in Kumbu².
**Scope:** the code as built (accounts, API keys, web app, MCP endpoint; steps 1 to 9a, plus the step 9b fixes to the recovery key file, HSTS, the CSP, and what a password change and a recovery revoke, and the overnight round's API-key expiry, deletion of vaults and accounts, optional MongoDB authentication and pinned build inputs). Everything that is only planned is in §9, "What comes next", and nowhere else.
**How to check it:** every key claim names the file (and, where useful, the line) in `src/` that implements it. The project is open source (AGPL-3.0-or-later). No external security audit has been done; the reviews so far were part of development.

## 1. Summary for a decision-maker

Kumbu² is a self-hosted Markdown notes server. People use it through a web app; AI agents (Claude Code and other MCP clients) use it through an API key that the person creates for chosen vaults. Notes are personal: there is no sharing between users.

**What it protects well.** Everything in the database is encrypted: note text, its history, the trash, folder and file names, vault names, the search vectors and checkpoints. Each vault has its own random key, and that key is stored only *wrapped*: by its owner's user key, and by each API key given that vault. The user key in turn is stored only wrapped by the owner's password, recovery key and web sessions. A database dump, a stolen backup or the disk of a compromised hoster shows no readable content and no paths, only metadata such as usernames and display names, the admins' notes on invitations ("for Lea"), sizes and times. Admins manage accounts but cannot read notes, cannot see vault or note names, and cannot reset passwords.

**What it does not protect against.** It is *encryption at rest*, not end-to-end encryption. To search, render and edit, the server decrypts a vault into memory while it is in use, and keeps it there until no caller has used it for 15 minutes (checked once a minute, so up to about 16). Signing out does not lock it, and an agent that keeps using a vault keeps it unlocked. Whoever controls the running server — the operator — can therefore read what is in use, and could change the server to capture passwords. The AI provider of a connected agent sees whatever that agent reads. These limits are by design and stated, not hidden.

**What that means for COR.energy.**

1. The decisive question is **who operates the instance**. For employees' personal notes, the operator must be someone COR trusts with that content: COR's own platform team on COR's own infrastructure (the planned `kumbu.internal.cor.energy`), not a third party. On Chris's community instance `kumbu.tidesson.net`, Chris is the operator.
2. **Admins are not the threat** the design worries about: the admin role grants no access to content (§4).
3. **No password reset, by design.** A user who loses both password and recovery key loses their notes. This has to be accepted and communicated.
4. **AI use follows COR's AI policy.** What an agent reads goes to its model provider. Kumbu² limits *which* vaults an agent can reach (and whether it may write), not what the provider does with what it receives.
5. **Deletion is for good, but not from backups.** A leaving employee's account is deactivated (`src/Kumbu.Server/Accounts/Admin.cs:53–87`) and can then be deleted by an admin, with the username typed out; a user can delete their own account (password and username) or a single vault (its name). Everything of it goes in one MongoDB transaction: vaults with notes, history, trash, checkpoints and search vectors, every slot, sessions and API keys (`src/Kumbu.Server/Accounts/Deletion.cs`, §6). Backups taken before keep the sealed data, and with it whatever credentials were valid then (§7).

## 2. Threat model and honest limits

### What is protected, against whom

| Threat | Protected? | Why |
|---|---|---|
| Database dump, stolen backup, hoster's disk | Yes, content and names | All content and paths sealed with AES-256-GCM under per-vault keys; keys stored only wrapped (§5). |
| Another user of the same instance | Yes | A user's vaults open only with their user key; other users' vaults are invisible, not merely refused (§4). |
| An admin (in the app) | Yes, content | The admin role carries no key material; admin screens show metadata only (`src/Kumbu.Server/Accounts/Admin.cs`). |
| A stolen API key | Limited to its vaults and rights | Slots per vault, right sealed into the slot; revocation deletes the slots at once (§6). It does not re-key the vault (§3). |
| Cross-site attacks in the browser | Yes, as far as tested | `SameSite=Strict` cookie, `Origin` check, CSP, HSTS, no raw HTML in rendered Markdown (§7). |
| Online password guessing | Rate-limited | Per username and per address (§7). The per-username limit lets anyone who knows a username keep that user from signing in, minute after minute (a targeted lockout; keys and live sessions keep working). |
| Offline password guessing from a dump | Slowed, not prevented | Argon2id with 64 MiB per guess (§5); a weak password remains guessable. |

### What is not protected

- **The operator of the running server.** While a vault is unlocked, its notes, paths, search vectors and the vault key itself are in the server's memory in plain form (`src/Kumbu.Server/Vaults/UnlockedVault.cs`, `VaultCatalog.cs:521`). The operator can read process memory, and can deploy a modified server or web app that records passwords, session cookies and API keys as they arrive (passwords are sent to the server and derived there; nothing is derived in the browser). Users who do not trust the operator must run their own instance.
- **The AI provider** of a connected client sees what the agent reads and writes. This is not end-to-end encryption and is never described as such (`README.md`, "Security").
- **Rollback by someone with write access to the database.** An older genuine version of a document (from a backup) can be put back; it opens, because it is genuine. Detecting this needs a per-vault counter or manifest; not built (`docs/superpowers/specs/2026-10-23-encryption-design.md` §5).
- **Old copies of the database with old credentials.** Revoking a credential deletes its slot but does not change the user key or any vault key (§3). Whoever holds an old backup *and* a credential that was valid then (an old password, an old recovery key, a revoked API key) can open those vaults from it. Restoring a backup brings back the credentials of that moment (§7).
- **Metadata.** The database holds, in the clear: usernames and roles, sign-in times, which vaults belong to which user, how many documents a vault has and their sizes, revision numbers and times (details in §5.6). An admin sees some of this by design.
- **Keys in memory are not zeroed.** Locking a vault drops the reference; the bytes stay until the .NET garbage collector reuses them (`VaultCatalog.cs:467`). Swap and core dumps of the server can hold plaintext; the operator should disable both on the host.

### How long plaintext lives in memory

A vault is decrypted **completely** the first time a request uses it (notes, history, trash, moves, embeddings, checkpoint index; `VaultCatalog.cs:521–573`) and served from memory afterwards. A sweep once a minute (`src/Kumbu.Server/Vaults/VaultSweeper.cs:9`) locks a vault after **15 idle minutes** (`Kumbu:Vaults:IdleMinutes`, `src/Kumbu.Server/KumbuOptions.cs:81`), so in practice after 15 to 16 minutes, and the least recently used vaults when all unlocked vaults together exceed a memory cap (default 512 MB, `KumbuOptions.cs:84`). A vault used in the last minute is never locked. Idle means *no caller* used the vault: the time counts across sessions and API keys together, so an agent that polls a vault keeps it unlocked, and signing out or the end of a session does not lock it (`Vaults/UnlockedVault.cs:70`, `VaultCatalog.cs:475`). A restart locks everything; but because sessions and API keys live in the database (§6), the next request with a valid cookie or key unlocks the vault again without the user doing anything.

The unlocked cache never replaces authorisation: every request derives its own lookups and unwraps its own slot, and a cached vault is handed out only if the unwrapped key matches it (`VaultCatalog.cs:527–530`).

## 3. Keys and slots

### The hierarchy

Every user has a random 256-bit **user key**. Every vault has a random 256-bit **vault key**. Neither is ever stored in the clear. Each is stored in **slots**: the key, sealed with AES-256-GCM under a key-encryption key (KEK) derived from one credential. Opening a slot needs that credential; revoking a credential means deleting its slot. This is the same idea as LUKS key slots.

```mermaid
flowchart TB
  subgraph held["Never stored"]
    PW["Password"]
    RK["Recovery key, 256 bit<br/>kumbu-rk-…"]
    SC["Session secret, 256 bit<br/>HttpOnly cookie"]
    AK["API key, 256 bit<br/>kumbu_…"]
  end

  UK(("User key<br/>256 bit, random"))
  VK(("Vault key<br/>256 bit, random,<br/>one per vault"))

  PW -- "Argon2id, own salt<br/>password slot" --> UK
  RK -- "HKDF-SHA256<br/>recovery slot" --> UK
  SC -- "HKDF-SHA256<br/>session slot, expires" --> UK
  UK -- "HKDF-SHA256, salt = vault id<br/>user slot (one per vault)" --> VK
  AK -- "HKDF-SHA256, salt = vault id<br/>API-key slot: vault key + right" --> VK

  VK -- HKDF --> CK["Content key<br/>AES-256-GCM"]
  VK -- HKDF --> PK["Path-id key<br/>HMAC-SHA256"]
  VK -- HKDF --> TK["Text-id key<br/>HMAC-SHA256"]
  CK --> DATA["Notes with history, trash, moves,<br/>embeddings, checkpoints, vault name"]
  PK --> IDS["Keyed hashes of paths<br/>and checkpoint names"]
  TK --> TXT["Keyed hashes of<br/>checkpoint texts"]
```

Why a user key in between: with five vaults and three credentials, direct slots would be fifteen to keep in step on every change. With the user key, a password change rewraps one slot, and a new vault is wrapped once (`docs/superpowers/specs/2026-10-24-accounts-and-web-design.md` §3).

**No key rotation.** Revoking a credential deletes its slot but does not change the user key or any vault key: a password change or a recovery re-wraps the same user key (`Accounts/UserCatalog.cs:234–290`), a new recovery key wraps the same user key (`UserCatalog.cs:202–212`), revoking an API key only deletes its slots (`Accounts/ApiKeys.cs:183–204`). Anyone who holds an old backup *and* a credential that was valid at the time (old password, old recovery key, revoked API key) can still open those vaults from it. A vault key, once exposed, stays valid for every later copy of the database. Re-keying is not built (§9).

### The slots as built

| Slot | Collection | KEK derivation | Associated data | Code |
|---|---|---|---|---|
| Password → user key | `user_slots` | Argon2id(password, 16-byte random salt) | `user:{userId}:password` | `Accounts/UserCatalog.cs:313–324` |
| Recovery key → user key | `user_slots` | HKDF(recovery key, `kumbu/v1/user-recovery-kek`, salt = user id) | `user:{userId}:recovery` | `UserCatalog.cs:326–335` |
| Session → user key | `user_slots` | HKDF(session secret, `kumbu/v1/session-kek`, salt = user id) | `user:{userId}:session` | `Accounts/Sessions.cs:43–61` |
| User key → vault key | `slots` | HKDF(user key, `kumbu/v1/user-slot-kek`, salt = vault id) | `slot:{vaultId}:user` | `Vaults/VaultCatalog.cs:162–218` |
| API key → vault key and right | `slots` | HKDF(API key, `kumbu/v1/api-key-kek`, salt = vault id) | `slot:{vaultId}:key:{keyId}` | `VaultCatalog.cs:226–264` |

(All paths relative to `src/Kumbu.Server/`.) Each derivation has its own label, so a value derived for finding a slot can never serve as a key for opening one. The per-vault and per-user salts mean one KEK opens exactly one slot. HKDF is used only for high-entropy secrets (random keys and tokens, and the dev token, which the operator chooses and which must therefore be random: the server checks only its length, `KumbuOptions.cs:8–17`, `Vaults/VaultCatalog.cs:70, 598`); Argon2id only for the password, the one low-entropy secret (`src/Kumbu.Core/Crypto/VaultCrypto.cs:54`).

**Finding a slot without storing the credential.** Sessions and API keys are found by a lookup value `HKDF(secret, "…-lookup")` stored next to the slot (`Sessions.cs:148`, `Accounts/ApiKeys.cs:372`); a user's vault slots by `HKDF(user key, "kumbu/v1/user-slot", salt = user id)` (`VaultCatalog.cs:603`). Since the secrets are 256 bits of randomness, a lookup value cannot be turned back into the secret, and guessing is pointless.

**Vaults of users have no recovery slot of their own.** The user's recovery key reaches them through the user key.

**The dev token** (a single server-wide bearer secret for local development, steps 1–7) has its own older slot kind on the vaults it created. It is optional: unset, it opens nothing (`KumbuOptions.cs:19–20`). It never had and cannot get a slot on a user's vault. A public instance runs without it (§7).

## 4. Who can do what

| Actor | Can | Cannot |
|---|---|---|
| **The user** | Everything with their own vaults: read, write, upload, search, history, checkpoints; create and revoke API keys (optionally expiring); change password, make a new recovery key (needs the current password), sign out other sessions; delete a vault (password and its name typed out) or the whole account (password and username typed out; not the primary admin). | See other users or their vaults. A user's `ls /` does not even count vaults that aren't theirs (`Vaults/Credentials.cs:24–31`). |
| **Admin** (role `admin` or `primary_admin`) | See the user list: username, role, active, created, last sign-in, number of vaults, bytes stored (`Accounts/Admin.cs:25–44`). Create and revoke invitations (role `user` or `admin`). Deactivate and reactivate users (ends their sessions and revokes their API keys at once, `Admin.cs:53–87`). Change roles, except the primary admin's. Delete a deactivated user, never the primary admin, with the username typed out: their vaults and everything in them go without being opened (`Accounts/Deletion.cs:69–86`). | Open any vault, see vault names, note names or content, reset a password, recover an account. There is no code path from the admin role to key material. *Limit:* a stolen admin session can invite a new admin account that outlives the session; it still opens nobody's vaults. |
| **Operator** of the running instance (root on the host, access to the container, the image or the deployment) | Read process memory, so every vault while unlocked. Change the server or web app to capture passwords, cookies and API keys as they arrive, and from them user keys. Read the database, logs and backups. | Read a vault whose owner never signs in and whose keys are never used while the operator is acting maliciously: without the credential, the slot does not open. |
| **Database dump or stolen backup** (read access) | See metadata (§5.6). Run offline guesses against each user's password slot at the cost of one Argon2id (64 MiB, 3 passes) per guess per user. | Read content, paths, vault names, checkpoint names, API key names. Recover API keys, session secrets or recovery keys from their lookup values. |
| **Database write access** | Delete data. Put back older genuine documents (rollback, §2). Change roles or the active flag; insert an invitation (the instance secret that keys invitation codes is in the database, `Accounts/ServerSecret.cs`) and so create an account. | Make a modified or moved document open: a changed byte, a blob copied to another document or vault, a changed right in an API-key slot, or a swapped path hash is rejected (`UnlockedVault.cs:86–99`; `tests/Kumbu.Server.Tests/Vaults/EncryptedNoteStoreTests.cs:533`). Gain access to an existing user's vaults. |
| **Stolen API key** | Read, or read and write, exactly the vaults it was given, through MCP (`/mcp`) and the note endpoints of the REST API, from anywhere, until revoked. Its writes carry the key's name in the history. With write rights, also remove checkpoints for good: `rmcp` deletes the checkpoint document, with no trash (`src/Kumbu.Core/Notes/Checkpoints.cs:103–117`, `Vaults/EncryptedNoteStore.cs:260–273`). | Reach other vaults; create or delete vaults; manage keys, the account or users (session-only endpoints answer 403, `Api/Security.cs:88–92`). Write to a read-only vault. Delete notes beyond recovery: a deleted note is in the trash for 30 days, and up to 10 earlier versions per note are kept (`src/Kumbu.Core/Notes/NoteWriter.cs:19–20`), though a key with write rights can push old versions out by writing repeatedly. A key expires only if its owner chose an expiry at creation (30, 90 or 365 days; the web app proposes 90). |
| **Lost or stolen laptop** | Use a browser session that is still valid (at most 30 idle minutes, 12 hours in all), and within that session create new API keys (write, any of the user's vaults) that outlast the session, until revoked; a password change revokes them unless the user unticks that (`Api/AccountApi.cs:111`, §6). Such a session alone cannot delete a vault or the account: both need the password (`Api/NoteApi.cs:98`, `Api/AccountApi.cs:108`). Use any API key stored in an agent's configuration (for Claude Code, in its local settings, in plain text) until it is revoked. **If the downloaded recovery key file is on the laptop and the finder knows the username**, take over the account. The file, `kumbu-recovery-key.txt`, holds only the key and a short instruction (keep it somewhere safe, not on this device; together with the username it opens the account; it is shown only once), but neither the username nor the instance's address (`web/src/components/RecoveryKeyReveal.tsx:25–34`), so the file alone is not enough. A username is not a secret, though: it may be the e-mail prefix or known to colleagues. | Read notes from browser storage: the web app keeps no note content and no secrets in local or session storage (local storage holds only the colour scheme, `web/src/theme/apply.ts`; session storage only a flag for the one automatic reload after an update, `web/src/components/AppError.tsx:9–36`; tested in `web/src/note/MarkdownEditor.test.tsx`, `web/src/routes/Keys.test.tsx:187`), and API responses are `Cache-Control: no-store` (`Api/Security.cs:55`). |
| **AI provider** (e.g. Anthropic for Claude) | See everything its agent reads or writes through Kumbu², as part of the conversation, under the provider's own data terms. | Reach vaults the API key wasn't given. With Claude Code, the MCP connection is made from the user's machine and the key sits in its local configuration, so it is not normally sent to the provider. This holds by the design of the set-up, not by enforcement: the agent can read its own configuration files. |

**What to do after a lost laptop:** from another device, sign in, end other sessions (`POST /api/sessions/end-others`, in Settings), change the password with "Also revoke all API keys" ticked, as it is by default, so the keys a session on the laptop may have made die too, not only those that were on the laptop (`Api/AccountApi.cs:34–36, 183`), give the agents still in use new keys, and make a new recovery key: a password change does **not** replace it (`Accounts/UserCatalog.cs:154–167`; README, "Accounts, API keys and the REST API"). Recommendation for users: keep the recovery key in a password manager and delete the downloaded file.

## 5. Encryption at rest in detail

### 5.1 Primitives

All from .NET itself (`System.Security.Cryptography`), except Argon2id:

| Purpose | Primitive | Parameters | Code |
|---|---|---|---|
| Sealing (encrypt and authenticate) | AES-256-GCM | 256-bit key, 96-bit nonce, 128-bit tag | `src/Kumbu.Core/Crypto/VaultCrypto.cs:27–36` |
| Deriving keys from random secrets | HKDF-SHA256 | 256-bit output, a distinct label per purpose, salt where a key must be bound to one vault or user | `VaultCrypto.cs:55–60` |
| Keyed hashes | HMAC-SHA256 | key derived from the vault key | `VaultCrypto.cs:65` |
| Password → KEK | Argon2id (Konscious.Security.Cryptography.Argon2 1.3.1) | **64 MiB memory, 3 iterations, parallelism 4**, 16-byte random salt per password slot, 32-byte output; NFC-normalised password | `src/Kumbu.Server/Accounts/Argon2Kdf.cs:11, 17–18, 69–76` |
| Random keys, nonces, secrets | `RandomNumberGenerator` (OS CSPRNG) | 32 bytes for every key and secret | `VaultCrypto.cs:20, 32` |

The Argon2id setting is the second recommended option of RFC 9106; it is above OWASP's current minimum. To keep sign-in floods from exhausting memory, at most one Argon2 run per CPU core happens at once across the process; the rest wait (`Argon2Kdf.cs:20–26`).

**Sealed blob format:** one format byte (`1`), the 12-byte nonce, the ciphertext, the 16-byte tag (`VaultCrypto.cs:18, 27–36`). The nonce is **fresh random for every seal**; nothing reuses a nonce deliberately. With random 96-bit nonces, the usual bound is about 2³² seals per key; one vault's content key is far from that in personal use.

### 5.2 Context binding (associated data)

Every blob is sealed with associated data that names what it is. A blob only opens in exactly that context:

| Document | Associated data |
|---|---|
| Note (with its history) | `note:{vaultId}:{documentId}:{revision}` |
| Trashed note | `trash:{vaultId}:{documentId}` |
| Move record | `move:{vaultId}:{documentId}` |
| Embeddings of a note | `embeddings:{vaultId}:{pathId}` |
| Checkpoint | `checkpoint:{vaultId}:{documentId}` |
| Checkpoint text | `cptext:{vaultId}:{textId}` |
| Vault name | `vault:{vaultId}` |
| Slots | see §3 |
| API key's name and vaults (owner's list) / its writer label | `api-key:{keyId}:meta` / `api-key:{keyId}:writer`, for a key with an expiry `api-key:{keyId}:writer:{expiry in Unix milliseconds}` |

(`src/Kumbu.Server/Vaults/UnlockedVault.cs:86–191`, `VaultCatalog.cs:110, 609–614`, `Accounts/ApiKeys.cs:382–386`.)

Consequence: someone with write access to the database cannot move a note, its embeddings or a checkpoint into another document, another vault or another revision, nor swap their clear fields around: it fails to open. On opening, the path inside the sealed note must also match the clear path hash (`UnlockedVault.cs:90–99`), likewise for embeddings and checkpoints. A document that fails is reported as damaged, never shown. The clear fields of trash and move records (the path hash, the deletion or move time) are *not* bound into the seal (`UnlockedVault.cs:100–116`): changing them can mislabel an entry or make the TTL index remove it earlier, which amounts to deleting it, something write access to the database allows anyway.

### 5.3 Sealed paths and names

Paths, file names and folder names exist in the database only inside sealed blobs. For MongoDB's unique index and lookups each document carries a **keyed hash** of its path, `HMAC-SHA256(HKDF(vault key, "kumbu/v1/path-id"), path)` (`UnlockedVault.cs:83`). Equal paths give equal hashes within a vault; without the vault key nothing else can be learned, not even whether two vaults hold the same path. Vault names are sealed (`VaultCatalog.cs:204`); their per-user uniqueness uses a keyed hash under the user key (`VaultCatalog.cs:175`). API key names are sealed under the user key, and their uniqueness also uses a keyed hash (`ApiKeys.cs:379`). Checkpoint names are keyed hashes too (`UnlockedVault.cs:154`).

### 5.4 History, trash, moves and checkpoints

- **History:** up to 10 earlier versions per note are kept **inside** the note's sealed blob (`src/Kumbu.Core/Notes/NoteWriter.cs:19`; `UnlockedVault.cs:213–215`), so the database does not even show how many versions exist, only the blob's size.
- **Trash and move records** are sealed documents of their own, removed by MongoDB TTL indexes after 30 days (`VaultCatalog.cs:652–663`).
- **Checkpoints** (named snapshots of a folder) store each note text once per vault, under a keyed hash of the content (`UnlockedVault.cs:157`). Which checkpoint refers to which text is recorded only inside the sealed checkpoint (`UnlockedVault.cs:159–162`), so a database reader cannot tell whether two checkpoints share a text; visible are the number and sizes of the distinct texts per vault.
- Deleting in MongoDB is not secure erasure: removed documents may persist in storage files and backups, but sealed. This holds for a deleted vault or account too (§6, "Deleting vaults and accounts").

### 5.5 Search and embeddings

Full-text search runs over the decrypted notes in memory. Search by meaning uses an embedding model (Snowflake Arctic Embed, `arctic-m` or `arctic-l`) that runs **inside the server process** with ONNX Runtime: no note leaves the server to be embedded. The model is downloaded once from Hugging Face at a pinned revision and checked against a SHA-256 per file while downloading (`src/Kumbu.Server/Search/OnnxEmbedder.cs:75–117`, `IEmbedder.cs:28`). At later starts only the file sizes are checked (`OnnxEmbedder.cs:81`): a model file replaced on the volume by one of the same size would be loaded, so the model volume must be as trusted as the image. The vectors are stored per note as 16-bit floats inside a sealed blob (`UnlockedVault.cs:120–151`); embeddings can leak content if exposed, so they get the same protection as the text. There is no vector database and no external search service.

### 5.6 What is in the clear

Verified against the document classes in `src/Kumbu.Server/Vaults/Documents.cs`, `Accounts/UserDocuments.cs`, `Accounts/ApiKeys.cs:419–459` and `Accounts/Invitations.cs`; the test `Nothing_readable_reaches_the_database` (`tests/Kumbu.Server.Tests/Vaults/EncryptedNoteStoreTests.cs:48`) checks a full dump for paths, names and content.

| Collection | In the clear |
|---|---|
| `users` | username, display name (as typed), role, active, created, last and previous sign-in, last credential change, number of API keys created (`keysCreated`), a key id derived from the user key |
| `user_slots` | user id, kind, Argon2 salt, lookup value, wrapped key, created, and for sessions expiry and last use (so: how many sessions a user has, and when they were used) |
| `vaults` | random vault id, creation time |
| `slots` | vault id, kind, lookup value, **owner's user id** (user slots, so admins can count vaults per user), keyed name hash, API key id, wrapped key, created |
| `notes`, `trash`, `moves`, `embeddings`, `checkpoints`, `checkpoint_texts` | vault id, keyed hash, revision number, blob (whose size shows), deletion and move times; MongoDB's ObjectIds also carry their creation time. Counts per vault follow. |
| `api_keys` | owner's user id, lookup value, **the first 8 characters of the key** (so the owner can tell keys apart; about 48 of its 256 bits, leaving about 208 unknown), keyed name hash, created, last used, revoked, expiry |
| `invitations` | keyed hash of the code, role, **the admin's note** ("for Lea"), who created and who used it, times |
| `server` | a random instance secret that keys the invitation-code hashes (`Accounts/ServerSecret.cs`) |

## 6. Sessions, API keys, revocation and recovery

### Sign-in and sessions

Sign-in sends username and password over TLS. The server runs Argon2id, opens the password slot and gets the user key. An unknown username runs Argon2 against a dummy salt, so it takes as long as a wrong password, and all failures answer the same (`Accounts/UserCatalog.cs:90–114`).

A **session** is a slot: the server makes a random 32-byte secret, puts it in the cookie and stores the user key wrapped under a KEK derived from it (`Accounts/Sessions.cs:43–61`). The server keeps neither the secret nor the user key; a request can use the user key only if it brings the cookie. Sessions survive a server restart. Each request checks (`Sessions.cs:67–116`):

- idle timeout **30 minutes**, absolute limit **12 hours** (`Sessions.cs:18–19`); MongoDB's TTL index also deletes the slot at the absolute limit;
- the user is still active;
- the session started after the last password change or recovery.

Signing out deletes the slot; "sign out other sessions" deletes the others; a password change or recovery deletes all. None of them locks a vault that is in memory: it stays there until idle (§2).

The cookie `kumbu_session` is `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, with `Max-Age` of 12 hours (`Api/AccountApi.cs:304–319`). It is accepted **only under `/api`**, never on `/mcp`, so a page on a sibling subdomain cannot drive the MCP endpoint with a user's cookie (`Auth/CallerMiddleware.cs:71–80`).

### API keys

- Format `kumbu_` plus 32 random bytes in Base62 (43 characters), shown once (`Accounts/ApiKeys.cs:54–57, 105`).
- Rights per vault: `read` or `write`. A key without a vault cannot be created (`ApiKeys.cs:96`). Each vault gets its own slot sealing the vault key **and the right together**, bound to the key's id; changing the right in the database makes the slot fail to open (`VaultCatalog.cs:226–296`). The read-only rule itself is enforced in the server before any write (`src/Kumbu.Core/Notes/ICaller.cs:16–19`, called by every write path); a read-only key still lets the server unwrap the vault key, as it must to read.
- Only a signed-in session can create, list or revoke keys. A key can never manage keys, the account or users (`Api/Security.cs:88–92`), so a leaked key cannot make more keys. A stolen *session* can, and those keys outlive it until revoked (§4). A creation whose session was checked just before a password change, recovery or deactivation cannot slip in after it: the key's transaction checks the user again (active, credentials unchanged since the session's sign-in) and writes the user document, so it either commits before the change's revocation, which then finds the key, or is refused with 401 (`ApiKeys.cs:123–153`).
- The key's name becomes the writer label in the history, so the user can see which agent changed what; the web app shows changes by keys since the previous sign-in.
- **Revoking** marks the key revoked and deletes its slots in one transaction; it opens nothing from then on, also for a vault still in memory, since every request opens its slots afresh (`ApiKeys.cs:183–204`, `VaultCatalog.cs:271–296`). A request that opened them just before (an MCP call in progress, say) may still finish; the next one is refused. It does not change the vault key: a copy of the database taken before the revocation still opens with the revoked key (§3, "No key rotation").
- **Changing the password** revokes all the user's keys too, unless the user unticks "Also revoke all API keys" in Settings (`revokeApiKeys`, true when left out, so an older client gets the safe behaviour; `Api/AccountApi.cs:34–36, 183`). A **recovery** always revokes them (`UserCatalog.cs:144`). Both mark every key revoked and delete its slots in one transaction, as revoking one key does, retrying a write conflict with the `lastUsedAt` update of a key in use (`ApiKeys.cs:206–223`). The revocation runs twice. First after the user's credentials are stamped as changed and before the new password is written (`UserCatalog.cs:241–245`): a fault in between leaves the keys revoked and the old password in place, the safe way round. Then again after the final stamp (`UserCatalog.cs:283–287`): a sign-in with the old password that read its slot between the two stamps passes the first one, and a key it made at once may land after the first revocation; no key can come from it after the second stamp.
- **Deactivating a user** ends all their sessions and revokes all their keys in one transaction; reactivating revives none of them (`Accounts/Admin.cs:53–87`).
- **Expiry** is optional and chosen at creation: 30, 90 or 365 days, or none (`ApiKeys.cs:69, 97–100`); the web app proposes 90 days. From that moment the key answers 401 like a revoked one (`ApiKeys.cs:282–286`). The expiry is stored in the clear for the list, and bound into the seal of the key's writer label (`ApiKeys.cs:385–386`): moved or removed in the database, the label no longer opens and the key is refused (`tests/Kumbu.Server.Tests/Accounts/ApiKeyTests.cs`). The first time an expired key is presented again, its slots are deleted and its name is freed (`ApiKeys.cs:257–268`); the user's next key does the same for every expired key of theirs, in its own transaction (`ApiKeys.cs:247–255`). An expired key that is never presented again keeps its slots, which open nothing without the key and are refused anyway, until then. A key without expiry stays valid until revoked: one by one, by a password change that asks for it, by a recovery or by deactivation.

### Deleting vaults and accounts

All in `src/Kumbu.Server/Accounts/Deletion.cs`, from a signed-in session only (API keys and the dev token get 403):

- **A vault**, by its owner, with the current password (checked before the vault is looked up, so without it nobody learns whether the name exists; the password-check limit applies, 429) and the name typed out exactly (`Deletion.cs:33–45`, `POST /api/vaults/{vault}/delete`, `Api/NoteApi.cs:98, 137–146`). Only the owner's user key finds a vault by name.
- **One's own account**, with the current password (it must open this very user key, `Accounts/UserCatalog.cs:189–195`, and counts against the password-check limit) and the username typed out (`Deletion.cs:51–62`, `POST /api/account/delete`, `Api/AccountApi.cs:108`). The primary admin's can't be deleted: there is always one.
- **A deactivated user**, by an admin, with their username typed out (`Deletion.cs:69–86`, `POST /api/admin/users/{id}/delete`, `AccountApi.cs:116`). Only a deactivated one: deactivating first ended their sessions and revoked their keys, as a step of its own that can still be undone; the transaction checks again that the user is still deactivated.

What goes, in **one transaction** (`Vaults/VaultCatalog.cs:317–393`, `Deletion.cs:95–123`): each vault document, every slot on those vaults (the owner's, API keys', the dev token's, recovery), every document in `notes` (with the history inside), `trash`, `moves`, `embeddings`, `checkpoints` and `checkpoint_texts`; for an account also the user, their password, recovery and session slots, their API keys and the keys' slots. A fault leaves everything or nothing. Nothing brings a deleted vault back or writes for it afterwards (`VaultCatalog.cs:317–397`): loading is held off for the whole deletion, so a request that read the slot and the vault document just before the commit cannot put the vault back into the working set; the vault in memory is taken off writes and indexing (both its gates) and marked deleted after the commit; and the ids are tombstoned in the process before the transaction, which every load, write and indexing checks (`VaultCatalog.cs:526`, `EncryptedNoteStore.cs:382`, `Search/SearchIndex.cs:100`), so an older object of the vault, locked meanwhile but still held by a request or waiting in the search queue, writes nothing either. After the commit the content collections are swept once more, for a write of such an older object that was already under way. A vault made in another tab while the account was being deleted is deleted right after, in a second transaction (`Deletion.cs:118–121`).

What stays: **the invitations the user created**, and the record of the one they joined with. They are the instance's record of who invited whom; the accounts they made exist on their own; an open one stays usable until it expires and any admin can revoke it, as after a deactivation. Backups taken before (§7), and removed documents that MongoDB has not yet overwritten in its files (§5.4), keep the sealed data. The log line names the user, the vault ids and counts, never a vault name or content (`Deletion.cs:43, 59, 82`).

### Invitations and the first admin

Accounts exist only by invitation: single-use, expiring (7 days by default, at most 30), revocable, with a role (`Accounts/Invitations.cs`). The code is 32 random bytes; the database holds only its keyed hash. The code travels in the link's fragment (`/join#…`), which browsers do not send, so it reaches no request line, proxy log or `Referer`. The first (primary) admin is created with `docker compose exec kumbu kumbu bootstrap-admin`, which prints a 24-hour link on standard output, never to the log, and refuses once a primary admin exists (`src/Kumbu.Server/Cli/BootstrapAdmin.cs`). So the first account goes to whoever has a shell on the container, not to whoever first finds a fresh server.

### Passwords

At least 12 characters (counted as characters, not bytes), not one of the 10,000 most common passwords, nor such a word padded with digits or punctuation (`Accounts/PasswordPolicy.cs`). The sign-up form warns that username and password are less safe than GitHub with a passkey, which come later (`web/src/components/PasswordField.tsx:47`).

### Recovery, and why there is no password reset

At sign-up the user gets a **recovery key** (32 random bytes, Base32 with a `kumbu-rk-` prefix, `src/Kumbu.Core/Crypto/RecoveryKey.cs`), shown once, to download (as `kumbu-recovery-key.txt`, without the username) or copy. The join screen does not let the user continue until it was downloaded or copied and they ticked that it is stored. (The account itself already exists at that moment; the confirmation is a step of the web app, not a server-side condition.)

The recovery key opens the user key. With username and recovery key, the "forgot password" screen sets a new password. This ends every session, revokes every API key and **replaces the recovery key**: the used one opens nothing from then on, and the answer carries the new one (`UserCatalog.cs:116–147`, `Api/AccountApi.cs:159–166`). The screen shows it once, under the same rules as at sign-up, and says that the old key no longer works and that agents need new API keys (`web/src/routes/ForgotPassword.tsx`). Regenerating the recovery key needs the current password and invalidates the old one at once (`UserCatalog.cs:174–185`), so a stolen session alone cannot make a recovery key and lock the owner out.

The writes of a recovery come in an order that a crash cannot turn into a lock-out or a dead key: the credentials stamp, then the revocation of the API keys, then the new password and the new recovery slot together in one transaction, then the final stamp and a second revocation, as for a password change (`UserCatalog.cs:234–290`). The recovery slot is replaced only if it is still the one that was opened, so of two recoveries racing with the same key exactly one gets through; the other gets 401 and sets nothing, and nobody is shown a new key that no longer works. A crash before the transaction leaves the old password and recovery key working (the API keys may already be revoked), so the user simply recovers again; a crash after it, before the answer arrives, leaves the user the password they just chose. The forgot-password screen says so after a server fault or a lost connection: try to sign in with the new password and, if that works, make a new recovery key in Settings (`web/src/routes/ForgotPassword.tsx`).

What this means for a stolen recovery key: whoever uses it first holds the account, and the other's copy no longer works. That was already possible before, with one more step (the thief could make a new recovery key with the password just set); what the change removes is the back and forth, and the thief's API keys surviving the owner's recovery. The user key stays the same (§3).

**There is no password reset by an admin or the operator, by design.** Nobody but the user holds a credential that opens their user key. Without password and recovery key, the vaults cannot be decrypted by anyone, and the web app says so at sign-up. The alternative — an escrow key held by the instance — would give the operator or an admin a way into every vault and defeat the point of §4.

## 7. Web and deployment hardening

### Built into the server

| Measure | As built | Code |
|---|---|---|
| Fail-closed authentication | Every endpoint needs a caller unless explicitly anonymous (sign-in, join, invitation peek, recovery, `/healthz`, the OpenAPI document, static files). Every path under `/mcp` and `/api` needs a caller even without a matching endpoint. | `Auth/CallerMiddleware.cs:92–104` |
| CSRF | `SameSite=Strict`, and every state-changing `/api` request from a session or to an anonymous endpoint must carry an `Origin` equal to `KUMBU_PUBLIC_URL`; a missing `Origin` is refused. Bearer callers need none (a browser page can't set their header). | `Api/Security.cs:62–107` |
| Rate limits (in memory, one instance) | Sign-in 5/min per username and 20/min per address; join 5/min per address; recovery 5/min per address; password checks behind a session (password change, new recovery key, deleting a vault or the account) 5/min per username; wrong bearer keys 20/min per address, IPv6 counted per /64. Sliding windows. The per-username sign-in limit can be used to keep one user from signing in (§2). | `Api/Security.cs:118–190`, `Auth/WrongKeyLimiter.cs` |
| Security headers (every response) | `X-Content-Type-Options: nosniff`, `Referrer-Policy: same-origin`, `Content-Security-Policy: default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'`; `Cache-Control: no-store` on `/api`. `base-uri` and `form-action` do not fall back to `default-src`, so they are set explicitly: no `<base>` element can redirect the app's relative URLs, and a form can only post to the app itself (the web app's forms all submit through `fetch`). The one weakening is `'unsafe-inline'` for styles; scripts are `'self'` only. | `Api/Security.cs:16–61` |
| HSTS | `Strict-Transport-Security: max-age=31536000; includeSubDomains` on every response to a request that came over HTTPS as the server sees it, or whenever `KUMBU_PUBLIC_URL` starts with `https://`. Behind the proxy, "over HTTPS" is the proxy's `X-Forwarded-Proto`, which the server reads only with `KUMBU_TRUSTED_PROXY=true`; without it the header is ignored. With neither (plain-HTTP development) there is no HSTS header. A request over HTTPS to a loopback host (`localhost`, `127.0.0.1`, `[::1]`) gets none either, as with ASP.NET's own `UseHsts`: pinned there, the browser would force HTTPS on every local port for a year. An `https://` public URL sends it on every response, and since it covers subdomains, pointing that URL at an apex domain pins every subdomain to HTTPS for a year (README). No `preload`. Tested for each case in `tests/Kumbu.Server.Tests/Api/AccountApiTests.cs`. | `Api/Security.cs:27–30, 43, 52`, `Program.cs:130–142` |
| Rendering untrusted Markdown | `react-markdown` with `remark-gfm` and without `rehype-raw`: raw HTML in a note is shown as text, never rendered. | `web/src/note/wikiLinks.ts:52`, `web/package.json` |
| Dev token | Optional; unset, it opens nothing. If set, at least 16 characters, compared in constant time via hashes. The public Compose set-up leaves it empty. | `KumbuOptions.cs:8–22`, `CallerMiddleware.cs:118–124` |
| Client address and scheme behind a proxy | `KUMBU_TRUSTED_PROXY=true` takes the address from the last `X-Forwarded-For` entry only, and the scheme from `X-Forwarded-Proto`; the server refuses to start with it unless `KUMBU_PUBLIC_URL` is set. Any peer is trusted as the proxy, so this is safe only when the proxy is the container's only way in. | `Program.cs:130–142`, `KumbuOptionsValidator.cs:37` |
| Upload limits | At most 200 files and 5 MB of text per request, 16 MB request body; only Markdown files (`.md`, `.markdown`) are taken. | `Api/UploadApi.cs:33–57, 128–144` |
| No secrets in URLs | Keys only in the `Authorization` header; invitation codes in the fragment and request bodies; passwords and recovery keys in bodies. | `CallerMiddleware.cs`, `AccountApi.cs:97–100` |
| No secrets in logs | ASP.NET's request logging is lowered to warnings. The server's own log lines name vaults by id, users by username, keys and invitations by id; never a password, key, code, cookie, note path or vault name (seeding from a development folder excepted). They are metadata nonetheless: the log records when each vault is unlocked and locked (`Vaults/VaultCatalog.cs:477, 485, 565`), which user created or revoked which API key, or all of them (`Accounts/ApiKeys.cs:155, 202, 221`), who deleted which vault (by id) or account, with counts (`Accounts/Deletion.cs:43, 59, 82`), and who set a new password with a recovery key (`Accounts/UserCatalog.cs:145`), all with times. Checked by hand after the phase 8 test (`docs/notes-test-phase8.md`). Unhandled errors are still logged with their exception message by ASP.NET; those messages have not been audited exhaustively. | `Program.cs:29–31` |
| Outbound connections | Only the one-time model download from Hugging Face; no telemetry. | `Search/OnnxEmbedder.cs` |

### Deployment shape

The server speaks plain HTTP on port 8080 inside its container and relies on a TLS-terminating reverse proxy. The public instances (accounts design §2):

| Instance | Where | Operator |
|---|---|---|
| `kumbu.tidesson.net` | Chris's VPS, Docker Compose behind Traefik with Let's Encrypt | Chris; community reference and demo |
| `kumbu.internal.cor.energy` | COR.energy's Kubernetes, behind an ingress; publicly reachable | COR; only if COR decides for it, after this document |

`compose.yaml` as shipped is a local set-up: the server's port is published on localhost (`127.0.0.1:${KUMBU_PORT}:8080`), and server and MongoDB share one default network, MongoDB listening on all its interfaces, without authentication unless `KUMBU_MONGO_PASSWORD` is set (below). With `KUMBU_TRUSTED_PROXY=true` and that port published, any local process could send its own `X-Forwarded-For` and pick the address rate limits count against. The public shape needs the Traefik override, planned as `compose.traefik.yaml` and **not yet in the repository**: no published port for the server, MongoDB on a network of its own that the proxy never sees, the dev token unset, `KUMBU_PUBLIC_URL=https://…`, `KUMBU_TRUSTED_PROXY=true`. The proxy has to redirect HTTP to HTTPS; this is checked when the instance is deployed (deployment plan, Review Focus 2), not by the server.

### Supply chain: the server image

The server image is built by GitHub Actions from the repository (`.github/workflows/docker.yml`) and published as `ghcr.io/scai-ngo/kumbukumbu`. Every third-party action in the workflow is pinned to a commit hash, not a movable tag. Pull requests are built but never pushed. A push to `master` publishes branch- and commit-tagged images; `:latest` and the version tags move **only** on a `v*.*.*` release tag, so a merge alone never changes what a server tracking `:latest` runs. Every pushed image is signed with cosign, keyless, through GitHub's OIDC identity for that workflow run; the signature is recorded in Sigstore's public transparency log. To verify an image before running it (the README, "Images and releases", has the same command for any build of the workflow):

```sh
cosign verify ghcr.io/scai-ngo/kumbukumbu:latest \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github.com/SCAI-NGO/kumbukumbu/\.github/workflows/docker\.yml@refs/tags/v'
```

The identity names the repository, the workflow and the ref it ran for (`@refs/tags/v` accepts release builds only; `@` alone also accepts `master` builds). A valid signature shows that the image was built by this workflow in this repository; it says nothing about the review of the code it was built from. Pinning by digest (`ghcr.io/scai-ngo/kumbukumbu@sha256:…`, `KUMBU_IMAGE` in `.env`) after verifying makes the deployment immune to a later re-tag. What the build pulls in is pinned too: the base images by digest, with the tag beside it (`node:22-slim`, `mcr.microsoft.com/dotnet/sdk:10.0` and `aspnet:10.0`, `Dockerfile`), the npm packages by `web/pnpm-lock.yaml` (`--frozen-lockfile`), the NuGet packages, transitive ones included, by a `packages.lock.json` per project (`RestorePackagesWithLockFile`, `Directory.Build.props`), restored in locked mode by CI and the image build, which fail if a project's packages differ from its lock file (`.github/workflows/ci.yml`, `Dockerfile`). Dependabot proposes updates to the actions, the base images' digests and the NuGet packages weekly (`.github/dependabot.yml`); each is a pull request to review, not an automatic change. The MongoDB image (`mongo:8`, `KUMBU_MONGO_IMAGE`) is pulled by tag at deployment, not built in; pin it by digest in `.env` the same way.

Points for the operator, not handled by the code:

- **Proxy access logs** contain what the server's own log leaves out: request lines under `/api` name vaults and note paths (`/api/notes/Work/plan.md`) and carry search queries (`/api/search?q=…`). Disable the access log for `/api` or strip paths and query strings (README, "Security notes for a public instance").
- **MongoDB** runs in Compose without TLS, and without authentication unless `KUMBU_MONGO_PASSWORD` is set. With it, MongoDB starts with a key file it generates once inside its data volume (a replica set with authentication needs one), its health check creates the root user `kumbu` through MongoDB's localhost exception, which holds only while no user exists, and the server signs in with `Kumbu:Mongo:User` and `Kumbu:Mongo:Password`, kept apart from the connection string (`compose.yaml`, `src/Kumbu.Server/KumbuOptions.cs:104–117`). This also works on an existing data volume (README, "Security notes for a public instance"). The password sits in both containers' environment. Without it, MongoDB is protected only by being on an internal network (in the shipped `compose.yaml`, the same network as the server; see above). Traffic between proxy and server, and between server and MongoDB, is unencrypted inside the host. On Kubernetes, use network policies and MongoDB authentication.
- **Swap and core dumps** of the server process can hold plaintext and keys; disable them.
- **Backups** of MongoDB are safe to store in the sense of §4 (sealed), but contain the metadata of §5.6 and the password slots that offline guessing works on. Since keys are never rotated (§3), an old backup opens with any credential that was valid when it was taken. **Restoring a backup brings back the credentials of that moment:** old passwords and recovery keys work again, and API keys revoked since are valid again, with their slots (`Accounts/ApiKeys.cs:281`). After a restore, users should change their passwords, make new recovery keys and review their API keys. A restore also brings back the vaults and accounts deleted since, with their data and the credentials of that moment: delete them again.

## 8. AI providers

- **How agents connect today:** an MCP client with an API key (`Authorization: Bearer kumbu_…`) at `/mcp`. With Claude Code the connection goes from the user's machine to Kumbu²; the key sits in the user's local configuration.
- **What the provider sees:** whatever the agent reads (tool results enter the model's context) and whatever it writes. Kumbu² controls *which* vaults and *whether* the agent may write; it cannot control what the provider keeps. Employees should give agents keys only for vaults whose content COR's AI policy allows to go to that provider, and read-only keys where writing isn't needed.
- **What the provider does not get:** other vaults, the user's password, user key or recovery key. In the Claude Code set-up the API key itself is not normally sent to the provider either; that holds by design of the set-up, not by enforcement, since the agent can read its own configuration.
- **Embeddings** are computed inside Kumbu² (§5.5), not by an AI provider.
- **Later, with connectors** (claude.ai and similar, §9): the provider's servers will hold an OAuth refresh token that carries a slot on the granted vaults. That is key material at the provider, consistent with the fact that it sees the content anyway, but it will be stated plainly when built (concept §9).

## 9. What comes next

None of this is built. Re-keying comes first; the rest follows in the order Chris set (accounts design §6):

1. **Key rotation and re-keying**, the next step after this document. It does not protect what an old backup already holds (that data was sealed with the keys of its time), but it ends an old credential's reach into data written later. Two parts, built together:
   - *Rotating the user key*: a new user key, with the user's vault slots, password, recovery and session slots wrapped anew. A few small records per user; the notes are not touched. An old password or recovery key from a backup then no longer leads to the current user key.
   - *Re-keying a vault*: a new vault key, and everything in the vault sealed anew (notes, history, trash, checkpoints, embeddings, sealed paths and names), the keyed lookup hashes recomputed, and the slots of every user and API key with access wrapped anew. The sealed records carry no key version today, so the first version locks the vault for the moment it takes and does all of it in one MongoDB transaction; notes are small, so this is a matter of seconds. Triggered by a recovery, a password change that revokes the API keys, revoking an API key with write rights, and on request.
2. **OAuth 2.1 for connectors** (claude.ai, ChatGPT and others; build step 10). Kumbu² as its own authorisation server with PKCE; the consent screen chooses vaults and rights and *is* the creation of slots. The refresh token carries the slot; each access token gets an ephemeral slot with a MongoDB TTL of about an hour; each refresh rotates the refresh token and its slot; revoking a grant deletes all its slots at once (concept §9). Needs the public instance.
3. **GitHub login**, as identity only, optionally limited to an organisation and team (`KUMBU_GITHUB_ORG`, `KUMBU_GITHUB_TEAM`, `KUMBU_GITHUB_ADMIN_TEAM`), the pattern COR uses for its internal tools. GitHub says *who* someone is; it holds no key material and cannot open a vault (concept §8).
4. **Passkeys with the PRF extension** as a further slot on the user key: GitHub identifies, the passkey unlocks. A passkey without PRF cannot unlock; such a user sets a passphrase instead, and the UI says why.

Known gaps. Each is described where it matters above; apart from the first, none is scheduled yet:

- **No key rotation or re-keying yet** of user or vault keys: revoking a credential deletes its slot only, so old backups open with old credentials (§3); planned as the next step (item 1 above).
- **Deletion does not reach backups**, nor removed documents MongoDB has not overwritten yet (§5.4, §6).>>>>

- **An expired API key that is never presented again** keeps its slots until its owner makes another key; they are refused all the same (§6).
- **API key expiry is optional**; a stolen session can create keys without one, and they outlive it until revoked, which a password change does by default and a recovery always (§4, §6).
- **Restoring a backup revives old credentials**, revoked API keys and deleted accounts and vaults included (§7).
- **Open invitations of a deleted admin stay valid** until they expire or another admin revokes them (§6).
- **Targeted sign-in lockout** through the per-username rate limit (§2).
- **A vault stays unlocked** after sign-out until idle, and any caller's use keeps it unlocked (§2).
- **Removing a checkpoint is permanent**, also for an API key with write rights (§4).
- **Trash and move records' clear fields** are not bound into their seals (§5.2).
- **Model files are checked by SHA-256 only at download**, by size afterwards (§5.5).
- **MongoDB without TLS** in Compose, and without authentication unless `KUMBU_MONGO_PASSWORD` is set; on the server's network in the shipped `compose.yaml` (§7).
- **The Traefik override** (`compose.traefik.yaml`) for the public shape is **not yet in the repository** (§7).
- **The server log** records unlock and lock times per vault and key changes per user (§7).
- Rollback detection (a per-vault counter or manifest); zeroing keys in memory; rate limits shared across several instances (Redis); re-authentication before creating admin invitations; TOTP/2FA for password-only accounts; several server processes on one database (correct today, but one does not see another's new notes until its vault is reopened).

## 10. Glossary

| Term | Meaning here |
|---|---|
| **Vault** | A user's collection of notes (a folder tree); the unit of encryption and of API-key rights. |
| **Vault key / user key** | Random 256-bit keys; the vault key encrypts a vault, the user key wraps the vault keys of one user. |
| **Slot** | A key sealed under a KEK derived from one credential, stored in the database. Deleting the slot revokes the credential. |
| **KEK** | Key-encryption key: derived from a credential, used only to wrap or unwrap another key, never stored. |
| **AES-256-GCM** | Authenticated encryption: confidentiality and tamper detection in one step. |
| **Associated data (context)** | Text bound into a seal without being encrypted; the blob only opens with the same text, which pins it to its document, vault and revision. |
| **HKDF** | A standard way to derive several independent keys from one high-entropy secret, one per label. |
| **Argon2id** | A deliberately slow, memory-hard password hash, so that guessing passwords is expensive. |
| **Keyed hash (HMAC)** | A fingerprint that only the key holder can compute; lets the database enforce uniqueness of paths and names without seeing them. |
| **Working set** | A vault decrypted in server memory while in use; locked again after 15 idle minutes. |
| **Operator** | Whoever runs and controls the server process, its host and its database. |
| **MCP** | Model Context Protocol: how AI agents call Kumbu²'s tools. |
