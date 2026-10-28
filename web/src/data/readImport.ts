// SPDX-License-Identifier: AGPL-3.0-or-later
import { readDirectory, readEntry, type ZipEntry } from "./zipReader";
import { isAttachmentName, MAX_ATTACHMENT_BYTES, MAX_NOTE_BYTES, NOT_SUPPORTED, OBSIDIAN, TOO_LARGE, TOO_LARGE_FILE, type Skipped } from "../upload/planUpload";

/** A note to import. `path`: below the vault, ending in ".md" as the server stores it. */
export interface ImportNote {
  path: string;
  content: string;
  version: string;
}

/**
 * An image or PDF to import, at its path below the vault. Its bytes stay in the ZIP until it is sent: `read` reads and
 * unpacks them (again) each time, so no more than one file's bytes are in memory at once.
 */
export interface ImportFile {
  path: string;
  size: number;
  version: string;
  read(): Promise<Uint8Array>;
}

/** A vault of the ZIP: a top-level folder, or the whole ZIP when it has notes or files at its root. */
export interface ImportVault {
  name: string;
  notes: ImportNote[];
  files: ImportFile[];
  skipped: Skipped[];
}

const NOT_UTF8 = "not UTF-8 text";
const SAME_PATH = "another file of this upload has the same path";
// The ZIP's own compression methods: stored and deflate. Anything else the browser can't unpack.
const UNREADABLE = "packed in a way this browser can't unpack (only stored and deflate are)";

const encoder = new TextEncoder();

/** The server's version token (`VersionToken.Of`) of a file's bytes, as they are. */
export async function versionOfBytes(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", bytes as Uint8Array<ArrayBuffer>);
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0"))
    .join("")
    .slice(0, 12);
}

/** The server's version token (`VersionToken.Of`) of the text as it would store it: CRLF → LF. */
export const versionOf = (text: string) => versionOfBytes(encoder.encode(text.replaceAll("\r\n", "\n")));

/** Kumbu² notes end in ".md"; ".markdown" and any case of either become it (`UploadApi.NoteName`). */
function noteName(name: string): string | null {
  const match = /^(.+)\.(md|markdown)$/i.exec(name);
  return match ? `${match[1]}.md` : null;
}

/**
 * Why a path below the vault isn't imported, in the server's words and order (`UploadApi.Target`, then the dot rule of
 * `NoteNames.Problem`); null for a note, an image or a PDF. The server checks again.
 */
function reasonAgainst(relative: string): string | null {
  const segments = relative.split("/");
  if (relative.includes("\\")) return "folders are separated by '/', not '\\'";
  if (relative.startsWith("/")) return "an absolute path; paths are relative to the folder uploaded into";
  if (segments.some((s) => s === "" || s === "." || s === "..")) return "empty, '.' and '..' segments aren't allowed";
  if (segments.slice(0, -1).some((s) => /^\.(obsidian|trash)$/i.test(s))) return OBSIDIAN;
  if (!noteName(segments.at(-1)!) && !isAttachmentName(segments.at(-1)!)) return NOT_SUPPORTED;
  const hidden = segments.find((s) => s.startsWith("."));
  if (hidden) return `'${hidden}' starts with '.', which hides it in Obsidian`;
  return null;
}

// The byte order mark is kept: the server stores a note verbatim, so a note exported with one hashes with it.
const strictUtf8 = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
const TWICE = "in the ZIP more than once";

/** What the notes of one import may unpack to, together: past it, the ZIP is refused before anything is unpacked. */
export const MAX_IMPORT_BYTES = 256 * 1024 * 1024;

/** The ZIP's notes would unpack to more than the budget: it is to be imported in parts. */
export class ImportTooLarge extends Error {
  constructor() {
    super("The notes in this ZIP add up to more than 256 MB. Import it in parts.");
  }
}

/** How the server tells vault names apart (`VaultCatalog`): NFC, trimmed, any case. */
export const vaultKey = (name: string) => name.normalize("NFC").trim().toLowerCase();

/**
 * The entry's name as its tool meant it. Without the ZIP's UTF-8 flag, the name is read as Latin-1, but macOS's Compress
 * and others write UTF-8 there unflagged: a name whose bytes are valid UTF-8 is read as UTF-8. fflate doesn't say
 * whether the flag was set, so a flagged name of Latin-1 characters that happen to form UTF-8 (`Ã©`) is misread too;
 * real names like that are rare.
 */
function entryName(name: string): string {
  if (!/[\x80-\xff]/.test(name) || /[^\x00-\xff]/.test(name)) return name;
  try {
    return strictUtf8.decode(Uint8Array.from(name, (c) => c.charCodeAt(0)));
  } catch {
    return name;
  }
}

/** Entries no one made on purpose: macOS's resource forks, and a dot folder (`.obsidian/`) at the top. */
const isSystem = (name: string) => /^__MACOSX(\/|$)/.test(name) || name.startsWith(".");

/**
 * The vaults of an import ZIP: each top-level folder is one, or, when any note, image or PDF sits at the ZIP's root (an
 * Obsidian vault zipped directly), the whole ZIP is one, named after the file. Which entries are unpacked is decided by
 * name and size alone, so other files are never inflated. The ZIP is read in slices, never whole (`zipReader`): its
 * directory, then one entry at a time. Images and PDFs (up to 15 MB) are unpacked one at a time for their version and
 * dropped again; `read` unpacks one when it is sent. Notes that aren't UTF-8 are skipped, never imported with
 * replacement characters. Names come in NFC, as the server stores them (macOS writes NFD). Rejects when the bytes
 * aren't a ZIP, and with `ImportTooLarge` when the notes would unpack to more than `budget` bytes.
 */
