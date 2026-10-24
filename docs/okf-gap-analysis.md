# OKF gap analysis

Written on Oct 24, 2026, after phase 6, against [OKF v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md) (Open Knowledge Format, Google Cloud, Apache 2.0). Section numbers `OKF §n` refer to the spec.

**Outcome (decided by Chris):** OKF becomes a v2 feature, **opt-in per directory**. Directories without the flag stay plain Obsidian folders; strict rules would annoy most Obsidian users and don't fit every use. Two groundworks go into v1 (below). Open design questions are in `concept.md` §13.

## What OKF is

A knowledge bundle is a directory tree of markdown files with YAML frontmatter. Each file is a *concept*; its id is its path without `.md`. The only required field is `type`. Optional families add provenance (`sources`, per-claim footnotes), trust (`generated`, `verified`, derived trust tiers), lifecycle (`status`, `stale_after`) and Attested Computation (sanctioned, checkable calculations). `index.md` and `log.md` are reserved filenames for directory listings and change logs. Consumers must be lenient: unknown types and keys, broken links and missing index files are never a reason to reject a bundle.

## What already fits

| OKF | Kumbu² |
|---|---|
| Bundle = directory tree, concept id = path without `.md` (OKF §2, §3) | Vault = top-level directory, absolute paths (concept §4) |
| A bundle may be a subdirectory of a larger tree (OKF §3) | An opt-in per directory needs nothing new from the spec |
| Producers may add keys, consumers preserve them (OKF §4.1) | Frontmatter is stored as is (concept §4) |
| `generated: { by, at }` (OKF §5.2) | `StoredNote.LastWriter`, `LastWriteAt` |
| Progressive disclosure via `index.md` (OKF §8) | `ls`, `outline`, section addresses |
| `log.md` change history (OKF §9) | `history <folder> --since` |
| Broken links are not malformed (OKF §6.1) | Links resolve leniently at read time |

## What is missing, by importance

1. **Markdown links.** OKF links are standard markdown links, preferably bundle-relative (`[customers](/tables/customers.md)`, OKF §6.1). Kumbu² only parses `[[wikilinks]]` (`WikiLink.LinkRegex`). An imported bundle's link graph would be invisible to `links` and `backlinks`, and `mv` would break its links silently. Obsidian supports markdown links too, so this gap exists today, independent of OKF. **→ v1.**
2. **Writer identity.** OKF's actor convention (OKF §7) separates `human:<id>`, `<producer>/<version>` for agents and `process:<id>`; trust tiers key off the `human:` prefix (OKF §5.3). Every write is `"dev"` today. If step 8 makes writer ids tell humans (web UI, Obsidian plugin) from agents (API key or OAuth client), the OKF mapping is free later; afterwards it would be a migration. It also answers a question worth asking without OKF: *what has an agent written that I haven't looked at yet?* **→ v1, step 8.**
3. **Export as a bundle** (zip or git, through the REST API of step 9): add `type: Note` where frontmatter lacks one, in the exported file only; fill `generated` from the last writer; turn wikilinks into bundle-relative markdown links through the existing resolver (open: ambiguous links, `#Heading` anchors versus OKF's markdown anchors, embeds `![[…]]`); synthesise `index.md` and `log.md`. **→ v2.**
4. **Reserved filenames.** `index.md` and `log.md` are common notes in Obsidian vaults but may not be concepts in a bundle (OKF §3.1). Needs a rule when the flag is set and on export. **→ v2.**
5. **`verified`, `status`, `stale_after`.** Users can write `status` and `stale_after` into frontmatter today, but no tool reads frontmatter (there is no `tags` command yet). A `verify` command would need a place to store its event: in the frontmatter it is a content change, as metadata it would only be written out on export. **→ v2.**

## Deliberately left out

- **`sources` and per-claim footnotes** (OKF §5.1): a writing convention, not a feature. Users can follow it; nothing in Kumbu² stands in the way.
- **Attested Computation** (OKF §10): made for data catalogues with SQL and dbt. Its executors and attesters are code files, which also contradicts text-only.
