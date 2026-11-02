// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import fixture from "./console-fixture.json";
import { commandTable, resolveLine, resolvePath } from "./paths";

const cwd = "/Gartenarbeit/Shopping";

// Spec §7, row by row: what is typed, and the line sent.
const rows: [string, string][] = [
  ["ls", "ls /Gartenarbeit/Shopping"],
  ["ls ..", "ls /Gartenarbeit"],
  ["rg foo", "rg foo /Gartenarbeit/Shopping"],
  ["rg foo /", "rg foo /"],
  ["rg -n foo a.md b.md", "rg -n foo /Gartenarbeit/Shopping/a.md /Gartenarbeit/Shopping/b.md"],
  ["rg --files", "rg --files /Gartenarbeit/Shopping"],
  ["rg --files sub", "rg --files /Gartenarbeit/Shopping/sub"],
  ["rg --tag x foo", "rg --tag x foo /Gartenarbeit/Shopping"],
  ["rg -e foo a.md", "rg -e foo /Gartenarbeit/Shopping/a.md"],
  ["rg -e foo", "rg -e foo /Gartenarbeit/Shopping"],
  ["rg -- --help", "rg -- --help /Gartenarbeit/Shopping"],
  ["semsearch how do plants grow", "semsearch how do plants grow /Gartenarbeit/Shopping"],
  ["semsearch Tomaten plan.md", "semsearch Tomaten plan.md /Gartenarbeit/Shopping"],
  ["semsearch Tomaten ./plan.md", "semsearch Tomaten /Gartenarbeit/Shopping/plan.md"],
  ["semsearch -n 3 Tomaten", "semsearch -n 3 Tomaten /Gartenarbeit/Shopping"],
  ["tags", "tags /Gartenarbeit/Shopping"],
  ["tags x", "tags x /Gartenarbeit/Shopping"],
  ["tags ./sub", "tags /Gartenarbeit/Shopping/sub"],
  ["tags x sub", "tags x /Gartenarbeit/Shopping/sub"],
  ["history", "history /Gartenarbeit/Shopping"],
  ["history --since 2h a.md", "history --since 2h /Gartenarbeit/Shopping/a.md"],
  ["trash", "trash /Gartenarbeit/Shopping"],
  ["lscp", "lscp /Gartenarbeit/Shopping"],
  ["board", "board /Gartenarbeit"],
  ["board --column Doing", "board --column Doing /Gartenarbeit"],
  ["cat plan.md#Goals", "cat /Gartenarbeit/Shopping/plan.md#Goals"],
  ['cat "a b.md"', "cat '/Gartenarbeit/Shopping/a b.md'"],
  ["cat --version v1 a.md", "cat --version v1 /Gartenarbeit/Shopping/a.md"],
  ["cat --checkpoint=c a.md", "cat --checkpoint=c /Gartenarbeit/Shopping/a.md"],
  ["cat", "cat"],
  ["head -n 5 a.md", "head -n 5 /Gartenarbeit/Shopping/a.md"],
  ["head -5 a.md", "head -5 /Gartenarbeit/Shopping/a.md"],
  ["head", "head"],
  ["tail -n +3 a.md", "tail -n +3 /Gartenarbeit/Shopping/a.md"],
  ["cat a.md | rg foo", "cat /Gartenarbeit/Shopping/a.md | rg foo"],
  ["cat a.md | head -n 2", "cat /Gartenarbeit/Shopping/a.md | head -n 2"],
  ["pdf a.pdf 3-5", "pdf /Gartenarbeit/Shopping/a.pdf 3-5"],
  ["pdf info a.pdf", "pdf info /Gartenarbeit/Shopping/a.pdf"],
  ["img a.png --size small", "img /Gartenarbeit/Shopping/a.png --size small"],
  ["diff a.md", "diff /Gartenarbeit/Shopping/a.md"],
  ["diff a.md a1b2c3", "diff /Gartenarbeit/Shopping/a.md a1b2c3"],
  ["diff a.md b.md", "diff /Gartenarbeit/Shopping/a.md /Gartenarbeit/Shopping/b.md"],
  ["diff a.md ../b.md", "diff /Gartenarbeit/Shopping/a.md /Gartenarbeit/b.md"],
  ["uname -a", "uname -a"],
  ["help rg", "help rg"],
  ["rm a.md", "rm a.md"],
  ["mv a b", "mv a b"],
  ["unknown a", "unknown a"],
  ["cat a.md | rm x", "cat a.md | rm x"],
  ["ls | unknown", "ls | unknown"],
  ["ls | toString", "ls | toString"],
  ["cat a > b", "cat a > b"],
  ['cat "open', 'cat "open'],
  ['cat   "/abs/x.md"', 'cat   "/abs/x.md"'],
];

