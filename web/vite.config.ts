// SPDX-License-Identifier: AGPL-3.0-or-later
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vitest/config";

const server = "http://localhost:8083";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { proxy: { "/api": server, "/mcp": server } },
  test: { environment: "jsdom", globals: true, setupFiles: ["./src/test-setup.ts"] },
});
