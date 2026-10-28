// SPDX-License-Identifier: AGPL-3.0-or-later

// The server's order for text (`TextOrder.Default`, Kumbu.Core/Sh/TextOrder.cs): the invariant culture's comparison,
// which is ICU's root collation (Ü next to U, case after letters and accents, no numeric order: "img10" before
// "img2"), and ties broken by code unit, as .NET's ordinal comparison does, so the order is total.
const collator = new Intl.Collator("und");

export const textOrder = (a: string, b: string): number => collator.compare(a, b) || (a < b ? -1 : a > b ? 1 : 0);

/**
 * Folders (`/Work/Projects/Teaser`) as the tree orders them, a segment at a time, each segment in text order; a
 * folder comes before those below it.
 */
export function folderOrder(a: string, b: string): number {
  const x = a.split("/");
  const y = b.split("/");
  for (let i = 0; i < Math.min(x.length, y.length); i++) {
    const c = textOrder(x[i], y[i]);
    if (c !== 0) return c;
  }
  return x.length - y.length;
}
