# Key rotation and re-keying — design

Date: 2026-10-26. Implements spec `2026-10-25-oauth-connectors-design.md` §10 and Ruling 20 of plan `2026-10-26-oauth-connectors-and-rekeying.md`. Written from the code as built after Task 12 (commit `8f9f5e2`); Tasks 14–16 build it and follow this document where it departs from their briefs (§8). Paths are relative to `src/Kumbu.Server/` unless they start with `src/`, `docs/`, `web/` or `tests/`; `compose.yaml` is at the repository root.

## 0. Inventory

### Keyed by the user key

Every value below is derived from the user key and stored. Labels are HKDF labels; "AAD" is the associated data of the seal.

| What | Where stored | Derivation, AAD | Code |
|---|---|---|---|
| User key in the password slot | `user_slots`, kind `password` | sealed under Argon2id(password, salt), AAD `user:{userId}:password` | `Accounts/UserCatalog.cs:325–336, 352` |
| User key in the recovery slot | `user_slots`, kind `recovery` | sealed under HKDF(recovery key, `kumbu/v1/user-recovery-kek`, user id), AAD `user:{userId}:recovery` | `UserCatalog.cs:338–346` |
| User key in each session slot | `user_slots`, kind `session` | sealed under HKDF(session secret, `kumbu/v1/session-kek`, user id), AAD `user:{userId}:session` | `Accounts/Sessions.cs:54, 167, 170` |
| Key id | `users.keyId` | HKDF(user key, `kumbu/v1/user-key-id`, user id); written at creation, read nowhere | `UserCatalog.cs:68, 349`, `Accounts/UserDocuments.cs:42–43` |
| Vault key in each user slot | `slots`, kind `user` | sealed under HKDF(user key, `kumbu/v1/user-slot-kek`, vault id), AAD `slot:{vaultId}:user` | `Vaults/VaultCatalog.cs:183, 854, 858` |
| User slot lookup | `slots.lookup` | HKDF(user key, `kumbu/v1/user-slot`, user id) | `VaultCatalog.cs:180, 852` |
| Vault name hash | `slots.nameId` (user slots) | HMAC(HKDF(user key, `kumbu/v1/vault-name-id`, no salt), normalised name) | `VaultCatalog.cs:182, 249` |
| API key's name and vaults | `api_keys.meta` | sealed under HKDF(user key, `kumbu/v1/api-key-meta`, user id), AAD `api-key:{keyId}:meta` | `Accounts/ApiKeys.cs:56, 107, 340, 342` |
| API key's name hash | `api_keys.nameId` | `WriterNames.NameId`: HMAC(HKDF(user key, `kumbu/v1/api-key-name-id`, user id), lower-cased name); unset once revoked or retired | `ApiKeys.cs:106, 188`, `Accounts/WriterNames.cs:18, 50–51` |
| Connection's name, rights, client | `connections.meta` | sealed under HKDF(user key, `kumbu/v1/connection-meta`, user id), AAD `connection:{id}:meta`; written at creation, rights change and consent | `Accounts/Connections.cs:67, 115, 237, 321, 402–407` |
| Connection's name hash | `connections.nameId` | the same `WriterNames.NameId`; unset once revoked or retired | `Connections.cs:114, 191, 362, 502` |

Not keyed by the user key, so stage 1 leaves them alone: API key slots (KEK from the API key, `VaultCatalog.cs:318`), an API key's writer label (sealed under the key, `ApiKeys.cs:108`), connection slots (`slots.kind == "connection"`, KEK from the code or token, `VaultCatalog.cs:376–389`), the token records in `oauth_tokens` (grant and successor sealed under the token itself, tombstones without either, `OAuth/OAuthTokens.cs:161, 438, 631, 651–652`, `Connections.cs:533–542`). The user key also passes through `Auth/RequestCaller.cs:26–63`, `Auth/CallerMiddleware.cs:95–96`, `Vaults/Credentials.cs:25–27`, `Accounts/Deletion.cs:34–55` and the API handlers without being stored.

### Keyed by a vault key

`UnlockedVault` derives three keys from the vault key: content (`kumbu/v1/content`), path id (`kumbu/v1/path-id`), text id (`kumbu/v1/text-id`), plus the cache key id (`kumbu/v1/key-id`) (`Vaults/UnlockedVault.cs:26–29`).

