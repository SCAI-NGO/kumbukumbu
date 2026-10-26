// SPDX-License-Identifier: AGPL-3.0-or-later

/** A double-quoted shell-style string, so a name or path with quotes or backslashes still reads as one value. */
const quoted = (s: string) => `"${s.replaceAll("\\", "\\\\").replaceAll('"', '\\"')}"`;

/**
 * The paragraph to paste into an AI's chat so it can fetch a checkpoint's note: where it is from, and the command
 * that reads it. The date is fixed to `en-GB` so the text does not change with the reader's locale.
 */
export function aiPrompt(vault: string, name: string, createdAt: string, path: string): string {
  const date = new Date(createdAt).toLocaleDateString("en-GB", { day: "numeric", month: "short", year: "numeric" });
  return `From my Kumbu² vault ${vault}, checkpoint ${name} (${date}): ${path}. Read it with: cat --checkpoint ${quoted(name)} ${quoted(path)}`;
}
