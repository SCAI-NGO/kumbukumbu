# Pins a former member can't forge: the task

Status: **task description**, 2026-11-02, for an unattended design run on `feature/pins-design`. It frames the problem and proposes no solution. The result goes to `docs/superpowers/specs/2026-11-02-pins-design.md`. Line anchors are from `d9099d7`.

Sources: [0.3.0 fixes design §1.2](2026-11-01-0.3.0-fixes-design.md), [collab design §2, §5.4, §7.1, §13, §14](2026-10-31-collab-design.md), [security architecture §2 and §3](../../security-architecture.md), the code named below.

**What is known of the two earlier attempts.** Fixes design §1.2 records that an independent review took two attempts at owner-authenticated pins apart, and that "new rows, revived tombstones and rollback all got through". That sentence is all the repository holds: the review's reports were never committed, and the fixes report (`docs/superpowers/overnighter/2026-11-01-0.3.0-fixes-report.md`) only defers the topic to 0.3.1. What the attempts were is not recorded. §4 therefore works out those three classes from the code, and marks what is derived.

## 1. The question

Can Kumbu² be changed so that re-encrypting a vault after a revocation also cuts off a former member who kept the old vault key and can write the live database? If so, how, and what does it cost? If not, what is the nearest thing to it, and what does each option give up?

## 2. The system today

### 2.1 What a pin does

A vault has one symmetric vault key K. A member's `member` slot wraps K to her P-256 public key (`src/Kumbu.Server/Sharing/MemberKeys.cs`, collab design §5.2). The `pin` on her `memberships` document holds that public key with the membership id, vault, member and algorithm, sealed under a key derived from K, bound to `membership-pin:{vault}:{id}` (`Sharing/Memberships.cs:152–157`).

A re-key gives the vault a new key K′. Its finish runs inside one transaction with the owner's slot update. For each active membership it opens the pin under K, wraps K′ to the key the pin names and seals the pin again under K′ (`Memberships.cs:281–300`, `:198–222`; `Vaults/VaultCatalog.Rekey.cs:540`). The pin exists so that no wrap goes to a key the database names in the clear (design §5.4), and it holds against an attacker who has no vault key (`tests/Kumbu.Server.Tests/Sharing/PinTests.cs`, for example `A_forged_membership_with_a_member_slot_gets_nothing`, whose forger seals under a key of his own).

Every member has held K, or has been able to. So a pin proves nothing against a former member.

### 2.2 The attack as it stands

Alice owns *Garten*, shared with Bob and Mallory. Alice revokes Mallory and ticks re-encryption. Mallory kept K and can write the database.

1. Before the finish commits, Mallory overwrites the `pin` of Bob's membership with one he seals himself under K, naming Bob, Bob's membership and Mallory's public key.
2. The finish opens that pin under K and wraps K′ to Mallory's key in Bob's member slot.
3. Mallory reads Bob's slot and has K′.
4. Mallory can now wrap K′ to Bob's real public key (it is public) and seal Bob's real pin under K′. Bob's access works, and nothing is logged as damaged.

`PinTests` has forgers who hold K only with a pin that contradicts its row (`A_pin_whose_member_field_differs_is_refused`, `PinTests.cs:232`); none writes a consistent one, which would pass. `docs/security-architecture.md:63` states the residual.

### 2.3 Where a pin matters

Without a re-key there is nothing to defend: K is still current and Mallory holds it. A member's rotation wraps the same K. So the boundary this work protects is one crossing, **from a key the attacker holds to a key he doesn't**, in a re-key. Once he holds the current key he stays in through later re-keys by the same trick. Conversely, a former member who was cleanly cut off once cannot forge afterwards, unless rollback undoes that.

### 2.4 Who has what, when

This table drives the constraints. "Owner's key" means the owner's user key is at hand in the server. The vault key is available only as stated.

