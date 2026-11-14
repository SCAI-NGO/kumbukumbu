// SPDX-License-Identifier: AGPL-3.0-or-later
// The AI link (the AI link report's hand-test checklist, steps 1-10 and 14; spec 2026-11-11-ai-link-design.md).
// "Bob" is a reader without an account. The publication's `#` code and the AI link's token are secrets: they are read
// into variables and checked by shape, with no value in an assertion message, a screenshot or a log; so no failure
// screenshots either (the dialog shows the link), and no assertion that would print the received value on a failure.
import { request, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type BrowserContextOptions, type Locator, type Page, type TestInfo } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { Api, encodePath } from "../support/api";
import { baseURL, fixturesDir, secrets, stackDir } from "../support/env";
import { reservePassword, reserveReaderRead } from "../support/session";
import { expect, onPhone, shot, test } from "../support/test";
import { nowTime } from "../support/time";

test.use({ screenshot: "off" });

const note = (folder: string) => `/Work/${folder}/Pump/Ruling.md`;
const files = ["Pump/Ruling.md", "Pump/one.png", "Pump/two.png"];
const gone = "This link for AIs has expired or the note is no longer published. Ask for a new one on the note's page.";
const readerLink = /^http:\/\/localhost:8080\/s#\S+$/;
const aiLinkShape = /^http:\/\/localhost:8080\/s\/t\/[A-Za-z0-9_-]{32}$/;

/** A check on a secret: only the message, never the value, is printed when it fails. */
const holds = (ok: boolean, message: string) => expect(ok, message).toBe(true);

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

/** The open note's row menu in the tree → Publish…, the owner's password if asked → the link, then Done. */
async function publishViaUi(page: Page, testInfo: TestInfo, path: string): Promise<string> {
  await openNote(page, path);
  if (onPhone(testInfo)) {
    // The phone's Publish… is in the note's own menu.
    await page.getByRole("button", { name: "Note actions" }).click();
    await page.getByRole("menuitem", { name: "Publish…" }).click();
  } else {
    // F-02: the menu closes while the tree is still scrolling to the open note (any scroll closes it): opened again if so.
    await expect(async () => {
      await page.keyboard.press("Escape");
      const tree = await openTree(page, testInfo);
      await tree.getByRole("button", { name: "Actions for Ruling", exact: true }).click({ timeout: 2_000 });
      await page.getByRole("menuitem", { name: "Publish…" }).click({ timeout: 2_000 });
    }).toPass({ timeout: 15_000 });
  }
  const dialog = page.getByRole("dialog", { name: "Publish this note" });
  const submit = dialog.getByRole("button", { name: "Publish", exact: true });
  // The password only when the session's window has closed (it counts against a rate limit).
  const password = dialog.getByLabel("Your password", { exact: true });
  await expect(async () => {
    if (await password.isVisible()) {
      await reservePassword("alice");
      await password.fill(secrets().passwords.alice);
    }
    await expect(submit).toBeEnabled({ timeout: 1_000 });
  }).toPass({ timeout: 10_000 });
  await submit.click();
  // Not an assertion on the field itself: its call log would show the link.
  await expect(dialog.getByRole("button", { name: "Copy link" })).toBeVisible();
  const field = dialog.getByLabel("Link", { exact: true });
  const link = await field.inputValue();
  holds(readerLink.test(link), "the publish dialog shows a /s# link");
  return link;
}

/** Alice withdraws the note through the bar above it. */
async function unpublishViaUi(page: Page) {
  const bar = page.getByRole("region", { name: "Publication" });
  await bar.getByRole("button", { name: "Unpublish" }).click();
  await page.getByRole("alertdialog").getByRole("button", { name: "Unpublish" }).click();
  await expect(bar).toHaveCount(0);
}

/** Bob's page: a context without a session, with the clipboard allowed, on the reader link. */
async function readAs(context: BrowserContext, link: string) {
  const reader = await context.newPage();
  await reserveReaderRead();
  // A failed goto would print the address: said without it.
  await reader.goto(link).catch(() => {
    throw new Error("the reader page did not open");
  });
  await expect(reader.getByRole("heading", { name: "Ruling", level: 1 })).toBeVisible();
  return reader;
}

const aiButton = (reader: Page) => reader.getByRole("button", { name: "Link for your AI", exact: true });
const aiField = (reader: Page) => reader.getByRole("textbox", { name: "Link for your AI" });

