// SPDX-License-Identifier: AGPL-3.0-or-later
import type { components } from "../api/schema";

export type LinkView = components["schemas"]["LinkView"];

// The parts of mdast this plugin touches; the full types aren't a dependency of the app.
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  position?: { start: { line: number } };
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}

// As the server finds them (Kumbu.Core WikiLink): `[[target#Heading|alias]]`, an embed with `!`.
const wikiLink = /!?\[\[([^[\]\n]+?)\]\]/g;

/** What a link shows: its alias, else its target (and heading, `plan › Goals`). */
export function linkLabel(raw: string) {
  const inner = raw.replace(/^!?\[\[/, "").replace(/\]\]$/, "");
  const bar = inner.search(/\\?\|/);
  if (bar >= 0) return inner.slice(inner.indexOf("|", bar) + 1);
  const [target, heading] = inner.split(/#(.*)/s);
  return heading ? (target ? `${target} › ${heading}` : heading) : target;
}

/**
 * A remark plugin that finds the wiki links in the text (code is never text, so links in code stay as written)
 * and marks each with the index of its entry in `links`, the API's answer for this note. Resolution is the
 * server's alone (the same code path as the `links` command, never across vaults): a link is matched to the
 * answer by its line and its place among the same links on that line, never by its name. A link the answer
 * doesn't have stays plain text.
 */
export function remarkWikiLinks({ links }: { links: LinkView[] }) {
  return (tree: MdNode) => {
    const answers = new Map<string, number[]>();
    links.forEach((l, i) => answers.set(`${l.line}\u0000${l.raw}`, [...(answers.get(`${l.line}\u0000${l.raw}`) ?? []), i]));
    const seen = new Map<string, number>();

    const replace = (node: MdNode): MdNode[] => {
      // Raw HTML is shown as written, never rendered (there is no rehype-raw either).
      if (node.type === "html") return [{ ...node, type: "text" }];
      if (node.type !== "text" || !node.value || !node.position) {
        if (node.children) node.children = node.children.flatMap(replace);
        return [node];
      }

      const value = node.value;
      const out: MdNode[] = [];
      let last = 0;
      for (const m of value.matchAll(wikiLink)) {
        const line = node.position.start.line + (value.slice(0, m.index).match(/\n/g)?.length ?? 0);
        const key = `${line}\u0000${m[0]}`;
        const nth = seen.get(key) ?? 0;
        seen.set(key, nth + 1);
        const index = answers.get(key)?.[nth] ?? -1;

        if (m.index > last) out.push({ type: "text", value: value.slice(last, m.index) });
        out.push({
          type: "wikiLink",
          data: { hName: "span", hProperties: { dataWikiLink: index } },
          children: [{ type: "text", value: linkLabel(m[0]) }],
        });
        last = m.index + m[0].length;
      }
      if (out.length === 0) return [node];
      if (last < value.length) out.push({ type: "text", value: value.slice(last) });
      return out;
    };

    replace(tree);
  };
}
