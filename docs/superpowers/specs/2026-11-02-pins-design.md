# Pins a former member can't forge: the design

**Outcome in brief.** Part of the problem can be solved inside the database and part can't. **Which key a membership's slot is wrapped to**, and **which memberships are genuine**, can be made unforgeable for a former member: a per-membership key that only the owner's user key and that member's own credentials reach (Option A). **Whether a revocation or a departure still holds** can't be decided from MongoDB alone. A consistent restore of older genuine state is indistinguishable from the present unless something changed in between that the attacker can't reproduce. Within one vault-key epoch nothing like that exists. A re-key is durable only for what the owner's own request tells it, or for what a record outside MongoDB tells it (Option B). The same limit leaves A3.5, the whole old-key era put back, open in every database-only design.

**Status: design proposal, not built; NOT attacked independently.** The run's two attackers were both stopped by a safety classifier before they reported anything (see "What was and wasn't tested"). Everything here is the author's own reasoning. No round counts.

Task: [2026-11-02-pins-design-task.md](2026-11-02-pins-design-task.md). Rounds and their records: [../overnighter/2026-11-02-pins-design-rounds/](../overnighter/2026-11-02-pins-design-rounds/). Report: [../overnighter/2026-11-02-pins-design-report.md](../overnighter/2026-11-02-pins-design-report.md).

## 1. The anchor

**Option A:** the owner's user key. Mallory lacks it. It is at hand whenever she invites, grants, revokes, changes a role or starts a re-key, and in the runner during a run (`Vaults/RekeyRunner.cs:49`). For the rows where she is absent (acceptance, a member's rotation), she has handed over a per-membership key beforehand. Durability across time rests on her **request** at the start of a re-key, which is not in the database.

**Option B** adds a second anchor: an append-only ledger on a volume outside MongoDB, written by the server, which the main model's attacker can't write. It doesn't hold against the at-rest variant.

## 2. Why a database-only design can't make a revocation durable

The owner's user key doesn't change at a revocation or a departure. So everything sealed or authenticated under it before the revocation still opens afterwards. Members' keys don't change either, and K is the attacker's. The task allows a consistent snapshot of every collection (§3.2, A3.2). So for any record R that says "Mallory is out", the earlier genuine state without R, or with an earlier R, opens just as well. A counter, list or manifest kept in MongoDB is rolled back with what it guards.

A state that the attacker can't reproduce exists only once a key he lacks came into being after the revocation, and the first such key is K′. So:

- a revocation is durable **from the start of the next re-key**, and only for what that start is told by something the attacker can't write: the owner's request (Option A), or a record outside MongoDB (Option B);
- a departure, made with the owner absent, can't reach her request unless she learns of it through a channel he can't suppress (A8.2). Inside the system there is no such channel (Option A). A server-written ledger is one (Option B);
- A3.5, the whole K era restored after the finish, owner's user slot included, is a rollback across the crossing itself. Today only process memory guards it: `_keyIds`, and a vault never loaded counts as current (`Vaults/VaultCatalog.Rekey.cs:60–71`, `:110–116`). Checked: a restart clears it. Only state outside MongoDB can close it.

This is the reasoned-negative part of the result. **Under §3 and the hard constraints, "re-encrypting cuts off every former member who can write the database" can't be had from MongoDB alone.** What can be had is "re-encrypting cuts off everyone not in the list you confirm", with A3.5 as a residual, or everything with a ledger outside MongoDB in the main model.

## 3. Option A: owner-made membership keys and an owner-chosen roster

