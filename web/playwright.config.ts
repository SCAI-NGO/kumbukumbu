// SPDX-License-Identifier: AGPL-3.0-or-later
import { defineConfig, devices } from "@playwright/test";
import { fileURLToPath } from "node:url";

// Always localhost, never 127.0.0.1: the server's Origin check compares with KUMBU_PUBLIC_URL, and Chromium keeps
// the Secure session cookie on http://localhost only.
const baseURL = "http://localhost:8080";

// Playwright matches test files by their absolute path, globs included: anchored on this checkout's e2e/mcp/, so a
// checkout under a folder named mcp doesn't move every spec into the mcp project.
const mcpDir = fileURLToPath(new URL("./e2e/mcp/", import.meta.url)).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const mcpFiles = new RegExp(`^${mcpDir}.*\\.spec\\.ts$`);
const notMcp = new RegExp(`^${mcpDir}`);

// Alice's saved session by default; the setup makes it (e2e/global.setup.ts).
const alice = "e2e/.auth/alice.json";
const desktop = { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 }, colorScheme: "light" as const, storageState: alice };
const phone = {
  ...devices["Desktop Chrome"],
  viewport: { width: 375, height: 812 },
  deviceScaleFactor: 1,
  isMobile: true,
  hasTouch: true,
  colorScheme: "dark" as const,
  storageState: alice,
};

/**
 * The suite runs against a stack of its own (e2e/stack/run.sh brings it up and down). `@serial` tests look at lists
 * across vaults (What's on, Recent changes, the badge) or at discreet vaults: they run after all others, one at a
 * time, so no other test writes meanwhile.
 */
export default defineConfig({
  testDir: "e2e",
  testMatch: /.*\.spec\.ts/,
  outputDir: "e2e/.results",
  timeout: 60_000,
  expect: { timeout: 10_000 },
  workers: 2,
  retries: 0,
  forbidOnly: true,
  // No HTML report: its steps name every address and filled value, the reader links' codes and the passwords too.
  reporter: [["list"]],
  use: {
    baseURL,
    timezoneId: "Europe/Vienna",
    locale: "en-GB",
    trace: "off",
    screenshot: "only-on-failure",
  },
  projects: [
    // No screenshots of the setup: its screens show the recovery keys.
    { name: "setup", testMatch: /global\.setup\.ts/, use: { ...desktop, storageState: undefined, screenshot: "off" } },
    // No browser: its tests use the `request` fixture only (e2e/mcp/fixtures.ts), and no saved session.
    { name: "mcp", dependencies: ["setup"], testMatch: mcpFiles },
    { name: "desktop", dependencies: ["setup"], testIgnore: notMcp, grepInvert: /@serial/, use: desktop },
    { name: "phone", dependencies: ["setup"], testIgnore: notMcp, grep: /@phone/, grepInvert: /@serial/, use: phone },
    // The offline mode in Firefox too: a Firefox-only bug of its storage bit once already. Not in WebKit: it sends no
    // Secure cookie to http://localhost, so the suite's plain-HTTP stack can't sign it in. Not the @serial ones: this
    // project runs in the parallel phase.
    { name: "offline-firefox", dependencies: ["setup"], grep: /@offline/, grepInvert: /@serial/, use: { ...devices["Desktop Firefox"], viewport: desktop.viewport, colorScheme: "light", storageState: alice } },
    { name: "serial", dependencies: ["desktop", "phone", "mcp"], testIgnore: notMcp, grep: /@serial/, workers: 1, use: desktop },
    { name: "serial-phone", dependencies: ["serial"], testIgnore: notMcp, grep: /(?=.*@serial)(?=.*@phone)/, workers: 1, use: phone },
  ],
});
