// SPDX-License-Identifier: AGPL-3.0-or-later
// A base in Obsidian's format (a .base file, or a ```base block of a query note): filters, formulas and views with their
// columns, sort and limit (Kumbu.Core Queries/Base). A view is checked when it runs, so one broken view or formula doesn't
// take the others down. The cases are in tests/query-conformance/bases.
import { Evaluator, order as ordered, truthy, type Scope } from "./evaluator";
import { QueryError, inExpression, type QueryWarning } from "./errors";
import type { Expr } from "./expr";
import { ExpressionParser } from "./parser";
import type { QueryNote } from "./note";
import { textValue, type Value } from "./value";
import { QueryVault } from "./vault";
import { child, readYaml, YamlProblem, type YamlNode } from "./yamlTree";

type YamlMap = Extract<YamlNode, { kind: "map" }>;

/** One row of a result: the note and its cells, in the order of `QueryResult.columns`. */
export type QueryRow = { readonly note: QueryNote; readonly cells: readonly Value[] };

/** What a view of a base gives: its name, its columns as written in `order`, its rows, and what its expressions name that doesn't exist. */
export type QueryResult = {
  readonly view: string;
  readonly columns: readonly string[];
  readonly rows: readonly QueryRow[];
  readonly warnings: readonly QueryWarning[];
};

const invalid = (message: string) => new QueryError("base", message, 0);

const scalarOf = (map: YamlMap, key: string): string | null => {
  const node = child(map, key);
  return node?.kind === "scalar" ? node.value : null;
};

const textOf = (node: YamlNode): string => {
  if (node.kind !== "scalar") throw invalid("Expected a text, found a list or a map.");
  return node.value;
};

// int.TryParse as .NET reads it: optional white space and sign around ASCII digits, within 32 bits.
function wholeNumber(text: string): number | null {
  if (!/^[\t-\r ]*[+-]?[0-9]+[\t-\r ]*$/.test(text)) return null;
  const n = Number(text.replace(/[\t-\r ]/g, ""));
  return n >= -2147483648 && n <= 2147483647 ? n : null;
}

export class Base {
  private constructor(
    private readonly filters: YamlNode | undefined,
    private readonly formulas: ReadonlyMap<string, string>,
    private readonly views: readonly YamlMap[],
  ) {}

  get viewNames(): string[] {
    return this.views.map((v, i) => scalarOf(v, "name") ?? `View ${i + 1}`);
  }

  /** Reads a base; throws `QueryError` (code `base`) when it isn't one. */
  static parse(yaml: string): Base {
    let root: YamlNode | null;
    try {
      root = readYaml(yaml);
    } catch (e) {
      if (!(e instanceof YamlProblem)) throw e;
      throw invalid(e.tooLarge ? "The base is too large or nested too deeply." : `The base isn't valid YAML: ${e.message}`);
    }

    if (root === null) return new Base(undefined, new Map(), []);
    if (root.kind !== "map") throw invalid("The base is not a map of keys and values.");

    const formulaNode = child(root, "formulas");
    if (formulaNode !== undefined && formulaNode.kind !== "map") throw invalid("formulas is not a map of names and expressions.");
    const formulas = new Map((formulaNode?.entries ?? []).map(([k, v]) => [textOf(k), textOf(v)] as const));

    const viewsNode = child(root, "views");
    if (viewsNode !== undefined && !(viewsNode.kind === "seq" && viewsNode.items.every((v) => v.kind === "map")))
      throw invalid("views is not a list of views.");
    return new Base(child(root, "filters"), formulas, (viewsNode?.items ?? []) as YamlMap[]);
  }

