# 0023. Queries and mutations run whatever the browser says about the network

- **Date:** 2026-11-23
- **Status:** Proposed
- **Decided by:** Claude, for Chris's review
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.12

## Context

React Query holds back queries and mutations while `navigator.onLine` is false (its `online` network mode): they stay pending until the browser says it is online again. With the offline mode, a query answered from the copy on this device then never ran: after a reload without a network the app stayed blank, and a new note or a trashed one waited forever.

## Decision

The app's `QueryClient` runs every query and mutation at once (`networkMode: "always"`). The offline mode decides where each goes: to the copy while offline, to the server otherwise. The save already did this for its own reasons (`useSaveNote`).

## Why

- The offline mode, not the browser's flag, knows whether the copy can answer (design §3a: the flag is a hint).
- Rejected: `offlineFirst`, which runs a query once and then holds its retries: the session's retries would still hold the shell back.

## Consequences

- Without a copy, a screen offline shows its error at once ("The server can't be reached …") rather than a spinner that never ends.
- A query that fails offline is retried by React Query's own rules, like any failed request.