| Event | Owner's key | Member's key | Vault key | Where |
|---|---|---|---|---|
| Invitation created | yes | no | K | `Sharing/VaultInvites.cs:88–105` |
| **Invitation accepted**; with no confirmation asked, the row becomes `active` here | **no** | the redeemer's | K, from the invite slot and the link's secret | `VaultInvites.cs:158–197`, pin at `:186` |
| Owner confirms a redeemer | yes | no | K | `VaultInvites.cs:248` |
| A grant (core of an acceptance, with both keys) | yes | yes | K | `Memberships.cs:71` |
| **Member's rotation**: new user key, new keypair, slot and pin rewritten | **no** | old and new | K, from what her slot opens | `Accounts/UserKeyRotation.cs:161–201`, `:393–433` |
| Role change | yes | no | K | `Memberships.cs:506` |
| Revocation | in the session, not passed to the step | no | not used | `Memberships.cs:473`, `:551` |
| **Leaving** | **no** | in the session, not passed to the step | not used | `Memberships.cs:478` |
| **Cleanup, sweeps, the 7-day lapse of a redeemed row** | **no** | **no** | none | `Memberships.cs:571` |
| **A member's account deleted**: tombstones go too; an admin's deletion has no user key at all | **no** | at most hers | none | `Memberships.cs:452` |
| Re-key start | yes | no | K, K′ | `VaultCatalog.Rekey.cs:424` |
| Re-key batches and finish | yes, held in memory by the runner (`Vaults/RekeyRunner.cs:49`) | no | K, K′ | `VaultCatalog.Rekey.cs:355–399` |
| Stalled re-key resumes | at her next sign-in, however long that takes | no | K, K′ | collab design §7.1 |
| Owner's own rotation or recovery | old and new (recovery: the recovery key instead of the password) | no | K per vault; all her vaults go into `rekeyPending` | `UserKeyRotation.cs` |
| A member's API key or connection opens the vault | **no** | **no** | K, from its own `grantedVia` slot | collab design §5.3 |
| Owner's account closing (7 days) | **gone** | members' | K, from members' slots | collab design §7.3 |

How often: an owner can stay away for months. Re-keys are rare and started by hand or by a rotation. Rotations are rare. A vault has at most 20 live memberships (`KUMBU_VAULT_MEMBER_LIMIT`), and the finish handles them all in one transaction.

All cryptography runs on the server. Clients hold passwords and bearer secrets, and no keys.

## 3. The threat model

**Keep this section fixed.** A candidate may not pass by narrowing it. A run that thinks it is wrong proposes a change to the maintainer in its report (see the brief).

### 3.1 Mallory holds

- **K**, the vault key current when he left, and everything derived from it. The server never shows a member K, but with database access and his own credentials he opens his own member slot, or an invite slot with its link.
- **His own account's secrets**: password, user key, keypair, API keys. He may still have an account on the instance, use every route as any user does, and own vaults of his own. He may be invited again.
- **Everything he saw while he was a member**, and everything in the database at any moment since: he may have copied any document at any time. Assume backups in his hands, as of any date.

### 3.2 Mallory can

- **Read and write the live MongoDB, every collection, at any time, and watch it in real time.** That covers inserting, deleting and restoring documents, in any combination, including consistent snapshots of several collections at once, and putting them back afterwards.
- That includes every plain field: `memberships.status`, `member`, `owner`, `role`, `revision`, `revokedAt`, `cleanedAt`; slots and their `kind`, `owner`, `lookup`; `users.publicKey`, `active`, roles; the vault's `rekeyStartedAt`, `rekeyEnded`, `membershipsChanged`; `users.rekeyPending`; `vault_events`, including deleting or forging the plain parts of a notice.
- That includes **the instance secret** (`server` collection, `Accounts/ServerSecret.cs`), so he can make invitation lookups and insert invitations, and through them accounts (`security-architecture.md:224`).
- His writes sit outside the server's transactions but conflict with them, so he can also force a retry at a moment he chooses.

### 3.3 Mallory cannot

- Know any other person's password, user key, recovery key, private key, API key secret or token, except what old backups plus a credential valid then would give, which is not assumed here.
- Know K′ at the start of the re-key, or any key sealed only under the owner's or another member's credentials.
- Read or change the running server's memory, its code or its image, or intercept TLS between clients and the server.
- Break AES-256-GCM, HKDF, Argon2id or P-256.

**At-rest variant.** The main model is the database alone. A hoster's disk (design §5.4, architecture §2) is the variant: MongoDB **and** every file on the host's volumes and its configuration at rest, still not the running process. A candidate that rests on anything outside MongoDB must say whether it survives the variant, and how far it can be separated from the database in a normal deployment.

