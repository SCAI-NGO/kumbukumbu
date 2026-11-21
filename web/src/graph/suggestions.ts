// SPDX-License-Identifier: AGPL-3.0-or-later
import type { TreeFolder, TreeView } from "../api/notes";
import { mockOn, similarity, type Similarity } from "./similarity";

export type Suggestion = { path: string; title: string; similarity: Similarity };

function notesOf(folder: TreeFolder): string[] {
  return [...folder.notes.filter((n) => !n.kind || n.kind === "note").map((n) => n.path), ...folder.folders.flatMap(notesOf)];
}

/** Notes of the same vault that `path` might link to but does not show: for now only the dev mock makes any. `tree` is the path's vault's. */
export function suggestedLinks(path: string, shown: ReadonlySet<string>, tree: TreeView | undefined): Suggestion[] {
  if (!mockOn() || !tree) return [];
  return notesOf(tree.root)
    .filter((p) => p !== path && !shown.has(p))
    .map((p) => ({ path: p, title: (p.split("/").pop() ?? p).replace(/\.md$/, ""), similarity: similarity(path, p) }))
    .sort((a, b) => b.similarity.score - a.similarity.score || a.path.localeCompare(b.path))
    .slice(0, 3);
}
