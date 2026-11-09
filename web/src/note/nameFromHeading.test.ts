// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import type { TreeFolder } from "../api/notes";
import { cleanName, headingName, isUntitled, renameTarget } from "./nameFromHeading";

describe("isUntitled", () => {
  it.each(["/W/Untitled.md", "/W/untitled 2.md", "/W/Untitled 1.md", "/W/UNTITLED 12.md"])("takes %s for an untitled note", (path) => {
    expect(isUntitled(path)).toBe(true);
  });

  it.each(["/W/Untitled draft.md", "/W/Untitled 0.md", "/W/Untitled2.md", "/W/Markt.md", "/W/Untitled/plan.md"])("takes %s for a named one", (path) => {
    expect(isUntitled(path)).toBe(false);
  });
});

describe("headingName", () => {
  it.each([
    ["# Einkaufsliste\n\n- x", "Einkaufsliste"],
    ["---\ntitle: a\n---\n# Plan", "Plan"],
    ["Intro\n\n# Later", "Later"],
    ["# Plan\r\n\r\ntext", "Plan"],
    ["```\n# code\n```\n# Real", "Real"],
    ["~~~~\n# code\n~~~\n# still code\n~~~~\n# Real", "Real"],
    ["%%\n# hidden\n%%\n# Shown", "Shown"],
    ["# A %%note%% B", "A B"],
    ["# **Bold** [[Gemüse|Beet]] #", "Bold Beet"],
    ["   # Indented", "Indented"],
    ["# ==Tomaten== $x$", "Tomaten x"],
  ])("finds the name in %j", (content, name) => {
    expect(headingName(content)).toBe(name);
  });

  it.each([["# "], ["#\n# B"], ["Title\n==="], ["## Sub"], ["    # code"], ["#Title"], ["%% open\n# hidden"], ["Just text"]])("finds none in %j", (content) => {
    expect(headingName(content)).toBeNull();
  });
});

describe("cleanName", () => {
  it("drops the characters file systems and Obsidian refuse", () => {
    expect(cleanName('a/b\\c:d*e?f"g<h>i|j#k^l[m]n')).toBe("abcdefghijklmn");
  });

  it("collapses whitespace and strips dots and spaces at the ends", () => {
    expect(cleanName("  many   spaces  ")).toBe("many spaces");
    expect(cleanName("...hidden")).toBe("hidden");
    expect(cleanName(". .hidden")).toBe("hidden");
    expect(cleanName("ends with dots...")).toBe("ends with dots");
  });

  it("answers null when nothing is left", () => {
    expect(cleanName("?#")).toBeNull();
    expect(cleanName(" ... ")).toBeNull();
  });

  it("cuts at the last space within 100 characters, else at 100", () => {
    const words = Array.from({ length: 20 }, (_, i) => `word${String(i).padStart(2, "0")}`).join(" ");
    expect(words.length).toBeGreaterThan(100);
    const cut = cleanName(words)!;
    expect(cut).toBe(words.slice(0, words.lastIndexOf(" ", 100)));
    expect(cut.length).toBeLessThanOrEqual(100);
    expect(cleanName("x".repeat(120))).toBe("x".repeat(100));
  });

  it("counts characters, not UTF-16 units, and composes them", () => {
    expect(cleanName("🥕".repeat(120))).toBe("🥕".repeat(100));
    expect(cleanName("Gemüse")).toBe("Gemüse");
  });
});

const note = (path: string) => ({ name: path.slice(path.lastIndexOf("/") + 1), path, size: 1, kind: "note", mediaType: "text/markdown" });
const folder = (path: string, notes: string[], folders: TreeFolder[] = []): TreeFolder => ({
  name: path.slice(path.lastIndexOf("/") + 1),
  path,
  folders,
  notes: notes.map((n) => note(`${path}/${n}`)),
});
const home = (notes: string[], folders: TreeFolder[] = []) => folder("/Home", [], [folder("/Home/Garden", ["Untitled.md", ...notes], folders)]);

describe("renameTarget", () => {
  const untitled = "/Home/Garden/Untitled.md";

  it("names an untitled note after its heading, in its folder", () => {
    expect(renameTarget(untitled, "# Einkaufsliste", home([]))).toBe("/Home/Garden/Einkaufsliste.md");
  });

  it("counts on past names that are taken, ignoring case, folders included", () => {
    expect(renameTarget(untitled, "# Einkaufsliste", home(["einkaufsliste.md"]))).toBe("/Home/Garden/Einkaufsliste 2.md");
    expect(renameTarget(untitled, "# Einkaufsliste", home(["Einkaufsliste.md", "Einkaufsliste 2.md"]))).toBe("/Home/Garden/Einkaufsliste 3.md");
    expect(renameTarget(untitled, "# Einkaufsliste", home([], [folder("/Home/Garden/Einkaufsliste.md", [])]))).toBe("/Home/Garden/Einkaufsliste 2.md");
  });

  it("leaves a named note alone", () => {
    expect(renameTarget("/Home/Garden/Markt.md", "# Einkaufsliste", folder("/Home", [], [folder("/Home/Garden", ["Markt.md"])]))).toBeNull();
  });

  it("does nothing without a heading, or with an untitled one", () => {
    expect(renameTarget(untitled, "Einkaufsliste", home([]))).toBeNull();
    expect(renameTarget(untitled, "# ", home([]))).toBeNull();
    expect(renameTarget(untitled, "# Untitled", home([]))).toBeNull();
    expect(renameTarget(untitled, "# untitled 3", home([]))).toBeNull();
  });

  it("leaves a board's cards alone", () => {
    const work = (notes: string[]) => folder("/Work", [], [folder("/Work/Board", ["Untitled.md", ...notes])]);
    expect(renameTarget("/Work/Board/Untitled.md", "# Beet", work(["Board.md"]))).toBeNull();
    expect(renameTarget("/Work/Board/Untitled.md", "# Beet", work(["Plan.md"]))).toBe("/Work/Board/Beet.md");
  });
});
