// SPDX-License-Identifier: AGPL-3.0-or-later
// Note deep links in the web app (docs/superpowers/specs/2026-11-17-note-deep-links-design.md; PR #229): the route
// `/n/:vaultId/*` (web/src/routes/DeepLink.tsx).
//
// Promises in scope, and the test that covers each:
// 1. The link in an agent's answer opens the note for Alice, under `/notes/<her name for the vault>/…`
//    → "the link in a write's answer opens the note for Alice".
// 2. The same link opens for every member, whatever each calls the vault: Bob renames his alias (and back in `finally`)
//    → "the same link opens the note for Bob, who calls the vault something else".
// 3. A file's link opens the viewer, `/view/…` → "a file's link opens the viewer". `cat` prints no version block for a
//    file, so no link: the test builds it in the spec's form from the id `GET /api/vaults` names.
// 4. A link to a removed note leads to the one "Not here" page, which offers the search filled with the note's name
//    → "a link to a removed note leads to Not here …". Search is off on the stack: no results are asserted.
// Left out: sign-in coming back to the link (needs a sign-in of its own; covered by SignIn.test.tsx); the one page for a
// discreet vault while hidden (Task 8, @serial); an archived note's link (covered by DeepLink.test.tsx, and over MCP by
// e2e/mcp/deeplinks.spec.ts).
import type { Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api, Mcp } from "../support/api";
import { shareWithBob, unshare, type Share } from "../support/share";
import { expect, shot, test } from "../support/test";

const noteUrl = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;
const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** The note's link from an agent's answer, `link to <path>: <url>`. */
async function writeWithLink(mcp: Mcp, path: string, content: string) {
  const written = await mcp.call("write", { path, content });
  expect(written.isError, written.text).toBe(false);
  const link = written.text.match(new RegExp(`^link to ${escape(path)}: (\\S+)$`, "m"))?.[1];
  expect(link, written.text).toMatch(/^http:\/\/localhost:8080\/n\/[0-9a-f]{32}\//);
  return link!;
}

/** Lands on the note: its address, and its heading in the article. On the phone the install hint is answered. */
async function expectNote(page: Page, path: string, heading: string) {
  await expect(page).toHaveURL(new RegExp(`${escape(noteUrl(path))}$`));
  await expect(page.locator("article").getByRole("heading", { name: heading, level: 1 })).toBeVisible();
  const notNow = page.getByRole("button", { name: "Not now" });
  if (await notNow.isVisible()) await notNow.click();
}

test("deeplinks: the link in a write's answer opens the note for Alice @deeplinks @phone", async ({ page, mcp, folder }, testInfo) => {
  const path = `/Work/${folder}/Garten Ü/Rosen ${folder}.md`;
  const link = await writeWithLink(mcp, path, "# Rosen\n\nPrune in March.\n");
  await page.goto(link);
  await expectNote(page, path, "Rosen");
  await shot(page, testInfo, "deeplinks");
});

test("deeplinks: a file's link opens the viewer @deeplinks @phone", async ({ page, aliceApi, seedAs, folder }) => {
  const path = `/Work/${folder}/pixel.png`;
  await seedAs("tags/smoke/pixel.png", path);
  const work = (await aliceApi.json<{ id: string; name: string }[]>("GET", "/api/vaults")).find((v) => v.name === "Work")!.id;
  await page.goto(`/n/${work}/${folder}/pixel.png`);
  await expect(page).toHaveURL(new RegExp(`/view/Work/${escape(folder)}/pixel\\.png$`));
  const image = page.getByRole("main", { name: "pixel.png" }).getByRole("img", { name: "pixel.png" });
  await expect(image).toBeVisible();
  await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth), "the image loaded").toBeGreaterThan(0);
});

test("deeplinks: a link to a removed note leads to Not here, whose search holds the note's name @deeplinks @phone", async ({ page, mcp, folder }) => {
  const name = `Gone ${folder}`;
  const path = `/Work/${folder}/${name}.md`;
  const link = await writeWithLink(mcp, path, "# Gone\n");
  const removed = await mcp.sh(`rm "${path}"`);
  expect(removed.isError, removed.text).toBe(false);

  await page.goto(link);
  await expect(page.getByRole("heading", { name: "Not here", level: 1 })).toBeVisible();
  await expect(page.getByText("This link leads to a note that isn't here: it was moved or removed, or it's in a vault you can't see right now.")).toBeVisible();
  // The link stays the link: the page names no other address.
  expect(page.url()).toBe(link);

  await page.getByRole("button", { name: "Search for it" }).click();
  const search = page.getByRole("dialog", { name: "Search" });
  await expect(search).toBeVisible();
  await expect(search.getByRole("searchbox", { name: "Search" })).toHaveValue(name);
});

test.describe("deeplinks in a shared vault", () => {
  let share: Share;
  let alice: Api;
  /** Alice's agent, writing in the shared vault. */
  let aliceKey: Mcp;

  test.beforeAll(async () => {
    share = await shareWithBob("Team", "write");
    alice = await Api.as("alice");
    aliceKey = await Mcp.create(alice, `e2e deeplinks ${randomBytes(3).toString("hex")}`, { [share.vault]: "write" });
  });

  test.afterAll(async () => {
    await aliceKey?.dispose();
    await alice?.dispose();
    if (share) await unshare(share);
  });

  test("deeplinks: the same link opens the note for Bob, who calls the vault something else @deeplinks @phone", async ({ page, bobPage, bobApi, folder }) => {
    const path = `/${share.vault}/${folder}/Plan.md`;
    const link = await writeWithLink(aliceKey, path, "# Plan\n\nOne step at a time.\n");
    const alias = `Ours-${randomBytes(3).toString("hex")}`;
    await bobApi.json("PUT", `/api/memberships/${encodeURIComponent(share.membershipId)}/alias`, { alias });
    try {
      await bobPage.goto(link);
      await expectNote(bobPage, `/${alias}/${folder}/Plan.md`, "Plan");
      // And for Alice, the owner, under her name.
      await page.goto(link);
      await expectNote(page, path, "Plan");
    } finally {
      await bobApi.json("PUT", `/api/memberships/${encodeURIComponent(share.membershipId)}/alias`, { alias: share.vault });
    }
  });
});
