# 0003. Treat the browser's copy exactly as an Obsidian device

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §2

## Context

Writing offline means two devices can change the same note. The choices were last write wins, a conflict copy, or merging. The server already speaks a sync protocol to the Obsidian plugin, with conflict copies, and accepts web sessions on it.

## Decision

The browser's local data is handled as an Obsidian vault client's: the existing sync API, the plugin's engine and decision table, and a conflict copy (`<name> (conflict <device> <date> <time>).md` with its callout) when both sides changed a note. No new server endpoint for notes.

The browser signs in with its session, never with a device key: it does not pair itself.

## Why

- One behaviour to build, test and explain, and a much smaller project.
- A conflict copy loses nothing and leaves the person the most agency; power users report conflicts as rare, so the copy is no nuisance.
- A long-lived bearer key in storage that JavaScript can read is exactly what a cross-site script would take. The session cookie is `HttpOnly` and `SameSite=Strict`.

## Consequences

- A session ends (idle and absolute limits), so a long time offline usually comes back to a sign-in before the changes upload; they wait in the copy until then.
- The web app runs one engine per mirrored vault, keyed by vault id, since a session holds many vaults where a device key holds one.
