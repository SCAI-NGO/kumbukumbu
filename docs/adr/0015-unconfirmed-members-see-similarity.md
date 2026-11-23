# 0015. Show similarity to vault members who haven't confirmed the embedding provider

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [graph view out of preview](../superpowers/specs/2026-11-22-graph-out-of-preview-design.md)

## Context

A shared vault may use an external embedding provider, which each member confirms before their searches are sent to it. The graph's edges by meaning and the similar-note suggestions compare vectors already stored on the server.

## Decision

Members who haven't confirmed the provider still see similarity scores and suggestions.

## Why

- Comparing stored vectors sends no text and no query to the provider; the confirmation is about what leaves the server, and nothing does here.

## Consequences

- A search by meaning, which embeds the query, still needs the confirmation.
