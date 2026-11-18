// SPDX-License-Identifier: AGPL-3.0-or-later
import { frontmatterUnreadable } from "./frontmatterText";

/** The calendar's keys (`CalendarKeys.cs`, and `due` for cards), and the board's `rank` and `moved`: a copy takes none of them. */
const DROPPED = new Set(["when", "until", "due", "task", "upcoming", "tz", "closed", "reason", "rank", "moved"]);

const MAX_NAME = 100;
const PREFIX = "Copy of ";

/** The key a line starts, as `Frontmatter.Set` on the server reads it: a quoted key is the same key. */
const keyOf = (line: string) => /^(?:"([^"]*)"|'([^']*)'|([^\s:#"'-][^:]*?))[ \t]*:/.exec(line)?.slice(1).find((k) => k !== undefined);

/** A line that belongs to the key above it: indented or a `- ` item. */
const continues = (line: string) => /^[ \t]|^-(\s|$)/.test(line) && line.trim() !== "";

/**
 * The note's text without the keys a copy doesn't take (the calendar's, `rank`, `moved`), each with its whole entry,
 * a list or map below it included; every other byte as it was. Unchanged where there is no readable frontmatter.
 */
export function copyText(content: string): string {
  const bom = content.startsWith("﻿") ? "﻿" : "";
  const text = content.slice(bom.length);
  if (frontmatterUnreadable(text)) return content;
  const lines = text.split("\n");
  if (lines[0]?.trimEnd() !== "---") return content;
  const end = lines.findIndex((l, i) => i > 0 && (l.trimEnd() === "---" || l.trimEnd() === "..."));
  if (end < 0) return content;
  const kept: string[] = [lines[0]];
  for (let i = 1; i < end; i++) {
    const key = keyOf(lines[i].replace(/\r$/, ""));
    if (key === undefined || !DROPPED.has(key)) {
      kept.push(lines[i]);
      continue;
    }
    // The entry runs over the lines under it and the blank lines between those.
    let stop = i + 1;
    for (let next = i + 1; next < end; next++) {
      if (continues(lines[next].replace(/\r$/, ""))) stop = next + 1;
      else if (lines[next].trim() !== "") break;
    }
    i = stop - 1;
  }
  return bom + [...kept, ...lines.slice(end)].join("\n");
}

/**
 * Where the `n`th copy of the note goes: `/Work/a/Copy of plan.md`, then `… plan 2.md`, `… plan 3.md`. The name is cut
 * before the number so the file name stays within the 100 characters the server allows.
 */
export function copyName(path: string, n: number): string {
  const slash = path.lastIndexOf("/");
  const name = [...path.slice(slash + 1).replace(/\.md$/i, "")];
  const suffix = n > 1 ? ` ${n}` : "";
  const room = Math.max(MAX_NAME - PREFIX.length - suffix.length, 1);
  const cut = name.slice(0, room).join("").replace(/[. ]+$/, "");
  return `${path.slice(0, slash + 1)}${PREFIX}${cut}${suffix}.md`;
}
