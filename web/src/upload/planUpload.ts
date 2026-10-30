// SPDX-License-Identifier: AGPL-3.0-or-later
import { MARKER } from "../api/paths";

/** The server's limits per request (UploadApi.MaxFiles, MaxBytes: UTF-8 bytes of the files' text). */
export const MAX_FILES = 200;
export const MAX_BYTES = 5_000_000;
/** The most a note may have (NoteWriter.MaxBytes): the server refuses a larger file, so it isn't sent. */
export const MAX_NOTE_BYTES = 1_048_576;
/** The most an image or PDF may have (`AttachmentFormat.MaxBytes`, 15 × 1024 × 1024), said as "15 MB". */
export const MAX_ATTACHMENT_BYTES = 15_728_640;

/**
 * What the file picker offers: notes and the five formats by extension and media type. Concrete types, never
 * `image/*`: iOS Safari then converts a HEIC photo to JPEG as it is picked.
 */
export const ACCEPT = ".md,.markdown,text/markdown,.pdf,.png,.jpg,.jpeg,.gif,.webp,application/pdf,image/png,image/jpeg,image/gif,image/webp";

export interface Skipped {
  path: string;
  reason: string;
}

export interface UploadFile {
  path: string;
  content: string;
}

// The server's sentences where it has them, so its list and this one read alike.
export const NOT_SUPPORTED = "not a format Kumbu² keeps: notes (.md, .markdown), PDFs (.pdf) and images (.png, .jpg, .jpeg, .gif, .webp)";
export const OBSIDIAN = "Obsidian's settings or trash, not a note";
export const TOO_LARGE = `larger than ${MAX_NOTE_BYTES} bytes (1 MiB), the limit for a note`;
export const TOO_LARGE_FILE = "larger than 15 MB, the limit for a file";

export const isMarkdown = (name: string) => /.\.(md|markdown)$/i.test(name);

/** Whether a file name is one of the five formats kept besides notes (`.pdf`, `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`). */
export const isAttachmentName = (name: string) => /.\.(pdf|png|jpe?g|gif|webp)$/i.test(name);

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
  const name = segments.at(-1)!;
  return isMarkdown(name) || isAttachmentName(name) ? null : NOT_SUPPORTED;
}

/** What a pick holds: notes to read and send, images and PDFs to send as they are, images too large to send as they are. */
export interface UploadPlan<T> {
  notes: T[];
  files: T[];
  /** Empty folders' markers: sent as zero bytes, whatever their size and content, and counted nowhere. */
  markers: T[];
  /** Images over 15 MB: the user is offered to shrink them. A PDF over 15 MB is skipped. */
  tooLarge: T[];
  skipped: Skipped[];
}

/**
 * Which files to read and send: notes and the five formats, nothing of Obsidian's settings or trash, nothing larger
 * than a note or file may be. Decided by path and size alone, so skipped files are never read. The server checks again.
 */
export function planUpload<T extends { path: string; size: number }>(files: T[]): UploadPlan<T> {
  const plan: UploadPlan<T> = { notes: [], files: [], markers: [], tooLarge: [], skipped: [] };
  for (const file of files) {
    const reason = reasonAgainst(file.path);
    if (reason === NOT_SUPPORTED && isFolderMarker(file.path)) plan.markers.push(file);
    else if (reason) plan.skipped.push({ path: file.path, reason });
    else if (isMarkdown(file.path)) {
      if (file.size > MAX_NOTE_BYTES) plan.skipped.push({ path: file.path, reason: TOO_LARGE });
      else plan.notes.push(file);
    } else if (file.size <= MAX_ATTACHMENT_BYTES) plan.files.push(file);
    else if (!/\.pdf$/i.test(file.path)) plan.tooLarge.push(file);
    else plan.skipped.push({ path: file.path, reason: TOO_LARGE_FILE });
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
