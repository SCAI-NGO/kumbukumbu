// SPDX-License-Identifier: AGPL-3.0-or-later

// A list item's checkbox, behind any quote markers and indentation: `- [ ]`, `* [x]`, `1. [X]`, `> - [ ]`.
const task = /^((?:\s*>)*\s*(?:[-*+]|\d+[.)])\s+\[)([ xX])(\])/;

/**
 * The source with the task on 1-based `line` ticked or unticked, every other byte unchanged (MOBILE_EDITOR §1:
 * a tap in Read mode is a text edit); null when that line holds no task.
 */
export function taskToggle(source: string, line: number): string | null {
  const lines = source.split("\n");
  const at = line - 1;
  if (at < 0 || at >= lines.length || !task.test(lines[at])) return null;
  lines[at] = lines[at].replace(task, (_, open: string, mark: string, close: string) => `${open}${mark === " " ? "x" : " "}${close}`);
  return lines.join("\n");
}
