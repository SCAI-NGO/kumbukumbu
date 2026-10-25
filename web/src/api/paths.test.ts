// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { crumbOf, folderOf, noteUrl, pathFromRoute, titleOf, untitledName } from "./paths";

describe("note paths", () => {
  it("puts a note into the URL one encoded segment at a time", () => {
    expect(noteUrl("/My Vault/a b/c%?#.md")).toBe("/notes/My%20Vault/a%20b/c%25%3F%23.md");
  });

  it("reads the path back from the route's decoded params", () => {
    expect(pathFromRoute("My Vault", "a b/c%?#.md")).toBe("/My Vault/a b/c%?#.md");
  });

  it("names a note by its file name without .md, and its place by vault and folders", () => {
    expect(titleOf("/Work/projects/plan.md")).toBe("plan");
    expect(crumbOf("/Work/projects/deep/plan.md")).toBe("Work / projects / deep");
    expect(folderOf("/Work/projects/plan.md")).toBe("/Work/projects");
    expect(folderOf("/Work/plan.md")).toBe("/Work");
  });

  it("picks the first free Untitled name in the folder", () => {
    expect(untitledName([])).toBe("Untitled.md");
    expect(untitledName(["plan.md"])).toBe("Untitled.md");
    expect(untitledName(["Untitled.md", "untitled 2.md", "Untitled 4.md"])).toBe("Untitled 3.md");
  });
});
