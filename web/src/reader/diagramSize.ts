// SPDX-License-Identifier: AGPL-3.0-or-later
// Without Mermaid: the note view decides from the source alone whether a diagram draws by itself.

/** The source's lines as Mermaid reads them: a line ends at `\r\n`, `\r` or `\n`. */
export const lines = (source: string) => source.split(/\r\n?|\n/);

// Mermaid's own (11.17.2): front matter closes at its opener's indent, and a directive may run over lines (to the
// end of the source when it isn't closed).
const frontMatter = /^([^\S\n\r]*)-{3}\s*[\n\r](.*?)[\n\r]\1-{3}\s*[\n\r]+/s;
const directive = /%{2}{\s*(?:(\w+)\s*:|(\w+))\s*(?:(\w+)|((?:(?!}%{2}).|\r?\n)*))?\s*(?:}%{2})?/gi;

/**
 * The source as Mermaid reads its type: without front matter and directives. `configured` when either may change
 * Mermaid's configuration. Front matter with a long run of blank space after its opener is left as it is: the
 * diagram then has no type.
 */
function body(source: string) {
  const text = source.replace(/\r\n?/g, "\n");
  const opener = /^[^\S\n]*---(\s*)/.exec(text);
  const front = opener && opener[1].length <= 64 ? frontMatter.exec(text) : null;
  const rest = front ? text.slice(front[0].length) : text;
  const plain = rest.replace(directive, "");
  return { text: plain, configured: plain.length !== rest.length || /config/i.test(front?.[2] ?? "") };
}

/** The diagram's keyword, as Mermaid finds it: past front matter, directives and `%%` comments. */
export function keyword(source: string) {
  const first = lines(body(source).text)
    .map((l) => l.trim())
    .find((l) => l && !l.startsWith("%%"));
  return first && /^[A-Za-z][\w-]*/.exec(first)?.[0];
}

/** Up to `auto` a diagram draws by itself; up to `cap` when asked; above, it is too large to draw here. */
export const diagramLimits = {
  flowchart: { auto: 120, cap: 960 },
  class: { auto: 120, cap: 1900 },
  state: { auto: 160, cap: 2500 },
  er: { auto: 160, cap: 2500 },
  mindmap: { auto: 42, cap: 320 },
  sequence: { auto: 500, cap: 2500 },
  gantt: { auto: 700, cap: 1400 },
  // A timeline holds fewer characters than the other types.
  timeline: { auto: 320, cap: 2000, chars: { auto: 1000, cap: 4000 } },
  journey: { auto: 700, cap: 1400 },
  gitgraph: { auto: 200, cap: 3300 },
  block: { auto: 120, cap: 2000 },
  other: { auto: 42, cap: 320 },
} as const satisfies Record<string, { auto: number; cap: number; chars?: { auto: number; cap: number }; days?: { auto: number; cap: number } }>;

type Kind = keyof typeof diagramLimits;

/** The type as the limits name it: Mermaid's keyword without `-v2`, `-beta`, `-elk`, `Diagram`; `graph` is a flowchart. */
export function diagramKind(source: string): Kind {
  const name = keyword(source);
  const kind = !name ? "" : name === "graph" ? "flowchart" : name.replace(/-(v2|beta|elk)$/, "").replace(/Diagram$/, "").toLowerCase();
  return kind in diagramLimits ? (kind as Kind) : "other";
}

// Laid out as graphs: a link counts as a statement does.
const graphs = new Set<Kind>(["flowchart", "class", "state", "er"]);
// What else a statement holds: a block row's blocks, the values of a chart's list.
const marks: Partial<Record<Kind, RegExp>> = { block: /\s+/g, other: /,/g };
const link = /--|==|\.\.|-\.|~~/;
const ands = (s: string) => s.split("&").length - 1;

