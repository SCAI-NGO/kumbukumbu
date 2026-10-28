// SPDX-License-Identifier: AGPL-3.0-or-later
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import type { Plugin } from "vite";
import { defineConfig } from "vitest/config";

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

export default defineConfig({
  plugins: [react(), tailwindcss(), pdfDecoders()],
  // At the top level, so the tests have them too.
  define: { __KUMBU_VERSION__: JSON.stringify(version), __PDF_DECODERS__: JSON.stringify(decoderFolder) },
  server: { proxy: { "/api": server, "/mcp": server } },
  build: {
    // pdf.js (540 kB, loaded only to show a PDF) is the one chunk over Vite's 500 kB default; the app's stay far below.
    chunkSizeWarningLimit: 600,
    rollupOptions: {
      output: {
        // The libraries in chunks of their own: they change seldom, so a release mostly replaces the app's own code,
        // and the main chunk stays under Vite's 500 kB warning.
        manualChunks(id) {
          if (!id.includes("/node_modules/")) return undefined;
          // Loaded only when a PDF is shown (attachments/pdf.ts). Not the worker's URL (`?url`), which the lightbox
          // holds: in this chunk, it would load all of pdf.js with the lightbox.
          if (id.includes("/node_modules/.pnpm/pdfjs-dist@") && !id.includes("?")) return "pdfjs";
          if (/\/node_modules\/\.pnpm\/(react-markdown|remark-|micromark|mdast-|hast-|unist-|unified|vfile|property-information|space-separated-tokens|comma-separated-tokens|html-url-attributes|decode-named-character-reference|character-entities|ccount|markdown-table|longest-streak|zwitch|trim-lines|devlop|bail|trough|is-plain-obj|estree-util|style-to-|inline-style-parser|escape-string-regexp|@ungap)/.test(id)) {
            return "markdown";
          }
          if (/\/node_modules\/\.pnpm\/(react@|react-dom@|scheduler@|react-router|@remix-run|@tanstack|zustand@|openapi-fetch@)/.test(id)) return "vendor";
          return undefined;
        },
      },
    },
  },
  test: { environment: "jsdom", globals: true, setupFiles: ["./src/test-setup.ts"] },
});
