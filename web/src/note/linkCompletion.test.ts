// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { completeLink, linkQuery, linkTarget, rankNotes } from "./linkCompletion";

describe("linkQuery", () => {
  it("finds an open [[ on the caret's line", () => {
    expect(linkQuery("See [[pla", 9)).toEqual({ start: 4, query: "pla" });
    expect(linkQuery("See [[", 6)).toEqual({ start: 4, query: "" });
    expect(linkQuery("a\nb [[x", 7)).toEqual({ start: 4, query: "x" });
  });

  it("ignores a closed link, an alias or heading, and another line's [[", () => {
    expect(linkQuery("[[plan]] and", 12)).toBeNull();
    expect(linkQuery("[[plan|Pl", 9)).toBeNull();
    expect(linkQuery("[[plan#Go", 9)).toBeNull();
    expect(linkQuery("[[plan\nnext", 11)).toBeNull();
    expect(linkQuery("no link", 7)).toBeNull();
  });
});

describe("linkTarget", () => {
  const all = ["/W/plan.md", "/W/a/status.md", "/W/b/status.md", "/W/x/a/status.md", "/W/Notes/Idea.md"];

  it("is the name when no other note ends with it", () => {
    expect(linkTarget("/W/plan.md", all)).toBe("plan");
    expect(linkTarget("/W/Notes/Idea.md", all)).toBe("Idea");
  });

  it("takes as many folders as it needs to tell repeated names apart, ignoring case", () => {
    expect(linkTarget("/W/b/status.md", all)).toBe("b/status");
    expect(linkTarget("/W/x/a/status.md", all)).toBe("x/a/status");
    expect(linkTarget("/W/deep/plan.md", ["/W/Plan.md", "/W/deep/plan.md"])).toBe("deep/plan");
  });

  it("goes from the vault's root where even the whole path is the end of another", () => {
    expect(linkTarget("/W/a/status.md", all)).toBe("/a/status");
    expect(linkTarget("/W/Plan.md", ["/W/Plan.md", "/W/deep/plan.md"])).toBe("/Plan");
  });
});

describe("rankNotes", () => {
  const all = ["/W/roadmap.md", "/W/plan.md", "/W/projects/beta.md", "/W/old/plans.md", "/W/x/unplanned.md"];

  it("puts names starting with the query first, then names containing it, then folders", () => {
    expect(rankNotes(all, "plan")).toEqual(["/W/plan.md", "/W/old/plans.md", "/W/x/unplanned.md"]);
    expect(rankNotes(all, "proj")).toEqual(["/W/projects/beta.md"]);
  });

  it("lists every note for an empty query, in text order, at most eight", () => {
    expect(rankNotes(all, "")).toEqual(["/W/projects/beta.md", "/W/plan.md", "/W/old/plans.md", "/W/roadmap.md", "/W/x/unplanned.md"]);
    expect(rankNotes(Array.from({ length: 12 }, (_, i) => `/W/n${i}.md`), "")).toHaveLength(8);
  });
});

describe("completeLink", () => {
  it("replaces the typed part, and the ]] the toolbar left, with the whole link", () => {
    expect(completeLink("See [[pl", 8, { start: 4, query: "pl" }, "plan")).toEqual({ start: 4, end: 8, text: "[[plan]]", selStart: 12, selEnd: 12 });
    expect(completeLink("See [[pl]] x", 8, { start: 4, query: "pl" }, "plan")).toMatchObject({ start: 4, end: 10 });
  });
});
