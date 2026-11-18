// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { copyName, copyText } from "./duplicate";

const note = (...fm: string[]) => `---\n${fm.join("\n")}\n---\nBody\n`;

describe("copyText", () => {
  it("removes_calendar_keys", () => {
    for (const key of ["when", "until", "due", "task", "upcoming", "tz", "closed", "reason"]) {
      expect(copyText(note("a: 1", `${key}: x`, "b: 2"))).toBe(note("a: 1", "b: 2"));
    }
  });

  it("removes_a_multi_line_value_whole", () => {
    expect(copyText(note("a: 1", "until:", "  - x", "  - y", "b: 2"))).toBe(note("a: 1", "b: 2"));
    expect(copyText(note("until:", "- x", "- y", "b: 2"))).toBe(note("b: 2"));
  });

  it("removes_a_quoted_key", () => {
    expect(copyText(note('"when": x', "'due' : y", "b: 2"))).toBe(note("b: 2"));
  });

  it("keeps_column_drops_rank_and_moved", () => {
    expect(copyText(note("column: Doing", "rank: 0|a:", "moved: 2026-01-01", "tags: [x]"))).toBe(note("column: Doing", "tags: [x]"));
  });

  it("keeps_tags_flags_and_other_properties", () => {
    const text = note("tags: [a, b]", "flag: red", "whenever: soon", "author: Me");
    expect(copyText(text)).toBe(text);
  });

  it("leaves_the_body_alone", () => {
    expect(copyText("when: x\nBody\n")).toBe("when: x\nBody\n");
    expect(copyText("---\na: 1\n---\nwhen: x\n")).toBe("---\na: 1\n---\nwhen: x\n");
  });

  it("copies_unreadable_frontmatter_unchanged", () => {
    const text = "---\nwhen: x\nnested:\n  a: 1\n---\nBody\n";
    expect(copyText(text)).toBe(text);
  });

  it("keeps_the_bom_and_line_endings", () => {
    expect(copyText("﻿---\r\nwhen: x\r\na: 1\r\n---\r\nBody")).toBe("﻿---\r\na: 1\r\n---\r\nBody");
  });
});

describe("copyName", () => {
  it("names", () => {
    expect(copyName("/Work/a/plan.md", 1)).toBe("/Work/a/Copy of plan.md");
    expect(copyName("/Work/a/plan.md", 2)).toBe("/Work/a/Copy of plan 2.md");
    expect(copyName("/Work/a/plan.md", 10)).toBe("/Work/a/Copy of plan 10.md");
  });

  it("cuts_a_long_name_before_the_number", () => {
    const base = "x".repeat(120);
    for (const n of [1, 2, 10]) {
      const file = copyName(`/Work/${base}.md`, n).slice("/Work/".length);
      expect(file.length - 3).toBe(100);
      expect(file.endsWith(n > 1 ? ` ${n}.md` : "x.md")).toBe(true);
    }
  });
});
