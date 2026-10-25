// SPDX-License-Identifier: AGPL-3.0-or-later

// The parts of mdast this plugin touches.
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  position?: { start: { line: number } };
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}

export type CalloutKind = "note" | "tip" | "info" | "warning" | "danger" | "quote";

/** Obsidian's callout types and their aliases, by the look they get here; any other type looks like a note. */
const kinds: Record<string, CalloutKind> = {
  note: "note",
  abstract: "note",
  summary: "note",
  tldr: "note",
  todo: "note",
  example: "note",
  tip: "tip",
  hint: "tip",
  important: "tip",
  success: "tip",
  check: "tip",
  done: "tip",
  info: "info",
  question: "info",
  help: "info",
  faq: "info",
  warning: "warning",
  caution: "warning",
  attention: "warning",
  danger: "danger",
  error: "danger",
  failure: "danger",
  fail: "danger",
  missing: "danger",
  bug: "danger",
  quote: "quote",
  cite: "quote",
};

export const calloutKind = (type: string): CalloutKind => kinds[type.toLowerCase()] ?? "note";

// `[!type]`, then `+` (folded open) or `-` (folded shut), then the title up to the line's end.
const head = /^\[!([\w-]+)\]([+-]?)[ \t]*/;

/**
 * A remark plugin for Obsidian's callouts: a quote whose first line is `[!type] Title` (optionally `[!type]+` or
 * `[!type]-`, foldable) is marked as a callout, its first line becomes the title (the type's name without one),
 * and the rest stays its body. Inline Markdown in the title stays Markdown. Anything else is a plain quote.
 */
export function remarkCallouts() {
  return (tree: MdNode) => {
    const visit = (node: MdNode) => {
      node.children?.forEach(visit);
      if (node.type !== "blockquote") return;
      const first = node.children?.[0];
      const lead = first?.type === "paragraph" ? first.children?.[0] : undefined;
      const match = lead?.type === "text" ? head.exec(lead.value ?? "") : null;
      if (!first || !lead || !match) return;

      const [, type, fold] = match;
      lead.value = lead.value!.slice(match[0].length);
      // The title is the paragraph up to its first line break; what follows on later lines is body.
      const inline = first.children!;
      const title: MdNode[] = [];
      let rest: MdNode[] = [];
      for (let i = 0; i < inline.length; i++) {
        const child = inline[i];
        const at = child.type === "text" ? child.value!.indexOf("\n") : child.type === "break" ? 0 : -1;
        if (at < 0) {
          title.push(child);
          continue;
        }
        if (child.type === "text") {
          // Wiki links find their line by the text's position (wikiLinks.ts): the parts keep theirs.
          const line = child.position?.start.line ?? 0;
          if (at > 0) title.push({ type: "text", value: child.value!.slice(0, at), position: { start: { line } } });
          const after = child.value!.slice(at + 1);
          rest = [...(after ? [{ type: "text", value: after, position: { start: { line: line + 1 } } }] : []), ...inline.slice(i + 1)];
        } else {
          rest = inline.slice(i + 1);
        }
        break;
      }
      const named = title.some((t) => t.type !== "text" || t.value!.trim() !== "");
      const foldable = fold !== "";

      node.data = {
        ...node.data,
        hName: foldable ? "details" : "aside",
        hProperties: { ...node.data?.hProperties, dataCallout: calloutKind(type), ...(fold === "+" ? { open: true } : {}) },
      };
      const titleNode: MdNode = {
        type: "calloutTitle",
        data: { hName: foldable ? "summary" : "p", hProperties: { dataCalloutTitle: calloutKind(type) } },
        children: named ? title : [{ type: "text", value: type[0].toUpperCase() + type.slice(1).toLowerCase() }],
      };
      node.children = [titleNode, ...(rest.length ? [{ ...first, children: rest }] : []), ...node.children!.slice(1)];
    };
    visit(tree);
  };
}
