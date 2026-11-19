// SPDX-License-Identifier: AGPL-3.0-or-later

/** One property of a note's front matter: a text, or a list (`tags: [a, b]` or one `- item` per line). */
export type Property = { key: string; value: string | string[] };

/** A value without its quotes; inside double quotes, YAML's `\\` and `\"` escapes undone, inside single ones its `''`. */
const unquote = (v: string) => {
  const t = v.trim();
  if (t.startsWith('"') && t.endsWith('"')) return t.slice(1, -1).replace(/\\(["\\])/g, "$1");
  return t.startsWith("'") && t.endsWith("'") ? t.slice(1, -1).replace(/''/g, "'") : t;
};

/** One whole quoted scalar (`"…"` with its escapes, `'…'` with `''`), where a `#` is text. */
const quoted = (v: string) => /^"(?:[^"\\]|\\.)*"$/.test(v) || /^'(?:[^']|'')*'$/.test(v);

/** A trailing comment (` # …`, as YAML has it: after a space) is YAML syntax this reading doesn't take apart. */
const commented = (v: string) => !quoted(v.trim()) && /(^|\s)#/.test(v);

/** A part split off at a comma that opens a quote it doesn't close: the comma sat inside quotes (`["a, b"]`, `["a\\", b"]`). */
const unclosed = (p: string) => (p.startsWith('"') || p.startsWith("'")) && !quoted(p);

/**
 * A part that is, or is split off, a nested flow list or map (`[[a, b], x]`, `[{a: b, c: d}]`); not a template's
 * placeholder (`[daily, {{date}}]`), which Obsidian fills before the YAML is read.
 */
const nested = (p: string) => /^[[{]|[\]}]$/.test(p) && !/^\{\{[^{}]*\}\}$/.test(p);

/**
 * An inline list's items, one trailing comma dropped as YAML drops it (`[a,]`); null where splitting at the commas
 * would be wrong: a comma inside quotes or inside a nested list or map, or an item left empty (`[a,,b]`).
 */
function inlineItems(inner: string): string[] | null {
  const parts = inner.split(",").map((p) => p.trim());
  if (parts.length > 1 && parts[parts.length - 1] === "") parts.pop();
  return parts.some((p) => p === "" || unclosed(p) || nested(p)) ? null : parts.map(unquote);
}

/**
 * A flat flow map (`{ Running: 2026-11-14, "In progress": 2026-11-12 }`) as its keys and texts; null for anything
 * else: no braces, nested ones, a comma inside quotes, a pair without its `:`.
 */
export function flowMap(value: string): Record<string, string> | null {
  const t = value.trim();
  if (!t.startsWith("{") || !t.endsWith("}")) return null;
  const inner = t.slice(1, -1).trim();
  if (/[{}[\]]/.test(inner)) return null;
  const map: Record<string, string> = {};
  if (inner === "") return map;
  for (const part of inner.split(",").map((p) => p.trim())) {
    // A comma inside a quoted key leaves a part without its `:`; one inside a quoted value, an unclosed value.
    const pair = /^("[^"]*"|'[^']*'|[^:"']+):(?:\s+(.*))?$/.exec(part);
    const text = pair?.[2]?.trim() ?? "";
    if (!pair || unclosed(text)) return null;
    map[unquote(pair[1])] = unquote(text);
  }
  return map;
}

/**
 * The flat key/value properties of a YAML front matter, as Obsidian shows them; null for anything flatter
 * reading can't take apart faithfully (nested maps and lists, block text, comments, anchors, commas in quoted list items),
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
