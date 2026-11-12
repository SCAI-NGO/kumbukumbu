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
