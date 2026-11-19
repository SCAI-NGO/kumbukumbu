// SPDX-License-Identifier: AGPL-3.0-or-later
// Write follows the click (workspace spec §3): a point in the rendered note, mapped back to where it is in the source.

/** How much of the rendered text on either side of the point is looked for in the source. */
const AROUND = 16;

/** Where line `line` (1-based) of `text` starts; past its end, the text's end. */
export function lineStart(text: string, line: number) {
  let at = 0;
  for (let n = 1; n < line; n++) {
    const next = text.indexOf("\n", at);
    if (next < 0) return text.length;
    at = next + 1;
  }
  return at;
}

/** Markup the rendered text leaves out: skipped in the source where it doesn't match. */
const MARKUP = new Set(["*", "_", "~", "`", "=", "[", "]", "|", ">", "#", "\\"]);

/** How many characters of `rendered` the source matches from `at` on (`step` 1) or before `at` (`step` -1), markup skipped. */
function matched(block: string, at: number, rendered: string, step: 1 | -1) {
  let n = 0;
  for (let i = step === 1 ? at : at - 1; i >= 0 && i < block.length && n < rendered.length; i += step) {
    const want = rendered[step === 1 ? n : rendered.length - 1 - n];
    if (block[i] === want) n++;
    else if (!MARKUP.has(block[i])) break;
  }
  return n;
}

/**
 * The source offset of a point in a block of lines `start` to `end` (1-based) of `source`, with the rendered text
 * `before` and `after` the point and its place in the block's rendered text (`ratio`, 0 to 1). The caret goes where
 * the most of that text is found around it in the block's lines, the markup the rendering leaves out (`**`, `[[`, `>`)
 * skipped. Found as well in more than one place, the one nearest to the proportional place is taken; not found at
 * all, that place itself.
 */
export function sourceOffset(source: string, { start, end }: { start: number; end: number }, before: string, after: string, ratio: number): number {
  const from = lineStart(source, start);
  const next = source.indexOf("\n", lineStart(source, end));
  const block = source.slice(from, next < 0 ? source.length : next);
  const guess = Math.round(ratio * block.length);
  let best = guess;
  let score = 0;
  for (let at = 0; at <= block.length; at++) {
    const found = matched(block, at, before, -1) + matched(block, at, after, 1);
    if (found > score || (found === score && found > 0 && Math.abs(at - guess) < Math.abs(best - guess))) [best, score] = [at, found];
  }
  return from + best;
}

/** What is drawn, not written, in the note: a click in it leads to the start of its block's line. */
const DRAWN = ".katex, [data-diagram], .k-embed";

/** The source offset of the point at `offset` in text `node`, in `block` (a rendered block carrying `data-line`). */
export function offsetAt(source: string, block: HTMLElement, node: Node, offset: number): number {
  const start = Number(block.dataset.line);
  const element = node instanceof Element ? node : node.parentElement;
  if (element?.closest(DRAWN)) return lineStart(source, start);
  const range = document.createRange();
  range.selectNodeContents(block);
  const text = range.toString();
  range.setEnd(node, offset);
  const at = range.toString().length;
  const lines = { start, end: Number(block.dataset.lineEnd ?? start) };
  return sourceOffset(source, lines, text.slice(Math.max(0, at - AROUND), at), text.slice(at, at + AROUND), text.length ? at / text.length : 0);
}

/** The text node and offset at a point on screen: `caretPositionFromPoint`, or `caretRangeFromPoint` in Safari. */
function caretAt(x: number, y: number): { node: Node; offset: number } | null {
  const position = document.caretPositionFromPoint?.(x, y);
  if (position) return { node: position.offsetNode, offset: position.offset };
  const range = document.caretRangeFromPoint?.(x, y);
  return range ? { node: range.startContainer, offset: range.startOffset } : null;
}

/** The source offset of the point (`x`, `y`) on screen, in a block of the note in `root`; null off the note's blocks. */
export function clickToSource(source: string, root: HTMLElement, x: number, y: number): number | null {
  const at = caretAt(x, y);
  if (!at || !root.contains(at.node)) return null;
  const element = at.node instanceof Element ? at.node : at.node.parentElement;
  const block = element?.closest<HTMLElement>("[data-line]");
  if (!block || !root.contains(block)) return null;
  return offsetAt(source, block, at.node, at.offset);
}
