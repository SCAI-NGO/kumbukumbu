// SPDX-License-Identifier: AGPL-3.0-or-later

// The parts of mdast these plugins touch.
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  position?: { start: { line: number } };
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}

// Never text a mark or a comment stands in: code, and what earlier plugins made of a link or a tag (their text is theirs).
const opaque = new Set(["code", "inlineCode", "html", "wikiLink", "tag"]);

/** Part of a text node; it keeps its line, as wiki links find theirs by it (callouts.ts does the same). */
function part(node: MdNode, from: number, to = node.value!.length): MdNode {
  if (from === 0 && to === node.value!.length) return node;
  const line = node.position ? node.position.start.line + (node.value!.slice(0, from).match(/\n/g)?.length ?? 0) : undefined;
  return { type: "text", value: node.value!.slice(from, to), ...(line !== undefined ? { position: { start: { line } } } : {}) };
}

/**
 * A remark plugin for `==highlights==`: a `highlight` node (a `<mark data-highlight>`) around what lies between
 * two `==` among the children of one parent, so it can hold emphasis, links and tags. Flanking as for emphasis:
 * no whitespace just inside either `==` (`a == b and c == d` stays text), never across a line, never in code.
 */
export function remarkHighlights() {
  return (tree: MdNode) => {
    const visit = (node: MdNode) => {
      if (!node.children || opaque.has(node.type)) return;
      node.children.forEach(visit);
      // The children as one string, a placeholder for each node that isn't text, a line break as one.
      const at: { child: number; offset: number }[] = [];
      let flat = "";
      node.children.forEach((child, i) => {
        const value = child.type === "text" ? (child.value ?? "") : child.type === "break" ? "\n" : "￼";
        for (let o = 0; o < value.length; o++) at.push({ child: i, offset: o });
        flat += value;
      });
      if (!flat.includes("==")) return;
      const delimiter = (p: number) => flat.startsWith("==", p) && node.children![at[p].child].type === "text" && at[p].child === at[p + 1].child;
      const ranges: [number, number][] = [];
      for (let open = 0; open < flat.length; open++) {
        if (!delimiter(open) || flat[open - 1] === "=" || !/\S/.test(flat[open + 2] ?? " ")) continue;
        for (let close = open + 3; close < flat.length && flat[close - 1] !== "\n"; close++) {
          if (delimiter(close) && /\S/.test(flat[close - 1]) && flat[close + 2] !== "=") {
            ranges.push([open, close]);
            open = close + 1;
            break;
          }
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

/**
 * A remark plugin that hides `%%comments%%`: what lies between two `%%` goes, inline or over several blocks
 * (a block a comment empties goes with it), and a `%%` without its pair hides the rest of the note, as
 * `findTags` reads it. Code is never a comment's start or end; inside a comment it goes too.
 */
export function remarkComments() {
  return (tree: MdNode) => {
    let open = false;
    // The node's children with what comments hide taken out; false when nothing of it is left.
    const visit = (node: MdNode): boolean => {
      const out: MdNode[] = [];
      for (const child of node.children!) {
        if (child.type === "text" && child.value?.includes("%%")) {
          const value = child.value;
          let from = 0;
          for (let p = value.indexOf("%%"); p >= 0; p = value.indexOf("%%", p + 2)) {
            if (!open && p > from) out.push(part(child, from, p));
            open = !open;
            from = p + 2;
          }
          if (!open && from < value.length) out.push(part(child, from));
        } else if (child.children && !opaque.has(child.type)) {
          const empty = child.children.length === 0;
          const wasOpen = open;
          // A table keeps its shape: a cell a comment empties stays, and so does a row the comment starts in.
          if (visit(child) || empty || child.type === "tableCell" || (child.type === "tableRow" && !wasOpen)) out.push(child);
        } else if (!open) {
          out.push(child);
        }
      }
      node.children = out;
      return out.some((c) => c.type !== "tableCell" || c.children!.length > 0);
    };
    visit(tree);
  };
}
