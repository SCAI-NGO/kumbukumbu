// SPDX-License-Identifier: AGPL-3.0-or-later
// SVG as an image in the web app and on the reader page (docs/superpowers/specs/2026-11-17-svg-as-image-design.md;
// PR #228): shown through an object URL in an `<img>`, where no script runs, never inline. The reader link is a
// secret: read into a variable and checked by shape, never in a message, a screenshot or a log.
//
// Promises in scope, and the test that covers each:
// 1. `![[x.svg]]` in a note is an image among the others: a chip that opens the lightbox, where it shows in an `<img>`
//    from an object URL; its elements never enter the page, and its script never runs (decisions 1, 3)
//    → "svg: ![[x.svg]] opens as an image …".
// 2. A published page shows it the same way, through an `<img>` from an object URL (decision 3) → "svg: a published
//    page shows the SVG …".
// 3. The upload sheet takes an SVG as it is, never shrunk or converted: the bytes stored are the bytes picked, and one
//    over 15 MB is skipped with the reason instead of offered for shrinking as a photo would be (decisions 1, 8)
//    → "svg: the upload sheet takes an SVG as it is …".
// Left out: the SPA export (decision 3): there is none in 0.6.0, the export in Settings → Data being a ZIP of the
// files (decision 9), which Kumbu² doesn't show; the import dialog's formats: its own unit tests; WebDAV (decision 9):
// not shown by Kumbu²; the server's headers and the strip: e2e/mcp/svg.spec.ts.
import type { Page, TestInfo } from "@playwright/test";
import { readFileSync, writeFileSync } from "node:fs";
import { randomBytes } from "node:crypto";
import { Api, encodePath, Mcp } from "../support/api";
import { baseURL } from "../support/env";
import { reserveReaderRead } from "../support/session";
import { redact } from "../mcp/fixtures";
import { expect, onPhone, shot, test } from "../support/test";

test.use({ screenshot: "off" });

const readerLink = new RegExp(`^${baseURL.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}/s#\\S+$`);

/** A green drawing with a rect of its own id, and a script that would rename the page if it ever ran. */
const drawing = (id: string) =>
  `<svg xmlns="http://www.w3.org/2000/svg" width="120" height="60" viewBox="0 0 120 60">` +
  `<rect id="e2e-${id}" width="120" height="60" fill="#2a6"/><script>top.document.title = "e2e-ran-${id}"</script></svg>\n`;

/** Opens a note by its address; on the phone the install hint is answered with Not now, as a person would. */
async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(`/notes/${encodePath(path)}`);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** An `<img>` shown from an object URL, loaded, and nothing of the drawing in the page itself. */
async function shownAsImage(page: Page, image: ReturnType<Page["getByRole"]>, id: string) {
  await expect(image).toBeVisible();
  await expect(image).toHaveAttribute("src", /^blob:/);
  await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth), "the image loaded").toBeGreaterThan(0);
  await expect(page.locator(`#e2e-${id}`), "the drawing's elements in the page").toHaveCount(0);
  expect(await page.title(), "the drawing's script ran").not.toBe(`e2e-ran-${id}`);
}

let written: string[] = [];
test.afterEach(async ({ mcp }) => {
  if (written.length > 0) await mcp.sh(`rm ${written.map((p) => `"${p}"`).join(" ")}`);
  written = [];
});

test("svg: ![[x.svg]] opens as an image in the lightbox, never inline @svg @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const id = folder.slice(-6);
  const svg = `/Work/${folder}/drawing ${id}.svg`;
  const note = `/Work/${folder}/Drawing ${id}.md`;
  written.push(svg, note);
  await aliceApi.putFile(svg, Buffer.from(drawing(id)));
  await aliceApi.putNote(note, `# Drawing\n\n![[drawing ${id}.svg]]\n`);

  await openNote(page, note);
  const article = page.locator("article");
  const chip = article.getByRole("link", { name: `Open the image drawing ${id}.svg` });
  await expect(chip).toHaveAttribute("href", `/view/${encodePath(svg)}`);
  await expect(article.locator("svg #e2e-" + id)).toHaveCount(0);

  await chip.click();
  const viewer = page.getByRole("main", { name: `drawing ${id}.svg` });
  await shownAsImage(page, viewer.getByRole("img", { name: `drawing ${id}.svg` }), id);
  await shot(page, testInfo, "svg");
  await viewer.getByRole("button", { name: "Close" }).click();
  await expect(viewer).toHaveCount(0);
});

