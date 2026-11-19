// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { atxHeading, headingLine, headingLink, outline, section } from "./outline";
import { embedHeading } from "./wikiLinks";

describe("outline", () => {
  it("lists ATX headings with level, readable text and source line", () => {
    const body = ["", "", "", "# Plan", "text", "## Goals ##", "#### See [[roadmap|the road]] and `code`", "#nottag", "    # indented code"].join("\n");
    expect(outline(body)).toEqual([
      { level: 1, text: "Plan", line: 4 },
      { level: 2, text: "Goals", line: 6 },
      { level: 4, text: "See the road and code", line: 7 },
    ]);
  });

  it("takes setext headings too, at their paragraph's first line, but not a rule after a blank line or a list", () => {
    const body = ["Title", "=====", "", "Two line", "sub *title*", "---", "", "text", "", "---", "", "- item", "---", "> quote", "==="].join("\n");
    expect(outline(body)).toEqual([
      { level: 1, text: "Title", line: 1 },
      { level: 2, text: "Two line sub title", line: 4 },
    ]);
  });

  it("skips headings inside fenced code, with either fence", () => {
    expect(outline("```\n# no\n```\n~~~~\n## no\n```\n~~~~\n## yes").map((h) => h.text)).toEqual(["yes"]);
  });

  it("leaves out headings inside %% comments, and a heading's own comment", () => {
    expect(outline("%%\n# Hidden\n%%\n# Shown")).toEqual([{ level: 1, text: "Shown", line: 4 }]);
    expect(outline("# A %%c%%")).toEqual([{ level: 1, text: "A", line: 1 }]);
    expect(outline("```\n%%\n```\n# Kept\n%%\n```\n%%\n```\n# Gone").map((h) => h.text)).toEqual(["Kept"]);
  });

  it("counts no %% in inline or indented code", () => {
    expect(outline("Use `%%` for comments.\n\n## Later").map((h) => h.text)).toEqual(["Later"]);
    expect(outline("# a `%%` b\n# c").map((h) => h.text)).toEqual(["a %% b", "c"]);
    expect(outline("text\n\n    %% code\n\n# After").map((h) => h.text)).toEqual(["After"]);
  });

  it("shows a highlight's text without its marks, and a comparison as written", () => {
    expect(outline("# ==Key== point\n## a == b").map((h) => h.text)).toEqual(["Key point", "a == b"]);
  });
});

