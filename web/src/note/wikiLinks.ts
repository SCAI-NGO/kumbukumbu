// SPDX-License-Identifier: AGPL-3.0-or-later
import type { components } from "../api/schema";

export type LinkView = components["schemas"]["LinkView"];

// The parts of mdast this plugin touches; the full types aren't a dependency of the app.
interface MdNode {
  type: string;
  value?: string;
  url?: string;
  children?: MdNode[];
  position?: { start: { line: number; offset?: number }; end?: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}

// What the server never reports, so never a note: a URL with a scheme (`https:`, `mailto:`) or a network path.
const external = /^([A-Za-z][A-Za-z0-9+.-]*:|\/\/)/;

// As the server finds them (Kumbu.Core WikiLink): `[[target#Heading|alias]]`, an embed with `!`.
const wikiLink = /!?\[\[([^[\]\n]+?)\]\]/g;

/** Markdown's backslash escapes (CommonMark: before any ASCII punctuation) taken out. */
const unescape = (raw: string) => raw.replace(/\\([!-/:-@[-`{-~])/g, "$1");

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
 * and the Markdown links that aren't external (`[the plan](/Work/plan.md)`), and marks each with the index of its
 * entry in `links`, the API's answer for this note. Resolution is the server's
 * alone (the same code path as the `links` command, never across vaults): a link is matched to the answer by
 * its line and its place among the same links on that line, never by its name. A link the answer doesn't have
 * stays plain text.
 */
export function remarkWikiLinks({ links }: { links: LinkView[] }) {
  return (tree: MdNode, file: { value: unknown }) => {
    const answers = new Map<string, number[]>();
    // Markdown takes backslash escapes out of the text (`[[plan\|alias]]` in a table, `[[a\_b]]`), the server's
    // raw keeps them: such a link is also known by its raw without them, where that is no other link's key.
    const unescaped = new Map<string, number[]>();
    links.forEach((l, i) => {
      const key = `${l.line}\u0000${l.raw}`;
      answers.set(key, [...(answers.get(key) ?? []), i]);
      const plain = `${l.line}\u0000${unescape(l.raw)}`;
      if (plain !== key) unescaped.set(plain, [...(unescaped.get(plain) ?? []), i]);
    });
    const seen = new Map<string, number>();

    const indexOf = (line: number, raw: string) => {
      const key = `${line}\u0000${raw}`;
      const nth = seen.get(key) ?? 0;
      seen.set(key, nth + 1);
      return (answers.get(key) ?? unescaped.get(key))?.[nth] ?? -1;
    };

    const replace = (node: MdNode): MdNode[] => {
      // Raw HTML is shown as written, never rendered (there is no rehype-raw either).
      if (node.type === "html") return [{ ...node, type: "text" }];
      // The server's raw is the link as written, which the parsed node no longer has (its url is decoded): the
      // source between the node's offsets is.
      const { start, end } = node.position ?? {};
      if (node.type === "link" && !external.test(node.url ?? "") && start?.offset !== undefined && end?.offset !== undefined) {
        const index = indexOf(start.line, String(file.value).slice(start.offset, end.offset));
        return [{ type: "wikiLink", data: { hName: "span", hProperties: { dataWikiLink: index } }, children: (node.children ?? []).flatMap(replace) }];
      }
      if (node.type !== "text" || !node.value || !node.position) {
        if (node.children) node.children = node.children.flatMap(replace);
        return [node];
      }

      const value = node.value;
      const out: MdNode[] = [];
      let last = 0;
      for (const m of value.matchAll(wikiLink)) {
        const line = node.position.start.line + (value.slice(0, m.index).match(/\n/g)?.length ?? 0);
        const index = indexOf(line, m[0]);

        if (m.index > last) out.push({ type: "text", value: value.slice(last, m.index) });
        out.push({
          type: "wikiLink",
          data: { hName: "span", hProperties: { dataWikiLink: index, ...(m[0].startsWith("!") ? { dataEmbed: "inline" } : {}) } },
          children: [{ type: "text", value: linkLabel(m[0]) }],
        });
        last = m.index + m[0].length;
      }
      if (out.length === 0) return [node];
      if (last < value.length) out.push({ type: "text", value: value.slice(last) });
      return out;
    };

    replace(tree);
    blockEmbeds(tree);
  };
}

const isEmbed = (n: MdNode) => n.type === "wikiLink" && n.data?.hProperties?.dataEmbed !== undefined;

/**
 * A paragraph of nothing but embeds (`![[plan]]` on its own line, as Obsidian writes them) becomes a block, so
 * the embedded note can show inside it; an embed within a sentence stays a link there.
 */
function blockEmbeds(node: MdNode) {
  node.children?.forEach(blockEmbeds);
  if (node.type !== "paragraph" || !node.children?.some(isEmbed)) return;
  if (!node.children.every((c) => isEmbed(c) || (c.type === "text" && !c.value?.trim()) || c.type === "break")) return;
  node.data = { ...node.data, hName: "div", hProperties: { ...node.data?.hProperties, dataEmbeds: true } };
  for (const c of node.children) if (isEmbed(c)) c.data!.hProperties!.dataEmbed = "block";
}

/**
 * Where a missing link's note is created, from the note it is in: `/…` from the vault's root, any other path from
 * the note's folder (`./` and `../` as they say, never above the vault), `.md` added where it lacks one.
 */
export function createdPath(notePath: string, target: string): string {
  const [, vault, ...rest] = notePath.split("/");
  const segments = target.startsWith("/") ? [] : rest.slice(0, -1);
  for (const segment of target.split("/")) {
    if (segment === "..") segments.pop();
    else if (segment && segment !== ".") segments.push(segment);
  }
  const path = `/${[vault, ...segments].join("/")}`;
  return path.endsWith(".md") ? path : `${path}.md`;
}

/** The heading of an embed's subpath (`![[plan#Goals]]` → `Goals`); null without one, or for a block (`#^id`). */
export function embedHeading(raw: string): string | null {
  const inner = raw.replace(/^!?\[\[/, "").replace(/\]\]$/, "");
  const bar = inner.search(/\\?\|/);
  const target = bar >= 0 ? inner.slice(0, bar) : inner;
  const hash = target.indexOf("#");
  if (hash < 0) return null;
  const heading = target.slice(hash + 1).trim();
  return heading && !heading.startsWith("^") ? heading : null;
}
