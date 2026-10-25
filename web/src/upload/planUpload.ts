// SPDX-License-Identifier: AGPL-3.0-or-later

/** The server's limits per request (UploadApi.MaxFiles, MaxBytes: UTF-8 bytes of the files' text). */
export const MAX_FILES = 200;
export const MAX_BYTES = 5_000_000;

export interface Skipped {
  path: string;
  reason: string;
}

export interface UploadFile {
  path: string;
  content: string;
}

// The server's sentences, so its list and this one read alike.
const NOT_MARKDOWN = "not a Markdown file (.md or .markdown)";
const OBSIDIAN = "Obsidian's settings or trash, not a note";
const TOO_LARGE = "larger than 5 MB, the most one upload takes";

const isMarkdown = (name: string) => /.\.(md|markdown)$/i.test(name);

function reasonAgainst(path: string): string | null {
  const segments = path.split("/");
  if (segments.slice(0, -1).some((s) => /^\.(obsidian|trash)$/i.test(s))) return OBSIDIAN;
  return isMarkdown(segments.at(-1)!) ? null : NOT_MARKDOWN;
}

/**
 * Which files to read and send: Markdown only, nothing of Obsidian's settings or trash, nothing larger than one
 * request takes. Decided by path and size alone, so skipped files are never read. The server checks again.
 */
export function planUpload<T extends { path: string; size: number }>(files: T[]): { keep: T[]; skipped: Skipped[] } {
  const keep: T[] = [];
  const skipped: Skipped[] = [];
  for (const file of files) {
    const reason = reasonAgainst(file.path) ?? (file.size > MAX_BYTES ? TOO_LARGE : null);
    if (reason) skipped.push({ path: file.path, reason });
    else keep.push(file);
  }
  return { keep, skipped };
}

const encoder = new TextEncoder();

/**
 * The files in requests the server takes, in order: at most 200 files and 5 MB of text each. A file over that
 * once read (its text can be larger than the file, if it wasn't UTF-8) is skipped.
 */
export function toBatches(files: UploadFile[]): { batches: UploadFile[][]; skipped: Skipped[] } {
  const batches: UploadFile[][] = [];
  const skipped: Skipped[] = [];
  let bytes = 0;
  for (const file of files) {
    const size = encoder.encode(file.content).length;
    if (size > MAX_BYTES) {
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