Today, outside MongoDB, there are the list of deleted users (`Accounts/DeletionList.cs`, `<Kumbu:Deletions:Path>`, a volume of its own) and the configuration; this is a fact about the system, not a hint.

### 3.4 What counts as a break

Take a re-key of the vault that starts after Mallory's membership ended. Mallory breaks the design if, at any time after that re-key finishes, without any of the things in §3.3:

- **G1.** He learns K′ or any later vault key of the vault.
- **G2.** He reads plaintext written to the vault after the finish, by any route, including steering the owner or a member into writing under a key he holds.
- **G3.** He forces a state whose way out reaches G1 or G2: a lock-out, for example, that the owner or the server resolves by trusting what the database says.

### 3.5 Knowingly out of scope

- A compromised running server, a malicious image, an operator changing the code.
- A **current** member: he has K′ by right, and collusion with one gives Mallory nothing new to defend.
- What K opens: everything sealed under K, in any copy, for good. Nothing recalls it.
- **Plain destruction and denial** by the database writer: he can always delete. It stays out of scope unless it fails open (G3), but a candidate says what users see and which **new** lock-outs it adds.
- Notes forged under K before the re-key, which the re-key's batches then seal under K′ as they do any document. Integrity of content is not this work's target. If a candidate depends on what the batches seal again, attack A11 applies.
- Rollback of notes and other content under the **same** key (architecture §2, "Rollback"), and offline guessing of weak passwords from a dump.
- Harvest-now quantum attacks on member slots (architecture §2).

## 4. Attacks any candidate must survive

Each is a requirement on a candidate: stopped, or stated as a residual, with the reason. **Review** means named by the review in fixes design §1.2. **Code** means read from the code at `d9099d7`. **Derived** means reasoned from the threat model and not executed. A run that finds a Derived item inapplicable says why.

**The baseline**

- **A0. Replaced pin on a live row** (Code). §2.2, including the silent repair of step 4.

**New rows** (Review)

- **A1.1 A row before the re-key.** Insert an active membership and a member slot, with a fresh id, a member of his choosing and a pin he seals under K. The finish wraps to whatever is active with a slot (`Memberships.cs:289–300`).
- **A1.2 A row during the re-key.** Insert it in the two-key period. The finish meets a mismatch, reads the memberships again (`VaultCatalog.Rekey.cs:389`) and retries up to five times, and the new row is then expected.
- **A1.3 A row laundered through the real acceptance** (Code). With K and the instance secret, insert a `pending` row and an invite slot with a secret of his own, then redeem it through the real route with his account or one he created. The server itself then writes a genuine pin and, without `confirm`, makes the row active, **with the owner absent** (`VaultInvites.cs:158–197`). Anything an honest acceptance produces, this produces. The check that the vault isn't re-keying is a plain field (`VaultCatalog.Members.cs:76`).
- **A1.4 Limits.** The member limit is counted when a row is created by the server, not by a row written into the database. A candidate must not depend on a count of rows he can change, nor a finish overflow its transaction by many planted rows.

**Revived tombstones** (Review)

- **A2.1 His own.** Set his revoked or left row back to `active`, clear `revokedAt` and `cleanedAt`, seal a new pin under K (the pin is unset on ending, `Memberships.cs:551`, `:600`), and insert a member slot. The tombstone keeps its id, so whatever authenticates by id authenticates it.
- **A2.2 Someone else's**: another leaver's row, a declined redeemer's, a withdrawn invitation's.
- **A2.3 Repeat membership.** The same person may have a tombstone and a later genuine membership of the vault; the unique index covers live rows only (design §14). Authorisation that names the person and vault doesn't tell the two apart.
- **A2.4 Absence.** Tombstones are deleted outright when a member's account goes (`Memberships.cs:452`). So "no tombstone" is not evidence of anything, and deleting one must not lift a revocation.

**Rollback and replay** (Review)

