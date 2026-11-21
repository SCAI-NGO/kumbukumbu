// SPDX-License-Identifier: AGPL-3.0-or-later
import { MAX_NAME } from "./nameFromHeading";

/** The calendar's keys, which the server reads in any case (`CalendarKeys.cs`): a copy takes none of them. */
const CALENDAR = new Set(["when", "until", "task", "upcoming", "tz", "closed", "reason"]);
/** A card's `due` and the board's `rank` and `moved`, which the server reads exactly as written. */
const BOARD = new Set(["due", "rank", "moved"]);

const dropped = (key: string) => CALENDAR.has(key.toLowerCase()) || BOARD.has(key);

const PREFIX = "Copy of ";

/**
 * The key a line starts, as `Frontmatter.Set` on the server reads it: a quoted key is the same key. Read in one pass
 * (the text before the line's first colon, its trailing blanks off), not with a lazy pattern, which is quadratic on a
 * long line without a colon.
 */
function keyOf(line: string): string | undefined {
  const first = line.charAt(0);
  if (first === '"' || first === "'") {
    const close = line.indexOf(first, 1);
    return close > 0 && /^[ \t]*:/.test(line.slice(close + 1)) ? line.slice(1, close) : undefined;
  }
  if (first === "" || /[\s:#"'-]/.test(first)) return undefined;
  const colon = line.indexOf(":");
  if (colon < 0) return undefined;
  let end = colon;
  while (line[end - 1] === " " || line[end - 1] === "\t") end--;
  return line.slice(0, end);
}

/** A line that belongs to the key above it: indented or a `- ` item. */
const continues = (line: string) => /^[ \t]|^-(\s|$)/.test(line) && line.trim() !== "";

/**
 * The note's text without the keys a copy doesn't take (the calendar's, `rank`, `moved`), each with its whole entry,
 * a list, map or block below it included, as `Frontmatter.Set` on the server takes a key's extent; every other byte as
 * it was. Also where the properties box can't read the frontmatter: the server's calendar still reads its dates.
 * Unchanged where there is no closed frontmatter block.
 */
export function copyText(content: string): string {
  const bom = content.startsWith("﻿") ? "﻿" : "";
  const text = content.slice(bom.length);
  const lines = text.split("\n");
  if (lines[0]?.trimEnd() !== "---") return content;
  const end = lines.findIndex((l, i) => i > 0 && (l.trimEnd() === "---" || l.trimEnd() === "..."));
  if (end < 0) return content;
  const kept: string[] = [lines[0]];
  for (let i = 1; i < end; i++) {
    const key = keyOf(lines[i].replace(/\r$/, ""));
    if (key === undefined || !dropped(key)) {
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
