// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import type { TreeFolder, TreeNote, TreeView } from "../api/notes";
import { suggestedLinks } from "./suggestions";

const note = (path: string, kind = "note") => ({ name: path.split("/").pop()!, path, kind, size: 1, writtenAt: null }) as unknown as TreeNote;
const folder = (path: string, notes: TreeNote[], folders: TreeFolder[] = []) => ({ name: path.split("/").pop()!, path, notes, folders }) as unknown as TreeFolder;

const tree = {
  root: folder("/Work", [note("/Work/a.md"), note("/Work/b.md"), note("/Work/pic.png", "image"), note("/Work/c.md")], [
    folder("/Work/sub", [note("/Work/sub/d.md"), note("/Work/sub/e.md")]),
  ]),
} as unknown as TreeView;

afterEach(() => {
  vi.unstubAllEnvs();
  localStorage.clear();
});

describe("suggestedLinks", () => {
  it("is empty without the mock", () => {
    vi.stubEnv("DEV", false);
    localStorage.setItem("kumbu.graph.mockSimilarity", "1");
    expect(suggestedLinks("/Work/a.md", new Set(), tree)).toEqual([]);
  });

  it("is empty while the tree is unknown", () => {
    vi.stubEnv("DEV", true);
    localStorage.setItem("kumbu.graph.mockSimilarity", "1");
    expect(suggestedLinks("/Work/a.md", new Set(), undefined)).toEqual([]);
  });

  it("offers at most three notes of the vault, not shown, not the note itself", () => {
    vi.stubEnv("DEV", true);
    localStorage.setItem("kumbu.graph.mockSimilarity", "1");
    const out = suggestedLinks("/Work/a.md", new Set(["/Work/b.md"]), tree);
    expect(out).toHaveLength(3);
    for (const s of out) {
      expect(["/Work/a.md", "/Work/b.md", "/Work/pic.png"]).not.toContain(s.path);
      expect(s.path.startsWith("/Work/")).toBe(true);
      expect(s.similarity.measured).toBe(false);
      expect(s.title).toBe(s.path.split("/").pop()!.replace(/\.md$/, ""));
    }
    expect(out.map((s) => s.similarity.score)).toEqual([...out.map((s) => s.similarity.score)].sort((x, y) => y - x));
  });
});
