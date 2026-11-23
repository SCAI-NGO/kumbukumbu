# 0010. Keep embeddings on the server; offline searches by words

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §7

## Context

Search by meaning needs an embedding model and the vault's vectors. The built-in models run on the server. One idea was to call an external provider (TensorX) from the client while offline.

## Decision

Built-in embeddings stay on the server. Offline, search runs by words only, in the browser (MiniSearch over the unlocked copy), and says so in the search sheet's notice line. What is written offline is embedded by the server when it uploads, as every write is. No provider is called from the client in 0.7.0.

## Why

- Offline usually means no network at all, so an external provider is out of reach as well.
- Searching by meaning in the browser would need the vectors there too: tens of megabytes per large vault, all derived from the plaintext and to be sealed.
- A client-side provider call needs either a key in the browser or a server that mints tokens, which is down in exactly the case this is for.

## Consequences

- Search by meaning, similar notes and link suggestions say "needs a connection" offline.
- A local embedding engine is a candidate once Eidolon is a library (1.0.0).
