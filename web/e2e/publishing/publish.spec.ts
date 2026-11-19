// SPDX-License-Identifier: AGPL-3.0-or-later
// Publish 2.0 in the web app and on the reader page (docs/superpowers/specs/2026-11-18-publish-bundles-design.md;
// PR #235). The reader link is a secret: read into a variable and checked by shape, never in a message, a screenshot
// or a log; so no failure screenshots (the dialog shows the link once published), and the reader page's address is
// checked by booleans only.
//
// Promises in scope, and the test that covers each:
// 1. The Publish… dialog: the depth from 0 ("This note only") with its live count ("+N notes"), the list of what goes
//    (title and path) and what stays behind with the reason, the button "Publish N notes" (decisions 1, 2; "The flows")
//    → "publish: the dialog shows the depth, what goes and what stays …".
// 2. Unticking a note takes it out, and what is reached only through it stays behind with "only reached through a note
//    that stays out"; what was seen is what goes (decision 2) → the same test (it publishes after unticking and the
//    reader gets exactly the ticked notes).
// 3. The unticked note listed with "taken out by you" and a Take back in button that ticks it again (decision 2's
//    reasons) → "publish: an unticked note is listed as taken out by you".
// 4. A PDF a note embeds is listed as staying behind by its name, "a PDF (not published)" (decision 2) → "publish: a
//    PDF the bundle embeds is listed as staying behind".
// 5. From 50 notes the line under the count says "That's a lot of notes. Check the list before you publish."; a
//    warning, not a block (decision 1) → "publish: from 50 notes a warning shows …". The count beside the depth takes
//    the warning colour → "publish: from 50 notes the count beside the depth changes colour"; the line takes the
//    warning colour → "publish: from 50 notes the line under the list takes the warning colour".
// 6. What was seen is what goes: a note linked after the list showed makes the publish fail with the new list and
//    nothing goes out (decision 2, the 409) → "publish: a list that changed meanwhile is shown again …".
// 7. The reader: the start note first, Contents one tap away with the titles in walk order, a link to a heading landing
//    on it, each note its own address (the hash ends in /N), a reload staying on it, Back within the copy; an embed of a
//    heading shows that section in place; Copy as Markdown carries the bundle, each note under its heading (decisions
//    5, 6; "Embeds and links within a note") → the dialog test.
// 8. The reader never learns a note was left out: not in Contents, its link is plain text, none of its text on the page,
//    in the copy the reader API sent, or in Copy as Markdown (anti-goal 3) → the dialog test.
// 9. The bar above the notes: "Published with <start>" on a bundled note for the owner, "Published by the vault's owner"
//    for a member; the start note's bar counts the linked notes, turns yellow when the copy is older, and Update copy
//    shows the list again before anything goes out (decisions 4, 7; "Update copy … always shows the list first")
//    → "publish: the bar names the start note …".
// 10. A bundled note's bar turns yellow when the copy is older than the note (decision 7) → "publish: a bundled note's
//    bar turns yellow …" (F-08, fixed in 0.6.0).
// Left out: the limits of 500 notes and 64 MB (decision 9): not cheap to reach, covered by xUnit; marking and removing a
// note later, the activity entries (decision 10), the API's refusals and a member's prune: covered over MCP
// (e2e/mcp/publish.spec.ts) through the reader's API, the activity API and `sh`; Link for your AI with a bundle: over
// MCP too (the 0.5.0 suite, e2e/ailink, covers the reader page's button).
import type { Locator, Page, Response, TestInfo } from "@playwright/test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { Api, encodePath } from "../support/api";
import { baseURL, fixturesDir, secrets } from "../support/env";
import { reservePassword, reserveReaderRead } from "../support/session";
import { expect, onPhone, shot, test } from "../support/test";

test.use({ screenshot: "off" });

const readerLink = new RegExp(`^${baseURL.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}/s#\\S+$`);

/** A check on a secret: only the message, never the value, is printed when it fails. */
const holds = (ok: boolean, message: string) => expect(ok, message).toBe(true);

