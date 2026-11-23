# 0017. Deactivating a user removes their offline copies

- **Date:** 2026-11-23
- **Status:** Proposed
- **Decided by:** proposed with the fix, from the review of the offline mode; Chris confirms in review
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.4

## Context

A browser purges its offline copy when it learns that its mirror slot is gone: at a contact, or from the `401` of a request that hands in the slot's token. The slots went with a removal from the device list, "End other sessions", a re-encryption and the account's deletion, but not with a deactivation. A deactivated user's browsers were signed out, yet told nothing: every copy, shared vaults included, kept opening offline with the password, however often the device came online. Deactivation is how an admin cuts off a departing user, so the promise failed in its main case.

## Decision

Deactivating a user deletes all their mirror slots, in the same transaction that sets the flag, ends their sessions and revokes their keys. Their browsers are told to purge at their next request, signed in or not. Reactivating brings no slot back; the user's next sign-in on a device starts a new copy. The admin's confirmation says that offline copies go, with changes not uploaded yet.

## Why

- It is what a deactivation already does to every other credential of the user; an offline copy is one more.
- Keeping the slots until a deletion would leave the copies open for as long as an admin hesitates to delete the account, which may be for good.
- Waiting for the reactivation to decide can't work: by then the browsers have been online and signed out with their copies intact.

## Consequences

- A deactivation can't be fully undone: changes a device hadn't uploaded are lost with its copy, and the confirmation says so.
- A device that never comes online again keeps its sealed copy, as for every other wipe signal.
