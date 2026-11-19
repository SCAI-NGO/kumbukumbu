// SPDX-License-Identifier: AGPL-3.0-or-later
// The MCP handshake (spec §3): the server's instructions come only from `initialize` and `server/discover`
// (src/Kumbu.Server/Mcp/HandshakeInstructions.cs), so the suite reads them through `Mcp.initialize`.
import { expect, test } from "./fixtures";

test("handshake: initialize answers with the server's name and instructions @memory", async ({ mcp }, testInfo) => {
  const result = await mcp.initialize();
  expect(result.serverInfo.name).toBe("kumbu");
  expect(typeof result.instructions).toBe("string");
  expect(result.instructions!.trim().length, "the instructions aren't empty").toBeGreaterThan(0);
  // The version the server answers to the one asked for (2025-11-25); recorded in the run's report.
  expect(typeof result.protocolVersion).toBe("string");
  testInfo.annotations.push({ type: "protocolVersion", description: result.protocolVersion });
});
