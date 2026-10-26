// SPDX-License-Identifier: AGPL-3.0-or-later
import { unzipSync, type UnzipFileInfo } from "fflate";
import { MAX_NOTE_BYTES, NOT_MARKDOWN, OBSIDIAN, TOO_LARGE, type Skipped } from "../upload/planUpload";

/** A note to import. `path`: below the vault, ending in ".md" as the server stores it. */
export interface ImportNote {
  path: string;
  content: string;
  version: string;
}

/** A vault of the ZIP: a top-level folder, or the whole ZIP when it has notes at its root. */
export interface ImportVault {
  name: string;
  notes: ImportNote[];
  skipped: Skipped[];
}

const NOT_UTF8 = "not UTF-8 text";
const SAME_PATH = "another file of this upload has the same path";
// The ZIP's own compression methods: stored and deflate. Anything else the browser can't unpack.
const UNREADABLE = "packed in a way this browser can't unpack (only stored and deflate are)";

const encoder = new TextEncoder();

/** The server's version token (`VersionToken.Of`) of the text as it would store it: CRLF → LF. */
export async function versionOf(text: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(text.replaceAll("\r\n", "\n")));
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0"))
    .join("")
    .slice(0, 12);
}

/** Kumbu² notes end in ".md"; ".markdown" and any case of either become it (`UploadApi.NoteName`). */
function noteName(name: string): string | null {
  const match = /^(.+)\.(md|markdown)$/i.exec(name);
  return match ? `${match[1]}.md` : null;
}

/**
 * Why a path below the vault isn't imported, in the server's words and order (`UploadApi.Target`, then the dot rule of
 * `NoteNames.Problem`); null for a note. The server checks again.
 */
function reasonAgainst(relative: string): string | null {
  const segments = relative.split("/");
  if (relative.includes("\\")) return "folders are separated by '/', not '\\'";
  if (relative.startsWith("/")) return "an absolute path; paths are relative to the folder uploaded into";
  if (segments.some((s) => s === "" || s === "." || s === "..")) return "empty, '.' and '..' segments aren't allowed";
  if (segments.slice(0, -1).some((s) => /^\.(obsidian|trash)$/i.test(s))) return OBSIDIAN;
  if (!noteName(segments.at(-1)!)) return NOT_MARKDOWN;
  const hidden = segments.find((s) => s.startsWith("."));
  if (hidden) return `'${hidden}' starts with '.', which hides it in Obsidian`;
  return null;
}

/** Entries no one made on purpose: macOS's resource forks, and a dot folder (`.obsidian/`) at the top. */
const isSystem = (name: string) => /^__MACOSX(\/|$)/.test(name) || name.startsWith(".");

/**
 * The vaults of an import ZIP: each top-level folder is one, or, when any note sits at the ZIP's root (an Obsidian
 * vault zipped directly), the whole ZIP is one, named after the file. Which entries are unpacked is decided by name and
 * size alone, so images, PDFs and other attachments are never inflated. Notes that aren't UTF-8 are skipped, never
 * imported with replacement characters. Rejects when the bytes aren't a ZIP.
 */
export async function readImport(zip: Uint8Array, zipName: string): Promise<ImportVault[]> {
  // A first pass reads the central directory only: the filter sees every entry and lets none be unpacked.
  const entries: UnzipFileInfo[] = [];
  unzipSync(zip, {
    filter: (entry) => {
      entries.push(entry);
      return false;
    },
  });

  const isFile = (e: UnzipFileInfo) => !e.name.endsWith("/");
  // One vault when an entry at the root is a note: a folder per vault has nothing but folders there.
  const single = entries.some((e) => isFile(e) && !e.name.includes("/") && reasonAgainst(e.name) === null);
  const vaults = new Map<string, { vault: ImportVault; paths: Set<string> }>();
  const vaultOf = (name: string) => {
    let entry = vaults.get(name);
    if (!entry) vaults.set(name, (entry = { vault: { name, notes: [], skipped: [] }, paths: new Set() }));
    return entry;
  };
  const whole = zipName.replace(/\.zip$/i, "");

  // Where each entry goes: its vault and path below it, and why it is skipped, if it is.
  const wanted = new Map<string, { vault: string; path: string; target: string }>();
  for (const entry of entries) {
    if (/^__MACOSX(\/|$)/.test(entry.name)) continue;
    let vault: string;
    let relative: string;
    if (single) {
      [vault, relative] = [whole, entry.name];
    } else {
      const slash = entry.name.indexOf("/");
      // A file at the root, an absolute path or a dot folder belongs to no vault.
      if (slash <= 0 || isSystem(entry.name)) continue;
      [vault, relative] = [entry.name.slice(0, slash), entry.name.slice(slash + 1)];
    }
    const { vault: target, paths } = vaultOf(vault);
    if (!isFile(entry)) continue;
    const reason =
      reasonAgainst(relative) ??
      (entry.originalSize > MAX_NOTE_BYTES ? TOO_LARGE : null) ??
      (entry.compression !== 0 && entry.compression !== 8 ? UNREADABLE : null);
    if (reason) {
      target.skipped.push({ path: relative, reason });
      continue;
    }
    const path = relative.split("/").slice(0, -1).concat(noteName(relative.split("/").at(-1)!)!).join("/");
    if (paths.has(path)) {
      target.skipped.push({ path: relative, reason: SAME_PATH });
      continue;
    }
    paths.add(path);
    wanted.set(entry.name, { vault, path, target: relative });
  }

  // Only the notes are unpacked.
  const unpacked = unzipSync(zip, { filter: (entry) => wanted.has(entry.name) });
  const decoder = new TextDecoder("utf-8", { fatal: true });
  for (const [name, { vault, path, target }] of wanted) {
    const { vault: into } = vaults.get(vault)!;
    let content: string;
    try {
      content = decoder.decode(unpacked[name]);
    } catch {
      into.skipped.push({ path: target, reason: NOT_UTF8 });
      continue;
    }
    if (encoder.encode(content).length > MAX_NOTE_BYTES) {
      into.skipped.push({ path: target, reason: TOO_LARGE });
      continue;
    }
    into.notes.push({ path, content, version: await versionOf(content) });
  }
  return [...vaults.values()].map((v) => v.vault);
}
