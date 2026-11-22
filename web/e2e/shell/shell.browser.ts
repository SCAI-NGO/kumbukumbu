// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, test, type Page } from "@playwright/test";

/** The page's worker has installed and kept its build: what a first online visit leaves behind. */
async function shellKept(page: Page): Promise<string[]> {
  return page.evaluate(async () => {
    await navigator.serviceWorker.ready;
    return caches.keys();
  });
}

const switchTo = (page: Page, build: "current" | "next") => page.request.post(`/__build?to=${build}`);

test.beforeEach(async ({ page }) => {
  await switchTo(page, "current");
});

test("the app opens offline after one visit, and the API is never answered from a cache", async ({ page, context }) => {
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  expect(await shellKept(page)).toHaveLength(1);

  await context.setOffline(true);
  await page.goto("/notes/Work/plan.md");

  await expect(page).toHaveTitle("Kumbu²");
  await expect(page.locator("#root")).not.toBeEmpty();
  expect(await page.evaluate(() => fetch("/api/version").then(() => "answered", () => "failed"))).toBe("failed");
  await context.setOffline(false);
});

test("a new release on the server: the app reloads into it, and its worker keeps only the new build", async ({ page, context }) => {
  await page.goto("/");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
  const [before] = await shellKept(page);

  await switchTo(page, "next");
  // The watcher asks the server when the page becomes visible again (web/src/update/apply.ts).
  await page.evaluate(() => document.dispatchEvent(new Event("visibilitychange")));

  await expect(page.getByRole("status", { name: "Updated" })).toHaveText("Updated to 0.7.0-pre.2");
  await expect.poll(() => page.evaluate(() => caches.keys())).toEqual([expect.not.stringMatching(before)]);

  await context.setOffline(true);
  await page.reload();
  await expect(page.locator("#root")).not.toBeEmpty();
  await expect(page.getByRole("alert")).toHaveCount(0);
  await context.setOffline(false);
});
