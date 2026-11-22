# The graph dialog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Where no graph column fits, the Waypoints button and Ctrl+Shift+G open the focused note's local graph as a dialog, touch-ready, in which a tap selects a node and shows its card before anything opens.

**Architecture:** `GraphColumn` splits into a frame-agnostic `GraphView` (toolbar, canvas, cards, list, foot) and two frames: the existing column and a new `GraphDialog`. `GraphCanvas` gains a select mode, pinch zoom, a double tap to fit and coarse-pointer hit circles. `MobileShell` decides column or dialog from the width and the split; `TopBar`'s graph button changes its ARIA to match.

**Tech Stack:** React 19, TypeScript, Tailwind 4, TanStack Query 5, d3-force, Vitest with Testing Library. pnpm.

**Spec:** `docs/superpowers/specs/2026-11-21-graph-view-design.md`, §12 (and §1–§10 for the column it extends).

## Global Constraints

- No server change; only `web/` (and `README.md`).
- Every new source file starts with `// SPDX-License-Identifier: AGPL-3.0-or-later`.
- British English in code, comments, copy and commits; commit messages imperative, no prefix, ending with the session's attribution lines.
- Copy, exact: dialog subtitle `Graph`; card buttons `Open` and `Open beside`; toolbar disclosure `Options`; the graph button's label stays `Graph`.
- Widths: column for one note from `120rem`, in the split view from `180rem`; dialog everywhere else. "Phone" is below `lg` (`useDesktop`, 64rem); `sm` is 40rem.
- Coarse pointer: `(pointer: coarse)`; hit circle radius at least 22 px in screen pixels.
- Tests first; run single files with `pnpm -C web test --run <file>`. Two `SkillPage.test.tsx` failures exist on `master` and are not ours.

## Review Focus

1. **The dialog open when the window widens into column territory** (or the split closes): the dialog should close and the column take over, not both show. Test in Task 3.
2. **The note changes under an open dialog** (a sync rename, the other pane focused): the dialog follows the focused note, and a selection from the old graph is cleared. Test in Task 2 (selection cleared on a new `path`).
3. **A pinch that starts on a node:** two fingers zoom; a node must not be dragged by the first finger meanwhile. Test in Task 1.
4. **Tap on a ghost in select mode:** it selects and shows "No note yet" with no button, rather than doing nothing (in the column a ghost still does nothing). Test in Task 1 and Task 2.
5. **Escape with the card open:** the first Escape closes the card, the second the dialog. Test in Task 3.

---

### Task 1: The canvas learns to select, pinch and be tapped

**Files:**
- Modify: `web/src/graph/GraphCanvas.tsx`
- Test: `web/src/graph/GraphCanvas.test.tsx`

**Interfaces:**
- Produces, new props on `GraphCanvas`: `select?: boolean` (default false), `selectedKey?: string | null`, `onSelect?(node: GNode | null): void`. With `select`, a click or tap on any node (ghost included) and Enter call `onSelect(node)` instead of `onOpen`; a click or tap on the background (not a pan) calls `onSelect(null)`; Ctrl+click is a select too. The selected node (`n.key === selectedKey`) gets `aria-pressed="true"` and a dashed ring (`data-selected-ring`, `r + 7`, accent colour).
- Without `select`, behaviour is unchanged (every existing test passes untouched).

- [ ] **Step 1: Write the failing tests** in a new `describe("select mode")` block and a `describe("touch")` block:
  - `"a click selects instead of opening"`: render with `select`, click Plan's node → `onSelect` called with Plan, `onOpen` not called.
  - `"Enter selects, a ghost selects too"`: focus the ghost, press Enter → `onSelect` with the ghost.
  - `"a click on the background clears the selection"`: pointerdown+pointerup+click on the svg with no movement → `onSelect(null)`.
  - `"the selected node is marked"`: `selectedKey` = Plan's key → its `role=button` has `aria-pressed="true"` and contains `[data-selected-ring]`; others have none.
  - `"a coarse pointer gives every node a hit circle of 22 px or more"`: stub `matchMedia` so `(pointer: coarse)` matches; each node `g` holds a `circle[data-hit]` with `fill="transparent"` and `r` ≥ 22 × (view width / svg width) (with the fallback size, view and svg are equal at the start, so assert `r >= 22`). Without the stub no `[data-hit]`.
  - `"two pointers pinch the zoom around their midpoint"`: pointerdown ids 1 at (200,280) and 2 at (360,280), move 2 to (520,280) → the svg's `viewBox` width shrinks (zoomed in by the distance ratio 320/160 = 2, within the clamp) and the midpoint's graph coordinate stays where it was (±1).
  - `"a pinch starting on a node drags nothing"`: pointerdown id 1 on Plan's node, id 2 on the background, move both → `drag` positions of Plan unchanged (assert its `transform` before and after).
  - `"a double tap on the background fits the view again"`: zoom by wheel, then two clicks on the background within 300 ms (`detail: 2` on the second, or two `pointerup`s via fake timers) → `viewBox` equals the one before zooming.
