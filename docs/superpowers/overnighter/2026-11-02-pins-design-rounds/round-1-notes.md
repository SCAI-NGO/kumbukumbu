# Round 1 notes (the author's reasoning; not shown to the attacker)

Started 2026-11-02 20:00 UTC.

## Where the problem really sits

Three separate questions hide in "pins a former member can't forge":

1. **Which key per membership** (A0, A5, A7). Needs an authenticator Mallory can't make: a key shared by the owner and that member only. K is shared with every member, so it can't be the key. The owner's user key is at hand at invitation and grant; the member's user key at acceptance and rotation. A random per-membership key M_X, made by the owner and handed to the redeemer through the link (whose secret Mallory doesn't have for anyone else's invitation), bridges the two: the owner has it under U, the member under her own user key, and the acceptance and rotation can MAC pins under it with the owner absent.
2. **Which memberships** (A1, A2). M_X's owner-side copy can only be made with U, so a row Mallory inserts has none (A1.1, A1.2, A1.3, A1.4 stopped by construction). But his **own** membership has a genuine owner-side copy, and he knows M_X. So the set must exclude him by something other than the database.
3. **Freshness** (A2.1, A2.4, A3.x). Inside the database, every record of a revocation is older genuine state away from being undone: the owner's user key does not change at a revocation, so whatever she sealed before it still opens after it. With a consistent snapshot assumed (A3.2), **nothing inside MongoDB can tell the state just before a revocation from the state just after it**, unless a key that only one of the two states can use came into being in between. In a re-key, K′ is that key. So the only place a revocation becomes durable against this attacker is the re-key's start, and the only source for the start that he can't write is the owner's request.

Hence the roster: chosen in the owner's request, sealed with K′ under U in `user-next`, and from the finish on in the owner's user slot. An older roster is bound to an older key (`from`), so a swapped `user-next` from an earlier run is refused.

## What I expect to stand and to fall

- Expected to stand: A0, A1.x, A5.x, A6.1, A6.3, A7.1, A10, A11; A9.1.
- Residual by construction: A3.5 (whole K era restored, including the owner's user slot payload v2 with K: the database alone can't know K is old; it needs state outside MongoDB). I didn't put that in the candidate as a mechanism: it's a stated residual or an option for the next round.
- **Weak spot I know of:** within one vault-key epoch, a membership that was revoked or left can be revived, and appears ticked in the owner's list (candidate kinds 1 and 2). In the revoke-and-re-encrypt dialog the revoked one is excluded by the request itself, but an earlier departure or revocation without re-key comes back (A9.2 "revoked, re-invited, revoked again"). The defence is the owner's eyes on genuine labels, which A8.3 says is weak. I want to see whether the attacker rates this High.
- **Migration** (kind 3) blesses whatever K-valid pins exist at the first re-key: A4.2 and A4.3 are residuals, mitigated only by the list being shown apart.
- **Leaving** is invisible to the owner if Mallory deletes the event: he looks like a current member to her. A threat-model question for the report.
- Pin rollback (A3.3) locks the member out; residual Medium as the task allows.
- A6.2: chose "skip the bad member" so one row can't stall the finish.

## Decided myself

- Labels in the dialog come from `grant`, not the vault-key-sealed `label`.
- `keep` lives only in memory; lost at a restart, the vault waits for the owner again.
- Rotation-driven re-keys of shared vaults wait for the owner's `keep` instead of starting in the runner.
