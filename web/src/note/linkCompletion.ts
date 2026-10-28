// SPDX-License-Identifier: AGPL-3.0-or-later
import type { TreeFolder } from "../api/notes";
import { fileKindOf } from "../api/paths";

/** An open `[[` before the caret: where it starts (at the first `[`) and what has been typed after it. */
export type LinkQuery = { start: number; query: string };

/**
 * The `[[` the caret is in, if any: on the caret's line, not closed yet, and before a `|` or `#` (an alias or a
 * heading is past the note's name, so the list has nothing more to offer there).
 */
export function linkQuery(value: string, caret: number): LinkQuery | null {
  const line = value.slice(value.lastIndexOf("\n", caret - 1) + 1, caret);
  const open = line.lastIndexOf("[[");
  if (open < 0) return null;
  const query = line.slice(open + 2);
  if (/[[\]|#]/.test(query)) return null;
  return { start: caret - line.length + open, query };
}

/** Every note and attachment path of a tree (`/Work/projects/plan.md`, `/Work/shot.png`). */
export const notePaths = (folder: TreeFolder): string[] => [...folder.notes.map((n) => n.path), ...folder.folders.flatMap(notePaths)];

/** `/Work/projects/plan.md` → `projects/plan`: the path in its vault without `.md` (an attachment keeps its
 * extension), as a link writes it. */
const linkPath = (path: string) => path.split("/").slice(2).join("/").replace(/\.md$/i, "");

/**
 * The shortest link that leads to `path` alone, by the server's rule (`LinkResolver`: the note whose path ends
 * with the link, ignoring case): the name, else as many folders in front of it as it takes to tell it apart.
 */
export function linkTarget(path: string, all: string[]): string {
  const segments = linkPath(path).split("/");
  const others = all.filter((p) => p !== path).map((p) => `/${linkPath(p).toLowerCase()}`);
  for (let k = 1; k <= segments.length; k++) {
    const suffix = segments.slice(-k).join("/");
    if (!others.some((o) => o.endsWith(`/${suffix.toLowerCase()}`))) return suffix;
  }
  // `a/b` next to `x/a/b`: only the path from the vault's root names it alone.
  return `/${segments.join("/")}`;
}

/**
 * Whether a wiki link can name the note: `#` starts a heading, `|` an alias, and `[` `]` end the link, in any
 * form of it (name or full path), so a note with one of them in its path can't be linked to and isn't offered.
 */
export const linkable = (path: string) => !/[#|[\]]/.test(linkPath(path));

const MAX = 8;

/**
 * The notes (and attachments) for a query, best first: names starting with it, then names containing it, then
 * notes whose folder contains it; each group in text order. An empty query lists the vault's notes in text order.
 * Notes no link can name (`linkable`) are left out.
 */
export function rankNotes(paths: string[], query: string): string[] {
  const q = query.trim().toLowerCase();
  const name = (p: string) => p.slice(p.lastIndexOf("/") + 1).replace(/\.md$/i, "").toLowerCase();
  const rank = (p: string) => {
    if (name(p).startsWith(q)) return 0;
    if (name(p).includes(q)) return 1;
    if (linkPath(p).toLowerCase().includes(q)) return 2;
    return -1;
  };
  return paths
    .filter(linkable)
    .map((p) => ({ p, r: rank(p) }))
    .filter(({ r }) => r >= 0)
    .sort((a, b) => a.r - b.r || name(a.p).localeCompare(name(b.p)) || a.p.localeCompare(b.p))
    .slice(0, MAX)
    .map(({ p }) => p);
}

/** Whether a link to `path` is written as an embed: an image shows in place (`![[shot.png]]`), a PDF is linked. */
export const embedsAsImage = (path: string) => fileKindOf(path) === "image";

/**
 * The edit that completes the link: `[[que` (and a `]]` right after the caret, as the toolbar's button leaves
 * it) becomes `[[target]]`, with the caret after it; with `embed`, `![[target]]`, unless the `!` is typed already.
 */
export function completeLink(value: string, caret: number, at: LinkQuery, target: string, embed = false) {
  const end = value.startsWith("]]", caret) ? caret + 2 : caret;
  const text = `${embed && value[at.start - 1] !== "!" ? "!" : ""}[[${target}]]`;
  return { start: at.start, end, text, selStart: at.start + text.length, selEnd: at.start + text.length };
}
