# The graph view out of preview

Date: 2026-11-22. Status: draft for the maintainer, written to start the work the same night (0.7.0, feature freeze about Tuesday 2026-11-24, then post-production). Builds on the graph view spec (`2026-11-21-graph-view-design.md`) and the graph backend spec (`2026-11-16-graph-backend-design.md`).

## Why

0.6.0 shipped the graph column and dialog as a **preview** (#274, #276): "there to try … before it counts as done". The maintainer pulled it into 0.7.0 as a parallel track to offline mode (offline mode design §9). What still made it a preview is one honest admission and one empty switch:

1. **Edges carry no meaning yet.** Every edge's similarity is the placeholder 0.5, and the foot says "Similarity not measured yet". The view's whole twist (graph view spec, "Why": a graph that arranges itself by sense) isn't there.
2. **The Suggestions switch shows a layer with no data** in the product.

Everything else the spec asked for is built and tested: widths, column and dialog, toolbar, node encoding, physics with kept positions, hover preview, keyboard walk and list, touch, the split view's line, live updates with `ETag`.

## What "out of preview" means here

- **Measured edges**: each edge between two notes carries the similarity the server computes from their embeddings; where it can't (no embeddings yet, search by meaning off), the edge is drawn as now and the foot says why, in a line that is true.
- **No empty switch**: Suggestions was hidden in production builds until link prediction (its own thread, 0.7.0) gave it data; link prediction lifted the guard, so the switch shows everywhere.
- **Offline**: the graph says it needs a connection (offline mode design §3a), once the mode's state is on `master`.
- **The wording**: the release note entry and the README drop "preview".

Not in this change: the global graph, a cache of all pairs, the suggestions' data and card (link prediction), offline graph from the local index.

## 1. The similarity, from the server

**`GET /api/graph/similarity`**, with the parameters of `/api/graph/local` (`path`, `depth`, `dir`, `cap`, the filter). The server walks the same local graph and scores **its edges between two notes**; attachments and ghosts have no score.

```json
{ "state": "ready", "pairs": [["/Work/a.md", "/Work/b.md", 0.62], ["/Work/a.md", "/Work/c.md", null]], "reason": null }
```

- `pairs`: one per note-to-note edge, keyed by the two paths, so the client matches them to its edges by key and never by an index of another answer. `null`: not measurable now (a note not embedded yet, or by another model).
- `state`: `ready`, `preparing` (the model or tokenizer is loading) or `off` (search by meaning is off on the server, or the vault's provider is unavailable), with `reason` in the words the search already uses.
- *Why the local graph's parameters and not a list of pairs:* the server bounds the work itself (at most the local graph's edges, about 160 nodes), there is no list to validate, and a caller can't ask for pairs outside what it may already see. *Why a second request and not a field of `/local`:* the local graph's `ETag` changes with links and names only, so that a save doesn't redraw it; similarity changes with the prose. The second answer is cached by the query client on its own key and asked again after a `changed` nudge, as the graph is.
- **No text leaves the server**: only stored vectors are compared, so the provider consent of shared vaults (collab design §8.5) isn't touched; an unconsented member still gets the scores of vectors the server already holds. *Decided by the maintainer on 2026-11-22: no text leaves the server, so there is nothing to consent to.*

### The score

As the graph view spec §7 proposed (maintainer's idea): for each chunk of A its most similar chunk of B (cosine, a dot product of the normalised vectors), averaged over A's chunks; the same from B to A; the score is the mean of both directions, clamped to [0, 1]. One pure function in `Kumbu.Core.Search` (`ChunkSimilarity.Mutual`), so `similar` (knowledge synthesis) and link prediction can score with the same measure and the three never disagree. If the knowledge synthesis branch lands its own function first, this one gives way to it.

Cost: n × m dot products of 768 floats per edge; a note has a few to a few dozen chunks, so a local graph's 20 to 200 edges take milliseconds. No cache in this change: measured first, cached if it shows.

### In the view

- `similarity.ts` stays the one place that knows where a score comes from (graph view spec §7). It takes the server's answer: measured pairs `{ score, measured: true }`, the rest the placeholder 0.5 unmeasured. The dev mock stays for development.
- **The encoding stretches the measured scores of the graph shown** over [0, 1] before width, opacity, distance and strength: raw cosines of related notes sit in a narrow band (about 0.3 to 0.8 with arctic-m), which would draw every edge alike. *Why relative:* the graph compares the neighbours of one note with each other; an absolute scale says little to the eye. With fewer than two distinct measured scores nothing is stretched.
- **The foot line says what is true:** nothing when every note-to-note edge is measured; "Similarity measured for 12 of 15 links" while some aren't embedded yet; "Similarity not measured: search by meaning is off" (or "… getting ready") when the server can't. The old "Similarity not measured yet" goes.
- A measured score is in each edge's tooltip ("Close in meaning: 62 %")? *Left out:* edges aren't focusable, and a per-edge figure invites reading more precision into it than there is.

## 2. Suggestions

The Suggestions switch is shown only in development builds until link prediction's data is there; the link prediction work removes the guard, swaps `suggestedLinks` for its query and enables the card's buttons. *Why:* a switch that draws nothing is the kind of half-done the preview label covered.

## 3. Offline

When the offline mode is on (its state from the mode work, `web/src/offline/mode.ts`, not on `master` yet), the column and the dialog show "The graph needs a connection. It's back when you are." in place of the drawing, and ask nothing; the Graph button stays, as §3a dims rather than hides. If the mode isn't merged before the freeze, this is a short follow-up in post-production.

## 4. Tests

Written first.

- **Core:** `ChunkSimilarity.Mutual` is symmetric, 1 for the same vectors, within [0, 1], averages both directions (a short note fully covered by a long one scores between the two directions), and is null without chunks.
- **Server:** `/api/graph/similarity` scores the note edges of the local graph and leaves out ghosts and attachments; `null` for a note without embeddings; `off` without a search index; 404 for a vault the caller hasn't, 400 for bad parameters as `/local`.
- **Web:** `similarity.ts` returns measured scores from an answer and the placeholder otherwise; the stretch; the foot's three lines; Suggestions hidden in production.

## 5. Release note

For `/SWE/Kumbu/Versions/0.7.0.md` (New): **The graph of a note, out of preview.** The links between notes are drawn by how close the notes are in meaning: the closer, the thicker and the shorter, so the neighbourhood arranges itself by sense; until a note is embedded its links are drawn plainly, and the column's foot says how many are measured.
