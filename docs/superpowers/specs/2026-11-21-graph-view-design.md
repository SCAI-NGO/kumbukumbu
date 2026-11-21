# The graph column: a local graph beside the note

Date: 2026-11-21. Status: approved by the maintainer on 2026-11-21, after a UX review and a builder check (its answers marked) (findings and decisions with their reasons in the maintainer's working notes, "Graph view 2026-11-21"). Target: 0.7.0, prepared on a feature branch while 0.6.0 is in post-production.

## Why

The graph view is what still keeps a power user with about 1,600 notes in Obsidian (graph backend spec, "Who asked"). The 0.6.0 backend already answers every query the view needs (`/api/graph/local`, `/node`, `/paths`). What Kumbu² adds is its own twist for knowledge work and collecting ideas: edges that carry how close two notes are **in meaning**, so the graph arranges itself by sense and not only by links, and, later, suggested links where meaning is close but no link exists yet (link prediction).

## Goals

1. **A graph column** beside the note on wide screens: the local graph of the focused note.
2. **Edges carry semantic similarity**, visibly (thickness, opacity) and physically (similar notes pull together). For now every similarity is a placeholder of **0.5**, behind one front-end interface, so the real value drops in once the backend computes it (after 0.6.0).
3. **Nodes speak at a glance:** size by links (as in Obsidian, switchable), colour by how recently the note was written (star temperature).
4. **A stub for suggested links**: the layer, its look and its card, without data in the product.

## Non-goals

- **No server change** (maintainer: late 0.6.0 changes would make merging painful). Only `web/`; the generated `web/src/api/schema.d.ts` gains the graph endpoints the server already has.
- No global graph in this change. It comes later on the same physics, drawn on a canvas.
- Nothing changes below 1920 px of viewport width.
- No computed similarity, no cache, no suggestion data: that is the backend's, later (see "The similarity to come").
- No shared graph of both split panes (see §3); kept as an idea.

## Anti-goals

- **A graph that claims meaning it doesn't have.** While similarity is a placeholder, the column says so plainly (honesty is security). Hard.
- **Losing one's place while walking the graph**: nodes already placed jump about when the centre changes.
- A hairball: hubs and day notes swallowing the neighbourhood.
- A picture only: a canvas silent to keyboard and screen readers.
- Typing that lags because the simulation runs beside the editor.

## Approaches

- **A. `d3-force` with SVG** (chosen by the maintainer). Only d3's physics module (ISC licence, about 15 KB gzipped, with `d3-quadtree`, `d3-dispatch`, `d3-timer`). It has per-edge link distance and strength, which carry the similarity, and a collision force, which carries the minimum distance. Drawn in SVG by React: the local graph stays below about 160 nodes (the hub cap falls with the depth, §2), and SVG nodes are elements that can be focused, labelled and tested.
- B. Our own small simulation: no dependency, but rebuilding what `d3-force` does well (Barnes–Hut, cooling, collision) for 15 KB.
- C. A graph library (Sigma.js, Cytoscape, react-force-graph): WebGL or canvas, strong for thousands of nodes, heavy for a side column, poor for accessibility, hard to fit to our themes.

The later global graph keeps `d3-force` as its physics and swaps only the drawing for a canvas; A closes no door.

## 1. Widths

- **Single note: from 1920 px** of viewport width (`min-width: 120rem`). **Split view: from 2880 px** (`180rem`), the graph a third column. Between 1920 and 2880 px the split view shows no graph column. *Maintainer: first written as 1980 and 2970, a slip; with 1980 no Full HD screen would get it.*
- The column sits right of the side panel: tree (318 px by default) | note(s) | side panel (300 px) | graph. A fixed **560 px**; at 1920 px with the tree open that leaves the note about 740 px, at 2880 px in the split view about 850 px per pane. Its own column and not a side panel tab, because 300 px is too narrow for a graph.
- Two hooks beside `useWide` (`web/src/hooks/useWide.ts`), on `mediaQueryHook`. Shown when the width fits and a note is shown, under the same conditions as the side panel (`MobileShell.tsx`: not for a note on its way out, nor one whose path isn't shown).
- **Collapsible**, as the side panel: collapsed or not remembered per device in `localStorage`; starts expanded, so it is found. Shortcut **Ctrl+Shift+G** (Cmd+Shift+G on a Mac), not while a dialog is open; hiding it while it holds the focus moves the focus to its toggle, as the side panel does.

## 2. What the column shows

- The **local graph of the focused note** (`GET /api/graph/local?path=…&depth=…&cap=…&…`), both directions. **The hub cap falls with the depth:** 20 at depth 1, 8 at depth 2, 5 at depth 3, so a graph stays below about 160 nodes; the "+N" beside a capped node says what was left out. Why: the cap holds per node, not per graph, so a cap of 20 allows about 420 nodes at depth 2 and 8,400 at depth 3, too many for a 560 px column and for a simulation beside the editor *(builder check; decided by the maintainer)*. The focused note sits in the middle.
- **A toolbar** in the column's head, every choice remembered per device: depth 1, 2 or 3; day notes, attachments and ghosts shown or hidden; node size by **Links**, **Backlinks** or **Length**; the hover preview on or off; the suggested links layer on or off. Defaults as the backend's: depth 1, everything shown, size by links, preview on, suggestions off.
- **One line at the column's foot**: "Similarity not measured yet" while the placeholder is in use; in the split view, how far the other pane's note is (§3). A node capped by the hub cap shows "+N" beside it (`leftOut`).
- **Loading and errors**: while the first answer is out, the column says "Drawing the graph…"; a refusal or a network error says so in one line with "Try again". A server without the graph (it has it since 0.6.0) never meets this branch.

## 3. The split view

The graph follows the **focused pane**, as the top bar and the side panel do. *Maintainer: a shared graph of both notes would clutter and break the paradigm that everything follows the focused pane.* The other pane's note:

- gets a **fainter selection ring** when it appears in the neighbourhood;
- is named in one line at the column's foot, from `GET /api/graph/paths?from=<focused>&to=<other>` with the column's filters: "Other pane: 2 steps away", "Other pane: not connected", "Other pane: more than 6 steps away", or nothing when both panes show the same note. Notes in two different vaults are never connected (links don't cross vaults, and the API answers `400`), so the line says "Other pane: in another vault" without asking *(Claude's call, builder check)*.

## 4. Nodes

- **Selection rings, as in No Man's Sky** (maintainer): the focused note has a ring around it, the other pane's note a fainter one.
- **Shape by kind**, so that nothing rests on colour alone: a note is a disc, a day note a disc too (it is a note), an attachment a diamond, a ghost a hollow ring.
- **Size**, area growing with the logarithm of the measure, clamped between a smallest and a largest radius *(the exact radii are the builder's, tuned by eye)*:
  - **Links** (default): `in + out`, the note's links in the whole vault, as the API gives them, not only the edges visible in the column. *Maintainer: as close to Obsidian as possible first.*
  - **Backlinks**: `in`.
  - **Length**: the note's size in bytes from the tree the web app already loads (`TreeNote.size`).
  - Attachments and ghosts keep a fixed small size whatever the measure (an 8 MB photo isn't the biggest thought), ghosts the smallest.
  - *Why links by default and colour for age:* length correlates strongly with link count (as on Wikipedia), so age is the more valuable second channel.
- **Colour is temperature**, as with real stars: how recently the note was written (`TreeNote.writtenAt`). Just written it glows blue-white; it cools through white, yellow and orange to a red dwarf over months. A continuous ramp on the logarithm of the age, from an hour to about half a year *(stops the builder's)*. Two ramps, one for dark and one for light schemes, picked by the scheme's brightness rather than one per scheme for all 17 *(Claude's call, builder check)*: on a light background a white star would vanish, so the light ramp is darker and every node has a thin outline. Every stop reaches 3:1 against the background of each scheme that uses its ramp, checked in `contrast.test.ts` as the flags' marks are. A note without `writtenAt`, an attachment and a ghost take a neutral colour. *Maintainer: "No Man's Sky style"; colour for size was considered and dropped, as it would spend the scarcest channel twice.*

## 5. Edges and the physics

Every edge has a similarity `s` in [0, 1] (§7).

- **Visible:** stroke width and opacity grow with `s` *(ranges the builder's)*. No arrows.
- **Physical:** link distance = `minimum + (1 − s) × span`, link strength grows with `s`; similar notes pull together and clusters form by meaning.
- **A minimum distance holds even at `s` = 1.0** (maintainer): a collision force with each node's radius plus a gap, so large nodes keep more room.
- The focused note is pinned in the middle.
- **Positions are kept** across answers: nodes are matched by path (a ghost by its title), a node already placed keeps its place, new ones start beside a neighbour that is placed and float in. The simulation is warmed again gently, not restarted.
- **Zoom and pan:** the wheel (or a pinch) zooms, dragging the background pans, a **Fit** button in the toolbar brings every node into view; a new centre fits again. Dragging a node moves it and pins it while held, then lets it go, as in Obsidian *(Claude's call, builder check)*.
- **Reduced motion:** with `prefers-reduced-motion`, the simulation runs to rest before the first paint and the result is shown without animation; later changes are shown at rest too.
- The simulation stops when it has cooled, and runs at all only while the column is shown; it never holds up typing (it lives outside the editor's render, ticking with `requestAnimationFrame`).

## 6. Using it

- **Click** on a note node opens it in the focused pane; the graph re-centres on it, keeping positions (§5). **Ctrl+click** (Cmd+click) opens it beside, in the right pane, as the palette's Ctrl+Enter does. A ghost isn't clickable. An **attachment** opens as it does everywhere else, in its window over the shell (`viewUrl`), and the graph stays on the note; Ctrl+click does the same, since panes hold notes only *(Claude's call, builder check)*.
- **Hover preview** (maintainer: small, and switchable off): after about 300 ms over a node, a small card with the title, the tags, the first paragraph and "changed 3 weeks ago", from `GET /api/graph/node`, cached by TanStack Query, so the second time it is there at once. A ghost's card says "No note yet". Off in the toolbar, no card and no request. The card shows on keyboard focus too.
- **Keyboard and screen readers:** each node is a focusable element (`role="button"`) with a label ("Budget, 3 links in, 5 out", a ghost "Anna, no note yet"); Tab enters the graph at the focused note, the arrow keys move to the nearest node in that direction, Enter opens, Ctrl+Enter opens beside. Beside the picture a **list of the neighbours** (a disclosure "List" in the toolbar) gives the same nodes as links, ordered by depth, then title.
- **Live:** after the sync socket's `changed` nudge for the vault (`useSyncSocket.ts`), the graph's query is invalidated and asked again with its `ETag` (`If-None-Match`); `304` changes nothing, otherwise only what changed moves.

## 7. The similarity, behind one interface

One module (`web/src/graph/similarity.ts`) is the only place that knows where a similarity comes from:

```ts
/** How close two notes are in meaning, 0 to 1; `measured` false while it is a placeholder. */
export type Similarity = { score: number; measured: boolean };
export function similarity(a: string, b: string): Similarity;
```

- **In the product: `{ score: 0.5, measured: false }` for every pair.** The column's foot says "Similarity not measured yet" while any shown edge is unmeasured.
- **In development** (`import.meta.env.DEV`, and only with `localStorage["kumbu.graph.mockSimilarity"] = "1"`): a fixed pseudo value per pair from a hash of both paths (order-independent), still `measured: false`. Why: with every edge at 0.5 the physics is a plain force graph and thickness says nothing, so we couldn't judge the encoding now. Never in a production build: the branch is removed by the bundler.
- When the backend computes similarities, this module asks it (likely along with the local graph) and nothing else in the view changes.

### The similarity to come (for the backend's spec, not built here)

- **Cosine.** The vectors are L2-normalised (`Vectors.Normalized`), so ‖a − b‖² = 2 − 2 cos: Euclidean distance gives the same order, and cosine is one dot product, in [−1, 1] (clamped to [0, 1] for the view).
- **Aggregation over chunks** (maintainer's idea): for each chunk of A its most similar chunk of B, averaged; and the same from B to A (a Chamfer or BERTScore-style measure). The two directions differ ("A is covered by B" is not "B is covered by A") and could one day give an edge a direction; the score is their mean. A chunk weighted by its length is the builder's question then.
- **Cost:** n × m dot products per pair; cheap for the edges of one local graph, the reason for a cache over all pairs of a large vault, keyed by both sides' chunk hashes, invalidated by a model switch. The hub-trap corrections from the link prediction notes (the vault's mean vector subtracted, CSLS) apply.

## 8. Suggested links (link prediction), the stub

- A layer of its own, switched in the toolbar, off by default: **dashed edges** in their own colour from the focused note to a few notes it doesn't link to.
- A click on a suggested edge (or Enter on it) opens a small card: the other note's title, "Similar in meaning, not linked", and two buttons, **Add link** and **Not related**, both disabled in this change with "Coming with link prediction".
- **Data:** one function beside the similarity (`suggestedLinks(path): Suggestion[]`) that returns nothing in the product, and, in development with the mock on, two or three notes of the same vault from the tree that aren't in the neighbourhood, picked by the same hash.
- Where an accepted link is written, how rejections are kept, and mutual kNN are the link prediction spec's questions (working notes "Link Prediction 2026-11-17").

## 9. Code

- `web/src/graph/`: the column (`GraphColumn.tsx`), its toolbar, the SVG drawing, the simulation hook, the scales (size, colour, edge), the similarity and suggestion modules, the API hooks (`useLocalGraph`, `useGraphNode`, `useGraphPaths`, with query keys under `["graph", …]`).
- `MobileShell.tsx`: renders the column after the side panel, with its toggle and shortcut, as it does the side panel.
- `web/src/api/schema.d.ts`: the graph endpoints, generated with `pnpm gen:api` against a server built from `master`.
- Colour ramps as theme tokens beside the existing ones (`web/src/theme/`), for every scheme.
- `README.md`: the graph column in the web app's description.

## 10. Tests

Vitest, written first.

- **Scales:** size grows with the measure and stays within its clamp; attachments and ghosts fixed; the colour ramp is monotone in age and gives the neutral colour without `writtenAt`; the light and dark ramps differ.
- **Physics parameters:** link distance falls with `s` and never goes below the minimum at `s` = 1; collision radius = radius + gap.
- **Similarity:** the product's answer is 0.5 and unmeasured for any pair; the mock is deterministic, order-independent, within [0, 1], and off without the flag.
- **Position keeping:** a new answer keeps the positions of nodes matched by path, places new ones beside a placed neighbour, drops the gone.
- **The column:** shown from 1920 px with a note, not below; in the split view from 2880 px only; collapsed state and toolbar choices survive a reload; Ctrl+Shift+G hides and shows it, the focus moved to the toggle when it held it; the "not measured" line; the other pane's ring and its distance line; click opens in the focused pane, Ctrl+click beside; a ghost doesn't open; preview off makes no request; keyboard walk and labels; the list of neighbours; a `changed` nudge asks again with the ETag.
- **Suggestions:** off by default; nothing in the product; the card's buttons disabled.

## 11. Left to the builder

- The exact radii, colour stops, stroke ranges and force constants, tuned by eye with the dev mock.
- How the arrow keys pick "the nearest node in that direction".
- Whether the toolbar is one row of icons with tooltips or a compact menu.
- The pull request's split: one for the column with nodes, edges and physics; one for the toolbar, preview, split-view line and suggestions stub, if the first grows large.
