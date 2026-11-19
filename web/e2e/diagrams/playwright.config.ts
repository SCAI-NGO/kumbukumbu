// SPDX-License-Identifier: AGPL-3.0-or-later
import { defineConfig, devices } from "@playwright/test";

const port = 5179;

/**
 * Diagrams drawn by the real Mermaid in Chromium (`pnpm test:diagrams`), which jsdom can't do: Vite serves
 * mermaidRender.ts to a page of its own (index.html here), no server and no stack needed.
 */
export default defineConfig({
  testDir: ".",
  testMatch: /.*\.browser\.ts/,
  outputDir: ".results",
  timeout: 60_000,
  forbidOnly: true,
  reporter: [["list"]],
  use: { ...devices["Desktop Chrome"], baseURL: `http://localhost:${port}` },
  webServer: {
    command: `pnpm exec vite --port ${port} --strictPort`,
    cwd: "../..",
    url: `http://localhost:${port}/e2e/diagrams/index.html`,
    reuseExistingServer: !process.env.CI,
  },
});
