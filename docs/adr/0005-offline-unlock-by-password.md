# 0005. Unlock the offline copy with the password, never a PIN

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §3a "Unlocking", §4.2

## Context

After a reload offline, the copy's key has to come from somewhere the server can't supply. A PIN would be quicker to type.

## Decision

The copy's key is wrapped under a key derived from the account password. Offline, the usual sign-in form (username and password) opens the copy; there is no unlock screen of its own. The unlock ends like a session, after the same idle limit.

## Why

- A PIN of 4 to 6 digits is guessed in seconds against a copied disk; the password is the one secret strong enough.
- One habit, and password managers fill the form as always.
- The username is needed anyway: it finds the right copy when two people share a browser.

## Consequences

- The copy is re-wrapped at every sign-in, so a password change needs nothing but the next sign-in on that device; see [0008](0008-password-change-keeps-other-devices-copies.md) for the other devices.
- A wrong password costs the key derivation's time, which is the throttle; there is no lockout counter.
