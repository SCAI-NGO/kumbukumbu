// SPDX-License-Identifier: AGPL-3.0-or-later
// Aliases (the aliases report's hand-test checklist, steps 1-8 and 11-13; spec 2026-11-12-aliases-design.md).
// Aliases resolve vault-wide: two folders with `aliases: [Kumbu, K²]` would make `[[Kumbu]]` ambiguous. So every test is
// serial and removes its notes at its end, even when it fails; steps 9 (Bob with Write) and 10 (Carol) are left out.
import type { Page } from "@playwright/test";
import { expect, shot, test } from "../support/test";
import type { Api } from "../support/api";

const url = (path: string) => `/notes/${path.replace(/^\//, "").split("/").map(encodeURIComponent).join("/")}`;

/** Opens a note by its address; on the phone the install hint is answered with Not now, as a person would. */
async function openNote(page: Page, path: string) {
  const hint = page.getByRole("region", { name: "Install Kumbu² as an app" });
  await page.addLocatorHandler(hint, async () => {
    await hint.getByRole("button", { name: "Not now" }).click();
  });
  await page.goto(url(path));
  await expect(page.getByRole("button", { name: "Show this note in the file tree" })).toBeVisible();
}

/** No sideways scroll: the page is no wider than its window. */
const fitsWidth = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth);

const diary = "Diary/2026-11-12.md";
const projects = "Projects/Kumbukumbu.md";

/**
 * Seeds the named fixtures (below `tags/aliases`) into this test's folder, runs the test, and removes every note it
 * may have made (the named ones and `extra`), on failure too: an alias left behind would make the next test's links ambiguous.
 */
async function withNotes(
  { aliceApi, seedAs, folder }: { aliceApi: Api; seedAs: (from: string, to: string) => Promise<string[]>; folder: string },
  names: string[],
  run: (at: (name: string) => string) => Promise<void>,
  extra: string[] = [],
) {
  const at = (name: string) => `/Work/${folder}/${name}`;
  try {
    for (const name of names) await seedAs(`tags/aliases/${name}`, at(name));
    await run(at);
  } finally {
    for (const name of [...names, ...extra]) await aliceApi.deleteNote(at(name)).catch(() => undefined);
  }
}

test("aliases #1+#2: links by alias are drawn as links and open Kumbukumbu, at Roadmap @aliases @serial @phone", async ({ page, aliceApi, seedAs, folder }, testInfo) => {
  await withNotes({ aliceApi, seedAs, folder }, [projects, diary], async (at) => {
    await openNote(page, at(diary));
    const article = page.locator("article");
    const kumbu = article.getByRole("link", { name: "Kumbu", exact: true });
    const roadmap = article.getByRole("link", { name: "roadmap", exact: true });
    await expect(kumbu).toBeVisible();
    await expect(roadmap).toBeVisible();
    // Neither is drawn as a link to a missing note.
    await expect(article.getByRole("button", { name: /^Create the note/ })).toHaveCount(0);
    const href = `/notes/Work/${folder}/Projects/Kumbukumbu.md`;
    await expect(kumbu).toHaveAttribute("href", href);
    await expect(roadmap).toHaveAttribute("href", href);
    expect(await fitsWidth(page), "no sideways scroll").toBe(true);
    await shot(page, testInfo, "aliases");

    await kumbu.click();
    await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Projects/Kumbukumbu\\.md$`));
    await expect(page.locator("article").getByRole("heading", { name: "Kumbukumbu", level: 1 })).toBeVisible();

    await page.goBack();
    await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Diary/2026-11-12\\.md$`));
    await roadmap.click();
    await expect(page).toHaveURL(new RegExp(`/notes/Work/${folder}/Projects/Kumbukumbu\\.md$`));
    // The app opens the note; it has no jump to a heading for any link, so the Roadmap section is there to read.
    await expect(page.locator("article").getByRole("heading", { name: "Kumbukumbu", level: 1 })).toBeVisible();
    await expect(page.locator("article").getByRole("heading", { name: "Roadmap", level: 2 })).toBeVisible();
  });
});

