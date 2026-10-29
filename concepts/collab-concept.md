# Vault Sharing ("Collab") — Concept

Status: **requirements**: the open questions were decided with Chris on 2026-10-28 (§10, §12). The design (`docs/superpowers/specs/`) is written on 2026-10-29, once attachments part 2 is merged, together with what attachments add (§13); after that comes an independent security review of the design (a fresh Claude Opus 5.5 session at maximum effort that sees only the design and `docs/security-architecture.md`), and only then the plan. Release: **v3 (0.3.0)**, all four phases of §11. · Scope: Kumbu² server, REST API, web app, Obsidian plugin · Related: `docs/security-architecture.md` (§3 key rotation, §6 connections), `docs/concept.md`

## 1. Goal

A vault owner can share a vault with another Kumbu² user, read-only or read-write. The member's agents (API keys, app connections, Obsidian devices) work on the shared vault like on the member's own vaults. The owner can withdraw access at any time, and withdrawal is immediate and complete.

Example used throughout: **Alice** shares her vault *Gartenarbeit* with **Bob**, with write access. Later she revokes it.

## 2. Starting point: the current key model

- Every vault has its own random **vault key** (AES-256-GCM over notes, history, trash, paths, names, embeddings).
- The vault key is stored only **wrapped**, in **key slots**: under the owner's user key, under each API key and app connection given the vault, and (dev-token vaults) under the dev token and the recovery key.
- A **user key** is symmetric and unwrapped with the user's password or recovery key (Argon2).
- Decryption happens **on the server**. Members never see a vault key. An unlocked vault is kept in memory and locked after 15 idle minutes.

Sharing fits into this as a new slot type. No change to how notes are encrypted is needed.

## 3. Core model

### 3.1 Membership

A **membership** is one document per (vault, member):

| Field | Meaning |
|---|---|
| `vaultId`, `memberUserId` | who has access to what |
| `role` | `read` or `write` (see §7) |
| `status` | `pending`, `active`, `revoked`, `left` |
| `label` | the owner's handle for this grant, e.g. "Bob" (a label only, never an identity) |
| `createdBy`, `createdAt`, `acceptedAt`, `revokedAt` | audit |

**The membership is the source of truth for authorization.** Key slots only make access *possible*; the membership decides whether it is *allowed*.

### 3.2 Member slot

On acceptance, the server creates a **member slot**: the vault key wrapped under Bob's user key (or, see §6, under Bob's public key). It carries the `membershipId`.

### 3.3 Derived credentials

Bob's API keys, app connections and Obsidian devices for a shared vault get their own slots, as today. Each such slot additionally carries `grantedVia: membershipId`. This makes the cascade on revocation explicit (§5.2) and lets the owner see which agents of Bob's reach the vault (§8).

## 4. Invitation

### 4.1 The problem

To create Bob's member slot, the server needs the vault key in plaintext. It has it only while someone with access holds the vault unlocked. When Bob accepts, Alice is usually gone and the vault locked.

### 4.2 Invite slot

1. Alice creates the invitation while signed in, **confirming with her password** (as for exports), so a stolen session alone cannot share a vault. She gives it a label ("Bob"), a role and an expiry (default 7 days, at most 30).
2. The server generates a random **invite secret** and stores an **invite slot**: the vault key wrapped under `HKDF-SHA256(invite secret)`, plus a keyed hash of the secret for lookup. The secret itself is never stored.
3. Alice gets a link `…/share#<secret>`. The secret travels in the fragment, so it never reaches server or proxy logs (same as existing invitations).
4. Bob opens the link **signed in** (or signs up first). The server derives the wrapping key from the secret, unwraps the vault key, creates Bob's member slot, sets the membership `active` and **deletes the invite slot**, in one transaction. The invitation is consumed.
5. Before accepting, Bob sees the vault's name, the owner, the role and the vault's **embedding provider** (§8).

Alice can withdraw a pending invitation (delete invite slot, membership `revoked`). An expired invitation is cleaned up the same way.

The Obsidian pairing flow (`POST /api/pairing/redeem`) solves the same "redeem while the owner is away" problem; its implementation is the reference for this one.

