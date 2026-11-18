// SPDX-License-Identifier: AGPL-3.0-or-later
import type { Row } from "./rows";

const groups: Row["group"][] = ["Note", "Commands", "Vaults", "Notes"];
const noteLimit = 50;

/**
 * Rows holding every word of the query in their place or name, favourites first, then by group; within a group the
 * rows where every word starts a word come before the rest, each alphabetical. At most 50 notes, so a large vault stays quick.
 */
export function filterRows(rows: Row[], query: string, favourites: ReadonlySet<string>): Row[] {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  const starting = words.map((w) => new RegExp(`[^\\p{L}\\p{N}]${w.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`, "u"));
  const ranked = rows.flatMap((row) => {
    // A note action is searched with its group, as it reads "Note: Publish".
    const text = `${row.group === "Note" ? "note " : ""}${row.place} ${row.name}`.toLowerCase();
    if (!words.every((w) => text.includes(w))) return [];
    const starts = words.every((w, i) => text.startsWith(w) || starting[i].test(text));
    return [{ row, favourite: !!row.favourite && favourites.has(row.key), starts }];
  });
  ranked.sort(
    (a, b) =>
      Number(b.favourite) - Number(a.favourite) ||
      groups.indexOf(a.row.group) - groups.indexOf(b.row.group) ||
      Number(b.starts) - Number(a.starts) ||
      a.row.name.localeCompare(b.row.name),
  );
  let notes = 0;
  return ranked.map((r) => r.row).filter((row) => row.group !== "Notes" || ++notes <= noteLimit);
}