/** Presses Link for your AI and returns the link it shows. */
async function makeAiLink(reader: Page, previous?: string) {
  await reserveReaderRead();
  await aiButton(reader).click();
  const field = aiField(reader);
  await expect(reader.getByText(/^Valid until \d\d:\d\d\./)).toBeVisible();
  if (previous !== undefined) await expect.poll(async () => (await field.inputValue()) !== previous, { message: "a second press made a different link" }).toBe(true);
  const link = await field.inputValue();
  holds(aiLinkShape.test(link), "the AI link has the shape /s/t/ and 32 characters");
  return link;
}

/** An anonymous caller whose failures say nothing of the address (a call log would print the token). */
function anonymous(context: APIRequestContext) {
  const call = (method: "get" | "post" | "head") => async (url: string) => {
    await reserveReaderRead();
    try {
      return await context[method](url);
    } catch {
      throw new Error(`an anonymous ${method.toUpperCase()} of the AI link path failed`);
    }
  };
  return { get: call("get"), post: call("post"), head: call("head"), dispose: () => context.dispose() };
}
type Anon = ReturnType<typeof anonymous>;

/** The fixture's notes, Alice's publication, Bob's page; everything removed again, on failure too. */
async function withPublished(
  { page, aliceApi, seedAs, folder, browser, projectContextOptions }: {
    page: Page;
    aliceApi: Api;
    seedAs: (from: string, to: string) => Promise<string[]>;
    folder: string;
    browser: Browser;
    projectContextOptions: BrowserContextOptions;
  },
  testInfo: TestInfo,
  run: (published: { link: string; context: BrowserContext; anon: Anon }) => Promise<void>,
) {
  const context = await browser.newContext({ ...projectContextOptions, permissions: ["clipboard-read", "clipboard-write"] });
  const anon = anonymous(await request.newContext({ baseURL }));
  try {
    for (const name of files) await seedAs(`tags/ailink/${name}`, `/Work/${folder}/${name}`);
    const link = await publishViaUi(page, testInfo, note(folder));
    await page.getByRole("dialog", { name: "Publish this note" }).getByRole("button", { name: "Done" }).click();
    await expect(page.getByRole("dialog", { name: "Publish this note" })).toHaveCount(0);
    await run({ link, context, anon });
  } finally {
    // Withdrawn even when the test failed (a withdrawal that was made already answers 404: no matter).
    await aliceApi.send("DELETE", `/api/publications/${encodePath(note(folder))}`).catch(() => undefined);
    await anon.dispose();
    await context.close();
    for (const name of files) await aliceApi.deleteNote(`/Work/${folder}/${name}`).catch(() => undefined);
  }
}

async function text(response: APIResponse) {
  return (await response.body()).toString("utf8");
}

test("ailink #1: the publish dialog shows a /s# link and nothing about AI links @ailink", async ({ page, aliceApi, seedAs, folder }, testInfo) => {
  try {
    for (const name of files) await seedAs(`tags/ailink/${name}`, `/Work/${folder}/${name}`);
    const link = await publishViaUi(page, testInfo, note(folder));
    holds(readerLink.test(link), "the link is an http://localhost:8080/s# link");
    const dialog = page.getByRole("dialog", { name: "Publish this note" });
    await expect(dialog.getByRole("button", { name: "Copy link" })).toBeVisible();
    await expect(dialog.getByText(/\bAI\b/)).toHaveCount(0);
    await expect(dialog.getByRole("button", { name: /AI/ })).toHaveCount(0);
    await expect(dialog.getByRole("textbox")).toHaveCount(1);
    await dialog.getByRole("button", { name: "Done" }).click();
    await expect(dialog).toHaveCount(0);
  } finally {
    await aliceApi.send("DELETE", `/api/publications/${encodePath(note(folder))}`).catch(() => undefined);
    for (const name of files) await aliceApi.deleteNote(`/Work/${folder}/${name}`).catch(() => undefined);
  }
});

test("ailink #2: the reader page offers Copy as Markdown and Link for your AI @ailink", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context }) => {
    const reader = await readAs(context, link);
    const markdown = reader.getByRole("button", { name: "Copy as Markdown", exact: true });
    await expect(markdown).toBeVisible();
    await expect(aiButton(reader)).toBeVisible();
    // Beside each other: on one line.
    const [a, b] = [await markdown.boundingBox(), await aiButton(reader).boundingBox()];
    holds(!!a && !!b && Math.abs(a.y - b.y) < 4 && a.x + a.width <= b.x, "Link for your AI sits beside Copy as Markdown");
    // The note shows, with its two images.
    await expect(reader.getByText("The committee ruled that the pump stays in service.")).toBeVisible();
    const images = reader.getByRole("article").getByRole("img");
    await expect(images).toHaveCount(2);
    for (const image of await images.all()) await expect.poll(() => image.evaluate((i: HTMLImageElement) => i.complete && i.naturalWidth)).toBeGreaterThan(0);
    // No link is shown before the button is pressed.
    await expect(aiField(reader)).toHaveCount(0);
    await shot(reader, testInfo, "ailink");
  });
});

