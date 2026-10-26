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

Clear fields that anchor write conflicts and that the design reuses: `connections.wraps`, incremented by every slot-issuing transaction (`Connections.cs:597`; incremented by the code at `OAuthTokens.cs:193`, the exchange at `:316` and the refresh at `:462`), and `users.vaultsDeleted` / `connectionsChanged`, written by vault deletions and rights changes (`UserDocuments.cs:77–84`, `VaultCatalog.cs:569–578`). `ConnectionRevocation.RevokeTokensAsync(db, session, filter, reason, now, ct)` deletes a connection's slots, turns its token records into 24-hour tombstones and stamps `tokensRevokedAt`/`tokensRevokedReason` (`Connections.cs:458, 465–482`).

## 1. Why

`docs/security-architecture.md` §3 "No key rotation" (line 98) and §2 "Old copies of the database with old credentials" (line 50) state the gap. Revoking a credential deletes its slot and changes no key. So an old backup plus a credential that was valid then opens those vaults, and an exposed vault key stays valid for every later copy. §9 item 1 plans the fix. Rotation can't take back what an old backup holds. It ends an old credential's reach into data written afterwards.

Both stages are needed:
- Without stage 1, a leaked old password plus an old backup yields the unchanged user key. That key opens the current user slots in any later dump.
- Without stage 2, a vault key that was exposed once, say through a revoked API key's slot in a backup, opens every later note.

## 2. Stage 1 — rotate the user key

`Accounts/UserKeyRotation.cs`. A new random user key U′ replaces U. One transaction writes the following.

- **`user_slots`**:
  - the password slot, sealed again under the password at hand with a new Argon2 salt;
  - a new recovery key and its slot;
  - the current session's slot, if one is kept, sealed again under its cookie secret. Its `createdAt` is set to T + 1 ms, where T is the stamp below, because `Sessions.cs:106` refuses a session with `createdAt <= credentialsChangedAt`;
  - every other session slot is deleted.
- **`users`**:
  - `keyId` = HKDF(U′, …);
  - `credentialsChangedAt` = T. This stamp refuses every session, key creation and code issuance whose credential was read before T (`ApiKeys.cs:126–131`, `Connections.cs:132–137`, `OAuthTokens.cs:184–185`);
  - `rekeyPending` += every vault id of the user, so that stage 2 follows (§4).
- **`slots`**: every slot found by U's lookup gets U′'s lookup and is wrapped under U′. That covers kinds `user` and `user-next` (§3). A `user` slot also gets the vault name hash under U′; the name comes from the vault's meta, opened with the vault key from the slot. Slots that don't fit this are handled as follows:
  - **Orphan** (a `user` slot without a vault document, left by a creation that failed half way, `VaultCatalog.cs:192–207`). If it is older than `OrphanGrace` (`VaultCatalog.cs:686`, one minute), Apply deletes it. A younger one may belong to a creation still under way, so the rotation throws `RotationConflictException` and is retried.
  - **Damaged meta** (the slot opens but the vault's meta doesn't). It is wrapped under U′, and its `nameId` is dropped, since without the name no hash under U′ can be computed. It is logged.
  - **Slot that doesn't open.** It is left as it is, logged, and excluded by `_id` from the check below. It opened nothing before and opens nothing after.
- **`api_keys` and `connections`** of the user, revoked and expired ones included (the list shows them):
  - the meta is sealed again under U′;
  - `nameId` is recomputed where it is set and stays unset where it isn't;
  - a meta that doesn't open is left as it is, logged and excluded by `_id`.

Not touched: API key and connection slots, token records, vault contents.

**Recovery key.** Outside a recovery the old recovery key isn't at hand, so its slot can't be sealed again. Leaving the old slot would let the old key open U, which no longer opens any vault. A recovery with it would then set a password for a dead key and lock the user out. Leaving no recovery slot at all risks the same lock-out after a forgotten password. So a rotation always issues a **new recovery key, shown once** (Ruling 20, kept). Rotating only inside a recovery would leave "Re-encrypt my vaults" without stage 1, and that leaves the leaked-password case of §1 open.

**A password change does not rotate** (Ruling 20, kept): it isn't among spec §10's triggers. After a suspected compromise the user runs "Re-encrypt my vaults". If they think the password itself leaked, they change it too (§4).

**Prepare and Apply.** `PrepareAsync(user, oldUserKey, password, keepSessionSecret, recoveryUsed, ct)` reads and computes everything and writes nothing. That includes Argon2 (64 MiB, about half a second) and sealing every slot and meta again. `ApplyAsync(session, rotation, ct)` only writes, inside the caller's transaction:
- Each write is conditional on the value it was prepared from: a slot on its old `wrapped`, a meta on its old `meta` and its old `nameId` (or its absence). Each write must match exactly one document.
- Inside the transaction it checks that no slot with U's lookup is left, apart from the logged `_id`s.
- It also checks that the user has exactly the API key ids and connection ids that were prepared.
- On any mismatch it throws `RotationConflictException` and the transaction aborts. The caller prepares again, at most 3 times.

**Why recovery needs the split.** Recovery writes in this order (`UserCatalog.cs:235–302`):
1. stamp (`:242`);
2. revoke the API keys and connection tokens (`:250–251`);
3. one transaction with the new password slot and the recovery slot, replaced only if it is still the one that was opened (`:260–282`);
4. stamp again (`:290`);
5. revoke again (`:296–297`);
6. delete the sessions (`:300`).

The rotation must commit in step 3's transaction. Otherwise a crash could leave the password opening U′ while the vault slots still need U, or the other way round, and the vaults would be lost. Argon2 and the resealing run before that transaction.

**Recovery, wired exactly** (`UserCatalog.RecoverAsync`, `:122–147`, and `SetPasswordAsync`):
- `PrepareAsync` runs right after the first revocation (step 2), so the API keys it reseals are the revoked ones and no `nameId` comes back. It gets `keepSessionSecret: null` and `recoveryUsed` = the `wrapped` of the recovery slot that was opened (`:133`).
- `RecoveryReplacement` (`:228`) carries only `Used`. The new recovery slot comes from `PreparedRotation`, and so does the password slot. The second Argon2 at `:239` is skipped when a rotation is given.
- The transaction of step 3 is `ApplyAsync` and nothing else. It runs under `WithTransactionAsync`, so transient errors are retried; the recovery slot's filter on `Used` keeps a lost race from passing. A refused old session's slot, which `Sessions.ResolveAsync` deletes (`Sessions.cs:106–109`), would otherwise turn a racing request into a failed recovery. If the filter on `Used` matches nothing, another recovery with this key won, and the answer is `null` (401, as today).
- The answer is `PreparedRotation.RecoveryKey`. The `newKey` made at `:142` goes away.
- A third `RotationConflictException` becomes `RecoveryConflictException`, answered as 409 "Something changed during the recovery; try again." Nothing was written but the stamp and the revocations, so the old recovery key still works.

