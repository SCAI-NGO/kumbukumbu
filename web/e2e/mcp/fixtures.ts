// SPDX-License-Identifier: AGPL-3.0-or-later
// The `mcp` project's `test`: no page, so no browser (spec §3); Alice's and Bob's API, a folder, and an agent's key.
import { test as base, expect } from "@playwright/test";
import { randomBytes } from "node:crypto";
import { Api, Mcp } from "../support/api";

type TestFixtures = {
  /** Alice's API as the web app calls it. */
  aliceApi: Api;
  /** Bob's API (read-only member of Work). */
  bobApi: Api;
  /** A folder name of this test's own, e.g. `e2e-mcp-memory-3f9a1c`: the test works below `/Work/<folder>/`. */
  folder: string;
};

type AutoFixtures = {
  /** A finding's test (`M-NN`) that fails for another reason than its finding's assertion is reported as a failure. */
  mcpFinding: void;
};

type WorkerFixtures = {
  /** An agent with an API key of Alice's, `Work: write`, for `sh`, `write` and the handshake. */
  mcp: Mcp;
};

/**
 * Text with its secrets replaced by `<redacted>`, before an `expect` that would print it: upload tokens, URL
 * fragments (reader, share and join links carry their code there), share codes, Bearer tokens and keys.
 */
export const redact = (text: string) =>
  text
    .replace(/\/api\/agent-upload\/[^\s"'`)<>]+/g, "/api/agent-upload/<redacted>")
    .replace(/(https?:\/\/[^\s#"'`<>]*)#[^\s"'`)<>]+/g, "$1#<redacted>")
    .replace(/\b[0-9A-HJKMNP-TV-Z]{4}(?:-[0-9A-HJKMNP-TV-Z]{4}){7}\b/g, "<redacted>")
    .replace(/\bBearer\s+\S+/g, "Bearer <redacted>")
    .replace(/\bkumbu_[A-Za-z0-9_]+/g, "<redacted>")
    .replace(/\b[A-Za-z0-9]{32,}\b/g, "<redacted>");

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
  // The same slug as the browser tests' (support/test.ts), without the project: this one is always `mcp`.
  folder: async ({}, use, testInfo) => {
    const slug = testInfo.title
      .toLowerCase()
      .replace(/@\w+/g, "")
      .split(":")[0]
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-|-$/g, "");
    await use(`e2e-mcp-${slug}-${randomBytes(3).toString("hex")}`);
  },
  // As findingShot: `test.fail` takes any failure as the expected one, so a finding's own assertion names it
  // (`expect(x, "M-01: …")`). `M-xx` is the placeholder until the coordinator numbers the finding.
  mcpFinding: [
    async ({}, use, testInfo) => {
      await use();
      const finding = testInfo.annotations.find((a) => a.type === "finding")?.description?.match(/^M-(?:\d+|xx)/)?.[0];
      if (!finding || testInfo.expectedStatus !== "failed" || testInfo.status !== "failed") return;
      if (!testInfo.errors.some((e) => (e.message ?? "").includes(`${finding}:`))) testInfo.expectedStatus = "passed";
    },
    { auto: true },
  ],
  mcp: [
    async ({}, use, workerInfo) => {
      const api = await Api.as("alice");
      const mcp = await Mcp.create(api, `e2e mcp worker ${workerInfo.workerIndex} ${randomBytes(3).toString("hex")}`, { Work: "write" });
      await use(mcp);
      await mcp.dispose();
      await api.dispose();
    },
    { scope: "worker" },
  ],
});

export { expect };
