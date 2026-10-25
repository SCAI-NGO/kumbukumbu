// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { taskToggle } from "./taskToggle";

describe("taskToggle", () => {
  const source = ["# Plan", "- [ ] write", "- [x] read", "  * [X] nested", "1. [ ] numbered", "- [ ] write"].join("\n");

  it("ticks an open task on exactly that line", () => {
    expect(taskToggle(source, 2)).toBe(["# Plan", "- [x] write", "- [x] read", "  * [X] nested", "1. [ ] numbered", "- [ ] write"].join("\n"));
  });

  it("opens a done task, in either case of x", () => {
    expect(taskToggle(source, 3)?.split("\n")[2]).toBe("- [ ] read");
    expect(taskToggle(source, 4)?.split("\n")[3]).toBe("  * [ ] nested");
  });

  it("leaves every other line byte for byte, line endings included", () => {
    const crlf = "a\r\n- [ ] b\r\nc [ ] d\r\n";
    expect(taskToggle(crlf, 2)).toBe("a\r\n- [x] b\r\nc [ ] d\r\n");
    expect(taskToggle(source, 5)?.split("\n")[4]).toBe("1. [x] numbered");
  });

  it("answers null for a line that is no task", () => {
    expect(taskToggle(source, 1)).toBeNull();
    expect(taskToggle("text [ ] here", 1)).toBeNull();
    expect(taskToggle(source, 99)).toBeNull();
  });

  it("finds the task behind a quote marker", () => {
    expect(taskToggle("> - [ ] quoted", 1)).toBe("> - [x] quoted");
  });
});
