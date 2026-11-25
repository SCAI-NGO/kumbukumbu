// SPDX-License-Identifier: AGPL-3.0-or-later
// A named query (Kumbu.Core Queries/Query): a query note (frontmatter `type: query`, a `params:` schema, prose and one or
// more ```base blocks) or a .base file. A call's arguments are bound into `this` over the query note's own properties,
// so the same base renders in Obsidian through the note it is embedded in. The cases are in tests/query-conformance/queries.
import { fences, frontmatterEnd, splitLines, trim } from "../links/text";
import { Base, type QueryResult } from "./base";
import { QueryError, type QueryWarning } from "./errors";
import { QueryNote } from "./note";
import { Properties } from "./properties";
import { PropertyTypes, type PropertyType } from "./propertyTypes";
import { NULL, listValue, textValue, type Value } from "./value";
import { child, readYaml, YamlProblem, type YamlNode } from "./yamlTree";

/** A parameter a query note declares: its type, whether a call must give it, the value it takes when left out, the
 * values it is held to, and what it means. */
export type QueryParam = {
  readonly name: string;
  readonly type: PropertyType;
  readonly required: boolean;
  readonly default: Value | null;
  readonly options: readonly string[];
  readonly description: string | null;
};

type ViewOfBase = { readonly base: Base; readonly view: string | null };
type YamlMap = Extract<YamlNode, { kind: "map" }>;

const invalid = (message: string) => new QueryError("params", message, 0);

const isBaseFile = (path: string): boolean => path.toLowerCase().endsWith(".base");

/** Whether a note is meant as a query, read cheaply: `type: query` in its frontmatter, or a .base file. */
function isQuery(note: QueryNote): boolean {
  const type = note.properties.values.get("type");
  return isBaseFile(note.path) || (type?.kind === "text" && type.text === "query");
}

/** The name of a type as Obsidian writes it ("date"). */
const name = (type: PropertyType): string => type;

export class Query {
  private constructor(
    private readonly note: QueryNote,
    /** Whether the note says it is a query (`type: query`), or is a .base file: what `query ls` lists. */
    readonly isMarked: boolean,
    readonly params: readonly QueryParam[],
    private readonly views: readonly ViewOfBase[],
  ) {}

  static readonly isBaseFile = isBaseFile;
  static readonly isQuery = isQuery;
  static readonly name = name;

  /** The views of its bases, in the order they are written; the first is the one a call without a view runs. */
  get viewNames(): string[] {
    return this.views.flatMap((v) => (v.view === null ? [] : [v.view]));
  }

  /** Reads a query; throws `QueryError` (code `no-base`, `params` or `base`) when it can't run. */
  static read(note: QueryNote): Query {
    if (isBaseFile(note.path)) return new Query(note, true, [], viewsOf([Base.parse(note.content)]));

    const blocks = blocksOf(note.content);
    if (blocks.length === 0) throw new QueryError("no-base", "The note has no ```base block to run.", 0);
    return new Query(note, isQuery(note), parameters(note), viewsOf(blocks.map((b) => Base.parse(b))));
  }

  /** Runs a view (by name; the first when null) with the call's arguments: each name a parameter, each value a text read
   * as the parameter's type (a list takes one per item). Throws `QueryError`. */
  run(view: string | null, args: ReadonlyMap<string, readonly string[]>, notes: Iterable<QueryNote>, now: string): QueryResult {
    const self = this.bind(args, false);
    const found = view === null ? this.views[0]! : this.views.find((v) => v.view === view);
    if (found === undefined) throw new QueryError("unknown-view", `The query has no view '${view}'.`, 0);
    return found.base.run(found.view, notes, self, now);
  }

