// SPDX-License-Identifier: AGPL-3.0-or-later
import { vaultOf } from "../api/paths";
import { dayAndMonth, partsOf } from "../calendar/dates";
import { flowMap, type Property } from "../note/properties";

const same = (a: string, b: string) => a.trim().toLocaleLowerCase("en") === b.trim().toLocaleLowerCase("en");

/**
 * A board card: a note directly in `/<vault>/Board/` with a `column`, but not `Board.md`; nothing under `Board/Archive/`.
 * Its column, and the day it entered it: `moved`'s entry for that column, matched ignoring case as the board does.
 */
export function boardCard(path: string, props: Property[] | null): { column: string; since: string | null } | null {
  const folder = `/${vaultOf(path)}/Board/`;
  if (!props || !path.startsWith(folder)) return null;
  const rest = path.slice(folder.length);
  // A note in a sub-folder (Archive/ among them) is none of the board's cards.
  if (rest === "Board.md" || rest.includes("/")) return null;
  const column = props.find((p) => p.key === "column")?.value;
  if (typeof column !== "string" || column.trim() === "") return null;
  const moved = props.find((p) => p.key === "moved")?.value;
  const map = typeof moved === "string" ? flowMap(moved) : null;
  const day = map && Object.entries(map).find(([name]) => same(name, column))?.[1];
  return { column: column.trim(), since: day && /^\d{4}-\d{2}-\d{2}(?:$|T)/.test(day) ? day.slice(0, 10) : null };
}

/** "14 Nov", or "14 Nov 2025" when it isn't `today`'s year. */
export function sinceText(day: string, today: string): string {
  const { year } = partsOf(day);
  return year === partsOf(today).year ? dayAndMonth(day) : `${dayAndMonth(day)} ${year}`;
}
