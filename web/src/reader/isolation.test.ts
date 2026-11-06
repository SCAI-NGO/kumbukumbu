// SPDX-License-Identifier: AGPL-3.0-or-later
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, it } from "vitest";

// The reader page runs on the app's origin, beside the owner's session cookie: none of the app's code may come with it.
const allowed = new Set(["react", "react-dom/client", "react-markdown", "remark-gfm", "../index.css"]);

it("imports nothing of the app", () => {
  const folder = import.meta.dirname;
  const sources = readdirSync(folder).filter((name) => /\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name));
  expect(sources).toContain("Reader.tsx");

  const imports = sources.flatMap((name) => {
    const text = readFileSync(join(folder, name), "utf8");
    // `import … from "x"`, `import "x"`, `export … from "x"` and `import("x")`.
    return [...text.matchAll(/(?:^|\s)(?:import|export)\s[^;]*?from\s*["']([^"']+)["']|(?:^|\s)import\s*["']([^"']+)["']|import\(\s*["']([^"']+)["']/g)].map(
      (m) => ({ name, specifier: m[1] ?? m[2] ?? m[3] }),
    );
  });
  expect(imports.length).toBeGreaterThan(0);
  expect(imports.filter(({ specifier }) => !allowed.has(specifier) && !(specifier.startsWith("./") && !specifier.includes("..")))).toEqual([]);
});
