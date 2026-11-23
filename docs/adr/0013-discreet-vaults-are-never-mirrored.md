# 0013. Never keep a discreet vault offline

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §4.3, §3a "Purges and discreet vaults"

## Context

The discreet vaults design has as its anti-goal a hidden vault's name or content reaching any browser storage.

## Decision

Discreet vaults are never mirrored, revealed or not. A vault marked discreet on another device is purged here at the next contact, and the purge notice names no vault.

## Why

- The anti-goal taken literally: a sealed copy is still a trace on the disk.
- Naming the purged vault would put a just-hidden vault's name on screen.

## Consequences

- An open discreet note stays readable when the connection drops, but can't be written; its unsaved text stays in memory only.
