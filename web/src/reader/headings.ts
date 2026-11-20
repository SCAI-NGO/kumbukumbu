// SPDX-License-Identifier: AGPL-3.0-or-later

const blank = (c: string) => c === " " || c === "\t";

/**
 * An ATX heading line's level and text, as the pattern `^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$` reads it: as
 * the app's `atxHeading` (note/outline.ts), copied, as the reader page imports nothing of the app, except that a line
 * of hashes alone is no heading here.
 */
export function atxHeading(line: string): { level: number; text: string } | null {
  let i = 0;
  while (i < 3 && line[i] === " ") i++;
  let h = i;
  while (line[h] === "#") h++;
  const level = h - i;
  if (level < 1 || level > 6 || h === line.length || !blank(line[h])) return null;
  if (/[\n\r\u2028\u2029]/.test(line.slice(h))) return null;
  // The text starts after the blanks that follow the hashes, and ends before the trailing blanks and before a closing
  // hash run that has a blank before it and something before that.
  let t = h;
  while (t < line.length && blank(line[t])) t++;
  let e = line.length;
  while (e > t && blank(line[e - 1])) e--;
  let k = e;
  while (k > t && line[k - 1] === "#") k--;
  let q = k;
  while (q > t && blank(line[q - 1])) q--;
  return { level, text: line.slice(t, k < e && q < k ? q : e) };
}

/** `md` with each link `[text](address)` as its text, as `/\[([^\]]*)\]\([^)]*\)/g` replaces them. */
function linkTexts(md: string): string {
  let out = "";
  let from = 0;
  for (let open = md.indexOf("["); open >= 0; open = md.indexOf("[", from)) {
    // A `[` before this one's `]` ends at the same `]`: where this one fails, so do they.
    const close = md.indexOf("]", open + 1);
    if (close < 0) break;
    if (md[close + 1] !== "(") {
      out += md.slice(from, close + 1);
      from = close + 1;
      continue;
    }
    const end = md.indexOf(")", close + 2);
    if (end < 0) break;
    out += md.slice(from, open) + md.slice(open + 1, close);
    from = end + 1;
  }
  return out + md.slice(from);
}

/** A heading's text as it reads, near enough: links by their text, emphasis and code marks gone. */
export const headingText = (md: string) =>
  linkTexts(md)
    .replace(/[*_`~=]/g, "")
    .trim()
    .toLowerCase();
