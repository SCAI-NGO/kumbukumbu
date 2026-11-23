// SPDX-License-Identifier: AGPL-3.0-or-later
// A user made for one test (spec §4: deactivation and BYOK, never Alice or Bob, whose sessions the other tests need):
// Alice's admin invitation, the join through its /join link as a person does, the recovery key passed without being
// kept. Random username and password, in memory only.
import { expect, type Browser, type BrowserContext, type BrowserContextOptions, type Page } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api } from "./api";
import { secrets } from "./env";
import { reserve, reservePassword } from "./session";

export type TestUser = {
  username: string;
  /** Random, never logged; for the user's own password checks. */
  password: string;
  /** The user's admin id, for Admin → Users. */
  id: string;
  context: BrowserContext;
  page: Page;
  /** Before each of this user's password checks: 5 a minute per username, as for Alice and Bob. */
  reservePassword: () => Promise<void>;
};

/**
 * Alice invites a new user (`POST /api/admin/invitations`), who joins in a context of their own with `options` (the
 * test's project options) and ends on the vault list, signed in. No screenshot may be taken before the join is done:
 * its screen shows the recovery key.
 */
export async function invitedUser(browser: Browser, options: BrowserContextOptions, prefix: string): Promise<TestUser> {
  const username = `${prefix}-${randomBytes(3).toString("hex")}`;
  const password = randomBytes(18).toString("base64url");
  const alice = await Api.as("alice");
  let link: string;
  try {
    // Never the body in an error: the link holds the invitation's code.
    const response = await alice.send("POST", "/api/admin/invitations", { data: { role: "user", expiresInDays: 1 } });
    if (!response.ok()) throw new Error(`POST /api/admin/invitations: ${response.status()}`);
    link = ((await response.json()) as { link: string }).link;
  } finally {
    await alice.dispose();
  }
  const context = await browser.newContext({ ...options, storageState: { cookies: [], origins: [] } });
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  const page = await context.newPage();
  try {
    await page.goto(link).catch(() => {
      throw new Error("the join link did not open");
    });
    await page.getByLabel("Username").fill(username);
    await page.getByLabel("Password", { exact: true }).fill(password);
    await page.getByRole("button", { name: "Create account" }).click();
    await expect(page.getByLabel("Recovery key", { exact: true })).toBeVisible();
    // Continue needs Copy or Download first; copied into this context's clipboard, never read back.
    await page.getByRole("button", { name: "Copy" }).click();
    await page.getByLabel(/I have stored my recovery key/).check();
    await page.getByRole("button", { name: "Continue" }).click();
    await expect.poll(() => new URL(page.url()).pathname, { message: "the join ends on the vault list" }).toBe("/");
  } catch (error) {
    // The failure's screenshot would show the recovery key, and a half-made user would stay: neither.
    await context.close();
    await removeUser(username);
    throw error;
  }

  const admin = await Api.as("alice");
  try {
    const users = await admin.json<{ id: string; username: string }[]>("GET", "/api/admin/users");
    const id = users.find((u) => u.username === username)?.id;
    if (!id) throw new Error(`${username} isn't in Admin → Users`);
    return { username, password, id, context, page, reservePassword: () => reserve(`password-${username}`) };
  } finally {
    await admin.dispose();
  }
}

/** Confirms the user's password (the password window guarded actions need), counted against their own ledger. */
export async function confirmAs(user: TestUser) {
  await user.reservePassword();
  const response = await user.context.request.post("/api/session/confirm", { data: { password: user.password }, headers: { Origin: new URL(user.page.url()).origin } });
  if (!response.ok()) throw new Error(`POST /api/session/confirm: ${response.status()}`);
}

/**
 * Alice deletes the user through Admin → Users (`POST /api/admin/users/{id}/delete`, her password), if they exist:
 * for a test's cleanup, and for a join that failed half-way.
 */
export async function removeUser(username: string) {
  const admin = await Api.as("alice");
  try {
    const users = await admin.json<{ id: string; username: string }[]>("GET", "/api/admin/users");
    const id = users.find((u) => u.username === username)?.id;
    if (!id) return;
    await reservePassword("alice");
    const response = await admin.send("POST", `/api/admin/users/${encodeURIComponent(id)}/delete`, { data: { password: secrets().passwords.alice, confirm: username } });
    if (!response.ok()) throw new Error(`POST /api/admin/users/…/delete: ${response.status()}`);
  } finally {
    await admin.dispose();
  }
}
