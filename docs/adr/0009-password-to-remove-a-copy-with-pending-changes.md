# 0009. Ask for the password before removing another device's copy with changes waiting

- **Date:** 2026-11-23
- **Status:** Accepted (implementation in progress)
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §3a "Settings, Offline", §4.6

## Context

A signed-in session can remove any of the user's offline copies from the device list. Changes not yet uploaded on that device go with it, and they are the one kind of data in Kumbu² with no trash and no history. The alternative was to leave it as it is and only document it.

## Decision

Removing another device's copy asks for the password when that device had changes waiting, or when the count isn't known. Without changes waiting, removal stays a confirmation as before. This is the pattern of deleting a vault.

## Why

- A stolen session alone must not be able to destroy work that can't be recovered.
- A lost device is still cut off at once: its session ends, whatever happens to its copy.

## Consequences

- The removal route takes the password in the cases above and refuses without it.
