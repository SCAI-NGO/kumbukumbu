// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { flowMap, parseProperties } from "./properties";

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

  it("takes a # inside a quoted value as text, not as a comment", () => {
    expect(parseProperties("title: \"Issue #42\"\nshort: 'a #b'\nitems:\n  - \"x #1\"")).toEqual([
      { key: "title", value: "Issue #42" },
      { key: "short", value: "a #b" },
      { key: "items", value: ["x #1"] },
    ]);
    expect(parseProperties('title: "a" # note')).toBeNull();
  });

  it("undoes YAML's escapes inside double quotes, and only there", () => {
    expect(parseProperties('quote: "say \\"hi\\""\npath: "C:\\\\temp"\nraw: \'a\\"b\'\nbare: a\\"b')).toEqual([
      { key: "quote", value: 'say "hi"' },
      { key: "path", value: "C:\\temp" },
      { key: "raw", value: 'a\\"b' },
      { key: "bare", value: 'a\\"b' },
    ]);
  });
});

describe("flowMap", () => {
  it("reads a flat flow map, quoted keys too", () => {
    expect(flowMap('{ Running: 2026-11-14, "In progress": 2026-11-12 }')).toEqual({ Running: "2026-11-14", "In progress": "2026-11-12" });
    expect(flowMap("{Done: '2026-11-01T09:30:00'}")).toEqual({ Done: "2026-11-01T09:30:00" });
    expect(flowMap("{}")).toEqual({});
  });

  it("is null for a nested map or a comma inside quotes", () => {
    expect(flowMap("{ Running: { at: 2026-11-14 } }")).toBeNull();
    expect(flowMap('{ "Running, late": 2026-11-14 }')).toBeNull();
    expect(flowMap("{ Running }")).toBeNull();
    expect(flowMap("Running: 2026-11-14")).toBeNull();
  });
});
