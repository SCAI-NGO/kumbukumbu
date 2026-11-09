// SPDX-License-Identifier: AGPL-3.0-or-later
import { joinLine, tokenise } from "./tokenise";

// Spec §7: the console turns relative paths into absolute ones before it sends a line. Comfort only, never a
// boundary: the server checks every path as for any other caller.

/** What stands in for a missing path when the command starts the pipeline. */
type Missing = "cwd" | "vault";

interface Found {
  /** Positions in the operands that are paths. */
  paths: number[];
  /** Added when no path was given, unless the stage is right of a pipe or the working directory is `/`. */
  missing?: Missing;
}

export interface CommandEntry {
  /** The options that take a value, as `-n` and `--lines`, read from the command's `Spec`. */
  values: readonly string[];
  /** `-5` means `-n 5`: `digitsMean` in `Options.TryParse`. */
  digits?: boolean;
  /** Which operands are paths; `options` holds every option given, as `-e` or `--files`. */
  find(operands: string[], options: Set<string>): Found;
}

const all = (operands: string[]) => operands.map((_, i) => i);
const first = (operands: string[]) => (operands.length > 0 ? [0] : []);

/** A path in a place where a bare word means something else (a query word, a tag). */
const looksLikePath = (word: string) => word.startsWith("/") || word.startsWith("./") || word.startsWith("../");

const oneDirectory: CommandEntry = { values: [], find: (o) => ({ paths: all(o), missing: "cwd" }) };
const oneNote = (values: readonly string[] = []): CommandEntry => ({ values, find: (o) => ({ paths: first(o) }) });
// Filters: without a path they read the pipe, so nothing is added.
const filter = (values: readonly string[], digits = false): CommandEntry => ({
  values,
  digits,
  find: (o) => ({ paths: all(o) }),
});

export const commandTable: Record<string, CommandEntry> = {
  ls: oneDirectory,
  trash: oneDirectory,
  lscp: oneDirectory,
  // CommandArgs.TryTakeVersion, then no options.
  cat: oneNote(["--version"]),
  outline: oneNote(["--version"]),
  links: oneNote(["--version"]),
  backlinks: oneNote(),
  img: oneNote(["--size", "--crop"]),
  rg: {
    values: [
      "-m", "--max-count", "-e", "--regexp", "-A", "--after-context", "-B", "--before-context", "-C", "--context",
      "--version", "--tag",
    ],
    find: (o, options) => {
      // The regex comes from -e or --files needs none; otherwise it is the first operand.
      const allPaths = options.has("-e") || options.has("--regexp") || options.has("--files");
      if (allPaths) return { paths: all(o), missing: "cwd" };
      // Without a regex, an added path would become one.
      return o.length === 0 ? { paths: [] } : { paths: all(o).slice(1), missing: "cwd" };
    },
  },
  semsearch: {
    values: ["-n", "--max", "--tag"],
    // Semsearch.cs: the last of several operands is the scope; the rest, unquoted, is the query.
    find: (o) => {
      if (o.length === 0) return { paths: [] };
      // A lone absolute operand is the shell's "missing query" error, not a query to scope.
      if (o.length === 1 && o[0].startsWith("/")) return { paths: [] };
      return o.length > 1 && looksLikePath(o[o.length - 1]) ? { paths: [o.length - 1] } : { paths: [], missing: "cwd" };
    },
  },
  tags: {
    values: [],
    find: (o) => {
      // TagsCommand.cs tells them apart by the leading '/': an absolute one is the path wherever it stands.
      const absolute = o.findIndex((w) => w.startsWith("/"));
      if (absolute >= 0) return { paths: [absolute] };
      if (o.length >= 2) return { paths: [1] };
      if (o.length === 1 && looksLikePath(o[0])) return { paths: [0] };
      return { paths: [], missing: "cwd" };
    },
  },
  history: { values: ["--since"], find: (o) => ({ paths: first(o), missing: "cwd" }) },
  board: { values: ["--column", "--tag"], find: (o) => ({ paths: first(o), missing: "vault" }) },
  pdf: {
    values: ["--size", "--crop"],
    find: (o) => (o[0] === "info" ? { paths: o.length > 1 ? [1] : [] } : { paths: first(o) }),
  },
  head: filter(["-n", "--lines", "--version"], true),
  tail: filter(["-n", "--lines", "--version"], true),
  wc: filter(["--version"]),
  sort: filter(["--version"]),
  diff: {
    values: ["--checkpoint"],
    // DiffCommand.cs: with two operands the second is a note or a version token, which has neither '/' nor '.'.
    find: (o) => ({ paths: o.length === 2 && /[/.]/.test(o[1]) ? [0, 1] : first(o) }),
  },
  // PublishCommand.cs: operand 0 is the subcommand, operand 1 the path; only ls has a default, the working directory.
  publish: {
    values: [],
    find: (o) => ({ paths: o.length > 1 ? [1] : [], missing: o[0] === "ls" ? "cwd" : undefined }),
  },
  // Df.cs: without a vault, every vault of one's own; Du.cs: without a path, every vault, so the working directory goes in.
  df: { values: [], find: (o) => ({ paths: first(o) }) },
  du: { values: ["-d", "--max-depth"], find: (o) => ({ paths: first(o), missing: "cwd" }) },
  uname: { values: [], find: () => ({ paths: [] }) },
  help: { values: [], find: () => ({ paths: [] }) },
};