| What | Collection, `pid` | AAD | Code |
|---|---|---|---|
| Note with its history | `notes`, pid = HMAC(path key, path) | `note:{vaultId}:{docId}:{revision}` | `UnlockedVault.cs:90, 93, 97–106` |
| Vault name and embedding provider choice | `vaults.meta` | `vault:{vaultId}` | `UnlockedVault.cs:109–110`, `VaultCatalog.cs:116, 211, 254, 734, 784` |
| Trashed note | `trash`, pid of its path | `trash:{vaultId}:{docId}` | `UnlockedVault.cs:112–119`, `Vaults/EncryptedNoteStore.cs:404` |
| Move record | `moves`, pid of the *from* path | `move:{vaultId}:{docId}` | `UnlockedVault.cs:121–128`, `EncryptedNoteStore.cs:161` |
| Embeddings of a note | `embeddings`, pid of its path | `embeddings:{vaultId}:{base64 pid}` (the pid is in the AAD) | `UnlockedVault.cs:131–147` |
| Checkpoint | `checkpoints`, pid = HMAC(path key, `checkpoint:` + name) | `checkpoint:{vaultId}:{docId}`; the payload holds the **text ids** of its notes | `UnlockedVault.cs:165, 170–188` |
| Checkpoint text | `checkpoint_texts`, pid = HMAC(text key, content) | `cptext:{vaultId}:{base64 text id}` | `UnlockedVault.cs:168, 190–202` |
| Vault key in slots | `slots`: `user` (owner), `key` with `apiKey` (API key, vault key + right), `connection` (token, vault key + right), `key` without `apiKey` and `recovery` (dev-token vaults only) | `slot:{vaultId}:{kind}`, `slot:{vaultId}:key:{keyId}`, `slot:{vaultId}:connection:{connectionId}:{tokenId}` | `Vaults/Documents.cs:23–30`, `VaultCatalog.cs:125, 183, 318, 388, 843, 858–869` |

Clear fields that anchor write conflicts and that the design reuses: `connections.wraps`, incremented by every slot-issuing transaction (`Connections.cs:597`, `OAuthTokens.cs:193`), and `users.vaultsDeleted` / `connectionsChanged`, written by vault deletions and rights changes (`UserDocuments.cs:77–84`, `VaultCatalog.cs:569–578`). `ConnectionRevocation.RevokeTokensAsync(db, session, filter, reason, now, ct)` deletes a connection's slots, turns its token records into 24-hour tombstones and stamps `tokensRevokedAt`/`tokensRevokedReason` (`Connections.cs:458, 465–482`).

## 1. Why

`docs/security-architecture.md` §3 "No key rotation" (line 98) and §2 "Old copies of the database with old credentials" (line 50): revoking a credential deletes its slot and changes no key, so an old backup plus a credential valid then opens those vaults, and an exposed vault key stays valid for every later copy. §9 item 1 plans the fix. Rotation cannot take back what an old backup holds; it ends an old credential's reach into data written afterwards.

