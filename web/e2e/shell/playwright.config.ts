// SPDX-License-Identifier: AGPL-3.0-or-later
import { defineConfig, devices } from "@playwright/test";

const port = 5180;

/**
 * The app shell's service worker in Chromium (`pnpm test:shell`): two builds of the app behind a stand-in for the
 * server (serve.ts), no stack needed. Opening offline after one visit, and the worker following a new release.
 */
export default defineConfig({
  testDir: ".",
  testMatch: /.*\.browser\.ts/,
  outputDir: ".results",
  timeout: 60_000,
  workers: 1,
  forbidOnly: true,
  reporter: [["list"]],
  use: { ...devices["Desktop Chrome"], baseURL: `http://localhost:${port}`, serviceWorkers: "allow" },
  webServer: {
    command: `node e2e/shell/serve.ts`,
    cwd: "../..",
    env: { PORT: String(port) },
    url: `http://localhost:${port}/`,
    timeout: 120_000,
    reuseExistingServer: !process.env.CI,
  },
});