`RotateAsync(user, oldUserKey, password, keepSessionSecret, ct)` is for Settings. It checks the password (it must open `oldUserKey`, `UserCatalog.cs:190–196`), then prepares and applies in a transaction of its own under `WithTransactionAsync`. The `lastUsedAt` writes on API keys in use conflict with it. It does not stamp before preparing: the stamp inside Apply is enough, and a stamp before it would sign the user out if Apply then failed.

A vault created with U while a rotation runs would get a slot under U's lookup, and would be lost. `CreateForUserAsync` (`VaultCatalog.cs:169–237`) inserts its slot before its transaction. It therefore takes the session's `credentialReadAt`, and its transaction requires `credentialsChangedAt < credentialReadAt`, as an API key's does. That transaction writes the user document (`:219–221`), and so does Apply, so the two conflict: either Apply's slot check sees the new slot, or the creation is refused.

## 3. Stage 2 — re-key a vault

`Vaults/VaultCatalog.Rekey.cs`. Only a vault with a `user` slot of this user that opens with the given user key can be re-keyed. That excludes dev-token vaults, which have no owner session (Ruling 20).

### One transaction does not fit; batches with a two-key period

The spec and the brief asked for one transaction per vault. That holds only for small vaults.

MongoDB 7.0.14 (the tests) reports these limits:
- `transactionLifetimeLimitSeconds` 60;
- `transactionSizeLimitBytes` unlimited;
- `transactionTooLargeForCacheThreshold` 0.75;
- `maxTransactionLockRequestTimeoutMillis` 5.

Since 4.2 a transaction may span several 16 MB oplog entries, and only each single document is still capped at 16 MB. The binding limit is the WiredTiger cache, which must hold everything the transaction dirties until it commits.

The measurement on 2026-10-26 rewrote N documents of S random bytes each in one transaction, on a single-node replica set with `--wiredTigerCacheSizeGB`:

| Cache | Rewritten in one transaction | Result |
|---|---|---|
| 7.5 GB | 2000 × 40 KB (80 MB); 5000 × 40 KB (200 MB) | committed in 2.9 s; 11.2 s |
| 1 GB | 2000 × 40 KB (80 MB) / 2000 × 60 KB (120 MB) | committed in 4.9 s / failed: "oldest pinned transaction ID rolled back for eviction" |
| 256 MB | 2000 × 10 KB (20 MB) / 2000 × 15 KB (30 MB) | committed in 2.1 s / failed the same way, partly as `WriteConflict` with `TransientTransactionError`, which a driver retry repeats until it gives up |

So a transaction can rewrite roughly 8–10 % of the cache, and time is not the limit.

How large a vault gets:
- A container's default cache is 50 % of (RAM − 1 GB), and at least 256 MB.
- A note's blob carries up to 10 earlier versions (`src/Kumbu.Core/Notes/NoteWriter.cs:19`), so 20–35 KB at 2–3 KB a version.
- Embeddings add 2 KB of Base64 per chunk (768 or 1024 half-floats, `Search/ModelFiles.cs:22, 27`).

A vault of 2000 notes is therefore likely to hold 40–80 MB.

**Decision (confirmed by the coordinator): batched resealing with a two-key period, for every vault.**
- A batch is a transaction of at most **4 MiB of sealed bytes or 500 documents**, about a fifth of what passed on a 256 MB cache.
- A single document larger than 4 MiB goes alone in its batch. A document is capped at 16 MB.
- A small vault takes three short transactions: start, one batch, finish.

The measurement was on 7.0.14. Production runs `mongo:8` (`compose.yaml:3`), so Task 15 repeats the manual check there (§7).

### The per-vault write lock

`VaultCatalog` holds one `VaultLocks` per vault id: `Gate` (writes) and `IndexGate` (indexing). Every `UnlockedVault` of that id uses the same pair. Today each object has its own pair (`Vaults/UnlockedVault.cs:40, 60`), so an older object of the vault, held by a request after the sweep locked it, has gates nobody else waits on.

A re-key run holds **both locks from the mark to the finish**. Every write and every indexing checks under its lock:

```
!vault.Pending && !vault.Deleted && !IsDeleted(id) && !IsRekeying(id) && vault.KeyId == CurrentKeyId(id)
```

`CheckWritable` throws on it and `MayIndex` returns false on it. `CurrentKeyId` is recorded by `CreateAsync`, `CreateForUserAsync` and `LoadAsync` (never for a placeholder), and replaced at the finish. A vault never loaded in this process counts as current. The check is applied at these places:
- `EncryptedNoteStore.WriteAsync` (`Vaults/EncryptedNoteStore.cs:458–461`);
- `SearchIndex.IndexAsync` (`Search/SearchIndex.cs:127, 131, 160, 178`);
- `SetEmbeddingProviderAsync` (`VaultCatalog.cs:727–741`);
- `GetAsync`'s refresh of a damaged path (`EncryptedNoteStore.cs:75`).

Each of these also refuses *before* waiting when `IsRekeying(id)`, so no request queues behind a run. The answers are:
- `VaultDeletedException` for a deleted vault;
- `VaultBusyException("This vault is being re-encrypted; try again in a moment.")` during a run, and for a placeholder;
- `VaultBusyException("This vault was just re-encrypted; try again.")` for an object with the old key.

`VaultBusyException` is a new subclass of `VaultUnavailableException` (unsealed for it, `Vaults/Credentials.cs:86`), and it carries exactly these two texts. See §3, "Errors", for how it maps.

This replaces the per-object `Retired` flag of the first version and its "check passed just before the mark" window: in this process no write of the vault overlaps a run.

**Database fences**, against anything outside the lock (another process, a bug):
- `AtRevision` (`EncryptedNoteStore.cs:528–529`, used at `:126, 140, 397`) also filters on `pid`.
- So do the deletes by `_id` of a trash record (`:439`) and a checkpoint (`:345`).

A writer still holding K computes K's pid, which matches nothing once the document was resealed. That turns what would have been a destroyed note into a refused write.

### Run

1. **Mark.**
   - The id goes into `_rekeying`. A second run for the vault in this process is refused (`RekeyRunner` is single-flight, §4).
   - The vault's interrupt token is cancelled, so an external embedding call under way stops instead of holding `IndexGate` for minutes. Today's `DeletionToken` (`VaultCatalog.cs:620–625`) becomes `InterruptToken`, cancelled by a deletion and by a mark, and already cancelled while either is in force. The cancelled source is removed from `_deletions` at the mark, as a deletion does at `:476`. After a finish or an aborted run, the next `InterruptToken` call creates a fresh, uncancelled source, so indexing of the vault resumes.
   - The run takes `Gate`, then `IndexGate`, then `_loading` for the start transaction.
