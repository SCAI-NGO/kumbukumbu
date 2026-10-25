// SPDX-License-Identifier: AGPL-3.0-or-later

// The parts of mdast this plugin touches.
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  position?: unknown;
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}

/**
 * A remark plugin for find in the note: every match of `query` in the note's text (ignoring case) becomes a
 * `<mark data-find="n">`, numbered in reading order, the `current` one marked as such. Runs after the wiki links,
 * so a link's label is found as it reads. Code (inline and fenced) is matched too, its text split the same way.
 */
export function remarkFind({ query, current }: { query: string; current: number }) {
  return (tree: MdNode) => {
    const q = query.toLowerCase();
    if (!q) return;
    let n = 0;
    const mark = (text: string): MdNode => {
      const i = n++;
      return {
        type: "findMatch",
        data: { hName: "mark", hProperties: { dataFind: i, ...(i === current ? { dataCurrent: true } : {}) } },
        children: [{ type: "text", value: text }],
      };
    };
    const split = (value: string): MdNode[] => {
      const out: MdNode[] = [];
      const lower = value.toLowerCase();
      let last = 0;
      for (let at = lower.indexOf(q); at >= 0; at = lower.indexOf(q, at + q.length)) {
        if (at > last) out.push({ type: "text", value: value.slice(last, at) });
        out.push(mark(value.slice(at, at + q.length)));
        last = at + q.length;
      }
      if (last < value.length) out.push({ type: "text", value: value.slice(last) });
      return out;
    };
    const visit = (node: MdNode): MdNode[] => {
      if (node.type === "text" && node.value) return split(node.value);
      // Inline code keeps its look: the marks go inside it.
      if (node.type === "inlineCode" && node.value && node.value.toLowerCase().includes(q)) {
        return [{ type: "inlineCodeFound", data: { hName: "code" }, children: split(node.value) }];
      }
      if (node.type === "code" && node.value && node.value.toLowerCase().includes(q)) {
        // The block keeps its position: the agent highlights (remarkChanged) go by it.
        return [{ type: "codeFound", position: node.position, data: { hName: "pre" }, children: [{ type: "codeText", data: { hName: "code" }, children: split(node.value) }] }];
      }
      if (node.children) node.children = node.children.flatMap(visit);
      return [node];
    };
    visit(tree);
  };
}
