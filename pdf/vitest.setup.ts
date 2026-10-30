// SPDX-License-Identifier: AGPL-3.0-or-later
// The pool and server tests run dist/worker.js: build it first, so no test runs stale code.
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

export default function setup() {
  const tsc = createRequire(import.meta.url).resolve("typescript/bin/tsc");
  execFileSync(process.execPath, [tsc, "-p", "."], { cwd: import.meta.dirname, stdio: "inherit" });
}