2. **Start transaction.**
   - If the vault already has a `user-next` slot, this is a resume: steps 2–3 are skipped. A unique partial index on `slots.vault` where `kind == "user-next"` keeps it to one, and the start looks for it inside its transaction.
   - Otherwise a new vault key K′ is made. It goes into a `user-next` slot, a new kind in `Documents.cs`. The slot is wrapped as the user slot is, with the AAD `slot:{vaultId}:user-next`, under the owner's lookup and without a name hash.
   - The vault document gets the clear field `rekeyStartedAt`.
   - Every API key with a slot on the vault is revoked **whole**: `revokedAt` is set, `nameId` unset and all its slots deleted, as `ApiKeys.RevokeAsync` does (`ApiKeys.cs:179–200`). The server can't wrap a slot again without the key (Ruling 20, spec §10).
   - Every connection that lists the vault and isn't revoked goes through `ConnectionRevocation.RevokeTokensAsync(…, ConnectionReasons.Rekeyed, …)` and gets `$inc wraps`. Its tokens become tombstones, so a revoked token stays recognised (Ruling 12), and the connection needs a reconnect.
   - The owner's `rekeyPending` gets the id (`$addToSet`), and `vaultsRekeyed` is incremented. That is a real write to the user document, so concurrent creations conflict with it.
   - After the commit the vault is dropped from `_open` and `_loading` is released.
3. **Passes.** Each collection of the vault is streamed from MongoDB: `notes`, `trash`, `moves`, `embeddings`, `checkpoints`, `checkpoint_texts`. They are read from the database, not from the working set, which misses damaged documents and holds no checkpoint texts. Each document is classified by its seal first and its `pid` second:
   - **Opens under K′** with the AAD its clear fields give: its `pid` is recomputed from the sealed path, name or content under K′, and corrected if it differs.
   - **Opens under K**: it is sealed again under K′, and its `pid` is recomputed from what is sealed inside, whatever the stored `pid` says. A stored `pid` that doesn't match is logged.
   - **Opens under neither**: it is **damaged**. It is left as it is, counted and logged by id.

   Resealing keeps `_id` and `rev`, and uses K′'s content key. Where the AAD contains the pid (embeddings, texts), it is the new pid.

   Checkpoint texts go first. They build the map from each old text id to its new one; both can be computed from the content, so a resumed run rebuilds the map. Checkpoints are then sealed again with their text ids mapped.

   Each batch writes with the filter `{_id, blob: old blob}`. If any write in a batch matches nothing, the batch is aborted and that collection is read again.

   A write that hits **DuplicateKey** on `(vault, pid)` means two documents now claim one path, name or text. The run keeps the document with the higher `rev` (for checkpoints the later `at`, for texts either one) and deletes the other, after logging its id.

   Between batches the run checks `IsDeleted(id)`. If the vault is being deleted, the run stops, releases its locks and throws `VaultDeletedException`, so a deletion waits at most one batch. Passes repeat until one finds nothing to change. Without interruptions that is the first pass plus one confirming pass, so the vault is read twice.
4. **Finish transaction** (under `_loading`):
   - the user slot's `wrapped` is replaced by K′, with a filter on the old `wrapped`;
   - the `user-next` slot is deleted;
   - `vaults.meta` is sealed again under K′, with a filter on the old meta, and `rekeyStartedAt` is unset;
   - the id is `$pull`ed from `rekeyPending`;
   - `vaultsRekeyed` is incremented, and so is the `wraps` of each connection that lists the vault.

   The three writes to the user slot, the `user-next` slot and the vault document must each match exactly one document. Otherwise the run aborts, takes the id out of `_rekeying`, keeps it in `rekeyPending` and throws: `VaultDeletedException` if the vault document is gone, else `VaultBusyException("This vault is being re-encrypted; try again in a moment.")`. Whoever next holds the user key resumes it (§4). After the commit, `CurrentKeyId(id)` becomes K′'s, the id goes into `_rekeyed` (for the life of the process) and out of `_rekeying`, and the locks are released.
5. **Nothing is re-indexed.** The next request loads the vault with K′. `SearchIndex.CatchUp` finds the resealed embeddings current: their version is the content's hash (`src/Kumbu.Core/Notes/StoredNote.cs:60`), which doesn't change.

The run returns `RekeySummary(long Documents, long Damaged, IReadOnlyList<string> RevokedApiKeys, IReadOnlyList<string> DisconnectedConnections)`. A resumed run returns empty lists, since the revocations were made by the start.

### While a vault waits or runs

- **Loading.** `LoadAsync` (`VaultCatalog.cs:770–821`) loads no working set for a vault whose document has `rekeyStartedAt`. Instead it returns a placeholder:
  - an `UnlockedVault` with `Pending = true`;
  - named, and given its embedding provider, from its meta. During the period the user slot and the meta still agree on K;
  - with no notes, trash, moves, embeddings or checkpoints;
  - never put in `_open`, never given a `CurrentKeyId`, and never raising `Unlocked`, so `SearchIndex.CatchUp` never sees it.

  `OpenForUserAsync` raises the new event `RekeyPendingFound(vaultId, userId, userKey)`, and `RekeyRunner` subscribes to it, as `SearchIndex` subscribes to `Unlocked` (§4). Only a session meets placeholders: API key and connection slots on the vault are gone from the start on.
- **Accessors.** Name lists keep a placeholder, so that the vault isn't reported missing or created again. Every other path refuses it.

  | Accessor | With a placeholder |
  |---|---|
  | `EncryptedNoteStore.VaultsAsync` (`Vaults/EncryptedNoteStore.cs:556–561`), the per-request map | keeps it |
  | `ListVaultsAsync` (`:21`); its users: `GET /api/vaults` (`Api/NoteApi.cs:132`), `RequireVaultAsync` (`NoteApi.cs:462`), the Seeder (`Notes/Seeder.cs:76`, dev token only, so never in practice) | lists the name |
  | `GET /api/vaults` (`NoteApi.cs:128–138`) | a `VaultView` with `pending: true`, its provider and no note count; it does not call `ListNotesUnderAsync` (`:134`) for it (Task 16 adds the field) |
  | `UnlockedVaultsAsync` (`EncryptedNoteStore.cs:271`); its users: `NoteApi.cs:130` (providers by name) and `:154` | keeps it; `PUT /api/vaults/{vault}/embeddings` (`:154`) reaches `SetEmbeddingProviderAsync`, whose `CheckWritable` throws `VaultBusyException` |
  | `AccountApi.VaultNamesAsync` (`Api/AccountApi.cs:315–316`), for the key and connection lists and the consent page (`Api/ConnectionApi.cs:151, 173, 182, 191`) | names it |
  | `VaultAsync` (`EncryptedNoteStore.cs:540–545`), the single-vault path of every read and write (`:35, 48, 59, 72, 91, 114, 184, 305, 341, 361, 425`, and the single-vault branches at `:39, 52, 205, 297, 415, 533`) | throws `VaultBusyException("This vault is being re-encrypted; try again in a moment.")` |
  | `AllVaultsAsync` (`:548–552`), for every cross-vault operation (`:39, 205, 297, 415, 533`) | leaves it out |
  | `CountDamagedAsync` over the root (`:52`, through `VaultsAsync` directly) | leaves it out |
  | `CheckWritable`, `MayIndex` (writes, indexing, the provider switch) | refuse it |