// The option grammar of Options.TryParse, beyond the rows: values attached, combined flags, options after operands.
const grammar: [string, string][] = [
  ["head -n5 a.md", "head -n5 /Gartenarbeit/Shopping/a.md"],
  ["head --lines=5 a.md", "head --lines=5 /Gartenarbeit/Shopping/a.md"],
  ["head --lines 5 a.md", "head --lines 5 /Gartenarbeit/Shopping/a.md"],
  ["head a.md -n 5", "head /Gartenarbeit/Shopping/a.md -n 5"],
  ["rg -inm 2 foo", "rg -inm 2 foo /Gartenarbeit/Shopping"],
  ["rg -C2 foo a.md", "rg -C2 foo /Gartenarbeit/Shopping/a.md"],
  ["rg --regexp=foo a.md", "rg --regexp=foo /Gartenarbeit/Shopping/a.md"],
  ["rg foo --checkpoint c", "rg foo --checkpoint c /Gartenarbeit/Shopping"],
  ["rg", "rg"],
  ["rg -m", "rg -m"],
  ["semsearch", "semsearch"],
  ["semsearch Tomaten /Work", "semsearch Tomaten /Work"],
  ["semsearch Tomaten ../plan.md", "semsearch Tomaten /Gartenarbeit/plan.md"],
  ["tags x /Work", "tags x /Work"],
  ["tags /Work x", "tags /Work x"],
  ["diff a.md v1 v2", "diff /Gartenarbeit/Shopping/a.md v1 v2"],
  ["diff --checkpoint c a.md", "diff --checkpoint c /Gartenarbeit/Shopping/a.md"],
  ["pdf --images=all a.pdf", "pdf --images=all /Gartenarbeit/Shopping/a.pdf"],
  ["outline ./a.md", "outline /Gartenarbeit/Shopping/a.md"],
  ["wc -l a.md | sort -r", "wc -l /Gartenarbeit/Shopping/a.md | sort -r"],
  ["cat a.md | sort b.md", "cat /Gartenarbeit/Shopping/a.md | sort /Gartenarbeit/Shopping/b.md"],
  ["cat a.md | rg foo | head", "cat /Gartenarbeit/Shopping/a.md | rg foo | head"],
  ["ls | rg foo", "ls /Gartenarbeit/Shopping | rg foo"],
  ["rg 'a|b'", "rg 'a|b' /Gartenarbeit/Shopping"],
  ["cat \"Tom's.md\"", "cat \"/Gartenarbeit/Shopping/Tom's.md\""],
];

describe("resolveLine", () => {
  it.each(rows)("sends %j as %j", (typed, sent) => {
    expect(resolveLine(typed, cwd)).toBe(sent);
  });

  it.each(grammar)("reads the options of %j as the shell does", (typed, sent) => {
    expect(resolveLine(typed, cwd)).toBe(sent);
  });

  it.each([
    ["ls", "ls"],
    ["rg foo", "rg foo"],
    ["board", "board"],
    ["tags", "tags"],
    ["ls Work", "ls /Work"],
  ])("adds nothing at the root: %j", (typed, sent) => {
    expect(resolveLine(typed, "/")).toBe(sent);
  });

  it("sends a line unchanged when every path is already absolute", () => {
    expect(resolveLine('rg  -n   foo "/Work/a.md"', cwd)).toBe('rg  -n   foo "/Work/a.md"');
  });

  it("leaves an empty line alone", () => {
    expect(resolveLine("   ", cwd)).toBe("   ");
  });
});

describe("resolvePath", () => {
  it.each([
    [".", cwd, cwd],
    ["..", cwd, "/Gartenarbeit"],
    ["../..", cwd, "/"],
    ["../../..", cwd, "/"],
    ["..", "/", "/"],
    ["./a/../b", cwd, "/Gartenarbeit/Shopping/b"],
    ["sub/", cwd, "/Gartenarbeit/Shopping/sub"],
    ["/", cwd, "/"],
    ["/Work/", cwd, "/Work"],
    ["/Work/x/../y.md", cwd, "/Work/y.md"],
    ["Tom's list.md", cwd, "/Gartenarbeit/Shopping/Tom's list.md"],
    ["plan.md#Goals", cwd, "/Gartenarbeit/Shopping/plan.md#Goals"],
    ["../plan.md#Goals#../x", cwd, "/Gartenarbeit/plan.md#Goals#../x"],
    ["a#b.png", cwd, "/Gartenarbeit/Shopping/a#b.png"],
    ["a", "/", "/a"],
  ])("resolves %j in %j to %j", (path, dir, resolved) => {
    expect(resolvePath(path, dir)).toBe(resolved);
  });
});

describe("commandTable", () => {
  it("has an entry for every command the console allows, and no other", () => {
    expect(Object.keys(commandTable).sort()).toEqual([...fixture.allowed].sort());
  });
});
