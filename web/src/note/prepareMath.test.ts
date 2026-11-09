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
  ])("%j", (input, source, maths) => {
    const prepared = prepareMath(input);
    expect(prepared).toEqual({ source, maths });
    expect(prepared.source).toHaveLength(input.length);
    expect(prepared.source.split("\n")).toHaveLength(input.split("\n").length);
  });

  it("leaves a source alone that already holds the placeholder", () => {
    expect(prepareMath(`a${hidden} $5`)).toEqual({ source: `a${hidden} $5`, maths: true });
    expect(prepareMath(`a${hidden}`)).toEqual({ source: `a${hidden}`, maths: false });
  });
});
