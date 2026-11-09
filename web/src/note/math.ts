// SPDX-License-Identifier: AGPL-3.0-or-later
// Loaded only for a note with maths (MarkdownView's `import("./math")`): KaTeX, its CSS and its fonts stay out of
// the app's first load.
import "katex/dist/katex.min.css";
import "./math.css";
import type { Options } from "react-markdown";
import rehypeKatex from "rehype-katex";

export { default as remarkMath } from "remark-math";

interface MdNode {
  type: string;
  value?: string;
  lang?: string | null;
  meta?: string | null;
  children?: MdNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, unknown>; hChildren?: unknown[] };
}

const displayCode = (value: string) => ({
  type: "element",
  tagName: "code",
  properties: { className: ["language-math", "math-display"] },
  children: [{ type: "text", value }],
});

const visit = (node: MdNode, f: (node: MdNode) => void) => {
  f(node);
  node.children?.forEach((child) => visit(child, f));
};

/**
 * A paragraph that is nothing but `$$x$$` is a display, as in Obsidian: remark-math only takes `$$` on lines of
 * their own for one, and reads a one-line `$$x$$` as inline maths. `file.value` is the note's own text here.
 */
export function remarkDisplayParagraph() {
  return (tree: MdNode, file: { value: unknown }) => {
    visit(tree, (node) => {
      const only = node.type === "paragraph" && node.children?.length === 1 ? node.children[0] : undefined;
      const { start, end } = only?.position ?? {};
      if (only?.type !== "inlineMath" || start?.offset === undefined || end?.offset === undefined) return;
      if (!String(file.value).slice(start.offset, end.offset).startsWith("$$")) return;
      node.type = "math";
      node.value = only.value;
      node.meta = null;
      delete node.children;
      node.data = { hName: "pre", hChildren: [displayCode(only.value ?? "")] };
    });
  };
}

/**
 * Every display in a `div` of its own: rehype-katex replaces the `pre` it is given, and with it whatever
 * `remarkChanged` put on it, so the agent's highlight goes on the `div`. A fenced ```` ```math ```` block stays code,
 * as it was before maths: rehype-katex would typeset any `code.language-math`.
 */
export function remarkMathBlocks() {
  return (tree: MdNode) => {
    visit(tree, (node) => {
      if (node.type === "math") {
        node.data = { hName: "div", hProperties: { className: ["k-math"] }, hChildren: [{ type: "element", tagName: "pre", properties: {}, children: node.data?.hChildren ?? [] }] };
      } else if (node.type === "code" && node.lang === "math") {
        node.data = { ...node.data, hProperties: { ...node.data?.hProperties, className: ["language-math-source"] } };
      }
    });
  };
}

// `trust: false` (no \href, \url, \htmlClass and the like) and KaTeX's own size and expansion limits. `strict:
// "ignore"`: the default writes warnings that quote the formula to the console. rehype-katex takes no `throwOnError`:
// it tries with errors on and, failing that, renders with `throwOnError: false`, the formula's source marked as such.
export const rehypePlugins: NonNullable<Options["rehypePlugins"]> = [[rehypeKatex, { trust: false, strict: "ignore", errorColor: "var(--warning)" }]];
