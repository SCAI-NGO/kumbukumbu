// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { createdPath } from "./wikiLinks";

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
