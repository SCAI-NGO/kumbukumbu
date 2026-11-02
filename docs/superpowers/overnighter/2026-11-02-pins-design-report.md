# Overnighter report: pins a former member can't forge

**First: nothing in this run was attacked independently.** Both fresh attackers were given the brief's prompt unchanged, and both were stopped by a safety classifier while writing their step-by-step attacks. Neither returned a finding. No round counts. Every candidate and option is labelled "not attacked independently". I didn't reword the prompt to get past the classifier, and I stopped rounds after the second failure. That is fewer than the three rounds the brief asks for, and it is said here first.

Design: [specs/2026-11-02-pins-design.md](../specs/2026-11-02-pins-design.md). Draft pull request: https://github.com/SCAI-NGO/kumbukumbu/pull/137

## Environment

- Cloud session, Linux. `git`, `rg` and `gh` were present. Nothing was installed, and no .NET, Node or Docker was run.
- Start: 2026-11-02 19:51:21 UTC, from `715260a` ([round 0](2026-11-02-pins-design-rounds/round-0-start.md)). End: about 20:12 UTC.

## Push and branch

The session was assigned `claude/pins-design-brief-k1dpkr`. The brief names `feature/pins-design` and gives the maintainer's permission, so everything went there. All pushes succeeded, and nothing was forced.

## Rounds

| Round | Commit | Candidate in two lines | Attacker's findings | Verdict |
|---|---|---|---|---|
| 0 | `0be1fbd` | start record | — | — |
| 1 | `c8da810` (candidate), records up to the verdict commit | Per-membership key M_X made under the owner's user key, given to the member through the link, authenticates `pin2`. A re-key carries only the roster the owner ticks, sealed with K′ | **none**: attempt 1 and attempt 2 both stopped by a safety classifier | Not attacked; 0 accepted, 0 rejected |

Files: [candidate](2026-11-02-pins-design-rounds/round-1-candidate.md), [notes](2026-11-02-pins-design-rounds/round-1-notes.md), [attempt 1](2026-11-02-pins-design-rounds/round-1-attack-attempt-1.md), [attack](2026-11-02-pins-design-rounds/round-1-attack.md), [verdict](2026-11-02-pins-design-rounds/round-1-verdict.md).

### Rejected candidates

| Candidate | What broke it |
|---|---|
| — | No candidate was rejected: none was attacked. Round 1's candidate stands only as unreviewed Option A |

### Rejected findings

None: there were none to judge.

## Each attack of §4

The outcomes are the author's analysis only, in the design's §3.2 table. In brief, under Option A:

- **Stopped:** A0, A1.1–A1.4, A2.4, A4.1 (after the first re-key), A5.1–A5.3, A6.1, A6.3, A7.1–A7.3, A9.1, A10, A11.
- **Residual:** A2.1–A2.3, A3.1, A3.2 and A9.2 within one key epoch (the owner's unticking is the only defence), A3.3 (lock-out, Medium), A3.5 (the main residual), A4.2 and A4.3 (the first re-key), A8.2 (departures).
- **Partly:** A6.2 (one bad row can't stall; forced retries can, as today), A8.1, A8.3.

Option B closes the within-epoch residuals and A3.5 in the main model, but not in the at-rest variant.

## Decided myself, and deviations from the brief

- **Stopped at one (unattacked) round** instead of three. The brief's "if you get stuck" rule applies.
- **Didn't reword the attacker's prompt** after the classifier stopped it.
- Wrote Option B, the ledger, straight into the design without a round. It is labelled as never shown to an attacker.
- In Option A: the list's labels come from `grant`, not from the vault-key-sealed `label`. `keep` lives only in the runner's memory. A finish skips a damaged member instead of stalling.

## Proposed changes to the threat model

- **Departures the owner never learned of.** A member who leaves and deletes the event is, to the owner, still a member. Within the database no channel can tell her otherwise (A8.2). Proposal: either the promise counts a departure as ended only once the owner has seen it or a re-key has carried it, or the model accepts that only state outside MongoDB (Option B) can make it durable. Under the model as written it is a residual.
- **A3.5** (task §7, assumption 2) can't be closed by any database-only design. Leaving it to the separate rollback item of architecture §9 would be honest, unless Option B is chosen.

## Surprises

1. **The safety classifier** stopped both attackers on a defensive design review of this repository. Future runs of this kind need a reviewer setup that can write attack steps, or a human reviewer.
2. **A3.5 is real as the code reads.** The guard against an old vault key is process memory: `_keyIds`, `_rekeyed`, and "a vault never loaded counts as current" (`src/Kumbu.Server/Vaults/VaultCatalog.Rekey.cs:60–71`, `:110–116`). After a restart, a database restored to the K era loads with K. `docs/security-architecture.md` §2 "Rollback" names rollback in general, but not that it undoes a re-encryption.
3. **Derived from reading, not tested: a planted user slot.** Someone who holds K and can write the database can insert a `user` slot for his own account that wraps K. `OpenForUserAsync` would then list the vault as his own (`src/Kumbu.Server/Vaults/VaultCatalog.cs:205–243`, the user-slot branch of `OpenUserSlot` at `:252`). That needs exactly the task's attacker, and line 63 already covers what K opens. But "owned" is a stronger position than "member", for example for starting a re-key on a vault he doesn't own. A re-key by the real owner puts that slot out of use. Worth a test and a sentence in architecture §4.

## Open points for Chris, the decisions first

1. **The independent review.** None was had. *Recommendation:* a human reviewer, or a reviewer setup that allows attack write-ups, on the design's Options A and B before anything is built.
2. **Which option.** *Recommendation:* build A's authenticity layer and decide on B. A closes the classes the earlier attempts failed on. B is the only route to durable revocations and to A3.5.
3. **Owner's list at every shared re-key start**, with rotation-driven re-keys of shared vaults waiting for it. *Recommendation:* accept it with A. It could be dropped with B.
4. **Departures** (above). *Recommendation:* decide together with option B.
5. **Size.** A takes two to three weeks, not one. *Recommendation:* if 0.3.1 must ship within a week, ship Option C's honest wording and build A for a later release.
6. **The planted user slot** (Surprise 3). *Recommendation:* confirm with a test, then document it.

## Pull request

https://github.com/SCAI-NGO/kumbukumbu/pull/137 (draft)
