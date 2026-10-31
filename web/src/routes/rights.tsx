// SPDX-License-Identifier: AGPL-3.0-or-later

/** What `maintain` adds to `write` (collab concept §7), said wherever it is chosen or shown. */
export const maintainSentence = "Can change the vault's structure: the board's columns, checkpoints.";

/** "read", "read and write", "read, write and maintain" (in the warning colour), or a right this app doesn't know, as it is. */
export function RightText({ right }: { right: string }) {
  if (right === "maintain") return <span className="text-warning">read, write and maintain</span>;
  return <>{right === "write" ? "read and write" : right}</>;
}
