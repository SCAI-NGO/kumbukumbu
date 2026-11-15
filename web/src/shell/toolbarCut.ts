// SPDX-License-Identifier: AGPL-3.0-or-later

/** The toolbar's targets, in the row's order (UI round spec §2.1). */
export type Tool = "newNote" | "upload" | "calendar" | "board" | "recent" | "sort" | "find" | "menu";

/** Each icon's target; they sit without a gap, as each is 48 px already. */
const icon = 48;

/** What the narrow row gives up first, in this order; then whatever doesn't fit, from the right. */
const rarer: Tool[] = ["upload", "sort", "recent"];

/**
 * Which of `tools` fit in a row `width` px wide, + New note being `newNote` px, and which go into ⋯ (at its top, in the
 * order returned). + New note and ⋯ always stay. A width of 0 is one not known (nothing laid out yet, or no layout at
 * all): everything shows.
 */
export function cut(width: number, tools: readonly Tool[], newNote: number): { shown: Tool[]; inMenu: Tool[] } {
  const widthOf = (shown: readonly Tool[]) => shown.reduce((sum, t) => sum + (t === "newNote" ? newNote : icon), 0);
  let shown = [...tools];
  const inMenu: Tool[] = [];
  if (width <= 0 || widthOf(shown) <= width) return { shown, inMenu };
  for (const t of rarer) {
    if (!shown.includes(t)) continue;
    shown = shown.filter((s) => s !== t);
    inMenu.push(t);
    if (widthOf(shown) <= width) return { shown, inMenu };
  }
  const fromRight: Tool[] = [];
  for (const t of [...shown].reverse()) {
    if (widthOf(shown) <= width) break;
    if (t === "newNote" || t === "menu") continue;
    shown = shown.filter((s) => s !== t);
    fromRight.unshift(t);
  }
  return { shown, inMenu: [...inMenu, ...fromRight] };
}