export async function readImport(source: Blob | Uint8Array, zipName: string, budget = MAX_IMPORT_BYTES): Promise<ImportVault[]> {
  const zip = source instanceof Uint8Array ? new Blob([source as Uint8Array<ArrayBuffer>]) : source;
  // The central directory only: nothing is unpacked yet.
  const entries = await readDirectory(zip);

  // Old Windows tools separate folders with "\\": in a ZIP with no "/" at all, that is what it means.
  const backslashed = !entries.some((e) => e.name.includes("/")) && entries.some((e) => e.name.includes("\\"));
  const nameOf = (raw: string) => {
    const name = entryName(raw).normalize("NFC");
    return backslashed ? name.replaceAll("\\", "/") : name;
  };
  const isFile = (e: ZipEntry) => !nameOf(e.name).endsWith("/");
  // A name in the ZIP twice (an appended ZIP): which copy is meant can't be told, so neither is imported.
  const seen = new Map<string, number>();
  for (const e of entries) seen.set(e.name, (seen.get(e.name) ?? 0) + 1);
  // One vault when an entry at the root is a note or file: a folder per vault has nothing but folders there.
  const single = entries.some((e) => isFile(e) && !nameOf(e.name).includes("/") && reasonAgainst(nameOf(e.name)) === null);
  // Keyed as the server tells vaults apart; the first spelling met names the vault.
  const vaults = new Map<string, { vault: ImportVault; paths: Set<string> }>();
  const vaultOf = (name: string) => {
    let entry = vaults.get(vaultKey(name));
    if (!entry) vaults.set(vaultKey(name), (entry = { vault: { name, notes: [], files: [], skipped: [] }, paths: new Set() }));
    return entry;
  };
  const whole = zipName.replace(/\.zip$/i, "").normalize("NFC").trim();

  // Where each entry goes: its vault and path below it, and why it is skipped, if it is.
  const wanted = new Map<ZipEntry, { into: ImportVault; path: string; target: string }>();
  const files = new Map<ZipEntry, { into: ImportVault; path: string }>();
  let bytes = 0;
  for (const entry of entries) {
    const name = nameOf(entry.name);
    if (/^__MACOSX(\/|$)/.test(name)) continue;
    let vault: string;
    let relative: string;
    if (single) {
      [vault, relative] = [whole, name];
    } else {
      const slash = name.indexOf("/");
      // A file at the root, an absolute path or a dot folder belongs to no vault.
      if (slash <= 0 || isSystem(name)) continue;
      [vault, relative] = [name.slice(0, slash).trim(), name.slice(slash + 1)];
    }
    const { vault: target, paths } = vaultOf(vault);
    if (!isFile(entry)) continue;
    const note = noteName(relative.split("/").at(-1)!);
    const reason =
      (seen.get(entry.name)! > 1 ? TWICE : null) ??
      reasonAgainst(relative) ??
      (note && entry.originalSize > MAX_NOTE_BYTES ? TOO_LARGE : null) ??
      (!note && entry.originalSize > MAX_ATTACHMENT_BYTES ? TOO_LARGE_FILE : null) ??
      (entry.compression !== 0 && entry.compression !== 8 ? UNREADABLE : null);
    if (reason) {
      target.skipped.push({ path: relative, reason });
      continue;
    }
    const path = note ? relative.split("/").slice(0, -1).concat(note).join("/") : relative;
    if (paths.has(path)) {
      target.skipped.push({ path: relative, reason: SAME_PATH });
      continue;
    }
    paths.add(path);
    // An image or PDF keeps its name, and is unpacked later, apart from the notes and outside their budget.
    if (!note) {
      files.set(entry, { into: target, path });
      continue;
    }
    bytes += entry.originalSize;
    wanted.set(entry, { into: target, path, target: relative });
  }
  // The budget is summed from the sizes the ZIP declares. That bounds memory because `readEntry` has fflate's
  // inflateSync (pinned to 0.8.3 in package.json for this) write into a buffer of exactly the declared size, which it
  // never grows; a lying
  // size truncates the note instead. A stored entry is cut at its compressed size, so it is never larger than the ZIP,
  // and the size check below still refuses it. readImport.test.ts pins all three with lying ZIPs: run it before
  // moving to another fflate version.
  if (bytes > budget) throw new ImportTooLarge();

  // The notes, one at a time.
  for (const [entry, { into, path, target }] of wanted) {
    const raw = await readEntry(zip, entry);
    let content: string;
    try {
      content = strictUtf8.decode(raw);
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

  // Files one at a time: each is unpacked for its version and dropped, and again when it is sent. Its declared size
  // bounds what fflate allocates, as for notes; a stored entry that lies is caught by the size check.
  for (const [entry, { into, path }] of files) {
    const bytes = await readEntry(zip, entry);
    if (bytes.length > MAX_ATTACHMENT_BYTES) {
      into.skipped.push({ path, reason: TOO_LARGE_FILE });
      continue;
    }
    into.files.push({ path, size: bytes.length, version: await versionOfBytes(bytes), read: () => readEntry(zip, entry) });
  }
  return [...vaults.values()].map((v) => v.vault);
}
