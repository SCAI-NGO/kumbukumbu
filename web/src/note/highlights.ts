// SPDX-License-Identifier: AGPL-3.0-or-later

// The parts of mdast these plugins touch.
interface MdNode {
  type: string;
  value?: string;
  url?: string;
  children?: MdNode[];
  position?: { start: { line: number; offset?: number }; end?: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}

// Never text a mark or a comment stands in: code, and what earlier plugins made of a link or a tag (their text is theirs).
const opaqueTypes = new Set(["code", "inlineCode", "html", "wikiLink", "tag"]);

/** Also a bare URL or an autolink, whose text is its address (`https://x.com/a%%20b`). */
function opaque(node: MdNode) {
  if (opaqueTypes.has(node.type)) return true;
  const text = node.type === "link" && node.children?.length === 1 && node.children[0].type === "text" ? node.children[0].value : undefined;
  return text !== undefined && [text, `mailto:${text}`, `http://${text}`].includes(node.url ?? "");
}

/** Part of a text node; it keeps its line, as wiki links find theirs by it (callouts.ts does the same). */
function part(node: MdNode, from: number, to = node.value!.length): MdNode {
  if (from === 0 && to === node.value!.length) return node;
  const line = node.position ? node.position.start.line + (node.value!.slice(0, from).match(/\n/g)?.length ?? 0) : undefined;
  return { type: "text", value: node.value!.slice(from, to), ...(line !== undefined ? { position: { start: { line } } } : {}) };
}

/**
 * For each text child of `node`, the places of the `=` and `%` the source escapes (`\==`, `\%%`). Markdown took the
 * backslashes out of the text, so each text is walked along the node's source; a text an earlier plugin split off
 * has no position of its own and is found after the one before it.
 */
function escapes(node: MdNode, source: string): Map<MdNode, Set<number>> {
  const found = new Map<MdNode, Set<number>>();
  const base = node.position?.start.offset;
  const stop = node.position?.end?.offset;
  if (base === undefined || stop === undefined) return found;
  const raw = source.slice(base, stop);
  if (!/\\[=%]/.test(raw)) return found;
  let j = 0;
  for (const child of node.children!) {
    const start = child.position?.start.offset;
    if (start !== undefined) j = start - base;
    if (child.type !== "text") {
      const end = child.position?.end?.offset;
      if (end !== undefined) j = end - base;
      continue;
    }
    const escaped = new Set<number>();
    for (let i = 0; i < child.value!.length; i++) {
      const c = child.value![i];
      const k = raw.indexOf(c, j);
      if (k < 0) break;
      let slashes = 0;
      while (k - slashes > 0 && raw[k - slashes - 1] === "\\") slashes++;
      if (slashes % 2 === 1 && (c === "=" || c === "%")) escaped.add(i);
      j = k + 1;
    }
    found.set(child, escaped);
  }
  return found;
}

/**
 * A remark plugin for `==highlights==`: a `highlight` node (a `<mark data-highlight>`) around what lies between
 * two `==` among the children of one parent, so it can hold emphasis, links and tags. Flanking as for emphasis:
 * no whitespace just inside either `==` (`a == b and c == d` stays text), never across a line, never in code, and
 * a longer run of `=` is none (`====x====` stays text).
 */
export function remarkHighlights() {
  return (tree: MdNode, file: { value: unknown }) => {
    const source = String(file.value);
    if (!source.includes("==")) return;
    const visit = (node: MdNode) => {
      if (!node.children || opaque(node)) return;
      node.children.forEach(visit);
      if (!node.children.some((c) => c.type === "text" && c.value?.includes("=="))) return;
      // The children as one string, a placeholder for each node that isn't text, a line break as one.
      const at: { child: number; offset: number }[] = [];
      let flat = "";
      node.children.forEach((child, i) => {
        const value = child.type === "text" ? (child.value ?? "") : child.type === "break" ? "\n" : "￼";
        for (let o = 0; o < value.length; o++) at.push({ child: i, offset: o });
        flat += value;
      });
      const escaped = escapes(node, source);
      const delimiter = (p: number) => {
        if (!flat.startsWith("==", p) || flat[p - 1] === "=" || flat[p + 2] === "=") return false;
        const { child, offset } = at[p];
        const text = node.children![child];
        return text.type === "text" && at[p + 1].child === child && !escaped.get(text)?.has(offset) && !escaped.get(text)?.has(offset + 1);
      };
      // One pass: the closers and line ends in order, each passed once as the openers move on.
      const closers: number[] = [];
      const lineEnds: number[] = [];
      for (let p = 0; p < flat.length; p++) {
        if (flat[p] === "\n") lineEnds.push(p);
        else if (p > 0 && /\S/.test(flat[p - 1]) && delimiter(p)) closers.push(p);
      }
      const ranges: [number, number][] = [];
      let c = 0;
      let e = 0;
      for (let open = 0; open < flat.length; open++) {
        if (!/\S/.test(flat[open + 2] ?? " ") || !delimiter(open)) continue;
        while (c < closers.length && closers[c] < open + 3) c++;
        while (e < lineEnds.length && lineEnds[e] < open + 2) e++;
        if (c < closers.length && (e === lineEnds.length || closers[c] < lineEnds[e])) {
          ranges.push([open, closers[c]]);
          open = closers[c] + 1;
        }
      }
      if (!ranges.length) return;

      // The children covering [from, to) of the string, text nodes cut where it ends inside them.
      const slice = (from: number, to: number): MdNode[] => {
        const out: MdNode[] = [];
        for (let p = from; p < to; ) {
          const { child, offset } = at[p];
          const c = node.children![child];
          const length = c.type === "text" ? c.value!.length : 1;
          const end = Math.min(to - p + offset, length);
          out.push(c.type === "text" ? part(c, offset, end) : c);
          p += end - offset;
        }
        return out;
      };
      const children: MdNode[] = [];
      let last = 0;
      for (const [open, close] of ranges) {
        children.push(...slice(last, open), { type: "highlight", data: { hName: "mark", hProperties: { dataHighlight: true } }, children: slice(open + 2, close) });
        last = close + 2;
      }
      node.children = [...children, ...slice(last, flat.length)];
    };
    visit(tree);
  };
}