- **A3.1 Replay of his own genuine state.** Whatever Mallory's row held while he was a member, including any authenticator, signature, counter or MAC, put back after the revocation.
- **A3.2 Undone revocation.** A candidate's record of the revocation, or its counter, list or manifest, restored to before it happened, **together** with whatever guards it. Assume a consistent snapshot of everything in the database.
- **A3.3 Undone rotation.** A member's pin and slot restored to before her rotation, so that the finish wraps K′ to her old public key. That alone only locks her out, unless Mallory also has the old private key (an old backup and an old password; design §5.1, §13 H1), which §3.3 doesn't assume. So the candidate says what rotation still ends if this rollback works, and an answer that it ends nothing against such a person is a stated residual, at most Medium.
- **A3.4 Transient rollback.** Write just before the finish reads, restore just after, so that any later audit sees clean state. Also force the retry of §3.2 to pick the moment.
- **A3.5 Back to K's whole era.** After the re-key, restore the owner's user slot, the vault's meta and its documents to their K state. The owner's session then opens under K, and what she writes from then on is sealed under a key Mallory holds. **No pin is touched.** As the code reads, the guard against an old key is process memory (`_keyIds`, `_rekeyed`, `VaultCatalog.Rekey.cs:60`, `:71`, `:110–116`), gone at a restart; please check this. This is G2, and §2.3's crossing undone from the other end. A candidate must close it or name it as a residual, with what that means for the promise "re-encrypting cuts him off".
- **A3.6 A genuine restore looks the same.** A candidate that detects rollback must say what happens when an operator restores a real backup (architecture §7, and the deleted-users list exists for this reason), and it must not offer a recovery path Mallory can trigger by staging one.

The same class has just appeared elsewhere: on `origin/feature/rename-own-vaults` (`0203fb7`), `docs/security-architecture.md` §5.3 says an older meta of the same vault still opens, so a database writer can put back an earlier display name or embedding provider. If another vault of hers now has the earlier name, her requests fail, and **a rotation of her user key and an account recovery fail too**, until she deletes the other vault (`Vaults/VaultCatalog.cs:1658`, `Accounts/UserKeyRotation.cs:373` on that branch). That branch's report, with its finding L1, doesn't exist yet at `0203fb7`. Cause: same class, older genuine state that opens because nothing says it is old. Effect: different, as it reaches no keys, though a rolled-back provider does send text somewhere. Two consequences here: a candidate's rollback answer may or may not extend to the meta, which is worth saying but not required; and **anything a candidate adds to rotation or recovery must not give a rollback a new way to block them**, since they are how a person recovers from a compromise.

**Downgrade and migration** (Derived)

- **A4.1 Stripped authenticator.** Remove a new field so that a row reads as legacy. The repository has met this before (architecture §4: "a slot's payload version stripped to make a new `write` read as a legacy one").
- **A4.2 Planted before the migration.** A pin forged under K before the upgrade is indistinguishable from a real one by anything K-sealed. A migration that blesses what it finds blesses that.
- **A4.3 Already inside.** A former member who already took K′ by forgery in an earlier re-key holds the current key when the migration runs.

**Moved, swapped, substituted** (Derived)

- **A5.1 Transplant.** An authenticator moved from another membership, vault, member or owner. Mallory owns vaults himself, so **anything a vault owner can produce for her own vault he can produce for his**, then move it, changing the plain `vault`, `owner` and `member` fields around it.
- **A5.2 Verifier swap.** Any verifying key or identity read from the database, including `users.publicKey`, a slot's `owner`, `memberships.owner`, replaced by his own. If something binds to "the owner", say how the server knows who that is at the finish.
- **A5.3 Secrets all members had.** Anything derived from K, or handed to every member through a member's own slot, or readable by any member's session while he was one, is something Mallory holds.

**The two-key period and the finish** (Derived)

- **A6.1 Check here, use there.** A check at the start or at some other time that the finish relies on. A stalled run may lie open for months.
- **A6.2 Stall as a lever.** If one bad row refuses the whole finish, Mallory holds the vault in "being re-encrypted" for everyone by writing one row, and the vault stays pending. Today a bad pin costs only that member's slot (`Memberships.cs:205–211`). The candidate says which it does, and why the choice can't be turned against the owner.
- **A6.3 Plain state of the run.** `rekeyStartedAt`, `rekeyEnded`, `rekeyPending` written by him, in order to restart, skip or confuse a run.

**Members' own paths** (Derived)

