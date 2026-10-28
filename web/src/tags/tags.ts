// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * Tags as the server reads them (Kumbu.Core TagReader, Obsidian's rules): `#` then tag characters, at the start of
 * the text or after whitespace, not only digits; nested with `/`; matched ignoring case.
 */

/** Letters, digits, `_`, `-`, `/` and any other character (emoji too) except whitespace, ASCII punctuation and the
 * general and supplemental punctuation blocks. */
export function isTagChar(c: string): boolean {
  if (c === "_" || c === "-" || c === "/") return true;
  if (/\s/.test(c)) return false;
  const code = c.charCodeAt(0);
  if (code < 0x20 || code === 0x7f) return false;
  if (code < 0x80) return /[A-Za-z0-9]/.test(c);
  return !(code >= 0x2000 && code <= 0x206f) && !(code >= 0x2e00 && code <= 0x2e7f);
}

/** A whole tag without `#`: tag characters only, at least one of them not a digit, no trailing `/`. */
export const isTag = (text: string) => text.length > 0 && !text.endsWith("/") && [...text].every(isTagChar) && /[^0-9]/.test(text);

/** A tag as asked for, with or without `#` and a trailing `/`; null if it can't be one. */
export function normaliseTag(asked: string): string | null {
  const tag = asked.trim().replace(/^#/, "").replace(/\/+$/, "").normalize("NFC");
  return isTag(tag) ? tag : null;
}

/**
 * A query that starts with `#tag`: the tag, the rest, and whether the tag is finished (whitespace after it), so
 * the search sheet can turn it into its tag filter. Null tag for any other query.
 */
export function splitTagQuery(query: string): { tag: string | null; rest: string; finished: boolean } {
  const m = /^\s*#(\S+)(\s+([\s\S]*))?$/.exec(query);
  const tag = m ? normaliseTag(m[1]) : null;
  return tag && m ? { tag, rest: (m[3] ?? "").trim(), finished: m[2] !== undefined } : { tag: null, rest: query, finished: false };
}

/** The tags of a frontmatter `tags` value: a list, or a string separated by commas or spaces; `#` dropped. */
export function frontmatterTags(value: string | string[]): string[] {
  return (Array.isArray(value) ? value : [value])
    .flatMap((v) => v.split(/[,\s]+/))
    .map((t) => t.replace(/^#/, "").replace(/\/+$/, ""))
    .filter(isTag);
}

/** The text's inline tags, each with where it starts and ends (the `#` included); wikilinks and `%% … %%` hold none. */
export function findTags(text: string, startsClean = true): { tag: string; start: number; end: number }[] {
  const skipped: [number, number][] = [];
  for (const m of text.matchAll(/!?\[\[[^[\]\n]+?\]\]|%%[\s\S]*?(%%|$)/g)) skipped.push([m.index, m.index + m[0].length]);
  const found: { tag: string; start: number; end: number }[] = [];
  for (let p = text.indexOf("#"); p >= 0; p = text.indexOf("#", p + 1)) {
    const before = p === 0 ? (startsClean ? " " : "x") : text[p - 1];
    if (!/\s/.test(before) || skipped.some(([s, e]) => p >= s && p < e)) continue;
    let stop = p + 1;
    while (stop < text.length && isTagChar(text[stop])) stop++;
    const tag = text.slice(p + 1, stop).replace(/\/+$/, "");
    if (isTag(tag)) {
      found.push({ tag: tag.normalize("NFC"), start: p, end: p + 1 + tag.length });
      p = stop - 1;
    }
  }
  return found;
}

// The parts of mdast this plugin touches; the full types aren't a dependency of the app.
interface MdNode {
  type: string;
  value?: string;
  children?: MdNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
  data?: { hName?: string; hProperties?: Record<string, unknown> };
}

// Never text a tag stands in: code, links (their text is the link's), raw HTML (shown as written).
const opaque = new Set(["code", "inlineCode", "link", "linkReference", "image", "imageReference", "html", "definition"]);

/**
 * A remark plugin that turns inline `#tags` in the text into `tag` nodes (a `span` with `data-tag`), which the view
 * shows as chips. Code is never text here, so tags in code stay as written. Runs before `remarkWikiLinks`, whose
 * `[[…]]` it leaves alone.
 */
export function remarkTags() {
  return (tree: MdNode, file: { value: unknown }) => {
    // Markdown takes the backslash out of `\#a`, which is no tag; the source still has it.
    const escaped = (node: MdNode) => {
      const { start, end } = node.position ?? {};
      if (start?.offset === undefined || end?.offset === undefined) return new Set<string>();
      const raw = String(file.value).slice(start.offset, end.offset);
      const cut = (t: string) => {
        let end = 0;
        while (end < t.length && isTagChar(t[end])) end++;
        return t.slice(0, end).replace(/\/+$/, "").normalize("NFC").toLowerCase();
      };
      return new Set([...raw.matchAll(/\\#(\S+)/g)].map((m) => cut(m[1])));
    };
    const walk = (node: MdNode) => {
      if (!node.children || opaque.has(node.type)) return;
      const out: MdNode[] = [];
      node.children.forEach((child, i) => {
        if (child.type !== "text" || !child.value) {
          walk(child);
          out.push(child);
          return;
        }
        // A text that follows emphasis or a link doesn't start after whitespace (`**a**#b`).
        const prev = node.children![i - 1];
        const startsClean = !prev || prev.type === "break" || (prev.type === "text" && /\s$/.test(prev.value ?? ""));
        const value = child.value;
        const tags = findTags(value, startsClean);
        const skip = tags.length && value.includes("#") ? escaped(child) : new Set<string>();
        let last = 0;
        for (const t of tags.filter((t) => !skip.has(t.tag.toLowerCase()))) {
          if (t.start > last) out.push({ type: "text", value: value.slice(last, t.start) });
          out.push({ type: "tag", data: { hName: "span", hProperties: { dataTag: t.tag } }, children: [{ type: "text", value: `#${t.tag}` }] });
          last = t.end;
        }
        if (last === 0) out.push(child);
        else if (last < value.length) out.push({ type: "text", value: value.slice(last) });
      });
      node.children = out;
    };
    walk(tree);
  };
}
