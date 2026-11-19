// SPDX-License-Identifier: AGPL-3.0-or-later
// Every file type and large files in the web app (spec 2026-11-18-files-design.md, PRs #254, #257; brief, open
// point 4). The MCP side is e2e/mcp/files.spec.ts.
//
// Promises in scope (spec decision → test):
//  5. a file shows as a chip with its name and Download; the lightbox
//     shows its icon, name, size and Download, never a preview; a text
//     file outside a skill's folder gets no viewer either; Download
//     gives the same bytes                                              → "files #1", "files #2"
// 15. the upload takes any file; one over 15 MB is sent as the File
//     itself with a progress bar; it comes back with the same bytes     → "files #3" (@large, about 100 MB)
// Left out: a file over the bound refused before sending (the bound is 1 GiB: UploadSheet.test.tsx covers it with a
// small bound), thumbnails over 15 MB by their icon (UI unit tests), the drop (Playwright can't drop a file of the
// disk), the ZIP import of a large file (not cheap: a 100 MB ZIP made in the test), Obsidian 0.3.1, rclone and 500 MB
// over a proxy (spec's hand tests).
import type { Download, Locator, Page, TestInfo } from "@playwright/test";
import { createHash, randomBytes } from "node:crypto";
import { createReadStream, createWriteStream, rmSync } from "node:fs";
import { once } from "node:events";
import { encodePath, type Api } from "../support/api";
import { expect, onPhone, shot, test } from "../support/test";

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

