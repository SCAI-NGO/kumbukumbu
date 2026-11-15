// SPDX-License-Identifier: AGPL-3.0-or-later
// The pool and server tests run dist/worker.js: build it first, so no test runs stale code.
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";

export default function setup() {
  // TypeScript 7 exports only its package.json and API, not bin/: found beside the package.json.
  const tsc = join(dirname(createRequire(import.meta.url).resolve("typescript/package.json")), "bin/tsc");
  execFileSync(process.execPath, [tsc, "-p", "."], { cwd: import.meta.dirname, stdio: "inherit" });
}
