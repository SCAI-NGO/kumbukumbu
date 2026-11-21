// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GEdge, GNode, SizeBy } from "./model";
import { MIN_DISTANCE, collideRadius, radius } from "./scales";

const score = vi.hoisted(() => ({ value: 0.5 }));
vi.mock("./similarity", () => ({ similarity: () => ({ score: score.value, measured: false }) }));

const { useForceLayout } = await import("./useForceLayout");

const node = (id: string, over: Partial<GNode> = {}): GNode => ({ id, key: id, path: id, title: id, kind: "note", tags: [], in: 3, out: 2, depth: 1, size: null, writtenAt: null, leftOut: 0, ...over });
const edge = (source: string, target: string): GEdge => ({ source, target, flags: 0 });

// A centre with ten neighbours, three of which have a neighbour of their own.
const star = (centre: string) => {
  const ids = ["c", ...Array.from({ length: 10 }, (_, i) => `n${i}`), "m0", "m1", "m2"];
  const nodes = ids.map((id) => node(id, { depth: id === centre ? 0 : 1 }));
  const edges = [...ids.slice(1, 11).map((id) => edge("c", id)), edge("n0", "m0"), edge("n1", "m1"), edge("n2", "m2")];
  return { nodes, edges };
};

let frames: Map<number, FrameRequestCallback>;
let next: number;
const runFrames = (n: number) =>
  act(() => {
    for (let i = 0; i < n; i++) {
      const due = [...frames.entries()];
      frames.clear();
      for (const [, cb] of due) cb(performance.now());
    }
  });

beforeEach(() => {
  frames = new Map();
  next = 1;
  vi.stubGlobal("requestAnimationFrame", vi.fn((cb: FrameRequestCallback) => (frames.set(next, cb), next++)));
  vi.stubGlobal("cancelAnimationFrame", vi.fn((id: number) => frames.delete(id)));
  score.value = 0.5;
});
afterEach(() => vi.unstubAllGlobals());

type Opts = { centreKey: string; sizeBy: SizeBy; reducedMotion: boolean; running: boolean };
const opts = (over: Partial<Opts> = {}): Opts => ({ centreKey: "c", sizeBy: "links", reducedMotion: false, running: true, ...over });

