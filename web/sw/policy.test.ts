// SPDX-License-Identifier: AGPL-3.0-or-later
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { appPolicy, isKdfWorker, kdfWorkerPolicy } from "./policy";

const server = readFileSync(join(import.meta.dirname, "../../src/Kumbu.Server/Api/Security.cs"), "utf8");

/** A C# string constant of Security.cs, its concatenated literals joined. */
function constant(name: string): string {
  const declaration = server.match(new RegExp(`const string ${name} =((?:\\s*"(?:[^"\\\\]|\\\\.)*"\\s*\\+?)+);`));
  if (!declaration) throw new Error(`Security.cs has no constant ${name}`);
  return Array.from(declaration[1]!.matchAll(/"((?:[^"\\]|\\.)*)"/g), (m) => m[1]).join("");
}

describe("the worker's policies", () => {
  it("equal the server's", () => {
    expect(appPolicy).toBe(constant("ContentSecurityPolicy"));
    expect(kdfWorkerPolicy).toBe(constant("KdfWorkerPolicy"));
  });

  it("give the password hash worker's own policy only to its file directly under /assets", () => {
    expect(isKdfWorker("/assets/kdfWorker-B1x2.js")).toBe(true);
    for (const path of ["/assets/main.js", "/assets/x/kdfWorker-B1x2.js", "/kdfWorker-B1x2.js", "/assets/kdfWorker-B1x2.css"]) expect(isKdfWorker(path), path).toBe(false);
  });
});
