# 0008. Keep other devices' offline copies on a password change, and show them

- **Date:** 2026-11-23
- **Status:** Accepted (implementation in progress)
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §3a "Settings, Offline"

## Context

Each device wraps its copy under the password it last signed in with. After a password change, another device's copy still opens offline with the old password until that device signs in online again. The strict alternative was to remove every other copy on a password change, as "Sign out other sessions" does.

## Decision

A password change leaves the other devices' copies in place. Right after the change, the app shows the devices that keep a copy, each with **Remove offline copy**, and the help explains that a copy opens with the old password until its device is next online.

## Why

- Removing the copies would throw away changes not yet uploaded on the person's own devices, the one kind of data in Kumbu² with no trash and no history.
- The person who changes the password because a device is lost can remove that copy in the same place, one tap away.

## Consequences

- The security architecture states that an old password keeps opening an offline copy until its device is online.
- Removing another device's copy with changes waiting asks for the password: [0009](0009-password-to-remove-a-copy-with-pending-changes.md).