- [ ] **Step 2: Run them** — `pnpm -C web test --run src/graph/GraphCanvas.test.tsx` — the new ones fail.
- [ ] **Step 3: Implement.** Keep a `Map<pointerId, {x,y}>` of active pointers; with two, scale the view by the change of their distance around the graph point under their midpoint (clamped like the wheel), and cancel any node press (`press.current = null` without a drag). Hit circle from `useCoarsePointer = mediaQueryHook("(pointer: coarse)")`, its radius `max(r, 22 * view.w / svgWidth)`, drawn first in the node's `g`. Double tap: a background click with `e.detail >= 2` resets `manual` to null.
- [ ] **Step 4: Run** the same file — all pass. Then `pnpm -C web test --run src/graph` — all pass.
- [ ] **Step 5: Commit** — "Let the graph canvas select nodes, pinch and fit on a double tap".

### Task 2: One graph, two frames

**Files:**
- Create: `web/src/graph/GraphView.tsx`, `web/src/graph/NodeCard.tsx`, `web/src/graph/NodeCard.test.tsx`
- Modify: `web/src/graph/GraphColumn.tsx` (becomes the frame), `web/src/graph/NodePreview.tsx` (clamp to the body's width, not 560), `web/src/graph/GraphColumn.test.tsx`
- Test: `web/src/graph/GraphView.test.tsx` (new; the dialog-mode cases), existing `GraphColumn.test.tsx` keeps passing

**Interfaces:**
- Consumes: Task 1's `select`, `selectedKey`, `onSelect`.
- Produces:
  - `GraphView({ path, otherPath, running, mode, onOpened }: { path: string; otherPath: string | null; running: boolean; mode: "column" | "dialog"; onOpened?(): void })`: everything inside today's `<aside>` (toolbar, body, list, foot) in a `flex min-h-0 flex-1 flex-col` wrapper. `mode="dialog"` passes `select` to the canvas, keeps the selected node per `path` (cleared when `path` changes), shows `NodeCard` for it, and calls `onOpened()` after any open (card buttons and the list). In `mode="dialog"` below `sm` (`mediaQueryHook("(min-width: 40rem)")` false) the toolbar shows depth, List and Fit plus an `Options` button (`aria-expanded`, `aria-controls`) that discloses a second row with the five switches and "Size by".
  - `GraphColumn({ path, otherPath, hidden })`: unchanged signature; `<aside id={graphColumnId} …>` around `<GraphView mode="column" running={!hidden} …/>`.
  - `NodeCard({ node, anchor, width, beside, onOpen, onClose }: { node: GNode; anchor: {x:number;y:number}; width: number; beside: boolean; onOpen(node: GNode, beside: boolean): void; onClose(): void })`: `role="dialog"`-free card (`role="group"`, `aria-label={node.title}`) with the preview's content (reuse `NodePreview`'s inner markup by extracting `PreviewBody({ node })` in `NodePreview.tsx`) plus buttons `Open` and, when `beside`, `Open beside`; a ghost gets no buttons. Escape on it calls `onClose` and stops propagation.

- [ ] **Step 1: Write the failing tests.**
  - `NodeCard.test.tsx`: `"shows Open and Open beside"` (beside true) → click `Open` → `onOpen(node, false)`; `Open beside` → `onOpen(node, true)`; `"beside false shows Open only"`; `"a ghost has no buttons and says No note yet"`; `"Escape closes it"`.
  - `GraphView.test.tsx` (mock `GraphCanvas` as `GraphColumn.test.tsx` does, its buttons calling `onSelect` when `select` is set): `"in the dialog a tap selects and shows the card, Open opens and reports"` → click "node Plan" → card named Plan; click Open → the location is Plan's view URL and `onOpened` was called once; `"Open beside opens in the right pane"` (wide: stub `(min-width: 40rem)` true) → location has `beside`; `"a new note clears the selection"` → rerender with another `path` → no card; `"below sm the options sit behind Options"` → no `Day notes` button until `Options` is clicked, then it is, and `Options` has `aria-expanded="true"`; `"a ghost shows its card without buttons"`.
  - `GraphColumn.test.tsx`: unchanged, must stay green (the column still opens on click).