  /** Runs a view (by name; the first when null) over the notes; `now` is "yyyy-MM-ddTHH:mm:ss". */
  run(viewName: string | null, notes: Iterable<QueryNote>, self: QueryNote | null, now: string): QueryResult {
    // A view without a name is called by the name viewNames gives it.
    const names = this.viewNames;
    const index = viewName === null ? 0 : names.indexOf(viewName);
    if (index < 0) throw new QueryError("unknown-view", `The base has no view '${viewName}'.`, 0);
    const view = this.views[index] ?? null;
    const name = view === null ? "Table" : names[index]!;

    const evaluator = new Evaluator(this.formulas);
    const filter: Array<(scope: Scope) => boolean | null> = [];
    if (this.filters !== undefined) filter.push(compileFilter(this.filters, evaluator, 0));
    const own = view === null ? undefined : child(view, "filters");
    if (own !== undefined) filter.push(compileFilter(own, evaluator, 0));

    const orderNode = view === null ? undefined : child(view, "order");
    const columns = orderNode?.kind === "seq" ? orderNode.items.map(textOf) : ["file.name"];
    const cells = columns.map((c) => column(c, evaluator));
    const sortNode = view === null ? undefined : child(view, "sort");
    const sort = sortNode?.kind === "seq"
      ? sortNode.items.map((k) => {
        const property = k.kind === "map" ? scalarOf(k, "property") : null;
        if (k.kind !== "map" || property === null) throw invalid("A sort key needs a property.");
        return { expr: column(property, evaluator), descending: scalarOf(k, "direction")?.toUpperCase() === "DESC" };
      })
      : [];
    const limitText = view === null ? null : scalarOf(view, "limit");
    let limit = Number.POSITIVE_INFINITY;
    if (limitText !== null) {
      const n = wholeNumber(limitText);
      if (n === null || n < 0) throw invalid("limit is not a whole number.");
      limit = n;
    }

    const files = [...notes];
    const vault = new QueryVault(files);
    const rows = files
      .sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))
      .map((note): Scope => ({ note, this: self, now, vault }))
      .filter((scope) => filter.every((f) => f(scope) === true))
      .map((scope) => ({ scope, keys: sort.map((k) => evaluator.evaluate(k.expr, scope)) }));
    // A stable sort (as Array.prototype.sort is): ties keep the notes in path order.
    if (sort.length > 0) rows.sort((x, y) => keyOrder(x.keys, y.keys, sort.map((k) => k.descending)));

    return {
      view: name,
      columns,
      rows: rows.slice(0, limit).map((r) => ({ note: r.scope.note, cells: cells.map((c) => evaluator.evaluate(c, r.scope)) })),
      warnings: evaluator.warnings,
    };
  }
}

// A filter is an expression, or a group: and (all), or (one), not (none of its items). Null is an error, which drops the
// row whatever the group, as && and || pass an error on; the items are read in order, as they read theirs.
function compileFilter(node: YamlNode, evaluator: Evaluator, depth: number): (scope: Scope) => boolean | null {
  if (depth > ExpressionParser.MAX_DEPTH) throw invalid("The filters are nested too deeply.");
  if (node.kind === "scalar") {
    const text = node.value;
    const expr = inExpression(text, () => evaluator.compile(text));
    return (scope) => {
      const value = evaluator.evaluate(expr, scope);
      return value.kind === "error" ? null : truthy(value);
    };
  }

  const entry = node.kind === "map" && node.entries.length === 1 ? node.entries[0]! : null;
  if (entry === null || entry[0].kind !== "scalar" || entry[1].kind !== "seq")
    throw invalid("A filter group is and, or or not, with a list of filters.");

  const kind = entry[0].value;
  const parts = entry[1].items.map((i) => compileFilter(i, evaluator, depth + 1));
  if (kind !== "and" && kind !== "or" && kind !== "not") throw invalid(`'${kind}' is no filter group: use and, or or not.`);

  // and is settled by the first item that fails, or and not by the first that holds.
  const settles = kind !== "and";
  return (scope) => {
    for (const part of parts) {
      const holds = part(scope);
      if (holds === null) return null;
      if (holds === settles) return kind === "or";
    }
    return kind !== "or";
  };
}

// file.x and formula.x are expressions; note.x and any other name are a property, so a name with spaces needs no quotes.
function column(id: string, evaluator: Evaluator): Expr {
  if (id.startsWith("file.") || id.startsWith("formula.")) return inExpression(id, () => evaluator.compile(id));
  const property = id.startsWith("note.") ? id.slice(5) : id;
  return {
    kind: "index",
    target: { kind: "name", name: "note", position: 0 },
    index: { kind: "literal", value: textValue(property), position: 0 },
    position: 0,
  };
}

// Orders rows by their sort keys: within a type as `compare` does, empty values and then errors last in either
// direction, values of different types by type.
function keyOrder(x: readonly Value[], y: readonly Value[], descending: readonly boolean[]): number {
  for (let i = 0; i < descending.length; i++) {
    const [a, b] = [x[i]!, y[i]!];
    if (last(a) + last(b) > 0) {
      if (last(a) === last(b)) continue;
      return last(a) - last(b);
    }

    const order = ordered(a, b);
    if (order !== 0) return descending[i] ? -order : order;
  }
  return 0;
}

// Empty values go after the others, and errors after them, whichever the direction.
const last = (v: Value): number => (v.kind === "null" ? 1 : v.kind === "error" ? 2 : 0);
