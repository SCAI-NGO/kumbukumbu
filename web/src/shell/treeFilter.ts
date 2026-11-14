// SPDX-License-Identifier: AGPL-3.0-or-later
import { fileKindOfEntry, type TreeFolder, type TreeNote } from "../api/notes";
import { isFlag, type FlagName } from "../flags/api";

/** The flag a search keeps: any flag at all, or one of the eight. */
export type FlagFilter = "any" | FlagName;

/** The typed words, each in lower case: every one must be found. */
export const wordsOf = (typed: string) => typed.toLowerCase().split(/\s+/).filter(Boolean);

/**
 * The tree cut down to what a search finds (spec §3.1): the notes and attachments whose path within the vault (a
 * note's without `.md`, so "pump cert" finds `Pump/Certificate.md`) holds every word, ignoring case, and that carry
 * the flag asked for; with the folders on the way to them, which `open` names, and nothing else.
 */
export function filterTree(root: TreeFolder, typed: string, flag: FlagFilter | null): { root: TreeFolder; open: Set<string> } {
  const words = wordsOf(typed);
  const open = new Set<string>();
  const within = (n: TreeNote) => {
    const path = n.path.slice(root.path.length + 1);
    return (fileKindOfEntry(n) === "note" ? path.replace(/\.md$/i, "") : path).toLowerCase();
  };
  const flagged = (n: TreeNote) => flag === null || (flag === "any" ? isFlag(n.flag) : n.flag === flag);
  const matches = (n: TreeNote) => {
    if (!flagged(n)) return false;
    const path = within(n);
    return words.every((w) => path.includes(w));
  };
  const cut = (folder: TreeFolder): TreeFolder => {
    const folders = folder.folders.map(cut).filter((f) => f.folders.length + f.notes.length > 0);
    for (const f of folders) open.add(f.path);
    return { ...folder, folders, notes: folder.notes.filter(matches) };
  };
  return { root: cut(root), open };
}