/**
 * Splits a stage's arguments as `Options.TryParse` would, for the positions of its operands; null when an option
 * lacks its value, so the shell's own error answers. Unknown options count as flags: the shell refuses them anyway.
 */
function parse(args: string[], entry: CommandEntry): { operands: number[]; options: Set<string> } | null {
  // Shell.TakeCheckpoint takes --checkpoint out of every reader's arguments before the command sees them.
  const values = new Set([...entry.values, "--checkpoint"]);
  const operands: number[] = [];
  const options = new Set<string>();

  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === "--") {
      for (let j = i + 1; j < args.length; j++) operands.push(j);
      break;
    }

    if (arg.startsWith("--")) {
      const eq = arg.indexOf("=");
      const name = eq < 0 ? arg : arg.slice(0, eq);
      options.add(name);
      if (eq < 0 && values.has(name) && ++i >= args.length) return null;
      continue;
    }

    if (arg.length < 2 || arg[0] !== "-") {
      operands.push(i);
      continue;
    }

    if (entry.digits && /^-\d+$/.test(arg)) continue;

    for (let j = 1; j < arg.length; j++) {
      const short = `-${arg[j]}`;
      options.add(short);
      if (!values.has(short)) continue;
      if (j + 1 === arg.length && ++i >= args.length) return null;
      break;
    }
  }

  return { operands, options };
}

/** `path` made absolute against `cwd`, with `.` and `..` resolved; a `#section` after `.md` stays as it is. */
export function resolvePath(path: string, cwd: string): string {
  // SectionAddress.Split: the section starts after ".md#"; a '#' elsewhere is part of a name (a#b.png).
  const at = path.indexOf(".md#");
  const [file, section] = at < 0 ? [path, ""] : [path.slice(0, at + 3), path.slice(at + 3)];

  const segments = file.startsWith("/") ? [] : cwd.split("/").filter(Boolean);
  for (const segment of file.split("/")) {
    if (segment === "" || segment === ".") continue;
    if (segment === "..") segments.pop();
    else segments.push(segment);
  }

  return `/${segments.join("/")}${section}`;
}

const known = (name: string) => Object.hasOwn(commandTable, name);

function resolveStage(stage: string[], cwd: string, starts: boolean): string[] {
  const entry = commandTable[stage[0]];

  const args = stage.slice(1);
  const parsed = parse(args, entry);
  if (!parsed) return stage;

  const operands = parsed.operands.map((i) => args[i]);
  const found = entry.find(operands, parsed.options);
  const resolved = [...args];
  for (const p of found.paths) resolved[parsed.operands[p]] = resolvePath(operands[p], cwd);

  if (found.paths.length === 0 && found.missing && starts && cwd !== "/") {
    resolved.push(found.missing === "vault" ? `/${cwd.split("/").filter(Boolean)[0]}` : cwd);
  }

  return [stage[0], ...resolved];
}

/** The line to send for `line` typed in `cwd`: as typed if no path changed, else re-joined with its paths absolute. */
export function resolveLine(line: string, cwd: string): string {
  const stages = tokenise(line);
  // A command the table doesn't know, in any stage, sends the whole line as typed: the shell's error or the
  // refusal answers it.
  if (!stages || !stages.every((stage) => known(stage[0]))) return line;

  const resolved = stages.map((stage, i) => resolveStage(stage, cwd, i === 0));
  const changed = resolved.some(
    (stage, i) => stage.length !== stages[i].length || stage.some((word, j) => word !== stages[i][j]),
  );
  return changed ? joinLine(resolved) : line;
}
