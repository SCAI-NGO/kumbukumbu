// SPDX-License-Identifier: AGPL-3.0-or-later
// YAML as a plain tree of the text it was written as, for bases and the params of a query note (the C# engine reads them
// as YamlDotNet's representation nodes). Anchors are resolved under a budget, so a few lines that expand into millions of
// nodes end as an error, never as a hang.
import { isAlias, isMap, isScalar, isSeq, parseAllDocuments, type Node } from "yaml";

export type YamlNode =
  | { readonly kind: "scalar"; readonly value: string }
  | { readonly kind: "seq"; readonly items: readonly YamlNode[] }
  | { readonly kind: "map"; readonly entries: ReadonlyArray<readonly [YamlNode, YamlNode]> };

/** What a base may hold, counted over the expanded tree (keys included). */
export const MAX_NODES = 10_000;
export const MAX_DEPTH = 64;

/** The text isn't YAML (`message` says why), or is too large or too deeply nested once its anchors are resolved. */
export class YamlProblem extends Error {
  constructor(message: string, readonly tooLarge: boolean) {
    super(message);
  }
}

/** The root of the first document, or null for an empty text. Throws `YamlProblem`. */
export function readYaml(text: string): YamlNode | null {
  // The library's alias limit (maxAliasCount) applies to turning a document into objects, which this never does; the
  // budget below is the one that counts.
  const documents = parseAllDocuments(text, { schema: "failsafe" });
  for (const document of documents) if (document.errors.length > 0) throw new YamlProblem(document.errors[0]!.message, false);
  const root = documents[0]?.contents;
  if (root === null || root === undefined) return null;

  const anchors = new Map<string, Node>();
  let count = 0;
  const walk = (source: Node | null | undefined, depth: number): YamlNode => {
    if (++count > MAX_NODES || depth > MAX_DEPTH) throw new YamlProblem("too large", true);
    let node = source ?? null;
    if (node !== null && isAlias(node)) node = anchors.get(node.source) ?? null;
    else if (node !== null && node.anchor !== undefined) anchors.set(node.anchor, node);
    if (node === null) return { kind: "scalar", value: "" };
    if (isMap(node))
      return { kind: "map", entries: node.items.map((pair) => [walk(pair.key as Node | null, depth + 1), walk(pair.value as Node | null, depth + 1)] as const) };
    if (isSeq(node)) return { kind: "seq", items: node.items.map((item) => walk(item as Node | null, depth + 1)) };
    if (isScalar(node)) return { kind: "scalar", value: node.value === null || node.value === undefined ? "" : String(node.value) };
    return { kind: "scalar", value: "" };
  };
  return walk(root as Node, 0);
}

/** The value under a scalar key of a map, or undefined. */
export function child(map: Extract<YamlNode, { kind: "map" }>, key: string): YamlNode | undefined {
  for (const [k, v] of map.entries) if (k.kind === "scalar" && k.value === key) return v;
  return undefined;
}
