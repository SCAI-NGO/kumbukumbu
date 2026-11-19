// SPDX-License-Identifier: AGPL-3.0-or-later
import { MARKER } from "../api/paths";

/** The server's limits per request (UploadApi.MaxFiles, MaxBytes: UTF-8 bytes of the files' text). */
export const MAX_FILES = 200;
export const MAX_BYTES = 5_000_000;
/** The most a note may have (NoteWriter.MaxBytes): the server refuses a larger file, so it isn't sent. */
export const MAX_NOTE_BYTES = 1_048_576;
/** The most a file read whole may have (`AttachmentFormat.MaxBytes`, 15 × 1024 × 1024), said as "15 MB": a larger one
 * is sent as it is, streamed, and the server keeps it in chunks without reading it. */
export const MAX_ATTACHMENT_BYTES = 15_728_640;
/** The bound per file the session names (`maxFileBytes`, the admin's `Kumbu:Vaults:MaxFileBytes`), 1 GiB by default. */
export const DEFAULT_MAX_FILE_BYTES = 1024 ** 3;

export interface Skipped {
  path: string;
  reason: string;
}

export interface UploadFile {
  path: string;
  content: string;
}

// The server's sentences where it has them, so its list and this one read alike.
export const OBSIDIAN = "Obsidian's settings or trash, not a note";
export const TOO_LARGE = `larger than ${MAX_NOTE_BYTES} bytes (1 MiB), the limit for a note`;
export const SVG_TOO_LARGE = "larger than 15 MB; an SVG image is read whole to be checked, so it can have at most 15 MB";

/** "1 GiB", "20 MiB", else its bytes: a bound as the server's `FileTooLargeException.Shown` words it. */
export function boundText(bytes: number) {
  const mib = 1024 ** 2;
  return bytes % (1024 * mib) === 0 ? `${bytes / (1024 * mib)} GiB` : bytes % mib === 0 ? `${bytes / mib} MiB` : `${bytes.toLocaleString("en-GB")} bytes`;
}

/** "A file can have at most 1 GiB.", the server's refusal of a larger file. */
export const boundSentence = (bytes: number) => `A file can have at most ${boundText(bytes)}.`;

export const isMarkdown = (name: string) => /.\.(md|markdown)$/i.test(name);

/** Whether a file name is one of the formats Kumbu² shows (`.pdf`, `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.svg`); any other is kept as a file. */
export const isAttachmentName = (name: string) => /.\.(pdf|png|jpe?g|gif|webp|svg)$/i.test(name);

/**
 * An empty folder's marker (`.vaultkeep`): its folder's name comes first, and no dot folder lies above it. One directly in
 * the picked place (no folder before it) is no marker.
 */
export function isFolderMarker(path: string): boolean {
  const segments = path.split("/");
  return segments.length > 1 && segments.at(-1) === MARKER && !segments.slice(0, -1).some((s) => s.startsWith("."));
}

function reasonAgainst(path: string): string | null {
  const segments = path.split("/");
  if (segments.slice(0, -1).some((s) => /^\.(obsidian|trash)$/i.test(s))) return OBSIDIAN;
  // The server's sentence (`NoteNames.NameProblem`): a `.DS_Store` isn't sent only to come back refused.
  const hidden = segments.find((s) => s.startsWith("."));
  return hidden ? `'${hidden}' starts with '.', which hides it in Obsidian` : null;
}

/** What a pick holds: notes to read and send, files to send as they are, images too large to send as they are. */
export interface UploadPlan<T> {
  notes: T[];
  files: T[];
  /** Empty folders' markers: sent as zero bytes, whatever their size and content, and counted nowhere. */
  markers: T[];
  /** Images over 15 MB the browser can shrink (PNG, JPEG, GIF, WebP): the user is offered to shrink them, or to keep them as they are. */
  tooLarge: T[];
  skipped: Skipped[];
}

/**
 * Which files to read and send: notes and every other file, nothing of Obsidian's settings or trash, no dot file,
 * nothing larger than a note or `bound` (a file) may be, no SVG over 15 MB. Decided by path and size alone, so skipped
 * files are never read. The server checks again.
 */
export function planUpload<T extends { path: string; size: number }>(files: T[], bound = DEFAULT_MAX_FILE_BYTES): UploadPlan<T> {
  const plan: UploadPlan<T> = { notes: [], files: [], markers: [], tooLarge: [], skipped: [] };
  for (const file of files) {
    const reason = reasonAgainst(file.path);
    if (isFolderMarker(file.path)) plan.markers.push(file);
    else if (reason) plan.skipped.push({ path: file.path, reason });
    else if (isMarkdown(file.path)) {
      if (file.size > MAX_NOTE_BYTES) plan.skipped.push({ path: file.path, reason: TOO_LARGE });
      else plan.notes.push(file);
    } else if (file.size <= MAX_ATTACHMENT_BYTES) plan.files.push(file);
    else if (/.\.(png|jpe?g|gif|webp)$/i.test(file.path)) plan.tooLarge.push(file);
    else if (/.\.svg$/i.test(file.path)) plan.skipped.push({ path: file.path, reason: SVG_TOO_LARGE });
    else if (file.size <= bound) plan.files.push(file);
    else plan.skipped.push({ path: file.path, reason: boundSentence(bound) });
  }
  return plan;
}

const encoder = new TextEncoder();

/**
 * The files in requests the server takes, in order: at most 200 files and 5 MB of text each. A file larger
 * than a note may be once read (its text can be larger than the file, if it wasn't UTF-8) is skipped.
 */
export function toBatches(files: UploadFile[]): { batches: UploadFile[][]; skipped: Skipped[] } {
  const batches: UploadFile[][] = [];
  const skipped: Skipped[] = [];
  let bytes = 0;
  for (const file of files) {
    const size = encoder.encode(file.content).length;
    if (size > MAX_NOTE_BYTES) {
      skipped.push({ path: file.path, reason: TOO_LARGE });
      continue;
    }
    const last = batches.at(-1);
    if (last && last.length < MAX_FILES && bytes + size <= MAX_BYTES) {
      last.push(file);
      bytes += size;
    } else {
      batches.push([file]);
      bytes = size;
    }
  }
  return { batches, skipped };
}