  /** Compiles every view without running it (no note is read, no argument needed): by view, in order, the error of each
   * view that can't run, or the warnings of one that runs with them; empty when all run without. */
  check(now: string): Array<{ view: string | null; error: QueryError | null; warnings: readonly QueryWarning[] }> {
    const self = this.bind(new Map(), true);
    const problems: Array<{ view: string | null; error: QueryError | null; warnings: readonly QueryWarning[] }> = [];
    for (const { base, view } of this.views) {
      try {
        const { warnings } = base.run(view, [], self, now);
        if (warnings.length > 0) problems.push({ view, error: null, warnings });
      } catch (e) {
        if (!(e instanceof QueryError)) throw e;
        problems.push({ view, error: e, warnings: [] });
      }
    }
    return problems;
  }

  // The query note as this: its own properties, under the arguments and the defaults of what wasn't given.
  private bind(args: ReadonlyMap<string, readonly string[]>, check: boolean): QueryNote {
    const values = new Map(this.note.properties.values);
    for (const given of args.keys()) {
      if (!this.params.some((p) => p.name === given)) {
        throw new QueryError("unknown-param", this.params.length === 0
          ? `The query takes no parameters, not '${given}'.`
          : `The query has no parameter '${given}'; it takes ${this.params.map((p) => p.name).join(", ")}.`, 0);
      }
    }

    for (const param of this.params) {
      const given = args.get(param.name);
      if (given !== undefined) values.set(param.name, argument(param, given));
      else if (param.default !== null) values.set(param.name, param.default);
      else if (param.required && !check) throw new QueryError("missing-param", `The query needs the parameter '${param.name}'${describe(param)}.`, 0);
      else values.set(param.name, NULL);
    }

    return new QueryNote(this.note.path, this.note.content, { values, problem: null }, this.note.size, this.note.created, this.note.modified);
  }
}

function argument(param: QueryParam, given: readonly string[]): Value {
  if (param.type === "multitext") return listValue(given.map((g) => checked(param, g, textValue(g))));
  if (given.length !== 1) throw new QueryError("param-value", `The parameter '${param.name}' takes one value, not ${given.length}.`, 0);
  return checked(param, given[0]!, Properties.typed(given[0]!, param.type));
}

function checked(param: QueryParam, raw: string, value: Value | null): Value {
  if (value === null) throw new QueryError("param-value", `'${raw}' is no value for the parameter '${param.name}'${describe(param)}.`, 0);
  if (param.options.length > 0 && !param.options.includes(raw))
    throw new QueryError("param-value", `'${raw}' is no value for the parameter '${param.name}': it is one of ${param.options.join(", ")}.`, 0);
  return value;
}

const describe = (param: QueryParam): string => `, ${name(param.type)}${param.description === null ? "" : `: ${param.description}`}`;

// A base without views runs as a table of every note (its view's name null), as Obsidian shows it.
function viewsOf(bases: readonly Base[]): ViewOfBase[] {
  const all = bases.flatMap((base) => base.viewNames.map((view) => ({ base, view })));
  return all.length > 0 ? all : [{ base: bases[0]!, view: null }];
}

