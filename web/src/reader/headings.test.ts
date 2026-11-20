// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { atxHeading, headingText } from "./headings";

// Every string of up to `max` characters drawn from `chars`.
const strings = (chars: string[], max: number) => {
  const all: string[] = [];
  let level = [""];
  for (let length = 0; length <= max; length++) {
    for (const s of level) all.push(s);
    level = level.flatMap((s) => chars.map((c) => s + c));
  }
  return all;
};

describe("atxHeading", () => {
  it("reads a heading line's level and text", () => {
    expect(atxHeading("## Big idea ##  ")).toEqual({ level: 2, text: "Big idea" });
    expect(atxHeading("   ###### Plan")).toEqual({ level: 6, text: "Plan" });
    expect(atxHeading("# ")).toEqual({ level: 1, text: "" });
    expect(atxHeading("##")).toBeNull();
    expect(atxHeading("#5")).toBeNull();
    expect(atxHeading("####### a")).toBeNull();
    expect(atxHeading("    # a")).toBeNull();
  });

  it("reads every short line as the heading pattern did", () => {
    const pattern = /^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$/;
    for (const line of strings(["#", " ", "\t", "a", "\r"], 7)) {
      const m = pattern.exec(line);
      const expected = m ? { level: m[1].length, text: m[2] } : null;
      if (JSON.stringify(atxHeading(line)) !== JSON.stringify(expected)) expect([line, atxHeading(line)]).toEqual([line, expected]);
    }
  });
});

describe("headingText", () => {
  it("reads a heading by its words: links by their text, marks gone", () => {
    expect(headingText(" See [the *plan*](Plan[1].md) `now` ")).toBe("see the plan now");
  });

  it("reads every short heading as the link pattern did", () => {
    const old = (md: string) =>
      md
        .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
        .replace(/[*_`~=]/g, "")
        .trim()
        .toLowerCase();
    for (const text of strings(["[", "]", "(", ")", "a", " "], 7))
      if (headingText(text) !== old(text)) expect([text, headingText(text)]).toEqual([text, old(text)]);
  });
});
