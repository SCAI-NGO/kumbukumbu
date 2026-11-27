# 0042. Removing another device's copy always asks for the password

- **Date:** 2026-11-26
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §3a "Settings, Offline", §4.4, §4.6

## Context

[0009](0009-password-to-remove-a-copy-with-pending-changes.md) asked for the password before removing another device's offline copy only when that device's last contact reported changes waiting, or reported no count; a copy whose last contact said nothing was waiting went with a confirmation, and "End other sessions" took those copies with the sessions. The count is what the device said when it was last online: whatever it wrote offline since, the server can't know.

## Decision

Removing any offline copy that isn't this browser's own asks for the password, whatever its last contact said. "End other sessions" ends the sessions and leaves every other device's copy in place; it goes with "Remove offline copy" and the password. The removal dialog always says that changes not uploaded on that device are lost. This browser removes its own copy without the password, having said what goes with it.

## Why

- One condition less: the server decides by whose copy it is, not by a count that may be out of date by the time of the removal. Changes not uploaded are the one kind of data in Kumbu² with no trash and no history, so the removal that may take them is the one that asks.
- Leaving the copies in place costs little: a copy whose session has ended opens only with the password anyway, and a lost device is still cut off at once, as its session ends.
- The option weighed against it, keeping the count and asking only where it isn't 0, keeps a rule that the person can't follow from the device list.

## Consequences

- `DELETE /api/account/mirror-slots/{id}` removes this browser's own copy; another device's answers 400 `changes-waiting` and goes through `POST …/{id}/remove` with the password.
- After "End other sessions", the other devices' copies stay in the device list until they are removed there; each device, signed out, keeps its copy sealed until it signs in again.
- Supersedes [0009](0009-password-to-remove-a-copy-with-pending-changes.md).
