// SPDX-License-Identifier: AGPL-3.0-or-later
import type { TreeFolder, TreeNote, TreeView } from "../api/notes";
import type { LocalGraphView } from "./api";

export type Kind = "note" | "day" | "attachment" | "ghost";
export type SizeBy = "links" | "backlinks" | "length";

/** `id` is unique within one answer; `key` is stable across answers, so a node keeps its place when the graph is refetched. */
export type GNode = {
  id: string;
  key: string;
  path: string | null;
  title: string;
  kind: Kind;
  tags: string[];
  in: number;
  out: number;
  depth: number;
  size: number | null;
  writtenAt: string | null;
  leftOut: number;
};

/** `s`: how alike the two ends are, 0 to 1, once known (`withSimilarity`); the placeholder until then. */
export type GEdge = { source: string; target: string; flags: number; s?: number };

/** The note or file at `path` in a folder's tree, if it is there. */
export function treeEntry(root: TreeFolder, path: string): TreeNote | null {
  const here = root.notes.find((n) => n.path === path);
  if (here) return here;
  for (const f of root.folders) {
    if (!path.startsWith(`${f.path}/`)) continue;
    const found = treeEntry(f, path);
    if (found) return found;
  }
  return null;
}

/** The graph's nodes and edges by id, each node with the size and time of writing the tree knows of it. */
export function toModel(local: LocalGraphView, tree: TreeView | undefined): { nodes: GNode[]; edges: GEdge[] } {
  const nodes = local.nodes.map((n, i): GNode => {
    const entry = n.path && tree ? treeEntry(tree.root, n.path) : null;
    return {
      id: n.path ?? `ghost:${i}`,
      key: n.path ?? `ghost:${n.title}`,
      path: n.path ?? null,
      title: n.title,
      kind: n.kind as Kind,
      tags: n.tags,
      in: Number(n.in ?? 0),
      out: Number(n.out ?? 0),
      depth: Number(n.depth ?? 0),
      size: entry ? Number(entry.size) : null,
      writtenAt: entry?.writtenAt ?? null,
      leftOut: Number(local.leftOut[String(i)] ?? 0),
    };
  });
  // An index past the nodes would be an edge to nothing: left out rather than thrown on.
  const edges = local.edges.flatMap(([from, to, flags]): GEdge[] => {
    const [a, b] = [nodes[Number(from)], nodes[Number(to)]];
    return a && b ? [{ source: a.id, target: b.id, flags: Number(flags) }] : [];
  });
  return { nodes, edges };
}
