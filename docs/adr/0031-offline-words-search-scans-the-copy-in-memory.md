# 0031. Offline words search scans the copy's texts in memory

- **Date:** 2026-11-25
- **Status:** Proposed
- **Decided by:** Chris, that words search works offline in 0.7.0; the plain scan instead of MiniSearch is proposed for his yes
- **Spec:** [Offline mode](../superpowers/specs/2026-11-22-offline-mode-design.md) §2a, §3, §3a "Search says what it searches"

## Context

The offline mode design promises words search across the mirrored vaults, through MiniSearch over the decrypted mirror, in memory and never stored (§3, §185 "built in memory after unlocking"). Until 0.7.0's test run the search sheet still asked `/api/search` offline and failed. Chris decided on 2026-11-25 that a little search has to work offline in 0.7.0 ("ein bisserl suche müssen wir auch offline anbieten"), after the feature freeze, so as a fix of the spec's promise and kept small.

## Decision

The Eidolon core searches the copy with a plain scan, without MiniSearch: each note's text is opened once per version and kept in memory with its sections (split as the server's words search splits them) and their words counted. A search finds the sections that hold every word of the query, or the whole query in one line, ignoring case; title hits first, then by how often the query's words occur, then in path and line order. The results have the server's shape (path, address, lines, snippet, the reason `title`, `exact` or `all words`, no score), so the search sheet shows them as it shows the server's. The tag filter and the archive rule are the server's.

## Why

- Enough for the size: a vault of a few thousand notes is a few megabytes of text; scanning their sections' word counts takes milliseconds, within the sheet's 200 ms debounce. An inverted index would buy nothing a person notices.
- No new dependency in the core days before the release, and none to keep within the boundary's licence check later.
- The same sections, addresses and snippets as online: a result opens the same place in the note whichever way it was found. MiniSearch ranks its own documents with its own tokeniser; matching the server's sections with it would have meant feeding it the same ported sections anyway.
- Against MiniSearch: fuzzy and prefix matching, and BM25 ranking like the server's words ranking. Words search offline is the fallback of a mode people meet rarely; "every word, title first" is predictable, which matters more there.

## Consequences

- No fuzzy or prefix matches offline: "pum" finds "pump" only as part of a line holding "pum" (the whole-query rule), not as a word.
- The ranking differs from the server's BM25: the same notes are found, in a simpler order.
- Memory holds every held note's text while the copy is unlocked, as the link resolver's aliases already need; it goes with the lock.
- Should vaults grow past where a scan answers within the debounce, an index (MiniSearch or another) can replace the matcher behind `MirrorSearch` without changing its callers.
