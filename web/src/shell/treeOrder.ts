// SPDX-License-Identifier: AGPL-3.0-or-later
import { useState } from "react";
import type { TreeFolder } from "../api/notes";

/** The tree's order (spec §3.2): by name, as the server sends it, or by last change, newest first. */
export type TreeOrder = "name" | "changed";

/** The time of a write, or none: what has none goes last. */
const timeOf = (at: string | null | undefined) => (at ? Date.parse(at) : NaN);

const newestFirst = (a: { writtenAt?: string | null }, b: { writtenAt?: string | null }) => {
  const [x, y] = [timeOf(a.writtenAt), timeOf(b.writtenAt)];
  if (Number.isNaN(x) || Number.isNaN(y)) return Number(Number.isNaN(x)) - Number(Number.isNaN(y));
  return y - x;
};

/**
 * The tree in the order chosen. By last change a folder is as new as the newest write anywhere below it (the server's
 * `writtenAt` of the folder), an empty one without a marker has no time and goes last; ties keep the name's order (a
 * stable sort of the server's). The folders of a level stay above its files: `rows` puts them first in either order.
 */
export function orderTree(root: TreeFolder, order: TreeOrder): TreeFolder {
  if (order === "name") return root;
  const sorted = (folder: TreeFolder): TreeFolder => ({
    ...folder,
    folders: folder.folders.map(sorted).sort(newestFirst),
    notes: [...folder.notes].sort(newestFirst),
  });
  return sorted(root);
}

const orderKey = "kumbu.treeOrder";

/** The order this device chose last, for every vault (as the gallery's); storage may be blocked, so every access is guarded. */
function storedOrder(): TreeOrder {
  try {
    return localStorage.getItem(orderKey) === "changed" ? "changed" : "name";
  } catch {
    return "name";
  }
}

export function useTreeOrder(): [TreeOrder, (order: TreeOrder) => void] {
  const [order, setOrder] = useState(storedOrder);
  const choose = (next: TreeOrder) => {
    setOrder(next);
    try {
      localStorage.setItem(orderKey, next);
    } catch {
      // The order still applies for this visit.
    }
  };
  return [order, choose];
}
