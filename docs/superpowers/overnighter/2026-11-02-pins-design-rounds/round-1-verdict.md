# Round 1 verdict: not attacked

Written 2026-11-02 ~20:10 UTC.

**There are no findings to judge.** Two fresh attackers, each given the brief's prompt unchanged, were stopped by a safety classifier while writing their step-by-step attacks, and returned nothing (`round-1-attack-attempt-1.md`, `round-1-attack.md`). Round 1 therefore doesn't count as a round: the candidate has **not been attacked independently**.

## Why I stopped here instead of starting more attackers

- The same prompt failed twice in the same way, at the same stage (after reading, while writing the attacks). A third run with the same prompt would only be hoping for a different outcome.
- Changing the prompt's wording so that the attack write-up gets past the classifier would mean working around a safety system. I won't do that, and the brief also asks that the prompt be used as it stands.
- The brief's rule for a run without independent attackers applies in effect: go on as far as I can, label every candidate "not attacked independently", and say so first in the report. A review I write myself doesn't count as a round.

## The author's own review (does not count as an attack)

So that the maintainer gets something to start from, these are the weak points I know of in candidate 1. I found them while writing it (`round-1-notes.md`). Nobody independent has confirmed or added to them.

| # | Weak point | My estimate |
|---|---|---|
| S1 | **Revival within one vault-key epoch.** A membership that left, or was revoked without a re-key, can be set back to `active`; its `grant` is genuine and its holder knows M_X, so it appears ticked among the candidates. Only the owner's eyes on the label stop it, except for the membership revoked in the same request. This covers A2.1 for a leaver, A3.2 and A9.2 ("revoked, re-invited, revoked again"). | High: G1 under a realistic extra condition (the owner doesn't untick him) |
| S2 | **A3.5, the whole K era put back** after the finish: payload-v2 user slot with K, meta and documents. Nothing in MongoDB tells it from the present; the old-key guard is process memory (`VaultCatalog.Rekey.cs:60–71`, `:110–116`, a vault never loaded counts as current). | Critical as a residual of every database-only design; needs state outside MongoDB |
| S3 | **Migration blesses** whatever K-valid legacy pins exist at the vault's first re-key (A4.2, A4.3), and their labels are vault-key-sealed, so forgeable (A8.1). | High residual for that first re-key |
| S4 | **A leaver who deletes the event** stays, for the owner, a member; he is in her list as one. | As S1 |
| S5 | **Pin rollback** (A3.3): an older `pin2` of Bob verifies, the finish wraps to his old public key, Bob is locked out. | Medium, as the task allows |
| S6 | **Rotation-driven re-keys of shared vaults wait for the owner's choice.** Priced, owner presence. | Cost, not a finding |

## What comes next

No more rounds. I write the design as options with the anchor argument, label all of it "not attacked independently", and put the attackers' failure first in the report.
