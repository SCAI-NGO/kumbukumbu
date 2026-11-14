// SPDX-License-Identifier: AGPL-3.0-or-later
// The suite's `test`: Alice's page by default (playwright.config.ts), plus what most tests need.
import { test as base, expect, type BrowserContext, type BrowserContextOptions, type Page, type TestInfo } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { mkdirSync, readdirSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { Api, Mcp, seed } from "./api";
import { e2eDir, fixturesDir, storageState, type User } from "./env";
import { limitWindow, newSession } from "./session";

/** Where each tag's screenshots go (spec §5): committed, so kept small. */
export const shotsDir = join(e2eDir, "..", "..", "docs", "superpowers", "overnighter", "2026-11-14-ui-tests-0.5.0");

/** The phone projects (`phone`, `serial-phone`): for `test.fail(onPhone(testInfo), "F-NN …")`. */
export const onPhone = (testInfo: TestInfo) => testInfo.project.name.endsWith("phone");

/** One screenshot named after the tag and the project, e.g. `calendar-desktop.png`; or a finding's, `F-03-phone.png`. */
export async function shot(page: Page, testInfo: TestInfo, name: string) {
  mkdirSync(shotsDir, { recursive: true });
  await page.screenshot({ path: join(shotsDir, `${name}-${testInfo.project.name.replace(/^serial-?/, "") || "desktop"}.png`), scale: "css" });
}

/**
 * Fixture files below `fixtures/<from>` (e.g. `Work/Pump`), mapped below the vault path `to` (e.g.
 * `/Work/e2e-flags-2/Pump`); a single file (`Work/Pump/Certificate.md`) maps to `to` itself.
 */
export function fixtureTree(from: string, to: string) {
  const root = join(fixturesDir, from);
  if (!statSync(root).isDirectory()) return [{ source: root, path: to }];
  const files: { source: string; path: string }[] = [];
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const source = join(dir, name);
      if (statSync(source).isDirectory()) walk(source);
      else files.push({ source, path: `${to}/${relative(root, source).split("\\").join("/")}` });
    }
  };
  walk(root);
  return files;
}

type TestFixtures = {
  /** Alice's API as the web app calls it. */
  aliceApi: Api;
  /** Bob's API (read-only member of Work). */
  bobApi: Api;
  /** A page of Bob's, in a context of its own with this project's viewport and scheme. */
  bobPage: Page;
  /**
   * A folder name of this test's own, e.g. `e2e-flags-2-desktop`: the tests work below `/Work/<folder>/` and assert
   * relative to it, so they don't see each other's notes.
   */
  folder: string;
  /**
   * A session of its own for `user`, signed in through the sign-in page, in a context with this project's viewport,
   * scheme, zone and locale: for discreet steps and sign-in steps. Closed after the test. Each call gives the test
   * the time a rate limit's wait may take.
   */
  freshSession: (user: User) => Promise<{ context: BrowserContext; page: Page }>;
  /** This project's options for a context of a test's own (e.g. a reader without an account). */
  projectContextOptions: BrowserContextOptions;
  /** Copies fixture files (`fixtureTree`'s arguments) into the vaults as Alice; returns the vault paths written. */
  seedAs: (from: string, to: string) => Promise<string[]>;
};

type AutoFixtures = {
  /** A finding's screenshot (`F-NN-<project>.png`) when a test marked with a finding fails as expected. */
  findingShot: void;
};

type WorkerFixtures = {
  /** An agent with an API key of Alice's (write on Work; Home is hidden in her shared session), for `sh` and `write` through MCP. */
  mcp: Mcp;
};

export const test = base.extend<TestFixtures & AutoFixtures, WorkerFixtures>({
  aliceApi: async ({}, use) => {
    const api = await Api.as("alice");
    await use(api);
    await api.dispose();
  },
  bobApi: async ({}, use) => {
    const api = await Api.as("bob");
    await use(api);
    await api.dispose();
  },
  projectContextOptions: async ({ contextOptions, viewport, isMobile, hasTouch, colorScheme, locale, timezoneId, baseURL }, use) => {
    // No session: the project's default is Alice's saved one, and signing in over it would end it for every test.
    await use({ ...contextOptions, viewport, isMobile, hasTouch, colorScheme, locale, timezoneId, baseURL, storageState: { cookies: [], origins: [] } });
  },
  bobPage: async ({ browser, projectContextOptions }, use) => {
    const context = await browser.newContext({ ...projectContextOptions, storageState: storageState("bob") });
    await use(await context.newPage());
    await context.close();
  },
  freshSession: async ({ browser, projectContextOptions }, use, testInfo) => {
    const contexts: BrowserContext[] = [];
    await use(async (user) => {
      testInfo.setTimeout(testInfo.timeout + limitWindow);
      const session = await newSession(browser, user, projectContextOptions);
      contexts.push(session.context);
      return session;
    });
    for (const context of contexts) await context.close();
  },
  // The source and step, the project, and a random part: a run again on the same stack starts in a fresh folder.
  folder: async ({}, use, testInfo) => {
    const slug = testInfo.title
      .toLowerCase()
      .replace(/@\w+/g, "")
      .split(":")[0]
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-|-$/g, "");
    await use(`e2e-${slug}-${testInfo.project.name}-${randomBytes(3).toString("hex")}`);
  },
  seedAs: async ({ aliceApi }, use) => {
    await use(async (from, to) => {
      const files = fixtureTree(from, to);
      await seed(aliceApi, files);
      return files.map((f) => f.path);
    });
  },
  // `test.fail` takes any failure as the expected one. A finding's own assertion names it in its message
  // (`expect(x, "F-03: …")`); a finding's test that fails for another reason (a page that never loaded) is reported
  // as a failure, not as the finding.
  findingShot: [
    async ({ page }, use, testInfo) => {
      await use();
      const finding = testInfo.annotations.find((a) => a.type === "finding")?.description?.match(/^F-\d+/)?.[0];
      if (!finding || testInfo.expectedStatus !== "failed" || testInfo.status !== "failed") return;
      if (!testInfo.errors.some((e) => (e.message ?? "").includes(`${finding}:`))) {
        testInfo.expectedStatus = "passed";
        return;
      }
      if (!page.isClosed()) await shot(page, testInfo, finding);
    },
    { auto: true },
  ],
  mcp: [
    async ({}, use, workerInfo) => {
      const api = await Api.as("alice");
      const mcp = await Mcp.create(api, `e2e worker ${workerInfo.workerIndex} ${randomBytes(3).toString("hex")}`, { Work: "write" });
      await use(mcp);
      await mcp.dispose();
      await api.dispose();
    },
    { scope: "worker" },
  ],
});

export { expect };
