# 0016. Ship synthesize-knowledge as a system skill

- **Date:** 2026-11-22
- **Status:** Accepted
- **Decided by:** Chris
- **Spec:** [knowledge synthesis](../superpowers/specs/2026-11-22-knowledge-synthesis-design.md) §3.9

## Context

The `synthesize-knowledge` skill walks an agent through Swanson-style bridge and explore runs over `similar`. It could first be tried as a user skill on one instance.

## Decision

It ships in 0.7.0 as a system skill, in `src/Kumbu.Core/Skills/System/synthesize-knowledge/`, with a hand-test list.

## Why

- The 0.7.0 test rounds then take it in as the system skill it will be, rather than testing something else first.

## Consequences

- Changes to it ship with releases, like the other system skills.
