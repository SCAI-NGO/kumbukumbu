// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { changedLines, lineDiff } from "./lineDiff";

describe("lineDiff", () => {
  it("keeps the common lines and names the removed and added ones, in order", () => {
    expect(lineDiff("a\nb\nc\nd", "a\nB\nc\nd\ne")).toEqual([
      { kind: "same", text: "a", before: 1, after: 1 },
      { kind: "removed", text: "b", before: 2 },
      { kind: "added", text: "B", after: 2 },
      { kind: "same", text: "c", before: 3, after: 3 },
      { kind: "same", text: "d", before: 4, after: 4 },
      { kind: "added", text: "e", after: 5 },
    ]);
  });

  it("finds the longest common run, not just the first match", () => {
    const kinds = lineDiff("x\na\nb\nc", "a\nb\nc\nx").map((l) => `${l.kind[0]}${l.text}`);
    expect(kinds).toEqual(["rx", "sa", "sb", "sc", "ax"]);
  });

  it("handles empty sides", () => {
    expect(lineDiff("", "a")).toEqual([
      { kind: "removed", text: "", before: 1 },
      { kind: "added", text: "a", after: 1 },
    ]);
    expect(lineDiff("a", "a")).toEqual([{ kind: "same", text: "a", before: 1, after: 1 }]);
  });
});

describe("changedLines", () => {
  it("answers the 1-based lines of the newer text that were added or changed", () => {
    expect([...changedLines("# T\n\nold\nkeep", "# T\n\nnew\nkeep\nmore")]).toEqual([3, 5]);
  });

  it("answers nothing for equal texts, and ignores CRLF against LF", () => {
    expect(changedLines("a\r\nb", "a\nb").size).toBe(0);
  });
});
