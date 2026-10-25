// SPDX-License-Identifier: AGPL-3.0-or-later

/** One property of a note's front matter: a text, or a list (`tags: [a, b]` or one `- item` per line). */
export type Property = { key: string; value: string | string[] };

const unquote = (v: string) => {
  const t = v.trim();
  return (t.startsWith('"') && t.endsWith('"')) || (t.startsWith("'") && t.endsWith("'")) ? t.slice(1, -1) : t;
};

/**
 * The flat key/value properties of a YAML front matter, as Obsidian shows them; null for anything flatter
 * reading can't take apart faithfully (nested maps, block text, comments, anchors), which is then shown as written.
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
    if (/^[|>&*!#]/.test(raw)) return null;
    if (raw.startsWith("[")) {
      if (!raw.endsWith("]")) return null;
      const inner = raw.slice(1, -1).trim();
      props.push({ key, value: inner ? inner.split(",").map(unquote) : [] });
    } else if (raw === "") {
      // A list below the key, one `- item` per line; anything else indented under it is a nested map.
      const items: string[] = [];
      while (i + 1 < lines.length && /^\s+-(\s|$)|^-\s/.test(lines[i + 1])) items.push(unquote(lines[++i].replace(/^\s*-\s?/, "")));
      if (i + 1 < lines.length && /^\s+\S/.test(lines[i + 1])) return null;
      props.push({ key, value: items.length ? items : "" });
    } else {
      props.push({ key, value: unquote(raw) });
    }
  }
  return props;
}
