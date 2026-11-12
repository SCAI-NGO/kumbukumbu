// SPDX-License-Identifier: AGPL-3.0-or-later

/** Obsidian's block id at the end of a line, after a space or tab: `The pump needs a new controller. ^controller`. */
export const blockIdAtEnd = /[ \t]\^([A-Za-z0-9-]+)[ \t]*$/;

/** A line that is only a block id: `^list`. */
export const blockIdLine = /^[ \t]*\^([A-Za-z0-9-]+)[ \t]*$/;

// Right below a paragraph's text, an id on its own line is the paragraph's last line.
const blockIdBelow = /\n[ \t]*\^([A-Za-z0-9-]+)[ \t]*$/;

interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
}

const onlyId = (node: MdNode) => node.type === "paragraph" && node.children?.length === 1 && node.children[0].type === "text" && blockIdLine.test(node.children[0].value ?? "");

function hide(node: MdNode) {
  if (!node.children) return;
  // A paragraph of only an id names the block above it, and shows nothing.
  node.children = node.children.filter((child) => !onlyId(child));
  for (const child of node.children) hide(child);
  if (node.type !== "paragraph" && node.type !== "heading") return;
  const last = node.children.at(-1);
  if (last?.type !== "text" || last.value === undefined) return;
  last.value = last.value.replace(blockIdAtEnd, "");
  if (node.type === "paragraph") last.value = last.value.replace(blockIdBelow, "");
}

/**
 * A remark plugin: the block ids the read view doesn't show, at the end of a paragraph, heading, list item or quote,
 * on the line below a paragraph, or on a line of their own. Code keeps them, and so does a table's row (GFM reads a
 * line right below a table as one), which names nothing.
 */
export function remarkHideBlockIds() {
  return (tree: MdNode) => hide(tree);
}
