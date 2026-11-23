# 0024. The browser's "offline" closes the sync socket

- **Date:** 2026-11-23
- **Status:** Proposed
- **Decided by:** Claude, for Chris's review
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.11, §4.12

## Context

The mode goes offline only after a failed request with the socket down (§4.11). When the network goes, an open WebSocket may not hear of it for minutes: no packet says so. A tab that was only reading stayed online, and the screens kept asking the server.

## Decision

The browser's `offline` event closes the sync socket at once, and counts as a failed request, as before. A page started without a network (`navigator.onLine` false) counts as a failed request from the start, so the sign-in page offline says what it can open.

## Why

- Without a network the socket is dead in fact; closing it says so sooner, and the socket's own back-off brings it back once the network is.
- The mode's rule stays as it is: the flag never switches to online, and a request reaching Kumbu² still decides that.

## Consequences

- A network that drops for a moment closes and reopens the socket, as a server restart does; hints sent meanwhile are asked again on reconnect, as before.
