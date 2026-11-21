// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { copyName, copyText } from "./duplicate";

const note = (...fm: string[]) => `---\n${fm.join("\n")}\n---\nBody\n`;

describe("copyText", () => {
  it("reads_a_long_frontmatter_line_without_a_colon_in_one_pass", () => {
    const long = "a" + " \t".repeat(100_000) + "b";
    const start = performance.now();
    expect(copyText(note(long, "when: 2026-11-21", "c: 3"))).toBe(note(long, "c: 3"));
    expect(copyText(note(long + ": 1", "c: 3"))).toBe(note(long + ": 1", "c: 3"));
    expect(performance.now() - start).toBeLessThan(500);
  });

  it("reads_keys_as_before_quoted_spaced_and_with_colons_in_the_value", () => {
    expect(copyText(note("when \t : x", "b: 2"))).toBe(note("b: 2"));
    expect(copyText(note("'when': x", "url: http://a:b"))).toBe(note("url: http://a:b"));
    expect(copyText(note("whenever: x", "-when: y", "#when: z"))).toBe(note("whenever: x", "-when: y", "#when: z"));
  });

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

  it("removes_the_calendar_keys_in_any_case_as_the_server_reads_them", () => {
    expect(copyText(note("When: 2026-12-01", "b: 2"))).toBe(note("b: 2"));
    expect(copyText(note("UNTIL: 2026-12-02", "Task: true", "TZ: Europe/Zurich", "b: 2"))).toBe(note("b: 2"));
  });

  it("keeps_due_rank_and_moved_in_another_case_which_the_board_doesnt_read", () => {
    const text = note("Due: 2026-12-01", "RANK: a", "Moved: x");
    expect(copyText(text)).toBe(text);
  });

  it("removes_a_key_with_a_comment_after_its_value", () => {
    expect(copyText(note("a: 1", "when: 2026-12-01 # dentist", "b: 2"))).toBe(note("a: 1", "b: 2"));
  });

  it("removes_a_key_below_a_block_scalar", () => {
    expect(copyText(note("summary: >", "  Folded text", "  over lines", "when: 2026-12-01", "b: 2"))).toBe(
      note("summary: >", "  Folded text", "  over lines", "b: 2"),
    );
  });

  it("removes_a_nested_map_under_a_removed_key", () => {
    expect(copyText(note("a: 1", "until:", "  day: 2026-12-02", "  time: 10:00", "b: 2"))).toBe(note("a: 1", "b: 2"));
  });

  it("removes_the_keys_from_a_frontmatter_the_properties_box_cant_read", () => {
    expect(copyText("---\nwhen: x\nnested:\n  a: 1\n---\nBody\n")).toBe("---\nnested:\n  a: 1\n---\nBody\n");
  });

  it("handles_a_frontmatter_at_the_end_without_a_newline", () => {
    expect(copyText("---\na: 1\nwhen: x\n---")).toBe("---\na: 1\n---");
  });

  it("leaves_an_empty_block_where_every_key_goes", () => {
    expect(copyText("---\nwhen: x\ntask: true\n---\nBody\n")).toBe("---\n---\nBody\n");
  });

  it("reads_a_dots_closing_line", () => {
    expect(copyText("---\nwhen: x\na: 1\n...\nBody\n")).toBe("---\na: 1\n...\nBody\n");
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
