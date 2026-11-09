// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { outline, section } from "./outline";
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