test("ailink #3: Link for your AI shows the link, Copy, the time an hour on, and the sentence @ailink", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context }) => {
    const reader = await readAs(context, link);
    const hour = () => nowTime(new Date(Date.now() + 3_600_000));
    const before = hour();
    await makeAiLink(reader);
    const after = hour();
    holds(await aiField(reader).evaluate((i) => (i as HTMLInputElement).readOnly), "the field is read-only");
    await expect(reader.getByRole("button", { name: "Copy", exact: true })).toBeVisible();
    const sentence = reader.getByText(/^Valid until \d\d:\d\d\./);
    await expect(sentence).toBeVisible();
    const said = (await sentence.textContent()) ?? "";
    const match = /^Valid until (\d\d:\d\d)\. Anyone with this link can read this note until then: it is for an AI to fetch, not to post in a chat\.$/.exec(said);
    expect(match, "the sentence: Valid until HH:MM, anyone with the link, not to post in a chat").not.toBeNull();
    // An hour on, in Bob's (Vienna) time; the minute may have ticked.
    expect([before, after], "Valid until is an hour on").toContain(match![1]);
  });
});

test("ailink #4: Copy says Copied and the clipboard holds the link @ailink", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context }) => {
    const reader = await readAs(context, link);
    const ai = await makeAiLink(reader);
    await reader.getByRole("button", { name: "Copy", exact: true }).click();
    await expect(reader.getByText("Copied", { exact: true })).toBeVisible();
    const clipboard = await reader.evaluate(() => navigator.clipboard.readText());
    holds(clipboard === ai, "the clipboard holds the AI link");
  });
});

test("ailink #5: a second press makes a different link; the first still works @ailink", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context, anon }) => {
    const reader = await readAs(context, link);
    const first = await makeAiLink(reader);
    const second = await makeAiLink(reader, first);
    holds(first !== second, "the second link differs from the first");
    for (const [which, ai] of [["first", first], ["second", second]] as const) {
      const answer = await anon.get(ai);
      expect(answer.status(), `the ${which} link answers`).toBe(200);
    }
  });
});

test("ailink #6: at 375 px the field takes the line, Copy below, no sideways scroll @ailink @phone", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  test.skip(!onPhone(testInfo), "The phone width only.");
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context }) => {
    const reader = await readAs(context, link);
    await shot(reader, testInfo, "ailink");
    await makeAiLink(reader);
    const field = aiField(reader);
    const copy = reader.getByRole("button", { name: "Copy", exact: true });
    const [f, c, row] = [await field.boundingBox(), await copy.boundingBox(), await reader.getByRole("article").boundingBox()];
    holds(!!f && !!c && !!row, "field, Copy and their row are laid out");
    // The field spans its row; Copy starts below its bottom edge.
    holds(f!.width >= row!.width - 1, "the field takes the article's whole width");
    holds(c!.y >= f!.y + f!.height - 1, "Copy sits below the field");
    holds(await reader.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), "no sideways scroll");
  });
});

test("ailink #7: the link answers Markdown with the right headers and body @ailink", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context, anon }) => {
    const reader = await readAs(context, link);
    const ai = await makeAiLink(reader);
    const answer = await anon.get(ai);
    expect(answer.status(), "status").toBe(200);
    const headers = answer.headers();
    expect(headers["content-type"], "Content-Type").toBe("text/markdown; charset=utf-8");
    expect(headers["x-robots-tag"], "X-Robots-Tag").toBe("noindex, nofollow");
    expect(headers["referrer-policy"], "Referrer-Policy").toBe("no-referrer");
    expect(headers["cache-control"], "Cache-Control").toBe("no-store");
    expect(headers["set-cookie"], "no Set-Cookie").toBeUndefined();

    const lines = (await text(answer)).split("\n");
    expect(lines[0], "the body begins with the title").toBe("# Ruling");
    expect(lines[2], "then what this is").toMatch(/^A published note, snapshot of \d{4}-\d\d-\d\d \d\d:\d\d UTC; links to other notes are plain text\.$/);
    const token = ai.split("/").pop()!;
    const images = lines.filter((l) => l.startsWith("!["));
    expect(images.length, "two images").toBe(2);
    images.forEach((line, n) => {
      const shape = new RegExp(`^!\\[[^\\]]*\\]\\(${baseURL.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}/s/t/${token}/files/${n}\\) \\(image/png\\)$`);
      holds(shape.test(line), `image ${n} points to /files/${n} with its type`);
    });
  });
});

