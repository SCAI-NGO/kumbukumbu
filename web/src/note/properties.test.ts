// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { parseProperties } from "./properties";

describe("parseProperties", () => {
  it("reads texts, inline lists and block lists, unquoting values", () => {
    const yaml = 'title: "Plan: Q4"\ntags: [work, "q4"]\naliases:\n  - Roadmap\n  - Q4 plan\nsource: https://example.org/a\nempty:\nnone: []';
    expect(parseProperties(yaml)).toEqual([
      { key: "title", value: "Plan: Q4" },
      { key: "tags", value: ["work", "q4"] },
      { key: "aliases", value: ["Roadmap", "Q4 plan"] },
      { key: "source", value: "https://example.org/a" },
      { key: "empty", value: "" },
      { key: "none", value: [] },
    ]);
  });

  it("takes an unindented block list too, and skips blank lines", () => {
    expect(parseProperties("tags:\n- a\n- b\n\ndone: true")).toEqual([
      { key: "tags", value: ["a", "b"] },
      { key: "done", value: "true" },
    ]);
  });

  it("gives up on what it can't show faithfully", () => {
    expect(parseProperties("author:\n  name: Ada")).toBeNull();
    expect(parseProperties("text: |\n  line")).toBeNull();
    expect(parseProperties("# a comment")).toBeNull();
    expect(parseProperties("just text")).toBeNull();
    expect(parseProperties("ref: *anchor")).toBeNull();
    expect(parseProperties('tags: ["a, b", c]')).toBeNull();
    expect(parseProperties("status: draft # for now")).toBeNull();
    expect(parseProperties("tags:\n  - a # first")).toBeNull();
  });

  it("keeps a # that is no comment, as in a URL's fragment", () => {
    expect(parseProperties("source: https://example.org/a#part")).toEqual([{ key: "source", value: "https://example.org/a#part" }]);
  });
});
