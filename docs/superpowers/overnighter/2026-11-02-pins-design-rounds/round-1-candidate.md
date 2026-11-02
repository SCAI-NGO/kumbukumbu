# Round 1 candidate: owner-made membership keys and an owner-chosen roster

Status: candidate for attack, round 1. Mechanism only.

## Anchor

The owner's user key. Every membership gets a random **membership key** made only where the owner's user key is at hand (invitation, grant), sealed under it; pins are authenticated under the membership key; and the set of memberships a re-key carries into the new vault key is the **roster**, taken from the owner's own choice in the request that starts the re-key and sealed under her user key with the new vault key.

## Keys and records

Notation: `Seal(key, plaintext, aad)` is AES-256-GCM as today; `HKDF(ikm, info, salt)` is HKDF-SHA256 as today. `kid(K)` is the vault key's existing key id. U is the owner's user key, V the vault id, X a membership id.

| Name | Where | Contents | Sealed under | AAD |
|---|---|---|---|---|
| Membership key M_X | nowhere in the clear | 32 random bytes, one per membership | — | — |
| `grant` | `memberships` document X, new field | `{v:1, membershipId: X, vault: V, label, mkey: M_X, epoch: kid(K)}`, K being the vault key when it was written | `HKDF(U, "kumbu/v1/membership-grant", salt=V)` | `membership-grant:{V}:{X}` |
| invite `mkey` | `invite` slot of X, new field | M_X | `HKDF(invite secret, "kumbu/v1/invite-mkey", salt=V)` | `invite-mkey:{V}:{X}` |
| member `mkey` | `member` slot of X, new field | M_X | `HKDF(member's user key, "kumbu/v1/membership-key", salt=V)` | `membership-key:{V}:{X}` |
| member `mkeyWrapped` | `member` slot of X, new field, only for a membership blessed at its first re-key (Migration) | M_X | ECDH to the pinned public key, as a member slot is wrapped (`MemberSlotCrypto.Wrap`) | `membership-key-wrap:{V}:{X}` |
| `pin2` | `memberships` document X, new field, replaces `pin` | `{membershipId: X, vault: V, member, alg: 1, publicKey}` | `HKDF(M_X, "kumbu/v1/membership-pin2", salt=V)` | `membership-pin2:{V}:{X}` |
| user slot, payload v2 | owner's `user` slot of V | `{v:2, vaultKey: K, roster: [{membershipId, mkey}]}` | `UserKek(U, V)` as today | `slot:{V}:user` as today, plus the payload's version byte |
| `user-next` slot, payload v2 | owner's `user-next` slot of V | `{v:2, vaultKey: K′, from: kid(K), roster: [{membershipId, mkey}]}` | `UserKek(U, V)` as today | `slot:{V}:user-next` as today, plus the version byte |