test("aliases #3: Linked from lists the diary; Unlinked mentions line 2 with Kumbu marked, not line 1 @aliases @serial @phone", async ({ page, aliceApi, seedAs, folder }) => {
  await withNotes({ aliceApi, seedAs, folder }, [projects, diary], async (at) => {
    await openNote(page, at(projects));
    const linked = page.getByRole("region", { name: "Linked from" });
    await expect(linked.getByRole("heading", { name: "Linked from · 1" })).toBeVisible();
    await expect(linked.getByRole("button", { name: /^2026-11-12/ })).toBeVisible();
    await expect(linked.getByRole("listitem")).toHaveCount(1);

    const mentions = page.getByRole("region", { name: "Unlinked mentions" });
    await mentions.getByRole("button", { name: "Unlinked mentions" }).click();
    const items = mentions.getByRole("listitem");
    await expect(items).toHaveCount(1);
    await expect(items.first()).toContainText("2026-11-12");
    await expect(items.first()).toContainText("Kumbu needs a calendar.");
    // The marked word is Kumbu, and only there: the first line (the linked one) isn't listed.
    await expect(items.first().locator("mark")).toHaveText(["Kumbu"]);
    await expect(mentions).not.toContainText("Talked with Bob");
    expect(await fitsWidth(page), "no sideways scroll").toBe(true);
  });
});

test("aliases #5–#7: links, backlinks and cat lead to Kumbukumbu by alias @aliases @serial", async ({ aliceApi, seedAs, folder }) => {
  await withNotes({ aliceApi, seedAs, folder }, [projects, diary, "Embed.md"], async (at) => {
    // #5: both links, marked (alias).
    const links = await aliceApi.console(`links ${at(diary)}`);
    expect(links.isError).toBe(false);
    expect(links.text.trim().split("\n")).toEqual([
      `1: [[Kumbu]] → ${at(projects)} (alias)`,
      `1: [[K²#Roadmap|roadmap]] → ${at(projects)} (alias)`,
    ]);

    // #6: the diary's line 1 links here; line 2 mentions it. The embed's note is another source, and no unlinked mention.
    const backlinks = await aliceApi.console(`backlinks ${at(projects)}`);
    expect(backlinks.text.trim().split("\n")).toEqual([
      `${at(diary)}:1:Talked with Bob about [[Kumbu]] and the [[K²#Roadmap|roadmap]].`,
      `${at("Embed.md")}:3:![[Kumbu]]`,
    ]);
    const unlinked = await aliceApi.console(`backlinks --unlinked ${at(projects)}`);
    expect(unlinked.text.trim()).toBe(`${at(diary)}:2:Kumbu needs a calendar.`);

    // #7: the embedded-notes block shows Kumbukumbu's text.
    const cat = await aliceApi.console(`cat ${at("Embed.md")}`);
    expect(cat.isError).toBe(false);
    const [text, embedded] = cat.text.split(/^--- embedded notes:.*---$/m);
    expect(text).toContain("![[Kumbu]]");
    expect(embedded).toContain(`[line 3: ![[Kumbu]] → ${at(projects)}]`);
    expect(embedded).toContain("# Kumbukumbu");
    expect(embedded).toContain("## Roadmap");
    expect(embedded).toContain("Text");
  });
});

test("aliases #8: mv keeps alias links unchanged and leads them to the new place @aliases @serial", async ({ aliceApi, mcp, seedAs, folder }) => {
  const moved = "Done/Kumbukumbu.md";
  await withNotes(
    { aliceApi, seedAs, folder },
    [projects, diary],
    async (at) => {
      const before = (await aliceApi.getNote(at(diary))).content;
      const away = await mcp.sh(`mv ${at(projects)} ${at(moved)}`);
      expect(away.isError, away.text).toBe(false);

      // The diary isn't rewritten; its links lead to the new place, still by alias.
      expect((await aliceApi.getNote(at(diary))).content).toBe(before);
      const cat = await aliceApi.console(`cat ${at(diary)}`);
      expect(cat.text.trim()).toBe(before.trim());
      const links = await aliceApi.console(`links ${at(diary)}`);
      expect(links.text.trim().split("\n")).toEqual([
        `1: [[Kumbu]] → ${at(moved)} (alias)`,
        `1: [[K²#Roadmap|roadmap]] → ${at(moved)} (alias)`,
      ]);

      // And back.
      const back = await mcp.sh(`mv ${at(moved)} ${at(projects)}`);
      expect(back.isError, back.text).toBe(false);
      expect((await aliceApi.getNote(at(diary))).content).toBe(before);
      const again = await aliceApi.console(`links ${at(diary)}`);
      expect(again.text.trim().split("\n")).toEqual([
        `1: [[Kumbu]] → ${at(projects)} (alias)`,
        `1: [[K²#Roadmap|roadmap]] → ${at(projects)} (alias)`,
      ]);
    },
    [moved],
  );
});