### 4.3 The label is not an identity

Whoever redeems the link first becomes "Bob". Single use makes misuse visible (the real Bob sees "already used") but doesn't prevent it. Options, from weakest to strongest:

- **A. Link only** (default): accepted by whoever redeems it.
- **B. Owner confirmation**: after redemption the membership stays `pending` until Alice confirms "Bob (username `bob`) accepted — grant access?". The member slot is created at redemption but unusable until `active`.
- **C. Bound to a user**: Alice names an existing username; only that user can redeem. With user keypairs (§6), the server can then create the member slot directly, without an invite secret.

**Decided (Chris, 2026-10-28): A by default; B per link as an option.** When creating the link, Alice can tick "Ask me to confirm before access is granted", for extra-careful owners; it is off by default. Protection against a forwarded link comes from the single use and from sending the link through a safe channel (Bitwarden Send, onetimesecret), as people do with Vaultwarden invitations. C is not needed for that; with user keypairs (§6) it remains a way to share with an existing user without a secret.

**Opening the link without an account:** only admins create accounts. The page tells the visitor that they need an account on this Kumbu² first and should tell the person who sent the link that they haven't been invited yet. The link stays valid (it hasn't been redeemed) until it expires, so the invitee can open it again once they have an account. Experience from Vaultwarden: an invitation to someone without an account is often what prompts the account. When the invitee accepts, the owner sees it in the vault's access list and in the activity.

## 5. Revocation

### 5.1 Fail-closed, one atomic step

Revocation is **one single-document write**: membership `status: revoked`. That write is atomic in MongoDB regardless of anything else. From that moment on, every request through Bob or his derived credentials is refused, because:

- **Every request checks the membership**, not just the existence of a slot. A slot whose membership is not `active` is worthless.
- This also covers vaults **already unlocked in memory**: Bob's agent may have unlocked the vault a minute ago, and the key stays in RAM for up to 15 minutes. Authorization must not happen only at unlock time.
- It covers **OAuth access tokens** (valid up to an hour) and **API keys**: both are checked against the membership per request.

### 5.2 Cleanup

After the status flip, the server removes in one transaction:

