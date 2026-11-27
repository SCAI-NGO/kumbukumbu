// SPDX-License-Identifier: AGPL-3.0-or-later
// The Eidolon core's boundary (offline mode design §2a): it is to be taken out of Kumbu² as a library, so its code
// imports nothing of the web app, of React or of Obsidian, and depends only on packages under permissive licences.
// The DOM is kept out by tsconfig.json, which gives the code no DOM types; the checks below refuse the ways past both,
// and pin the globals the core may use.
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

function allFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? allFiles(join(dir, e.name)) : [join(dir, e.name)]));
}

const sourceFiles = (dir: string) => allFiles(dir).filter((f) => f.endsWith(".ts"));

/** The only file that declares globals, and the names it may declare (design §2a). */
const GLOBALS = join(src, "globals.d.ts");
const GLOBAL_NAMES = ["TextEncoder", "TextDecoder", "URL", "crypto", "console"];

/** Ways into a file that the specifiers below don't show, or that get past tsconfig.json's types. */
const ESCAPES: [RegExp, string][] = [
  [/\/\/\/\s*<reference\b/, "a triple-slash reference brings in types of its own"],
  [/\bimport\s*\((?!\s*["'][^"'\n]*["']\s*[,)])/, "an import() of anything but a string literal can't be checked"],
  [/\brequire\s*\(/, "require() is an import the check can't see"],
  [/\bglobalThis\b/, "globalThis reaches the host's globals past globals.d.ts"],
  [/\bFunction\s*\(/, "code built from a string reaches the host's globals past globals.d.ts"],
  [/\beval\b/, "eval reaches the host's globals past globals.d.ts"],
  [/@ts-/, "a @ts- comment turns the type check off"],
  [/\bdeclare\s+(?:global|module)\b/, "declare global or module adds to what the core sees"],
];

/** Why a file's code gets past the boundary other than by an import, if it does. */
function escapesOf(code: string): string[] {
  return ESCAPES.filter(([pattern]) => pattern.test(code)).map(([, why]) => why);
}

/** The globals a declaration file declares, or null if it holds anything else (a type, an interface, a namespace). */
function globalsOf(code: string): string[] | null {
  const declarations = code.replace(/\/\/.*$/gm, "");
  if (/\b(?:interface|type|namespace|module|global|enum|function|var|let|import|export)\b/.test(declarations)) return null;
  return [...declarations.matchAll(/\bdeclare\s+(?:class|const)\s+([\w$]+)/g)].map((m) => m[1]!);
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

test("the boundary check finds the ways past it", () => {
  expect(escapesOf(`/// <reference lib="dom" />`)).toHaveLength(1);
  expect(escapesOf(`/// <reference types="node" />`)).toHaveLength(1);
  expect(escapesOf("const m = await import(`./${name}`);")).toHaveLength(1);
  expect(escapesOf("const m = await import(name);")).toHaveLength(1);
  expect(escapesOf(`import fs = require("fs");`)).toHaveLength(1);
  expect(escapesOf(`(globalThis as any).localStorage`)).toHaveLength(1);
  expect(escapesOf("// @ts-ignore\nlocalStorage.getItem(k);")).toHaveLength(1);
  expect(escapesOf("// @ts-expect-error")).toHaveLength(1);
  expect(escapesOf("declare global { const localStorage: unknown; }")).toHaveLength(1);
  expect(escapesOf(`declare module "x" {}`)).toHaveLength(1);
  expect(escapesOf(`new Function("return this")()`)).toHaveLength(1);
  expect(escapesOf(`Function ("return this")()`)).toHaveLength(1);
  expect(escapesOf(`eval("this")`)).toHaveLength(1);
  expect(escapesOf(`(0, eval)("this")`)).toHaveLength(1);
  expect(escapesOf(`const m = await import("./ports");\nimport { a } from "./a";\ndeclare const x: number;\nconst f: Function = isFunction(x) ? retrieval : evaluate;`)).toEqual([]);
  expect(globalsOf("// A comment, with a type and an interface in it.\ndeclare class URL { constructor(url: string); }\ndeclare const crypto: { readonly subtle: unknown };"))
    .toEqual(["URL", "crypto"]);
  expect(globalsOf("declare const localStorage: { getItem(key: string): string | null };")).toEqual(["localStorage"]);
  expect(globalsOf("interface Window { fetch: unknown }")).toBeNull();
  expect(globalsOf("declare function fetch(url: string): Promise<unknown>;")).toBeNull();
  expect(globalsOf("declare var document: unknown;")).toBeNull();
  expect(globalsOf("declare namespace Deno {}")).toBeNull();
});

test("no code of the core gets past the boundary but by an import the check sees", () => {
  const found = sourceFiles(src).flatMap((file) => escapesOf(readFileSync(file, "utf8")).map((why) => `${relative(root, file)}: ${why}`));
  expect(found).toEqual([]);
});

test("tsconfig.json gives the core's code the language only: no library or types of a host", () => {
  const config = JSON.parse(readFileSync(join(root, "tsconfig.json"), "utf8").replace(/^\s*\/\/.*$/gm, "")) as { compilerOptions: { lib?: string[]; types?: string[] } };
  expect(config.compilerOptions.lib).toEqual(["ES2022"]);
  expect(config.compilerOptions.types).toEqual([]);
});

test("the core's src/ holds TypeScript only, and declares its globals in one file", () => {
  expect(allFiles(src).filter((f) => !f.endsWith(".ts") || (f.endsWith(".d.ts") && f !== GLOBALS)).map((f) => relative(root, f))).toEqual([]);
  expect(globalsOf(readFileSync(GLOBALS, "utf8"))).toEqual(GLOBAL_NAMES);
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
