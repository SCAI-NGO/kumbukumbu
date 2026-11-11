// SPDX-License-Identifier: AGPL-3.0-or-later
import { commentRanges } from "./commentRanges";
import { splitFrontmatter } from "./properties";

// The parts of mdast this plugin touches.
interface MdNode {
  type: string;
  value?: string;
  url?: string;
  alt?: string | null;
  title?: string | null;
  children?: MdNode[];
}

/**
 * `note` with its `%%comments%%` masked: every character the server's fair copy takes out (commentRanges, its port)
 * replaced by `mask`, line ends kept, so offsets and lines hold and Markdown reads the rest as the server does. Mask
 * the whole note, then split off the frontmatter: the ranges count from the note's start. `mask` is a character the
 * note doesn't hold; null when there is no comment, or (never in practice) no such character, when nothing is hidden
 * rather than something the copy publishes.
 */
export function maskComments(note: string): { source: string; mask: string | null } {
  const ranges = commentRanges(note);
  if (!ranges.length) return { source: note, mask: null };
  let mask: string | null = null;
  for (let c = 0xe000; c <= 0xf8ff && mask === null; c++) if (!note.includes(String.fromCharCode(c))) mask = String.fromCharCode(c);
  if (mask === null) return { source: note, mask: null };
  const chars = note.split("");
  for (const [start, end] of ranges) {
    for (let i = start; i < end; i++) if (chars[i] !== "\n" && chars[i] !== "\r") chars[i] = mask;
  }
  return { source: chars.join(""), mask };
}

/** A note's body for MarkdownView: its comments masked, its frontmatter split off. */
export function maskedBody(content: string): { body: string; mask: string | null } {
  const { source, mask } = maskComments(content);
  return { body: splitFrontmatter(source).body, mask };
}

// Kept when a comment empties them, as in the fair copy: a link keeps its address, a table its shape.
const kept = new Set(["link", "linkReference", "image", "imageReference", "tableCell", "tableRow", "table", "footnoteReference"]);

/**
 * A remark plugin, the first after parsing a masked source: `mask` out of every text, address, alt and title, and
 * every node a comment emptied gone (a block a comment covers whole goes with it).
 */
export function remarkMaskedComments({ mask }: { mask: string | null }) {
  return (tree: MdNode) => {
    if (mask === null) return;
    const strip = (s: string) => s.split(mask).join("");
    // Whether the node held the mask; its children with what the comment emptied taken out.
    const visit = (node: MdNode): boolean => {
      let masked = false;
      for (const key of ["value", "url", "alt", "title"] as const) {
        const v = node[key];
        if (typeof v === "string" && v.includes(mask)) {
          node[key] = strip(v);
          masked = true;
        }
      }
      if (!node.children) return masked;
      node.children = node.children.filter((child) => {
        if (!visit(child)) return true;
        masked = true;
        const empty = child.children ? child.children.length === 0 : child.value === "";
        return !empty || kept.has(child.type);
      });
      return masked;
    };
    visit(tree);
  };
}
