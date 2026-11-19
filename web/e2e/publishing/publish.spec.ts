// SPDX-License-Identifier: AGPL-3.0-or-later
// Publish 2.0 in the web app and on the reader page (docs/superpowers/specs/2026-11-18-publish-bundles-design.md;
// PR #235). The reader link is a secret: read into a variable and checked by shape, never in a message, a screenshot
// or a log; so no failure screenshots (the dialog shows the link once published).
//
// Promises in scope, and the test that covers each:
// 1. The Publish… dialog: the depth from 0 ("This note only") with its live count ("+N notes"), the list of what goes
//    (title and path) and what stays behind with the reason, the button "Publish N notes" (decisions 1, 2; "The flows")
//    → "publish: the dialog shows the depth, what goes and what stays …".
// 2. Unticking a note takes it out, and what is reached only through it stays behind with "only reached through a note
//    that stays out"; what was seen is what goes (decision 2) → the same test (it publishes after unticking and the
//    reader gets exactly the ticked notes).
// 3. The unticked note listed with "taken out by you" (decision 2's list of reasons) → "publish: an unticked note is
//    listed as taken out by you" (finding F-05).
// 4. From 50 notes on the count turns red with "That's a lot of notes. Check the list before you publish."; a warning,
//    not a block (decision 1) → "publish: from 50 notes the count turns red …".
// 5. The reader: the start note first, Contents one tap away with the titles in walk order, a link to a heading landing
//    on it, each note its own address after the code (`#/N`), Back within the copy (decisions 5, 6) → the dialog test.
// 6. The reader never learns a note was left out: not in Contents, its link is plain text, none of its text on the page
//    (anti-goal 3) → the dialog test.
// Left out: the bar above the notes (decision 7) and Update copy showing the list again (decision 4): not in this
// task's scope; the limits of 500 notes and 64 MB (decision 9): not cheap to reach, covered by xUnit; marking and
// removing a note later: covered over MCP (e2e/mcp/publish.spec.ts), reading the reader's API.
import type { Locator, Page, TestInfo } from "@playwright/test";
import { encodePath, type Api } from "../support/api";
import { secrets } from "../support/env";
import { reservePassword, reserveReaderRead } from "../support/session";
import { expect, onPhone, shot, test } from "../support/test";

test.use({ screenshot: "off" });

const readerLink = /^http:\/\/localhost:8080\/s#\S+$/;

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