- **Errors.** `VaultBusyException` alone maps to 503 with `Retry-After: 5`, in `Api/ApiErrors.cs` beside the `VaultDeletedException` case at `:60`. The two `HistoryApi` catches that answer a `VaultUnavailableException` with 409 (`Api/HistoryApi.cs:160, 186`) get `when (e is not VaultBusyException)`. Every other `VaultUnavailableException` keeps its mapping today: the damaged checkpoint (`EncryptedNoteStore.cs:384`) and the standalone or plaintext MongoDB (`VaultCatalog.cs:886, 898`). MCP turns all of them into a tool error (`Mcp/ToolResult.cs:33`), as it does today.

### Old keys must not escape into new slots

Two paths wrap vault keys opened from user slots: `ApiKeys.CreateAsync` (`ApiKeys.cs:96`) and `OAuthTokens.IssueCodeAsync` (`OAuthTokens.cs:145`). Two checks guard them:
- `UserVaultKeysAsync` (`VaultCatalog.cs:270–301`) refuses a vault with `rekeyStartedAt` with `VaultBusyException("This vault is being re-encrypted; try again in a moment.")`.
- Both issuing transactions require, as read inside the transaction:
  - **`rekeyStartedAt` absent** on every vault they wrap. A key prepared before a start that commits after it would otherwise pass, because during the period the user slot still wraps K;
  - **the same keys**: the user slots, opened again, give the keys that were wrapped. A key prepared before a finish that commits after it would otherwise carry K.

  Either failure answers `VaultBusyException("This vault was just re-encrypted; try again.")`.

Both transactions also conflict with the start and the finish. The key's transaction writes the user document (`ApiKeys.cs:126–131`, against `vaultsRekeyed`), and the code's writes the connection (`OAuthTokens.cs:193`, against `wraps`). Retried, they see the change.

The exchange and the refresh wrap again from the code's or token's own slots. The start deletes those slots and tombstones their records, and both transactions write the connection document, so a retried one finds its code or token revoked.

A stale cached key returns `null` at `VaultCatalog.cs:778` and does not throw. A stale key whose meta doesn't open throws a `CryptographicException` from `:784`. For an id in `_rekeyed`, both answer "This vault was just re-encrypted; try again." instead.

**Index hand-over.** The queue key is `(provider, vault id, path)` (`SearchIndex.cs:52, 99`). An item queued for the old object would make the new object's catch-up skip that path. So `IndexAsync`, on meeting an object with an old key, puts the path in the queue again for `VaultCatalog.Current(id)` when that is loaded.

**Deletion.** `DeleteVaultsAsync` deletes every slot of the vault, `user-next` included. It sets the tombstone (`VaultCatalog.cs:473`) before waiting for the locks, and the run yields at its next batch. `RekeyRunner` drops ids from `rekeyPending` whose vault is gone.

Single process only, as for the rest of the working set (security document §9, last known gap). The fences make a writer in another process fail instead of destroying a document. A stale *insert* from another process stays sealed under K and shows as damaged.

## 4. Triggers and the runner

**`RekeyRunner`** (`Vaults/RekeyRunner.cs`) runs stage 2 in the background, never inline in a request's own flow.

