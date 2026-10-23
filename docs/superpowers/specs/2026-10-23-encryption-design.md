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

## 4. Independent review

Before the PR, a separate review read the encryption code without being told the conclusions. It found the primitives sound (fresh nonces, correct HKDF/AES-GCM use, distinct labels, nothing secret in MongoDB or logs) and no way for one token to reach another's vault, and these defects, all fixed with tests:

| Finding | Fix |
|---|---|
| A request cancelled between the two writes of `mv`, `rm` or `restore` left a note at both paths, or in notes and trash. | Once the first write is committed, the rest runs (or undoes it) without the request's cancellation. |
| Locking a vault while a request held it could leave two copies in memory; a single vault above the memory cap would be reloaded on every request; every request marked all of its token's vaults as used. | Only the minute-by-minute sweep locks vaults, never one used in the last minute; a vault counts as used only when a request touches it. |
| If writing the recovery key failed, the vault was created anyway, without a usable recovery key, and the file was briefly readable by others. | The key file is written first (created 0600, flushed to disk); only then is the vault stored. If writing fails, no vault is created. |
| After a token change, the seeder could create a second, empty vault with the name of the locked one, which would later hide the real one. | While vaults exist that the dev token can't open, the seeder creates none and says why. |
| A vault in memory was handed to any token whose slot matched by lookup hash, without unwrapping it. | The slot is always unwrapped and must yield that vault's key. |
| The path id and revision in the clear were not bound to the sealed note. | The revision is part of the note's context; on opening, the path id must match the sealed path. |
| The plaintext check looked at `notes` only. | It also checks `trash` and the old unique index on `path`. |
| Expired trash and move records stayed in a busy vault's memory. | The sweep drops them. |

## 5. Known limits

- **Rollback.** Someone with write access to the database can put back an *older* sealed version of a document (from a backup) or a deleted note; it opens, because it is genuine. Detecting that needs an authenticated counter or manifest per vault. Not built.
- **Metadata.** Vault names appear in the server log (`Unlocked vault Demo`); MongoDB holds sizes, counts and times.
- **Weak tokens.** The lookup value of the dev token is in the database, so a guessable token can be tried offline. The server checks only the length (16 characters); the README says to use a random one.
- **One process.** Two server processes on one database stay correct (compare-and-swap, unique index, refresh on a failed swap), but one doesn't see the other's new notes until its vault is locked and opened again.

## 6. After the hands-on test (phase 5 notes)

| Observation | Change |
|---|---|
| 1 | A document that doesn't open is remembered by its path id (the only thing known about it). `write`, `edit`, `mv`, `rm`, `restore` and `cat` on that path say `damaged: the stored note doesn't open …` instead of throwing or claiming the path is free; a repair or removal in the database counts at the next attempt. The tool catches any remaining cryptographic error as a message. |
| 2 | `ls` of the vault and of the root ends with `(N notes here don't open: …)`. |
| 3 | `ls` without vaults says `(no vaults)`, and how many vaults don't open with the token. This hint has to go with per-user keys (step 7), where it would tell one user about another's vaults. |
| 4 | An empty folder becomes an empty vault. |
| 5 | Recovery log lines tell a new slot from an existing one and name `KUMBU_RECOVERY_KEYS` as well as `Kumbu__Recovery__Keys`; the summary counts refused vaults separately and uses singular and plural. |
| 6 | README: a vault whose notes were all removed and whose trash expired is seeded again. |
