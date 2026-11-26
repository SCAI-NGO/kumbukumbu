# 0038. What Kumbu² 1.0 promises

- **Date:** 2026-11-25
- **Status:** Proposed
- **Decided by:** drafted by Claude for Chris; the open questions below are his
- **Spec:** none yet; the roadmap's "What 1.0 promises (semver)"

## Context

Kumbu² is in beta, released weekly; 1.0.0 (end of 2026) leaves the beta. A 1.0 under semantic versioning promises that what people and their agents rely on doesn't break within 1.x. Today nothing is promised: the `sh` syntax, the MCP tools, the REST API and the stored format change when a release needs them to. Old stored forms are read by ad-hoc code where a change needed it (for example, an embedding choice stored before 0.3.0); there is no migration framework.

## Decision (proposed)

Within 1.x, a minor or patch release does not break:

1. **The vault content**: Markdown notes, attachments, `.obsidian/types.json`, and Kumbu²'s own formats inside notes (frontmatter keys such as `when`, `until`, `task`, the board's, a query's `params:` and `meaning:`, skills' `SKILL.md`). A note written for 1.0 means the same in 1.x. *Why:* this is the person's knowledge; it outlives every server.
2. **The agent surface**: the MCP tools (`sh`, `write`, `edit`, `replace`, `upload`, `board`), their arguments, and the `sh` commands with their options and output shapes as documented in `help`. New commands, options and output lines may come; an existing one keeps its meaning. *Why:* agents and skills are written against it, often by people who never read a changelog.
3. **The upgrade path**: any 1.x server opens the database of any earlier 1.x, and of 0.9 or the last 0.x named in the 1.0 notes, by upgrading it on start; no 1.x upgrade needs a manual step. There is no downgrade. *Why:* weekly releases until now meant one step at a time; a 1.x operator may skip several.
4. **The sync protocol** the Obsidian plugin and Eidolon speak keeps working with every 1.x server for plugins of 1.0 or later.

Not promised within 1.x: the REST API beyond the sync protocol (the web app's own), the web app's UI, the configuration's defaults (a changed default is named in the notes), and Eidolon's API, which is versioned on its own ([0037](0037-eidolon-is-published-from-the-kumbu-repository.md)).

## Open questions for Chris

1. **The REST API:** keep it unpromised (the web app's, free to change), or promise a documented subset for scripts and integrations? Recommendation: unpromised in 1.0, a documented subset in a later minor if someone asks.
2. **Deprecation:** may a 1.x minor remove a `sh` option after announcing it a minor earlier, or only 2.0? Recommendation: only 2.0; announce in 1.x, remove in 2.0.
3. **Oldest database a 1.x upgrades from:** any 0.x, or only the last 0.x (0.9.x)? Recommendation: from 0.8.0 on, since that is what testers will run, with a test that upgrades a 0.8.0 database.
4. **A migration framework:** a numbered, tested list of upgrade steps run on start (a schema version in the database), instead of ad-hoc readers of old forms? Recommendation: yes, before 1.0, as part of polish.
5. **Eidolon at 1.0:** stays 0.x until a second host runs on it (0037), or 1.0 together with Kumbu²?

## Why (so far)

- What a person writes and what an agent calls are what break silently; the UI breaks visibly and is learned again in a minute.
- Promising less and keeping it is worth more than promising the REST API and breaking it.

## Consequences

- A change to a promised surface needs a deprecation path and a note in the release notes, or waits for 2.0.
- Upgrades become tested code with a fixed order, not readers scattered through the stores.
- `help`'s text becomes part of the contract, so its output shapes need tests where they don't have them.