- **Concurrency.** **One vault at a time, process-wide**: a single worker serves one global first-in-first-out queue (coordinator's ruling). Each run's batches already sit near the cache budget of §3, and parallel runs would multiply it past the ceiling. The worker runs on the app-stopping token (`IHostApplicationLifetime.ApplicationStopping`), not on a request's.
- **One entry per vault.** Each vault has at most one entry (single-flight), in one of three states: `Queued`, `Running` or `Backoff(until)`. The entry holds the user id, the user key, the failure count and the awaiters of its next attempt.
- **`Start(vaultId, userId, userKey)`** is fire-and-forget. Sign-in, recovery, Settings and `RekeyPendingFound` all call it.
  - No entry: one is created `Queued` at the tail.
  - Entry `Queued` or in `Backoff`: the newer key is swapped in, for example U′ after a rotation. Nothing else changes: the place in the queue and the backoff stay. A trigger during backoff does **not** reset it.
  - Entry `Running`: a no-op.
- **`RunAsync(vaultId, userId, userKey)`** is the per-vault request's call.
  - It does what `Start` does, with one difference: an entry in `Backoff` is moved to the queue's tail at once. An explicit request by the owner ends the wait; a background trigger doesn't.
  - It then awaits the entry's next attempt and returns its `RekeySummary`.
  - When that attempt fails, the exception is rethrown to every awaiter, and only then is the backoff scheduled. The request gets the error: a `VaultBusyException` answers 503, a wrong state its usual mapping.
  - The awaiter may stop waiting (the request's token cancels only its wait). The attempt goes on.
- **Backoff.** After the n-th consecutive failure the entry waits min(2ⁿ⁻¹ minutes, 1 hour), then goes back to the queue's tail. It **keeps the user key during backoff** (coordinator's ruling; Task 17 records it in the security document).

  The entry ends, and the key is dropped, when:
  - the run succeeds;
  - the vault is gone (`VaultDeletedException`, or no vault document);
  - the vault is no longer this user's: there is no `user` slot that opens with the key held, and no `user-next` slot. That happens, for example, after a recovery by someone else rotated the key and nobody swapped the new one in. The id then stays in `rekeyPending` for the next trigger with the right key.
- **Restart.** A crash or restart loses the runner, not the state: `rekeyPending` and `rekeyStartedAt` stay, and the next trigger starts it again.

The triggers:
- **Recovery.** Stage 1 runs inside the recovery's transaction (§2). The rotation records every vault in `rekeyPending`. After the commit, the API (`AccountApi.RecoverAsync`, `Api/AccountApi.cs:176–182`) answers with the new recovery key **at once** and calls `Rekeying.ResumePending(user, newUserKey)`, which starts the runner. A recovery never waits for stage 2 and never fails because of it. Until each vault's start, the vault stays usable under its old key: its user slot is already wrapped under U′. API keys and connections are revoked by the recovery already (`UserCatalog.cs:250–251`).
- **Sign-in.** `AccountApi.SignInAsync` (`Api/AccountApi.cs:123–135`) calls `ResumePending` *after* the session started and does not wait for it. Sign-in is never blocked.
- **A request that meets a pending vault** (`RekeyPendingFound`, §3) starts the runner with the session's user key.
- **Settings, "Re-encrypt my vaults"** (`POST /api/account/reencrypt`, with the password):
  1. The password is checked first, under the password-check limit.
  2. Stage 1 runs (`RotateAsync`, keeping the current session). It records every vault in `rekeyPending`.
  3. Right after stage 1 commits, and before answering, **all** the user's API keys are revoked whole (`ApiKeys.RevokeAllAsync`, `ApiKeys.cs:202–219`), and the tokens of all their connections are revoked (`Connections.RevokeAllTokensAsync(user, ConnectionReasons.Rekeyed, ct)`, `Connections.cs:256–273`), as a recovery does (coordinator's ruling). Access an attacker may hold must not outlive the request while stage 2 runs in the background.

     A key or code whose creation raced the rotation either committed before it, and is found by the revocation, or is refused by the rotation's stamp (`ApiKeys.cs:126–131`, `OAuthTokens.cs:184–185`). If a crash falls between the commit and the revocation, the keys stay until stage 2 revokes each one with its vault: every key has at least one vault, and every vault is pending.
  4. The request answers with the new recovery key, shown once through `RecoveryKeyReveal`.
  5. The runner then re-keys each vault in the background with U′. Progress shows as each vault's `pending` state.

  This is the recovery's order, and it is the simplest one that keeps both properties. The recovery key is lost only if the answer to the one transaction that made it is lost; then the page says to make a new recovery key in Settings, as the forgot-password screen does. Sign-in only starts the runner. A crash during stage 2 leaves `rekeyPending`, which is resumed with U′ (§5). The first version put stage 2 before stage 1 so that the long part couldn't stand between the recovery key and its answer. Running stage 2 after the answer achieves that too.

  The page says: "New keys for your account and every vault. API keys and connected apps lose access and need to be set up or reconnected; you get a new recovery key. If you think your password leaked, change it too."
- **Per vault** (`POST /api/vaults/{vault}/rekey`, with the password). The action sits next to "Delete vault" (Ruling 19). The password is checked before the name is looked up (as `Deletion.cs:38`). The request then **waits** for `RekeyRunner.RunAsync`: it is one vault, and the UI shows progress. It may wait behind other vaults in the global queue. If the request is cancelled, the run goes on, since it runs on the app's token. A failed attempt answers its error, 503 for `VaultBusyException`. Before the user confirms, the dialog lists what will lose access, from `GET /api/vaults/{vault}/access`: API keys with a slot on the vault, and connections that list it and aren't revoked.

## 5. Crash points

| Crash | State left | What follows |
|---|---|---|
| Stage 1, before Apply commits | Nothing written (recovery: API keys and tokens already revoked, as today) | Old password and recovery key open U; every vault opens |
| Stage 1, after commit, before the answer | U′ in force; new recovery key not seen; `rekeyPending` = all vaults | Sign in with the password (which starts the runner); make a new recovery key |
| Stage 2, before or inside the start | Nothing written | Vault opens with K; still in `rekeyPending` if stage 1 put it there |
| Stage 2, after the start, during the passes | `user-next` holds K′, the user slot K, the meta K. Each document opens under exactly one of K and K′, or is damaged. API keys revoked, connections `rekeyed`, the id in `rekeyPending` | The vault answers "being re-encrypted"; the next sign-in or request starts the runner, which resumes |
| Stage 2, inside the finish, or a finish mismatch | As the row above | As the row above |
| Stage 2, after the finish | Done | Vault opens with K′ |
| Between vaults | Finished vaults under new keys; the rest in `rekeyPending` | Runner resumes at sign-in or the next request |

If stage 1 runs during a two-key period, it wraps the `user-next` slot under U′ along with the user slot. The vault's name is still read through K and its meta, which agree until the finish.

## 6. What it protects, and what not

- An old backup keeps what it held. Whatever opened it then still opens it: an old password, an old recovery key, a revoked API key.
- An old credential plus an old backup can't open data written after the re-keying. That data is under K′, and K′ is in no slot an old credential opens. With stage 1, an old password or recovery key reaches U, never U′.
- A current dump plus an old credential and an old backup opens nothing of the user's that was written after the re-keying. **One exception: documents left damaged.** They keep whatever seal they had, possibly K with altered clear fields, and whoever holds K and guesses the original fields may open them. They were already unreadable to the user, and the run logs them.
- Not protected:
  - the operator of the running server (security §2);
  - dev-token vaults;
  - a password change alone, which wraps U again without rotating it;
  - writes from a second server process (§3).
- Metadata added in the clear:
  - `rekeyStartedAt` on a vault, during a run only;
  - the `user-next` slot kind;
  - `users.rekeyPending` (vault ids);
  - `users.vaultsRekeyed` (a count).

  Task 17 adds them to §5.6 of the security document.

## 7. Tests

**Task 14**, `tests/Kumbu.Server.Tests/Accounts/UserKeyRotationTests.cs`: the tests of its brief, plus:
- `A_vault_created_during_rotation_is_not_lost`: a creation with the old key after Apply gets 401;
- `A_recovery_race_still_lets_one_through`;
- `An_old_orphan_slot_is_removed_and_a_young_one_retries`;
- `A_slot_with_a_damaged_meta_is_rewrapped_without_its_name`;
- `A_third_conflict_in_a_recovery_is_retryable_not_wrong_key`;
- `Rotation_records_every_vault_as_pending`.

