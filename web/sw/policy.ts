// SPDX-License-Identifier: AGPL-3.0-or-later
// The server's two policies for the files the worker keeps (src/Kumbu.Server/Api/Security.cs), which the worker sets
// on its own answers. A copy, since the image's web build doesn't see the server's sources; policy.test.ts holds it
// equal to the server's.

/** `Security.ContentSecurityPolicy`: the app's page and every kept file but the password hash worker. */
export const appPolicy =
  "default-src 'self'; img-src 'self' data: blob:; worker-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; " +
  "base-uri 'none'; form-action 'self'; object-src 'none'";

/** `Security.KdfWorkerPolicy`: the password hash worker's, the one script that may compile WebAssembly. */
export const kdfWorkerPolicy =
  "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'; object-src 'none'";

/** Whether `path` is the password hash worker's: a file directly under /assets, as `Security.IsKdfWorker` has it. */
export const isKdfWorker = (path: string) => /^\/assets\/kdfWorker-[^/]*\.js$/.test(path);
