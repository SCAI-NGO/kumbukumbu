// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, test, type Page } from "@playwright/test";

type Render = (source: string, dark: boolean) => Promise<{ svg: string; alt: string }>;

/** What the drawn SVG holds, read in the page as an image reads it (XML). */
type Drawn = {
  links: number;
  scripts: number;
  references: string[];
  handlers: string[];
  foreignObjects: number;
  /** Inside a `foreignObject`: elements other than MathML or a `div`/`span`, and styles that name a URL. */
  foreign: string[];
  foreignText: string;
  /** The width the drawing has as an `<img>` of its `data:` URL. */
  width: number;
};

async function draw(page: Page, source: string): Promise<Drawn> {
  return page.evaluate(async (source) => {
    const { svg } = await (window as unknown as { renderDiagram: Render }).renderDiagram(source, false);
    const doc = new DOMParser().parseFromString(svg, "image/svg+xml");
    if (doc.getElementsByTagName("parsererror").length) throw new Error("The drawing is no well-formed XML.");
    const all = [...doc.getElementsByTagName("*")];
    const attributes = all.flatMap((e) => [...e.attributes]);
    const objects = all.filter((e) => e.localName === "foreignObject");
    const inside = objects.flatMap((o) => [...o.getElementsByTagName("*")]);
    const mathml = "http://www.w3.org/1998/Math/MathML";
    const xhtml = "http://www.w3.org/1999/xhtml";
    const image = new Image();
    image.src = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
    await image.decode();
    return {
      links: all.filter((e) => e.localName === "a").length,
      scripts: all.filter((e) => e.localName === "script").length,
      references: attributes.filter((a) => /^(href|src)$/i.test(a.localName)).map((a) => `${a.name}=${a.value}`),
      handlers: attributes.filter((a) => /^on/i.test(a.localName)).map((a) => a.name),
      foreignObjects: objects.length,
      foreign: [
        ...inside.filter((e) => e.namespaceURI !== mathml && !(e.namespaceURI === xhtml && /^(div|span)$/.test(e.localName))).map((e) => e.localName),
        ...inside.flatMap((e) => [...e.attributes]).filter((a) => /url\(/i.test(a.value)).map((a) => `${a.name}=${a.value}`),
      ],
      foreignText: objects.map((o) => o.textContent ?? "").join(" "),
      width: image.naturalWidth,
    };
  }, source);
}

/** Nothing in the drawing links, loads or runs anything. */
function expectInert(drawn: Drawn) {
  expect(drawn.links, "no link").toBe(0);
  expect(drawn.scripts, "no script").toBe(0);
  expect(drawn.references, "no href or src").toEqual([]);
  expect(drawn.handlers, "no event handler").toEqual([]);
  expect(drawn.foreign, "only MathML, div and span in a foreignObject").toEqual([]);
  expect(drawn.width, "the image shows").toBeGreaterThan(0);
}

test.beforeEach(async ({ page }) => {
  await page.goto("/e2e/diagrams/index.html");
  await page.waitForFunction(() => "renderDiagram" in window);
});

test("a flowchart with a link is drawn as text, without the link and without a foreignObject", async ({ page }) => {
  const drawn = await draw(page, 'flowchart LR\n  A[Start] --> B[End]\n  click A href "https://example.org"');
  expectInert(drawn);
  expect(drawn.foreignObjects).toBe(0);
});

test("maths in a sequence diagram's message is MathML in a foreignObject", async ({ page }) => {
  const drawn = await draw(page, "sequenceDiagram\n  Alice->>Bob: $$x^2$$");
  expectInert(drawn);
  expect(drawn.foreignObjects).toBeGreaterThan(0);
  expect(drawn.foreignText).toContain("x2");
});

test("a journey's sections and tasks are text in a foreignObject", async ({ page }) => {
  const drawn = await draw(page, "journey\n  title A day\n  section Work\n    Write: 5: Me");
  expectInert(drawn);
  expect(drawn.foreignObjects).toBeGreaterThan(0);
  expect(drawn.foreignText).toContain("Work");
  expect(drawn.foreignText).toContain("Write");
});

test("maths in a class label is MathML in a foreignObject", async ({ page }) => {
  const drawn = await draw(page, 'classDiagram\n  class Shape["$$x^2$$"]');
  expectInert(drawn);
  expect(drawn.foreignObjects).toBeGreaterThan(0);
  expect(drawn.foreignText).toContain("x2");
});