describe("atxHeading", () => {
  it("reads levels, closing hashes, tabs and empty headings", () => {
    expect(atxHeading("# Plan")).toEqual({ level: 1, text: "Plan" });
    expect(atxHeading("## Plan ##")).toEqual({ level: 2, text: "Plan" });
    expect(atxHeading("# a ## b ##")).toEqual({ level: 1, text: "a ## b" });
    expect(atxHeading("# a#")).toEqual({ level: 1, text: "a#" });
    expect(atxHeading("#\tPlan")).toEqual({ level: 1, text: "Plan" });
    expect(atxHeading("   ###### Plan  ")).toEqual({ level: 6, text: "Plan" });
    expect(atxHeading("#")).toEqual({ level: 1 });
    expect(atxHeading("# ")).toEqual({ level: 1, text: "" });
    expect(atxHeading("#5")).toBeNull();
    expect(atxHeading("####### a")).toBeNull();
    expect(atxHeading("    # a")).toBeNull();
    expect(atxHeading("# a\r")).toBeNull();
    expect(atxHeading("# a\r", true)).toEqual({ level: 1, text: "a\r" });
  });

  it("reads every short line as the heading pattern did", () => {
    const patterns = [/^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/, /^ {0,3}(#{1,6})(?:[ \t]+([^\n]*?))?(?:[ \t]+#+)?[ \t]*$/];
    const chars = ["#", " ", "\t", "a", "\r"];
    let lines = [""];
    for (let length = 0; length <= 7; length++) {
      for (const line of lines) {
        patterns.forEach((pattern, anyChar) => {
          const m = pattern.exec(line);
          const expected = m ? (m[2] === undefined ? { level: m[1].length } : { level: m[1].length, text: m[2] }) : null;
          if (JSON.stringify(atxHeading(line, anyChar === 1)) !== JSON.stringify(expected)) expect([line, anyChar === 1, atxHeading(line, anyChar === 1)]).toEqual([line, anyChar === 1, expected]);
        });
      }
      lines = lines.flatMap((line) => chars.map((c) => line + c));
    }
  });

  it("lists the headings of a note with closing hashes and tabs", () => {
    expect(outline("# Plan #\n\ntext\n##\tGoals ##\n").map((h) => h.text)).toEqual(["Plan", "Goals"]);
  });
});

describe("section", () => {
  const body = "# A\nintro\n## B\nb text\n### B1\ndeep\n## C\nc text";

  it("keeps the heading's lines up to the next of its level or higher, the rest left empty", () => {
    expect(section(body, "b")).toBe("\n\n## B\nb text\n### B1\ndeep\n\n");
    expect(section(body, "C")).toBe("\n\n\n\n\n\n## C\nc text");
  });

  it("is null for a heading the note doesn't have", () => {
    expect(section(body, "Z")).toBeNull();
  });
});

describe("embedHeading", () => {
  it("reads the heading of an embed, not a block or an alias", () => {
    expect(embedHeading("![[plan#Goals]]")).toBe("Goals");
    expect(embedHeading("![[plan#Goals|alias]]")).toBe("Goals");
    expect(embedHeading("![[plan]]")).toBeNull();
    expect(embedHeading("![[plan#^block1]]")).toBeNull();
  });
});

describe("headingLine", () => {
  it("finds a heading by its text as shown, at its source line", () => {
    const body = ["", "", "# Plan", "", "## The **road**map", "", "text"].join("\n");
    expect(headingLine(body, "The roadmap")).toBe(5);
    expect(headingLine(body, "Plan")).toBe(3);
  });

  it("finds a heading by its source text too", () => {
    const body = ["# Plan", "", "## The **road**map", "", "## The `api`", "", "## Q\\#A"].join("\n");
    expect(headingLine(body, "The **road**map")).toBe(3);
    expect(headingLine(body, "the **ROAD**map")).toBe(3);
    expect(headingLine(body, "The `api`")).toBe(5);
    expect(headingLine(body, "Q\\#A")).toBe(7);
    // Duplicates are numbered by their text as written, as cat numbers them.
    expect(headingLine(["# A", "## **Notes**", "## Notes", "## Notes"].join("\n"), "A#Notes[2]")).toBe(4);
  });

  it("takes the first of two headings of the same name", () => {
    expect(headingLine(["# Plan", "## Roadmap", "a", "# Later", "## Roadmap", "b"].join("\n"), "Roadmap")).toBe(2);
  });

  it("follows a nested address under its parent", () => {
    const body = ["# A", "## Notes", "# B", "## Notes"].join("\n");
    expect(headingLine(body, "B#Notes")).toBe(4);
    expect(headingLine(body, "A#Notes")).toBe(2);
    expect(headingLine(body, "C#Notes")).toBeNull();
  });

  it("takes an index for a duplicate under one parent", () => {
    const body = ["# A", "## Notes", "## Notes", "# B", "## Notes"].join("\n");
    expect(headingLine(body, "A#Notes[2]")).toBe(3);
    expect(headingLine(body, "B#Notes[1]")).toBe(5);
    expect(headingLine(body, "B#Notes[2]")).toBeNull();
  });

  it("matches exactly first, then ignoring case", () => {
    const body = ["## roadmap", "## Roadmap"].join("\n");
    expect(headingLine(body, "Roadmap")).toBe(2);
    expect(headingLine(body, "ROADMAP")).toBe(1);
  });

  it("reads an escaped # as text", () => {
    expect(headingLine(["# C", "## C\\#"].join("\n"), "C\\#")).toBe(2);
  });

  it("is null for a heading the note doesn't have", () => {
    expect(headingLine("# Plan\n\ntext", "Roadmap")).toBeNull();
  });
});

describe("a link to a heading whose text a link can't hold", () => {
  const body = ["# Top", "", "## C# notes | x", "c", "## [draft] v2", "d", "## Notes [2]", "n", "## C\\# tips", "t", "## Notes", "## Notes", "## The **road**map", "r", "## `a|b` x", "a"].join("\n");
  const linkOf = (line: number) => headingLink(outline(body).find((h) => h.line === line)!.text);

  it("is the heading as shown, without # | ^ [ ], %% and Markdown's escapes", () => {
    expect([3, 5, 7, 9, 13, 15].map(linkOf)).toEqual(["C notes x", "draft v2", "Notes 2", "C tips", "The roadmap", "ab x"]);
  });

  it.each([3, 5, 7, 9, 13, 15])("finds the heading at line %i, and its section", (line) => {
    expect(headingLine(body, linkOf(line))).toBe(line);
    expect(headingLine(body, linkOf(line).toUpperCase())).toBe(line);
    expect(section(body, linkOf(line))?.split("\n")[line - 1]).toBe(body.split("\n")[line - 1]);
  });

  it("finds a heading by a link that holds it as written too, its Markdown kept", () => {
    expect(headingLine(body, "the **road**map")).toBe(13);
    expect(headingLine(body, "`ab` X")).toBe(15);
    expect(section(body, "`ab` x")?.split("\n")[14]).toBe("## `a|b` x");
    expect(section(body, "The **road**map")?.split("\n")[12]).toBe("## The **road**map");
  });

  it("still reads a trailing [n] as an index", () => {
    expect(headingLine(body, "Notes[2]")).toBe(12);
    expect(headingLine(body, "Notes[1]")).toBe(11);
  });
});
