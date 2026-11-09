// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { prepareMath } from "./prepareMath";

const hidden = "\uFDD0";
const hide = (s: string) => s.replaceAll("$", hidden);

describe("prepareMath", () => {
  it.each([
    ["costs $5 and $10", hide("costs $5 and $10"), false],
    ["$3", hide("$3"), false],
    ["$x$", "$x$", true],
    ["$ x $", hide("$ x $"), false],
    ["$x$5", hide("$x$5"), false],
    ["$a\n\nb$", hide("$a\n\nb$"), false],
    ["$a\nb$", "$a\nb$", true],
    ["`$x$`", "`$x$`", false],
    ["```\n$x$\n```", "```\n$x$\n```", false],
    ["~~~\n$x$ and $5\n~~~", "~~~\n$x$ and $5\n~~~", false],
    ["$$\nx\n$$", "$$\nx\n$$", true],
    ["$$\na$b\n$$", "$$\na$b\n$$", true],
    ["$$x$$", "$$x$$", true],
    ["$$\nx", hide("$$") + "\nx", false],
    ["\\[\nA\\\\B\n\\]", "$$\nA\\\\B\n$$", true],
    ["\\\\[5pt]", "\\\\[5pt]", false],
    ["an inline \\[x\\] stays", "an inline \\[x\\] stays", false],
    ["\\$x\\$", "\\$x\\$", false],
    ["$x$ in $$y$$", "$x$ in $$y$$", true],
    ["> $$\n> x\n> $$", "> $$\n> x\n> $$", true],
    ["- item\n\n  $$\n  x\n  $$", "- item\n\n  $$\n  x\n  $$", true],
    ["[a](https://x/?p=$5)", "[a](https://x/?p=$5)", false],
    ["<https://x/$5>", "<https://x/$5>", false],
    ["[$5 plan](/a.md) and $y$", "[" + hidden + "5 plan](/a.md) and $y$", true],
    ["see https://x/$5 and $x$", "see https://x/$5 and $x$", true],
    ["$a $ b$", "$a " + hidden + " b$", true],
    ["# Price $5\n$y$ costs", "# Price " + hidden + "5\n$y$ costs", true],
    ["[[A$B]] and [[C$D]]", "[[A" + hidden + "B]] and [[C" + hidden + "D]]", false],
    ["[[Plan$x$]] and $y$", "[[Plan" + hidden + "x" + hidden + "]] and $y$", true],
    ["$a [[b]] c$", hide("$a [[b]] c$"), false],
    ["#a$b and #c$d", hide("#a$b and #c$d"), false],
    ["[$x][a] and y$", hide("[$x][a] and y$"), false],
    ["> ```\n> $x$\n\n$y$", "> ```\n> $x$\n\n$y$", true],
    ["> ```\n> code\n\nprice $5\n```\n$y$ ```", "> ```\n> code\n\nprice " + hidden + "5\n```\n$y$ ```", false],
  ])("%j", (input, source, maths) => {
    const prepared = prepareMath(input);
    expect(prepared).toEqual({ source, maths });
    expect(prepared.source).toHaveLength(input.length);
    expect(prepared.source.split("\n")).toHaveLength(input.split("\n").length);
  });

  it("leaves a source alone that already holds the placeholder, without maths", () => {
    expect(prepareMath(`a${hidden} $x$`)).toEqual({ source: `a${hidden} $x$`, maths: false });
  });

  it("returns a source without $ or \\[ as it is", () => {
    expect(prepareMath("plain [[link]] #tag")).toEqual({ source: "plain [[link]] #tag", maths: false });
  });

  describe("takes time in proportion to the note", () => {
    const quick = (source: string) => {
      const started = performance.now();
      const prepared = prepareMath(source);
      expect(performance.now() - started).toBeLessThan(1500);
      expect(prepared.source).toHaveLength(source.length);
    };
    it("a long table of amounts", () => quick("| a | b |\n|---|---|\n" + "| $5 | $6 |\n".repeat(8000)));
    it("long prose with links and amounts", () =>
      quick("Alice paid $5 for [seeds](/Home/Garden/seeds.md) and `code` <https://x/$1> then $x$ and $3 [[plan]] #veg ".repeat(4000)));
    it("many formulas in one paragraph", () => quick("$x$ and ".repeat(100000)));
    it("unclosed $$ fences on every line", () => quick("$$ x\n".repeat(70000)));
    it("unclosed code spans", () => quick("a ` b $5 ".repeat(40000)));
  });
});
