// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { createdPath, subpathBlock } from "./wikiLinks";

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
    ["[[Plan#Goals]]", null],
    ["[[Plan]]", null],
    ["[c](Plan.md#Goals)", null],
    ["[c](Plan.md)", null],
  ])("reads %s as %s", (raw, expected) => {
    expect(subpathBlock(raw)).toBe(expected);
  });
});
