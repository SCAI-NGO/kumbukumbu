// SPDX-License-Identifier: AGPL-3.0-or-later
import type { GEdge, GNode } from "./model";

/** Where a node was in the last layout, by its key, which outlives one answer. */
export type Placed = { key: string; x: number; y: number };

const NEAR = 20;
const RING = 60;

/** Two numbers in [0, 1) from FNV-1a over the text, so a node starts in the same place every time. */
function jitter(text: string): [number, number] {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 0x01000193) >>> 0;
  return [(h & 0xffff) / 0x10000, (h >>> 16) / 0x10000];
}

/**
 * Where each node of an answer starts, by id: a node placed before keeps its place, the centre sits fixed at the
 * origin, a new node starts beside a neighbour already placed or, without one, on a ring around the centre.
 */
export function seedPositions(nodes: GNode[], edges: GEdge[], previous: ReadonlyMap<string, Placed>, centreKey: string): Map<string, { x: number; y: number; fixed: boolean }> {
  const seeds = new Map<string, { x: number; y: number; fixed: boolean }>();
  const centre = nodes.find((n) => n.key === centreKey);
  if (centre) seeds.set(centre.id, { x: 0, y: 0, fixed: true });

  // Two ghosts can share a key; the first keeps the place, the others start as new nodes with jitter of their own.
  const seen = new Map<string, number>();
  const fresh: { node: GNode; salt: string }[] = [];
  for (const n of nodes) {
    const count = seen.get(n.key) ?? 0;
    seen.set(n.key, count + 1);
    if (n === centre) continue;
    const p = count === 0 ? previous.get(n.key) : undefined;
    if (p) seeds.set(n.id, { x: p.x, y: p.y, fixed: false });
    else fresh.push({ node: n, salt: count === 0 ? n.key : `${n.key}\n${count}` });
  }

  const neighbours = new Map<string, string[]>();
  for (const e of edges) {
    neighbours.set(e.source, [...(neighbours.get(e.source) ?? []), e.target]);
    neighbours.set(e.target, [...(neighbours.get(e.target) ?? []), e.source]);
  }
  for (const { node, salt } of fresh) {
    const [u, v] = jitter(salt);
    const angle = 2 * Math.PI * u;
    const by = (neighbours.get(node.id) ?? []).map((id) => seeds.get(id)).find((p) => p !== undefined);
    const [cx, cy, r] = by ? [by.x, by.y, NEAR * (0.5 + 0.5 * v)] : [0, 0, RING];
    seeds.set(node.id, { x: cx + r * Math.cos(angle), y: cy + r * Math.sin(angle), fixed: false });
  }
  return seeds;
}
