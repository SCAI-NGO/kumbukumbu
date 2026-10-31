# Vault sharing ("collab") — Design

Status: **design**, 2026-10-31. Requirements: `concepts/collab-concept.md` (decisions of 2026-10-28 and 2026-10-31, §10 and §14). This document says how the concept is built against the code as it stands on `master` (`9441243`). Where it and the concept differ, this document wins and says so. Next: an independent security review of this design (a fresh Claude Opus 5.5 session at maximum effort that sees only this document and `docs/security-architecture.md`), then the plan. Release: **0.3.0**, built on a long-lived `v0.3.0` branch (§12).

Paths are under `src/Kumbu.Server/` unless they start with `Kumbu.Core/`, `web/` or `kumbu-obsidian/` (the plugin's own repository).

## 1. Intent

A vault owner shares a vault with people she trusts, read-only, read-write or with `maintain`. Their agents work on it as on their own vaults. She can change their role or withdraw access at any moment, with effect on the next request. Kumbu² stays a personal second brain that a few people can share a room of; it does not become a team workspace.

**Goals:** the concept's §1–§9a, §13 as decided.

**Non-goals for 0.3.0:** owner transfer (§10.3 of the concept), re-sharing by members, messages between users (v6/v7, perhaps earlier), quotas, renaming one's own vaults.

**Anti-goals:** live co-typing (shared cursors, merged keystrokes, CRDT/OT): turned down outright (concept §9a). Sham rules: a right that one route enforces and another doesn't (concept §7). A member being able to tell other members apart (concept §8).

## 2. The model in the database

### 2.1 Memberships

New collection `memberships`, one document per (vault, member). Plain fields, because the server must read them per request without a key:

| Field | Type | Notes |
|---|---|---|
| `_id` | string (random id) | the `membershipId` |
| `vault` | vault id | |
| `owner` | user id | denormalised from the vault's owner slot, for the owner's listings and account deletion |
| `member` | user id, null while `pending` without redemption | |
| `role` | `read` \| `write` \| `maintain` | |
| `status` | `pending` \| `active` \| `revoked` \| `left` | |
| `confirm` | bool | owner confirmation requested (concept §4.3 B) |
| `label` | sealed under the vault key | the owner's handle ("Bob"); sealed because it is user input that names a person |
| `inviteLookup` | binary, only while `pending` | keyed hash of the invite secret (§4.1) |
| `expiresAt` | date, only while `pending` | |
| `createdAt`, `acceptedAt`, `revokedAt`, `roleChangedAt` | dates | audit |
| `redeemedAt` | date | set at redemption; with `confirm`, the owner then has 7 days from it to confirm or decline |
| `writers` | sealed under the vault key | map credential id → display name of the member's credentials on this vault ("Claude", "Obsidian Laptop"), written when a derived slot is created (§3.4); the source of "Bob · Claude" (§8.2) |
| `revision` | int | sentinel for transactions that derive slots (§3.4) |

Indexes: unique `(vault, member)` where `member` exists; `(member, status)`; `(vault, status)`; unique `inviteLookup` where it exists. Limits from the concept §10.6: at most 20 non-revoked memberships per vault (`KUMBU_VAULT_MEMBER_LIMIT`, default 20), at most 10 `pending` per vault.

Statuses only move forward: `pending → active → revoked | left`, `pending → revoked`. A revoked or left membership is kept as a **tombstone** (with `label` and `writers`), so the owner keeps seeing "Bob · Claude" in the history of a member who left. Tombstones are deleted with the vault, and **deleted outright when the member's account is deleted** (§7.2): then nothing that names Bob remains in the vault, and his writes resolve to "a former member". A redeemed but unconfirmed membership counts towards the 10 pending invitations; if the owner neither confirms nor declines within 7 days of `redeemedAt`, it becomes `revoked` and its member slot is deleted.

### 2.2 Slot kinds

`SlotDocument` (`Vaults/Documents.cs:26`) gets two kinds. **Neither reuses `user` or the `Owner` field**: `Deletion.OwnedVaultsAsync` (`Accounts/Deletion.cs:219`), `MarkOwnersAsync` (`Vaults/VaultCatalog.cs:670`), `Admin.cs:32` and `CountLockedAsync` treat every `user` slot with `Owner` as ownership, so a member slot of kind `user` would let a member's account deletion delete the owner's vault.

| Kind | Wrapped under | Carries |
|---|---|---|
| `member` | the member's **public key** (§5.2) | `Membership`, `Lookup` = the member's user-slot lookup, `NameId` = MAC of the member's **alias** (§2.3), `Alias` sealed under a key from the member's user key |
| `invite` | `HKDF-SHA256(invite secret, "kumbu/v1/invite-slot", salt=vaultId)`, AAD `slot:{vault}:invite:{membershipId}` | `Membership`, `ExpiresAt` (TTL index, §2.4) |

Derived slots (kinds `key` and `connection`) of a member get a new field `GrantedVia: membershipId`. Slots without it are the owner's.

### 2.3 Aliases share the vault-name index

The existing unique partial index `(Lookup, NameId)` (`VaultCatalog.cs:1266`) is kind-agnostic: it applies to every slot whose `nameId` is binary. A member slot carries the member's own `Lookup` and the MAC of the alias under the member's own name-id key (`HKDF(userKey, "kumbu/v1/vault-name-id")`, as at `:264`). So **one index arbitrates own vault names and aliases in both directions**, without a second collection, and MongoDB never sees a name. Creating one's own vault with an alias's name fails with the existing `VaultNameTakenException`; accepting with a taken alias fails the same way and the dialog asks for another name.

The alias is sealed in the member slot under `HKDF(userKey, "kumbu/v1/alias", salt=vaultId)`, AAD `alias:{vault}:{membershipId}`. The owner can't read it; it is the member's.

**Renaming an alias** (decided here, not in the concept): one update of `NameId` and `Alias` on the member's slot, in a transaction; the index refuses a taken name. Own vaults still have no rename (none exists today), so the concept's "like one of his own vaults" is corrected in the concept to "Bob can rename the alias later".

**Member's user-key rotation** (`UserKeyRotation`, `PrepareUserSlotsAsync` at `VaultCatalog.cs:1310`): today it rewraps every slot under the old lookup regardless of kind. For `member` slots it must recompute `Lookup`, `NameId` and reseal `Alias` under the new user key, and **leave `Wrapped` alone** (it is under the public key, which doesn't change). `ApplyVaultSlotsAsync`' check that no slot under the old lookup remains (`UserKeyRotation.cs:243-266`) then covers member slots as well.

