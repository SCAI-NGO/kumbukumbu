// SPDX-License-Identifier: AGPL-3.0-or-later
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vitest/config";

const server = "http://localhost:8083";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { proxy: { "/api": server, "/mcp": server } },
  build: {
    rollupOptions: {
      output: {
        // The libraries in chunks of their own: they change seldom, so a release mostly replaces the app's own code,
        // and the main chunk stays under Vite's 500 kB warning.
        manualChunks(id) {
          if (!id.includes("/node_modules/")) return undefined;
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
