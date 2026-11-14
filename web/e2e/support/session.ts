// SPDX-License-Identifier: AGPL-3.0-or-later
import { expect, type Browser, type BrowserContext, type BrowserContextOptions, type Page } from "@playwright/test";
import { closeSync, mkdirSync, openSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { authDir, secrets, usernames, type User } from "./env";

// The server allows 5 sign-ins a minute per username, and 5 PIN entries a minute per account. Every sign-in and PIN
// entry of the run goes through a ledger in .auth/, across workers; the sixth within a minute waits until the
// oldest is a minute old. A test that signs in or enters the PIN gets the time for that wait (freshSession).
export const limitWindow = 61_000;

async function withLock<T>(work: () => T): Promise<T> {
  mkdirSync(authDir, { recursive: true });
  const lock = join(authDir, "ledger.lock");
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

async function reserve(ledgerName: string, limit = 5) {
  const file = join(authDir, `ledger-${ledgerName}.json`);
  for (;;) {
    const wait = await withLock(() => {
      let times: number[] = [];
      try {
        times = JSON.parse(readFileSync(file, "utf8")) as number[];
      } catch {
        // Nothing yet.
      }
      const now = Date.now();
      times = times.filter((t) => now - t < limitWindow);
      if (times.length < limit) {
        writeFileSync(file, JSON.stringify([...times, now]));
        return 0;
      }
      return limitWindow - (now - times[0]);
    });
    if (wait === 0) return;
    // A rate limit's own window, not a wait for the page.
    await new Promise((r) => setTimeout(r, wait));
  }
}

/** Signs in through the sign-in page, as a person does. */
export async function signIn(page: Page, user: User) {
  await reserve(`sign-in-${user}`);
  await page.goto("/signin");
  await page.getByLabel("Username").fill(usernames[user]);
  await page.getByLabel("Password", { exact: true }).fill(secrets().passwords[user]);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page).not.toHaveURL(/\/signin/);
}

/** A browser context with a session of its own: for discreet steps, which belong to a session. */
export async function newSession(browser: Browser, user: User, options: BrowserContextOptions): Promise<{ context: BrowserContext; page: Page }> {
  // Never with a saved session: signing in over Alice's shared one would end it for every other test.
  const context = await browser.newContext({ ...options, storageState: { cookies: [], origins: [] } });
  const page = await context.newPage();
  await signIn(page, user);
  return { context, page };
}

/**
 * In the open "More vaults" dialog (the vault menu's More vaults…): enters Alice's PIN, keeps the default time, and
 * opens. Passes when the pill says the discreet vaults are shown. At most two PIN entries a test: the tenth in one
 * session ends it.
 */
export async function enterPin(page: Page) {
  await reserve("pin-alice");
  const dialog = page.getByRole("dialog", { name: "More vaults" });
  await dialog.getByLabel("PIN").fill(secrets().pin);
  await dialog.getByRole("button", { name: "Open" }).click();
  await expect(page.getByRole("group", { name: "Discreet vaults shown" }).first()).toBeVisible();
}

/** ✕ on the pill: the discreet vaults are hidden again. */
export async function hideDiscreet(page: Page) {
  await page.getByRole("button", { name: "Hide discreet vaults" }).first().click();
  await expect(page.getByRole("group", { name: "Discreet vaults shown" })).toHaveCount(0);
}

/**
 * Opens the vault menu's More vaults… and enters the PIN. On the phone, open the tree drawer first (the menu sits
 * at its foot).
 */
export async function revealDiscreet(page: Page) {
  await page.getByRole("button", { name: /^Switch vault/ }).click();
  await page.getByText("More vaults…").click();
  await enterPin(page);
}
