// SPDX-License-Identifier: AGPL-3.0-or-later
import { createHash } from "node:crypto";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, relative } from "node:path";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import type { Plugin } from "vite";
import { configDefaults, defineConfig } from "vitest/config";
import { precache } from "./sw/shell.ts";

const server = "http://localhost:8083";

// The image build passes the version and commit (Dockerfile), the same as the server's: `0.2.0+a6362ec`. Without
// both, as in `vite dev` and local builds, `0.0.0-dev`, which turns update detection off.
const { KUMBU_VERSION, KUMBU_GIT_SHA } = process.env;
const version = KUMBU_VERSION && KUMBU_GIT_SHA ? `${KUMBU_VERSION}+${KUMBU_GIT_SHA}` : "0.0.0-dev";

// pdf.js decodes JPEG 2000 and JBIG2 images (scans) with WebAssembly, or with these plain-script stand-ins, which it
// imports from a folder by fixed names. The app runs pdf.js without WebAssembly (attachments/pdf.ts), so it needs
// them served from its own origin: under /assets, named by their content, as the other assets are.
const decoderNames = ["openjpeg_nowasm_fallback.js", "jbig2_nowasm_fallback.js"];
const decoderSources = join(dirname(createRequire(import.meta.url).resolve("pdfjs-dist/package.json")), "wasm");
const decoders = decoderNames.map((name) => ({ name, source: readFileSync(join(decoderSources, name)) }));
const decoderHash = createHash("sha256");
for (const d of decoders) decoderHash.update(d.source);
const decoderFolder = `/assets/pdfjs-${decoderHash.digest("hex").slice(0, 10)}/`;

function pdfDecoders(): Plugin {
  return {
    name: "kumbu-pdf-decoders",
    configureServer(server) {
      server.middlewares.use((request, response, next) => {
        const decoder = request.url?.startsWith(decoderFolder) && decoders.find((d) => request.url === decoderFolder + d.name);
        if (!decoder) return next();
        response.setHeader("Content-Type", "text/javascript");
        response.end(decoder.source);
      });
    },
    generateBundle() {
      for (const d of decoders) this.emitFile({ type: "asset", fileName: decoderFolder.slice(1) + d.name, source: d.source });
    },
  };
}

/**
 * The app shell's service worker (sw/worker.ts) as /sw.js, at the root so that its scope is the whole app, with the
 * list of this build's files written into it, each with its SHA-256, which the worker checks a kept file against
 * before serving it. Its cache is named by their content: a new build is a new /sw.js, which the browser installs,
 * and only an unchanged build keeps the old cache. Hashed from the files as written, once every plugin is done with
 * them: a chunk's code in `generateBundle` isn't yet what is served.
 */