/** The notes of a bundle below `/Work/<folder>/`, named with the folder's random part, as links resolve vault-wide. */
function bundle(folder: string) {
  const id = folder.slice(-6);
  const name = (title: string) => `${title} ${id}`;
  return {
    name,
    path: (title: string) => `/Work/${folder}/${name(title)}.md`,
    link: (title: string, rest = "") => `[[${name(title)}${rest}]]`,
    /** A text only this note holds: the reader page must never show it once the note is out. */
    body: (title: string) => `body-of-${title.toLowerCase()}-${id}`,
  };
}

/** Opens a note by its address; on the phone the install hint is answered with Not now, as a person would. */
async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(`/notes/${encodePath(path)}`);
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** The file tree: docked on the desktop, a drawer on the phone (opened if it isn't). */
async function openTree(page: Page, testInfo: TestInfo): Promise<Locator> {
  if (!onPhone(testInfo)) return page.getByRole("complementary", { name: "File tree" });
  const drawer = page.getByRole("dialog", { name: "File tree" });
  if (!(await drawer.isVisible())) await page.getByRole("button", { name: "Open file tree" }).click();
  return drawer;
}

/** The note's row menu in the tree → Publish…: the dialog. */
async function openPublish(page: Page, testInfo: TestInfo, path: string, name: string) {
  await openNote(page, path);
  const tree = await openTree(page, testInfo);
  await tree.getByRole("button", { name: `Actions for ${name}`, exact: true }).click();
  await page.getByRole("menuitem", { name: "Publish…" }).click();
  const dialog = page.getByRole("dialog", { name: "Publish this note" });
  await expect(dialog).toBeVisible();
  return dialog;
}

const plus = (others: number) => `+${others === 1 ? "1 note" : `${others} notes`}`;

/** Sets the depth field and waits for the count beside it. */
async function setDepth(dialog: Locator, depth: number, others: number) {
  await dialog.getByLabel("How many links deep").fill(String(depth));
  await expect(dialog.getByText(plus(others), { exact: true })).toBeVisible();
}

/** The owner's password if the dialog asks for it (only when the session's window has closed: it counts against a limit). */
async function givePassword(dialog: Locator, submit: Locator) {
  const password = dialog.getByLabel("Your password", { exact: true });
  await expect(async () => {
    if (await password.isVisible()) {
      await reservePassword("alice");
      await password.fill(secrets().passwords.alice);
    }
    await expect(submit).toBeEnabled({ timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
}

const whatGoes = (dialog: Locator) => dialog.getByRole("group", { name: "What goes out" });
const staysBehind = (dialog: Locator) => dialog.getByRole("list", { name: "Stays behind" });
const box = (dialog: Locator, b: ReturnType<typeof bundle>, title: string) =>
  whatGoes(dialog).getByRole("checkbox", { name: `${b.name(title)} ${b.path(title)}`, exact: true });

/** The item of Stays behind that names `path`. */
const behindItem = (dialog: Locator, path: string) => staysBehind(dialog).getByRole("listitem").filter({ hasText: path });

/** One line of Stays behind: the path and its reason (the item's line, without a Take back in button), whatever else the list holds. */
async function staysWith(dialog: Locator, path: string, reason: string, message?: string) {
  await expect(behindItem(dialog, path).locator(":scope > span"), message).toHaveText(`${path}: ${reason}`);
}

/** Publishes over the API, as the dialog does, with the password (it counts against a limit). Nothing of the answer is kept. */
async function publishByApi(api: Api, start: string, depth: number, expected: string[]) {
  await reservePassword("alice");
  const response = await api.send("POST", `/api/publications/${encodePath(start)}`, {
    data: { password: secrets().passwords.alice, depth, expected: expected.map((p) => Api.inVault(p)) },
  });
  expect(response.status(), "published over the API").toBe(201);
}

/** Withdraws, lifts the marks and removes the notes and files, whatever the test got to. */
async function cleanUp(api: Api, start: string, marks: string[], paths: string[], files: string[] = []) {
  await api.send("DELETE", `/api/publications/${encodePath(start)}`).catch(() => undefined);
  for (const m of marks) await api.send("DELETE", `/api/unpublishable/${encodePath(m)}`).catch(() => undefined);
  for (const p of paths) await api.deleteNote(p).catch(() => undefined);
  for (const f of files) await api.send("DELETE", `/api/files/${encodePath(f)}`).catch(() => undefined);
}

/** Clean-ups a test registers before it writes: they run after it even when it timed out, which a finally doesn't. */
let cleanUps: (() => Promise<void>)[] = [];
const afterTest = (cleanUp: () => Promise<void>) => void cleanUps.push(cleanUp);
test.afterEach(async () => {
  for (const cleanUp of cleanUps.reverse()) await cleanUp();
  cleanUps = [];
});

test("publish: the dialog shows the depth, what goes and what stays; the reader moves around the copy @publish @phone", async ({
  page,
  aliceApi,
  folder,
  browser,
  projectContextOptions,
}, testInfo) => {
  const b = bundle(folder);
  // Start → Alpha (at its heading Details) → Deep; Start → Beta → Gamma; Start → Secret (marked) → Hidden; Start embeds
  // the section Part of Sections.
  const filler = Array.from({ length: 60 }, (_, i) => `Paragraph ${i + 1} before the details.`).join("\n\n");
  const notes: Record<string, string> = {
    Start: `# Start\n\nSee ${b.link("Alpha", "#Details|the details")}, ${b.link("Beta")} and ${b.link("Secret")}.\n\n!${b.link("Sections", "#Part")}\n`,
    Alpha: `# Alpha\n\n${filler}\n\n## Details\n\nThe details of Alpha. ${b.link("Deep")}\n`,
    Beta: `# Beta\n\n${b.body("Beta")} ${b.link("Gamma")}\n`,
    Secret: `# Secret\n\n${b.body("Secret")} ${b.link("Hidden")}\n`,
    Sections: `# Sections\n\n## Part\n\nPart text.\n\n## Other\n\nOther text.\n`,
    Deep: `# Deep\n\nThe deep note.\n`,
    Gamma: `# Gamma\n\n${b.body("Gamma")}\n`,
    Hidden: `# Hidden\n\n${b.body("Hidden")}\n`,
  };
  const left = ["Beta", "Secret", "Gamma", "Hidden"].map((t) => b.body(t));
  const paths = Object.keys(notes).map((t) => b.path(t));
  const reader = await browser.newContext({ ...projectContextOptions, permissions: ["clipboard-read", "clipboard-write"] });
  afterTest(async () => {
    await reader.close();
    await cleanUp(aliceApi, b.path("Start"), [b.path("Secret")], paths);
  });
  for (const [title, content] of Object.entries(notes)) await aliceApi.putNote(b.path(title), content);
  expect((await aliceApi.send("PUT", `/api/unpublishable/${encodePath(b.path("Secret"))}`)).status(), "Secret marked unpublishable").toBe(204);

  const dialog = await openPublish(page, testInfo, b.path("Start"), b.name("Start"));
  // Depth 0 is the default: this note only.
  await expect(dialog.getByLabel("How many links deep")).toHaveValue("0");
  await expect(dialog.getByText("This note only", { exact: true })).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Publish", exact: true })).toBeVisible();

  await setDepth(dialog, 2, 5);
  await expect(dialog.getByRole("status")).toHaveText("6 notes");
  await expect(whatGoes(dialog).getByRole("checkbox")).toHaveCount(6);
  for (const title of ["Start", "Alpha", "Beta", "Sections", "Deep", "Gamma"]) await expect(box(dialog, b, title)).toBeChecked();
  // The start note can't be taken out.
  await expect(box(dialog, b, "Start")).toBeDisabled();
  await staysWith(dialog, b.path("Hidden"), "only reached through a note that stays out");
  await staysWith(dialog, b.path("Secret"), "marked unpublishable");
  await expect(dialog.getByRole("button", { name: "Publish 6 notes", exact: true })).toBeVisible();

  // Unticking Beta takes Gamma with it.
  await box(dialog, b, "Beta").click();
  await expect(dialog.getByRole("status")).toHaveText("4 notes");
  await expect(dialog.getByText("+3 notes", { exact: true })).toBeVisible();
  // Beta is no box under What goes out but listed with what stays behind, each with its reason.
  await expect(whatGoes(dialog).getByRole("checkbox")).toHaveCount(4);
  await staysWith(dialog, b.path("Beta"), "taken out by you");
  await staysWith(dialog, b.path("Gamma"), "only reached through a note that stays out");
  await staysWith(dialog, b.path("Hidden"), "only reached through a note that stays out");
  await staysWith(dialog, b.path("Secret"), "marked unpublishable");
  await expect(staysBehind(dialog).getByRole("listitem")).toHaveCount(4);
  const submit = dialog.getByRole("button", { name: "Publish 4 notes", exact: true });
  await givePassword(dialog, submit);
  await shot(page, testInfo, "publish");

  await submit.click();
  // Not an assertion on the field itself: its call log would show the link.
  await expect(dialog.getByRole("button", { name: "Copy link" })).toBeVisible();
  const link = await dialog.getByLabel("Link", { exact: true }).inputValue();
  holds(readerLink.test(link), "the publish dialog shows a /s# link");
  await dialog.getByRole("button", { name: "Done" }).click();
  await expect(dialog).toHaveCount(0);

  // The reader: the start note first, Contents with the ticked notes in walk order, and nothing of the others.
  const view = await reader.newPage();
  // What the reader API sends the page, read without printing it.
  const sent: Promise<string>[] = [];
  view.on("response", (r: Response) => {
    if (new URL(r.url()).pathname === "/api/s" && r.request().method() === "POST") sent.push(r.text());
  });
  await reserveReaderRead();
  // A failed goto would print the address: said without it.
  await view.goto(link).catch(() => {
    throw new Error("the reader page did not open");
  });
  const title = view.getByRole("heading", { level: 1 });
  await expect(title).toHaveText("Start");
  await expect.poll(() => sent.length, { message: "the page read the copy once" }).toBe(1);
  const copy = await sent[0];
  holds(
    left.every((t) => !copy.includes(t)),
    "the copy the reader API sent holds no text of a note left out",
  );
  holds(copy.includes("The deep note."), "the copy the reader API sent holds Deep");

  await expect(view.getByRole("navigation", { name: "Contents" })).toHaveCount(0);
  await view.getByRole("button", { name: "Contents", exact: true }).click();
  const contents = view.getByRole("navigation", { name: "Contents" });
  await expect(contents.getByRole("link")).toHaveText(["Start", "Alpha", "Sections", "Deep"]);
  await view.getByRole("button", { name: "Contents", exact: true }).click();
  const article = view.getByRole("article");
  // The links to Beta and Secret are their text: no link, no marker.
  await expect(article).toContainText(`See the details, ${b.name("Beta")} and ${b.name("Secret")}.`);
  await expect(article.getByRole("link")).toHaveText(["the details"]);
  for (const text of left) await expect(view.locator("body")).not.toContainText(text);
  // The embedded heading shows its section in place, and only that.
  await expect(article.getByRole("heading", { name: "Part" })).toBeVisible();
  await expect(article).toContainText("Part text.");
  await expect(article).not.toContainText("Other text.");

  // Copy as Markdown carries the bundle: each note under its own heading, the left-out ones not at all.
  await view.getByRole("button", { name: "Copy as Markdown", exact: true }).click();
  await expect(view.getByText("Copied", { exact: true })).toBeVisible();
  const markdown = await view.evaluate(() => navigator.clipboard.readText());
  expect(markdown.startsWith("# Start\n\n"), "Copy as Markdown starts with the start note").toBe(true);
  for (const heading of ["# Alpha {#n1}", "# Sections {#n2}", "# Deep {#n3}"]) expect(markdown).toContain(`\n\n---\n\n${heading}\n\n`);
  expect(markdown).toContain("[the details](#n1)");
  expect(
    left.filter((t) => markdown.includes(t)),
    "texts of left-out notes in Copy as Markdown",
  ).toEqual([]);

  /** The note's own address: the hash ends in /N (booleans only: the address may hold the code). */
  const at = (n: number) => expect.poll(() => new URL(view.url()).hash.endsWith(`/${n}`), { message: `the address ends in /${n}` }).toBe(true);

  // A link to a heading lands on it, with the note's own address.
  await article.getByRole("link", { name: "the details" }).click();
  await expect(title).toHaveText("Alpha");
  await at(1);
  await expect(article.getByRole("heading", { name: "Details" })).toBeInViewport();
  await expect(title).not.toBeInViewport();

  // Contents lands at the top of a note.
  await view.getByRole("button", { name: "Contents", exact: true }).click();
  await contents.getByRole("link", { name: "Deep", exact: true }).click();
  await expect(title).toHaveText("Deep");
  await at(3);
  await expect(title).toBeInViewport();
  await expect(article).toContainText("The deep note.");

  // Back goes back within the copy, and a reload stays on the note.
  await view.goBack();
  await expect(title).toHaveText("Alpha");
  await at(1);
  await reserveReaderRead();
  await view.reload();
  await expect(title).toHaveText("Alpha");
  await at(1);
});

test("publish: the bar names the start note, a member learns only that it is published, and Update copy shows the list again @publish @phone", async ({
  page,
  bobPage,
  aliceApi,
  folder,
}) => {
  const b = bundle(folder);
  const paths = ["Start", "Alpha", "Gamma"].map((t) => b.path(t));
  afterTest(() => cleanUp(aliceApi, b.path("Start"), [], paths));
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${b.link("Alpha")}\n`);
  for (const title of ["Alpha", "Gamma"]) await aliceApi.putNote(b.path(title), `# ${title}\n`);
  await publishByApi(aliceApi, b.path("Start"), 1, [b.path("Start"), b.path("Alpha")]);
  const bar = (p: Page) => p.getByRole("region", { name: "Publication" });

  // A bundled note: the owner learns which start note it went out with, a member only that it is published.
  await openNote(page, b.path("Alpha"));
  await expect(bar(page)).toHaveText(`Published with ${b.name("Start")}`);
  await expect(bar(page).getByRole("link", { name: b.name("Start"), exact: true })).toBeVisible();
  await openNote(bobPage, b.path("Alpha"));
  await expect(bar(bobPage)).toHaveText("Published by the vault's owner");

  // The start note: up to date, with its linked note; a member, again, only that it is published.
  await openNote(page, b.path("Start"));
  await expect(bar(page).getByRole("status").first()).toHaveText("Published, up to date · with 1 linked note");
  await expect(bar(page)).toHaveAttribute("data-tone", "green");
  await openNote(bobPage, b.path("Start"));
  await expect(bar(bobPage)).toHaveText("Published by the vault's owner");

  // Changed since: yellow, and Update copy shows the list before anything goes out.
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${b.link("Alpha")} ${b.link("Gamma")}\n`);
  await openNote(page, b.path("Start"));
  await expect(bar(page)).toHaveAttribute("data-tone", "yellow");
  await expect(bar(page).getByRole("status").first()).toContainText("Published copy is older than this note.");
  await bar(page).getByRole("button", { name: "Update copy" }).click();
  const dialog = page.getByRole("dialog", { name: "Update the published copy" });
  await expect(dialog.getByRole("status")).toHaveText("3 notes");
  for (const title of ["Start", "Alpha", "Gamma"]) await expect(box(dialog, b, title)).toBeChecked();
  await dialog.getByRole("button", { name: "Update copy", exact: true }).click();
  await expect(dialog).toHaveCount(0);
  await expect(bar(page)).toHaveAttribute("data-tone", "green");
  await expect(bar(page).getByRole("status").first()).toHaveText("Published, up to date · with 2 linked notes");
});

test("publish: a list that changed meanwhile is shown again, and nothing goes out @publish @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const b = bundle(folder);
  const paths = ["Start", "Alpha", "Beta"].map((t) => b.path(t));
  afterTest(() => cleanUp(aliceApi, b.path("Start"), [], paths));
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${b.link("Alpha")}\n`);
  for (const title of ["Alpha", "Beta"]) await aliceApi.putNote(b.path(title), `# ${title}\n`);
  const dialog = await openPublish(page, testInfo, b.path("Start"), b.name("Start"));
  await setDepth(dialog, 1, 1);
  const submit = dialog.getByRole("button", { name: "Publish 2 notes", exact: true });
  await givePassword(dialog, submit);

  // A link added after the list showed.
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${b.link("Alpha")} ${b.link("Beta")}\n`);
  await submit.click();
  await expect(dialog.getByRole("alert").filter({ hasText: "Something changed in the vault: check the list again." })).toBeVisible();
  await expect(dialog.getByRole("status")).toHaveText("3 notes");
  await expect(box(dialog, b, "Beta")).toBeChecked();
  await expect(dialog.getByRole("button", { name: "Publish 3 notes", exact: true })).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Copy link" })).toHaveCount(0);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  expect((await aliceApi.send("GET", `/api/publications/${encodePath(b.path("Start"))}`)).status(), "nothing was published").toBe(404);
});

/** The start note and 49 linked notes: 50 at depth 1. */
async function fiftyNotes(api: Api, b: ReturnType<typeof bundle>) {
  const linked = Array.from({ length: 49 }, (_, i) => `N${String(i + 1).padStart(2, "0")}`);
  const paths = [b.path("Start"), ...linked.map((t) => b.path(t))];
  afterTest(() => cleanUp(api, b.path("Start"), [], paths));
  for (const t of linked) await api.putNote(b.path(t), `# ${t}\n`);
  await api.putNote(b.path("Start"), `# Start\n\n${linked.map((t) => b.link(t)).join("\n")}\n`);
}

const colourOf = (locator: Locator) => locator.evaluate((e) => getComputedStyle(e).color);

test("publish: from 50 notes a warning shows under the count, which doesn't block @publish @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const b = bundle(folder);
  await fiftyNotes(aliceApi, b);
  const dialog = await openPublish(page, testInfo, b.path("Start"), b.name("Start"));
  const warning = dialog.getByText("That's a lot of notes. Check the list before you publish.", { exact: true });
  const status = dialog.getByRole("status");

  await setDepth(dialog, 1, 49);
  await expect(status).toHaveText("50 notesThat's a lot of notes. Check the list before you publish.");
  await expect(warning).toBeVisible();
  const warned = await colourOf(status);
  // A warning, not a block.
  await expect(dialog.getByRole("button", { name: "Publish 50 notes", exact: true })).toBeEnabled();

  // One note fewer: no warning, and the count in its usual colour.
  await box(dialog, b, "N49").click();
  await expect(status).toHaveText("49 notes");
  await expect(warning).toHaveCount(0);
  expect(await colourOf(status), "the count's colour below 50 differs from the warning's").not.toBe(warned);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toHaveCount(0);
});

/** The count beside the depth at 50 notes and at 49, the colour of the line under the list at 50, and the theme's warning colour. */
async function colours(page: Page, testInfo: TestInfo, aliceApi: Api, folder: string) {
  const b = bundle(folder);
  await fiftyNotes(aliceApi, b);
  const dialog = await openPublish(page, testInfo, b.path("Start"), b.name("Start"));
  const count = (others: number) => dialog.getByText(plus(others), { exact: true });
  await setDepth(dialog, 1, 49);
  const at50 = await colourOf(count(49));
  const line = await colourOf(dialog.getByText("That's a lot of notes. Check the list before you publish.", { exact: true }));
  // The theme's warning token, as the dialog's own CSS resolves it.
  const warning = await dialog.evaluate((d) => {
    const probe = d.appendChild(document.createElement("span"));
    probe.style.color = "var(--warning)";
    const colour = getComputedStyle(probe).color;
    probe.remove();
    return colour;
  });
  await box(dialog, b, "N49").click();
  await expect(count(48)).toBeVisible();
  return { at50, at49: await colourOf(count(48)), line, warning };
}

test("publish: from 50 notes the count beside the depth changes colour @publish @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const { at50, at49 } = await colours(page, testInfo, aliceApi, folder);
  expect(at50 !== at49, "the count beside the depth changes colour from 50 notes").toBe(true);
});

test("publish: from 50 notes the line under the list takes the warning colour @publish @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const { line, warning } = await colours(page, testInfo, aliceApi, folder);
  expect(line, "the line under the list is the theme's warning colour").toBe(warning);
});

test("publish: an unticked note is listed as taken out by you @publish @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const b = bundle(folder);
  afterTest(() => cleanUp(aliceApi, b.path("Start"), [], [b.path("Start"), b.path("Beta")]));
  await aliceApi.putNote(b.path("Beta"), "# Beta\n");
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${b.link("Beta")}\n`);
  const dialog = await openPublish(page, testInfo, b.path("Start"), b.name("Start"));
  await setDepth(dialog, 1, 1);
  await box(dialog, b, "Beta").click();
  await expect(dialog.getByRole("status")).toHaveText("1 note");
  await expect(whatGoes(dialog).getByRole("checkbox")).toHaveCount(1);
  await expect(staysBehind(dialog).getByRole("listitem")).toHaveCount(1);
  await staysWith(dialog, b.path("Beta"), "taken out by you", "the unticked note is listed with its reason");

  // Take back in ticks it again: back under What goes out, and nothing stays behind.
  await behindItem(dialog, b.path("Beta")).getByRole("button", { name: "Take back in", exact: true }).click();
  await expect(dialog.getByRole("status")).toHaveText("2 notes");
  await expect(box(dialog, b, "Beta")).toBeChecked();
  await expect(staysBehind(dialog)).toHaveCount(0);
});

test("publish: a PDF the bundle embeds is listed as staying behind @publish @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const b = bundle(folder);
  const pdf = `/Work/${folder}/manual-${folder.slice(-6)}.pdf`;
  afterTest(() => cleanUp(aliceApi, b.path("Start"), [], [b.path("Start"), b.path("Alpha")], [pdf]));
  await aliceApi.putFile(pdf, readFileSync(join(fixturesDir, "tags", "recent", "Pump", "manual.pdf")));
  await aliceApi.putNote(b.path("Alpha"), "# Alpha\n");
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${b.link("Alpha")}\n\n![[${pdf.split("/").pop()}]]\n`);
  const dialog = await openPublish(page, testInfo, b.path("Start"), b.name("Start"));
  await setDepth(dialog, 1, 1);
  await expect(dialog.getByRole("status")).toHaveText("2 notes");
  // The preview names an embedded file by its name, not its path.
  await staysWith(dialog, pdf.split("/").pop()!, "a PDF (not published)", "the PDF is listed as staying behind");
});

test("publish: a bundled note's bar turns yellow when the copy is older than the note @publish @phone", async ({ page, aliceApi, folder }) => {
  const b = bundle(folder);
  afterTest(() => cleanUp(aliceApi, b.path("Start"), [], [b.path("Start"), b.path("Alpha")]));
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${b.link("Alpha")}\n`);
  await aliceApi.putNote(b.path("Alpha"), "# Alpha\n");
  await publishByApi(aliceApi, b.path("Start"), 1, [b.path("Start"), b.path("Alpha")]);
  await aliceApi.putNote(b.path("Alpha"), "# Alpha\n\nChanged after the copy.\n");
  await openNote(page, b.path("Alpha"));
  const bar = page.getByRole("region", { name: "Publication" });
  await expect(bar).toContainText(`Published with ${b.name("Start")}`);
  await expect(bar, "F-08: the bundled note's bar is yellow").toHaveAttribute("data-tone", "yellow", { timeout: 3_000 });
});