test("aliases #11: the published diary reads plain text @aliases @serial @phone", async ({ aliceApi, mcp, browser, projectContextOptions, seedAs, folder }) => {
  let published: string | undefined;
  const context = await browser.newContext(projectContextOptions);
  try {
    await withNotes({ aliceApi, seedAs, folder }, [projects, diary], async (at) => {
      published = at(diary);
      const added = await mcp.sh(`publish add ${at(diary)}`);
      expect(added.isError, "publish add was refused").toBe(false);
      // The link is shown once; its secret is never printed.
      const link = /^link \(shown once\): (\S+)$/m.exec(added.text)?.[1];
      expect(link, "publish add gave a reader link").toMatch(/^https?:\/\/[^\s#]+#\S+$/);
      expect(added.text, "nothing of the other note").not.toMatch(/Projects|Kumbukumbu/);

      const reader = await context.newPage();
      await reader.goto(link!);
      const article = reader.locator("article");
      await expect(article.getByRole("heading", { name: "2026-11-12", level: 1 })).toBeVisible();
      await expect(article.getByText("Talked with Bob about Kumbu and the roadmap.", { exact: true })).toBeVisible();
      await expect(article.getByText("Kumbu needs a calendar.", { exact: true })).toBeVisible();
      await expect(article.getByRole("link")).toHaveCount(0);
      await expect(reader.getByText(/Kumbukumbu|Projects|Roadmap/)).toHaveCount(0);
      expect(await fitsWidth(reader), "no sideways scroll").toBe(true);
    });
  } finally {
    // Withdrawn even when the test failed: a publication outlives the note's removal.
    if (published) await mcp.sh(`publish rm ${published}`).catch(() => undefined);
    await context.close();
  }
});

test("aliases #12: a Markdown link to an alias is not found @aliases @serial", async ({ aliceApi, seedAs, folder }) => {
  await withNotes({ aliceApi, seedAs, folder }, [projects, "Talk.md"], async (at) => {
    const links = await aliceApi.console(`links ${at("Talk.md")}`);
    expect(links.text.trim()).toBe("3: [talk](Kumbu) → not found");
  });
});

test("aliases #13: odd YAML saves and opens; other links keep working; the server stays up @aliases @serial", async ({ page, aliceApi, seedAs, folder }) => {
  await withNotes({ aliceApi, seedAs, folder }, [projects, diary, "Yaml.md"], async (at) => {
    // It saved (the seed wrote it) and reads back as written.
    const note = await aliceApi.getNote(at("Yaml.md"));
    expect(note.content).toContain("a: &a [*a]");
    expect(note.content).toContain("aliases: [x]");

    await openNote(page, at("Yaml.md"));
    const article = page.locator("article");
    await expect(article.getByRole("heading", { name: "Yaml", level: 1 })).toBeVisible();
    await expect(article.getByText("Odd front matter")).toBeVisible();
    await expect(article.getByRole("link", { name: "Kumbu", exact: true })).toHaveAttribute("href", `/notes/Work/${folder}/Projects/Kumbukumbu.md`);

    // Links elsewhere in the vault keep working.
    const links = await aliceApi.console(`links ${at(diary)}`);
    expect(links.text.trim().split("\n")).toEqual([
      `1: [[Kumbu]] → ${at(projects)} (alias)`,
      `1: [[K²#Roadmap|roadmap]] → ${at(projects)} (alias)`,
    ]);
    await openNote(page, at(projects));
    await expect(page.getByRole("region", { name: "Linked from" }).getByRole("heading", { name: "Linked from · 2" })).toBeVisible();

    // The server is up and answers.
    const health = await aliceApi.context.get("/healthz");
    expect(health.ok()).toBe(true);
    expect(await health.text()).toBe("ok");
  });
});
