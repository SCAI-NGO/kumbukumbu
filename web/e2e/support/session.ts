// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { closeSync, mkdirSync, openSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { authDir, secrets, usernames, type User } from "./env";

// The server allows 5 sign-ins a minute per username. Every sign-in of the run goes through here and is noted in
// .auth/, across workers; the sixth within a minute waits until the oldest is a minute old.
const ledger = (user: User) => join(authDir, `sign-ins-${user}.json`);
const lock = join(authDir, "sign-ins.lock");

async function withLock<T>(work: () => T): Promise<T> {
  mkdirSync(authDir, { recursive: true });
  for (;;) {
    try {
      closeSync(openSync(lock, "wx"));
      break;
    } catch {
      await new Promise((r) => setTimeout(r, 50));
    }
  }
  try {
    return work();
  } finally {
    rmSync(lock, { force: true });
  }
}

async function reserveSignIn(user: User) {
  for (;;) {
    const wait = await withLock(() => {
      let times: number[] = [];
      try {
        times = JSON.parse(readFileSync(ledger(user), "utf8")) as number[];
      } catch {
        // No sign-in yet.
      }
      const now = Date.now();
      times = times.filter((t) => now - t < 61_000);
      if (times.length < 5) {
        writeFileSync(ledger(user), JSON.stringify([...times, now]));
        return 0;
      }
      return 61_000 - (now - times[0]);
    });
    if (wait === 0) return;
    // A rate limit's own window, not a wait for the page.
    await new Promise((r) => setTimeout(r, wait));
  }
}

/** Signs in through the sign-in page, as a person does. */
export async function signIn(page: Page, user: User) {
  await reserveSignIn(user);
  await page.goto("/signin");
  await page.getByLabel("Username").fill(usernames[user]);
  await page.getByLabel("Password", { exact: true }).fill(secrets().passwords[user]);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).not.toHaveURL(/\/signin/);
}

/** A browser context with a session of its own, made for this test: for discreet steps, which belong to a session. */
export async function freshSession(browser: Browser, user: User, options: Parameters<Browser["newContext"]>[0] = {}): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext(options);
  const page = await context.newPage();
  await signIn(page, user);
  return { context, page };
}
