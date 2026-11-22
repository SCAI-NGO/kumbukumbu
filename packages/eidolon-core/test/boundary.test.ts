// SPDX-License-Identifier: AGPL-3.0-or-later
// The Eidolon core's boundary (offline mode design §2a): it is to be taken out of Kumbu² as a library, so its code
// imports nothing of the web app, of React or of Obsidian, and depends only on packages under permissive licences.
// The DOM is kept out by tsconfig.json, which gives the code no DOM types.
import { readdirSync, readFileSync, realpathSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "vitest";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const src = join(root, "src");
const manifest = JSON.parse(readFileSync(join(root, "package.json"), "utf8")) as { dependencies?: Record<string, string> };
const dependencies = Object.keys(manifest.dependencies ?? {});

/** Licences that keep relicensing the core under the LGPL open: no copyleft of anyone else's inside it. */
const PERMISSIVE = new Set(["MIT", "ISC", "BSD-2-Clause", "BSD-3-Clause", "Apache-2.0", "0BSD", "Unlicense", "CC0-1.0", "BlueOak-1.0.0"]);

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? sourceFiles(join(dir, e.name)) : e.name.endsWith(".ts") ? [join(dir, e.name)] : []);
}

/** Every module specifier of a file: static and dynamic imports, re-exports and import types. */
function specifiersOf(code: string): string[] {
  const found: string[] = [];
  const patterns = [/\b(?:import|export)\s[^;]*?\bfrom\s*["']([^"']+)["']/g, /\bimport\s*["']([^"']+)["']/g, /\bimport\s*\(\s*["']([^"']+)["']/g];
  for (const pattern of patterns) for (const m of code.matchAll(pattern)) found.push(m[1]!);
  return found;
}

/** Why an import breaks the boundary, or null if it may stay. */
function breach(file: string, specifier: string): string | null {
  if (specifier.startsWith(".")) {
    const target = resolve(dirname(file), specifier);
    return target === src || target.startsWith(src + sep) ? null : `reaches outside the core's src/ (${relative(root, target)})`;
  }
  const name = specifier.startsWith("@") ? specifier.split("/").slice(0, 2).join("/") : specifier.split("/")[0]!;
  if (name === "react" || name === "react-dom" || name === "obsidian" || name === "kumbu-web") return `imports ${name}`;
  if (specifier.startsWith("node:")) return "imports a Node module: the host's runtime comes in through a port";
  return dependencies.includes(name) ? null : `imports ${name}, which is no dependency in package.json`;
}

test("the boundary check finds what it is for", () => {
  const file = join(src, "engine", "engine.ts");
  expect(specifiersOf(`import { a } from "./a";\nimport type { B } from "react";\nexport * from "obsidian";\nimport "x";\nawait import("y");`))
    .toEqual(["./a", "react", "obsidian", "x", "y"]);
  expect(breach(file, "./ports")).toBeNull();
  expect(breach(file, "../api/client")).toBeNull();
  expect(breach(file, "../../../../web/src/state/editor")).toMatch(/outside/);
  expect(breach(file, "react")).toMatch(/react/);
  expect(breach(file, "react-dom/client")).toMatch(/react-dom/);
  expect(breach(file, "obsidian")).toMatch(/obsidian/);
  expect(breach(file, "node:fs")).toMatch(/Node/);
  expect(breach(file, "@tanstack/react-query")).toMatch(/no dependency/);
});

test("the core's code imports nothing of the web app, React, Obsidian or Node", () => {
  const breaches = sourceFiles(src).flatMap((file) =>
    specifiersOf(readFileSync(file, "utf8")).flatMap((s) => {
      const why = breach(file, s);
      return why === null ? [] : [`${relative(root, file)}: "${s}" ${why}`];
    }));
  expect(breaches).toEqual([]);
});

test("every package the core depends on, and theirs, has a permissive licence", () => {
  const seen = new Map<string, string>();
  const visit = (name: string, from: string) => {
    const manifestPath = realpathSync(createRequire(join(from, "package.json")).resolve(`${name}/package.json`));
    if (seen.has(manifestPath)) return;
    const dep = JSON.parse(readFileSync(manifestPath, "utf8")) as { license?: string; dependencies?: Record<string, string> };
    seen.set(manifestPath, `${name}: ${dep.license ?? "no licence"}`);
    for (const next of Object.keys(dep.dependencies ?? {})) visit(next, dirname(manifestPath));
  };
  for (const name of dependencies) visit(name, root);
  const licence = (entry: string) => entry.slice(entry.lastIndexOf(": ") + 2);
  expect([...seen.values()].filter((entry) => !PERMISSIVE.has(licence(entry)))).toEqual([]);
});
