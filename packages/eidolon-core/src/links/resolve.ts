// SPDX-License-Identifier: AGPL-3.0-or-later
// Where a link leads within one vault, by the server's rules (Kumbu.Core Links/LinkResolver), close to Obsidian's:
// case-insensitive; relative to the source's folder for "./" and "../", from the vault's root for "/"; otherwise the
// note whose path ends with the link path, preferring for a path with a folder the one below the source's folder, then
// the one at that path from the vault's root, and for a bare name the source's own folder. A source outside the vault's
// archive prefers what lies outside it. Never guesses beyond that. Attachments resolve by the same rules, by their whole
// name: one ending in a format's extension ("shot.png") before notes, any other ("data.csv", "Makefile") only when no
// note matches it. Only when nothing of that name is found does a bare wikilink name lead to the notes whose aliases
// contain it. Paths are full: "/Work/projects/plan.md".
import { inArchive } from "./archive";
import { FILE, FOLDER_MARKER, formatForName } from "./formats";
import type { NoteLink } from "./parse";
import { foldCase, trim } from "./text";

/** note: exactly one note; self: [[#Heading]]; ambiguous: several match and none is in the source's folder; attachment:
 * exactly one attachment, or, without a target, a name like a file's when the resolver was given no attachments. */
export type LinkKind = "note" | "self" | "ambiguous" | "attachment" | "not_found";

/** `viaAlias`: the target was found by a note's aliases, not by its name. */
export interface LinkResolution { kind: LinkKind; target: string | null; candidates: string[]; viaAlias: boolean }

interface Index {
  /** By the path inside the vault ("/F1/x.md"), case folded, for a link that names one exactly. */
  byPath: Map<string, string[]>;
  /** By every end of that path that starts a segment ("/x.md", "/F1/x.md"), case folded, for a link that names an end. */
  bySuffix: Map<string, string[]>;
}

const NOT_FOUND: LinkResolution = { kind: "not_found", target: null, candidates: [], viaAlias: false };

export class LinkResolver {
  private readonly notes: Index = { byPath: new Map(), bySuffix: new Map() };
  private readonly attachments: Index = { byPath: new Map(), bySuffix: new Map() };
  private readonly byAlias = new Map<string, string[]>();
  /** Whether the resolver was given the vault's attachments. */
  private readonly attachmentsKnown: boolean;

  /**
   * `notes`: every note of the vault named `vault`; paths in other vaults are left out. `attachments`: every attachment
   * of it, or null if unknown, so a link to a file is then an attachment without a target, whether the file exists or
   * not. `aliases`: the aliases of notes, as aliasesOf reads them; they count only for paths among `notes`.
   */
  constructor(private readonly vault: string, notes: Iterable<string>, attachments: Iterable<string> | null, aliases?: ReadonlyMap<string, readonly string[]>) {
    for (const note of notes) {
      if (vaultOf(note) !== vault) continue;
      add(this.notes, note);
      const own = aliases?.get(note);
      if (own === undefined) continue;
      const name = foldCase(stripMd(nameOf(note)));
      for (const alias of own) {
        const key = foldCase(alias);
        // An alias that is the note's own name already finds it by its name.
        if (key === name) continue;
        const list = this.byAlias.get(key);
        if (list === undefined) this.byAlias.set(key, [note]);
        else list.push(note);
      }
    }

    this.attachmentsKnown = attachments !== null;
    for (const attachment of attachments ?? []) {
      // A kept folder's marker is no file of the vault anyone links to.
      if (vaultOf(attachment) === vault && nameOf(attachment) !== FOLDER_MARKER) add(this.attachments, attachment);
    }
  }

  /** Where `link` in the note at `source` leads; a string is a wikilink's target. A Markdown link names a path, so
   * never an alias. */
  resolve(source: string, link: NoteLink | string): LinkResolution {
    return typeof link === "string" ? this.resolveTarget(source, link, true) : this.resolveTarget(source, link.target, link.wiki);
  }

