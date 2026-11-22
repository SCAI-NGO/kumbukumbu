# Graph Column Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A local graph of the focused note in a column beside the note on wide screens, its edges carrying a (for now placeholder) semantic similarity visibly and physically, with a stub for suggested links.

**Architecture:** Front end only, in a new folder `web/src/graph/`. Data from the existing `/api/graph/local`, `/node` and `/paths` endpoints, joined with the vault's tree (size, `writtenAt`). Physics with `d3-force`, drawn as SVG by React. Similarity and suggestions come from one module each, a placeholder in the product and a hash-based mock in development.

**Tech Stack:** React 19, TypeScript, Tailwind 4, TanStack Query 5, openapi-fetch, `d3-force` 3, Vitest + Testing Library.

**Spec:** `docs/superpowers/specs/2026-11-21-graph-view-design.md`

## Global Constraints

- No change outside `web/` except `README.md` (spec, Non-goals). No server change.
- Every new source file starts with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- British English in code, comments and UI copy. UI copy in English.
- Use `pnpm` (`pnpm -C web …`); `rg`, not `grep`.
- Widths: single note from `(min-width: 120rem)`, split view from `(min-width: 180rem)`; the column a fixed `560px`.
- Hub cap by depth: 20 at depth 1, 8 at depth 2, 5 at depth 3.
- Similarity placeholder `{ score: 0.5, measured: false }` for every pair in production builds.
- The dev mock only when `import.meta.env.DEV` and `localStorage["kumbu.graph.mockSimilarity"] === "1"`.
- `localStorage` is a convenience: wrap every read and write in try/catch, as `web/src/hooks/useSidePanel.ts` does.
- Colour only through tokens or the two star ramps of `web/src/graph/ramps.ts`; no colour carries meaning alone.
- Tests first (Vitest); run `pnpm -C web test --run <file>` per task, `pnpm -C web build` at the end of each task that touches types.
- Commit messages imperative, free-form, no prefix, ending with the session's attribution lines.

## Review Focus

