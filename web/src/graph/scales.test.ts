// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import type { GNode } from "./model";
import { darkRamp, lightRamp, neutral } from "./ramps";
import { GAP, MIN_DISTANCE, R_MAX, R_MIN, ageColour, collideRadius, edgeOpacity, edgeWidth, linkDistance, measureOf, radius } from "./scales";

const node = (over: Partial<GNode> = {}): GNode => ({ id: "a", key: "a", path: "/a.md", title: "a", kind: "note", tags: [], in: 0, out: 0, depth: 0, size: null, writtenAt: null, leftOut: 0, ...over });

describe("measureOf", () => {
  it("reads links, backlinks and length", () => {
    const n = node({ in: 2, out: 3, size: 40 });
    expect([measureOf(n, "links"), measureOf(n, "backlinks"), measureOf(n, "length")]).toEqual([5, 2, 40]);
  });
  it("counts an unknown size as 0", () => expect(measureOf(node({ size: null }), "length")).toBe(0));
});

describe("radius", () => {
  it("grows with the measure and stays in range", () => {
    expect(radius(node({ in: 1 }), "links")).toBeLessThan(radius(node({ in: 10 }), "links"));
    for (const m of [0, 10_000]) {
      const r = radius(node({ in: m }), "links");
      expect(r).toBeGreaterThanOrEqual(R_MIN);
      expect(r).toBeLessThanOrEqual(R_MAX);
    }
    expect(radius(node({ size: 0 }), "length")).toBe(R_MIN);
    expect(radius(node({ size: 10_000_000 }), "length")).toBe(R_MAX);
  });
  it("keeps attachments and ghosts the same whatever the measure", () => {
    for (const kind of ["attachment", "ghost"] as const) {
      const rs = (["links", "backlinks", "length"] as const).map((by) => radius(node({ kind, in: 50, out: 50, size: 90_000 }), by));
      expect(new Set(rs).size).toBe(1);
    }
    expect(radius(node({ kind: "ghost" }), "links")).toBeLessThan(radius(node({ kind: "attachment" }), "links"));
    expect(radius(node({ kind: "ghost" }), "links")).toBeLessThan(R_MIN);
  });
  it("sizes a day note like a note", () => expect(radius(node({ kind: "day", in: 5 }), "links")).toBe(radius(node({ in: 5 }), "links")));
});

describe("ageColour", () => {
  const now = Date.UTC(2026, 10, 21);
  const ago = (ms: number) => new Date(now - ms).toISOString();
  it("gives the neutral colour for no time", () => {
    expect(ageColour(null, now, true)).toBe(neutral.dark);
    expect(ageColour(null, now, false)).toBe(neutral.light);
  });
  it("gives the neutral colour for a time it can't read", () => {
    expect(ageColour("not a time", now, true)).toBe(neutral.dark);
    expect(ageColour("", now, false)).toBe(neutral.light);
  });
  it("gives the first stop up to an hour old and the last from half a year", () => {
    expect(ageColour(ago(3_600_000), now, true)).toBe(darkRamp[0]);
    expect(ageColour(ago(60_000), now, true)).toBe(darkRamp[0]);
    expect(ageColour(ago(365 * 86_400_000), now, true)).toBe(darkRamp[4]);
    expect(ageColour(ago(365 * 86_400_000), now, false)).toBe(lightRamp[4]);
  });
  it("differs between dark and light for one age", () => {
    expect(ageColour(ago(86_400_000), now, true)).not.toBe(ageColour(ago(86_400_000), now, false));
  });
  it("blends between stops", () => {
    const c = ageColour(ago(5 * 86_400_000), now, true);
    expect(c).toMatch(/^#[0-9a-f]{6}$/);
    expect(darkRamp).not.toContain(c);
  });
});

describe("edges and forces", () => {
  it("shortens the distance as similarity rises", () => {
    expect(linkDistance(1)).toBe(MIN_DISTANCE);
    expect(linkDistance(0)).toBeGreaterThan(linkDistance(0.5));
    expect(linkDistance(0.5)).toBeGreaterThan(linkDistance(1));
  });
  it("thickens and strengthens with similarity", () => {
    expect(edgeWidth(1)).toBeGreaterThan(edgeWidth(0));
    expect(edgeOpacity(1)).toBeGreaterThan(edgeOpacity(0));
  });
  it("pads the collision radius", () => expect(collideRadius(10)).toBe(10 + GAP));
});