Keeps every hard constraint of §5.1. Prices more owner presence (§5.2): a list to confirm at every re-key start of a shared vault. This is the mechanism of [round 1's candidate](../overnighter/2026-11-02-pins-design-rounds/round-1-candidate.md), unchanged. Its full specification, field by field, is there. In short:

- **Membership key M_X**, random, one per membership, made at invitation or grant (owner present). It is stored as `grant` on the membership, sealed under `HKDF(owner's user key, "kumbu/v1/membership-grant", salt=vault)` together with the owner's label and the epoch `kid(K)`. Beside the invitation it is sealed under the link's secret (invite `mkey`), and after acceptance under the member's own user key (member `mkey`).
- **`pin2`**, sealed under a key from M_X: `{membership, vault, member, alg, publicKey}`. Written by the acceptance and by the member's rotation (owner absent; they have M_X) and by a grant. It replaces today's pin sealed under K (`Sharing/Memberships.cs:152–157`).
- **Roster.** A shared vault's re-key starts only from an owner's request carrying `keep`, the memberships she ticked in a list shown by the labels in `grant`. R′ = `{id, M_X}` for each kept one, sealed with K′ in `user-next` (payload v2, bound to `kid(K)`), and from the finish on in the owner's user slot. The finish wraps K′ only to members in R′ whose `pin2` verifies under their M_X. Every other membership loses its member slot and is revoked.
- Rotation-driven re-keys of a shared vault wait for her `keep` at her next page load. Until then the vault stays under K and works for everyone.

### 3.1 Every row of §2.4

| Event | Written | Verified by, key |
|---|---|---|
| Invitation created (owner) | M_X; `grant` under U; invite `mkey` under the link secret | — |
| Invitation accepted (owner absent) | member `mkey` under the redeemer's user key; `pin2` under M_X with the key from her private key (`Memberships.cs` `MemberPublicKeyAsync`); `active` unless `confirm` | M_X from the invite slot, with the link secret |
| Owner confirms | as today | — |
| Grant | M_X, `grant`, member `mkey`, `pin2` | — |
| Member's rotation (owner absent) | `pin2` for the new public key under M_X; member `mkey` under the new user key; slot rewrapped as today (`Accounts/UserKeyRotation.cs:393–433`) | the old `pin2` under M_X must name her old public key |
| Role change, revocation, leaving, cleanup, sweeps, lapse, account deletion | as today; `grant` and `pin2` stay or go with the document | — |
| Re-key start (owner) | R′ from `keep`; `user-next` v2 `{K′, from: kid(K), R′}` | candidates by `grant` (U) and the current roster (U) |
| Batches | as today | — |
| Finish | K′ to `pin2` keys of R′; others revoked; user slot v2 `{K′, R′}` | `pin2` under M from R′ |
| Stalled resume | K′, R′ from `user-next` | `from` = `kid` of the user slot's key |
| Owner's rotation or recovery | `grant`s and v2 payloads resealed; unopenable ones skipped and logged, never blocking | — |
| Member's API key, connection; owner's account closing | as today | — |

### 3.2 §4, row by row (author's analysis, not attacked)

| Attack | Option A | Option B |
|---|---|---|
| A0 replaced pin | Stopped: `pin2` needs M_Bob, which only Alice's U, Bob's user key and Bob's link reach | as A |
| A1.1, A1.2 new row | Stopped: no `grant` under U, so not a candidate, not in R′ | as A |
| A1.3 laundered acceptance | Stopped: the real acceptance writes `pin2`, but the row has no `grant` | as A |
| A1.4 limits | Stopped: the finish handles only R′, at most the member limit | as A |
| A2.1 own tombstone | **Residual** unless it is the one revoked in the same request: revived, it is a ticked candidate, and only the owner's eyes stop it (S1) | Stopped: the ledger says ended |
| A2.2 someone else's | As A2.1, for the person who knows that membership's M_X (the redeemer of that link) | Stopped |
| A2.3 repeat membership | Each membership has its own M_X and `grant`; authorisation is by id. As A2.1 for the earlier one | Stopped |
| A2.4 absence | Stopped: nothing is read from tombstones | Stopped |
| A3.1 replay of his own state | As A2.1 | Stopped |
| A3.2 undone revocation | As A2.1, within the epoch; across a re-key, stopped (an older roster is bound to an older key) | Stopped in the main model |
| A3.3 undone rotation | **Residual, Medium:** an older `pin2` verifies; Bob is locked out; rotation ends nothing against a holder of the old private key with database write | Stopped if the ledger records the pinned key |
| A3.4 transient | Stopped for R′ (sealed in `user-next`); for pins, as A3.3 | as A |
| A3.5 whole K era | **Residual, the main one:** undetectable from MongoDB | Stopped in the main model (the ledger names the current key id), not in the at-rest variant |
| A3.6 genuine restore | Nothing detects rollback, so nothing new | A database restored without its ledger refuses to load the vaults the ledger has newer; the operator restores both together, or deliberately sets a configuration switch to trust the database. The switch is in the configuration, out of the attacker's reach |
| A4.1 stripped authenticator | Stopped after the vault's first re-key under this design: its user slot has a roster, and a stripped row is no candidate. Before it, every row is legacy anyway | as A |
| A4.2 planted before migration | **Residual:** the first re-key shows legacy rows apart with forgeable labels, and blesses what the owner keeps (S3) | as A, unless the ledger starts before the planting |
| A4.3 already inside | **Residual** for the first re-key, as A4.2 | as A |
| A5.1 transplant | Stopped: `grant`, `pin2`, member `mkey` bound by AAD to vault and membership; his own vaults' `grant`s are under his user key | as A |
| A5.2 verifier swap | Stopped: the verifying key is M_X from R′ or `grant`; "the owner" is whoever's user key the run holds, and only her `grant`s open | as A |
| A5.3 shared secrets | Stopped: M_X is never given to other members | as A |
| A6.1 check here, use there | Stopped: R′ is fixed at the start, under U and bound to K′; `pin2` is checked at the finish | as A |
| A6.2 stall | A bad `pin2` costs only that member's slot; forced retries can still hold the vault pending, as today (denial) | as A |
| A6.3 plain run state | Stopped for keys: a swapped `user-next` from an earlier run fails `from`; deleting it stalls (denial) | as A |
| A7.1 forged slot | Stopped: no one treats what a slot opens as proof; `pin2` uses M_X from her own sealed copy | as A |
| A7.2 rotation with owner away | Works: the member's own M_X | as A |
| A7.3 forced damage | Bob loses his slot; the way back is a new invitation, a new M_X under U, trusting nothing from the database | as A |
| A8.1 lying screen | Labels in the list come from `grant` under U; legacy ones (first re-key only) are forgeable and shown apart | as A |
| A8.2 suppressed warnings | Departures reach the owner only through the database: S4 | The ledger is the channel |
| A8.3 trained clicks | The list appears at re-key starts only, which are rare | No list needed |
| A9.1 swapped member field | Stopped: `pin2` names the member, under M_X | as A |
| A9.2 twice | **Residual** within the epoch, as A2.1; across re-keys, stopped | Stopped |
| A10 oracles | The acceptance MACs under an M_X only the link reaches; the rotation under her own; `Resealed` touches no `pin2`; nothing signs what the database supplies | as A |
| A11 laundering | Nothing K-sealed carries authority after the first re-key | as A |

### 3.3 Migration

Nothing runs at the upgrade, and nobody loses access. New invitations and grants get M_X at once. A vault's first re-key under this design shows its legacy memberships apart ("Shared before this version; Kumbu² can't check these"), ticked. Those kept get M_X and `pin2` from their K-sealed pin. From that finish the vault's promise holds against forgeries made afterwards (A4.2, A4.3 residual for that first re-key). A vault whose owner never re-keys stays as today.

### 3.4 Failure as users meet it

- A kept member whose `pin2` doesn't verify: "You no longer have access to Garten" for her. "Bob couldn't be given the new key; invite Bob again" for the owner. The way back is a new invitation.
- A shared vault waiting for her list: "Garten waits for you: check who keeps access, then it is re-encrypted." Members notice nothing.
- New lock-outs Mallory can cause: a member deleted from the list by a stripped `grant` or a status he sets, and a member locked out by an older `pin2`. Both are what deleting her slot already does today.

## 4. Option B: Option A plus a ledger outside MongoDB

Relaxes nothing in §5.1. Prices **something outside MongoDB** (§5.2). **Not a round candidate: never shown to an attacker.**

- A file on a volume of its own, like the list of deleted users (`Accounts/DeletionList.cs`, which lives outside MongoDB for exactly this reason). The server appends to it after each commit, one line per event with ids only: a vault's current key id at each finish; each membership's creation (invitation or grant), activation, end (revocation, departure, lapse, decline) and pinned public key's hash.
- The re-key start takes the candidates as Option A does, minus everything the ledger says ended. The finish requires the key id the vault opens to to be the ledger's current one. A load refuses a vault whose key the ledger says is old, so A3.5 is closed, in place of `_keyIds`.
- **Operator's new duty:** the volume must be backed up and restored together with MongoDB. A restore without it refuses the affected vaults until an operator sets a configuration switch to trust the database once.
- **At-rest variant:** fails. Whoever has the volume can roll it back too, and Option A's guarantees remain. In a normal Compose deployment the volume sits beside MongoDB's on the same host, so the separation is only as good as the deployment makes it.
- Departures and earlier revocations become durable without the owner's list (S1, S4 closed). The list could then be dropped, or kept for legacy rows only.

## 5. Option C: no change to pins, state it honestly

Keep today's pins. Reword line 63 to say plainly that re-encrypting doesn't cut off a former member with database write access. Costs nothing, promises nothing.

## 6. Recommendation

**Build Option A's authenticity layer** (M_X, `grant`, `pin2`, the roster in `user-next` and the user slot). It closes the classes the earlier attempts failed on (A0, A1.x) cheaply, and needs nothing outside MongoDB. **Decide on Option B** for durability (A2, A3.5, departures). It needs a duty of the operator that Kumbu² already has for the deletion list. **But first, an independent review.** None was had in this run, and this design must not be built on the author's word alone.

## 7. Costs (§5.2)

- **Owner presence:** a list to confirm at every shared re-key start; rotation-driven re-keys of shared vaults wait for it. Option B could drop this.
- **Members:** nothing new. At the first re-key they may be unticked by mistake, and are then re-invited.
- **Outside MongoDB:** none in A. A volume and its backup duty in B.
- **Client or out of band:** none.
- **Detection:** none; both options prevent.
- **Primitives:** none new. AES-256-GCM, HKDF and P-256 ECDH as today.
- **Transaction:** the finish does one extra open per member (`pin2`), and handles at most the member limit, as today.
- **Size:** larger than "about a week". A: new fields on three documents, payload v2 of user and `user-next` slots, the list in the web app's revoke and re-encrypt dialogs, deferral in the runner, migration at the first re-key: an estimated two to three weeks with tests and review. B: about another week. A smaller 0.3.1 could honestly promise only what Option C says.
- **Test classes:** pin2 forgery (A0, A9.1, A5.1); planted rows (A1.1–A1.4); laundered acceptance; revival within an epoch (residual: asserts the list shows it); roster swap across runs (`from`); rotation with owner absent, and with a forged slot; owner rotation that skips an unopenable `grant`; migration at first re-key; finish with a damaged member; `Nothing_but_WrapToPinAsync_wraps_a_member_slot` kept; for B, ledger refusal of an old key and the restore switch.

## 8. Sentences

**Beside the re-encrypt offer (Option A):** "Re-encrypting gives a new key only to the people ticked in the list, even if someone kept a copy of the old key. Check the list: anyone who left or was removed earlier and is still listed keeps access."

**Proposed wording for `docs/security-architecture.md:63` (Option A built):** "**An ex-member who can also write the live database** can't be given the new key by a re-encryption: it wraps the new key only to the memberships the owner keeps in the list she confirms, each through a pin sealed under a key only she and that member hold. Within the time since the last re-encryption, he can make a membership that ended look active again, and it is then in that list; only the owner's unticking stops it. And whoever can write the database can put back the whole state before a re-encryption, the owner's slot included, after which what she writes is under the old key again; nothing in the database tells this from the present (§2, "Rollback")."

## 9. What was and wasn't tested

- **Nothing was attacked independently.** Two fresh attackers, each with the brief's prompt unchanged, were stopped by a safety classifier while writing their attacks, and returned no findings ([attempt 1](../overnighter/2026-11-02-pins-design-rounds/round-1-attack-attempt-1.md), [attempt 2](../overnighter/2026-11-02-pins-design-rounds/round-1-attack.md)). The run didn't try to word around the classifier.
- The author's own weak points are in [round 1's verdict](../overnighter/2026-11-02-pins-design-rounds/round-1-verdict.md). They are not a review.
- No code was built or run. The claims about today's code are from reading it at `715260a`. A3.5's reliance on process memory was checked by reading `VaultCatalog.Rekey.cs`, not by a test.
- §2's argument is the part I'm most confident of, and it is still an argument, not a proof.