1. **A note not in the tree yet** (just created, the tree query still loading): the node draws with the neutral colour and the smallest note radius, never throws. Test in Task 2 (`toModel` without a tree entry).
2. **The focused note changes while the simulation runs** (clicking through quickly): the old simulation is stopped, no tick touches unmounted state, positions of shared nodes are kept. Test in Task 4.
3. **An empty neighbourhood** (an orphan note: one node, no edges): the column draws the single node centred, the list says "No links yet", nothing divides by zero in the scales. Test in Task 2 (scales with 0) and Task 6.
4. **A 404 for the focused note** (removed in another session, or an attachment that isn't in the graph): the column shows its one-line error with "Try again", the shell keeps working. Test in Task 6.
5. **Two ghosts with the same title from different folders** (`[[./Anna]]` per folder): node ids must not collide. Ids are `path ?? "ghost:" + index` within one answer, matched across answers by `path ?? "ghost:" + title`. Test in Task 2 and Task 4.

---

### Task 1: The graph API in the web app

**Files:**
- Modify: `web/package.json` (add `d3-force@^3.0.0`, dev `@types/d3-force@^3.0.10`), `web/pnpm-lock.yaml` (via `pnpm -C web add`)
- Modify: `web/src/api/schema.d.ts`: the four paths `/api/graph`, `/api/graph/local`, `/api/graph/node`, `/api/graph/paths` and the schemas `GraphNodeView`, `GraphView`, `LocalGraphView`, `GraphNodeDetailView`, `GraphPathsView`, in the generator's shape (copy the shape of `/api/links/{vault}/{path}`; query parameters and fields from `src/Kumbu.Server/Api/GraphApi.cs`; responses 200, 304 for the global and local graph, 400, 401, 404, 503). Mark the block with a comment `// By hand until gen:api runs against a 0.6.0 server.`
- Modify: `web/src/api/history.ts:27`: add `"graph"` to the keys `vaultChanged` invalidates.
- Create: `web/src/graph/api.ts`
- Test: `web/src/graph/api.test.ts`

**Interfaces:**
- Produces:
  - `export type GraphNodeView = components["schemas"]["GraphNodeView"]` (and `LocalGraphView`, `GraphNodeDetailView`, `GraphPathsView`).
  - `export type GraphFilters = { depth: 1 | 2 | 3; days: boolean; attachments: boolean; ghosts: boolean }`
  - `export const capFor = (depth: 1 | 2 | 3): number` → 20, 8, 5.
  - `export function useLocalGraph(path: string | null, filters: GraphFilters): UseQueryResult<LocalGraphView>`; query key `["graph", "local", path, filters]`; sends `cap: capFor(depth)`, `dir: "both"`; keeps the last answer's `ETag` per key and sends it as `If-None-Match`; on `304` resolves with the cached data; `placeholderData: (previous) => previous`; `enabled: path !== null`.
  - `export function useGraphNode(path: string | null, enabled: boolean): UseQueryResult<GraphNodeDetailView>`; key `["graph", "node", path]`; `staleTime: 60_000`.
  - `export function useGraphPaths(from: string | null, to: string | null, filters: GraphFilters): UseQueryResult<GraphPathsView | "other-vault" | null>`; key `["graph", "paths", from, to, filters]`; resolves `null` without a request when either is null or `from === to`, `"other-vault"` without a request when `vaultOf(from) !== vaultOf(to)`.

- [ ] **Step 1: Write the failing tests** in `api.test.ts` with a mocked `fetch` (as other `web/src/api/*.test.ts` do):
  - `capFor(1) === 20`, `capFor(2) === 8`, `capFor(3) === 5`.
  - `useLocalGraph("/Work/A.md", { depth: 2, days: false, attachments: true, ghosts: true })` requests `/api/graph/local?path=%2FWork%2FA.md&depth=2&dir=both&cap=8&days=false&attachments=true&ghosts=true`.
  - A second fetch of the same key sends `If-None-Match` with the first answer's ETag; a `304` answer leaves `data` equal to the first answer.
  - `useGraphPaths("/Work/A.md", "/Work/A.md", …)` and `useGraphPaths("/Work/A.md", null, …)` make no request and give `null`; `useGraphPaths("/Work/A.md", "/Ideas/B.md", …)` makes no request and gives `"other-vault"`.
  - `vaultHinted(queryClient, "Work")` invalidates `["graph", "local", "/Work/A.md", …]` and not `["graph", "local", "/Ideas/B.md", …]`.
- [ ] **Step 2: Run** `pnpm -C web test --run src/graph/api.test.ts`. Expected: FAIL (module missing).
- [ ] **Step 3: Add the dependencies** (`pnpm -C web add d3-force@^3.0.0 && pnpm -C web add -D @types/d3-force@^3.0.10`), the schema entries, the `history.ts` key and `api.ts`. For the ETag, a module-level `Map<string, { etag: string; data: LocalGraphView }>` keyed by `JSON.stringify(queryKey)`; call `api.GET` with `headers: { "If-None-Match": etag }` and treat `response.status === 304` before `call()` throws.
- [ ] **Step 4: Run** the test file and `pnpm -C web build`. Expected: PASS, build clean.
- [ ] **Step 5: Commit** "Give the web app the graph endpoints".

### Task 2: The model and the scales

**Files:**
- Create: `web/src/graph/model.ts`, `web/src/graph/scales.ts`, `web/src/graph/ramps.ts`
- Modify: `web/src/theme/contrast.test.ts` (the ramp's stops on every scheme's grounds)
- Test: `web/src/graph/model.test.ts`, `web/src/graph/scales.test.ts`

**Interfaces:**
- Consumes: `LocalGraphView` (Task 1), `TreeView`/`TreeNote` (`web/src/api/notes.ts`).
- Produces:
  - `export type Kind = "note" | "day" | "attachment" | "ghost"`
  - `export type SizeBy = "links" | "backlinks" | "length"`
  - `export type GNode = { id: string; key: string; path: string | null; title: string; kind: Kind; tags: string[]; in: number; out: number; depth: number; size: number | null; writtenAt: string | null; leftOut: number }` (`id` unique within one answer: `path ?? "ghost:" + index`; `key` stable across answers: `path ?? "ghost:" + title`).
  - `export type GEdge = { source: string; target: string; flags: number }` (ids).
  - `export function toModel(local: LocalGraphView, tree: TreeView | undefined): { nodes: GNode[]; edges: GEdge[] }`
  - `export function treeEntry(root: TreeFolder, path: string): TreeNote | null`
  - In `scales.ts`: `measureOf(node: GNode, by: SizeBy): number` (`in + out`, `in`, `size ?? 0`); `radius(node: GNode, by: SizeBy): number` with `R_MIN = 5`, `R_MAX = 22`, ghosts `4`, attachments `6`, notes `R_MIN + (R_MAX − R_MIN) × min(1, log1p(m) / log1p(M))` with `M = 60` for links and backlinks, `M = 100_000` for length; `ageColour(writtenAt: string | null, now: number, dark: boolean): string`; `edgeWidth(s: number): number` (`0.75 + 2.25 s`); `edgeOpacity(s: number): number` (`0.25 + 0.6 s`); `linkDistance(s: number): number` (`MIN_DISTANCE + (1 − s) × SPAN`, `MIN_DISTANCE = 40`, `SPAN = 110`); `linkStrength(s: number): number` (`0.15 + 0.85 s`); `collideRadius(r: number): number` (`r + GAP`, `GAP = 8`). Constants exported; the builder may tune them by eye in Task 5, the tests pin the shape, not the numbers.
  - In `ramps.ts`: `export const darkRamp: readonly string[]`, `export const lightRamp: readonly string[]`, each five stops from hottest to coldest (blue-white, white, yellow, orange, red); `export const neutral = { dark: string; light: string }`; `AGE_MIN_MS = 3_600_000` (an hour), `AGE_MAX_MS = 180 × 86_400_000` (about half a year). `ageColour` interpolates on `log(age)` between the stops, clamped.

- [ ] **Step 1: Write the failing tests.**
  - `toModel`: ids unique with two ghosts titled "Anna"; `key` of a ghost is `"ghost:Anna"`; `leftOut` read from the string-keyed map; a node whose path isn't in the tree has `size: null, writtenAt: null`; edges map `[from, to, flags]` indices to ids.
  - `treeEntry` finds `/Work/projects/A.md` below nested folders and gives null for a missing one.
  - `radius`: grows with the measure; stays in `[R_MIN, R_MAX]` for 0 and for 10,000; attachments and ghosts the same whatever `by`; ghost smallest; `measureOf` with `size: null` is 0.
  - `ageColour`: `null` gives the neutral colour; an hour and a minute give the first stop; a year gives the last; dark and light differ for the same age.
  - `linkDistance(1) === MIN_DISTANCE`, `linkDistance(0) > linkDistance(0.5) > linkDistance(1)`; `edgeWidth` and `edgeOpacity` rise with `s`; `collideRadius(10) === 10 + GAP`.
  - In `contrast.test.ts`: every stop of `darkRamp` on the grounds of every scheme with `light: false`, and of `lightRamp` on every scheme with `light: true`, is at least 3:1 (the existing `contrast()` helper and `grounds`).
- [ ] **Step 2: Run** `pnpm -C web test --run src/graph/model.test.ts src/graph/scales.test.ts src/theme/contrast.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.** Pick stops near a blackbody ramp; if a stop fails 3:1 on some scheme, lighten it (dark ramp) or darken it (light ramp) for the whole ramp until all pass.
- [ ] **Step 4: Run** the same tests. Expected: PASS.
- [ ] **Step 5: Commit** "Model the graph column's nodes, sizes and star colours".

### Task 3: Similarity and suggestions behind one interface

**Files:**
- Create: `web/src/graph/similarity.ts`, `web/src/graph/suggestions.ts`
- Test: `web/src/graph/similarity.test.ts`, `web/src/graph/suggestions.test.ts`

**Interfaces:**
- Consumes: `GNode` (Task 2), `TreeView`.
- Produces:
  - `export type Similarity = { score: number; measured: boolean }`
  - `export function similarity(a: string, b: string): Similarity`
  - `export function mockSimilarity(a: string, b: string): number` (exported for tests: FNV-1a over the two keys sorted and joined by `"\n"`, mapped to `[0, 1]`).
  - `export const mockOn = (): boolean` (`import.meta.env.DEV` and the flag).
  - `export type Suggestion = { path: string; title: string; similarity: Similarity }`
  - `export function suggestedLinks(path: string, shown: ReadonlySet<string>, tree: TreeView | undefined): Suggestion[]`: `[]` unless `mockOn()`; with it, the three notes (`kind` note) of the same vault's tree not in `shown` and not `path` with the highest `mockSimilarity(path, other)`.

- [ ] **Step 1: Write the failing tests.** `similarity` gives `{ score: 0.5, measured: false }` without the flag, with `vi.stubEnv("DEV", false)` and the flag set; `mockSimilarity(a, b) === mockSimilarity(b, a)`, stable across calls, within `[0, 1]`, different for at least two of three sample pairs; with DEV and the flag, `similarity` gives the mock's score and `measured: false`; `suggestedLinks` is `[]` without the mock and, with it, at most three, none in `shown`, none equal to `path`, all in the same vault.
- [ ] **Step 2: Run** `pnpm -C web test --run src/graph/similarity.test.ts src/graph/suggestions.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.** Keep the `import.meta.env.DEV` check inline at the top of `mockOn`, so the bundler drops the mock from production builds.
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** "Put the graph's similarity behind one placeholder".

### Task 4: The force layout

**Files:**
- Create: `web/src/graph/layout.ts`, `web/src/graph/useForceLayout.ts`
- Test: `web/src/graph/layout.test.ts`, `web/src/graph/useForceLayout.test.ts`

**Interfaces:**
- Consumes: `GNode`, `GEdge`, `radius`, `linkDistance`, `linkStrength`, `collideRadius` (Task 2), `similarity` (Task 3).
- Produces:
  - `export type Placed = { key: string; x: number; y: number }`
  - `export function seedPositions(nodes: GNode[], edges: GEdge[], previous: ReadonlyMap<string, Placed>, centreKey: string): Map<string, { x: number; y: number; fixed: boolean }>`: kept nodes keep `x, y`; the centre is fixed at `(0, 0)`; a new node starts within 20 px of a placed neighbour (deterministic jitter from its key), or on a ring of radius 60 around the centre without one.
  - `export function useForceLayout(nodes: GNode[], edges: GEdge[], opts: { centreKey: string; sizeBy: SizeBy; reducedMotion: boolean; running: boolean }): { positions: ReadonlyMap<string, { x: number; y: number }>; drag(key: string, x: number | null, y: number | null): void }`. `forceLink` with `distance`/`strength` from `similarity(source.key, target.key).score`, `forceManyBody` (strength −120), `forceCollide` with `collideRadius(radius(node, sizeBy))`, centre pinned with `fx = fy = 0`. Ticks on `requestAnimationFrame` (`simulation.stop()` and manual `tick()` per frame), positions published at most once per frame. A new answer reheats with `alpha(0.3)`, never a restart from 1. With `reducedMotion`, `tick()` until `alpha < alphaMin` synchronously before publishing. Stops and cancels its frame on unmount and when `running` is false. `drag` sets `fx/fy` while held, `null` releases.

- [ ] **Step 1: Write the failing tests.**
  - `seedPositions`: a node in `previous` keeps its exact position; the centre is `(0, 0)` and fixed; a new node with a placed neighbour lands within 20 px of it; two ghosts with the same title in one answer but different ids both get positions; a node gone from the answer is not in the result.
  - `useForceLayout` (fake `requestAnimationFrame`): with `reducedMotion: true` the first render's positions are already at rest (no change after more frames) and no two nodes are closer than the sum of their collide radii minus 1 px; at `s = 1` for every edge (mocked `similarity`) linked nodes are still at least `MIN_DISTANCE − 1` apart; unmounting cancels the pending frame (`cancelAnimationFrame` called); rerendering with a new centre keeps the positions of shared keys within 1 px on the first frame.
- [ ] **Step 2: Run** `pnpm -C web test --run src/graph/layout.test.ts src/graph/useForceLayout.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** "Lay the graph column out with d3-force".

### Task 5: Drawing the graph

**Files:**
- Create: `web/src/graph/GraphCanvas.tsx`, `web/src/graph/keyboard.ts`
- Test: `web/src/graph/GraphCanvas.test.tsx`, `web/src/graph/keyboard.test.ts`

**Interfaces:**
- Consumes: Tasks 2 to 4.
- Produces:
  - `export function nearestInDirection(from: { x: number; y: number }, others: ReadonlyArray<{ key: string; x: number; y: number }>, dir: "up" | "down" | "left" | "right"): string | null`: the node in the 90° cone of the direction with the smallest distance; null when the cone is empty.
  - `export default function GraphCanvas(props: { nodes: GNode[]; edges: GEdge[]; centreKey: string; otherKey: string | null; sizeBy: SizeBy; suggestions: Suggestion[]; showSuggestions: boolean; onOpen(node: GNode, beside: boolean): void; onHover(node: GNode | null): void; onSuggestion(s: Suggestion): void; fitSignal: number; running: boolean })`. It calls `useForceLayout(nodes, edges, { centreKey, sizeBy, reducedMotion, running })` itself, `reducedMotion` from `mediaQueryHook("(prefers-reduced-motion: reduce)")`.
  - SVG `role="group"` labelled "Graph of <title>". Edges: `<line>` with `edgeWidth`/`edgeOpacity` of the similarity, stroke `var(--color-line)` brightened toward `text` (builder's), no arrows. Suggestions: dashed (`stroke-dasharray="4 4"`) in `var(--color-accent)`, focusable, Enter or click calls `onSuggestion`. Nodes: `<g role="button" tabIndex>` with `aria-label` "`<title>`, `<in>` links in, `<out>` out" (a ghost "`<title>`, no note yet"); disc for note and day, diamond (rotated square) for attachment, hollow ring for ghost; fill `ageColour(writtenAt, Date.now(), dark)` (`useDarkScheme()`), a thin outline in light schemes; the centre with a solid ring (stroke `var(--color-text)`, width 2, radius + 4), the other pane's node with the same ring at opacity 0.45; "+N" (`leftOut`) as small text beside a capped node; the title under each node in `text-dim`, truncated at 24 characters with `…`.
  - Interaction: click opens (`onOpen(node, e.ctrlKey || e.metaKey)`), not for ghosts; Enter and Ctrl+Enter the same from the keyboard; arrow keys move the focus by `nearestInDirection`; Tab enters at the centre (`tabIndex` 0 on the centre, −1 on the rest, roving). Wheel zooms around the pointer (0.3× to 3×), dragging the background pans, dragging a node calls `drag`; a change of `fitSignal` or `centreKey` fits all nodes into the viewBox with 24 px padding. Hover and focus call `onHover` after 300 ms (cleared on leave or blur).

- [ ] **Step 1: Write the failing tests.**
  - `nearestInDirection`: picks the closer of two nodes to the right; ignores a node up-right at 60° from the axis; null with nothing to the left.
  - `GraphCanvas`: renders one `role="button"` per node with the labels above ("Budget, 3 links in, 5 out", "Anna, no note yet"); a ghost is a circle with `fill="none"`, an attachment a `rect` rotated 45°; the centre has a ring and the other node a fainter one (opacity attribute); click on a note calls `onOpen(node, false)`, Ctrl+click `onOpen(node, true)`, click on a ghost calls nothing; ArrowRight from the centre focuses the node to its right; Enter on a focused node opens it; dashed edges render only with `showSuggestions` and suggestions given; hover for 300 ms (fake timers) calls `onHover(node)`, leaving before does not.
- [ ] **Step 2: Run** `pnpm -C web test --run src/graph/GraphCanvas.test.tsx src/graph/keyboard.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.** With the dev mock on (`localStorage["kumbu.graph.mockSimilarity"] = "1"`, `pnpm -C web dev`), tune the constants of Task 2 by eye; adjust the tests only where they pinned a number rather than a shape.
- [ ] **Step 4: Run** the tests and `pnpm -C web build`. Expected: PASS.
- [ ] **Step 5: Commit** "Draw the graph column's nodes and edges".

### Task 6: The column: toolbar, states, foot, list

**Files:**
- Create: `web/src/graph/GraphColumn.tsx`, `web/src/graph/useGraphPrefs.ts`
- Test: `web/src/graph/GraphColumn.test.tsx`, `web/src/graph/useGraphPrefs.test.ts`

**Interfaces:**
- Consumes: Tasks 1 to 5; `useTree` (`web/src/api/notes.ts`), `vaultOf`, `viewUrl` (`web/src/api/paths.ts`), `useOpenNote` (`web/src/shell/openInPane.ts`).
- Produces:
  - `export type GraphPrefs = GraphFilters & { sizeBy: SizeBy; preview: boolean; suggestions: boolean; list: boolean }`, defaults `{ depth: 1, days: true, attachments: true, ghosts: true, sizeBy: "links", preview: true, suggestions: false, list: false }`.
  - `export function useGraphPrefs(): [GraphPrefs, (change: Partial<GraphPrefs>) => void]`, stored as JSON under `kumbu.graphPrefs`; an unreadable or partial value falls back per field to the default.
  - `export const graphColumnId = "graph-column"`
  - `export default function GraphColumn(props: { path: string; otherPath: string | null; hidden: boolean })`: an `<aside id={graphColumnId} aria-label="Graph" hidden={hidden} className="flex w-[560px] flex-none flex-col border-l border-line bg-surface">`.
  - Toolbar (one row, icon buttons with `title` and `aria-pressed` where they toggle, from `lucide-react`): depth 1/2/3 (a segmented control), Day notes, Attachments, Ghosts, Size (Links / Backlinks / Length, a small menu or segmented control), Preview, Suggestions, List, Fit.
  - States: while the first answer is out "Drawing the graph…"; on error one line, the server's message or "Couldn't draw the graph.", and a "Try again" button (`refetch`).
  - Foot: "Similarity not measured yet" while any shown edge's similarity has `measured: false`; in the split view, from `useGraphPaths(path, otherPath, prefs)`: "Other pane: 1 step away" / "Other pane: N steps away" (the shortest path's length), "Other pane: not connected", "Other pane: more than 6 steps away", "Other pane: in another vault", nothing for `null`.
  - The canvas gets `running={!hidden}`, so the simulation stops while the column is collapsed.
  - Opening: a note node with `useOpenNote()(path)` or, beside, `useOpenNote()(path, { side: "right" })`; an attachment with `navigate(viewUrl(path), { state: { paths: [path] } })` either way.
  - List (when `prefs.list`): the nodes as links, ordered by depth, then title; a ghost as plain text with "no note yet"; "No links yet" when only the centre is there.

