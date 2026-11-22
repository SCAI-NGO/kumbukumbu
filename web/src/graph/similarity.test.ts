// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { mockSimilarity, similarity } from "./similarity";

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
