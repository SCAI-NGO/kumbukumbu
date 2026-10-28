// SPDX-License-Identifier: AGPL-3.0-or-later

/**
 * A file to upload, with its path relative to the folder uploaded into ('/' between folders), in NFC as the server
 * stores paths: macOS, iOS and some Android pickers name files in NFD.
 */
export interface Picked {
  path: string;
  file: File;
}

const fileOf = (entry: FileSystemFileEntry) => new Promise<File>((ok, fail) => entry.file(ok, fail));

/** All entries of a folder: a reader hands them out in chunks (100 in Chromium) until an empty one. */
async function entriesOf(dir: FileSystemDirectoryEntry) {
  const reader = dir.createReader();
  const all: FileSystemEntry[] = [];
  for (;;) {
    const chunk = await new Promise<FileSystemEntry[]>((ok, fail) => reader.readEntries(ok, fail));
    if (chunk.length === 0) return all;
    all.push(...chunk);
  }
}

async function walk(entry: FileSystemEntry): Promise<Picked[]> {
  if (entry.isFile) return [{ path: entry.fullPath.replace(/^\//, "").normalize("NFC"), file: await fileOf(entry as FileSystemFileEntry) }];
  const children = await entriesOf(entry as FileSystemDirectoryEntry);
  return (await Promise.all(children.map(walk))).flat();
}

/**
 * The files of a drop, folders walked through: a dropped folder keeps its own name as the first segment
 * (`Vault/notes/a.md`). The entries are taken at once, as the list is only readable during the drop event.
 */
export function collectFromDrop(items: DataTransferItemList): Promise<Picked[]> {
  const pending: (Promise<Picked[]> | Picked[])[] = [];
  for (const item of Array.from(items)) {
    if (item.kind !== "file") continue;
    const entry = item.webkitGetAsEntry?.();
    if (entry) pending.push(walk(entry));
    else {
      const file = item.getAsFile();
      if (file) pending.push([{ path: file.name.normalize("NFC"), file }]);
    }
  }
  return Promise.all(pending).then((lists) => lists.flat());
}

/** The files of `<input type="file">`: with `webkitdirectory`, each path within the picked folder (its name first). */
export const collectFromInput = (files: ArrayLike<File>): Picked[] => Array.from(files, (file) => ({ path: (file.webkitRelativePath || file.name).normalize("NFC"), file }));
