// SPDX-License-Identifier: AGPL-3.0-or-later
import type { Row } from "./rows";

const groups: Row["group"][] = ["Note", "Commands", "Vaults", "Notes"];
const noteLimit = 50;

/**
 * Rows holding every word of the query in their place or name (a note action: in its group and name), favourites first, then by group; within a group the
 * rows where every word starts a word come before the rest, each alphabetical. At most 50 notes, so a large vault stays quick.
 */
export function filterRows(rows: Row[], query: string, favourites: ReadonlySet<string>): Row[] {
  const match = wordMatch(query);
  const ranked = rows.flatMap((row) => {
    // A note action is searched with its group, as it reads "Note: Publish", and not by its place: the note's title
    // would bring up every action wherever the title's words are typed to find another note.
    const rank = match(row.group === "Note" ? `note ${row.name}` : `${row.place} ${row.name}`);
    if (rank === null) return [];
    return [{ row, favourite: !!row.favourite && favourites.has(row.key), starts: rank === 0 }];
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

/**
 * How well a text answers the query, ignoring case: 0 when it holds every word of the query and each starts a word of
 * it, 1 when it only holds every word, null when a word is missing. The palette's rows and the link list's notes
 * (`rankNotes`) are ranked by it, the 0s first, then by name.
 */
export function wordMatch(query: string): (text: string) => 0 | 1 | null {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  const starting = words.map((w) => new RegExp(`[^\\p{L}\\p{N}]${w.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`, "u"));
  return (text) => {
    const lower = text.toLowerCase();
    if (!words.every((w) => lower.includes(w))) return null;
    return words.every((w, i) => lower.startsWith(w) || starting[i].test(lower)) ? 0 : 1;
  };
}