function appShell(): Plugin {
  let publicDir = "";
  let files: string[] = [];
  return {
    name: "kumbu-app-shell",
    apply: "build",
    enforce: "post",
    configResolved(config) {
      publicDir = config.publicDir;
    },
    buildStart() {
      this.emitFile({ type: "chunk", id: join(import.meta.dirname, "sw/worker.ts"), fileName: "sw.js" });
    },
    generateBundle(_, bundle) {
      const worker = bundle["sw.js"];
      if (worker?.type !== "chunk") return this.error("The app shell's worker wasn't built.");
      if (worker.imports.length > 0) return this.error("The app shell's worker must be one file: it imports none of the app.");
      if (!/(["'`])__KUMBU_SHELL__\1/.test(worker.code)) return this.error("The app shell's worker has no place for its list of files.");
      const publicFiles = publicDir
        ? readdirSync(publicDir, { recursive: true, withFileTypes: true })
            .filter((entry) => entry.isFile())
            .map((entry) => relative(publicDir, join(entry.parentPath, entry.name)).split("\\").join("/"))
            .sort()
        : [];
      files = precache(Object.keys(bundle).sort(), publicFiles);
    },
    writeBundle(options) {
      const outDir = options.dir ?? "";
      const hash = createHash("sha256");
      const listed: Record<string, string> = {};
      for (const file of files) {
        const content = readFileSync(join(outDir, file));
        hash.update(file).update(content);
        listed[file] = createHash("sha256").update(content).digest("hex");
      }
      const shell = JSON.stringify({ name: `kumbu-shell-${hash.digest("hex").slice(0, 16)}`, files: listed });
      const worker = join(outDir, "sw.js");
      writeFileSync(worker, readFileSync(worker, "utf8").replace(/(["'`])__KUMBU_SHELL__\1/, () => shell));
    },
  };
}

export default defineConfig({
  plugins: [react(), tailwindcss(), pdfDecoders(), appShell()],
  // At the top level, so the tests have them too.
  define: { __KUMBU_VERSION__: JSON.stringify(version), __PDF_DECODERS__: JSON.stringify(decoderFolder) },
  server: { proxy: { "/api": server, "/mcp": server } },
  build: {
    // pdf.js (540 kB, loaded only to show a PDF) is the one chunk over Vite's 500 kB default; the app's stay far below.
    chunkSizeWarningLimit: 600,
    // KaTeX's smallest font is under the 4 kB limit; inlined as `data:`, the CSP (default-src 'self') would refuse it.
    assetsInlineLimit: (file) => (/\.(woff2?|ttf)$/.test(file) ? false : undefined),
    rolldownOptions: {
      // The reader page (s.html) and the skill page (k.html) are entries of their own: strangers open them, and they
      // load none of the app.
      input: { main: "index.html", s: "s.html", k: "k.html" },
      output: {
        // The libraries in chunks of their own: they change seldom, so a release mostly replaces the app's own code,
        // and the main chunk stays under Vite's 500 kB warning.
        manualChunks(id) {
          if (!id.includes("/node_modules/")) return undefined;
          // Loaded only when a PDF is shown (attachments/pdf.ts). Not the worker's URL (`?url`), which the lightbox
          // holds: in this chunk, it would load all of pdf.js with the lightbox.
          if (id.includes("/node_modules/.pnpm/pdfjs-dist@") && !id.includes("?")) return "pdfjs";
          // Loaded only for a note with maths (note/math.ts), with the hast utilities only rehype-katex uses: not in
          // the markdown chunk, which loads with the app.
          if (/\/node_modules\/\.pnpm\/(remark-math|micromark-extension-math|mdast-util-math|rehype-katex|katex|hast-util-from-html-isomorphic|hast-util-from-dom|hast-util-to-text|hastscript|hast-util-parse-selector|web-namespaces)@/.test(id)) {
            return undefined;
          }
          // Loaded only for a note with a diagram (reader/mermaidRender.ts), in the app and on the reader page: not in
          // the markdown chunk, which loads with both.
          if (/\/node_modules\/\.pnpm\/(mermaid|@mermaid-js\+[a-z-]+)@/.test(id)) return undefined;
          if (/\/node_modules\/\.pnpm\/(react-markdown|remark-|micromark|mdast-|hast-|unist-|unified|vfile|property-information|space-separated-tokens|comma-separated-tokens|html-url-attributes|decode-named-character-reference|character-entities|ccount|markdown-table|longest-streak|zwitch|trim-lines|devlop|bail|trough|is-plain-obj|estree-util|style-to-|inline-style-parser|escape-string-regexp|@ungap)/.test(id)) {
            return "markdown";
          }
          if (/\/node_modules\/\.pnpm\/(react@|react-dom@|scheduler@|react-router|@remix-run|@tanstack|zustand@|openapi-fetch@)/.test(id)) return "vendor";
          return undefined;
        },
      },
    },
  },
  // e2e/ holds the Playwright suite, which runs against a stack of its own (e2e/stack/run.sh).
  test: { environment: "jsdom", globals: true, setupFiles: ["./src/test-setup.ts"], exclude: [...configDefaults.exclude, "e2e/**"] },
});
