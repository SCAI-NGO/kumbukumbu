// SPDX-License-Identifier: AGPL-3.0-or-later
import { consoleCall } from "./api";
import { quote } from "./tokenise";

// What a folder holds, for completion: one `ls` per folder, kept for a short while and forgotten whenever a command
// has run (it may have changed things) and when the console resets (a discreet vault's names go with its hiding).

/** How long a listing is trusted; typing fast asks the same folder again and again. */
const TTL = 10_000;

let cache = new Map<string, { at: number; entries: Promise<string[] | null> }>();
let epoch = 0;

/** The names `ls` printed: folders with their `/`; the notes about shares, damage and locked vaults left out. */
function parse(output: string): string[] {
  const names: string[] = [];
  for (const line of output.split("\n")) {
    if (line.trim() === "" || line.startsWith("(")) continue;
    // A shared vault carries its owner and level, an attachment its summary after two spaces.
    const entry = line.replace(/ \(shared by .*\)$/, "");
    const gap = entry.indexOf("  ");
    names.push(gap < 0 ? entry : entry.slice(0, gap));
  }
  return names;
}

async function fetch(folder: string): Promise<string[] | null> {
  const mine = epoch;
  try {
    const view = await consoleCall({ call: "sh", command: folder === "/" ? "ls" : `ls ${quote(folder)}` }, new AbortController().signal);
    if (view.isError || mine !== epoch) return null;
    return parse(view.parts.map((part) => (part.type === "image" ? "" : part.text ?? "")).join("\n"));
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
