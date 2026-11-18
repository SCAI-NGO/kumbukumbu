// SPDX-License-Identifier: AGPL-3.0-or-later
import type { TreeFolder } from "../api/notes";
import { fileKindOf, folderOf, titleOf } from "../api/paths";
import { wordMatch } from "../palette/filter";
import { blockList } from "./blocks";
import { outline } from "./outline";
import { splitFrontmatter } from "./properties";

/**
 * What the caret is completing, and where the text it replaces starts: a wiki link's note (at the first `[`), a
 * heading or block of the note it names (`note` as typed; "" for the open note, `[[#`), or a Markdown link's
 * destination (just after `](`), a note or a heading of the note it names (`note` its path as typed).
 */
export type LinkQuery =
  | { kind: "note"; start: number; query: string }
  | { kind: "heading" | "block"; start: number; note: string; query: string }
  | { kind: "markdown"; start: number; query: string }
  | { kind: "markdownHeading"; start: number; note: string; query: string };

/**
 * The link the caret is in, if any, on the caret's line and not closed yet: an open `[[` before a `|` (an alias is
 * past what the list offers) and before a second `#`; else a `](` whose `[text]` is on the line, before a `)`.
 */
export function linkQuery(value: string, caret: number): LinkQuery | null {
  const line = value.slice(value.lastIndexOf("\n", caret - 1) + 1, caret);
  const lineStart = caret - line.length;
  const open = line.lastIndexOf("[[");
  const typed = open < 0 ? "" : line.slice(open + 2);
  if (open >= 0 && !/[[\]|]/.test(typed)) {
    const start = lineStart + open;
    const hash = typed.indexOf("#");
    if (hash < 0) return { kind: "note", start, query: typed };
    const note = typed.slice(0, hash);
    const rest = typed.slice(hash + 1);
    if (rest.startsWith("^")) return /[#^]/.test(rest.slice(1)) ? null : { kind: "block", start, note, query: rest.slice(1) };
    return rest.includes("#") ? null : { kind: "heading", start, note, query: rest };
  }
  const close = line.lastIndexOf("](");
  // `]](` closes a wiki link, not a Markdown link's text.
  if (close < 0 || !line.slice(0, close).includes("[") || line[close - 1] === "]") return null;
  const destination = line.slice(close + 2);
  if (destination.includes(")")) return null;
  const start = lineStart + close + 2;
  const hash = destination.indexOf("#");
  if (hash < 0) return { kind: "markdown", start, query: destination };
  const note = destination.slice(0, hash);
  const query = destination.slice(hash + 1);
  return /\.md$/i.test(note) && !/^\^|#/.test(query) ? { kind: "markdownHeading", start, note, query } : null;
}

/** Every note and attachment path of a tree (`/Work/projects/plan.md`, `/Work/shot.png`). */
export const notePaths = (folder: TreeFolder): string[] => [...folder.notes.map((n) => n.path), ...folder.folders.flatMap(notePaths)];

/** `/Work/projects/plan.md` → `projects/plan`: the path in its vault without `.md` (an attachment keeps its
 * extension), as a link writes it. */
const linkPath = (path: string) => path.split("/").slice(2).join("/").replace(/\.md$/i, "");

/**
 * The shortest link that leads to `path` alone, by the server's rule (`LinkResolver`: the note whose path ends
 * with the link, ignoring case): the name, else as many folders in front of it as it takes to tell it apart. `all`
 * comes from the vault's tree, which leaves its archive out, as the server's rule does for a living note's links.
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
 * The note a link from `from` leads to, as the server's `LinkResolver` (src/Kumbu.Core/Links/LinkResolver.cs) finds
 * it: the note whose path in the vault ends with the link (from the vault's root for a leading `/`), ignoring case;
 * `all` comes from the tree, which leaves the archive out. Of several, the server takes the one in the source's folder
 * and calls the others ambiguous; the list takes the first in text order then. Null when no note fits.
 */
export function linkedNote(link: string, all: string[], from: string): string | null {
  const target = link.trim().normalize("NFC").replace(/\.md$/i, "").toLowerCase();
  if (!target) return null;
  const fits = all
    .filter((p) => /\.md$/i.test(p))
    .filter((p) => {
      const at = `/${linkPath(p).toLowerCase()}`;
      return target.startsWith("/") ? at === target : at.endsWith(`/${target}`);
    })
    .sort((a, b) => a.localeCompare(b));
  const folder = from.slice(0, from.lastIndexOf("/") + 1);
  const beside = fits.filter((p) => p.slice(0, p.lastIndexOf("/") + 1) === folder);
  return (beside.length === 1 ? beside[0] : fits[0]) ?? null;
}

/**
 * Whether a wiki link can name the note: `#` starts a heading, `|` an alias, and `[` `]` end the link, in any
 * form of it (name or full path), so a note with one of them in its path can't be linked to and isn't offered.
 */
export const linkable = (path: string) => !/[#|[\]]/.test(linkPath(path));

const MAX = 8;

/**
 * The notes (and attachments) for a query, best first, as the palette ranks them (`wordMatch`): every word of the
 * query in the note's folder or name, those where each starts a word first; then those that hold every word in their
 * own name before those that need the folder (the list shows 8, and the note named as typed must not fall off it);
 * then by name. Last, the notes whose path holds the query as typed (`projects/pl`, `/pro`). Searched within the
 * vault, so without its name. An empty query lists the vault's notes by name. Notes no link can name (`linkable`) are
 * left out.
 */
export function rankNotes(paths: string[], query: string): string[] {
  const match = wordMatch(query);
  const typed = query.trim().replace(/^\//, "").toLowerCase();
  const inName = wordMatch(typed);
  return paths
    .filter(linkable)
    .flatMap((p) => {
      const rank = match(`${folderOf(p).split("/").slice(2).join(" / ")} ${titleOf(p)}`) ?? (linkPath(p).toLowerCase().includes(typed) ? 2 : null);
      return rank === null ? [] : [{ p, rank, folderOnly: inName(titleOf(p)) === null ? 1 : 0 }];
    })
    .sort((a, b) => a.rank - b.rank || a.folderOnly - b.folderOnly || titleOf(a.p).localeCompare(titleOf(b.p)) || a.p.localeCompare(b.p))
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

/** The note's headings, in order, whose text holds what is typed after `#`, ignoring case. */
export const headingsFor = (content: string, query: string) =>
  outline(splitFrontmatter(content).body).filter((h) => h.text.toLowerCase().includes(query.trim().toLowerCase()));

/** A heading (its text as shown, `headingText`) as Obsidian writes it in a link: without `# | ^ [ ]` and `%%`. */
export const headingLink = (text: string) => text.replace(/%%|[#|^[\]]/g, "").replace(/\s+/g, " ").trim();

/** The block ids the note has, with the start of their text, whose id or text holds what is typed after `#^`. */
export function blocksFor(content: string, query: string) {
  const q = query.trim().toLowerCase();
  return blockList(content).filter((b) => b.id.toLowerCase().includes(q) || b.text.toLowerCase().includes(q));
}

/** The edit that completes `[[Note#que` (and a `]]` right after the caret) with a heading or `^block`: `[[Note#Goals]]`. */
export function completeSubpath(value: string, caret: number, at: LinkQuery & { note: string }, subpath: string) {
  const end = value.startsWith("]]", caret) ? caret + 2 : caret;
  const text = `[[${at.note}#${subpath}]]`;
  return { start: at.start, end, text, selStart: at.start + text.length, selEnd: at.start + text.length };
}

/**
 * A segment of a Markdown link's destination as the server's `MarkdownLink.Encode` writes it
 * (src/Kumbu.Core/Links/MarkdownLink.cs): control characters and ` %#?()<>[]^|\`"{}` percent-encoded, every other
 * letter as it is.
 */
const encodeSegment = (text: string) =>
  [...text]
    .map((c) => {
      if (!/\p{Cc}/u.test(c) && !' %#?()<>[]^|\\`"{}'.includes(c)) return c;
      return c.charCodeAt(0) < 0x80 ? `%${c.charCodeAt(0).toString(16).toUpperCase().padStart(2, "0")}` : encodeURIComponent(c);
    })
    .join("");

/** `/Work/My Folder/Plan.md` → `/My%20Folder/Plan.md`: a note's path from the vault's root, as we write Markdown links everywhere. */
export const markdownPath = (path: string) => `/${path.split("/").slice(2).map(encodeSegment).join("/")}`;

/** A heading after a Markdown link's `#`: as a wiki link holds it (`headingLink`), encoded (`#Next%20steps`). */
export const markdownFragment = (text: string) => `#${encodeSegment(headingLink(text))}`;

/** The edit that completes a Markdown link's destination (and a `)` right after the caret): `[x](/plan.md)`. */
export function completeMarkdown(value: string, caret: number, at: LinkQuery, destination: string) {
  const end = value[caret] === ")" ? caret + 1 : caret;
  const text = `${destination})`;
  return { start: at.start, end, text, selStart: at.start + text.length, selEnd: at.start + text.length };
}
