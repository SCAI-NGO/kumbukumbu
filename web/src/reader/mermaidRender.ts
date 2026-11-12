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

/** The diagram's `accTitle` and `accDescr`, else its type: "Diagram (flowchart)". */
export function diagramAlt(source: string): string {
  const title = /^\s*accTitle\s*:\s*(.*?)\s*$/m.exec(source)?.[1];
  const description = (/^\s*accDescr\s*:\s*(.*?)\s*$/m.exec(source)?.[1] ?? /^\s*accDescr\s*\{([^}]*)\}/m.exec(source)?.[1])?.trim().replace(/\s+/g, " ");
  const given = [title, description].filter((s): s is string => !!s);
  if (given.length) return given.join(". ");

  // Past a front matter block (`---` … `---`), which Mermaid takes for the diagram's title and config.
  let rest = lines(source);
  if (rest[0]?.trim() === "---") rest = rest.slice(rest.findIndex((l, i) => i > 0 && l.trim() === "---") + 1);
  const first = rest.map((l) => l.trim()).find((l) => l && !l.startsWith("%%"));
  const keyword = first && /^[A-Za-z][\w-]*/.exec(first)?.[0];
  if (!keyword) return "Diagram";
  const type = keyword === "graph" ? "flowchart" : keyword.replace(/-(v2|beta)$/, "").replace(/Diagram$/, "");
  return `Diagram (${type.toLowerCase()})`;
}

/** The root `<svg>` at the size of its `viewBox`: Mermaid gives it `width="100%"` and a `max-width`. */
function naturalSize(svg: string) {
  return svg.replace(/<svg\b(?:[^>"']|"[^"]*"|'[^']*')*>/, (tag) => {
    const box = /\sviewBox\s*=\s*["']\s*[-\d.eE]+[\s,]+[-\d.eE]+[\s,]+([\d.eE]+)[\s,]+([\d.eE]+)\s*["']/.exec(tag);
    if (!box) return tag;
    const rest = tag
      .replace(/\s(?:width|height)\s*=\s*(?:"[^"]*"|'[^']*')/g, "")
      .replace(/(\sstyle\s*=\s*)(["'])(.*?)\2/, (_, name: string, quote: string, style: string) => {
        const kept = style.replace(/max-width\s*:[^;]*;?/g, "").trim();
        return kept ? `${name}${quote}${kept}${quote}` : "";
      });
    return rest.replace(/^<svg\b/, `<svg width="${box[1]}" height="${box[2]}"`);
  });
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
    mermaid.initialize(config(dark));
    await mermaid.parse(source);
    const { svg } = await mermaid.render(id, source);
    return { svg: naturalSize(svg), alt: diagramAlt(source) };
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
