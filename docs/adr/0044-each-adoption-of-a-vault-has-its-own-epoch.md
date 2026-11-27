# 0044. Give each adoption of a vault into the copy its own epoch

- **Date:** 2026-11-27
- **Status:** Accepted
- **Decided by:** Chris, with the fixes of 0.7.0
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.10; extends [0007](0007-a-version-counter-for-sealed-records.md)

## Context

[0007](0007-a-version-counter-for-sealed-records.md) stamps every sealed record of a vault's mirror with a generation, and the vault's head names the current generation of each record, so an older record put back in its place reads as damage. The generations count from zero whenever the vault's mirror starts again: when the vault is taken into the copy anew (re-keyed, unticked and ticked again), and when its head record is missing, which reads as an empty vault. A record of the earlier mirror, still bound to the same vault and path, then matches a generation of the new one and opens as current.

## Decision

Each adoption of a vault into the copy has its own epoch: a random value in the vault's sealed meta, which the head and every entry and state record carry. A record opens only when its epoch is the meta's as well as its generation the head's. A head that is missing or of another epoch while the vault has records is damage: the vault starts a new epoch and is synced afresh, as one just taken in. A tab that finds the head gone while the vault is open counts on from the highest generation it has seen. A copy written before epochs has none in its meta or its records, and reads as before; its next adoption of a vault gives that vault an epoch.

## Why

- The generations make sense only within one life of the vault's mirror; the epoch names that life, so the generations may start again without meeting an earlier life's.
- Bound into the sealed records, the epoch can't be changed without the key, as the generation can't.
- A missing head can't be told from a removed one, and starting from zero would reuse the generations of the records still there; a new epoch keeps the rule of 0007 whatever else is missing.
- Clearing the vault's records at each adoption, which the copy already does, was weighed and lost: it removes what is there at that moment, not a copy of a record put back later.

## Consequences

- A record of the copy is a little larger (the epoch, once per record).
- A vault whose head is missing downloads again; changes on the device that hadn't uploaded were unreadable already, as before.
- The vault's records format gains a field; older copies keep opening, and no migration is needed.