describe("useForceLayout", () => {
  it("with reduced motion, shows the layout at rest on the first render, without overlaps", () => {
    const { nodes, edges } = star("c");
    const { result } = renderHook(() => useForceLayout(nodes, edges, opts({ reducedMotion: true })));
    const first = new Map(result.current.positions);
    expect(first.size).toBe(nodes.length);
    expect(first.get("c")).toEqual({ x: 0, y: 0 });
    runFrames(5);
    expect(result.current.positions).toEqual(first);
    for (const a of nodes)
      for (const b of nodes) {
        if (a === b) continue;
        const [p, q] = [first.get(a.id)!, first.get(b.id)!];
        expect(Math.hypot(p.x - q.x, p.y - q.y)).toBeGreaterThanOrEqual(collideRadius(radius(a, "links")) + collideRadius(radius(b, "links")) - 1);
      }
  });

  it("keeps linked nodes the minimum distance apart even at full similarity", () => {
    score.value = 1;
    const { nodes, edges } = star("c");
    const { result } = renderHook(() => useForceLayout(nodes, edges, opts({ reducedMotion: true })));
    for (const e of edges) {
      const [p, q] = [result.current.positions.get(e.source)!, result.current.positions.get(e.target)!];
      expect(Math.hypot(p.x - q.x, p.y - q.y)).toBeGreaterThanOrEqual(MIN_DISTANCE - 1);
    }
  });

  it("ticks on animation frames and stops when cooled", () => {
    const { nodes, edges } = star("c");
    const { result } = renderHook(() => useForceLayout(nodes, edges, opts()));
    const seeded = new Map(result.current.positions);
    runFrames(1);
    expect(result.current.positions).not.toEqual(seeded);
    runFrames(1000);
    expect(frames.size).toBe(0);
  });

  // A hint that changes only a time or a size gives new arrays of the same graph: the layout stays as it is.
  it("neither re-seeds nor warms the layout for new arrays holding the same graph", () => {
    const { result, rerender } = renderHook(({ g }: { g: ReturnType<typeof star> }) => useForceLayout(g.nodes, g.edges, opts()), { initialProps: { g: star("c") } });
    runFrames(1000);
    const rest = new Map(result.current.positions);
    vi.mocked(requestAnimationFrame).mockClear();
    const same = star("c");
    same.nodes = same.nodes.map((n) => ({ ...n, writtenAt: "2026-11-21T10:00:00Z", size: 12 }));
    rerender({ g: same });
    expect(requestAnimationFrame).not.toHaveBeenCalled();
    expect(result.current.positions).toEqual(rest);
  });

  it("warms the layout again when a radius changes", () => {
    const { rerender } = renderHook(({ g }: { g: ReturnType<typeof star> }) => useForceLayout(g.nodes, g.edges, opts()), { initialProps: { g: star("c") } });
    runFrames(1000);
    const more = star("c");
    more.nodes = more.nodes.map((n) => (n.id === "n4" ? { ...n, in: 40 } : n));
    rerender({ g: more });
    expect(frames.size).toBe(1);
  });

  it("cancels its pending frame on unmount", () => {
    const { nodes, edges } = star("c");
    const { unmount } = renderHook(() => useForceLayout(nodes, edges, opts()));
    expect(frames.size).toBe(1);
    const [pending] = frames.keys();
    vi.mocked(cancelAnimationFrame).mockClear();
    unmount();
    expect(cancelAnimationFrame).toHaveBeenCalledWith(pending);
    expect(frames.size).toBe(0);
  });

  it("does not tick while not running", () => {
    const { nodes, edges } = star("c");
    const { result, rerender } = renderHook((o: Opts) => useForceLayout(nodes, edges, o), { initialProps: opts() });
    runFrames(3);
    rerender(opts({ running: false }));
    expect(frames.size).toBe(0);
    const held = new Map(result.current.positions);
    runFrames(3);
    expect(result.current.positions).toEqual(held);
  });

  // The layout shifts so the new centre sits at the origin; every shared node keeps its place around it.
  it("keeps the positions of shared keys around the new centre on the first frame", () => {
    const first = star("c");
    const { result, rerender } = renderHook(({ g, o }: { g: ReturnType<typeof star>; o: Opts }) => useForceLayout(g.nodes, g.edges, o), { initialProps: { g: first, o: opts() } });
    runFrames(1000);
    const before = new Map(result.current.positions);
    // n0 becomes the centre: c, n0 and m0 stay, a new note joins n0.
    const nodes = [node("n0", { depth: 0 }), node("c"), node("m0"), node("x")];
    const edges = [edge("c", "n0"), edge("n0", "m0"), edge("n0", "x")];
    rerender({ g: { nodes, edges }, o: opts({ centreKey: "n0" }) });
    runFrames(1);
    const o = before.get("n0")!;
    for (const id of ["c", "m0"]) {
      const [p, q] = [before.get(id)!, result.current.positions.get(id)!];
      expect(Math.hypot(p.x - o.x - q.x, p.y - o.y - q.y)).toBeLessThanOrEqual(1);
    }
    expect(result.current.positions.get("n0")).toEqual({ x: 0, y: 0 });
    expect(result.current.positions.has("n5")).toBe(false);
  });

  it("settles when reduced motion turns on while a node is held", () => {
    const { nodes, edges } = star("c");
    const { result, rerender } = renderHook((o: Opts) => useForceLayout(nodes, edges, o), { initialProps: opts() });
    runFrames(3);
    act(() => result.current.drag("n3", 200, -150));
    runFrames(3);
    rerender(opts({ reducedMotion: true }));
    expect(frames.size).toBe(0);
    expect(result.current.positions.get("n3")).toEqual({ x: 200, y: -150 });
    const rest = new Map(result.current.positions);
    runFrames(3);
    expect(result.current.positions).toEqual(rest);
  });

  it("stops ticking after a release for a node a new answer has dropped", () => {
    const first = star("c");
    const { result, rerender } = renderHook(({ g }: { g: ReturnType<typeof star> }) => useForceLayout(g.nodes, g.edges, opts()), { initialProps: { g: first } });
    act(() => result.current.drag("n3", 200, -150));
    rerender({ g: { nodes: [node("c", { depth: 0 }), node("n0")], edges: [edge("c", "n0")] } });
    act(() => result.current.drag("n3", null, null));
    runFrames(1000);
    expect(frames.size).toBe(0);
  });

  it("leaves out edges whose ends are not in the answer", () => {
    const nodes = [node("c", { depth: 0 }), node("a")];
    const edges = [edge("c", "a"), edge("a", "gone")];
    const { result } = renderHook(() => useForceLayout(nodes, edges, opts({ reducedMotion: true })));
    expect(result.current.positions.size).toBe(2);
  });

  it("pins a dragged node while held and lets it go", () => {
    const { nodes, edges } = star("c");
    const { result } = renderHook(() => useForceLayout(nodes, edges, opts()));
    runFrames(1000);
    act(() => result.current.drag("n3", 200, -150));
    runFrames(3);
    expect(result.current.positions.get("n3")).toEqual({ x: 200, y: -150 });
    act(() => result.current.drag("n3", null, null));
    runFrames(3);
    expect(result.current.positions.get("n3")).not.toEqual({ x: 200, y: -150 });
  });
});
