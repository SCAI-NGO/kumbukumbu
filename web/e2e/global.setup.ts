// SPDX-License-Identifier: AGPL-3.0-or-later
// The run's setup, through the app's public paths only: bootstrap-admin's link for Alice, her vaults and notes
// through the API the web app calls, Bob through her invitations. Secrets are random per run and stay in .auth/.
import { expect, test as setup, type Browser, type Page } from "@playwright/test";
import { execFileSync } from "node:child_process";
import { randomBytes, randomInt } from "node:crypto";
import { mkdirSync, readdirSync, rmSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { Api, seed } from "./support/api";
import { authDir, fixturesDir, stackDir, storageState, usernames, writeSecrets, type Secrets, type User } from "./support/env";

const password = () => randomBytes(18).toString("base64url");

/** Follows a /join link: chooses the username and password, then passes the recovery key screen without keeping the key. */
async function joinWith(page: Page, link: string, user: User, secrets: Secrets) {
  await page.goto(link);
  await page.getByLabel("Username").fill(usernames[user]);
  await page.getByLabel("Password", { exact: true }).fill(secrets.passwords[user]);
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.getByLabel("Recovery key", { exact: true })).toBeVisible();
  // Copied into this browser's clipboard, which ends with the context; never read back or written anywhere.
  await page.getByRole("button", { name: "Copy" }).click();
  await page.getByLabel(/I have stored my recovery key/).check();
  await page.getByRole("button", { name: "Continue" }).click();
  await expect(page).toHaveURL(/\/$/);
}

// browser.newContext() doesn't take the config's `use`: the zone matters, as the app presets the calendar's zone
// from the browser's on the first visit.
async function newPage(browser: Browser, baseURL: string) {
  const context = await browser.newContext({ baseURL, timezoneId: "Europe/Vienna", locale: "en-GB", viewport: { width: 1280, height: 800 } });
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  return { context, page: await context.newPage() };
}

/** The vaults' own notes (fixtures/Home, fixtures/Work); each tag's fixtures (fixtures/tags/) are seeded by its tests. */
function fixtureFiles(vault: string) {
  const root = join(fixturesDir, vault);
  const files: { source: string; path: string }[] = [];
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const source = join(dir, name);
      if (statSync(source).isDirectory()) walk(source);
      else files.push({ source, path: `/${vault}/${relative(root, source).split("\\").join("/")}` });
    }
  };
  walk(root);
  return files;
}

setup("Alice and Bob join, with Home (discreet) and Work", async ({ browser, baseURL }) => {
  setup.setTimeout(180_000);
  rmSync(authDir, { recursive: true, force: true });
  mkdirSync(authDir, { recursive: true });

  // This checkout's build, never a release pulled in its place.
  const anonymous = await Api.anonymous();
  const { version } = await anonymous.json<{ version: string }>("GET", "/api/version");
  expect(version, "the server is this checkout's dev build").toMatch(/^0\.0\.0-dev/);
  await anonymous.dispose();

  const secrets: Secrets = { passwords: { alice: password(), bob: password() }, pin: String(randomInt(0, 1_000_000)).padStart(6, "0") };
  writeSecrets(secrets);

  // Alice, the first admin.
  const link = execFileSync(join(stackDir, "bootstrap-admin.sh"), { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim().split("\n").pop()!;
  expect(link.startsWith(`${baseURL}/join#`), "bootstrap-admin printed a join link").toBe(true);
  const alice = await newPage(browser, baseURL!);
  await joinWith(alice.page, link, "alice", secrets);
  await alice.context.storageState({ path: storageState("alice") });

  const api = await Api.from(alice.context.request, "alice");
  // The API context sends the session cookie (Secure, on http://localhost) and keeps it.
  await api.json("GET", "/api/session");
  const headers = { Origin: baseURL! };
  const post = (url: string, data: unknown) => alice.context.request.post(url, { data, headers });
  const put = (url: string, data: unknown) => alice.context.request.put(url, { data, headers });

  for (const name of ["Home", "Work"]) expect((await post("/api/vaults", { name })).ok(), `create ${name}`).toBe(true);
  const write = await Api.as("alice");
  await seed(write, fixtureFiles("Home"));
  await seed(write, fixtureFiles("Work"));

  // Home becomes discreet the app's way: a PIN, discreet mode, the mark, then hidden.
  expect((await post("/api/account/discreet/pin", { password: secrets.passwords.alice, pin: secrets.pin })).ok(), "set the PIN").toBe(true);
  expect((await post("/api/account/discreet/reveal", { pin: secrets.pin })).ok(), "reveal").toBe(true);
  expect((await put("/api/account/discreet/vaults/Home", { discreet: true })).ok(), "mark Home discreet").toBe(true);
  expect((await post("/api/account/discreet/hide", {})).ok(), "hide").toBe(true);

  // Bob: an account invitation, then a read-only invitation to Work, each through its link.
  const invitation = await (await post("/api/admin/invitations", { role: "user", expiresInDays: 1 })).json();
  await write.confirmPassword();
  const share = await write.json<{ link: string }>("POST", "/api/vaults/Work/invitations", {
    password: secrets.passwords.alice,
    label: "Bob",
    role: "read",
    expiresInDays: 1,
    confirm: false,
  });
  const bob = await newPage(browser, baseURL!);
  await joinWith(bob.page, invitation.link, "bob", secrets);
  await bob.page.goto(share.link);
  await bob.page.getByRole("button", { name: "Accept" }).click();
  await expect(bob.page.getByRole("heading", { name: "Work is shared with you" })).toBeVisible();
  await bob.context.storageState({ path: storageState("bob") });

  await alice.context.storageState({ path: storageState("alice") });
  await write.dispose();
  await alice.context.close();
  await bob.context.close();
});
