// SPDX-License-Identifier: AGPL-3.0-or-later

// A port of the server shell's Tokenizer and Pipeline (src/Kumbu.Core/Sh), state for state. console-fixture.json is
// read by tests on both sides to keep the two in step.

const PIPE = Symbol("pipe");

type Token = string | typeof PIPE;

/** Whether a real shell would act on the syntax at `i` and the shell doesn't support it. Only asked outside quotes. */
function unsupported(input: string, i: number): boolean {
  const c = input[i];
  const next = input[i + 1];
  if (c === "|") return next === "|";
  if (c === "$") return next === "(" || next === "{";
  return "&;<>`()".includes(c);
}

function tokens(line: string): Token[] | null {
  const input = line.trim();
  const result: Token[] = [];
  let current = "";
  let inToken = false;
  let state: "normal" | "single" | "double" = "normal";

  const endWord = () => {
    if (!inToken) return;
    result.push(current);
    current = "";
    inToken = false;
  };

  for (let i = 0; i < input.length; i++) {
    const c = input[i];
    const next = input[i + 1];
    switch (state) {
      case "normal":
        // An unquoted line break would start a second command; the shell runs exactly one per call.
        if (c === "\n" || c === "\r" || (c === "\\" && (next === "\n" || next === "\r"))) return null;
        if (unsupported(input, i)) return null;

        if (c === " " || c === "\t") endWord();
        else if (c === "|") {
          endWord();
          result.push(PIPE);
        } else if (c === "'") {
          state = "single";
          inToken = true;
        } else if (c === '"') {
          state = "double";
          inToken = true;
        } else if (c === "\\" && next !== undefined) {
          current += next;
          i++;
          inToken = true;
        } else {
          current += c;
          inToken = true;
        }
        break;
      case "single":
        if (c === "'") state = "normal";
        else current += c;
        break;
      case "double":
        if (c === '"') state = "normal";
        else if (c === "\\" && (next === '"' || next === "\\")) {
          current += next;
          i++;
        } else current += c;
        break;
    }
  }

  if (state !== "normal") return null;
  endWord();
  return result;
}

/** The stages of a pipeline as `Pipeline.TryParse` gives them; `null` for a line the shell refuses, `[]` for an empty one. */
export function tokenise(line: string): string[][] | null {
  const all = tokens(line);
  if (all === null) return null;
  if (all.length === 0) return [];

  const stages: string[][] = [];
  let current: string[] = [];
  for (const token of all) {
    if (token !== PIPE) {
      current.push(token);
      continue;
    }
    if (current.length === 0) return null;
    stages.push(current);
    current = [];
  }
  if (current.length === 0) return null;
  stages.push(current);
  return stages;
}

/** The word as the shell reads it back unchanged: bare if it can be, else quoted. */
export function quote(word: string): string {
  if (/^[\p{L}\p{N}_\-./:#,=+@%]+$/u.test(word)) return word;
  if (!word.includes("'")) return `'${word}'`;
  return `"${word.replace(/[\\"]/g, "\\$&")}"`;
}

export function joinLine(stages: string[][]): string {
  return stages.map((stage) => stage.map(quote).join(" ")).join(" | ");
}
