# Link prediction in the web app — design

Date: 2026-11-22. Release: 0.7.0 (pulled in by Chris on 2026-11-22, beside the knowledge synthesis skill). Size: M. Status: draft; the decisions marked *(Chris)* are his, the rest are Claude's calls from the UX review (`link-prediction/ux-review.md` in the project files), open to change.

Sources: the idea *Link Prediction* (2026-11-17, `/SWE/Kumbu/Ideas/`); the graph view spec (`2026-11-21-graph-view-design.md` §8, the stub this fills); the knowledge synthesis spec (`2026-11-22-knowledge-synthesis-design.md`), whose `SimilarSearch` this builds on.

## 1. Why

A second brain holds notes that belong together and don't know it: written months apart, in other words, in another folder. Unlinked mentions find them when the title is named; they miss every note that speaks of the same thing in other words. The embeddings Kumbu² already keeps can find those, by meaning. The knowledge synthesis work gives agents that question (`similar --unlinked`); this gives it to people, where they read and link: beside the note and in the graph.

## 2. Goals, non-goals, anti-goals

Goals:
- **Similar notes** beside a note: up to five notes of its vault, close in meaning, that it neither links nor is linked from; each with the line of its closest section.
- Two answers per suggestion: **Add link** writes the link, **Not related** hides the pair for good, for everyone, on every device and for agents.
- The graph view's suggested-links layer shows the same suggestions, its card the same two answers.

Non-goals (0.8.0 or later):
- A list of rejected pairs to review or restore (the property in the note is that list, §3.6).
- A vault-wide feed of suggestions ("serendipity"), mutual kNN, hubness correction (mean vector, CSLS), a learnt threshold. The floor (§3.5) is a constant until a hold-out measurement on real vaults sets it.
- Suggestions offline: the embeddings stay on the server; offline the section says it needs a connection.
- Suggestions across vaults: links don't cross vaults.

Anti-goals:
- **A link written without the person's tap.** Similar is not the same as relevant.
- **A second similarity.** The scores are `SimilarSearch`'s, as `sh similar` and the graph's edges compute them, so the three agree.
- **A query leaving the server.** Stored vectors only; nothing is sent to an embedding provider.

## 3. Decisions

1. **A section in the Links tab, not a tab of its own.** "Similar notes" sits below "Unlinked mentions" and behaves like it: closed and not asked until opened, closed again on a change of note; under the note on narrow screens, as the other two. *Because* it answers the same question (what should link here and doesn't?) by meaning instead of by words; unified UX, no new place to look.
2. **A row is a mention row**: title, the first line of the closest section dimmed below it, the folder on the right; a tap opens the note at that line. **No score shown.** *Because* the order says it, and a cosine means nothing to a person.
3. **Two 44 px icon buttons per row, Add link and Not related**, labelled for screen readers. Offered only where the caller may change the note, as Add property is. While the note is being written, a tap saves and leaves Write mode first, as a flag's tap does.
4. **Add link appends `- [[Other]]` to a `## Related` section at the end of the note**, created once if missing; an existing `## Related` (any case, level 2) takes the item at its end. The link is the other note's title when no other note of the vault has that name, else its path in the vault without `.md`. *(Chris, 2026-11-22)* *Because* it is readable, Obsidian-compatible and gathers the added links in one place, easy to edit away.
5. **Up to 5 suggestions, cosine ≥ 0.5**, one per note (its best section), same vault, not the archive (unless the note is in it), only notes (no attachments), not linked either way. Day notes count like any note. *Because* five fit the panel without scrolling and a short list asks for a judgement per row; the floor keeps a small vault from suggesting noise.
6. **Not related is kept in the note's properties**: `not-related:` a list of paths in the vault without `.md` (`Work/Diving/Decompression`), no `[[…]]`, so neither the graph nor `--unlinked` sees a link. Written to the note the tap was on; read from both sides, so the pair is hidden on both notes. `sh similar --unlinked` leaves those pairs out too. The row turns into "Hidden · Undo" in place; Undo removes the entry again. *(Chris, pending)* *Because* it syncs, versions and shows in Obsidian like the flag, is sealed with the note, and needs no new storage for re-keying, deletion and moves to learn about. A rename of the other note makes the pair show again; accepted.
7. **States**, in the section's own words: search by meaning off ("Similar notes need search by meaning, which is off for this vault."), preparing (with Try again), this note not indexed yet, nothing above the floor ("No similar notes that aren't linked."), an error (with Try again). Offline the query fails as any other and says so.

## 4. Server

- `GET /api/similar/{vault}/{**path}` → `SimilarNotesView(State, Notes, Pending)`: `State` one of `ready`, `unindexed`, `preparing`, `off`; `Notes` the suggestions as `SimilarNoteView(Path, Line, Text, Score)` (1-based line of the best chunk, its first line of text, the cosine). Through `SimilarSearch.RunAsync` with the note as the one source, the vault as the scope, `Unlinked`, and `LeftOut` for the rejected pairs (§3.6) of both sides.
- `POST /api/vaults/{vault}/similar/link` `{ path, target }` and `POST /api/vaults/{vault}/similar/not-related` `{ path, other, undo }` → `{ path, version }`. Both through `SuggestionService` in `Kumbu.Core/Links`: read, change, check, compare-and-swap as the caller, retry on a new state, as `FlagService` does; the caller's rights checked first.

## 5. Web app

- `web/src/api/similar.ts`: `useSimilarNotes(path, enabled)`, `useAddLink`, `useNotRelated`; the writes wait for the editor (`editorSettled`) and refresh the note, its links and the suggestions.
- `web/src/note/SimilarNotes.tsx`: the section, beside `UnlinkedMentions` in `BacklinksList`.
- `web/src/graph/suggestions.ts` becomes `useSuggestedLinks(path, shown)` on the same query; `SuggestionCard` gets the two answers, enabled.

## 6. Tests

- xUnit: the Related section (new, existing, at the end of the file, CRLF), the link text (unique title, ambiguous name), `not-related` written, read from both sides, undone; the endpoint's states, leaving out rejected pairs, the floor, the rights.
- Vitest: the section's states and rows, Add link and Not related with Undo, the card's buttons.
