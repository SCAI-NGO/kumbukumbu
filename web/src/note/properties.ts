// SPDX-License-Identifier: AGPL-3.0-or-later

/** One property of a note's front matter: a text, or a list (`tags: [a, b]` or one `- item` per line). */
export type Property = { key: string; value: string | string[] };

const unquote = (v: string) => {
  const t = v.trim();
  return (t.startsWith('"') && t.endsWith('"')) || (t.startsWith("'") && t.endsWith("'")) ? t.slice(1, -1) : t;
};

/** A trailing comment (` # …`, as YAML has it: after a space) is YAML syntax this reading doesn't take apart. */
const commented = (v: string) => /(^|\s)#/.test(v);

/** An inline list's items, or null where a comma sits inside quotes (`["a, b"]`): splitting on it would be wrong. */
function inlineItems(inner: string): string[] | null {
  const parts = inner.split(",").map((p) => p.trim());
  const open = (p: string) => (p.startsWith('"') || p.startsWith("'")) && (p.length < 2 || !p.endsWith(p[0]));
  return parts.some(open) ? null : parts.map(unquote);
}

/**
 * The flat key/value properties of a YAML front matter, as Obsidian shows them; null for anything flatter
 * reading can't take apart faithfully (nested maps, block text, comments, anchors, commas in quoted list items),
 * which is then shown as written.
 * No YAML library: the subset is small, and nothing here is ever executed or resolved.
 */
export function parseProperties(yaml: string): Property[] | null {
  const props: Property[] = [];
  const lines = yaml.split("\n");
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].replace(/\r$/, "");
    if (line.trim() === "") continue;
    const m = /^([^\s:#][^:]*):(?:[ \t]+(.*))?$/.exec(line);
    if (!m) return null;
    const key = m[1].trim();
    const raw = (m[2] ?? "").trim();
    if (/^[|>&*!#]/.test(raw) || commented(raw)) return null;
    if (raw.startsWith("[")) {
      if (!raw.endsWith("]")) return null;
      const inner = raw.slice(1, -1).trim();
      const items = inner ? inlineItems(inner) : [];
      if (!items) return null;
      props.push({ key, value: items });
    } else if (raw === "") {
      // A list below the key, one `- item` per line; anything else indented under it is a nested map.
      const items: string[] = [];
      while (i + 1 < lines.length && /^\s+-(\s|$)|^-\s/.test(lines[i + 1])) {
        const item = lines[++i].replace(/^\s*-\s?/, "");
        if (commented(item)) return null;
        items.push(unquote(item));
      }
      if (i + 1 < lines.length && /^\s+\S/.test(lines[i + 1])) return null;
      props.push({ key, value: items.length ? items : "" });
    } else {
      props.push({ key, value: unquote(raw) });
    }
  }
  return props;
}

/**
 * A leading YAML block (`---` … `---` or `...`), and the body with those lines left empty: the body keeps the
 * source's line numbers, which ticking a task and the links' lines go by.
 */
export function splitFrontmatter(source: string): { frontmatter: string | null; body: string } {
  const lines = source.split("\n");
  if (lines[0]?.trimEnd() !== "---") return { frontmatter: null, body: source };
  const end = lines.findIndex((l, i) => i > 0 && (l.trimEnd() === "---" || l.trimEnd() === "..."));
  if (end < 0) return { frontmatter: null, body: source };
  return {
    frontmatter: lines.slice(1, end).join("\n"),
    body: [...lines.slice(0, end + 1).map(() => ""), ...lines.slice(end + 1)].join("\n"),
  };
}
