// SPDX-License-Identifier: AGPL-3.0-or-later

/** How alike two nodes are; `measured` is false until the server can say. */
export type Similarity = { score: number; measured: boolean };

/** The development mock: only in a dev build, only when switched on in the browser. */
export const mockOn = (): boolean => {
  if (!import.meta.env.DEV) return false;
  try {
    return localStorage.getItem("kumbu.graph.mockSimilarity") === "1";
  } catch {
    return false;
  }
};

/** A deterministic stand-in: FNV-1a over the two keys, sorted so the order of the pair does not matter, mapped to [0, 1]. */
export function mockSimilarity(a: string, b: string): number {
  const text = [a, b].sort().join("\n");
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 0x01000193) >>> 0;
  return h / 0xffffffff;
}

/** The one place the graph asks for similarity; keys are node keys (paths, or `ghost:<title>`). */
export function similarity(a: string, b: string): Similarity {
  return mockOn() ? { score: mockSimilarity(a, b), measured: false } : { score: 0.5, measured: false };
}