**Task 15a**, `tests/Kumbu.Server.Tests/Vaults/RekeyTests.cs`:
- `Everything_opens_with_the_new_key_and_reads_the_same`, from the brief;
- `Nothing_is_sealed_under_the_old_key_any_more`, from the brief;
- `Api_keys_with_the_vault_are_revoked_and_connections_disconnected`, from the brief;
- `An_old_credential_with_an_old_backup_cannot_read_later_writes`, from the brief;
- `A_vault_being_deleted_is_not_rekeyed`, from the brief;
- `A_crash_at_any_point_leaves_one_consistent_key`. The points are `"after-start"`, `"after-batch"`, `"inside-finish"` and `"after-finish"`, with a batch budget of a few documents. After each, every document opens under exactly one of K and K′. After a resume all open under K′, and the vault loads;
- `A_finish_that_matches_nothing_keeps_the_vault_pending`;
- `A_stale_object_writes_nothing_and_destroys_nothing`: hold an old `UnlockedVault`, re-key, then write, delete and restore from the trash through it. Every call throws `VaultUnavailableException`, and every document reads as before;
- `The_fences_refuse_a_write_with_the_old_pid`: the same writes made directly with a K pid match nothing;
- `A_document_sealed_under_the_old_key_with_a_new_pid_is_resealed`;
- `A_duplicate_pid_keeps_the_higher_revision`;
- `A_damaged_document_is_counted_and_left`;
- `A_deletion_during_a_run_wins_within_one_batch`;
- `A_large_document_goes_alone_in_its_batch`;
- `Rekeys_2000_notes_in_batches_within_the_limits`: 2000 notes with 10 earlier versions of 2 KB each, and embeddings. It asserts the run takes under 50 s and that no batch exceeds the budget, and logs the time, the bytes and the number of batches;
- `The_runner_runs_one_task_per_vault_and_backs_off`;
- `A_pending_vault_answers_being_reencrypted`: every accessor in the table of §3 behaves as stated. Writes, deletes, the provider switch and indexing through a placeholder throw `VaultBusyException` or do nothing. The vault list and the key list still name the vault. `Unlocked` is never raised for it;
- `The_runner_serves_one_vault_at_a_time_in_order`;
- `Start_during_backoff_swaps_the_key_and_keeps_the_backoff`;
- `RunAsync_rethrows_a_failure_then_backs_off`;
- `Only_VaultBusyException_answers_503`;
- `Indexing_resumes_after_a_finish`: a fresh interrupt token.

The manual check, recorded in the task report: the 2000-note test against a mongod started with `--wiredTigerCacheSizeGB 0.25`, on 7.0.14 and on `mongo:8`.

**Task 15b**, same class:
- `A_key_or_code_prepared_before_the_start_and_committed_after_fails`;
- `A_key_or_code_prepared_before_the_finish_and_committed_after_fails`;
- `A_stale_cached_key_answers_just_reencrypted`;
- `The_index_queue_moves_to_the_new_object`;
- `An_embedding_call_under_way_is_cancelled_by_the_mark`.

**Task 16**, `tests/Kumbu.Server.Tests/Accounts/RekeyingTests.cs`, the brief's tests with these changes:
- `A_recovery_answers_at_once_and_the_runner_rekeys_every_vault`;
- `A_recovery_interrupted_between_vaults_resumes_after_sign_in`: sign-in answers without waiting, and the runner completes the rest and clears `rekeyPending`;
- `Reencrypt_answers_the_recovery_key_after_stage_1`;
- `Reencrypt_revokes_every_api_key_and_connection_before_answering`: also a key of a vault that no runner has reached yet;
- `Rekeying_one_vault_waits_and_needs_the_password_first`;
- `Access_lists_what_will_lose_access`.

`Settings.test.tsx`: the brief's two tests, plus `the re-encrypt section says to change a leaked password` and `a pending vault shows as being re-encrypted`.

## 8. Corrections to the rulings and briefs

