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
// 5. Opening a link needs a sign-in: without a session it leads to `/signin?next=<the link's path>` and shows no note
//    → "a link opened without a session leads to the sign-in".
// 6. The same "Not here" page when the vault isn't in the reader's list: Bob opens a link into a vault of Alice's he
//    isn't a member of → "a link into a vault Bob isn't a member of leads to the same Not here". Alice's `Welcome`, not
//    `Home`: her saved session keeps Home hidden, so its id isn't in her vault list without a PIN entry (Task 8's).
// Left out: the sign-in coming back to the link afterwards (needs a sign-in of its own; covered by
// SignIn.test.tsx:134-155); the one page for a discreet vault while hidden (Task 8, @serial); an archived note's link (covered by DeepLink.test.tsx, and over MCP by
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

const notHere = "This link leads to a note that isn't here: it was moved or removed, or it's in a vault you can't see right now.";

const vaultId = async (api: Api, name: string) => {
  const vault = (await api.json<{ id: string; name: string }[]>("GET", "/api/vaults")).find((v) => v.name === name);
  expect(vault, `${name} in the vault list`).toBeDefined();
  return vault!.id;
};

/** On the phone the install hint is answered with Not now, as a person would, whenever it comes up. */
const handled = new WeakSet<Page>();
async function answerInstallHint(page: Page) {
  if (handled.has(page)) return;
  handled.add(page);
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
}

/** Lands on the note: its address, and its heading in the article. */
async function expectNote(page: Page, path: string, heading: string) {
  await expect(page).toHaveURL(new RegExp(`${escape(noteUrl(path))}$`));
  await expect(page.locator("article").getByRole("heading", { name: heading, level: 1 })).toBeVisible();
}

test("deeplinks: the link in a write's answer opens the note for Alice @deeplinks @phone", async ({ page, mcp, folder }, testInfo) => {
  const path = `/Work/${folder}/Garten Ü/Rosen ${folder}.md`;
  const link = await writeWithLink(mcp, path, "# Rosen\n\nPrune in March.\n");
  await answerInstallHint(page);
  await page.goto(link);
  await expectNote(page, path, "Rosen");
  await shot(page, testInfo, "deeplinks");
});

test("deeplinks: a file's link opens the viewer @deeplinks @phone", async ({ page, aliceApi, seedAs, folder }) => {
  const path = `/Work/${folder}/pixel.png`;
  await seedAs("tags/smoke/pixel.png", path);
  const work = await vaultId(aliceApi, "Work");
  await answerInstallHint(page);
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

  // No install-hint handler here: the hint comes with the shell, under the modal search sheet, which covers its Not now;
  // nothing this test does needs it answered.
  await page.goto(link);
  await expect(page.getByRole("heading", { name: "Not here", level: 1 })).toBeVisible();
  await expect(page.getByText(notHere, { exact: true })).toBeVisible();

  await page.getByRole("button", { name: "Search for it" }).click();
  const search = page.getByRole("dialog", { name: "Search" });
  await expect(search).toBeVisible();
  await expect(search.getByRole("searchbox", { name: "Search" })).toHaveValue(name);
});

test("deeplinks: a link opened without a session leads to the sign-in @deeplinks @phone", async ({ browser, projectContextOptions, mcp, folder }) => {
  const path = `/Work/${folder}/Private ${folder}.md`;
  const link = await writeWithLink(mcp, path, "# Private\n\nNot for strangers.\n");
  const context = await browser.newContext({ ...projectContextOptions, storageState: { cookies: [], origins: [] } });
  try {
    const page = await context.newPage();
    await page.goto(link);
    const { pathname } = new URL(link);
    await expect(page).toHaveURL(`${new URL(link).origin}/signin?next=${encodeURIComponent(pathname)}`);
    // The sign-in page has rendered, so the absences below are of a page, not of a blank screen.
    await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "Private" })).toHaveCount(0);
    await expect(page.getByText("Not for strangers.")).toHaveCount(0);
  } finally {
    await context.close();
  }
});

test("deeplinks: a link into a vault Bob isn't a member of leads to the same Not here @deeplinks @phone", async ({ aliceApi, bobApi, bobPage }) => {
  const welcome = await vaultId(aliceApi, "Welcome");
  // A note that is there, so only the vault decides.
  const tree = await aliceApi.json<{ root: { notes: { path: string }[] } }>("GET", "/api/vaults/Welcome/tree");
  const note = tree.root.notes[0]?.path;
  expect(note, "a note at the top of Alice's Welcome").toBeDefined();
  const bobIds = (await bobApi.json<{ id: string }[]>("GET", "/api/vaults")).map((v) => v.id);
  expect(bobIds, "Welcome of Alice's isn't in Bob's list").not.toContain(welcome);

  await answerInstallHint(bobPage);
  await bobPage.goto(`/n/${welcome}/${note!.split("/").slice(2).map(encodeURIComponent).join("/")}`);
  await expect(bobPage.getByRole("heading", { name: "Not here", level: 1 })).toBeVisible();
  await expect(bobPage.getByText(notHere, { exact: true })).toBeVisible();
  await expect(bobPage.getByRole("button", { name: "Search for it" })).toBeVisible();
});

test.describe("deeplinks in a shared vault", () => {
  let share: Share;
  let alice: Api;
  /** Alice's agent, writing in the shared vault. */
  let aliceKey: Mcp;

  test.beforeAll(async () => {
    // Keys, invitations and deletions wait on the 5-a-minute password ledger, shared with other workers.
    test.setTimeout(4 * 60_000);
    share = await shareWithBob("Team", "write");
    alice = await Api.as("alice");
    aliceKey = await Mcp.create(alice, `e2e deeplinks ${randomBytes(3).toString("hex")}`, { [share.vault]: "write" });
  });

  test.afterAll(async () => {
    // Keys, invitations and deletions wait on the 5-a-minute password ledger, shared with other workers.
    test.setTimeout(4 * 60_000);
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
      await answerInstallHint(bobPage);
      await answerInstallHint(page);
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
