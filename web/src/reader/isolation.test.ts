// SPDX-License-Identifier: AGPL-3.0-or-later
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve, sep } from "node:path";
import { expect, it } from "vitest";

// The reader page runs on the app's origin, beside the owner's session cookie: none of the app's code may come with it.
const packages = new Set(["react", "react-dom/client", "react-markdown", "remark-gfm"]);

it("imports nothing of the app", () => {
  const folder = import.meta.dirname;
  const styles = resolve(folder, "../index.css");
  const sources = readdirSync(folder, { recursive: true, encoding: "utf8" }).filter((name) => /\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name));
  expect(sources).toContain("Reader.tsx");

  const imports = sources.flatMap((name) => {
    const text = readFileSync(join(folder, name), "utf8");
    // `import … from "x"`, `import "x"`, `export … from "x"` and `import("x")`.
    return [...text.matchAll(/(?:^|\s)(?:import|export)\s[^;]*?from\s*["']([^"']+)["']|(?:^|\s)import\s*["']([^"']+)["']|import\(\s*["']([^"']+)["']/g)].map(
      (m) => ({ name, specifier: m[1] ?? m[2] ?? m[3] }),
    );
  });
  expect(imports.length).toBeGreaterThan(0);

  // A relative import counts by where it lands, from the file that holds it: inside this folder, or the app's styles.
  const allowed = ({ name, specifier }: { name: string; specifier: string }) => {
    if (!specifier.startsWith(".")) return packages.has(specifier);
    const target = resolve(folder, dirname(name), specifier);
    return target === styles || target.startsWith(folder + sep);
  };
  expect(imports.filter((i) => !allowed(i))).toEqual([]);
});

// Fetch tools of AI agents run no script and never send the fragment: the page's static HTML tells them how to read it.
it("tells agents without scripts how to read the note", () => {
  const page = readFileSync(resolve(import.meta.dirname, "../../s.html"), "utf8");
  const noscript = /<noscript>([\s\S]*?)<\/noscript>/.exec(page)?.[1] ?? "";
  expect(noscript).toContain("POST /api/s");
  expect(noscript).toContain("Origin: ");
  expect(noscript).toContain('{"code":"&lt;code&gt;"}');
});
