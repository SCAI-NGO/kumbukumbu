// SPDX-License-Identifier: AGPL-3.0-or-later

/** One line of a diff; `before` and `after` are 1-based line numbers in the older and the newer text. */
export type DiffLine =
  | { kind: "same"; text: string; before: number; after: number }
  | { kind: "removed"; text: string; before: number }
  | { kind: "added"; text: string; after: number };

// Above this many cells the middle part counts as replaced as a whole: a note has at most 1 MiB, and a table
// of every line against every line could take far more memory than a phone has to spare.
const MAX_CELLS = 4_000_000;

const linesOf = (text: string) => text.replace(/\r\n/g, "\n").split("\n");

/**
 * A line diff by longest common subsequence, after the common start and end are taken off. Removed lines come
 * before the added ones that replace them.
 */
export function lineDiff(before: string, after: string): DiffLine[] {
  const a = linesOf(before);
  const b = linesOf(after);
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let end = 0;
  while (end < a.length - start && end < b.length - start && a[a.length - 1 - end] === b[b.length - 1 - end]) end++;

  const out: DiffLine[] = [];
  for (let i = 0; i < start; i++) out.push({ kind: "same", text: a[i], before: i + 1, after: i + 1 });

  const n = a.length - start - end;
  const m = b.length - start - end;
  if (n * m > MAX_CELLS) {
    for (let i = 0; i < n; i++) out.push({ kind: "removed", text: a[start + i], before: start + i + 1 });
    for (let j = 0; j < m; j++) out.push({ kind: "added", text: b[start + j], after: start + j + 1 });
  } else {
    // lcs[i * (m + 1) + j]: the longest common run of the middle parts from line i of a and line j of b on.
    const lcs = new Uint32Array((n + 1) * (m + 1));
    for (let i = n - 1; i >= 0; i--) {
      for (let j = m - 1; j >= 0; j--) {
        lcs[i * (m + 1) + j] =
          a[start + i] === b[start + j] ? lcs[(i + 1) * (m + 1) + j + 1] + 1 : Math.max(lcs[(i + 1) * (m + 1) + j], lcs[i * (m + 1) + j + 1]);
      }
    }
    let i = 0;
    let j = 0;
    while (i < n || j < m) {
      if (i < n && j < m && a[start + i] === b[start + j]) {
        out.push({ kind: "same", text: a[start + i], before: start + i + 1, after: start + j + 1 });
        i++;
        j++;
      } else if (j >= m || (i < n && lcs[(i + 1) * (m + 1) + j] >= lcs[i * (m + 1) + j + 1])) {
        out.push({ kind: "removed", text: a[start + i], before: start + i + 1 });
        i++;
      } else {
        out.push({ kind: "added", text: b[start + j], after: start + j + 1 });
        j++;
      }
    }
  }

  const offset = b.length - a.length;
  for (let i = a.length - end; i < a.length; i++) out.push({ kind: "same", text: a[i], before: i + 1, after: i + 1 + offset });
  return out;
}

/** The 1-based lines of `after` that `before` doesn't have: added, or changed (removed and added in their place). */
export function changedLines(before: string, after: string): Set<number> {
  return new Set(lineDiff(before, after).flatMap((l) => (l.kind === "added" ? [l.after] : [])));
}