- **A7.1 Forged slot.** A member slot is public-key encryption: anyone can write one to Bob's public key, holding any key. A candidate in which Bob or the server treats what a slot opens as proof of anything must survive this. A rotation reads its K out of the slot (`UserKeyRotation.cs:208`) and seals the new pin under it (`:433`), so ask what it seals under when the slot holds a key of Mallory's choosing.
- **A7.2 Rotation with the owner away.** Any authorisation of a pin change must work with the owner absent for months, and an attacker must not be able to pose as the rotating member, including with a private key from before her last rotation.
- **A7.3 Forced damage.** A forged pin makes Bob's rotation or the finish delete Bob's slot as damaged (`Memberships.cs:205–211`). Say what Bob and Alice then see, and whether the way back is safe: a "re-add" that trusts the database is a way in.

**The owner as a witness** (Derived)

- **A8.1 What the owner is shown.** The access list's labels are sealed under K (`Memberships.cs:114`, `:668`), so **Mallory can forge the label "Bob" on his own row** before the re-key, and redeemer names and usernames are plain. A candidate in which the owner reviews or approves anything on screen must survive a screen that lies this way.
- **A8.2 Suppressed warnings.** A warning or event stored in the database can be deleted. A detection that reports through the database reports only what he allows.
- **A8.3 Trained clicks.** A prompt that appears often gets clicked, and a flood of false alarms from forged rows trains that.

**Identity and repetition** (Derived)

- **A9.1 A swapped member field.** The plain `member`, or a slot's `lookup`, moved to another user or a recreated one.
- **A9.2 Twice.** Revoked, re-invited, revoked again; or two former members in turn; or a second re-key K′ to K″. An authenticator from one era replayed in the next.

**Oracles** (Derived)

- **A10. Honest code as an oracle.** He feeds the server values and reads what it writes back. The finish's `Resealed` opens fields under K and seals them under K′ (`Memberships.cs:319–341`), the batches do the same to documents, a rotation seals a pin, an acceptance writes one. Any operation that signs, MACs or seals what the database supplies is a tool in his hand.
- **A11. Laundering.** A value that is only "something that opened under K and was sealed again under K′" carries no authority, however it looks after the re-key.

### Severity

- **Critical:** G1 or G2 within §3, with no further assumption.
- **High:** G1 or G2 under a narrow timing or a realistic extra condition inside §3; or a hard constraint of §5.1 broken in normal use.
- **Medium:** a new lock-out, or a loss of detection the candidate claims; a priced cost of §5.2 left out.
- **Low:** a wording, an edge case, a documentation gap.

Only an attack with concrete steps counts: which document and field are written, what Mallory holds at each step, and where the honest code does what.

## 5. Constraints

### 5.1 Hard

An option may relax one of these only if it says so in its first sentence and prices it (§6). It is then a trade-off for the maintainer to decide, not a design that meets the task.

1. **No change to what a member's credentials can do while the owner is offline.** Every row of §2.4 with "no" under the owner's key keeps working with her absent for months: acceptance without confirmation, a member's rotation and recovery, leaving, a member's API keys, connections and devices opening the vault (with the member signed out too), her exports, her alias, her new credentials, sweeps and account deletion.
2. **The owner's offline state stays as it is.** No key of hers stays in memory longer than today, and nothing that opens her vaults is stored for use while she is away, outside what her credentials open. A stalled run still waits for her.
3. **Existing memberships migrate without a flag day.** No member loses access by upgrading. Nobody has to act for a member's access to go on. An owner who never comes back keeps her vault working for its members, as today.
4. **The re-key stays crash-safe, resumable and non-interactive once started.** No question to anyone between start and finish. Its finish stays one transaction, wrapping nothing to a key the database names in the clear, and it creates no member slot.
5. **A member's rotation stays atomic**, in its one transaction, gives a new keypair and ends the old private key's use for new wraps (design §5.1, §13 H1).
6. **Everything fails closed**, and what isn't verified grants nothing.
7. **Members stay anonymous among themselves, and no new names reach the database in the clear** (design §8, §13 M4, architecture §2, §5.6). The owner still can't read a member's alias.
8. **Nothing already stopped comes back.** `PinTests` (in particular `Nothing_but_WrapToPinAsync_wraps_a_member_slot`), `MembershipCheckTests`, `BoundMembershipWriteTests`, and the conditional writes on `revision`. A candidate may replace a test's mechanism, and says what replaces its guarantee.

