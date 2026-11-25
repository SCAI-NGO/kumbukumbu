# 0029. Every contact checks the copy's owner at the slot port, just before it goes out

- **Date:** 2026-11-24
- **Status:** Accepted
- **Decided by:** proposed with the fix of the 0.7.0 test run's finding F-10; accepted by Chris on 2026-11-25, to be looked at again once real use shows how it is received
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.11

## Context

§4.11 says another user's copy is locked and set aside and their token is never handed in; [0020](0020-another-users-token-is-refused-not-wiped.md) calls the client's side the main protection. The web app compared the copy with the signed-in user in some of the paths that make contact (the socket's contact), before an await, and not in others (the opening at a page load or sign-in, which contacts twice). A copy still opening when one user signed out and another signed in in the same tab handed in the first user's token under the second's session, and was even kept open for them.

## Decision

Every contact with a mirror slot passes one check in the browser's slot port (`signedInSlots`, `web/src/offline/slots.ts`): it goes out only with the token of the copy of the user this tab has signed in, looked at after the copy lookup and immediately before the request, with no await between the last look and the fetch. Anything else is refused as `AuthError`, which every caller already treats as "signed out: nothing changes". Turning offline on asks for a slot only while its user is the one signed in in the tab, looked at just before the request. Opening a copy for one user no longer joins another user's opening still under way, and a copy whose user is no longer the signed-in one when it has opened (or been made) is locked, not kept; one found gone then is removed from the device without ending the new user's session.

What this guarantees is within the tab: the check reads this tab's session, so no sign-out and sign-in in this tab lets one user's token go out under another's session. A sign-in in another tab of the same browser changes the cookie without this tab hearing of it; the server's `403` ([0020](0020-another-users-token-is-refused-not-wiped.md)) covers that case.

## Why

- One place the contacts go through can't be missed by a contact added later; checks at each caller were, and each had an await between the check and the request.
- The port is where the token leaves the tab, so the check sees the session as it is when the request is made, not as it was when the contact started.
- `AuthError` changes nothing for the callers: a contact under no session already ended that way.
- Checking in the Eidolon core instead would need the signed-in user in the core, which knows no session; the core's sync part, which the Obsidian plugin vendors, is untouched.

## Consequences

- The check reads the tab's session store, not the cookie: between a sign-in's answer and the store's update the store says "signed out", so within the tab it errs towards refusing.
- Each contact reads the copies' headers from IndexedDB once more, a cheap read.