### 2.4 Indexes and TTL

`invite` slots carry `ExpiresAt` and fall under the existing TTL index (`VaultCatalog.cs:1255`); the code also checks expiry (as for connection slots, `:453`). A sweep (with `VaultSweeper`'s pattern) sets expired `pending` memberships to `revoked` and removes stray `invite` slots; it is idempotent.

## 3. Authorisation per request

### 3.1 Where the check sits

There is no central authoriser; every request opens the slots its credential has, and only then loads the vault (`LoadAsync`, `VaultCatalog.cs:1014`, whose cache hit compares only the key id). The membership check goes **into the opening methods, before `LoadAsync`**:

- `OpenForUserAsync` (`:195`, sessions): finds slots by `Lookup`; today only kind `user`. It now also takes kind `member`, and for those reads the memberships in one query (`_id in [...]`, `status == active`). A member slot whose membership isn't active is skipped as if absent.
- `OpenForApiKeyAsync` (`:515`) and `OpenConnectionSlotsAsync` (`:444`): for every slot with `GrantedVia`, the same batched query. Not active ⇒ skipped.
- The dev token (`OpenAsync`, `:129`) never reaches shared vaults: dev-token vaults can't be shared (they have no owner).

This keeps the existing guarantee that the in-memory cache never replaces authorisation (comments at `:127-128`, `:139`): a vault already unlocked by Bob a minute ago isn't reachable on his next request once the membership is revoked, and a live OAuth token opens nothing for it (`RequestCaller.ForConnectionAsync` refuses a token with no vaults, `Auth/RequestCaller.cs:90`). The query is one indexed `find` per request that touches shared vaults; requests of owners without shared vaults pay nothing (no `member` or `GrantedVia` slots).

### 3.2 The vault a credential sees: a per-caller view

`UnlockedVault` is shared by all callers (`VaultCatalog._open`), and its `Name` comes from the owner's meta (`:1033`). An alias therefore can't live on `UnlockedVault`. The opening methods return, per vault, an **access record**: `(UnlockedVault, name as this caller sees it, level, membership or null, isOwner)`. `EncryptedNoteStore.VaultsAsync` (`Vaults/EncryptedNoteStore.cs:886`) builds the namespace from the caller's names, not from `UnlockedVault.Name`.

- Its `GroupBy(Name).First()` silently hides one of two same-named vaults today. With aliases arbitrated by the index (§2.3) a collision can't arise; the grouping is replaced by a check that **throws** on a duplicate (a broken invariant must be loud, not hide a vault).
- Rights lookups (`CanWrite(string vault)`, `Vaults/Credentials.cs:63,84`) are keyed by vault id internally; the name is resolved once.
- Everything downstream that carries a vault name (sync hints, `TicketGrant`, `AccountApi.VaultNamesAsync`, exports, activity) uses the caller's name.

### 3.3 Levels

`KeyRight` (`Accounts/ApiKeys.cs:13`) becomes ordered `Read < Write < Maintain` (text `read`, `write`, `maintain`). The **effective level** of a request on a vault is:

| Caller | Level |
|---|---|
| owner's session, dev token | `Owner` (above `Maintain`) |
| member's session | membership `role` |
| owner's key, device or connection | its sealed level |
| member's key, device or connection | `min(membership role, sealed level)` |

`ICaller` (`Kumbu.Core/Notes/ICaller.cs:16`) gets `Level(vault)` in place of `CanWrite(vault)`; `CanWrite` stays as `Level >= Write` for the many call sites. `ICaller.Refuse(command, path)` is the single choke point (called from `NoteWriter`, `NoteMover`, `Replacer`, `Checkpoints`, `AttachmentWriter`, `BoardService` and the REST endpoints via `NoteApi.Refuse`, `NoteApi.cs:668`) and grows a **structural** class: it refuses below `Maintain` when

- the path is a structural file (`BoardPaths.Definition`, `/Board/Board.md`, `Kumbu.Core/Board/BoardModel.cs:26`; later the OKF manifests), by any command (`write`, `edit`, `replace`, `mv` to or from it, `rm`, `restore` of it, upload);
- the command is `mkcp`, `rmcp`, `board create`, `board rename_column`. `BoardService` passes one `Tool = "board"` for every operation today (`Kumbu.Core/Board/BoardService.cs:34`, calls at `:45, 95, 151, 191, 256`); its call sites pass the operation (`board create`, `board rename_column`, `board add`, …) so `Refuse` can tell structure from cards;
- a `mv` or `rm` of a folder that contains a structural file (e.g. `rm /Board`, `mv /Board /Old`);
- a `restore --checkpoint` whose checkpoint holds a structural file that differs from the current one (or the current one is absent, or the checkpoint lacks it): the whole restore is refused, not partly applied, since a board whose columns came back without its cards (or the other way round) would be worse than no restore. The sentence says who can do it: "This checkpoint changes the board's structure; only those who may maintain *Gartenarbeit* can restore it." A checkpoint whose structural files equal the current ones restores with `write`.

The server's list of structural paths is one static place (`StructuralFiles`), so v4's manifests are one line.

**Existing write keys become `maintain`** (concept §7, point 7 of the open points). Slot payloads are sealed per credential (`ApiKeySlotPayload`, `VaultCatalog.cs:1155`) and can't be rewritten without the credential. So the payload gets a version: `{vaultKey, rights, v: 2}` for everything written from 0.3.0 on; a payload without `v` and with `write` reads as `Maintain`. No migration; the owner's display copy in the key meta (`ApiKeys.cs:468`) is read the same way. New keys and connections suggest `write`.

**OAuth scope** (`OAuth/OAuthTokens.cs:655`) stays binary (`read` / `read write`): the scope caps a connection to `read` or lets it be what the user chose per vault; `maintain` is chosen per vault in Kumbu²'s consent and rights dialogs, never by the client.

**Members may set any level on their own credentials** (concept §7.1): the key, connection and device dialogs offer levels up to `maintain` for a shared vault regardless of the member's role; the effective level is computed per request. `UserVaultKeysAsync` (`VaultCatalog.cs:358`), which today only unwraps through the caller's `user` slots, gets a second path through active `member` slots (private key, §5.2), and the derived slot gets `GrantedVia`.

**Owner-only actions** (share, revoke, change roles, choose the embedding provider, re-encrypt, export, delete the vault, see the access list): today these rest on `FindForUserAsync`, which looks up the owner slot by name id and so stays owner-only, *except* where an endpoint resolves the vault through the open store: export (`Api/ExportApi.cs:134`) and the embedding provider (`NoteApi.cs:200-218`). Both get an explicit `isOwner` check from the access record. Export also allows a member during the closing period (§7.3).

### 3.4 Creating derived slots against revocation

A member's key or connection slot for a shared vault is created in a transaction that also writes the membership document (`revision++`, filter `status == active`), as the connection's `Wraps` sentinel does (`OAuthTokens.cs:550`); the same write adds the credential's display name to the membership's sealed `writers` (§2.1). The server has the vault key at that moment, since it just wrapped it. A revocation that commits first makes the creation fail; one that commits after it finds the new slot in its cleanup (§6.2). Either way no slot outlives an active membership as access (§3.1 would refuse it anyway).

### 3.5 Refusals, machine-readable

Every refusal from `Refuse` (REST 403) carries a `code`: `read-only` (level below `Write`), `structural` (below `Maintain`), `closing` (§7.3), `owner-inactive` (§7.4). Owner-only endpoints keep answering a member with 404, as today (`FindForUserAsync` filters on kind `user`, `VaultCatalog.cs:334`); the web app doesn't offer those actions to members, so no code is needed for them. The text stays as today ("this key can only read vault X", and a new "Only those who may maintain *Gartenarbeit* can change its board's columns."). The web app and the plugin branch on the code (§9.3).

## 4. Invitation

### 4.1 Creating

`POST /api/vaults/{vault}/invitations` (session-only, owner-only, password in the body and the password window: the pattern of vault deletion, `NoteApi.cs:233-266`, and `Security.RequirePasswordWindow`, whose comment already names invitations, `Api/Security.cs:180`). Body: label, role, expiry in days (1–30, default 7), `confirm`.

In one transaction: the membership (`pending`, `inviteLookup = HMAC(HKDF(ServerSecret, "kumbu/v1/vault-invite"), secret)`, as `Invitations.cs:270`), the `invite` slot, the per-vault limits checked inside the transaction. The response holds the link `…/share#<secret>` (32 random bytes, Base64Url, in the fragment) once; the server keeps neither secret nor link.

### 4.2 Accepting

The page `/share` reads the fragment and calls `POST /api/invitations/peek` (session-only): the vault's name, the owner's display name, the role, the embedding provider, and whether the alias is free. The server needs the vault's name for that, which is sealed: peek **unwraps the invite slot** with the secret to read the meta, and then forgets the key. A wrong or used secret answers a uniform 404 with its own rate limit (`PairingApi.cs:48-51` pattern).

Without a session, the page says what the concept §4.3 says (an account first; tell the sender) and offers sign-in; the fragment survives sign-in (the existing `/join` handling is the reference).

`POST /api/invitations/accept` (session-only), body: secret, alias. In one transaction: claim the membership atomically (`FindOneAndUpdate` on `inviteLookup`, `status == pending`, `expiresAt > now`, the pattern of `Invitations.cs:142-146`), ensure the member's keypair (§5.1), create the `member` slot (alias name id against the index; a taken alias aborts the transaction and the claim with it, so the link stays usable), delete the `invite` slot, set `member`, `redeemedAt` and the status: `active` (with `acceptedAt`), or with `confirm` it stays `pending` with `member` set until the owner confirms or declines (`POST …/memberships/{id}/confirm` or `…/decline`, owner-only). The owner's confirmation shows who redeemed: "Bob Example (username `bob`) accepted *Bob*'s invitation — grant access?", from the redeemer's user document; it is the one place the owner sees the member's username, and it is what makes option B worth anything. Bob can't accept an invitation to his own vault, nor a second membership of the same vault (unique index).

The Obsidian pairing (`Accounts/Pairings.cs`) is not a model here after all: it keeps a copy of the user key in memory for 10 minutes and never persists a slot. The account invitation (`Invitations.cs`) and the connection slot (`WrapConnectionSlots`, `VaultCatalog.cs:472`) are.

### 4.3 Withdrawing

The owner withdraws a pending invitation (`DELETE …/memberships/{id}`): membership `revoked`, invite slot deleted, in one transaction.

## 5. Keypairs

### 5.1 Creation

Every user gets a keypair: the public key in plain on `UserDocument` (`publicKey`), the private key sealed under the user key (`HKDF(userKey, "kumbu/v1/user-private-key")`, AAD `user-private:{userId}`). Created lazily wherever the user key is at hand and the user has none yet: at sign-in (`UserCatalog.TrySignInAsync`, `:95`), on accepting an invitation (§4.2) and at account creation. An invitation never waits for a keypair: acceptance creates it.

`UserKeyRotation` (password change, recovery, "Re-encrypt my vaults") reseals the private key under the new user key in the same transaction as the other slots. The keypair itself is not rotated in 0.3.0.

### 5.2 Algorithm (a call of this design)

The concept names X25519. .NET has no X25519 in `System.Security.Cryptography`, and the project's only crypto dependency is Konscious (Argon2). This design uses **ECDH over P-256** from `System.Security.Cryptography.ECDiffieHellman` (OpenSSL on Linux) in an HPKE-like construction (as RFC 9180's DHKEM(P-256, HKDF-SHA256) with AES-256-GCM): ephemeral P-256 key, ECDH with the member's public key, `HKDF-SHA256(shared secret, info "kumbu/v1/member-slot", salt = ephemeral public ‖ recipient public)`, AES-256-GCM over the vault key with AAD `slot:{vault}:member:{membershipId}`; the slot stores `ephemeral public ‖ sealed`. No new dependency. The builder may use a vetted X25519 library instead (NSec) if the security review prefers it; the slot format then carries an algorithm byte either way (`1` = P-256).

