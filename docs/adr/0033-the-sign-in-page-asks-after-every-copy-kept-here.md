# 0033. The sign-in page asks after every copy kept here, whoever's it is

- **Date:** 2026-11-25
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.11

## Context

§4.11 says another user's copy is locked and set aside and their token is never handed in, and [0029](0029-every-contact-checks-the-copys-owner-at-the-slot-port.md) checks the owner on every contact. The sign-in page, online, sends one `GET /api/session` per copy kept in the browser, each with that copy's token in `Kumbu-Mirror`, and removes the copies answered `mirror-wipe` (`sweepRemovedCopies`, `web/src/offline/unlock.ts`). It sends every copy's token, other users' too, which the rule didn't name as an exception.

## Decision

Keep the sweep as it is and name it as the one exception: at the sign-in page, with no one signed in in the tab, the browser may hand in the token of every copy it keeps, each in a request of its own, only to learn whether that copy's slot still exists.

## Why

- It is what removes the copy of a deactivated or removed account, or one removed in the device list, from a shared or returned browser: its owner may never sign in there again, so a sweep limited to the signing-in user would leave it.
- The question carries no session, so no token goes out under another user's session, which is what the rule protects against; the server answers only "still there" or `mirror-wipe` and opens nothing.
- Asking after sign-in for the signing-in user's copy only was weighed and left for the first reason.

## Consequences

The rule of §4.11 now reads "never handed in under a session". A change to the sweep that sends anything more than the token, or sends it while signed in, needs a new record.
