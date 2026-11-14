// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { createdPath, subpathBlock, subpathHeading } from "./wikiLinks";

describe("createdPath", () => {
  it.each([
    ["new", "/V/a/new.md"],
    ["/Work/new.md", "/V/Work/new.md"],
    ["./x/new.md", "/V/a/x/new.md"],
    ["../new", "/V/new.md"],
    ["Meeting Notes.md", "/V/a/Meeting Notes.md"],
  ])("creates %s from /V/a/n.md as %s", (target, expected) => {
    expect(createdPath("/V/a/n.md", target)).toBe(expected);
  });
});

describe("subpathBlock", () => {
  it.each([
    ["[[Plan#^controller|x]]", "controller"],
    ["![[Plan#^controller]]", "controller"],
    ["[c](Plan.md#%5Econtroller)", "controller"],
    ["[c](Plan.md#^controller)", "controller"],
    ["[x](<My Plan.md#^controller>)", "controller"],
    ["[x](<My Plan.md#^controller> \"title\")", "controller"],
    ["[[Plan#Goals]]", null],
    ["[[Plan]]", null],
    ["[c](Plan.md#Goals)", null],
    ["[c](Plan.md)", null],
  ])("reads %s as %s", (raw, expected) => {
    expect(subpathBlock(raw)).toBe(expected);
  });
});

describe("subpathHeading", () => {
  it("reads a heading, with or without an alias", () => {
    expect(subpathHeading("[[K²#Roadmap]]")).toBe("Roadmap");
    expect(subpathHeading("[[K²#Roadmap|roadmap]]")).toBe("Roadmap");
    expect(subpathHeading("[[Plan# Next steps |x]]")).toBe("Next steps");
  });

  it("keeps a nested address whole", () => {
    expect(subpathHeading("[[Note#A#B]]")).toBe("A#B");
    expect(subpathHeading("[[Note#A#B|both]]")).toBe("A#B");
  });

  it("reads a Markdown link's fragment, decoded", () => {
    expect(subpathHeading("[x](Plan.md#Goals)")).toBe("Goals");
    expect(subpathHeading("[x](Plan.md#Next%20steps)")).toBe("Next steps");
    expect(subpathHeading("[x](<My Plan.md#Next steps>)")).toBe("Next steps");
  });

  it("is null for a block or no subpath", () => {
    expect(subpathHeading("[[Plan#^id]]")).toBeNull();
    expect(subpathHeading("[c](Plan.md#%5Eid)")).toBeNull();
    expect(subpathHeading("[[Plan]]")).toBeNull();
    expect(subpathHeading("[[Plan#]]")).toBeNull();
    expect(subpathHeading("[c](Plan.md)")).toBeNull();
  });
});
