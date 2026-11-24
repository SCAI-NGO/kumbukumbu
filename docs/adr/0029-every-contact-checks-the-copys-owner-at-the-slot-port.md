# 0029. Every contact checks the copy's owner at the slot port, just before it goes out

- **Date:** 2026-11-24
- **Status:** Proposed
- **Decided by:** proposed with the fix of the 0.7.0 test run's finding F-10; Chris confirms in review
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.11

## Context

§4.11 says another user's copy is locked and set aside and their token is never handed in; [0020](0020-another-users-token-is-refused-not-wiped.md) calls the client's side the main protection. The web app compared the copy with the signed-in user in some of the paths that make contact (the socket's contact), before an await, and not in others (the opening at a page load or sign-in, which contacts twice). A copy still opening when one user signed out and another signed in in the same tab handed in the first user's token under the second's session, and was even kept open for them.

## Decision

Every contact with a mirror slot passes one check in the browser's slot port (`signedInSlots`, `web/src/offline/slots.ts`): it goes out only with the token of the signed-in user's copy, looked at after the copy lookup and immediately before the request, with no await between the last look and the fetch. Anything else is refused as `AuthError`, which every caller already treats as "signed out: nothing changes". Opening a copy for one user no longer joins another user's opening still under way, and a copy whose user is no longer the signed-in one when it has opened is locked, not kept.

## Why

- One place every path goes through can't be missed by a path added later; checks at each caller were, and each had an await between the check and the request.
- The port is where the token leaves the tab, so the check sees the session as it is when the request is made, not as it was when the contact started.
- `AuthError` changes nothing for the callers: a contact under no session already ended that way.
- Checking in the Eidolon core instead would need the signed-in user in the core, which knows no session; the core's sync part, which the Obsidian plugin vendors, is untouched.

## Consequences

- The check reads the tab's session store, not the cookie: between a sign-in's answer and the store's update the store says "signed out", so it errs towards refusing. A sign-in in another tab of the same browser changes the cookie without this tab's store hearing of it; the server's `403` (0020) stays the line behind that.
- Each contact reads the copies' headers from IndexedDB once more, a cheap read.
