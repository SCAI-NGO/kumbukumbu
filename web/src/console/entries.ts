// SPDX-License-Identifier: AGPL-3.0-or-later
import { consoleCall } from "./api";
import { quote } from "./tokenise";

// What a folder holds, for completion: one `ls` per folder, kept for a short while and forgotten whenever a command
// has run (it may have changed things) and when the console resets (a discreet vault's names go with its hiding).

/** How long a listing is trusted; typing fast asks the same folder again and again. */
const TTL = 10_000;

let cache = new Map<string, { at: number; entries: Promise<string[] | null> }>();
let epoch = 0;

/**
 * The names `ls` printed: folders with their `/`; the notes about damage and locked vaults left out, which are whole
 * lines in parentheses (a name like `(draft).md` is not). Null where `folder` is itself a note or an attachment:
 * `ls` then prints that path, with a summary after two spaces for an attachment.
 */
function parse(output: string, folder: string): string[] | null {
  const names: string[] = [];
  for (const line of output.split("\n")) {
    // A shared vault carries its owner and level, an attachment its summary after two spaces.
    const entry = line.replace(/ \(shared by .*\)$/, "");
    if (entry.trim() === "" || /^\(.*\)$/.test(entry)) continue;
    if (entry === folder || entry.startsWith(`${folder}  `)) return null;
    const gap = entry.indexOf("  ");
    names.push(gap < 0 ? entry : entry.slice(0, gap));
  }
  return names;
}

/** A vault is listed with its archive (`-a`), so that completion knows whether it has one. */
const lsCommand = (folder: string) =>
  folder === "/" ? "ls" : `ls ${folder.split("/").length === 2 ? "-a " : ""}${quote(folder)}`;

async function fetch(folder: string): Promise<string[] | null> {
  const mine = epoch;
  try {
    const view = await consoleCall({ call: "sh", command: lsCommand(folder) }, new AbortController().signal);
    if (view.isError || mine !== epoch) return null;
    return parse(view.parts.map((part) => (part.type === "image" ? "" : part.text ?? "")).join("\n"), folder);
  } catch {
    return null;
  }
}

/** The entries of an absolute folder, or null where `ls` failed. */
export function listFolder(folder: string): Promise<string[] | null> {
  const hit = cache.get(folder);
  if (hit && Date.now() - hit.at < TTL) return hit.entries;
  const entries = fetch(folder);
  cache.set(folder, { at: Date.now(), entries });
  return entries;
}

/** Forgets every listing, and drops the answers still on their way. */
export function forgetEntries() {
  cache = new Map();
  epoch++;
}
