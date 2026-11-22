// SPDX-License-Identifier: AGPL-3.0-or-later
// `pnpm test:shell`'s server: two builds of the app, the one a test switches to standing for the next release, served
// as the Kumbu² server serves its web app (Program.cs, Api/Security.cs): the same cache headers and CSP, index.html for
// every other path, and an /api that answers only the version and otherwise "not signed in".
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, statSync } from "node:fs";
import { createServer } from "node:http";
import { extname, join, normalize } from "node:path";

const port = Number(process.env.PORT ?? 5180);
const web = join(import.meta.dirname, "../..");
const builds = { current: "0.7.0-pre.1+aaaaaaa", next: "0.7.0-pre.2+bbbbbbb" } as const;
type Build = keyof typeof builds;

const outDir = (build: Build) => join(import.meta.dirname, ".builds", build);
for (const build of Object.keys(builds) as Build[]) {
  const [version, sha] = builds[build].split("+");
  execFileSync("pnpm", ["exec", "vite", "build", "--logLevel", "warn", "--emptyOutDir", "--outDir", outDir(build)], {
    cwd: web,
    stdio: "inherit",
    env: { ...process.env, KUMBU_VERSION: version, KUMBU_GIT_SHA: sha },
  });
}

// Copied from Api/Security.cs, ContentSecurityPolicy: the worker must work under the server's policy.
const csp =
  "default-src 'self'; img-src 'self' data: blob:; worker-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; " +
  "base-uri 'none'; form-action 'self'; object-src 'none'";
const types: Record<string, string> = {
  ".html": "text/html",
  ".js": "text/javascript",
  ".mjs": "text/javascript",
  ".css": "text/css",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".woff2": "font/woff2",
  ".webmanifest": "application/manifest+json",
};

let serving: Build = "current";

createServer((request, response) => {
  const path = new URL(request.url ?? "/", "http://localhost").pathname;
  response.setHeader("Content-Security-Policy", csp);
  if (path === "/__build" && request.method === "POST") {
    serving = new URL(request.url!, "http://localhost").searchParams.get("to") === "next" ? "next" : "current";
    return response.end(serving);
  }
  if (path.startsWith("/api/")) {
    response.setHeader("Cache-Control", "no-store");
    response.setHeader("Content-Type", "application/json");
    response.setHeader("X-Kumbu-Version", builds[serving]);
    if (path === "/api/version") return response.end(JSON.stringify({ version: builds[serving] }));
    response.statusCode = 401;
    return response.end(JSON.stringify({ error: "Not signed in." }));
  }
  const root = outDir(serving);
  const file = normalize(join(root, path === "/" ? "index.html" : path));
  const found = file.startsWith(root) && existsSync(file) && statSync(file).isFile();
  if (!found && path.startsWith("/assets/")) {
    response.statusCode = 404;
    return response.end();
  }
  const served = found ? file : join(root, "index.html");
  response.setHeader("Cache-Control", path.startsWith("/assets/") ? "public, max-age=31536000, immutable" : "no-cache");
  response.setHeader("Content-Type", types[extname(served)] ?? "application/octet-stream");
  response.end(readFileSync(served));
}).listen(port, "localhost");
