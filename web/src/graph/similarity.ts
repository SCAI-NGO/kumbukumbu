// SPDX-License-Identifier: AGPL-3.0-or-later

import type { GraphSimilarityView } from "./api";

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

const PLACEHOLDER: Similarity = { score: 0.5, measured: false };
export const pairKey = (a: string, b: string) => (a < b ? `${a}\n${b}` : `${b}\n${a}`);

/**
 * The similarity of each edge (its two ends' node keys), from the server's answer for the graph shown: measured where it
 * scored the pair, the placeholder (or the dev mock) elsewhere. The measured scores are stretched over [0, 1] across the
 * graph: raw cosines of related notes sit in a narrow band, which would draw every edge alike, and the graph compares one
 * note's neighbours with each other (spec 2026-11-22-graph-out-of-preview §1). With fewer than two distinct scores
 * nothing is stretched.
 */
export function edgeSimilarities(edges: readonly (readonly [string, string])[], answer: GraphSimilarityView | undefined): Similarity[] {
  const scores = new Map<string, number>();
  if (answer?.state === "ready") for (const p of answer.pairs) if (p.score !== null) scores.set(pairKey(p.from, p.to), Number(p.score));
  const shown = edges.flatMap(([a, b]) => (scores.has(pairKey(a, b)) ? [scores.get(pairKey(a, b))!] : []));
  const [low, high] = [Math.min(...shown), Math.max(...shown)];
  const stretch = high - low > 1e-6 ? (s: number) => (s - low) / (high - low) : (s: number) => s;
  return edges.map(([a, b]) => {
    const s = scores.get(pairKey(a, b));
    return s === undefined ? (mockOn() ? similarity(a, b) : PLACEHOLDER) : { score: stretch(s), measured: true };
  });
}

/**
 * The foot's line on the similarity, given how many distinct links between notes are shown and how many of them are
 * measured: nothing while asking or when all are, a count while some notes aren't embedded yet, else why not.
 */
export function similarityLine(links: number, measured: number, answer: GraphSimilarityView | undefined): string | null {
  if (!answer || links === 0 || measured === links) return null;
  if (answer.state === "preparing") return "Similarity not measured yet: search by meaning is getting ready";
  if (answer.state !== "ready" || (measured === 0 && answer.reason)) return answer.reason ? `Similarity not measured: ${answer.reason}` : "Similarity not measured";
  return `Similarity measured for ${measured} of ${links} ${links === 1 ? "link" : "links"}`;
}
