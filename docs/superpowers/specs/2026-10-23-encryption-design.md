# Encryption at Rest — Design (step 5)

**Date:** 2026-10-23
**Scope:** `docs/concept.md` §12 step 5 (vaults, key slots, HKDF, recovery key) and §7, plus the decided and small observations from [notes-test-phase4b.md](../../notes-test-phase4b.md).
**Status:** Settled with Chris: one PR for feedback and encryption; everything encrypted including paths; the dev token plus a recovery key unlock vaults until step 7; no migration of plaintext data. The rest written by Claude for review with the PR.

## 1. Intent

- A dump of the database, or a backup, shows nothing readable: no content, no paths, no vault names (concept §3).
- Every vault has its own key; credentials only unwrap it (key slots, concept §7). Losing the dev token is survivable with the recovery key; losing both is final, and the README says so.
- Nothing changes for agents: the same tools and commands, and since vaults are now real objects, an empty vault still exists.

Out of scope: password slots with Argon2id (there is no password before the login in step 7), OAuth token slots (step 9), revoking slots and per-client keys (step 7), zeroing keys in memory, several server instances.

## 2. Decisions

| Topic | Decision | Why |
|---|---|---|
| Primitives | AES-256-GCM, HKDF-SHA256, HMAC-SHA256, all from .NET. A sealed blob is format byte, 12-byte random nonce, ciphertext, 16-byte tag. | Standard, authenticated, no extra dependency. |
| Context binding | Every blob is sealed with associated data naming what it is (`note:<vault>:<doc id>`, `trash:…`, `move:…`, `vault:…`, `slot:<vault>:<kind>`). | A blob copied into another document or vault doesn't open: an attacker with write access to the database can't swap notes around unnoticed. |
| Vault key and slots | 32 random bytes per vault. An API-key slot stores it sealed with `HKDF(token, "kumbu/v1/key-kek", salt = vault id)` and is found by `HKDF(token, "kumbu/v1/key-lookup")`; neither the token nor the KEK is stored. A recovery slot uses `HKDF(recovery key, "kumbu/v1/recovery-kek", salt = vault id)`. | Concept §7. Two derivations with different labels: identification and key wrapping can't be confused. Per-vault salt: one KEK opens one slot. HKDF fits because both secrets are high-entropy; passwords will get Argon2id in step 7. |
| What is in the clear | Per document: the vault id, a keyed hash of the path (`HMAC(HKDF(vault key, "path-id"), path)`, for the unique index and lookups), the revision (for compare-and-swap) and the times the trash and move records expire on. Sizes and counts are visible too. | Metadata MongoDB needs to enforce uniqueness, atomic writes and expiry. Equal paths give equal hashes; nothing else can be learned from them. |
| Working set | A vault is decrypted completely when first used (notes, trash, moves) and served from memory; writes go to MongoDB first (one atomic operation, as since step 3) and to memory after. Idle vaults are locked after 15 minutes, and the least recently used ones above 512 MB. | Concept §7. Encrypted paths can't be searched by MongoDB, so search and listing need the plaintext in memory anyway. |
| Authorization | Every request derives the lookup from its own bearer token and uses only the vaults that token has a slot for. A cached vault saves the decryption, never the check. | Concept §7 ("the cache saves computation, never replaces authorization"). Ready for per-client keys in step 7. |
| Several processes | A compare-and-swap that fails refreshes that note from MongoDB, so a second process (or a test) never stays stale. | Cheap robustness; a real multi-instance setup needs change notifications later. |
| Recovery key | Created with the vault, Base32 with a `kumbu-rk-` prefix, written once to `Kumbu:Recovery:Path` (`/recovery`, a named volume, mode 0600) and nowhere else, never logged. `Kumbu:Recovery:Keys` at startup gives the current dev token a slot in their vaults: the way to change the dev token. | Concept §7 says the recovery key must be handed out; without a UI, a file the operator collects is the closest. Logs are kept and copied; key material doesn't belong there. |
| Vault creation | Only the seeder creates vaults (a folder becomes a vault). `write` still doesn't create vaults. | Vaults get created on purpose; in step 7 that is user registration. |
| Plaintext data | No migration. If the database holds plaintext notes, every vault access fails with `wipe it with docker compose down -v`. | Decided by Chris: development data only, and no migration code to carry forever. |

## 3. Feedback from phase 4b

| Observation | Change |
|---|---|
| 4 (decided) | `mv` keeps the link's form: a bare name stays a bare name, a path keeps its length, a leading `/` stays; relative links and anything not unique fall back to the vault path. A move back restores the original links. |
| 11 (decided) | The seeder fills only vaults without notes and without trash; the update and conflict logic is gone. |
| 1 | `mv` records the move for 30 days; a missing old path says `it was moved to … on … (dev)`, following later moves. |
| 2 | `rm` names the links that now lead nowhere. |
| 3, 13 | Link rewrites by `mv` and `restore` always get their own history entry, marked `dev (mv)` and `dev (restore)`, so later writes don't coalesce into them; a state already on top of the history isn't stored twice. Separating sessions properly needs per-client keys (step 7). |
| 6, 7 | `links` takes a section (`note.md#Heading`) and a directory, where it lists only broken and ambiguous links. |
| 8 | `backlinks` says `(no notes link here)` and lists ambiguous links that may mean the note. |
| 10 | `[[ ]]` and `[[|x]]` are no links. |
| 12 | The version block starts on a new line, for clients that join blocks without a separator. |

Observations 5 and 9 (an ambiguous link resolved by a move; heading targets not checked) stay open.
