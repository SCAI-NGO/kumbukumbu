// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { outline } from "./outline";

describe("outline", () => {
  it("lists ATX headings with level, readable text and source line", () => {
    const body = ["", "", "", "# Plan", "text", "## Goals ##", "#### See [[roadmap|the road]] and `code`", "#nottag", "    # indented code"].join("\n");
    expect(outline(body)).toEqual([
      { level: 1, text: "Plan", line: 4 },
      { level: 2, text: "Goals", line: 6 },
      { level: 4, text: "See the road and code", line: 7 },
    ]);
  });

  it("skips headings inside fenced code, with either fence", () => {
    expect(outline("```\n# no\n```\n~~~~\n## no\n```\n~~~~\n## yes").map((h) => h.text)).toEqual(["yes"]);
  });
});
