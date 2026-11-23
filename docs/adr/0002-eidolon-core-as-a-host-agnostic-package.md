# 0002. Build the client's data layer as the Eidolon core, a host-agnostic package

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §2a

## Context

Offline mode needs a data layer in the browser: sync, an encrypted store, a local index, the VAFS-shaped tools. The Obsidian plugin already has a sync engine, and 1.0.0 is to take this core out of Kumbu² as a library under the LGPL, for Chatsundere's C2Duress.

## Decision

Everything the client keeps and does with notes lives in `packages/eidolon-core`, a pnpm workspace package beside `web/`. It imports nothing of React, `web/` or Obsidian, sees no DOM types, and reaches the host only through ports (storage, crypto, HTTP, clock, socket, KDF). A Kumbu²-only call goes through a port the host fills. Its dependencies are under permissive licences only. The plugin vendors the sync part, pinned by a full commit hash in its `eidolon-core.json`.

## Why

- A clean boundary now is cheaper than untangling the core from the web app at the extraction.
- One engine for the web app and the plugin: one sync behaviour, one set of race tests.
- Relicensing to the LGPL later needs the copyright holder and no third-party AGPL or GPL code inside the package; permissive dependencies keep that possible.
- Publishing to npm would also share the engine, but is an outward step for a package that isn't public yet.

## Consequences

- The boundary is checked by a lint rule and the package's own build.
- Sync changes are made here first; the plugin's copy then moves to the new commit.
- Anything that needs a specific runtime (WebAssembly, IndexedDB, WebCrypto) is the host's, behind a port: see [0006](0006-argon2id-through-a-kdf-port.md).