/** A graph's statement: itself, each node joined with `&`, and each link, once for every pair of nodes it joins. */
function graphStatement(s: string) {
  let size = 1;
  let before = 0;
  for (const group of s.split(link)) {
    const nodes = 1 + ands(group);
    size += nodes - 1 + before * nodes;
    before = nodes;
  }
  return size;
}

// Numbers that Mermaid draws as that many things: a block diagram's `space:N`, a packet's bit ranges (a row for every
// 32 bits).
const space = /\bspace:(\d+)/g;
const bits = /^(?:(\d+)(?:-(\d+))?|\+(\d+))\s*:/;
function expanded(name: string | undefined, s: string) {
  if (name === "block-beta" || name === "block") {
    let n = 0;
    for (const [, width] of s.matchAll(space)) n += Number(width);
    return n;
  }
  if (name === "packet-beta" || name === "packet") {
    const m = bits.exec(s);
    const count = !m ? 0 : m[3] !== undefined ? Number(m[3]) : m[2] !== undefined ? Number(m[2]) - Number(m[1]) + 1 : 1;
    return Math.floor(count / 1600);
  }
  return 0;
}

/**
 * Statements (lines and `;`-separated parts, not blank, not `%%` comments) with what they hold: in a graph its
 * nodes and links, in a block diagram each block of a row, in the other types each item of a `,`-separated list,
 * and the things a number in it stands for.
 */
export function diagramSize(source: string) {
  const kind = diagramKind(source);
  const name = keyword(source);
  const graph = graphs.has(kind);
  const mark = marks[kind];
  let size = 0;
  for (const line of lines(source))
    for (const part of line.split(";")) {
      const s = part.trim();
      if (!s || s.startsWith("%%")) continue;
      size += graph ? graphStatement(s) : 1 + (mark ? (s.match(mark)?.length ?? 0) : 0);
      size += expanded(name, s);
    }
  return size;
}

/** The same for a diagram's text, whatever its type: all its characters, and its longest word. */
export const textLimits = {
  chars: { auto: 2400, cap: 32_000 },
  word: { auto: 2000, cap: 16_000 },
} as const;

/** The longest word as Mermaid wraps text: words end at a plain space or a line end. */
function longestWord(source: string) {
  let longest = 0;
  for (const [word] of source.matchAll(/[^ \r\n]+/g)) longest = Math.max(longest, word.length);
  return longest;
}

/** Up to `auto` any number in a diagram draws by itself, up to `cap` when asked: the types of values aside. */
export const numberLimits = { auto: 10_000, cap: 1_000_000 } as const;
const values = new Set(["pie", "xychart-beta", "xychart", "sankey-beta", "sankey", "radar-beta", "treemap-beta", "treemap"]);

/** The largest whole number written outside a quoted string, not part of a name or a colour. */
function largestNumber(source: string) {
  let largest = 0;
  for (const [token, digits] of source.matchAll(/"[^"\r\n]*"|(?<![\w.#])(\d+)/g)) if (digits && token) largest = Math.max(largest, Number(digits));
  return largest;
}

/**
 * Whether the diagram draws by itself, only when asked, or never here. With a directive or front matter that may
 * change Mermaid's configuration, every type takes the limits of the other types.
 */
export function drawing(source: string): "itself" | "asked" | "never" {
  if (source.length > textLimits.chars.cap) return "never";
  // A gantt with excluded days shows its source here.
  if (/gantt/i.test(source) && /excludes/i.test(source)) return "never";
  const kind = diagramKind(source);
  const { auto, cap } = diagramLimits[body(source).configured ? "other" : kind];
  const own = diagramLimits[kind];
  const text = "chars" in own ? own.chars : textLimits.chars;
  const [size, chars, word] = [diagramSize(source), source.length, longestWord(source)];
  const number = values.has(keyword(source) ?? "") ? 0 : largestNumber(source);
  if (size > cap || chars > text.cap || word > textLimits.word.cap || number > numberLimits.cap) return "never";
  return size > auto || chars > text.auto || word > textLimits.word.auto || number > numberLimits.auto ? "asked" : "itself";
}
