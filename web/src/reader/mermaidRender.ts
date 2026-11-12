// SPDX-License-Identifier: AGPL-3.0-or-later
// Loaded only for a note with a diagram (MermaidDiagram's `import("./mermaidRender")`): Mermaid stays out of the
// first load of the app and of the reader page.
import mermaid, { type MermaidConfig } from "mermaid";

// Mermaid's default `maxTextSize`, which a longer source doesn't fail: it is drawn as a diagram saying so.
const maxTextSize = 50_000;

// `secure` keeps a diagram's `%%{init}%%` directive from undoing any of these (Mermaid's own list protects only the
// first five). Labels as SVG text, no `foreignObject`, and a font an image has: it can load none.
const config = (dark: boolean): MermaidConfig => ({
  startOnLoad: false,
  securityLevel: "strict",
  suppressErrorRendering: true,
  secure: ["secure", "securityLevel", "startOnLoad", "maxTextSize", "suppressErrorRendering", "maxEdges", "htmlLabels", "flowchart", "fontFamily", "themeCSS", "themeVariables", "theme"],
  theme: dark ? "dark" : "default",
  htmlLabels: false,
  flowchart: { htmlLabels: false },
  fontFamily: 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
});

const lines = (source: string) => source.split(/\r?\n/);

/** What follows `key` at the start of a line, past blank lines as Mermaid's `\s*` reads them, and the line it is on. */
function* keyed(rows: string[], key: string) {
  for (let i = 0; i < rows.length; i++) {
    const row = rows[i].trim();
    if (!row.startsWith(key)) continue;
    let [rest, at] = [row.slice(key.length).trimStart(), i];
    while (!rest && at + 1 < rows.length) rest = rows[++at].trim();
    yield { rest, at };
  }
}

/** The text after `key:`; with nothing after the colon, the next line with text. */
function accLine(rows: string[], key: string) {
  for (const { rest, at } of keyed(rows, key)) {
    if (rest.startsWith(":")) return rest.slice(1).trim() || (rows.slice(at + 1).find((l) => l.trim())?.trim() ?? "");
  }
}

/** What stands between `key {` and the next `}`. */
function accBlock(rows: string[], key: string) {
  for (const { rest, at } of keyed(rows, key)) {
    if (!rest.startsWith("{")) continue;
    const body = [];
    for (let i = at, text = rest.slice(1); i < rows.length; text = rows[++i]) {
      const end = text.indexOf("}");
      if (end >= 0) return [...body, text.slice(0, end)].join("\n");
      body.push(text);
    }
    return undefined;
  }
}

/** The diagram's keyword, past a front matter block (`---` … `---`), which Mermaid takes for its title and config. */
function keyword(rows: string[]) {
  if (rows[0]?.trim() === "---") rows = rows.slice(rows.findIndex((l, i) => i > 0 && l.trim() === "---") + 1);
  const first = rows.map((l) => l.trim()).find((l) => l && !l.startsWith("%%"));
  return first && /^[A-Za-z][\w-]*/.exec(first)?.[0];
}

/** The diagram's `accTitle` and `accDescr`, else its type: "Diagram (flowchart)". */
export function diagramAlt(source: string): string {
  const rows = lines(source);
  const title = accLine(rows, "accTitle");
  const description = (accLine(rows, "accDescr") ?? accBlock(rows, "accDescr"))?.trim().replace(/\s+/g, " ");
  const given = [title, description].filter((s): s is string => !!s);
  if (given.length) return given.join(". ");

  const name = keyword(rows);
  if (!name) return "Diagram";
  const type = name === "graph" ? "flowchart" : name.replace(/-(v2|beta)$/, "").replace(/Diagram$/, "");
  return `Diagram (${type.toLowerCase()})`;
}

/** The shape data blocks (`A@{ … }`) as Mermaid's lexer reads them: a `"` runs to the next `"`, a `}` outside one ends the block. */
function shapeData(source: string) {
  const blocks = [];
  for (let at = source.indexOf("@{"); at >= 0; ) {
    let end = at + 2;
    for (let quoted = false; end < source.length && (quoted || source[end] !== "}"); end++) if (source[end] === '"') quoted = !quoted;
    blocks.push(source.slice(at + 2, end));
    at = source.indexOf("@{", end);
  }
  return blocks;
}