- the member slot,
- every slot with `grantedVia` this membership (Bob's API keys, connections and Obsidian devices for this vault),
- API keys left with no vault are revoked (as on account deletion).

Cleanup is **idempotent** and can be retried, or run by a background job. If it fails, stale slots remain as data, never as access, because §5.1 already holds.

Open WebSockets of Bob's keys for this vault are closed with `4401` (the existing 30-second check is sufficient).

### 5.3 What revocation does and doesn't cover

- **Covers:** all future access through Kumbu², by Bob and by every agent he connected.
- **Doesn't cover:** what Bob or his agents already read, and what their AI providers saw. That cannot be undone by any system.
- **Doesn't cover (without re-encryption):** a database copy or backup from before the revocation still holds Bob's member slot, and Bob's password still opens it. This is the same caveat the README states for revoked API keys. Against it: re-encrypt the vault (§6). The revoke dialog offers "Also re-encrypt this vault" with that explanation.

## 6. Re-encryption of shared vaults

### 6.1 The problem

Re-encrypting a vault means wrapping the new vault key for every remaining member. User keys are symmetric and available only while their user is signed in. If Carol is a member and offline, the server cannot create her new slot.

The same problem arises in several existing flows once vaults are shared:

- "Re-encrypt vault" by the owner,
- "Re-encrypt my vaults" and **recovery** of the owner, which re-encrypt all her vaults in the background,
- re-encryption after revoking a member (§5.3).

### 6.2 User keypairs

Every user gets an **X25519 keypair**:

- the **private key** is stored wrapped under the user key,
- the **public key** is stored in plain.

Member slots are then created **under the member's public key** (HPKE or equivalent: ephemeral X25519 + HKDF + AES-256-GCM). The server can wrap a vault key for any member at any time, signed in or not. Opening a member slot needs the user key (to unwrap the private key), so access still requires the member's password, recovery key, or a credential derived from them.

Side benefit: invitations to existing users (§4.3 C) need no invite secret.

### 6.3 Migration

Existing accounts get a keypair at their next sign-in (the user key is available then). Until a user has a keypair, they can be invited only via the invite secret, and a shared vault whose members are not all migrated cannot be re-encrypted. The dialog says which members block it.

### 6.4 Interplay with existing flows

- **Owner re-encrypts / recovers:** the new vault key is wrapped for the owner and for every active member via their public keys. Members' derived credentials (API keys, connections, devices) for that vault are revoked, as today for the owner's own keys; members are told to reconnect.
- **Member recovers or rotates their user key:** their private key is re-wrapped under the new user key; member slots stay valid (they are under the public key, which doesn't change). If the keypair itself is rotated, member slots are re-wrapped while the old private key is still available.

## 7. Rights

**Three levels, for members and for every credential alike** (decided by Chris, 2026-10-28): `read`, `write`, **`maintain`**. They rise in that order. They apply to memberships and also to API keys, app connections and Obsidian devices, so a user can let one agent change the vault's structure and not another (a strong model in one client, a weaker one in another). The name follows GitHub, whose "Maintain" sits between "Write" and "Admin".

| Action | read | write | maintain | owner |
|---|:-:|:-:|:-:|:-:|
| `cat`, `ls`, `rg`, `semsearch`, `board` (read), history, diff, `img`, `pdf` | ✓ | ✓ | ✓ | ✓ |
| `write`, `edit`, `replace`, `mv`, `rm`, `restore <path>`, `restore --checkpoint`; `board` add/move/update (cards); uploads | | ✓ | ✓ | ✓ |
| **Structure:** writing `/Board/Board.md` by any route, `board create`, `rename_column`, `mkcp`, `rmcp`; later the OKF bundles' manifests (v4) | | | ✓ | ✓ |
| create own API keys / connections for the vault (up to own level) | ✓ | ✓ | ✓ | ✓ |
| share, revoke members, choose embedding provider, re-encrypt, export, delete vault | | | | ✓ |

- **No sham rules** (Chris): a right that one route enforces and another doesn't is worse than none. So a *structural file* is protected on every route: the tools, the REST API and web app (opened read-only for those below `maintain`), and the Obsidian sync (a local change to it is refused and listed as not synced, with the reason; the server's version stays).
- **The server knows which files are structural** (today `/Board/Board.md`; the OKF manifests with v4). Users don't flag files themselves; that would be a feature of its own.
- **Existing keys:** a write key can do `mkcp` and change the board's structure today; on the update it becomes `maintain`, so nothing breaks. New keys and invitations suggest `write`; `maintain` is chosen deliberately.
- **In the web app,** the `maintain` option is shown in a warning colour with a sentence: "Can change the vault's structure: the board's columns, checkpoints." The theme has no warning token yet: a new `--warning` for every colour scheme, checked by the contrast test.

The level is enforced by the server, as for read-only API keys today; it is not a cryptographic property (whoever can decrypt could produce ciphertext).

Writes by members show under the member's name in history and activity (the writer label is already per caller).

## 8. Transparency

- The owner sees, per vault, every member with role and status, and every derived credential of each member (name, kind, last used). Extends `GET /api/vaults/{vault}/access`.
- The member sees the vault's owner, their own role, and the vault's embedding provider. If the owner changes the provider later, members are told.
- Every share, acceptance, revocation and leave is an activity entry.

## 9. Edge cases

- **Member leaves:** same as revocation, initiated by the member (`status: left`). Offer re-encryption to the owner.
- **Member deletes account / privacy deletion** (decided by Chris, 2026-10-28): memberships and all slots go with the account. What the member wrote into someone else's vault **stays** as part of that vault, as a former employee's code and documents stay; the member's identity goes: in history, activity and writer labels, the member and their devices and agents become "a former member". The deletion receipt says so: "Notes you wrote in vaults shared with you stay in those vaults; your name was removed from their history." Anything more would be destructive.
- **Owner deletes account** (decided by Chris, 2026-10-28): the account and all its credentials go at once. For each shared vault, the deletion dialog lets the owner choose:
  - **"Give members 7 days"** (default): the vault stays for 7 days, read-only for everyone; members see a banner and an activity entry ("Alice has deleted her account. This vault will be deleted on 4 November. Export a copy to keep its notes."), their agents get the same line in tool results, and members may **export** it during the period (otherwise owner-only). Then it is deleted.
  - **"Delete it for everyone now":** the owner is still the owner. Members then see "Alice has deleted the vault *Gartenarbeit*".

  The privacy deletion through an admin offers the same choice, set from the request. 7 days stays well within the GDPR's month; the receipt names the shared vaults and their deletion date.
- **Member deactivated by an admin:** memberships stay, all requests refused, as for the member's own vaults.
- **Re-sharing:** members cannot invite others in v1.

## 10. Open questions (decided 2026-10-28)

1. ~~Owner confirmation by default or opt-in?~~ Opt-in per link, off by default (§4.3).
2. ~~Checkpoint restore, `mkcp`/`rmcp`, board structure?~~ `restore --checkpoint` is `write`; `mkcp`, `rmcp` and the board's structure are `maintain`, a new level (§7).
3. **Owner transfer:** later (v6 or after). It brings organisational questions of its own (who accepts, what happens to the embedding provider, the owner's own keys, several members); until then, a warning and a period (§9).
4. ~~Content of a member deleted under a privacy request?~~ Stays; the identity becomes "a former member" (§9).
5. ~~Re-encrypt after revoke?~~ **Offered, off by default**, with an honest sentence in the revoke dialog: "Only needed if Bob could get hold of an old backup of this server. Everyone's app connections and keys for this vault stop working and need to be set up again." Revocation alone already ends every access through Kumbu²; re-encryption only adds protection against someone who gets hold of an old database backup, which takes a break-in or the operator's help. Chris: there is a fine line between security for reasonable scenarios and paranoia, and Kumbu² doesn't cross it.
6. ~~Limits?~~ At most **20 members per vault** (admin setting, e.g. `KUMBU_VAULT_MEMBER_LIMIT`), at most **10 pending invitations per vault**, expiry 7 days by default and 30 at most. Generous for "people you trust", and a gentle nudge against a vault becoming a department's dump: one vault, one topic. Members' uploads count towards the owner's vault; there are no quotas yet (a known limit, as today).

## 11. Implementation phases

1. **Memberships + per-request authorization** against memberships (no sharing UI yet; tests that a revoked membership refuses already-unlocked vaults and live tokens).
2. **Invite slot flow** (§4.2), share and revoke in the web app, cascade cleanup (§5.2), access listing (§8).
3. **User keypairs** (§6.2) with migration at sign-in; member slots under public keys; invitations bound to users.
4. **Re-encryption of shared vaults** and the interplay with recovery and "Re-encrypt my vaults" (§6.4).

Phases 1–2 deliver a usable feature; until phase 4, re-encrypting a shared vault is refused with a sentence that says why. **0.3.0 needs all four phases:** the owner's recovery re-encrypts all of her vaults (the re-keying design), so without phases 3–4 a recovery would either fail on a shared vault or drop its members. Phases 1–2 may land on `master` as an intermediate state, not as a release.

## 12. Ideas for later (not requirements)

- **Messages between users** (collab 2.0, an idea of Chris's, no request): the invitation flow already has the moment for it ("tell the sender you haven't been invited yet").
- **Owner transfer** (§10.3).

## 13. To settle in the design, with attachments (2026-10-29)

Attachments part 1 is merged (#81, #84), part 2 (`img`, `pdf`, the PDF sidecar) is being built. The design must cover, against the code as built:

- uploads by members (rights, writer labels, the 15 MB limit, conflicts) and the vault's storage;
- blobs and the PDF analysis in re-encryption of shared vaults (§6.4) and in the cascade of §5.2;
- "a former member" in attachment histories and the activity;
- the members' export during the 7-day period, with attachments;
- the gallery, lightbox and attachment history for members (and read-only members);
- the Obsidian sync of a member: attachments and the refused structural files;
- metrics for sharing (monitoring design: no labels from user input).