A user slot of payload v1 (today's raw vault key) is a **legacy** user slot: its vault has no roster yet. A v2 payload is never written without a roster (an empty list is a roster).

`pin2` **verifies** for membership document D under M if it opens under `HKDF(M, "kumbu/v1/membership-pin2", salt=D.vault)` with AAD `membership-pin2:{D.vault}:{D._id}`, names `D._id`, `D.vault`, `D.member`, alg 1, and a public key on the curve.

The existing `pin` (sealed under K) is kept only on legacy memberships, those created before the upgrade, until their vault's first re-key under this design (Migration). `label`, `writers` and `writerIds` stay sealed under the vault key as today.

## The re-key

### Before the start: the owner's choice

A shared vault's re-key starts only from a request of the owner's session that carries **`keep`**, a list of membership ids. The server offers her the **candidates**, shown by the `label` inside each one's `grant` (opened with her user key), never by the vault-key-sealed `label`:

1. every entry of the roster in her current user slot (payload v2) whose membership document says `active`;
2. every `active` membership whose `grant` opens under her user key with `epoch` = `kid(K)` of the current vault key;
3. only while her user slot is legacy (payload v1): every `active` membership with a legacy `pin` that opens under K and verifies as today (`Memberships.PinnedKey`). These are shown apart, with their vault-key-sealed `label`, under "Shared before this version; Kumbu² can't check these".

Every candidate is ticked by default, except one the same request revokes. The dialog says: "Everyone ticked gets the new key. Anyone else loses access to Garten." `keep` must be a subset of the candidates; at most `KUMBU_VAULT_MEMBER_LIMIT` ids; otherwise 400 and nothing starts.

Where the choice comes from:

- **"Also re-encrypt this vault"** in the revoke dialog, and **"Re-encrypt vault"** in Settings: the dialog shows the list; the request carries `keep`.
- **The owner's own rotation or recovery, and "Re-encrypt my vaults"**: stage 1 is unchanged and her vaults go into `rekeyPending` as today. A vault with no active membership starts in the runner as today. A **shared** vault's run does not start until a session of the owner answers the question "Check who keeps access to Garten before it is re-encrypted" (asked at her next page load, and in the vault list) with a `keep`. Until then the vault stays under K and works as today for everyone; it is listed to her as waiting for her.

`keep` travels with the queued run in the runner's memory, beside the user key it already holds (`RekeyRunner.cs:49`), and is never written to the database. A run whose `keep` was lost (a restart before its start committed) waits for the owner's choice again.

### Start

In the start transaction, as today (`VaultCatalog.Rekey.cs:424`), plus:

- **R′**, the new roster: for each id in `keep`, `{id, M_X}`, with M_X from the current roster entry, else from the `grant`; for a legacy candidate kept, a new random M_X.
- For each **legacy** candidate kept: `pin2` written under its new M_X with the public key its legacy `pin` names, and `mkeyWrapped` written into its member slot (M_X wrapped to that public key); and a `grant` written for it under U with its label as the vault-key-sealed `label` shows it, `epoch` = `kid(K)`.
- The `user-next` slot is written with payload v2: `{K′, from: kid(K), roster: R′}`.
- Invitations are withdrawn and unconfirmed redemptions declined, as today.

A resumed run reads K′ and R′ from its `user-next`; it is refused (`VaultNotRekeyableException`) if `from` isn't `kid` of the key the owner's user slot opens to, or if K′ equals that key.

### Finish

In the finish transaction, as today, but driven by R′ instead of by the active memberships:

- For each `{id, M}` in R′: the membership document is read with a filter on `_id`, `vault` and `status: active`. If there is one and its `pin2` verifies under M, its existing member slots on V are wrapped to K′ for the pinned public key (`WrapToPinAsync`'s seam, as today). If there is none, or `pin2` doesn't verify, its member slots are deleted and it is logged as damaged; the finish goes on.
- Every membership of V not in R′, whatever its status: its member slots are deleted; one that says `active` or `pending` is set to `revoked` with `revokedAt`, `cleanedAt` and an event `membership-revoked`; its legacy `pin` is unset.
- `label`, `writers`, `writerIds` resealed under K′ for every membership, as today. `pin2` and `grant` are not touched.
- The owner's user slot is written with payload v2: `{K′, roster: R′ minus the damaged}`.
- No member slot is created. Concurrency: every write is conditional on the `revision` it read; a conflict retries the transaction, five times, then the vault stays pending, as today.

## Each row of §2.4

| Event | What is written | Verified by, with which key |
|---|---|---|
| Invitation created (owner present) | M_X new; `grant` under U with `epoch` = `kid(K)`; invite `mkey` under the link's secret; invite slot as today | — |
| Invitation accepted (owner absent) | M_X opened from invite `mkey` with the link's secret; member slot with member `mkey` under the redeemer's user key; `pin2` under M_X with the public key derived from her private key (`Memberships.MemberPublicKeyAsync`); `active` unless `confirm`. An invite slot without `mkey` (created before the upgrade): as today, a legacy `pin` under K, and the membership is legacy | — |
| Owner confirms a redeemer | as today | — |
| Grant (both keys) | M_X new; `grant` under U; member `mkey` under the member's user key; `pin2` under M_X | — |
| Member's rotation (owner absent) | Per member slot: M_X from member `mkey` (old user key), else from `mkeyWrapped` (old private key). The current `pin2` must verify under M_X and name her old public key (else damaged, as today's rule). `pin2` written under M_X for her new public key; member `mkey` sealed under her new user key; `mkeyWrapped` removed; slot rewrapped to her new public key as today; `revision++`. A legacy membership (no M_X): as today, its legacy `pin` under K | `pin2` under M_X from her own copy |
| Role change | as today | — |
| Revocation | as today (`status`, `revokedAt`, `revision++`, legacy `pin` unset); `grant` and `pin2` stay | — |
| Leaving (owner absent) | as today | — |
| Cleanup, sweeps, the 7-day lapse | as today | — |
| A member's account deleted | as today: her memberships go, with their `grant` and `pin2` | — |
| Re-key start | above | candidates: `grant` under U, the roster under U |
| Re-key batches | as today | — |
| Re-key finish | above | `pin2` under M from R′, R′ under U |
| Stalled re-key resumes | K′ and R′ from `user-next`, under U | `from` = `kid` of the user slot's key |
| Owner's own rotation or recovery | stage 1 also seals each `grant` of her vaults' memberships again under the new user key (one that doesn't open is left and logged; the rotation goes on), and carries payload v2 of her user slots and `user-next` slots; shared vaults wait for her `keep` (above) | — |
| A member's API key or connection opens the vault | as today | — |
| Owner's account closing | as today | — |

## Migration

Nothing runs at the upgrade. Legacy memberships keep working as today: their member slots open as before, their rotations use the legacy `pin`. New invitations and grants after the upgrade get `grant`, M_X and `pin2`.

A vault's **first re-key under this design** is where its legacy memberships are carried or not: they are candidates of kind 3, shown apart with their vault-key-sealed labels, ticked by default, and those kept get M_X, `pin2`, `mkeyWrapped` and a `grant` in the start transaction. From its finish the owner's user slot has payload v2 and the vault never offers legacy candidates again.

A vault whose owner never re-keys it stays as it is today, for as long as that lasts.

## Failure as users meet it

- **A kept member whose `pin2` doesn't verify at the finish**, or whose document is gone or not `active`: no new key. She sees "You no longer have access to Garten" in her list; the owner sees an event "Bob couldn't be given the new key; invite Bob again." The way back is a new invitation, which makes a new M_X under the owner's key.
- **A candidate whose `grant` doesn't open** is not shown; if it was in the roster, it is shown as "a member (label unavailable)".
- **A shared vault waiting for `keep`**: the owner sees "Garten waits for you: check who keeps access, then it is re-encrypted." Members notice nothing.
- **A run that stays pending** (finish conflicts five times): as today, "being re-encrypted" for everyone; the owner's next sign-in resumes it with R′ from `user-next`.

## The sentence for the re-encrypt offer

"Re-encrypting gives a new key to the people you keep in the list, and to nobody else, even someone who kept a copy of the old key."
