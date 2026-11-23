// SPDX-License-Identifier: AGPL-3.0-or-later
// A vault's archive (Kumbu.Core Notes/ArchiveFolder): the folder _archive directly in its root, compared ordinally;
// /Work/Inbox/_archive/ is an ordinary folder.

export const ARCHIVE = "_archive";

/** The path ("/Work/_archive/x.md") is the archive of its vault or lies below it. */
export function inArchive(path: string): boolean {
  return path.split("/")[2] === ARCHIVE;
}