/** The source with CSS escapes undone: `\` and 1–6 hex digits (and one whitespace after them), or `\` and a character. */
const cssUnescaped = (source: string) =>
  source.replace(/\\(?:([0-9a-fA-F]{1,6})\s?|([\s\S]))/g, (_, hex: string | undefined, char: string | undefined) => {
    if (hex === undefined) return char!;
    const code = parseInt(hex, 16);
    return code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : "\ufffd";
  });

/**
 * Whether drawing would load an image into the live page, a request with the viewer's cookies that the note names:
 * a CSS function such as `url()` in any style, a shape's `img` (its YAML key spelt plainly, quoted or escaped), a
 * sequence actor's `properties` with an `icon`, or its `details`, which reads properties from an element of the page.
 */
function loadsImage(source: string) {
  if (/(?:url|image-set|image|cross-fade|element|src)\(/i.test(cssUnescaped(source))) return true;
  if (shapeData(source).some((block) => /\bimg\b|\\[xuU]/.test(block))) return true;
  const rows = lines(source);
  if (keyword(rows) !== "sequenceDiagram") return false;
  return rows.some((row) =>
    row.split(";").some((statement) => /^\s*details\b/i.test(statement) || (/^\s*properties\b/i.test(statement) && /icon|\\u/i.test(statement))),
  );
}

/**
 * The SVG as XML, its root at the size of its `viewBox` (Mermaid gives it `width="100%"` and a `max-width`). Mermaid's
 * strict output is DOMPurify's HTML serialisation (`&nbsp;`, unclosed `<br>`), which an image, read as XML, refuses.
 * The parsed document is inert: nothing of it reaches the page.
 */
function asImage(svg: string) {
  const root = new DOMParser().parseFromString(svg, "text/html").querySelector("svg");
  if (!root) throw new Error("Mermaid's drawing is no SVG.");
  const box = root.getAttribute("viewBox")?.trim().split(/[\s,]+/).map(Number);
  if (box?.length === 4 && box.every(Number.isFinite)) {
    root.setAttribute("width", String(box[2]));
    root.setAttribute("height", String(box[3]));
    const style = root.getAttribute("style")?.replace(/max-width\s*:[^;]*;?/g, "").trim();
    if (style) root.setAttribute("style", style);
    else root.removeAttribute("style");
  }
  return new XMLSerializer().serializeToString(root);
}

const firstLine = (e: unknown) => {
  const message = e instanceof Error ? e.message : typeof (e as { message?: unknown })?.message === "string" ? (e as { message: string }).message : String(e);
  return lines(message).find((l) => l.trim())?.trim() ?? "Mermaid gave no reason.";
};

let queue: Promise<unknown> = Promise.resolve();
let drawn = 0;

async function draw(source: string, dark: boolean) {
  const id = `kumbu-mermaid-${++drawn}`;
  try {
    if (source.length > maxTextSize) throw new Error("Maximum text size in diagram exceeded");
    if (loadsImage(source)) throw new Error("images in diagrams aren't loaded.");
    mermaid.initialize(config(dark));
    await mermaid.parse(source);
    const { svg } = await mermaid.render(id, source);
    return { svg: asImage(svg), alt: diagramAlt(source) };
  } catch (e) {
    // Mermaid measures in hidden elements of the live document and doesn't always take them out after a failure.
    for (const left of [id, `d${id}`, `i${id}`]) document.getElementById(left)?.remove();
    throw new Error(firstLine(e));
  }
}

/** The diagram as an SVG string, one at a time in call order: Mermaid's configuration is global. */
export async function renderDiagram(source: string, dark: boolean): Promise<{ svg: string; alt: string }> {
  const run = queue.then(() => draw(source, dark));
  queue = run.catch(() => undefined);
  return run;
}