/** Sets the depth field and waits for the count beside it. */
async function setDepth(dialog: Locator, depth: number, others: number) {
  await dialog.getByLabel("How many links deep").fill(String(depth));
  await expect(
    dialog.getByText(`+${others === 1 ? "1 note" : `${others} notes`}`, {
      exact: true,
    }),
  ).toBeVisible();
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

/** Withdraws, lifts the marks and removes the notes, whatever the test got to. */
async function cleanUp(api: Api, start: string, marks: string[], paths: string[]) {
  await api.send("DELETE", `/api/publications/${encodePath(start)}`).catch(() => undefined);
  for (const m of marks) await api.send("DELETE", `/api/unpublishable/${encodePath(m)}`).catch(() => undefined);
  for (const p of paths) await api.deleteNote(p).catch(() => undefined);
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
  // Start → Alpha (at its heading Details) → Deep; Start → Beta → Gamma; Start → Secret (marked) → Hidden.
  const filler = Array.from({ length: 60 }, (_, i) => `Paragraph ${i + 1} before the details.`).join("\n\n");
  const notes: Record<string, string> = {
    Start: `# Start\n\nSee ${b.link("Alpha", "#Details|the details")}, ${b.link("Beta")} and ${b.link("Secret")}.\n`,
    Alpha: `# Alpha\n\n${filler}\n\n## Details\n\nThe details of Alpha. ${b.link("Deep")}\n`,
    Beta: `# Beta\n\n${b.body("Beta")} ${b.link("Gamma")}\n`,
    Secret: `# Secret\n\n${b.body("Secret")} ${b.link("Hidden")}\n`,
    Deep: `# Deep\n\nThe deep note.\n`,
    Gamma: `# Gamma\n\n${b.body("Gamma")}\n`,
    Hidden: `# Hidden\n\n${b.body("Hidden")}\n`,
  };
  const paths = Object.keys(notes).map((t) => b.path(t));
  const reader = await browser.newContext(projectContextOptions);
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

  await setDepth(dialog, 2, 4);
  await expect(dialog.getByRole("status")).toHaveText("5 notes");
  await expect(whatGoes(dialog).getByRole("checkbox")).toHaveCount(5);
  for (const title of ["Start", "Alpha", "Beta", "Deep", "Gamma"]) {
    await expect(
      whatGoes(dialog).getByRole("checkbox", {
        name: `${b.name(title)} ${b.path(title)}`,
        exact: true,
      }),
    ).toBeChecked();
  }
  // The start note can't be taken out.
  await expect(
    whatGoes(dialog).getByRole("checkbox", {
      name: `${b.name("Start")} ${b.path("Start")}`,
      exact: true,
    }),
  ).toBeDisabled();
  await expect(staysBehind(dialog).getByRole("listitem")).toHaveText([
    `${b.path("Hidden")}: only reached through a note that stays out`,
    `${b.path("Secret")}: marked unpublishable`,
  ]);
  await expect(dialog.getByRole("button", { name: "Publish 5 notes", exact: true })).toBeVisible();

  // Unticking Beta takes Gamma with it.
  await whatGoes(dialog)
    .getByRole("checkbox", {
      name: `${b.name("Beta")} ${b.path("Beta")}`,
      exact: true,
    })
    .click();
  await expect(dialog.getByRole("status")).toHaveText("3 notes");
  await expect(dialog.getByText("+2 notes", { exact: true })).toBeVisible();
  await expect(whatGoes(dialog).getByRole("checkbox", { checked: true })).toHaveCount(3);
  await expect(
    whatGoes(dialog).getByRole("checkbox", {
      name: b.path("Beta"),
      exact: true,
    }),
  ).not.toBeChecked();
  await expect(staysBehind(dialog).getByRole("listitem")).toHaveText([
    `${b.path("Gamma")}: only reached through a note that stays out`,
    `${b.path("Hidden")}: only reached through a note that stays out`,
    `${b.path("Secret")}: marked unpublishable`,
  ]);
  const submit = dialog.getByRole("button", {
    name: "Publish 3 notes",
    exact: true,
  });
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
  await reserveReaderRead();
  // A failed goto would print the address: said without it.
  await view.goto(link).catch(() => {
    throw new Error("the reader page did not open");
  });
  const title = view.getByRole("heading", { level: 1 });
  await expect(title).toHaveText("Start");
  await expect(view.getByRole("navigation", { name: "Contents" })).toHaveCount(0);
  await view.getByRole("button", { name: "Contents", exact: true }).click();
  const contents = view.getByRole("navigation", { name: "Contents" });
  await expect(contents.getByRole("link")).toHaveText(["Start", "Alpha", "Deep"]);
  await view.getByRole("button", { name: "Contents", exact: true }).click();
  const article = view.getByRole("article");
  // The links to Beta and Secret are their text: no link, no marker.
  await expect(article).toContainText(`See the details, ${b.name("Beta")} and ${b.name("Secret")}.`);
  await expect(article.getByRole("link")).toHaveText(["the details"]);
  for (const left of ["Beta", "Secret", "Gamma", "Hidden"]) await expect(view.locator("body")).not.toContainText(b.body(left));

  // A link to a heading lands on it, with the note's own address after the code.
  await article.getByRole("link", { name: "the details" }).click();
  await expect(title).toHaveText("Alpha");
  await expect(view).toHaveURL(/\/s#\/1$/);
  await expect(article.getByRole("heading", { name: "Details" })).toBeInViewport();
  await expect(title).not.toBeInViewport();

  // Contents lands at the top of a note.
  await view.getByRole("button", { name: "Contents", exact: true }).click();
  await contents.getByRole("link", { name: "Deep", exact: true }).click();
  await expect(title).toHaveText("Deep");
  await expect(view).toHaveURL(/\/s#\/2$/);
  await expect(title).toBeInViewport();
  await expect(article).toContainText("The deep note.");

  // Back goes back within the copy.
  await view.goBack();
  await expect(title).toHaveText("Alpha");
  await expect(view).toHaveURL(/\/s#\/1$/);
});

test("publish: from 50 notes the count turns red with a warning, which doesn't block @publish @phone", async ({ page, aliceApi, folder }, testInfo) => {
  const b = bundle(folder);
  // The start note and 49 linked notes: 50 at depth 1.
  const linked = Array.from({ length: 49 }, (_, i) => `N${String(i + 1).padStart(2, "0")}`);
  const paths = [b.path("Start"), ...linked.map((t) => b.path(t))];
  afterTest(() => cleanUp(aliceApi, b.path("Start"), [], paths));
  for (const t of linked) await aliceApi.putNote(b.path(t), `# ${t}\n`);
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${linked.map((t) => b.link(t)).join("\n")}\n`);
  const dialog = await openPublish(page, testInfo, b.path("Start"), b.name("Start"));
  const warning = dialog.getByText("That's a lot of notes. Check the list before you publish.", { exact: true });
  const status = dialog.getByRole("status");
  const colour = () => status.evaluate((e) => getComputedStyle(e).color);

  await setDepth(dialog, 1, 49);
  await expect(status).toHaveText("50 notesThat's a lot of notes. Check the list before you publish.");
  await expect(warning).toBeVisible();
  const red = await colour();
  // A warning, not a block.
  await expect(dialog.getByRole("button", { name: "Publish 50 notes", exact: true })).toBeEnabled();

  // One note fewer: no warning, and the count in its usual colour.
  await whatGoes(dialog)
    .getByRole("checkbox", {
      name: `${b.name("N49")} ${b.path("N49")}`,
      exact: true,
    })
    .click();
  await expect(status).toHaveText("49 notes");
  await expect(warning).toHaveCount(0);
  expect(await colour(), "the count's colour below 50 differs from the warning's").not.toBe(red);
  await dialog.getByRole("button", { name: "Cancel" }).click();
  await expect(dialog).toHaveCount(0);
});

test("publish: an unticked note is listed as taken out by you @publish @phone", async ({ page, aliceApi, folder }, testInfo) => {
  test.fail(true, 'F-05 (Low, unclear): an unticked note shows as an empty box under What goes out, never with the reason "taken out by you" the spec lists');
  const b = bundle(folder);
  afterTest(() => cleanUp(aliceApi, b.path("Start"), [], [b.path("Start"), b.path("Beta")]));
  await aliceApi.putNote(b.path("Beta"), "# Beta\n");
  await aliceApi.putNote(b.path("Start"), `# Start\n\n${b.link("Beta")}\n`);
  const dialog = await openPublish(page, testInfo, b.path("Start"), b.name("Start"));
  await setDepth(dialog, 1, 1);
  await whatGoes(dialog)
    .getByRole("checkbox", {
      name: `${b.name("Beta")} ${b.path("Beta")}`,
      exact: true,
    })
    .click();
  await expect(dialog.getByRole("status")).toHaveText("1 note");
  await expect(staysBehind(dialog).getByRole("listitem"), "F-05: the unticked note is listed with its reason").toHaveText(
    [`${b.path("Beta")}: taken out by you`],
    { timeout: 3_000 },
  );
});
