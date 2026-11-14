// SPDX-License-Identifier: AGPL-3.0-or-later
// Recent changes (the flags report's hand-test checklist, steps 14-17; spec 2026-11-14-flags-explorer-photo-design.md §4).
// The page is paged and shared by every test in Work: each test narrows to its own folder with the words filter.
import type { Locator, Page, TestInfo } from "@playwright/test";
import { encodePath } from "../support/api";
import { revealDiscreet, hideDiscreet } from "../support/session";
import { expect, onPhone, shot, test } from "../support/test";

/** Opens a note by its address; the install hint is answered with Not now, as a person would. */
async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(`/notes/${encodePath(path.replace(/^\//, ""))}`);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** The file tree: docked on the desktop, a drawer on the phone. */
async function openTree(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  const drawer = page.getByRole("dialog", { name: "File tree" });
  if (!(await drawer.isVisible())) await page.getByRole("button", { name: "Open file tree" }).click();
  await expect(drawer).toBeVisible();
  return drawer;
}

/** From the tree's top row to the page, as a person does it (the page stays loaded: no reload). */
async function toRecent(page: Page, testInfo: TestInfo) {
  await (await openTree(page, testInfo)).getByRole("link", { name: "Recent changes" }).click();
  await expect(page).toHaveURL(/\/recent$/);
  await expect(page.getByRole("heading", { name: "Recent changes" })).toBeVisible();
}

const rows = (page: Page) => page.getByRole("list", { name: "Recent changes" }).getByRole("listitem");
const words = (page: Page) => page.getByRole("searchbox", { name: "Find by name" });
const vaultChips = (page: Page) => page.getByRole("group", { name: "Vaults" });

test("flags #14: Recent changes lists the newest first with path, who, when and flag @recent @serial @phone", async ({ page, aliceApi, seedAs, folder }, testInfo) => {
  // The older note first, the flagged certificate after it.
  await aliceApi.putNote(`/Work/${folder}/Pump/Valve.md`, "# Valve\n\nThe pump's valve.\n");
  await seedAs("tags/recent/Pump/Certificate.md", `/Work/${folder}/Pump/Certificate.md`);
  await openNote(page, `/Work/${folder}/Pump/Certificate.md`);
  await toRecent(page, testInfo);

  await words(page).fill(folder);
  await expect(rows(page)).toHaveCount(2);
  const [newer, older] = [rows(page).nth(0), rows(page).nth(1)];
  await expect(newer).toHaveText(new RegExp(`^Certificate.*Work/${folder}/Pump · You · (just now|.+ ago)$`));
  await expect(newer.getByRole("img", { name: "Red flag" })).toBeVisible();
  await expect(older).toHaveText(new RegExp(`^Valve.*Work/${folder}/Pump · You · (just now|.+ ago)$`));
  await expect(older.getByRole("img")).toHaveCount(0);
  await expect(newer.locator("time")).toHaveAttribute("datetime", /^\d{4}-\d\d-\d\dT/);
  await shot(page, testInfo, "recent");
});

