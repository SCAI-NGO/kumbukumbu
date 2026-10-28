// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { findTags, frontmatterTags, normaliseTag, splitTagQuery } from "./tags";

const tags = (text: string) => findTags(text).map((t) => t.tag);

describe("inline tags, as the server reads them", () => {
  it.each([
    ["#research", ["research"]],
    ["text #research, more", ["research"]],
    ["word#tag", []],
    ["#123 #2026", []],
    ["#v2 #2026-plan", ["v2", "2026-plan"]],
    ["#project/kumbu #project/", ["project/kumbu", "project"]],
    ["#Grüße #🚀launch", ["Grüße", "🚀launch"]],
    ["#tag's #tag…", ["tag", "tag"]],
    ["https://example.org/page#part", []],
    ["[[Note#Heading]] and [[Note #x]]", []],
    ["%% #hidden %% #shown", ["shown"]],
  ])("%s", (text, expected) => expect(tags(text)).toEqual(expected));

  it("gives where each tag stands", () => {
    expect(findTags("see #a and #b.")).toEqual([
      { tag: "a", start: 4, end: 6 },
      { tag: "b", start: 11, end: 13 },
    ]);
  });
});

describe("a tag asked for", () => {
  it.each([
    ["#research", "research"],
    [" research ", "research"],
    ["project/", "project"],
    ["2026", null],
    ["two words", null],
  ])("%s", (asked, expected) => expect(normaliseTag(asked)).toBe(expected));
});

describe("a query that starts with a tag", () => {
  it("is the tag and the rest once a space ends the tag", () => {
    expect(splitTagQuery("#research capabilities")).toEqual({ tag: "research", rest: "capabilities", finished: true });
    expect(splitTagQuery("#research ")).toEqual({ tag: "research", rest: "", finished: true });
    expect(splitTagQuery("#res")).toEqual({ tag: "res", rest: "", finished: false });
  });

  it("is no tag otherwise", () => {
    expect(splitTagQuery("capabilities #research").tag).toBeNull();
    expect(splitTagQuery("#2026 plan").tag).toBeNull();
    expect(splitTagQuery("# heading").tag).toBeNull();
  });
});

describe("frontmatter tags", () => {
  it("reads a list or a string, without '#'", () => {
    expect(frontmatterTags(["research", "#idea", "2026"])).toEqual(["research", "idea"]);
    expect(frontmatterTags("research, idea")).toEqual(["research", "idea"]);
    expect(frontmatterTags("research idea")).toEqual(["research", "idea"]);
  });
});