test("ailink #8: files/0 is the image, files/9 the plain-text 404 @ailink", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context, anon }) => {
    const reader = await readAs(context, link);
    const ai = await makeAiLink(reader);
    for (const [n, name] of [[0, "one.png"], [1, "two.png"]] as const) {
      const image = await anon.get(`${ai}/files/${n}`);
      expect(image.status(), `files/${n} status`).toBe(200);
      expect(image.headers()["content-type"], `files/${n} type`).toBe("image/png");
      holds((await image.body()).equals(readFileSync(join(fixturesDir, "tags", "ailink", "Pump", name))), `files/${n} is ${name}`);
    }
    const missing = await anon.get(`${ai}/files/9`);
    expect(missing.status(), "files/9 status").toBe(404);
    expect(missing.headers()["content-type"], "files/9 type").toBe("text/plain; charset=utf-8");
    expect(await text(missing), "files/9 says").toBe(gone);
  });
});

test("ailink #9: POST and other paths get the plain-text 404, HEAD 200 without a body @ailink", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context, anon }) => {
    const reader = await readAs(context, link);
    const ai = await makeAiLink(reader);
    const post = await anon.post(ai);
    expect(post.status(), "POST status").toBe(404);
    expect(post.headers()["content-type"], "POST type").toBe("text/plain; charset=utf-8");
    expect(await text(post), "POST says").toBe(gone);

    const other = await anon.get("/s/t/whatever/else");
    expect(other.status(), "other path status").toBe(404);
    expect(other.headers()["content-type"], "other path type").toBe("text/plain; charset=utf-8");
    expect(await text(other), "other path says (never the web app's HTML)").toBe(gone);

    const head = await anon.head(ai);
    expect(head.status(), "HEAD status").toBe(200);
    expect(head.headers()["content-type"], "HEAD type").toBe("text/markdown; charset=utf-8");
    expect((await head.body()).length, "HEAD has no body").toBe(0);
  });
});

test("ailink #10: after withdrawing, the link and its files give the 404; the reader page says withdrawn @ailink", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context, anon }) => {
    const reader = await readAs(context, link);
    const ai = await makeAiLink(reader);
    expect((await anon.get(ai)).status(), "the link answers before").toBe(200);

    await unpublishViaUi(page);

    for (const url of [ai, `${ai}/files/0`]) {
      const answer = await anon.get(url);
      expect(answer.status(), "status after withdrawing").toBe(404);
      expect(answer.headers()["content-type"], "type after withdrawing").toBe("text/plain; charset=utf-8");
      expect(await text(answer), "says after withdrawing").toBe(gone);
    }
    // Bob's page answers as for a withdrawn note: a new link can't be made, and a fresh visit of the link is refused.
    await reserveReaderRead();
    await aiButton(reader).click();
    await expect(reader.getByText("The link couldn't be made: open the note's link again.")).toBeVisible();
    const again = await context.newPage();
    await reserveReaderRead();
    await again.goto(link).catch(() => {
      throw new Error("the reader page did not open");
    });
    await expect(again.getByText("This code doesn't open a published note.")).toBeVisible();
  });
});

test("ailink #14: the server's log has no /s/t/ and no token @ailink @serial", async ({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo) => {
  await withPublished({ page, aliceApi, seedAs, folder, browser, projectContextOptions }, testInfo, async ({ link, context, anon }) => {
    const reader = await readAs(context, link);
    const tokens = [await makeAiLink(reader)];
    tokens.push(await makeAiLink(reader, tokens[0]));
    // Every kind of answer the path gives, so that it has had its chance to be logged.
    for (const ai of tokens) {
      expect((await anon.get(ai)).status(), "the link answers").toBe(200);
      await anon.get(`${ai}/files/0`);
      await anon.get(`${ai}/files/9`);
      await anon.head(ai);
      await anon.post(ai);
    }
    await anon.get("/s/t/whatever/else");
    await unpublishViaUi(page);
    expect((await anon.get(tokens[0])).status(), "the link is gone after withdrawing").toBe(404);

    // The error of a failed run carries the log: said without it.
    let log: string;
    try {
      log = execFileSync(join(stackDir, "logs.sh"), { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });
    } catch {
      throw new Error("logs.sh failed");
    }
    const lines = log.split("\n");
    expect(lines.length, "the log has lines").toBeGreaterThan(1);
    // Counted, never shown: a line that matches carries the token.
    const secrets = tokens.map((t) => t.split("/").pop()!);
    expect(lines.filter((l) => l.includes("/s/t/")).length, "log lines with /s/t/").toBe(0);
    expect(lines.filter((l) => secrets.some((t) => l.includes(t))).length, "log lines with a token of this test").toBe(0);
  });
});