- [ ] **Step 1: Write the failing tests** (mocked API as in `SidePanel.test.tsx`/`BacklinksList` tests):
  - `useGraphPrefs`: defaults without storage; a stored partial JSON keeps the stored fields and defaults the rest; garbage JSON gives the defaults; a change is stored.
  - `GraphColumn`: "Drawing the graph…" before the answer; "Similarity not measured yet" with an answer; a 404 shows its line and "Try again", which asks again; depth 2 asks with `cap=8`; toggling Day notes asks with `days=false` and survives a remount; with `otherPath` two steps away the foot says "Other pane: 2 steps away", with another vault "Other pane: in another vault" and no paths request; clicking an attachment node navigates to its `viewUrl`; List shows the neighbours by depth then title, and "No links yet" for an orphan.
- [ ] **Step 2: Run** `pnpm -C web test --run src/graph/GraphColumn.test.tsx src/graph/useGraphPrefs.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** "Give the graph column its toolbar, states and list".

### Task 7: Hover preview and the suggestions stub

**Files:**
- Create: `web/src/graph/NodePreview.tsx`, `web/src/graph/SuggestionCard.tsx`
- Modify: `web/src/graph/GraphColumn.tsx`
- Test: `web/src/graph/NodePreview.test.tsx`, `web/src/graph/SuggestionCard.test.tsx`

**Interfaces:**
- Consumes: `useGraphNode` (Task 1), `ago` (`web/src/note/AgentBanner.tsx:23`), `Suggestion` (Task 3), `GraphCanvas`'s `onHover`/`onSuggestion` (Task 5).
- Produces:
  - `export default function NodePreview(props: { node: GNode; anchor: { x: number; y: number } })`: a small card (`role="tooltip"`) over the column near the node: title, tags, the first paragraph, "changed <ago>" from `changed`; a ghost "No note yet" without a request. Requests only while shown (`useGraphNode(path, true)`).
  - `export default function SuggestionCard(props: { suggestion: Suggestion; onClose(): void })`: the title, "Similar in meaning, not linked", and the buttons **Add link** and **Not related**, both `disabled` with `title="Coming with link prediction"` and a visible line "Coming with link prediction"; Escape and a click outside close it.
  - `GraphColumn` shows the preview only when `prefs.preview`; with it off, `onHover` does nothing and no `/api/graph/node` request is made. Suggestions: `suggestedLinks(path, shownPaths, tree)` passed to the canvas when `prefs.suggestions`.

- [ ] **Step 1: Write the failing tests.** The preview shows title, tags, first paragraph and "changed 3 weeks ago" (fixed `now`); a ghost's preview says "No note yet" and makes no request; with `preview: false` hovering a node for 400 ms makes no `/api/graph/node` request; a second hover of the same node makes no second request; `SuggestionCard` has both buttons disabled and the line "Coming with link prediction", closes on Escape.
- [ ] **Step 2: Run** `pnpm -C web test --run src/graph/NodePreview.test.tsx src/graph/SuggestionCard.test.tsx src/graph/GraphColumn.test.tsx`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the tests. Expected: PASS.
- [ ] **Step 5: Commit** "Preview a graph node on hover and stub suggested links".

### Task 8: The column in the shell

**Files:**
- Create: `web/src/hooks/useGraphWide.ts` (both media hooks), `web/src/hooks/useGraphColumn.ts` (collapsed state)
- Modify: `web/src/shell/MobileShell.tsx` (beside `NoteSidePanel`, `:532`; toggle and shortcut beside the side panel's, `:312-333`), `web/src/shell/TopBar.tsx` (a toggle after the side panel's, `:92-106`)
- Modify: `README.md` (the web app's description: the graph column, its widths, the placeholder similarity)
- Test: `web/src/hooks/useGraphWide.test.ts`, `web/src/shell/GraphColumnShell.test.tsx`

**Interfaces:**
- Consumes: `GraphColumn`, `graphColumnId` (Task 6).
- Produces:
  - `export const useGraphWide = mediaQueryHook("(min-width: 120rem)")`, `export const useGraphWideSplit = mediaQueryHook("(min-width: 180rem)")`.
  - `export function useGraphColumnCollapsed(): readonly [boolean, (next: boolean) => void]` under `kumbu.graphColumnCollapsed`, as `useSidePanelCollapsed`.
  - `export const graphToggleId = "graph-column-toggle"` in `TopBar.tsx`; the toggle `aria-label="Graph"`, `aria-expanded`, `aria-controls={graphColumnId}`, `aria-keyshortcuts="Control+Shift+G Meta+Shift+G"`, `title` "Hide the graph (Ctrl+Shift+G or ⌘⇧G)" / "Show the graph (…)", icon `Waypoints` from `lucide-react`.
  - In `MobileShell`: `const graph = !!shownNote && (split ? graphWideSplit : graphWide)`; `<GraphColumn path={shownNote} otherPath={split ? (focusedSide === "right" ? path : beside) : null} hidden={graphCollapsed} />` after the side panel. Ctrl+Shift+G (Cmd+Shift+G; `e.code === "KeyG"` too) toggles it, not while `topDialog()`; hiding while it holds the focus moves the focus to the toggle.

- [ ] **Step 1: Write the failing tests** (the `fakeMedia` pattern of `web/src/hooks/useWide.test.ts`; the shell rendered as the existing `MobileShell` tests do):
  - `useGraphWide` true at 120rem, false below; `useGraphWideSplit` the same at 180rem.
  - The column is shown at 120rem with a note, not at 80rem; in the split view not at 120rem, shown at 180rem; with the split view its foot names the other pane; collapsed survives a remount; Ctrl+Shift+G hides and shows it; with the focus inside, hiding moves the focus to `#graph-column-toggle`; no shortcut while a dialog is open.
- [ ] **Step 2: Run** `pnpm -C web test --run src/hooks/useGraphWide.test.ts src/shell/GraphColumnShell.test.tsx`. Expected: FAIL.
- [ ] **Step 3: Implement**, then the README paragraph.
- [ ] **Step 4: Run** the whole web suite and the build: `pnpm -C web test --run && pnpm -C web build`. Expected: all pass, build clean.
- [ ] **Step 5: Commit** "Show the graph column beside the note on wide screens".
