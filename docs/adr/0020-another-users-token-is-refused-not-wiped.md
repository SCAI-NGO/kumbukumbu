# 0020. Answer a contact with another user's token with a refusal, not a wipe

- **Date:** 2026-11-23
- **Status:** Proposed
- **Decided by:** proposed with the fix, from the review of the offline mode; Chris confirms in review
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.6

## Context

A browser's contact hands in its slot's token. The server looked the slot up by the signed-in user and the token, so a token of another user's slot found nothing, and the answer was the `404` with `mirror-wipe`, the same as for a slot that was removed. A browser hands in another user's token when someone else signs in over a tab that held a copy; the wipe then destroyed the first user's changes that hadn't uploaded.

## Decision

Where the signed-in user has no slot of the token but a slot of it exists, the contact answers `403` without a code. The browser keeps that copy and doesn't open it for this session. Only a token whose slot doesn't exist at all is answered `mirror-wipe`.

## Why

- The copy isn't gone: its owner can still sign in and open it. "Not yours" and "removed" are different answers.
- It matches the `401` rule, which already wipes only when no slot of the token exists, whoever's.
- The token is 256 random bits, so telling the two cases apart tells nothing to someone who doesn't hold it.

## Consequences

- The web app's own fix (the copy's life follows the session's) is the main protection; this is the second line behind it.