/** Opens a note by its address; on the phone the install hint is answered with Not now, as a person would. */
async function openNote(page: Page, path: string) {
  await page.goto(noteUrl(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
  const notNow = page.getByRole("button", { name: "Not now" });
  if (await notNow.isVisible()) await notNow.click();
}

/** The file tree: docked on the desktop, a drawer on the phone. */
async function openTree(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  await page.getByRole("button", { name: "Open file tree" }).click();
  return page.getByRole("dialog", { name: "File tree" });
}

/** The SHA-256 of a file on the disk, read as a stream: the large one isn't held in memory. */
async function sha256File(path: string) {
  const hash = createHash("sha256");
  for await (const chunk of createReadStream(path)) hash.update(chunk as Buffer);
  return hash.digest("hex");
}

const sha256 = (bytes: Buffer) => createHash("sha256").update(bytes).digest("hex");

/** Clicks `button` and waits for the download it starts; the bytes' SHA-256 from where Playwright saved them. */
async function downloaded(page: Page, button: Locator) {
  const [download] = await Promise.all([page.waitForEvent("download"), button.click()]);
  return { name: download.suggestedFilename(), sha: await sha256File(await savedPath(download)) };
}

async function savedPath(download: Download) {
  const path = await download.path();
  if (!path) throw new Error(`the download of ${download.suggestedFilename()} failed: ${await download.failure()}`);
  return path;
}

async function removeFile(api: Api, path: string) {
  const response = await api.send("DELETE", `/api/files/${encodePath(path)}`);
  if (!response.ok() && response.status() !== 404) throw new Error(`DELETE file ${path}: ${response.status()}`);
}

test("files #1: a file in a note is a chip with Download; it opens as a card, never a preview, and downloads its bytes @files @phone", async ({ page, aliceApi, folder }, testInfo) => {
  // A Word document's name, bytes of no format Kumbu² knows.
  const bytes = Buffer.concat([Buffer.from([0x50, 0x4b, 0x03, 0x04, 0x00]), randomBytes(2043)]);
  const file = `/Work/${folder}/report.docx`;
  const note = `/Work/${folder}/Hub.md`;
  await aliceApi.putFile(file, bytes);
  await aliceApi.putNote(note, `# Hub\n\nThe [[report.docx]] is attached.\n`);
  try {
    await openNote(page, note);
    const article = page.locator("article");
    const chip = article.getByRole("link", { name: "Open the file report.docx" });
    await expect(chip).toHaveAttribute("href", `/view/Work/${folder}/report.docx`);
    // Download straight from the chip.
    const fromChip = await downloaded(page, article.getByRole("button", { name: "Download report.docx" }));
    expect(fromChip).toEqual({ name: "report.docx", sha: sha256(bytes) });

    await chip.click();
    const viewer = page.getByRole("main", { name: "report.docx" });
    const card = viewer.getByRole("region", { name: "report.docx" });
    await expect(card).toBeVisible();
    await expect(card.getByText("report.docx", { exact: true })).toBeVisible();
    await expect(card.getByText("2.0 KB", { exact: true })).toBeVisible();
    await expect(card.getByText("No preview for this file", { exact: true })).toBeVisible();
    // Never a preview: no picture, no frame, no text of the file's.
    await expect(viewer.getByRole("img")).toHaveCount(0);
    await expect(viewer.locator("iframe, embed, object, canvas")).toHaveCount(0);
    await shot(page, testInfo, "files");

    const fromCard = await downloaded(page, card.getByRole("button", { name: "Download" }));
    expect(fromCard).toEqual({ name: "report.docx", sha: sha256(bytes) });
  } finally {
    await aliceApi.deleteNote(note).catch(() => undefined);
    await removeFile(aliceApi, file);
  }
});

test("files #2: a text file outside a skill's folder opens as a card too, without a viewer @files", async ({ page, aliceApi, folder }) => {
  const text = "Item,Amount\nlantern,12.50\n";
  const file = `/Work/${folder}/costs.csv`;
  await aliceApi.putFile(file, Buffer.from(text));
  try {
    await page.goto(`/view/Work/${folder}/costs.csv`);
    const viewer = page.getByRole("main", { name: "costs.csv" });
    const card = viewer.getByRole("region", { name: "costs.csv" });
    await expect(card.getByText("No preview for this file", { exact: true })).toBeVisible();
    await expect(card.getByText(`${text.length} B`, { exact: true })).toBeVisible();
    await expect(viewer.getByText("lantern", { exact: false }), "its text isn't shown").toHaveCount(0);
    const fromCard = await downloaded(page, card.getByRole("button", { name: "Download" }));
    expect(fromCard).toEqual({ name: "costs.csv", sha: sha256(Buffer.from(text)) });
  } finally {
    await removeFile(aliceApi, file);
  }
});

test("files #3: about 100 MB go through the upload with its progress bar and download with the same bytes @files @large", async ({ page, aliceApi, folder }, testInfo) => {
  test.setTimeout(600_000);
  // Made in the test, written to the disk (setInputFiles refuses a buffer over 50 MB), removed at the end.
  const source = testInfo.outputPath("big.bin");
  const out = createWriteStream(source);
  for (let i = 0; i < 100; i++) if (!out.write(randomBytes(1024 * 1024))) await once(out, "drain");
  out.end();
  await once(out, "finish");
  const sha = await sha256File(source);

  const note = `/Work/${folder}/Hub.md`;
  const file = `/Work/${folder}/big.bin`;
  await aliceApi.putNote(note, "# Hub\n");
  try {
    await openNote(page, note);
    const tree = await openTree(page, testInfo);
    await tree.getByRole("button", { name: `Actions for ${folder}` }).click();
    await page.getByRole("menuitem", { name: "Upload here" }).click();
    const sheet = page.getByRole("dialog", { name: "Upload" });
    await sheet.getByLabel("Choose files").setInputFiles(source);
    // Over 15 MB: the file's own progress bar while it is sent. On this machine 100 MB go in a second or two, so the
    // bar is recorded as it changes rather than looked for afterwards, which would race the end of the upload.
    await page.evaluate(() => {
      const seen: number[] = [];
      (window as unknown as { progressSeen: number[] }).progressSeen = seen;
      new MutationObserver(() => {
        const bar = document.querySelector('[role="progressbar"][aria-label="big.bin sent"]');
        const now = bar ? Number(bar.getAttribute("aria-valuenow")) : undefined;
        if (now !== undefined && seen[seen.length - 1] !== now) seen.push(now);
      }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ["aria-valuenow"] });
    });
    await sheet.getByRole("button", { name: "Upload", exact: true }).click();
    await expect(sheet.getByRole("status")).toHaveText("0 notes and 1 file uploaded, 0 replaced, 0 files skipped", { timeout: 300_000 });
    const progress = await page.evaluate(() => (window as unknown as { progressSeen: number[] }).progressSeen);
    expect(progress[0], "the bar starts at 0 %").toBe(0);
    expect(progress.length, "and moves while the file is sent").toBeGreaterThan(1);
    expect(progress, "never backwards").toEqual([...progress].sort((a, b) => a - b));
    await sheet.getByRole("button", { name: "Done" }).click();

    await page.goto(`/view/Work/${folder}/big.bin`);
    const card = page.getByRole("main", { name: "big.bin" }).getByRole("region", { name: "big.bin" });
    await expect(card.getByText("100.0 MB", { exact: true })).toBeVisible();
    const [download] = await Promise.all([page.waitForEvent("download"), card.getByRole("button", { name: "Download" }).click()]);
    const saved = await savedPath(download);
    expect(await sha256File(saved), "the same bytes back").toBe(sha);
    await download.delete();
  } finally {
    rmSync(source, { force: true });
    await aliceApi.deleteNote(note).catch(() => undefined);
    await removeFile(aliceApi, file);
  }
});