### 5.2 Priced

Each is allowed if the result states what it costs and who pays.

- **More owner presence**, or the owner doing something she doesn't do today, including when it applies: rarely and at the re-key is one thing, at each member's rotation another. Say what happens in the months she is away.
- **Members doing something new**, or losing something at a re-key that they keep today.
- **Something outside MongoDB**: a secret in the configuration or state on a volume. `ServerSecret.cs` lives in MongoDB on purpose, so that an instance needs no further secret set up. State the operator's new duty, what a backup and a restore must now include, and the at-rest variant.
- **Something in a client or out of band**: all cryptography is on the server now (§2.4), and the web app and plugin hold no keys.
- **Detecting rather than preventing**: state who learns of it, through what channel the attacker can't suppress (A8.2), and what they can then do.
- **A new primitive or dependency.** Today: AES-256-GCM, HKDF, Argon2id, P-256 ECDH from .NET's `System.Security.Cryptography`, and Konscious for Argon2. Architecture §2 names P-256 as the only primitive not resistant to quantum attacks, so another asymmetric one adds to that sentence.
- **The re-key's transaction.** The finish holds the process-wide `_loading` semaphore (fixes design §1.3), up to 20 memberships inside one transaction. Don't lengthen it noticeably. How `_loading` is held is another 0.3.1 item, not this one's to solve, so the design should not depend on it staying as it is.
- **Size.** The open item asks for this in 0.3.1, within about a week of 0.3.0. A much larger answer is a legitimate result if it says so, along with what a smaller release can honestly promise.

### 5.3 Style of the promise

The owner is not an expert. Whatever is built must be stateable in one honest sentence beside the re-encrypt offer (design §6.4), and it replaces the residual at `security-architecture.md:63`.

## 6. What a good result looks like

**Any of these is a good result:**

- A proposal that meets §5.1 and stops all of §4, or names each residual.
- Honest options: "this can't be had without X; here is the trade-off, and these are the options".
- A reasoned negative: why, under the hard constraints and this attacker, no design can work, stating what is needed for that to change.

**In each case the result states:**

1. **The anchor, in one sentence.** What does Mallory lack that the verification rests on? Who holds it, where does it live, and when is it at hand? Everything else follows from that sentence.
2. **Every row of §2.4 walked through:** what is written, what verifies it and with which key, with the owner away where the table says she is.
3. **A table of §4, row by row:** stopped (how), residual (what remains, for whom), or not applicable (why). Each row is checked against the code, not only against the idea.
4. **Migration**, including A4.2 and A4.3, and from when a promise holds for a given vault: what the first re-key after the upgrade does and doesn't end.
5. **Failure as users meet it.** What Alice and Bob see for a damaged row, what they can do about it, and why that way back is safe.
6. **Costs:** §5.2 item by item, a rough size of the build, and the test classes it needs (named, no code).
7. **The sentence for the re-encrypt offer and proposed wording for `security-architecture.md:63`.** Proposed in the design; the file isn't changed by the run.
8. **For options:** each opens with the constraint it relaxes, then who pays, which attacks of §4 still stand, then a recommendation with its reason. An option that has not faced a round says so.
9. **What was and wasn't tested.** "Survived N rounds by reviewers without the reasoning" is the most that can be claimed. It is not proof, and the result says so.

**It is a bad result if it:**

- moves trust into a field Mallory can write and calls it verified;
- answers rollback with a counter, list or manifest, without saying what stops that from being rolled back with the rest;
- authenticates "by the owner" without saying what happens at the rows where she is absent;
- narrows §3 instead of stating a residual;
- has been changed since the last adversary saw it;
- sounds more certain than the evidence.

## 7. Assumptions for the maintainer's review

Each is written into the above as stated, and is his to change before the run:

1. **The main attacker has the database alone**, and the hoster's disk is a declared variant (§3.3), not the main model.
2. **A3.5, restoring the vault's whole K era, counts as an attack to be answered**, though it touches no pin, because it defeats the same promise. The alternative is to leave it to the separate rollback item in architecture §9.
3. **Forged notes laundered by a re-key are out of scope** (§3.5).
4. **Attacks in this file and the run's output are written down in a public repository.** All need database write access and a former member's key, and line 63 of the architecture already states the residual, though not these steps.