test.describe("svg published", () => {
  let alice: Api;
  /** Alice's agent with "May publish" in Work. */
  let key: Mcp;

  test.beforeAll(async () => {
    alice = await Api.as("alice");
    key = await Mcp.create(alice, `e2e svg page ${randomBytes(3).toString("hex")}`, { Work: "write" }, ["Work"]);
  });

  test.afterAll(async () => {
    await key?.dispose();
    await alice?.dispose();
  });

  test("svg: a published page shows the SVG through an <img> too @svg @phone", async ({ browser, projectContextOptions, aliceApi, folder }) => {
    const id = folder.slice(-6);
    const svg = `/Work/${folder}/sketch ${id}.svg`;
    const note = `/Work/${folder}/Sketch ${id}.md`;
    written.push(svg, note);
    await aliceApi.putFile(svg, Buffer.from(drawing(id)));
    await aliceApi.putNote(note, `# Sketch\n\n![[sketch ${id}.svg]]\n`);

    const reader = await browser.newContext(projectContextOptions);
    try {
      const added = await key.sh(`publish add "${note}"`);
      expect(added.isError, `publish add failed:\n${redact(added.text)}`).toBe(false);
      const link = /^link \(shown once\): (\S+)$/m.exec(added.text)?.[1] ?? "";
      expect(readerLink.test(link), "publish add printed a /s# link").toBe(true);

      const view = await reader.newPage();
      // The copy, then its file.
      await reserveReaderRead();
      await reserveReaderRead();
      // A failed goto would print the address: said without it.
      await view.goto(link).catch(() => {
        throw new Error("the reader page did not open");
      });
      await expect(view.getByRole("heading", { level: 1 })).toHaveText("Sketch");
      const images = view.getByRole("article").getByRole("img");
      await expect(images).toHaveCount(1);
      await shownAsImage(view, images, id);
    } finally {
      await reader.close();
      await key.sh(`publish rm "${note}"`);
    }
    expect((await key.sh(`publish ls /Work/${folder}`)).text).toBe(`no published notes below /Work/${folder}`);
  });
});

/** The file tree: docked on the desktop, a drawer on the phone. */
async function openTree(page: Page, testInfo: TestInfo) {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  await page.getByRole("button", { name: "Open file tree" }).click();
  return page.getByRole("dialog", { name: "File tree" });
}

test("svg: the upload sheet takes an SVG as it is, and skips one over 15 MB instead of shrinking it @svg", async ({ page, aliceApi, folder }, testInfo) => {
  const id = folder.slice(-6);
  const note = `/Work/${folder}/Hub ${id}.md`;
  // Large on screen (3000 px a side), small in bytes; and one a byte over 15 MB.
  const small = testInfo.outputPath(`drawing ${id}.svg`);
  const big = testInfo.outputPath(`big ${id}.svg`);
  const head = `<svg xmlns="http://www.w3.org/2000/svg"><!--`;
  const tail = `--></svg>`;
  writeFileSync(small, `<svg xmlns="http://www.w3.org/2000/svg" width="3000" height="3000"><!-- kept as written -->\n  <rect width="3000" height="3000" fill="#2a6"/>\n</svg>\n`);
  writeFileSync(big, Buffer.concat([Buffer.from(head), Buffer.alloc(15 * 1024 * 1024 + 1 - head.length - tail.length, 0x61), Buffer.from(tail)]));
  written.push(note, `/Work/${folder}/drawing ${id}.svg`);
  await aliceApi.putNote(note, "# Hub\n");

  await openNote(page, note);
  const tree = await openTree(page, testInfo);
  await tree.getByRole("button", { name: `Actions for ${folder}` }).click();
  await page.getByRole("menuitem", { name: "Upload here" }).click();
  const sheet = page.getByRole("dialog", { name: "Upload" });
  await sheet.getByLabel("Choose files").setInputFiles([small, big]);
  await expect(sheet.getByText("1 file to upload, 1 file skipped")).toBeVisible();
  // Not offered for shrinking, as a photo over 15 MB would be: skipped, with the reason.
  await expect(sheet.getByRole("list", { name: "Images over 15 MB" })).toHaveCount(0);
  await sheet.getByText("1 file skipped", { exact: true }).click();
  await expect(sheet.getByRole("list", { name: "Skipped files" }).getByRole("listitem")).toHaveText([
    `big ${id}.svglarger than 15 MB; an SVG image is read whole to be checked, so it can have at most 15 MB`,
  ]);
  await sheet.getByRole("button", { name: "Upload", exact: true }).click();
  await expect(sheet.getByRole("status")).toHaveText("0 notes and 1 file uploaded, 0 replaced, 1 file skipped");
  await sheet.getByRole("button", { name: "Done" }).click();

  const stored = await aliceApi.send("GET", `/api/files/${encodePath(`/Work/${folder}/drawing ${id}.svg`)}`);
  expect(stored.status()).toBe(200);
  expect(stored.headers()["content-type"]).toBe("image/svg+xml");
  expect((await stored.body()).equals(readFileSync(small)), "the bytes stored are the bytes picked").toBe(true);
  expect((await aliceApi.send("GET", `/api/files/${encodePath(`/Work/${folder}/big ${id}.svg`)}`)).status(), "the large one wasn't sent").toBe(404);
});
