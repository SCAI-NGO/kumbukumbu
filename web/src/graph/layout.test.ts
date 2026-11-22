// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { type Placed, seedPositions } from "./layout";
import type { GEdge, GNode } from "./model";

const node = (id: string, over: Partial<GNode> = {}): GNode => ({ id, key: id, path: id, title: id, kind: "note", tags: [], in: 0, out: 0, depth: 1, size: null, writtenAt: null, leftOut: 0, ...over });
const edge = (source: string, target: string): GEdge => ({ source, target, flags: 0 });
const placed = (...ps: Placed[]) => new Map(ps.map((p) => [p.key, p]));
const dist = (a: { x: number; y: number }, b: { x: number; y: number }) => Math.hypot(a.x - b.x, a.y - b.y);

describe("seedPositions", () => {
  it("keeps the position of a node already placed", () => {
    const seeds = seedPositions([node("c"), node("a")], [edge("c", "a")], placed({ key: "c", x: 0, y: 0 }, { key: "a", x: 37.25, y: -12.5 }), "c");
    expect(seeds.get("a")).toEqual({ x: 37.25, y: -12.5, fixed: false });
  });

  it("fixes the centre at the origin", () => {
    const seeds = seedPositions([node("a"), node("c")], [edge("c", "a")], placed({ key: "c", x: 50, y: 50 }), "c");
    expect(seeds.get("c")).toEqual({ x: 0, y: 0, fixed: true });
  });

  it("starts a new node within 20 px of a placed neighbour", () => {
    const seeds = seedPositions([node("c"), node("a"), node("b")], [edge("c", "a"), edge("a", "b")], placed({ key: "a", x: 100, y: 40 }), "c");
    const b = seeds.get("b")!;
    expect(dist(b, { x: 100, y: 40 })).toBeLessThanOrEqual(20);
    expect(b.fixed).toBe(false);
  });

  it("starts a new node without a placed neighbour on a ring around the centre", () => {
    const seeds = seedPositions([node("c"), node("a")], [], new Map(), "c");
    expect(dist(seeds.get("a")!, { x: 0, y: 0 })).toBeCloseTo(60);
  });

  it("is deterministic", () => {
    const run = () => seedPositions([node("c"), node("a"), node("b")], [edge("c", "a")], new Map(), "c");
    expect(run()).toEqual(run());
  });

  it("places two ghosts with the same title by their ids", () => {
    const ghost = (id: string) => node(id, { key: "ghost:Later", path: null, title: "Later", kind: "ghost" });
    const seeds = seedPositions([node("c"), ghost("ghost:1"), ghost("ghost:2")], [edge("c", "ghost:1"), edge("c", "ghost:2")], placed({ key: "c", x: 0, y: 0 }), "c");
    expect(seeds.has("ghost:1")).toBe(true);
    expect(seeds.has("ghost:2")).toBe(true);
    expect(dist(seeds.get("ghost:1")!, seeds.get("ghost:2")!)).toBeGreaterThan(0);
  });

  it("leaves out a node gone from the answer", () => {
    const seeds = seedPositions([node("c")], [], placed({ key: "c", x: 0, y: 0 }, { key: "gone", x: 10, y: 10 }), "c");
    expect([...seeds.keys()]).toEqual(["c"]);
  });
});
