// SPDX-License-Identifier: AGPL-3.0-or-later
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { blockSource, findBlock, isDelimiterRow } from "./blocks";

type Case = { name: string; note: string; id: string; lines: [number, number] | null };

const cases: Case[] = JSON.parse(readFileSync(join(import.meta.dirname, "blocks.cases.json"), "utf8"));

// The Core's finder reads the same cases (tests/Kumbu.Core.Tests/Sections/BlocksTests.cs).
describe("findBlock", () => {
  it.each(cases.map((c) => [c.name, c] as const))("every shared case: %s", (_, c) => {
    const found = findBlock(c.note, c.id);
    expect(found === null ? null : [found.start, found.end]).toEqual(c.lines);
  });
});

describe("table rows", () => {
  it("a delimiter row reads with or without outer pipes, with alignment and spaces", () => {
    expect(findBlock("a | b\n--- | ---\n1 | 2\n\n^tab\n", "tab")).toEqual({ start: 1, end: 5 });
    expect(findBlock("  | a | b |\n  |  :--- | ---: |  \n  | 1 | 2 |\n\n^tab\n", "tab")).toEqual({ start: 1, end: 5 });
    expect(findBlock("a | b\nx | y\n^p", "p")).toEqual({ start: 1, end: 3 });
    expect(isDelimiterRow("|:-:|")).toBe(true);
    expect(isDelimiterRow("| --- | :-- |")).toBe(true);
    expect(isDelimiterRow("| --- | |")).toBe(false);
    expect(isDelimiterRow("| :: |")).toBe(false);
    expect(isDelimiterRow("\v| --- |\v")).toBe(true);
    expect(isDelimiterRow("|\v:--\v|\f")).toBe(true);
  });

  it("reads every short line as a delimiter row exactly as before", () => {
    const before = /^[ \t\v\f\r]*\|?[ \t\v\f\r]*:?-+:?[ \t\v\f\r]*(?:\|[ \t\v\f\r]*:?-+:?[ \t\v\f\r]*)*\|?[ \t\v\f\r]*$/;
    const chars = [" ", "\t", "\f", "\r", "|", "-", ":", "x"];
    let lines = [""];
    for (let length = 0; length <= 6; length++) {
      for (const line of lines) if (isDelimiterRow(line) !== before.test(line)) expect([line, isDelimiterRow(line)]).toEqual([line, before.test(line)]);
      lines = lines.flatMap((line) => chars.map((c) => line + c));
    }
  });
});

describe("blockSource", () => {
  it("blanks the lines outside the block, so its line numbers hold", () => {
    const note = "# Plan\n\nThe pump needs\na new controller. ^controller\n\nNext.";
    expect(blockSource(note, { start: 3, end: 4 })).toBe("\n\nThe pump needs\na new controller. ^controller\n\n");
  });

  it("takes off the block's common indentation, so a nested item stays a list", () => {
    const note = "- pump\n    - controller ^controller\n        - board\n\n      more\n- valve";
    expect(blockSource(note, { start: 2, end: 5 })).toBe("\n- controller ^controller\n    - board\n\n  more\n");
    expect(blockSource("- a\n\t- b ^b\n\t    - c", { start: 2, end: 3 })).toBe("\n- b ^b\n    - c");
  });
});