### 5.3 Opening a member slot

`OpenForUserAsync` unwraps the private key with the session's user key, then the member slot. API keys and connections of members don't open member slots: they have slots of their own (`GrantedVia`), wrapped for them when created (§3.3), so they work while Bob is signed out.

## 6. Revocation, leaving, role changes

### 6.1 The atomic step

`POST …/memberships/{id}/revoke` (owner) and `POST /api/memberships/{id}/leave` (member): one `UpdateOne` on the membership (`status: revoked | left`, `revokedAt`, `revision++`). From that commit every opening method skips the vault for Bob and for each of his credentials (§3.1).

### 6.2 Cleanup

Then, in one transaction, idempotent and retried by the sweep (§2.4): delete the member slot, every slot with `GrantedVia` this membership, and revoke Bob's API keys left with no vault (`RevokeKeysWithoutVaultsAsync`, `VaultCatalog.cs:701`, extended to count `GrantedVia` slots as vaults); remove the vault from Bob's connections (`ConnectionRevocation.RemoveVaultsAsync`). Sync sockets drop the vault within the 30-second check (§9.1).

### 6.3 Role changes

`PUT …/memberships/{id}/role` (owner, session-only; no password: it is reversible and lowers or raises only what she granted): one update (`role`, `roleChangedAt`, `revision++`). Effective on the next request (§3.3); no slot changes. An activity entry for the member (§8.3).

