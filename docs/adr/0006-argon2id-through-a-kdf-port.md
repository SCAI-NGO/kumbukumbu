# 0006. Derive the offline wrap with Argon2id, through a KDF port

- **Date:** 2026-11-23
- **Status:** Accepted (implementation in progress)
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.2, which still says PBKDF2

## Context

The spec chose PBKDF2-SHA-256 from WebCrypto at about 1,000,000 iterations, because Argon2id needs WebAssembly and the content security policy allowed none. Someone who copies the disk can guess the password against the wrap offline, on GPUs, as fast as the derivation allows. The alternative on the table was to keep PBKDF2 for 0.7.0 and switch in 0.8.0.

## Decision

The password wrap uses Argon2id from 0.7.0. The Eidolon core defines a KDF port and records the function and its parameters in the copy's header (a `kdf` field); it contains no Argon2id itself. The web app brings the implementation, as WebAssembly under a permissive licence, and fills the port.

## Why

- A memory-hard function is much stronger against GPU guessing than PBKDF2, and the offline copy is exactly the case of an attacker holding the disk.
- Done now, no copy is ever written in the weaker format, so no migration of existing wraps is needed later.
- Keeping the implementation in the host keeps the core runtime-agnostic and its licence clean for the LGPL extraction ([0002](0002-eidolon-core-as-a-host-agnostic-package.md)); another host can fill the port natively.
- The `kdf` field lets the parameters, or the function, change later without breaking the format.

## Consequences

- The content security policy has to allow WebAssembly (`'wasm-unsafe-eval'`) for the web app, a widening that `docs/security-architecture.md` records with its reason.
- The parameters are tuned to a time a person accepts at sign-in on a mid-range phone, as PBKDF2's count was, and kept in the header so they can be raised.
