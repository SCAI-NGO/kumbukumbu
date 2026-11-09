// SPDX-License-Identifier: AGPL-3.0-or-later
import { Brackets, Copy, Link } from "lucide-react";
import type { TreeFolder } from "../api/notes";
import { inVault, titleOf } from "../api/paths";
import type { MenuItem } from "../components/Menu";

/** The path with the vault, as `sh` and the agents use it. */
export const filePathText = (path: string) => path;

const key = (name: string) => name.normalize("NFC").toLowerCase();

/** How many notes in the tree (attachments left out; the tree leaves `_archive/` out itself) have `name`, as a link compares it. */
const notesNamed = (folder: TreeFolder, name: string): number =>
  folder.notes.filter((n) => n.kind !== "image" && n.kind !== "pdf" && n.kind !== "text" && key(n.name.replace(/\.md$/, "")) === name).length +
  folder.folders.reduce((sum, f) => sum + notesNamed(f, name), 0);

/**
 * Obsidian's default, the shortest wikilink unique in the vault: the bare name where exactly one note has it, else the
 * path inside the vault without `.md` (the server's `LinkResolver.LinkText`), also while the tree isn't loaded. Null
 * where the form needed holds a character that would break the link. Known gap: a note named like an attachment
 * (`x.png.md`) is resolved to the attachment first by the server.
 */
export function wikiLinkText(path: string, root: TreeFolder | undefined): string | null {
  const bare = titleOf(path);
  const full = inVault(path).replace(/\.md$/, "");
  const forms = root && notesNamed(root, key(bare)) === 1 ? [bare, full] : [full];
  const form = forms.find((f) => !/[#|^[\]]/.test(f));
  return form === undefined ? null : `[[${form}]]`;
}

const encoder = new TextEncoder();
// As the server's `MarkdownLink.Encode`: control characters, the space and these; anything else, non-ASCII too, raw.
const needsEncoding = (c: string) => /[\u0000-\u001f\u007f-\u009f]/.test(c) || " %#?()<>[]^|\\`\"{}".includes(c);
const encodeSegment = (segment: string) =>
  [...segment].map((c) => (needsEncoding(c) ? [...encoder.encode(c)].map((b) => `%${b.toString(16).toUpperCase().padStart(2, "0")}`).join("") : c)).join("");

/** A Markdown link as the agents write it: the name as text, the path inside the vault as the destination. */
export const markdownLinkText = (path: string) =>
  `[${titleOf(path).replace(/[\\[\]()]/g, "\\$&")}](/${inVault(path).split("/").map(encodeSegment).join("/")})`;

/** The copy entries of a file's menu, at any level: an attachment's path only, a note's link forms too. */
export function copyItems(path: string, root: TreeFolder | undefined, note: boolean, copy: (text: string) => void): MenuItem[] {
  const link = note ? wikiLinkText(path, root) : null;
  return [
    { label: "Copy file path", icon: Copy, onSelect: () => copy(filePathText(path)) },
    ...(link ? [{ label: "Copy link", icon: Brackets, onSelect: () => copy(link) }] : []),
    ...(note ? [{ label: "Copy markdown link", icon: Link, onSelect: () => copy(markdownLinkText(path)) }] : []),
  ];
}
