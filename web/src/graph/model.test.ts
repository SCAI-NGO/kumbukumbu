// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import type { TreeFolder, TreeView } from "../api/notes";
import type { LocalGraphView } from "./api";
import { toModel, treeEntry } from "./model";

const note = (path: string, size = 10, writtenAt: string | null = "2026-01-01T00:00:00Z") => ({ name: path.split("/").pop()!, path, size, kind: "note", mediaType: "text/markdown", writtenAt });
const folder = (path: string, folders: TreeFolder[], notes: ReturnType<typeof note>[]): TreeFolder => ({ name: path.split("/").pop()!, path, folders, notes }) as TreeFolder;
const tree: TreeView = {
  root: folder("", [folder("/Work", [folder("/Work/projects", [], [note("/Work/projects/A.md", 1234)])], [note("/Work/B.md", 5)])], []),
  damaged: 0,
};

const view = (nodes: Partial<LocalGraphView["nodes"][number]>[], edges: number[][] = [], leftOut: Record<string, number> = {}): LocalGraphView =>
  ({ nodes: nodes.map((n) => ({ path: null, title: "", kind: "note", tags: [], archived: false, in: 0, out: 0, ...n })), edges, leftOut }) as LocalGraphView;

describe("toModel", () => {
  it("gives two ghosts of one title distinct ids and the same stable key", () => {
    const { nodes } = toModel(view([{ title: "Anna", kind: "ghost" }, { title: "Anna", kind: "ghost" }]), undefined);
    expect(new Set(nodes.map((n) => n.id)).size).toBe(2);
    expect(nodes.map((n) => n.key)).toEqual(["ghost:Anna", "ghost:Anna"]);
  });

  it("uses the path as id and key of a note", () => {
    const [n] = toModel(view([{ path: "/Work/B.md", title: "B" }]), tree).nodes;
    expect(n.id).toBe("/Work/B.md");
    expect(n.key).toBe("/Work/B.md");
    expect(n.size).toBe(5);
    expect(n.writtenAt).toBe("2026-01-01T00:00:00Z");
  });

  it("reads leftOut from the string-keyed map and defaults missing numbers to 0", () => {
    const [a, b] = toModel(view([{ path: "/Work/B.md", in: 2, out: 3, depth: 1 }, { path: "/Work/x.md" }], [], { "0": 4 }), tree).nodes;
    expect(a).toMatchObject({ leftOut: 4, in: 2, out: 3, depth: 1 });
    expect(b).toMatchObject({ leftOut: 0, in: 0, out: 0, depth: 0 });
  });

  it("gives null size and time to a node the tree does not know", () => {
    const [n] = toModel(view([{ path: "/Work/gone.md" }]), tree).nodes;
    expect(n.size).toBeNull();
    expect(n.writtenAt).toBeNull();
    const [m] = toModel(view([{ path: "/Work/B.md" }]), undefined).nodes;
    expect(m.size).toBeNull();
  });

  it("maps edge indices to ids", () => {
    const { edges } = toModel(view([{ path: "/Work/B.md" }, { title: "Anna", kind: "ghost" }], [[0, 1, 3]]), tree);
    expect(edges).toEqual([{ source: "/Work/B.md", target: "ghost:1", flags: 3 }]);
  });

  it("drops an edge whose index points past the nodes", () => {
    const { edges } = toModel(view([{ path: "/Work/B.md" }, { title: "Anna", kind: "ghost" }], [[0, 1, 0], [0, 2, 0], [-1, 0, 0]]), tree);
    expect(edges).toEqual([{ source: "/Work/B.md", target: "ghost:1", flags: 0 }]);
  });
});

describe("treeEntry", () => {
  it("finds a note below nested folders", () => {
    expect(treeEntry(tree.root, "/Work/projects/A.md")?.size).toBe(1234);
  });
  it("gives null for a missing one", () => {
    expect(treeEntry(tree.root, "/Work/projects/Z.md")).toBeNull();
    expect(treeEntry(tree.root, "/Other/A.md")).toBeNull();
  });
});
