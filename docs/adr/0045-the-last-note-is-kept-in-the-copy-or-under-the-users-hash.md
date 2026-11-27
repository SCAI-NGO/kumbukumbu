# 0045. Keep the last note opened in the copy, or under the user's hash

- **Date:** 2026-11-27
- **Status:** Accepted
- **Decided by:** Chris, with the follow-ups of 0.7.0
- **Spec:** [the follow-ups of 0.7.0](../superpowers/specs/2026-11-27-follow-ups-0-7-0-design.md) §2; [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.1

## Context

Since 0.5.0 the app starts on the last note a person opened in an ordinary vault, per device and user, and a session that ends keeps that start for the next sign-in; only signing out, deleting the account and the consent page's sign-out forget it. The path was kept in local storage under the user's id. Everything else the device keeps of a person's notes is sealed in the offline copy, whose only unsealed record, its header, holds the user id as a hash; the start should name no one on the device either.

## Decision

With the user's copy unlocked in the tab, the last note's path is a record of the copy: sealed like the rest, in the copy's own space beside the device's choice of what it keeps offline (`o/`, under the sealed name of `last`), as `{ path }`. It goes with every removal of the copy. Otherwise it is kept in local storage as `kumbu.lastNote.<hash>`, the hash being the one the copy's header holds for the user id (SHA-256 with the store's label, Base64Url), never the id or the name. What local storage holds moves into the copy once it is unlocked; an entry under the user's id, as 0.6.0 kept it, moves to its new place on its first read. The entry under the hash goes wherever that user's copy leaves the device: a wipe, a removal in Settings, Offline, and the sign-in page's sweep, which matches each copy it removes by its header's hash. A session that ends keeps the start, as before.

## Why

- A person's start stays as Chris decided it for 0.5.0, while nothing on the device names whose start it is.
- The header's hash is the one key both sides know: the app knows the user id, and the sign-in page, which knows no user, knows the headers of the copies it removes. A plain hash of the id would leave the sweep nothing to match.
- A record of its own rather than a field of the device's choice keeps the choice's shape and its messages between tabs as they are.
- Forgetting the start at every end of a session would have been simpler, and lost: it takes away what Chris decided the start is for.

## Consequences

- The start is read asynchronously (the hash, and the copy after its opening): the shell waits for it as it waits for the kept note's tree, without showing the vault list on the way.
- The copy gains one sealed record; older copies open as before, and no migration of the copy is needed.
- An entry under a hash that matches no copy on the device stays until its user signs in or out there again.