- [ ] **Step 2: Run** `pnpm -C web test --run src/graph` — the new ones fail.
- [ ] **Step 3: Implement** by moving the body of `GraphColumn` into `GraphView` unchanged and adding the dialog branch. `Open` goes through the existing `open(n, beside)`; the card's anchor is the node's last pointer/focus spot (the existing `spot`), clamped by `NodePreview`'s rule to the body's measured width.
- [ ] **Step 4: Run** `pnpm -C web test --run src/graph` — all pass; `pnpm -C web build` — clean.
- [ ] **Step 5: Commit** — "Split the graph from its column, and let it select first in a dialog".

### Task 3: The dialog in the shell

**Files:**
- Create: `web/src/graph/GraphDialog.tsx`
- Modify: `web/src/shell/MobileShell.tsx:338-362,515,562`, `web/src/shell/TopBar.tsx:15-31,86-127`, `README.md` ("The web app", the graph paragraph)
- Test: `web/src/shell/GraphColumnShell.test.tsx` (extend; rename the `describe` to cover the dialog)

**Interfaces:**
- Consumes: Task 2's `GraphView`.
- Produces:
  - `GraphDialog({ path, otherPath, onClose }: { path: string; otherPath: string | null; onClose(): void })`, built on `useDialog` like `Sheet` (`web/src/components/Sheet.tsx`) but its own markup: `fixed inset-0` full screen below `sm`; from `sm` a centred panel `w-[min(90vw,960px)] h-[min(85dvh,800px)]` over `bg-black/55`, a tap beside closes. Header: the note's title (`h2`, `data-autofocus`, `tabIndex={-1}`), `Graph` below it in `text-dim`, close button `aria-label="Close graph"`. Body: `<GraphView mode="dialog" running path otherPath onOpened={onClose} />`.
  - `TopBar` prop `graph` becomes `{ expanded: boolean; toggle(): void; dialog: boolean } | null`. With `dialog`, the button has `aria-haspopup="dialog"` and no `aria-expanded`/`aria-controls`, title `Show the graph (Ctrl+Shift+G or ⌘⇧G)`. The button shows when `graph` is set; on a phone (`!useDesktop()`) only in read mode, placed right after Outline.
  - `MobileShell`: `const graphColumn = !!shownNote && (split ? graphWideSplit : graphWide)`; `const graphDialogAllowed = !!shownNote && !graphColumn && (desktop || mode === "read")`; state `graphDialog: boolean`, closed whenever `graphColumn` becomes true or `shownNote` goes. Ctrl+Shift+G toggles the column where it fits, else opens the dialog (not while another dialog is open: the existing `topDialog()` check, which the open graph dialog itself also blocks; it closes by Escape). `otherPath` as for the column.

- [ ] **Step 1: Write the failing tests** in `GraphColumnShell.test.tsx`, reusing its width stubs:
  - `"below 1920 px the button opens the dialog"`: width 1440 → button `Graph` has `aria-haspopup="dialog"`; click → `role=dialog` named by the note's title, containing `Graph`.
  - `"in the split view between 1920 and 2880 px it is a dialog too"`; `"from 2880 px the split view has the column"` (existing case, keep).
  - `"Ctrl+Shift+G opens the dialog where no column fits"`.
  - `"on a phone the button shows in read mode only"`: width 390, read → button present; switch to write → absent.
  - `"Escape closes the card first, then the dialog, and the focus returns to the button"`.
  - `"widening the window into the column closes the dialog"`: open at 1440, change the stub to 1920 and fire its change → no dialog, the column (`complementary` named Graph) shown.
  - `"Open in the dialog opens the note and closes the dialog"` (one end-to-end case through the real `GraphView`, canvas mocked as in Task 2).
- [ ] **Step 2: Run** `pnpm -C web test --run src/shell/GraphColumnShell.test.tsx` — new ones fail.
- [ ] **Step 3: Implement** `GraphDialog`, the `TopBar` and `MobileShell` changes. Lazy-load `GraphDialog` with `lazy(() => import("../graph/GraphDialog"))` inside the existing `Suspense` pattern used for the sheets, so phones don't pay for d3 until they open it.
- [ ] **Step 4: Run** `pnpm -C web test --run src/shell src/graph` — all pass; then the whole suite `pnpm -C web test --run` (only the two known SkillPage failures) and `pnpm -C web build`.
- [ ] **Step 5: README** — in the graph paragraph, one sentence: where no column fits (narrower windows, phones, the split view below 2880 px), the Waypoints button opens the graph as a dialog, in which a tap shows a note's card and Open opens it.
- [ ] **Step 6: Commit** — "Open the graph as a dialog where no column fits".