1. **Ruling 20, spec §10, Task 15 brief: "one MongoDB transaction" per vault becomes batches with a two-key period** (§3). A transaction can rewrite about 8–10 % of the WiredTiger cache. A 2000-note vault (40–80 MB) exceeds that on a 256 MB cache, and the failure is a transient error that the driver retries until it gives up. Confirmed by the coordinator. **Task 15's commit subject must not say "in one transaction".** Use "Re-key a vault in batches, revoking the access that can't be re-wrapped" (15a) and "Keep old vault keys out of new slots and hand the index over" (15b).
2. **Task 15 is split into 15a and 15b** (§9). Besides the brief's files, the split touches the following.
   - Files:
     - `Vaults/Documents.cs`: the `user-next` kind and `VaultDocument.RekeyStartedAt`;
     - `Accounts/UserDocuments.cs`: `VaultsRekeyed`;
     - `Vaults/UnlockedVault.cs`: the shared locks, `Pending`, and no `Retired` flag;
     - `Vaults/EncryptedNoteStore.cs`: the lock check, the fences and pending vaults;
     - `Search/SearchIndex.cs`: the lock check, `InterruptToken` and the hand-over;
     - `Vaults/VaultCatalog.cs`: `SetEmbeddingProviderAsync`, `LoadAsync`, `OpenForUserAsync`, `UserVaultKeysAsync`, `DeleteVaultsAsync` (`InterruptToken`), the index on `user-next`, and `CurrentKeyId` and `Current`;
     - `Accounts/ApiKeys.cs` and `OAuth/OAuthTokens.cs`: the checks in their transactions;
     - `Vaults/Credentials.cs`: `VaultUnavailableException` unsealed, and the new `VaultBusyException`;
     - `Api/ApiErrors.cs`: 503 with `Retry-After: 5` for `VaultBusyException` only; `Api/HistoryApi.cs:160, 186` exclude it from their 409;
     - `Api/NoteApi.cs`: the pending `VaultView` (§3, "Accessors");
     - the new `Vaults/RekeyRunner.cs`, registered in `Program.cs`.
   - Error texts, exactly:
     - `VaultBusyException`: "This vault is being re-encrypted; try again in a moment.";
     - `VaultBusyException`: "This vault was just re-encrypted; try again.";
     - `RecoveryConflictException`: "Something changed during the recovery; try again." (409);
     - `RotationConflictException`: "Something changed while re-encrypting; try again." (`RotateAsync`'s third conflict, 409).
3. **Task 15 brief, "mark the old `UnlockedVault` retired": replaced by the per-vault lock held for the whole run, the key-id check and the database fences** (§3). A stale writer could otherwise destroy a note after the run. `AtRevision` filters only on `{_id, rev}`, which resealing keeps, so a stale replace would leave a K′ pid with a K blob. A stale cached key returns `null` at `VaultCatalog.cs:778` and does not throw. A stale key whose meta doesn't open throws a `CryptographicException` at `:784`. For a re-keyed id, both answer "This vault was just re-encrypted; try again."
4. **Task 14 interface** (§9): `PreparedRotation` carries:
   - the password and recovery slots, and `RecoveryUsed`;
   - the kept session slot;
   - the expected ids of the vault slots, API keys and connections;
   - the skipped and orphan slot ids;
   - the vault ids for `rekeyPending`.

   `ApplyAsync` throws `RotationConflictException`. `RotateAsync` does **not** stamp before preparing, and it retries a conflict. The kept session's `createdAt` (in `user_slots`) is set just after the stamp. Apply's API key writes are conditional on the old `nameId`. Orphan slots are deleted if old and retried if young. A slot with a damaged meta loses its `nameId`. Task 14 also adds `UserDocument.RekeyPending`, since Apply writes it; Task 16's brief had it.
5. **Task 14 files:** `CreateForUserAsync(name, userId, userKey, ct, DateTime? credentialReadAt = null)`. The production caller `Api/NoteApi.cs:178` passes the session's read time. Test callers compile unchanged: `tests/Kumbu.Server.Tests/Mcp/McpClientTests.cs:409–410`, `Vaults/UserVaultTests.cs` (lines 21–164), and `AccountApiTests`, `CallerTests`, `SeederTests`, `ApiKeyTests`, `DeletionTests`, `ConnectionTests`, `UserCatalogTests`, `AdminTests` and `EncryptedNoteStoreTests`. Without the check, a vault created with the old user key during a rotation would be lost.
6. **Task 16, recovery** is wired as §2 states. Stage 2 runs in the background: the recovery records `rekeyPending` and answers with the new recovery key at once. `UserCatalog` gains `RecoverWithKeyAsync`, which returns the user and U′ for that; `RecoverAsync` stays as a wrapper, so existing tests are unchanged.
7. **Task 16, Settings:** stage 1, then the revocation of every API key and connection token of the user (`rekeyed`, coordinator's ruling), then the answer, then the runner in the background (§4). The first version of this document said stage 2 first. Per vault, the request waits for the runner, which serves one vault at a time process-wide.
8. **Task 16, sign-in:** `ResumePending` runs *after* the session started and is not awaited. The brief said "the answer waits", and the first version of this document said "before the session starts". Sign-in must never be blocked (coordinator's ruling). `ResumePendingAsync` becomes `void ResumePending`. A request that meets a pending vault starts the runner too.
9. **Brief line numbers** refer to an older commit:
   - `VaultCatalog.cs:112, 207, 618` are now `:116, 211, 784`;
   - `DeleteVaultsAsync` is now `:467–557`;
   - `UserCatalog.cs:234–290` is now `:235–302`;
   - `UnlockedVault.cs:90–202` is unchanged.

## 9. Implementation split

Each task gets this document and its brief. Where they differ, this document wins. Interfaces are given exactly. "Internal" members are visible to the tests (`InternalsVisibleTo`). The order is 14, 15a, 15b, 16.

### Task 14 — stage 1 (`Rotate a user's key with every slot and keyed hash that hangs on it`)

```csharp
public sealed record KeptSession(ObjectId SlotId, byte[] OldWrapped, byte[] NewWrapped);
public sealed record SlotRewrap(ObjectId Id, byte[] OldWrapped, byte[] NewWrapped, byte[]? NewNameId);   // NewNameId null: unset
public sealed record MetaReseal(string Id, byte[] OldMeta, byte[]? OldNameId, byte[] NewMeta, byte[]? NewNameId);
public sealed record PreparedRotation(
    string UserId, byte[] NewUserKey, string RecoveryKey,
    UserSlotDocument PasswordSlot, UserSlotDocument RecoverySlot, byte[]? RecoveryUsed,
    KeptSession? KeptSession,
    byte[] OldLookup, byte[] NewLookup, byte[] UserKeyId,
    IReadOnlyList<SlotRewrap> VaultSlots, IReadOnlyList<ObjectId> SkippedSlotIds, IReadOnlyList<ObjectId> OrphanSlotIds,
    IReadOnlyList<MetaReseal> ApiKeys, IReadOnlyList<MetaReseal> Connections,
    IReadOnlyList<string> VaultIds);
public sealed class RotationConflictException() : Exception("Something changed while re-encrypting; try again.");
public sealed class RecoveryConflictException() : Exception("Something changed during the recovery; try again.");  // 409, in ApiErrors
public sealed class UserKeyRotation(IMongoDatabase db, Argon2Kdf kdf, TimeProvider time, ILogger<UserKeyRotation> log)
{
    public Task<PreparedRotation> PrepareAsync(User user, byte[] oldUserKey, string password, string? keepSessionSecret, byte[]? recoveryUsed, CancellationToken ct);
    public Task ApplyAsync(IClientSessionHandle session, PreparedRotation rotation, CancellationToken ct);   // throws RotationConflictException
    public Task<PreparedRotation?> RotateAsync(User user, byte[] oldUserKey, string password, string? keepSessionSecret, CancellationToken ct); // null: wrong password
    internal Func<string, Task>? FaultAt { get; set; }   // "before-apply", "inside-apply"
}
```

Plus:
- `UserDocument.RekeyPending` (`[BsonElement("rekeyPending"), BsonIgnoreIfNull] List<string>?`);
- `UserCatalog.RecoverWithKeyAsync(username, recoveryKey, newPassword, ct)`, returning `(string RecoveryKey, User User, byte[] UserKey)?`, with `RecoverAsync` as a wrapper. `RecoverWithKeyAsync` throws `RecoveryConflictException` (409) on a third conflict;
- `VaultCatalog.CreateForUserAsync(…, DateTime? credentialReadAt = null)`.

Task 14 comes before 15a, which defines `SlotDocument.UserNext`. So Task 14 matches vault slots **by lookup, whatever their kind**, and wraps each one under the AAD of its own kind, `slot:{vaultId}:{slot.Kind}`. Only a `user` slot gets a name hash. A `user-next` slot, once 15a creates them, is then covered without a change to Task 14; its test is added in 15a.

Helpers stay internal to their classes and are called only by `UserKeyRotation`: `VaultCatalog.PrepareUserSlotsAsync`, `ApiKeys.PrepareResealAsync`, `Connections.PrepareResealAsync` and `Sessions.PrepareKeep`. The constructor drops the brief's `VaultCatalog` parameter and works on the collections directly, as `ConnectionRevocation` does, to avoid a dependency cycle. If the cycle doesn't arise, keeping the parameter is fine.

### Task 15a — the run (`Re-key a vault in batches, revoking the access that can't be re-wrapped`)

```csharp
public sealed record RekeySummary(long Documents, long Damaged, IReadOnlyList<string> RevokedApiKeys, IReadOnlyList<string> DisconnectedConnections);
public sealed class VaultLocks { public SemaphoreSlim Gate { get; } = new(1, 1); public SemaphoreSlim IndexGate { get; } = new(1, 1); }
public sealed class VaultBusyException(string message) : VaultUnavailableException(message);  // only the two texts of §8.2; 503 + Retry-After: 5
// UnlockedVault(string id, string name, ReadOnlySpan<byte> vaultKey, DateTime now, VaultLocks locks); required; Gate/IndexGate come from locks.
// The one test that constructs it directly (tests/Kumbu.Server.Tests/Search/SearchIndexTests.cs:198) passes new VaultLocks().
// UnlockedVault.Pending { get; init; }   (placeholder of a vault in its two-key period; no working set)
partial class VaultCatalog
{
    internal Task<RekeySummary> RekeyAsync(string vaultId, string userId, byte[] userKey, CancellationToken ct); // starts or resumes
    internal Task<(IReadOnlyList<string> ApiKeyIds, IReadOnlyList<string> ConnectionIds)> AccessAsync(string vaultId, CancellationToken ct);
    internal VaultLocks LocksFor(string vaultId);         // one per id, for the life of the process; every UnlockedVault of the id gets it
    internal bool IsRekeying(string vaultId);
    internal Task<IReadOnlyList<string>> PendingVaultIdsAsync(string userId, byte[] userKey, CancellationToken ct); // the user's vaults (by user slot) whose document has rekeyStartedAt; for ResumePending
    internal void CheckWritable(UnlockedVault vault);      // !Pending && !Deleted && !IsDeleted && !IsRekeying && current key id; throws VaultDeletedException / VaultBusyException
    internal bool MayIndex(UnlockedVault vault);           // the same condition, as a bool
    internal CancellationToken InterruptToken(string vaultId);   // replaces DeletionToken
    public event Action<string, string, byte[]>? RekeyPendingFound;   // vaultId, userId, userKey
    internal Func<string, Task>? RekeyFaultAt { get; set; }    // "after-start", "after-batch", "inside-finish", "after-finish"
    internal int BatchDocuments { get; set; } = 500;
    internal long BatchBytes { get; set; } = 4 * 1024 * 1024;
}
public sealed class RekeyRunner(VaultCatalog catalog, IHostApplicationLifetime life, TimeProvider time, ILogger<RekeyRunner> log)
{
    public Task<RekeySummary> RunAsync(string vaultId, string userId, byte[] userKey);  // as Start, but ends a backoff; awaits the next attempt and rethrows its failure
    public void Start(string vaultId, string userId, byte[] userKey);                  // fire and forget; swaps in the newer key if queued or backing off; no-op if running
    public bool IsRunning(string vaultId);
}
```

`RekeyRunner`, in three sentences:
- **Global concurrency:** a single worker takes vaults one at a time from one process-wide FIFO queue, so batches of two runs never share the cache.
- **Backoff with the key held:** after the n-th consecutive failure an entry keeps its user key and waits min(2ⁿ⁻¹ min, 1 h) before going back to the queue's tail.
- **Start during backoff:** `Start` on a queued or backing-off entry only swaps in the newer key and never resets the backoff, while `RunAsync` also moves a backing-off entry to the queue at once.

§4 has the full rules.

Plus:
- `SlotDocument.UserNext = "user-next"`;
- `VaultDocument.RekeyStartedAt` (`[BsonElement("rekeyStartedAt"), BsonIgnoreIfNull] DateTime?`);
- `UserDocument.VaultsRekeyed` (`[BsonElement("vaultsRekeyed")] long`);
- the unique partial index on `slots.vault` for `kind == "user-next"`;
- `AtRevision(id, revision, pathId)` and the fenced deletes;
- 503 with `Retry-After: 5` for `VaultBusyException` in `ApiErrors`, and `HistoryApi`'s two catches excluding it;
- the placeholder accessors of §3, "Accessors" (`EncryptedNoteStore`), and `GET /api/vaults` not counting a placeholder's notes;
- `PendingVaultIdsAsync`, built here and used by Task 16's `ResumePending`.

The runner subscribes to `RekeyPendingFound`.

### Task 15b — escape checks and the index (`Keep old vault keys out of new slots and hand the index over`)

```csharp
partial class VaultCatalog
{
    // Inside the caller's transaction: every vault without rekeyStartedAt, and its user slot opening to the same key.
    internal Task RequireSameVaultKeysAsync(IClientSessionHandle session, string userId, byte[] userKey, IReadOnlyDictionary<string, byte[]> wrapped, CancellationToken ct);
    internal UnlockedVault? Current(string vaultId);
}
// ApiKeySlotsAsync and ConnectionSlotsFromUserAsync return (IReadOnlyList<SlotDocument> Slots, IReadOnlyDictionary<string, byte[]> VaultKeys).
```

Plus:
- `UserVaultKeysAsync` refuses a vault with `rekeyStartedAt`;
- `ApiKeys.CreateAsync` and `OAuthTokens.IssueCodeAsync` call `RequireSameVaultKeysAsync` in their transactions;
- `LoadAsync` answers "This vault was just re-encrypted; try again." for a stale key of an id in `_rekeyed`, at `:778` and at `:784`;
- `SearchIndex.IndexAsync` hands the path over to `Current(id)`.

### Task 16 — triggers (`Re-encrypt after a recovery, on request and per vault`)

```csharp
public sealed class Rekeying(UserCatalog users, UserKeyRotation rotation, VaultCatalog catalog, ApiKeys keys, Connections connections, RekeyRunner runner, IMongoDatabase db, ILogger<Rekeying> log)
{
    public Task<string?> ReencryptAccountAsync(User user, byte[] userKey, string password, string sessionSecret, CancellationToken ct); // null: wrong password; stage 1, then keys.RevokeAllAsync and connections.RevokeAllTokensAsync(rekeyed), then runner.Start per vault; returns the recovery key
    public Task<RekeySummary?> RekeyVaultAsync(User user, byte[] userKey, string vaultName, string password, CancellationToken ct);   // null: no such vault; ArgumentException: wrong password, checked first
    public void ResumePending(User user, byte[] userKey);  // runner.Start for each id in rekeyPending and in catalog.PendingVaultIdsAsync; never waits
}
```

REST (session-only, under the password-check limit as at `AccountApi.cs:194–218`):
- `POST /api/account/reencrypt`: `ReencryptRequest(string Password)` → `RecoveryKeyView`;
- `GET /api/vaults/{vault}/access` → `VaultAccessView(string[] ApiKeys, string[] Connections)`, by name;
- `POST /api/vaults/{vault}/rekey`: `RekeyVaultRequest(string Password)` → `RekeyVaultView(int RevokedApiKeys, int DisconnectedApps)`.

The vault list gains `pending`. `AccountApi.RecoverAsync` uses `RecoverWithKeyAsync` and calls `ResumePending`. `SignInAsync` calls `ResumePending` after `StartAsync`. Web: the Settings section with the text of §4, and the per-vault dialog. Regenerate `web/src/api/schema.d.ts`.