const BASE_FENCE = /^ {0,3}(`{3,}|~{3,})\s*base\s*$/;

// The ```base blocks at the top level of the note, as CommonMark reads its fences; an unclosed one runs to the end.
function blocksOf(content: string): string[] {
  const lines = splitLines(content);
  const { code, open } = fences(lines);
  const blocks: string[] = [];
  for (let i = frontmatterEnd(lines); i < lines.length; i++) {
    if (!code[i] || open[i] !== null || !BASE_FENCE.test(lines[i]!)) continue;
    let end = i + 1;
    while (end < lines.length && !(code[end] && (end + 1 === lines.length ? open[end] !== null && closes(lines[end]!, open[end]!) : open[end + 1] === null))) end++;
    blocks.push(lines.slice(i + 1, end).join("\n"));
    i = end;
  }
  return blocks;
}

function closes(line: string, fence: string): boolean {
  const text = trim(line);
  return text.startsWith(fence) && [...text].every((c) => c === fence[0]);
}

const LETTER = /^[\p{L}_]$/u;
const LETTER_OR_DIGIT = /^[\p{L}\p{N}_]$/u;

// A name as .NET reads ^[\p{L}_][\p{L}\p{N}_]*$: by UTF-16 unit, and `$` also matches before a final line break.
function isParamName(text: string): boolean {
  const units = (text.endsWith("\n") ? text.slice(0, -1) : text).split("");
  return units.length > 0 && LETTER.test(units[0]!) && units.every((u) => LETTER_OR_DIGIT.test(u));
}

// The params: map of the frontmatter, each entry a map of type, required, default, options and description.
function parameters(note: QueryNote): QueryParam[] {
  if (!note.properties.values.has("params")) return [];
  if (note.properties.problem !== null) throw invalid(note.properties.problem);
  const lines = splitLines(note.content);
  let root: YamlNode | null;
  try {
    root = readYaml(lines.slice(1, frontmatterEnd(lines) - 1).join("\n"));
  } catch (e) {
    if (e instanceof YamlProblem) throw invalid(e.message);
    throw e;
  }
  const declared = root?.kind === "map" ? child(root, "params") : undefined;
  if (declared?.kind !== "map") throw invalid("params is not a map of parameters by name.");

  return declared.entries.map(([key, value]) => {
    const paramName = key.kind === "scalar" ? key.value : "";
    if (!isParamName(paramName)) throw invalid(`'${paramName}' is no parameter name: a letter or _, then letters, digits or _.`);
    if (value.kind !== "map") throw invalid(`The parameter '${paramName}' is not a map of type, required, default, options and description.`);
    return parameter(paramName, value);
  });
}

const SPEC_KEYS = new Set(["type", "required", "default", "options", "description"]);

function parameter(paramName: string, spec: YamlMap): QueryParam {
  const scalar = (key: string): string | null => {
    const node = child(spec, key);
    if (node === undefined) return null;
    if (node.kind !== "scalar") throw invalid(`The ${key} of the parameter '${paramName}' is not a single value.`);
    return node.value;
  };

  for (const [key] of spec.entries) {
    if (key.kind !== "scalar" || !SPEC_KEYS.has(key.value))
      throw invalid(`The parameter '${paramName}' has '${key.kind === "scalar" ? key.value : ""}', which is none of type, required, default, options and description.`);
  }

  const typeName = scalar("type");
  let type: PropertyType = "text";
  if (typeName !== null) {
    const named = PropertyTypes.named(typeName);
    if (named === null || typeName === "aliases" || typeName === "tags")
      throw invalid(`'${typeName}' is no type for the parameter '${paramName}': text, multitext, number, checkbox, date or datetime.`);
    type = named;
  }

  const requiredText = scalar("required");
  if (requiredText !== null && requiredText !== "false" && requiredText !== "true")
    throw invalid(`required of the parameter '${paramName}' is '${requiredText}', not true or false.`);
  const required = requiredText === "true";

  const optionsNode = child(spec, "options");
  if (optionsNode !== undefined && !(optionsNode.kind === "seq" && optionsNode.items.every((c) => c.kind === "scalar")))
    throw invalid(`The options of the parameter '${paramName}' are not a list of values.`);
  const options = (optionsNode?.kind === "seq" ? optionsNode.items : []).map((c) => (c as { value: string }).value);

  const param: QueryParam = { name: paramName, type, required, default: null, options, description: scalar("description") };
  const defaultNode = child(spec, "default");
  if (defaultNode === undefined) return param;
  if (required) throw invalid(`The parameter '${paramName}' is required and has a default; a default makes it optional.`);

  let given: string[];
  if (defaultNode.kind === "scalar") given = [defaultNode.value];
  else if (defaultNode.kind === "seq" && type === "multitext" && defaultNode.items.every((c) => c.kind === "scalar"))
    given = defaultNode.items.map((c) => (c as { value: string }).value);
  else throw invalid(`The default of the parameter '${paramName}' doesn't fit its type ${name(type)}.`);

  try {
    return { ...param, default: argument(param, given) };
  } catch (e) {
    if (e instanceof QueryError) throw invalid(`The default of the parameter '${paramName}' doesn't fit: ${e.message}`);
    throw e;
  }
}
