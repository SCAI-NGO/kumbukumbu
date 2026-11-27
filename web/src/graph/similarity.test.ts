// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import type { GraphSimilarityView } from "./api";
import { edgeSimilarities, mockSimilarity, similarity, similarityLine } from "./similarity";

const FLAG = "kumbu.graph.mockSimilarity";

afterEach(() => {
  vi.unstubAllEnvs();
  localStorage.clear();
});

describe("similarity", () => {
  it("is the placeholder in production builds, flag or not", () => {
    vi.stubEnv("DEV", false);
    localStorage.setItem(FLAG, "1");
    expect(similarity("/Work/a.md", "/Work/b.md")).toEqual({ score: 0.5, measured: false });
  });

  it("is the placeholder in dev without the flag", () => {
    vi.stubEnv("DEV", true);
    expect(similarity("/Work/a.md", "/Work/b.md")).toEqual({ score: 0.5, measured: false });
  });

  it("is the mock's score, still unmeasured, in dev with the flag", () => {
    vi.stubEnv("DEV", true);
    localStorage.setItem(FLAG, "1");
    expect(similarity("/Work/a.md", "/Work/b.md")).toEqual({ score: mockSimilarity("/Work/a.md", "/Work/b.md"), measured: false });
  });
});

describe("mockSimilarity", () => {
  const pairs: [string, string][] = [
    ["/Work/a.md", "/Work/b.md"],
    ["/Work/a.md", "/Work/c.md"],
    ["/Work/x.md", "ghost:Idea"],
  ];

  it("is symmetric, stable and within [0, 1]", () => {
    for (const [a, b] of pairs) {
      const s = mockSimilarity(a, b);
      expect(s).toBe(mockSimilarity(b, a));
      expect(s).toBe(mockSimilarity(a, b));
      expect(s).toBeGreaterThanOrEqual(0);
      expect(s).toBeLessThanOrEqual(1);
    }
  });

  it("tells pairs apart", () => {
    expect(new Set(pairs.map(([a, b]) => mockSimilarity(a, b))).size).toBeGreaterThanOrEqual(2);
  });
});

describe("edgeSimilarities", () => {
  const A = "/Work/a.md";
  const B = "/Work/b.md";
  const C = "/Work/c.md";
  const D = "/Work/d.md";
  const view = (pairs: [string, string, number | null][], state = "ready", reason: string | null = null): GraphSimilarityView => ({
    state,
    reason,
    pairs: pairs.map(([from, to, score]) => ({ from, to, score })),
  });

  it("stretches the measured scores of the graph shown over [0, 1], whichever way an edge runs", () => {
    const out = edgeSimilarities(
      [
        [A, B],
        [C, A],
        [A, D],
      ],
      view([
        [A, B, 0.6],
        [A, C, 0.4],
        [A, D, 0.5],
      ]),
    );
    expect(out.map((s) => s.measured)).toEqual([true, true, true]);
    expect(out[0].score).toBeCloseTo(1);
    expect(out[1].score).toBeCloseTo(0);
    expect(out[2].score).toBeCloseTo(0.5);
  });

  it("leaves a single measured score as it is, and an unmeasured edge the placeholder", () => {
    const out = edgeSimilarities(
      [
        [A, B],
        [A, C],
        [A, "ghost:Anna"],
      ],
      view([
        [A, B, 0.62],
        [A, C, null],
      ]),
    );
    expect(out).toEqual([
      { score: 0.62, measured: true },
      { score: 0.5, measured: false },
      { score: 0.5, measured: false },
    ]);
  });

  it("is the placeholder everywhere without an answer or when the server can't measure", () => {
    expect(edgeSimilarities([[A, B]], undefined)).toEqual([{ score: 0.5, measured: false }]);
    expect(edgeSimilarities([[A, B]], view([], "off", "search by meaning is off on this server"))).toEqual([{ score: 0.5, measured: false }]);
  });
});

describe("similarityLine", () => {
  const ready = (n: number): GraphSimilarityView => ({ state: "ready", reason: null, pairs: Array.from({ length: n }, (_, i) => ({ from: "/W/a.md", to: `/W/${i}.md`, score: 0.5 })) });

  it("says nothing while asking, without links between notes, or when every link is measured", () => {
    expect(similarityLine(3, 3, undefined)).toBeNull();
    expect(similarityLine(0, 0, ready(0))).toBeNull();
    expect(similarityLine(3, 3, ready(3))).toBeNull();
  });

  it("counts the measured links while some notes aren't embedded yet", () => {
    expect(similarityLine(3, 2, ready(3))).toBe("Similarity measured for 2 of 3 links");
    expect(similarityLine(1, 0, { state: "ready", reason: null, pairs: [{ from: "/W/a.md", to: "/W/b.md", score: null }] })).toBe("Similarity measured for 0 of 1 link");
  });

  it("says the server is busy when it answered so", () => {
    expect(similarityLine(2, 0, undefined, true)).toBe("Similarity not measured: the server is busy. Try again in a moment.");
    expect(similarityLine(2, 2, ready(2), true)).toBeNull();
  });

  it("gives the server's reason when it can't measure", () => {
    expect(similarityLine(2, 0, { state: "off", reason: "search by meaning is off on this server", pairs: [] })).toBe("Similarity not measured: search by meaning is off on this server");
    expect(similarityLine(2, 0, { state: "preparing", reason: null, pairs: [] })).toBe("Similarity not measured yet: search by meaning is getting ready");
    expect(similarityLine(2, 0, { state: "ready", reason: "Work: its embedding provider isn't configured on this server any more", pairs: [] })).toBe(
      "Similarity not measured: Work: its embedding provider isn't configured on this server any more",
    );
  });
});
