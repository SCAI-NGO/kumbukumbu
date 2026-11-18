// SPDX-License-Identifier: AGPL-3.0-or-later
import { consoleCommands, shCommands } from "./help";
import { resolvePath } from "./paths";
import { quote, unsupported } from "./tokenise";

export interface Completion {
  /** The word under the cursor, as typed: its first character up to the cursor. */
  replaceFrom: number;
  replaceTo: number;
  /** Display names: folders end with `/`. */
  matches: string[];
  /** What goes in place of the range, quoted as the tokeniser reads it; null where there is nothing to add. */
  insert: string | null;
}

const commands = [...new Set([...consoleCommands, ...shCommands])].sort();

interface Word {
  start: number;
  /** The word as the shell reads it so far. */
  value: string;
  /** The words of its stage before it. */
  before: string[];
}

/** Where the word at the end of `text` starts, read as the tokeniser reads; null for syntax the shell refuses. */
function wordAt(text: string): Word | null {
  let state: "normal" | "single" | "double" = "normal";
  let words: string[] = [];
  let value = "";
  let start = 0;
  let inToken = false;
  const begin = (i: number) => {
    if (!inToken) start = i;
    inToken = true;
  };
  const end = () => {
    if (!inToken) return;
    words.push(value);
    value = "";
    inToken = false;
  };

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    const next = text[i + 1];
    if (state === "normal") {
      if (c === "\n" || c === "\r" || (c === "\\" && (next === "\n" || next === "\r"))) return null;
      if (unsupported(text, i)) return null;
      if (c === " " || c === "\t") end();
      else if (c === "|") {
        end();
        words = [];
      } else if (c === "'" || c === '"') {
        begin(i);
        state = c === "'" ? "single" : "double";
      } else if (c === "\\" && next !== undefined) {
        begin(i);
        value += next;
        i++;
      } else {
        begin(i);
        value += c;
      }
    } else if (c === (state === "single" ? "'" : '"')) state = "normal";
    else if (state === "double" && c === "\\" && (next === '"' || next === "\\")) {
      value += next;
      i++;
    } else value += c;
  }

  return { start: inToken ? start : text.length, value, before: words };
}

const common = (names: string[]) => names.reduce((prefix, name) => {
  let n = 0;
  while (n < prefix.length && prefix[n] === name[n]) n++;
  return prefix.slice(0, n);
});

/** A quote left open, as zsh leaves one while a word is only part-way: more can be typed inside it. */
const open = (word: string) => (word.includes("'") ? `"${word.replace(/[\\"]/g, "\\$&")}` : `'${word}`);

/**
 * What Tab completes at `cursor` in `line`: a command as the first word of a stage, else a path in the folder the
 * word's directory part names (relative to `cwd`). `list` gives a folder's entries, folders with their `/`, or null.
 */
export async function complete(
  line: string,
  cursor: number,
  cwd: string,
  list: (folder: string) => Promise<string[] | null>,
): Promise<Completion> {
  const none: Completion = { replaceFrom: cursor, replaceTo: cursor, matches: [], insert: null };
  const word = wordAt(line.slice(0, cursor));
  if (!word) return none;
  const range = { replaceFrom: word.start, replaceTo: cursor };

  if (word.before.length === 0) {
    const matches = commands.filter((c) => c.startsWith(word.value));
    return { ...range, matches, insert: matches.length === 1 ? `${matches[0]} ` : null };
  }
  // An option, not a path; no listing for it.
  if (word.value.startsWith("-")) return none;

  const slash = word.value.lastIndexOf("/") + 1;
  const dir = word.value.slice(0, slash);
  const base = word.value.slice(slash);
  const folder = resolvePath(dir || ".", cwd);
  const entries = await list(folder);
  if (!entries) return none;

  const names = new Set(entries);
  // ls leaves a vault's archive out; it is offered once its first letter is typed.
  if (base.startsWith("_") && folder.split("/").filter(Boolean).length === 1) names.add("_archive/");
  // `..` completes to the folder above, except at the root, which has none.
  if (base.startsWith(".") && "..".startsWith(base) && folder !== "/") names.add("../");

  const foldersOnly = word.before[0] === "cd";
  const matches = [...names].filter((n) => n.startsWith(base) && (!foldersOnly || n.endsWith("/")));
  if (matches.length === 0) return { ...range, matches, insert: null };

  if (matches.length === 1) {
    const name = matches[0];
    return { ...range, matches, insert: quote(dir + name) + (name.endsWith("/") ? "" : " ") };
  }

  const prefix = common(matches);
  if (prefix.length <= base.length) return { ...range, matches, insert: null };
  const part = dir + prefix;
  const needsQuote = matches.some((m) => quote(dir + m) !== dir + m);
  return { ...range, matches, insert: needsQuote ? open(part) : part };
}
