# 0001. Record architecture decisions

- **Date:** 2026-11-23
- **Status:** Accepted
- **Decided by:** Chris

## Context

Kumbu²'s decisions live in its specs (`docs/superpowers/specs/`), next to the design they shape, each with its reason. That works while a spec is being written. It works less well later: a decision taken in a review or in a conversation, after the spec was merged, lands in a section titled "built" or nowhere at all, and a decision that spans several specs (the Eidolon core, the offline copy's security) has no one place. The weekend of 2026-11-22 alone produced some twenty decisions for 0.7.0, several of them changing what a spec had said a day before.

## Decision

Every decision that shapes the architecture, the security model, the data format or a lasting behaviour gets an Architecture Decision Record in `docs/adr/`, numbered, in the shape of `template.md`, in the same pull request as the change it decides, or before it when the change comes later. A decision that changes an earlier one gets a new record, and the earlier one is marked superseded; records are not rewritten.

Specs keep their decisions and reasons as before. The record is the short, findable version, and links to the spec for the detail.

## Why

- One place to answer "why is it like this?", without reading every spec that touched the subject.
- Superseding instead of editing keeps the history of a decision readable: what we thought, and when it changed.
- Short records cost little to write while the decision is fresh, which is when the reason is still known.

## Consequences

- Reviews check that a pull request with a lasting decision carries its record.
- `docs/adr/README.md` lists the records; a new one is added there.
- The records are public like the rest of the repository: a decision about security says what we do and why, never which confidential review finding led to it.