test("flags #15: chips, words and flags narrow the rows; Show more; rows open notes and the lightbox @recent @serial @phone", async ({ freshSession, aliceApi, seedAs, folder }, testInfo) => {
  test.slow();
  // More rows than a page (50) in the test's folder, then the three files on top of them.
  const filler = Array.from({ length: 52 }, (_, i) => `/Work/${folder}/Bulk/Filler ${String(i + 1).padStart(2, "0")}.md`);
  for (let i = 0; i < filler.length; i += 8) await Promise.all(filler.slice(i, i + 8).map((p) => aliceApi.putNote(p, `# ${p.split("/").pop()}\n`)));
  for (const name of ["Certificate.md", "scan.png", "manual.pdf"]) await seedAs(`tags/recent/Pump/${name}`, `/Work/${folder}/Pump/${name}`);

  const { page } = await freshSession("alice");
  try {
    await openNote(page, `/Work/${folder}/Pump/Certificate.md`);
    if (onPhone(testInfo)) await openTree(page, testInfo);
    await revealDiscreet(page);
    await page.goto("/recent");
    await expect(page.getByRole("heading", { name: "Recent changes" })).toBeVisible();

    // The Home chip: its note is there with it, gone without it, back again.
    await words(page).fill("Zahnarzt");
    const home = vaultChips(page).getByRole("button", { name: "Home" });
    await expect(home).toHaveAttribute("aria-pressed", "true");
    await expect(rows(page).filter({ hasText: "Zahnarzt" })).toHaveCount(1);
    await home.click();
    await expect(home).toHaveAttribute("aria-pressed", "false");
    await expect(page.getByRole("status")).toHaveText("Nothing matches that.");
    await expect(page.getByText("Zahnarzt")).toHaveCount(0);
    await home.click();
    await expect(rows(page).filter({ hasText: "Zahnarzt" })).toHaveCount(1);

    // Words: "pump cert" within the folder leaves the certificate alone.
    await words(page).fill(`${folder} pump cert`);
    await expect(rows(page)).toHaveCount(1);
    await expect(rows(page).first()).toContainText(`Work/${folder}/Pump`);
    await expect(rows(page).first()).toContainText("Certificate");

    // A flag chip: the red ones of the folder, the certificate; Green has none.
    await words(page).fill(folder);
    await expect(rows(page)).toHaveCount(50);
    const flags = page.getByRole("group", { name: "Find by flag" });
    await flags.getByRole("button", { name: "Red" }).click();
    await expect(rows(page)).toHaveCount(1);
    await expect(rows(page).first().getByRole("img", { name: "Red flag" })).toBeVisible();
    await flags.getByRole("button", { name: "Green" }).click();
    await expect(page.getByRole("status")).toHaveText("Nothing matches that.");
    await flags.getByRole("button", { name: "Green" }).click();
    await expect(rows(page)).toHaveCount(50);

    // Show more adds the rows after the first page: 52 fillers and the three files.
    await page.getByRole("button", { name: "Show more" }).click();
    await expect(rows(page)).toHaveCount(55);
    await expect(page.getByRole("button", { name: "Show more" })).toHaveCount(0);

    // The rows open: a note as the note, the image and the PDF in the lightbox.
    await rows(page).filter({ hasText: "scan.png" }).getByRole("link").click();
    await expect(page).toHaveURL(new RegExp(`/view/Work/${folder}/Pump/scan\\.png$`));
    const viewer = page.getByRole("main", { name: "scan.png" });
    const image = viewer.getByRole("img", { name: "scan.png" });
    await expect(image).toBeVisible();
    await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth), "the image loaded").toBeGreaterThan(0);
    await viewer.getByRole("button", { name: "Close" }).click();

    await page.goto("/recent");
    await words(page).fill(`${folder} manual`);
    await expect(rows(page)).toHaveCount(1);
    await rows(page).getByRole("link").click();
    await expect(page).toHaveURL(new RegExp(`/view/Work/${folder}/Pump/manual\\.pdf$`));
    await expect(page.getByRole("main", { name: "manual.pdf" })).toBeVisible();
    await page.getByRole("main", { name: "manual.pdf" }).getByRole("button", { name: "Close" }).click();

    await page.goto("/recent");
    await words(page).fill(`${folder} certificate`);
    await expect(rows(page)).toHaveCount(1);
    await rows(page).getByRole("link").click();
    await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Pump/Certificate\\.md$`));
    await expect(page.locator("article").getByRole("heading", { name: "Certificate", level: 1 })).toBeVisible();
  } finally {
    await page.goto("/");
    if (onPhone(testInfo) && (await page.getByRole("button", { name: "Open file tree" }).isVisible())) await page.getByRole("button", { name: "Open file tree" }).click();
    if (await page.getByRole("button", { name: "Hide discreet vaults" }).first().isVisible()) await hideDiscreet(page);
  }
});

test("flags #16: hiding in this tab and from a second tab leaves nothing of Home @recent @serial @phone", async ({ freshSession, seedAs, folder }, testInfo) => {
  await seedAs("tags/recent/Pump/Certificate.md", `/Work/${folder}/Pump/Certificate.md`);
  const { context, page } = await freshSession("alice");
  const note = `/Work/${folder}/Pump/Certificate.md`;
  const pill = (p: Page) => p.getByRole("group", { name: "Discreet vaults shown" });
  const homeGone = async (p: Page) => {
    await expect(vaultChips(p).getByRole("button", { name: "Home" })).toHaveCount(0);
    await expect(p.getByText("Zahnarzt")).toHaveCount(0);
  };
  const other = await context.newPage();
  try {
    await openNote(page, note);
    await openNote(other, note);

    // First: reveal, see Home in Recent changes and its chip, hide in this tab.
    if (onPhone(testInfo)) await openTree(page, testInfo);
    await revealDiscreet(page);
    await toRecent(page, testInfo);
    await words(page).fill("Zahnarzt");
    await expect(rows(page).filter({ hasText: "Zahnarzt" })).toHaveCount(1);
    await expect(vaultChips(page).getByRole("button", { name: "Home" })).toBeVisible();
    await page.getByRole("link", { name: "Back to the notes" }).click();
    if (onPhone(testInfo)) await openTree(page, testInfo);
    await hideDiscreet(page);
    // The tree and the vault menu, then Recent changes again: nothing of Home.
    const tree = await openTree(page, testInfo);
    await expect(tree.getByText("Zahnarzt")).toHaveCount(0);
    await page.getByRole("button", { name: /^Switch vault/ }).click();
    await expect(page.getByRole("dialog", { name: /vault/i }).getByRole("button", { name: /^Work/ })).toBeVisible();
    await expect(page.getByRole("dialog", { name: /vault/i }).getByText("Home")).toHaveCount(0);
    await page.keyboard.press("Escape");
    await toRecent(page, testInfo);
    await words(page).fill("Zahnarzt");
    await expect(page.getByRole("status")).toHaveText("Nothing matches that.");
    await homeGone(page);

    // Second: reveal again (Home is back), hide from the second tab; the first, on Recent changes, learns of it by itself.
    await page.getByRole("link", { name: "Back to the notes" }).click();
    if (onPhone(testInfo)) await openTree(page, testInfo);
    await revealDiscreet(page);
    await toRecent(page, testInfo);
    await words(page).fill("Zahnarzt");
    await expect(rows(page).filter({ hasText: "Zahnarzt" })).toHaveCount(1);
    await expect(vaultChips(page).getByRole("button", { name: "Home" })).toBeVisible();
    await page.evaluate(() => ((window as unknown as { __mark: number }).__mark = 1));

    await other.reload();
    await expect(other.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
    if (onPhone(testInfo)) await openTree(other, testInfo);
    await expect(pill(other).first()).toBeVisible();
    await hideDiscreet(other);

    await homeGone(page);
    await expect(rows(page).filter({ hasText: "Zahnarzt" })).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { __mark?: number }).__mark), "no reload").toBe(1);
  } finally {
    for (const p of [other, page]) {
      await p.goto("/");
      if (onPhone(testInfo) && (await p.getByRole("button", { name: "Open file tree" }).isVisible())) await p.getByRole("button", { name: "Open file tree" }).click();
      if (await p.getByRole("button", { name: "Hide discreet vaults" }).first().isVisible()) await hideDiscreet(p);
    }
  }
});

test("flags #17: Bob sees Work's rows and nothing of Alice's own vaults @recent @serial @phone", async ({ bobPage, seedAs, folder }) => {
  await seedAs("tags/recent/Pump/Certificate.md", `/Work/${folder}/Pump/Certificate.md`);
  await bobPage.goto("/recent");
  await expect(bobPage.getByRole("heading", { name: "Recent changes" })).toBeVisible();
  await words(bobPage).fill(folder);
  const row = rows(bobPage).filter({ hasText: "Certificate" });
  await expect(row).toHaveCount(1);
  // Another member's name reads as Activity writes it, in quotes (WriterLabels.Quoted); the checklist says Alice (F-?? unclear, see the report).
  await expect(row).toHaveText(new RegExp(`^Certificate.*Work/${folder}/Pump · "?alice"? · (just now|.+ ago)$`, "i"));

  // Home is no chip and no row; Alice's own vaults are not his (his chips are Work and what is his own).
  await words(bobPage).fill("Zahnarzt");
  await expect(bobPage.getByRole("status")).toHaveText("Nothing matches that.");
  await words(bobPage).fill("");
  await expect(rows(bobPage).first()).toBeVisible();
  await expect(vaultChips(bobPage).getByRole("button", { name: "Work" })).toBeVisible();
  await expect(vaultChips(bobPage).getByRole("button", { name: "Home" })).toHaveCount(0);
  await expect(rows(bobPage).filter({ hasText: /^[^·]*·\s*Home\// })).toHaveCount(0);
});