### 6.4 Re-encryption offered after revoke and leave

As the concept §10.5: offered, off by default, with its honest sentence. It runs §7.1.

## 7. Re-encryption, recovery, account lifecycle

### 7.1 Re-keying a shared vault

`StartRekeyAsync` (`VaultCatalog.Rekey.cs:333`) deletes every slot that isn't `user` or `user-next` (`:401`) and revokes every API key and connection token with a slot on the vault, entirely (`:377-398`). For shared vaults:

- `member` slots are **not** deleted at the start; they are rewrapped at the finish. During a run nobody reads the vault, the owner included: it loads as a placeholder and every access answers "being re-encrypted" (`VaultBusyException.Being()`, `Vaults/EncryptedNoteStore.cs:870`, `Vaults/UnlockedVault.cs:63-68`). Members see the same sentence. Accepted: a run takes minutes, as today for the owner.
- **Only the owner's key drives a run.** `OpenForUserAsync` resumes a pending run with the calling session's key (`VaultCatalog.cs:223`, `RekeyRunner.cs:70`); a member's session that meets the placeholder must not start or resume it (it has no owner slot, and must never hand its own user key to the runner). A stalled run resumes when the owner next signs in, as today; until then members see "being re-encrypted".
- `invite` slots are deleted at the start and their memberships set `revoked` (an outstanding link can't be rewrapped; the owner is told which invitations were withdrawn and sends new ones).
- Members' derived credentials go the way the owner's do: revoked entirely, including their slots on the member's other vaults, as today for the owner's own keys. The members are told (§8.3), with the sentence that keys which also reached their other vaults need to be set up again.
- **Finish** (`:413`): in the same transaction as the owner slot update, every `member` slot of an `active` (or confirmed-pending) membership is rewrapped under the member's public key with the new vault key (at most 20, inside one transaction). A member without a keypair can't exist (§5.1 creates it before the slot exists), so phase 4 of the concept's §6.3 problem ("members not yet migrated") disappears.

"Re-encrypt my vaults" and recovery reach shared vaults through the owner's slots (`PendingVaultIdsAsync`, `Rekey.cs:188`) and run the same steps. A member's recovery or password change touches only their keypair's seal and their `member` slots' lookup (§2.3, §5.1); no vault is re-keyed for it.

### 7.2 Member deletes the account, privacy deletion, deactivation

- **Deletion** (`Deletion.DeleteAccountAsync`, `:159`): in its transaction, every membership with `member == user` is cleaned up (§6.2) and then **deleted**, tombstone included (§2.1). The member's writes stay (concept §9). Because members' writes store no name (§8.1), nothing in the vault names them any more, and the receipt's "your name was removed from their history" is literally true; their writes resolve to "a former member". The receipt (`DeletionReceiptView`, `Api/AccountApi.cs:110`) gains counts of memberships ended and of shared vaults closed, and the concept's sentence; no vault names.
- **Deactivation of a member** (`Admin.cs:56-100`): memberships stay; every request is refused already.
- **Deactivation of an owner:** §7.4.

### 7.3 Owner deletes the account: the closing period

The deletion dialog lists the shared vaults with the two choices of the concept §9. "Give members 7 days":

- a plain field `closingAt` on the vault document (beside `rekeyStartedAt`, `Documents.cs:21`), set in the deletion transaction;
- the owner's slot, account and credentials go at once, as today; the vault survives through its `member` slots only; it is excluded from `OwnedVaultsAsync` for that deletion;
- writes are refused on every route with `code: closing` through `CheckWritable` (`VaultCatalog.Rekey.cs:123`), which every write already passes; indexing stops (`MayIndex`);
- members may export (§3.3); the banner and the agents' line (§8.4);
- a sweep deletes the vault with `DeleteVaultsAsync` at `closingAt` (idempotent; a restored backup is cleaned by the deletion list, `DeletionList.cs`, which gains the vault ids).

**Deletion by an admin** (`Deletion.DeleteUserAsync`, `:92`, e.g. for a privacy request): the admin's dialog offers the same two choices, once for all of the user's shared vaults, default "Give members 7 days"; it is set from the request. The choice is recorded with the deletion in `DeletionList`, so `RepeatAsync` (`:120`, the repeat after a restored backup) applies it again: a restored shared vault whose `closingAt` has passed is deleted at once, otherwise it closes at the original `closingAt`.

"Delete it for everyone now" runs today's deletion; members get an activity entry they can still see in their list of memberships ("Alice has deleted the vault *Gartenarbeit*"), kept with the tombstone membership until they dismiss it.

### 7.4 Owner deactivated (decided by Chris, 2026-10-31)

When an admin deactivates an owner (`Admin.SetActiveAsync`, `Accounts/Admin.cs:56-100`), her shared vaults become **read-only for every member, and members may export them**, until she is reactivated. Deactivation is reversible, so its effect on others must be too, and nobody should lose data because someone else was stopped; at the same time nothing new flows into a room whose responsible owner can't oversee it (concept §3.4). Whatever the admin's reason (the owner left, her credentials may be compromised, misuse), this is the one behaviour without a bad outcome; an admin who wants the vaults gone deletes the account (§7.3).

- The check is per request, from the owner's `active` flag: the opening methods (§3.1) already read the membership; they read the owner's `active` with it (one indexed lookup per request that touches shared vaults, or the membership's `owner` joined to `users`). The effective level for every member becomes `Read`; writes are refused with `code: owner-inactive` on every route (the same path as `closing`, `CheckWritable`, `VaultCatalog.Rekey.cs:123`); indexing stops.
- Export is allowed for members while the owner is inactive (§3.3), as during the closing period.
- Members see a banner: "Alice's account is deactivated. This vault is read-only until it is active again. You can export a copy."; an event (§8.3) and the agents' notice (§8.4).
- Reactivation lifts it on the next request; memberships were never touched.

## 8. Names, visibility, notices

### 8.1 Writer references

Writer labels are free strings sealed in payloads (`LastWriter`, `HistoryEntry.WrittenBy`, `DeletedBy`, `MovedBy`, `Checkpoint.CreatedBy`, attachment equivalents; `Kumbu.Core/Notes/StoredNote.cs:66,74`), unique only per account (`Accounts/WriterNames.cs`). Two members' "Claude" are the same string, and nothing says whose. So every place that stores a label also stores a **writer reference**, sealed beside it: `{user: userId, kind: session|key|device|connection|dev|seed, id: credentialId | null}`, taken from `RequestCaller` (`Auth/RequestCaller.cs:46`). Entries without a reference (everything before 0.3.0) are the vault owner's.

**Members' writes store no name**, only the reference: the label stored is empty (`""`), and every display resolves it through §8.2. The owner's own writes keep storing their label as today. So when a member's account is deleted, deleting the membership (§7.2) is enough to remove their name from the vault, without re-sealing a single entry of a vault that is locked at that moment. Changesets (`Changeset.ForAsync`, `StoredNote.cs:41`) are keyed by `(user, Identity(label))`, so two members' "Claude" never share one.

### 8.2 Rendering per viewer, on the server

One service, `WriterLabels` (beside `Api/WriterKinds.cs`, which it absorbs), maps `(viewer, label, reference)` to `(display, kind)`:

| Viewer \ writer | the viewer | the owner | another member | a former member |
|---|---|---|---|---|
| owner | "You", own kinds as today | — | "Bob · Claude" (the membership's `label` and `writers`, also from a tombstone: "Bob (left) · Claude") | "a former member" (no membership any more) |
| member | "You", own kinds | the owner's display name; her agents as "Alice's agent", her devices as "Alice in Obsidian" | **"another user"**, kind `user` or `agent` only, no device names | "a former member" |

It is called at every place that emits a writer: `NoteApi` (history `:478`, earlier versions `:540`, version, `ChangeView` `:643`), `HistoryApi` (trash `:222`, activity `:245`, checkpoints, version), `FileApi` (file state), **and the shell** (`Kumbu.Core/Sh/Commands/History.cs`, `Trash.cs`, `Checkpoints.cs`), because agents read writers through `history`, `diff`, `trash` and `lscp`. Clients never receive a raw label of someone else. The web app's self-detection (`writerOf(...) === username`, `web/src/api/activity.ts:56` and others) moves to the server's `kind: self`.

Activity rows (`Kumbu.Core/Notes/Activity.cs`) of other members keep their own rows (a row per changeset), but row keys become opaque per viewer, so a member sees "another user" rows without anything that groups them by person. That several "another user" rows exist is accepted: it says that changes happened, not by whom.

The agent banner (`ChangesAsync`) keeps showing only the viewer's own agents and devices.

### 8.3 Activity entries for collab

New activity kinds, visible to the owner and, as stated, to the member concerned: invitation created/withdrawn/accepted/confirmed (owner), membership revoked/left (both), role changed (both: "Alice changed your role to *write*"), embedding provider changed (members), re-key finished and credentials revoked (members), closing period started (members). Stored as plain event documents in a new `vault_events` collection (vault, kind, membership, at; the text is built at read time, names resolved through §8.2), deleted with the vault.

### 8.4 One-time notices to agents

A notice ("The owner changed the embedding provider of /gartenarbeit", "This vault will be deleted on 4 November; export a copy to keep its notes", "Your role in /gartenarbeit is now read") is appended once per credential to the next tool result: a `CallToolFilter` beside `RequiredArguments.Filter` (`Mcp/RequiredArguments.cs:24`, registered in `Program.cs:160`) appends a `TextContentBlock`. "Once" is a persisted mark per (credential, event) (`notice_marks`, TTL 30 days), because MCP is stateless (`Program.cs:152`).

### 8.5 Embedding provider changes

The owner's dialog (`NoteApi.cs:200-218`, now owner-only) states the member count and the sentence of the concept §8; members get the event (§8.3), a dismissable banner with "Leave this vault", and the agents' notice (§8.4). Same in both directions.

### 8.6 What members see of the vault

`VaultView` (`NoteApi.cs:25`) gains `owner` (display name, null for own vaults), `level` (in place of `canWrite`, which stays as derived for compatibility) and `shared` (own vault shared with anyone). The member sees no member list. The owner's access list (`GET /api/vaults/{vault}/access`, `Accounts/Rekeying.cs:135`) gains the memberships (label, role, status, accepted, the membership's derived credentials by name, kind and last use). Credential names of members are sealed in the member's key meta, which the owner can't open; the list reads them from the membership's `writers` (§2.1), sealed under the vault key, which the owner holds.

## 9. Live updates and presence

### 9.1 Web sessions on the sync socket

`POST /api/ws-ticket` (`Api/SyncApi.cs:61`) accepts sessions as well as API keys. `TicketGrant` (`Sync/SyncTickets.cs:10`) carries a subject `{kind: key|session, id, user}` instead of a key id; the per-subject cap (`MaxPerKey`, `SyncHub.cs:29`) applies per subject (a person with several tabs: 4, the oldest replaced). The 30-second check (`SyncHub.CheckAsync`, `:112`) validates the subject (key active, or session alive and user active) and **recomputes the subject's vault set** from slots and active memberships, dropping lost vaults and updating levels; a key whose user is inactive or a dead session closes with 4401. Hints stay content-free (`{"type":"changed","vault":<caller's name>}`); the 30-second lag of a revocation on hints is accepted, since they carry nothing.

The web app gets one socket per signed-in tab (a `useSyncSocket` hook in the shell, `web/src/shell/MobileShell.tsx`), invalidating `["tree", v]`, `["vaults"]`, `["trash", v]`, `["checkpoints", v]`, `["changes"]`, history, and the open note's version (`vaultChanged()`, `web/src/api/history.ts:22`, with `notes: false`). This closes kumbukumbu#116. The 5-second version poll of the open note (`useVersionPoll`, `history.ts:56`) stays as the fallback while the socket is down, and stops while it is up.

### 9.2 Presence

Inbound frames become a small protocol: `{"type":"editing","vault":…,"path":…,"on":true|false}`, at most 1 KiB, only from a session subject whose level on the vault is at least `write`; anything else is ignored as today. The hub keeps `(vaultId, path) → set of connections`, cleared on `on:false`, on close and when the vault drops; an entry without a refresh for 60 s expires (the web app refreshes every 30 s while in Write mode). Outbound `{"type":"presence","vault":…,"path":…,"editors":[…]}` goes only to subjects with the vault open, **rendered per receiving connection** by §8.2's rules (the owner sees names; a member sees the owner by name and "another user" for the rest, collapsed into a count: "Bob, Carol and 2 other users"). Presence messages use their own channel per connection with last-state-wins per path, not the drop-oldest hint channel (`SyncHub.cs:161`), so an "off" is never lost to a burst.

### 9.3 The editor

The existing behaviour already matches the concept §9a: `ChangedElsewhere` and the editor's "changed elsewhere" bar (`web/src/note/MarkdownEditor.tsx:194-205`, `ConflictDiff`, `useAutosave` with `offer`, `reload`, `keepMine`). New: the trigger is the socket hint, the writer is named per §8.2 ("Updated by Bob just now", "Bob saved a newer version"), the presence line sits above the note in Write mode, and Write mode is refused for `/Board/Board.md` below `maintain` and for everything below `write` and during `closing`, with the refusal's sentence.

## 10. Clients

### 10.1 Web app

- **Tree and vault list:** "Your vaults" and "Shared with me", grouped by owner (`VaultOverview`, `MobileShell.tsx:56-98`; `VaultSwitcher.tsx`; `TreeDrawer.tsx`).
- **Share dialog** (owner): label, role (with `maintain` in the warning colour and its sentence), expiry, "Ask me to confirm", password; shows the link once with a copy button and the hint to send it through a safe channel; the sentence that members with `write` can delete and that the trash keeps everything for 30 days.
- **Access list** (owner): memberships with role change, confirm, revoke (with the re-encrypt option and its sentence), derived credentials.
- **Accept page** `/share`: vault, owner, role, embedding provider, alias field (prefilled, must change on collision).
- **Member's side:** role, owner, provider, "Leave this vault", banners (§8.5, §7.3), alias rename.
- **Key, connection and device dialogs** (`web/src/routes/NewKeyDialog.tsx`, `Connections.tsx`, `Keys.tsx`, `shell/ConnectObsidianDialog.tsx`): `maintain` as a fourth level in the warning colour; shared vaults listed; a capped credential shown as "write, acts as read while you can only read *Gartenarbeit*".
- **Theme:** a `warning` token in all 17 schemes (`web/src/theme/schemes.ts`, `index.css`), in `textTokens` of `contrast.test.ts`.

### 10.2 Obsidian plugin (`kumbu-obsidian`, its own version, 0.2.0)

Today `expectOk` (`kumbu-obsidian/src/api/client.ts:250`) maps every 403 to `AuthError`, which stops sync as if the key were revoked (`engine.ts:916`). A read-only member's device, or a `write` device editing `Board.md`, would therefore stop syncing. 0.2.0 maps a 403 with a refusal `code` (§3.5) to `RejectedError`: the file is listed under "Not synced" with the server's sentence, and the server's version is downloaded again on the next check (a test for the interplay of `rejected` with a remote change). Devices paired with 0.1.x against an own vault are unaffected: their level reads as `maintain` (§3.3). Pairing a device to a shared vault **requires plugin 0.2.0**: the redeem request (`RedeemPairingRequest(Code, Device)`, `Api/PairingApi.cs:17`) gains `plugin` (its version); the server refuses to pair a shared vault without it ("Update the Kumbu² plugin to 0.2.0 or newer to sync a shared vault."), before the code is consumed. Devices paired to own vaults pair as `maintain` by default (it is the person themselves in Obsidian), so a 0.1.x device on an own vault never meets a refusal it can't read.

### 10.3 MCP

`ls /` lists own vaults and aliases alike; a shared vault's line carries its owner and the caller's effective level (`gartenarbeit (shared by Alice, read)`), so an agent knows before it tries. `help` mentions it. Refusals carry their sentence as today.

## 11. Decided while writing this design

1. **An owner deactivated by an admin:** her shared vaults become read-only for members, with export allowed, until she is reactivated (§7.4; Chris, 2026-10-31).
2. Chris agreed on 2026-10-31 to the calls this design makes on its own: member slot kinds, aliases in the vault-name index, P-256 for keypairs (subject to the security review), the payload version for `maintain`, writer references and members' writes without stored names, the whole-or-nothing checkpoint restore for structural files, owner-only re-key runs, the admin's closing choice, the redeemer shown on confirmation, plugin 0.2.0 for shared vaults and `maintain` for own devices.

## 12. Phases and branch

Code on a long-lived `v0.3.0` branch, a pull request per phase into it, `master` merged in regularly; pre-release tags stay on `master` commits. 0.3.0 needs all phases.

0. **Groundwork, no sharing yet:** levels with `maintain` and the payload version (§3.3), structural files on every route and refusal codes (§3.5), writer references and `WriterLabels` (§8.1–8.2, with only "self" and "owner" in play), the `warning` token. Each is useful alone and testable against today's single-owner world.
1. **Memberships, member slots, per-request authorisation, per-caller view, aliases** (§2, §3), with test seams that create memberships directly. Tests: a revoked membership refuses an already-unlocked vault, a live OAuth token and a live API key on the next request; an alias collision is refused in both directions; a member's account deletion never deletes the owner's vault.
2. **Keypairs** (§5) and member slots under public keys; rotation and recovery of a member's user key.
3. **Invitations, acceptance, withdrawal, revocation, leaving, role changes, the web app's sharing UI and access list, events and notices, the closing period** (§4, §6, §7.2–7.3, §8.3–8.6, §10.1, §10.3).
4. **Re-keying shared vaults** with recovery and "Re-encrypt my vaults" (§7.1).
5. **Live updates and presence** (§9), plus plugin 0.2.0 (§10.2). Phase 5's socket for sessions does not depend on sharing and may come first if #116 is wanted sooner.

The security architecture (`docs/security-architecture.md`) is updated in the phase that builds each part; its §9 "What comes next" loses sharing at the release.
