# 0038. What Kumbu² 1.0 promises

- **Date:** 2026-11-25
- **Status:** Accepted (2026-11-26)
- **Decided by:** Chris, on Claude's draft
- **Spec:** none yet; the roadmap's "What 1.0 promises (semver)"

## Context

Kumbu² is in beta, released weekly; 1.0.0 (end of 2026) leaves the beta. A 1.0 under semantic versioning promises that what people and their agents rely on doesn't break within 1.x. Today nothing is promised: the `sh` syntax, the MCP tools, the REST API and the stored format change when a release needs them to. Old stored forms are read by ad-hoc code where a change needed it (for example, an embedding choice stored before 0.3.0); there is no migration framework.

## Decision

Within 1.x, a minor or patch release does not break:

1. **The vault content**: Markdown notes, attachments, `.obsidian/types.json`, and Kumbu²'s own formats inside notes (frontmatter keys such as `when`, `until`, `task`, the board's, a query's `params:` and `meaning:`, skills' `SKILL.md`). A note written for 1.0 means the same in 1.x. *Why:* this is the person's knowledge; it outlives every server.
2. **The agent surface**: the MCP tools (`sh`, `write`, `edit`, `replace`, `upload`, `board`), their arguments, and the `sh` commands with their options and output shapes as documented in `help`. New commands, options and output lines may come; an existing one keeps its meaning. *Why:* agents and skills are written against it, often by people who never read a changelog.
3. **The upgrade path**: any 1.x server opens the database of any earlier 1.x, and of any release from 0.8.0 on, by upgrading it on start; no 1.x upgrade needs a manual step. There is no downgrade. A test upgrades a 0.8.0 database. *Why:* weekly releases until now meant one step at a time; a 1.x operator may skip several, and 0.8.0 is what testers run.
4. **The sync protocol** the Obsidian plugin and Eidolon speak keeps working with every 1.x server for plugins of 1.0 or later.
5. **MongoDB 8**: every 1.x server runs on MongoDB 8, as well as on any newer major it supports at the time. *Why:* MongoDB 8 is what many installations run, including shared clusters that Kumbu² uses beside other applications; their operators can't move the cluster for one application.

Upgrades run as a migration framework: a numbered, tested list of steps run on start, with the schema's version stored in the database, built before 1.0 as part of the polish. It replaces the ad-hoc readers of old forms. *Why:* an operator who skips several releases gets the steps in a fixed order, and each step is tested once instead of every reader in every store.

A promised surface changes only through deprecation: a 1.x release may announce that something goes, and keeps it working; it goes in 2.0. *Why:* an agent or skill nobody maintains any more must not break in a minor.

Not promised within 1.x: the REST API beyond the sync protocol (the web app's own), the web app's UI, the configuration's defaults (a changed default is named in the notes), and Eidolon's API, which is versioned on its own and stays 0.x until a second host runs on it ([0037](0037-eidolon-is-published-from-the-kumbu-repository.md)). *Why the REST API isn't promised:* opening Kumbu² to integrations belongs to 2.0, together with everything else that expands it; a documented API then is designed for that, not frozen from the web app's. *Why Eidolon waits:* a second host will show what its API has to change, and that is larger work.

## Why

- What a person writes and what an agent calls are what break silently; the UI breaks visibly and is learned again in a minute.
- Promising less and keeping it is worth more than promising the REST API and breaking it.

## Consequences

- A change to a promised surface needs a deprecation path and a note in the release notes, or waits for 2.0.
- Upgrades become tested code with a fixed order, not readers scattered through the stores.
- `help`'s text becomes part of the contract, so its output shapes need tests where they don't have them.
- CI runs against MongoDB 8 as well, where the runner's kernel lets it start (MongoDB 8 and 9 refuse kernels 6.19–7.0.13; the tests use `mongo:7.0` for that reason today).