  private resolveTarget(source: string, target: string, wikilink: boolean): LinkResolution {
    let text = trim(target).normalize("NFC");
    if (text.length === 0) return { kind: "self", target: source, candidates: [], viaAlias: false };

    // "./x", "../x" (relative to the source's folder) and "/x" (from the vault's root) name one path exactly;
    // anything else matches the end of a path.
    let exact = text.startsWith("/");
    if (text.startsWith("./") || text.startsWith("../")) {
      exact = true;
      const relative = parsePath(source.slice(0, source.lastIndexOf("/")) + "/" + text);
      if (relative === null || relative[0] !== this.vault || relative.length < 2) return NOT_FOUND;
      text = relative.slice(1).join("/");
    }

    const full = text.replace(/^\/+/, "");
    const fileName = full.slice(full.lastIndexOf("/") + 1);
    // A name ending in a format's extension (shot.png) names that format, so a stored one comes first. Any other name
    // (Ideas, data.csv, Makefile) is a note's first, by name or alias, and a stored file's only where no note matches:
    // a file uploaded under a note's name takes none of its links over.
    const format = formatForName(fileName);
    const formatName = format !== null && format !== FILE;
    if (formatName) {
      const attachment = this.findAttachment(source, full, exact);
      if (attachment !== null) return attachment;
    }

    const path = stripMd(full);
    const name = path.slice(path.lastIndexOf("/") + 1);
    const note = find(this.notes, "note", source, path, `/${path}.md`, exact);
    if (note !== null) return note;

    // A bare name that no note carries may be a note's alias; "Kumbu.md" names a file, never an alias, and so does
    // "shot.png": without the vault's attachments it may name one that exists.
    if (wikilink && !exact && !full.includes("/") && !endsWithMd(full) && !formatName) {
      const aliased = this.byAlias.get(foldCase(name));
      const byAlias = aliased === undefined ? null : choose("note", source, [...aliased], exact);
      if (byAlias !== null) return { ...byAlias, viaAlias: true };
    }

    if (!formatName) {
      const file = this.findAttachment(source, full, exact);
      if (file !== null) return file;
    }

    // Without the vault's attachments, a name that looks like a file's may name one that exists; with them, a missing
    // file leads nowhere, as a missing note's link does.
    const dot = name.lastIndexOf(".");
    const looksLikeFile = dot > 0 && dot < name.length - 1 && !name.slice(dot + 1).includes(" ");
    return looksLikeFile && !this.attachmentsKnown ? { kind: "attachment", target: null, candidates: [], viaAlias: false } : NOT_FOUND;
  }

  private findAttachment(source: string, full: string, exact: boolean): LinkResolution | null {
    return this.attachmentsKnown ? find(this.attachments, "attachment", source, full, "/" + full, exact) : null;
  }
}

function add(index: Index, path: string): void {
  const inVault = path.slice(path.indexOf("/", 1));
  push(index.byPath, foldCase(inVault), path);
  const folded = foldCase(inVault);
  for (let end = folded.length; end > 0; end = folded.lastIndexOf("/", end - 1)) {
    push(index.bySuffix, folded.slice(folded.lastIndexOf("/", end - 1)), path);
  }
}

function push(map: Map<string, string[]>, key: string, path: string): void {
  const list = map.get(key);
  if (list === undefined) map.set(key, [path]);
  else list.push(path);
}

/** The note or attachment (by `kind`) that `path` names, as the rules above say; null if none matches. `suffix` is the
 * path inside the vault it names, from a "/". */
function find(index: Index, kind: LinkKind, source: string, path: string, suffix: string, exact: boolean): LinkResolution | null {
  const key = foldCase(suffix);
  const candidates = [...((exact ? index.byPath : index.bySuffix).get(key) ?? [])];

  // A path with a folder ("Work/plan") is, as in Obsidian, first a path below the source's folder, then a path from
  // the vault's root, and only then a path suffix.
  if (!exact && path.includes("/") && candidates.length > 1) {
    const folder = source.slice(source.indexOf("/", 1), source.lastIndexOf("/"));
    const preferred = index.byPath.get(foldCase(folder + suffix))?.[0] ?? index.byPath.get(key)?.[0];
    if (preferred !== undefined) return { kind, target: preferred, candidates, viaAlias: false };
  }

  return choose(kind, source, candidates, exact);
}

/** The one of `candidates` a link from `source` leads to, after the archive and same-folder preferences; ambiguous if
 * several remain; null if there are none. */
function choose(kind: LinkKind, source: string, candidates: string[], exact: boolean): LinkResolution | null {
  // For a living note, what lies in the vault's archive counts only when nothing else matches: an archived copy never
  // makes its link ambiguous. An archived note keeps leading to its archived siblings.
  if (!exact && !inArchive(source) && candidates.some(inArchive) && candidates.some((c) => !inArchive(c))) {
    candidates = candidates.filter((c) => !inArchive(c));
  }

  if (candidates.length === 1) return { kind, target: candidates[0]!, candidates, viaAlias: false };
  if (candidates.length === 0) return null;

  const folder = parentOf(source);
  const sameFolder = candidates.filter((c) => parentOf(c) === folder);
  return sameFolder.length === 1
    ? { kind, target: sameFolder[0]!, candidates, viaAlias: false }
    : { kind: "ambiguous", target: null, candidates, viaAlias: false };
}

function vaultOf(path: string): string {
  const slash = path.indexOf("/", 1);
  return slash < 0 ? path.slice(1) : path.slice(1, slash);
}

function nameOf(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}

function parentOf(path: string): string {
  return path.slice(0, path.lastIndexOf("/"));
}

function endsWithMd(name: string): boolean {
  return name.length >= 3 && foldCase(name.slice(-3)) === ".MD";
}

function stripMd(name: string): string {
  return endsWithMd(name) ? name.slice(0, -3) : name;
}

/** An absolute path's segments as the server's VaultPath.TryParse reads them: NFC, empty and "." segments dropped, ".."
 * climbing (never above the root); null for a path with a lone surrogate. */
function parsePath(input: string): string[] | null {
  if (/[\ud800-\udbff](?![\udc00-\udfff])|(?<![\ud800-\udbff])[\udc00-\udfff]/.test(input)) return null;
  const segments: string[] = [];
  for (const part of input.normalize("NFC").split("/")) {
    if (part.length === 0 || part === ".") continue;
    if (part === "..") segments.pop();
    else segments.push(part);
  }
  return segments;
}