Both stages are needed. Without stage 1, a leaked old password plus an old backup yields the unchanged user key, which opens the current user slots in any later dump. Without stage 2, a vault key once exposed (through a revoked API key's slot in a backup) opens every later note.

## 2. Stage 1 — rotate the user key

`Accounts/UserKeyRotation.cs`. A new random user key U′ replaces U. One transaction writes:

- **`user_slots`**: the password slot re-sealed under the password at hand (new Argon2 salt); a new recovery key and slot; the current session's slot, if one is kept, re-sealed under its cookie secret; every other session slot deleted.
- **`users`**: `keyId` = HKDF(U′, …); `credentialsChangedAt` = T; the kept session's `createdAt` = T + 1 ms, since `Sessions.cs:106` refuses `createdAt <= credentialsChangedAt`. The stamp refuses every session and every key or code creation whose credential was read before T (`ApiKeys.cs:126–131`, `Connections.cs:132–137`, `OAuthTokens.cs:184–185`).
- **`slots`**: every slot found by U's lookup (kinds `user` and `user-next`, §3) gets U′'s lookup and wrapping; `user` slots also get the vault name hash under U′. To get the name, the vault's meta is opened with the vault key from the slot.
- **`api_keys`, `connections`** of the user, revoked and expired ones included (the list shows them): meta re-sealed under U′; `nameId` recomputed where it is set and left unset where it isn't.

Not touched: API key and connection slots, token records, vault contents.

**Recovery key.** Outside a recovery the old recovery key is not at hand, so its slot can't be re-sealed. Leaving it would let it open U, which no longer opens any vault: a recovery with it would set a password for a dead key and lock the user out. Leaving no recovery slot risks the same lock-out after a forgotten password. So a rotation always issues a **new recovery key, shown once** (Ruling 20, kept). Rotating only in a recovery would leave "Re-encrypt my vaults" without stage 1, and that leaves the leaked-password case of §1 open.

**A password change does not rotate** (Ruling 20, kept): it is not among spec §10's triggers. After a suspected compromise the user runs "Re-encrypt my vaults".

**`PrepareAsync` / `ApplyAsync`.** `PrepareAsync(user, oldUserKey, password, keepSessionSecret, recoveryUsed, ct)` reads and computes everything: Argon2 (64 MiB, about half a second), every user slot and meta opened and sealed again. It writes nothing. `ApplyAsync(session, rotation, ct)` only writes, inside the caller's transaction. Each write is conditional on the value it was prepared from: a slot on its old `wrapped`, a meta on its old `meta` and old `nameId` (or its absence). Inside the transaction, Apply also checks that no slot with U's lookup is left, and that the user has exactly the API keys and connections that were prepared. Any mismatch throws `RotationConflictException` and the transaction aborts; the caller prepares again (at most 3 times). Slots, metas and keys that don't open with U are logged and left as they are, outside the checks: they were unreadable before.

Recovery needs the split because of its write order (`UserCatalog.cs:235–302`): stamp (`:242`); revoke API keys and connection tokens (`:250–251`); one transaction with the new password slot and the recovery slot, which is replaced only if it is still the one that was opened (`:260–282`); stamp again (`:290`); revoke again (`:296–297`); delete sessions (`:300`). The rotation must commit in that same transaction. Otherwise a crash could leave the password opening U′ while the vault slots still need U, or the reverse, and the vaults would be lost. Argon2 and the re-sealing stay outside the transaction. Prepare runs after the first revocation, so the API keys it re-seals are the revoked ones and no `nameId` comes back. The opened recovery slot's `wrapped` goes in as `recoveryUsed`, so that of two recoveries with one key exactly one gets through, as today.

`RotateAsync(user, oldUserKey, password, keepSessionSecret, ct)` checks the password (it must open `oldUserKey`, `UserCatalog.cs:190–196`), then prepares and applies in a transaction of its own, retried with `WithTransactionAsync`: the `lastUsedAt` writes on API keys in use conflict with it. It does not stamp before preparing. The stamp inside Apply is enough, and a stamp before it would sign the user out if Apply then failed.

A new vault created with U while the rotation runs would get a slot under U's lookup, and so be lost. `CreateForUserAsync` inserts its slot before its transaction (`VaultCatalog.cs:169–232`). It therefore gets the session's `credentialReadAt`, and its transaction requires `credentialsChangedAt < credentialReadAt`, as an API key's does. That transaction writes the user document (`:219–221`), and so does Apply, so the two conflict; either Apply's slot check sees the new slot, or the creation is refused.

## 3. Stage 2 — re-key a vault

`Vaults/VaultCatalog.Rekey.cs`. Only a vault with a `user` slot of this user that opens with the given user key: no dev-token vault, since those have no owner session (Ruling 20).

### One transaction does not fit; batches with a two-key period

The spec and the brief ask for one transaction per vault. That holds only for small vaults, so the design re-seals in batches.

MongoDB 7.0.14 (the tests) reports `transactionLifetimeLimitSeconds` 60, `transactionSizeLimitBytes` unlimited, `transactionTooLargeForCacheThreshold` 0.75 and `maxTransactionLockRequestTimeoutMillis` 5. Since 4.2 a transaction may span several 16 MB oplog entries. Only each document stays at 16 MB at most. The binding limit is the WiredTiger cache, which must hold everything the transaction dirties until it commits. A measurement on 2026-10-26 rewrote N documents of S random bytes each in one transaction, on a single-node replica set with `--wiredTigerCacheSizeGB`:

| Cache | Rewritten in one transaction | Result |
|---|---|---|
| 7.5 GB | 2000 × 40 KB (80 MB); 5000 × 40 KB (200 MB) | committed in 2.9 s; 11.2 s |
| 1 GB | 2000 × 40 KB (80 MB) / 2000 × 60 KB (120 MB) | committed in 4.9 s / failed: "oldest pinned transaction ID rolled back for eviction" |
| 256 MB | 2000 × 10 KB (20 MB) / 2000 × 15 KB (30 MB) | committed in 2.1 s / failed the same way, partly as `WriteConflict` with `TransientTransactionError`, which a driver retry repeats until it gives up |

So a transaction can rewrite roughly 8–10 % of the cache; time is not the limit. A container's default cache is 50 % of (RAM − 1 GB), at least 256 MB, and production runs `mongo:8` (`compose.yaml:3`). A note's blob carries up to 10 earlier versions (`src/Kumbu.Core/Notes/NoteWriter.cs:19`). With 2–3 KB per version that is 20–35 KB. Its embeddings add 2 KB of Base64 per chunk (768 or 1024 half-floats, `Search/ModelFiles.cs:22, 27`). A vault of 2000 notes is therefore likely to hold 40–80 MB. That fails on a small VPS, and a transient-error retry turns the failure into a two-minute stall.

**Decision: batched re-sealing with a two-key period, for every vault.** Each batch is a transaction of at most **4 MiB of sealed bytes or 500 documents**, about a fifth of what passed on a 256 MB cache. A small vault takes three short transactions (start, one batch, finish). There is one code path, and it has no size limit.

### Run

1. **In-process mark.** The vault id goes into `_rekeying`, before any gate is taken (as `_deleted` comes first in `DeleteVaultsAsync`, `VaultCatalog.cs:473`). A second re-key of the same vault in this process is refused. `IsRekeying(id)` joins `IsDeleted(id)` at every write and index check (`EncryptedNoteStore.cs:461`, `Search/SearchIndex.cs:131, 160, 178`, `VaultCatalog.cs:733`), so no object of the vault writes from now on. Then both gates of the vault in memory are taken, `Gate` then `IndexGate` (`VaultCatalog.cs:489–495`), and `_loading` for the start transaction (`:504`).
2. **Start transaction.** A new vault key K′. It goes into a **`user-next`** slot (new kind, `Documents.cs`), wrapped as the user slot is but with the AAD `slot:{vaultId}:user-next`, under the owner's lookup, without a name hash. The vault document gets the clear field `rekeyStartedAt`. Every API key with a slot on the vault is revoked **whole**: `revokedAt` set, `nameId` unset, all its slots deleted, as `ApiKeys.RevokeAsync` does (`ApiKeys.cs:179–200`). Ruling 20 and spec §10: the server can't re-wrap a slot without the key. Every connection listing the vault (`connections.vaults`) and not revoked goes through `ConnectionRevocation.RevokeTokensAsync(…, ConnectionReasons.Rekeyed, …)` and gets `$inc wraps`. Its tokens become tombstones, so a revoked token stays recognised (Ruling 12), and the connection needs a reconnect. The vault id goes into the owner's `rekeyPending` (`$addToSet`) and `vaultsRekeyed` is incremented: a real write to the user document, against concurrent creations. After the commit, the vault in memory is marked `Retired` and dropped from `_open`, and `_loading` is released.
3. **Passes.** Each collection of the vault (`notes`, `trash`, `moves`, `embeddings`, `checkpoints`, `checkpoint_texts`) is streamed from MongoDB, not from the working set: that misses damaged documents and holds no checkpoint texts. Each document is opened with K′ (already done) or K (to re-seal). One that opens with neither is **damaged**: left as it is, counted and logged by id. It was unreadable before and stays so. Re-sealing keeps each document's `_id` and `rev`; it recomputes `pid` under K′ and seals under K′'s content key, with the AAD of the new pid where the AAD has one (embeddings, texts). Checkpoint texts go first, building the map from old to new text id; both ids can be computed from the content, so a resumed run rebuilds the map. Checkpoints are re-sealed with their text ids mapped. Each batch writes with the filter `{_id, blob: old blob}`. A batch in which any write matches nothing is aborted and that collection is read again (a stray write landed). Passes repeat until one finds nothing under K. With no interruption that is the first pass plus one confirming pass, reading the vault twice.
4. **Finish transaction** (under `_loading`). Replaces the user slot's `wrapped` with K′ (filter: the old `wrapped`), deletes the `user-next` slot, re-seals `vaults.meta` under K′ (filter: the old meta) and unsets `rekeyStartedAt`. It `$pull`s the id from `rekeyPending` and increments `vaultsRekeyed` and the `wraps` of the vault's connections again. The vault id goes into `_rekeyed` (process lifetime) and out of `_rekeying`.
5. **Nothing is re-indexed.** The vault is not put back into the working set; the next request loads it with K′. `SearchIndex.CatchUp` finds the re-sealed embeddings current, because their version is the content's hash (`src/Kumbu.Core/Notes/StoredNote.cs:60`) and doesn't change.

Returns `RekeySummary(long Documents, long Damaged, IReadOnlyList<string> RevokedApiKeys, IReadOnlyList<string> DisconnectedConnections)`. A resumed run (a `user-next` slot already exists) skips step 2's revocations, which are done.

### Old keys must not escape into new slots or writes

- **Loading.** `LoadAsync` (`VaultCatalog.cs:770–821`) returns nothing for a vault whose document has `rekeyStartedAt`. `OpenForUserAsync` leaves such a vault out while it is in `_rekeying`. When it is not, a crash left it, and the owner's session resumes it inline; the request waits. A key opened from a slot read before the finish fails on the meta, or on the cached key id at `:778`. For an id in `_rekeyed` that answers `VaultUnavailableException("This vault was just re-encrypted; try again.")`, not a crypto error.
- **Held objects.** `UnlockedVault.Retired` is set after the start's commit, under both gates. A write or indexing through it throws that same exception (writes) or stops (index). An older object of the vault, not in `_open` but held by a request, is stopped by `IsRekeying` during the run. After the run it fails its writes: its `pid`s and seals no longer match, notes fail their compare-and-swap on `rev`, and a stray insert would show as a damaged document. That is the same window, a check passed just before the mark, that deletion documents.
- **Index queue.** The queue key is `(provider, vault id, path)` (`SearchIndex.cs:52, 99`). An item queued for the retired object would make the new object's catch-up skip that path. `IndexAsync` therefore re-enqueues a retired object's path for the current object, if one is loaded.
- **New slots.** Two paths wrap vault keys opened from user slots: `ApiKeys.CreateAsync` (`ApiKeys.cs:96`) and `OAuthTokens.IssueCodeAsync` (`OAuthTokens.cs:145`). `UserVaultKeysAsync` (`VaultCatalog.cs:270–301`) refuses a vault with `rekeyStartedAt`. Both issuing transactions open the user slots again, as read in the transaction, and require the same keys as those they wrapped. Otherwise they throw `VaultUnavailableException("This vault was just re-encrypted; try again.")`. Their writes conflict with the start and the finish: the key's on the user document (`ApiKeys.cs:126–131` against `vaultsRekeyed`), the code's on the connection (`OAuthTokens.cs:193` against `wraps`). A conflicting transaction retries and then sees the change. The exchange and the refresh re-wrap from the code's or token's own slots. Those are deleted and their records tombstoned at the start, and both transactions write the connection document, so a retried one finds its code or token revoked.
- **Deletion wins.** `DeleteVaultsAsync` deletes every slot of the vault, `user-next` included. A run checks `IsDeleted` before each batch, and its filtered writes then match nothing. It stops with `VaultDeletedException`. A deletion waits for the gates that a run holds only during the start. The resume drops ids from `rekeyPending` whose vault is gone.

Single process only, as the rest of the working set (security document §9, last known gap). Another server process on the same database would keep its old object and write under K.

## 4. Triggers

- **Recovery** (`UserCatalog.RecoverAsync`, `:122–147`). Stage 1 inside the recovery's transaction, in the order of §2. The rotation records every vault id of the user in `rekeyPending` in the same transaction. Then the recovery request runs stage 2 for each vault with U′, one run each, removing each id at its finish. A vault that fails is logged and stays pending. A recovery never fails because of stage 2: it must stay the way back into the account. The recovery transaction retries transient errors instead of returning false (`:279–282`). The conditional replacement of the recovery slot keeps a lost race from passing, and old sessions' slots, which `Sessions.ResolveAsync` deletes on refusal (`Sessions.cs:106–109`), would otherwise turn a racing request into a failed recovery. API keys and connections are revoked by the recovery already (`:250–251`).
- **Sign-in.** `AccountApi.SignInAsync` calls `Rekeying.ResumePendingAsync` when `rekeyPending` is non-empty, before the session starts; the answer waits. The same happens when a session's request meets a vault with `rekeyStartedAt` (§3).
- **Settings, "Re-encrypt my vaults"** (`POST /api/account/reencrypt`, password). Password check first, under the password-check limit. Then **stage 2 for every vault with the current U, then stage 1** with the current session kept, and the new recovery key answered, shown once through `RecoveryKeyReveal`. The order puts the long part first. A crash during stage 2 leaves U, the password and the old recovery key valid, with the remaining vaults in `rekeyPending` (set before the first run). Only stage 1's single transaction stands between the new recovery key and its answer. If the answer is lost, the page says to make a new recovery key in Settings, as the forgot-password screen does.
- **Per vault** (`POST /api/vaults/{vault}/rekey`, password). Next to "Delete vault" (Ruling 19). The password is checked before the name is looked up (as `Deletion.cs:38`), then stage 2. The dialog first lists what will lose access, from `GET /api/vaults/{vault}/access`: API keys with a slot on the vault and connections listing it, not revoked.

## 5. Crash points

| Crash | State left | What follows |
|---|---|---|
| Stage 1, before Apply commits | Nothing written (recovery: API keys and tokens already revoked, as today) | Old password and recovery key open U; every vault opens |
| Stage 1, after commit, before the answer | U′ in force, new recovery key not seen | Sign in with the password; make a new recovery key |
| Stage 2, before or inside the start | Nothing written | Vault opens with K |
| Stage 2, after the start, during the passes | `user-next` holds K′, the user slot K, the meta K. Each document opens under exactly one of K, K′. API keys revoked, connections `rekeyed`, the id in `rekeyPending` | The vault doesn't load; the owner's next sign-in or request resumes |
| Stage 2, inside the finish | As the row above | As the row above |
| Stage 2, after the finish | Done | Vault opens with K′ |
| Between vaults (recovery, Settings) | Finished vaults under new keys; the rest in `rekeyPending` | Resumed at sign-in |

Stage 1 during a two-key period re-wraps the `user-next` slot with the user slot. The vault name is still read through K and its meta, which agree until the finish.

## 6. What it protects, and what not

- An old backup keeps what it held: whatever opened it then, an old password or recovery key or a revoked API key, still opens it.
- An old credential plus an old backup cannot open data written after the re-keying. That data is under K′, and K′ is in no slot that an old credential opens. With stage 1, an old password or recovery key reaches U, never U′.
- A current dump plus an old credential opens nothing of the user's: no slot in it wraps U, and no slot wraps K.
- Not protected: the operator of the running server (security §2); documents that were damaged before the run (left under their old seal); dev-token vaults; a password change alone, which re-wraps U and doesn't rotate it.
- Metadata added in the clear: `rekeyStartedAt` on a vault (only during a run), the `user-next` slot kind, `users.rekeyPending` (vault ids) and `users.vaultsRekeyed` (a count). Task 17 adds them to §5.6 of the security document.

## 7. Tests

Task 14, `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs`: as in its brief, plus `A_vault_created_during_rotation_is_not_lost` (a creation with the old key after Apply → 401) and `A_recovery_race_still_lets_one_through` (two recoveries with one key: one succeeds, the other's prepared rotation conflicts, nothing written).

Task 15, `tests/Kumbu.Server.Tests/Vaults/RekeyTests.cs`: as in its brief, with these changes. `A_crash_at_any_point_leaves_one_consistent_key` uses the points `"after-start"`, `"after-batch"`, `"inside-finish"`, `"after-finish"` with a batch budget of a few documents. After each, every document opens under exactly one of K, K′; after a resume, all under K′ and the vault loads. `Rekeys_2000_notes_within_the_transaction_limit` is renamed `Rekeys_2000_notes_in_batches_within_the_limits`. It uses 2000 notes with 10 earlier versions of 2 KB each and embeddings, and asserts completion in under 50 s and every batch's sealed bytes within the budget. It logs the total time, the bytes and the batch count. New tests: `A_damaged_document_is_counted_and_left`, `A_key_or_code_issued_across_a_rekey_wraps_the_new_key_or_fails`, `Resume_finishes_a_vault_left_in_its_two_key_period`, `The_index_queue_moves_to_the_new_object`. Manual check, recorded in the task report: the 2000-note test against a mongod started with `--wiredTigerCacheSizeGB 0.25`.

Task 16, `tests/Kumbu.Server.Tests/Accounts/RekeyingTests.cs`: as in its brief, plus `Reencrypt_interrupted_in_stage_2_keeps_the_old_recovery_key` and `A_failing_vault_does_not_fail_the_recovery`.

## 8. Corrections to the rulings and briefs

1. **Ruling 20, Task 15 brief, spec §10: "one MongoDB transaction" per vault → batches with a two-key period** (§3). Measured: a transaction can rewrite about 8–10 % of the WiredTiger cache, and a 2000-note vault (40–80 MB) exceeds that on a 256 MB cache. The failure is a transient error that the driver retries until it gives up.
2. **Task 15 interface:** `RekeySummary` gains `long Damaged`. `RekeyFaultAt` points become `"after-start"`, `"after-batch"`, `"inside-finish"`, `"after-finish"`. Task 15 also changes `Documents.cs` (`user-next` kind, `VaultDocument.RekeyStartedAt`), `UserDocuments.cs` (`VaultsRekeyed`; `RekeyPending` moves here from Task 16), `VaultCatalog.UserVaultKeysAsync`/`LoadAsync`/`OpenForUserAsync`, `ApiKeys.CreateAsync` and `OAuthTokens.IssueCodeAsync` (re-check of the vault keys in the transaction), and `SearchIndex` (queue hand-over). Reason: §3, "Old keys must not escape". Without these, a key or code wrapped just before a re-key carries K into new slots, and an indexed path is skipped.
3. **Task 15 brief "drop it from the working set":** kept, and LoadAsync must answer `VaultUnavailableException` for a key that no longer matches a vault re-keyed in this process. Today it would throw a `CryptographicException` from `VaultCatalog.cs:784`.
4. **Task 14 interface:** `PrepareAsync` gains `byte[]? recoveryUsed`, without which the "one of two recoveries wins" guarantee is lost. `ApplyAsync` throws `RotationConflictException`. `RotateAsync` does **not** stamp before preparing (the stamp is inside Apply; a stamp before it signs the user out if Apply fails), and it retries a conflict. The kept session's `createdAt` is set just after the stamp, or `Sessions.cs:106` refuses it. Apply's API key writes are conditional on the old `nameId`, so that a key revoked meanwhile doesn't get its name hash back.
5. **Task 14 files:** add `Vaults/VaultCatalog.cs` `CreateForUserAsync(…, credentialReadAt, …)` and its caller `Api/NoteApi.cs`. Reason: a vault created with the old user key during a rotation would be unreachable for good.
6. **Task 16, recovery:** Prepare runs after the first revocation. The recovery transaction retries transient errors. Stage 2 failures don't fail the recovery. Reason: §4.
7. **Task 16, Settings:** stage 2 for every vault first, then stage 1. `rekeyPending` is recorded before the first vault, not only by recoveries. Reason: §4. The new recovery key must be at most one transaction away from its answer.
8. **Task 16, resume:** also when a session's request meets a vault left in its two-key period, not only at sign-in. Sessions survive restarts, so a crash would otherwise leave the vault unavailable for up to 12 hours.
9. **Brief line numbers** (they refer to an older commit): `VaultCatalog.cs:112, 207, 618` are now `:116, 211, 784`; `DeleteVaultsAsync` is `:467–557`; `UserCatalog.cs:234–290` is `:235–302`; `UnlockedVault.cs:90–202` is unchanged.
