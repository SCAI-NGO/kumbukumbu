// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import type { TreeFolder, TreeNote } from "../api/notes";
import { rows } from "./TreeDrawer";
import { orderTree, useTreeOrder } from "./treeOrder";

const note = (path: string, writtenAt?: string | null): TreeNote => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown", writtenAt });
const folder = (path: string, writtenAt: string | null | undefined, folders: TreeFolder[], notes: TreeNote[]): TreeFolder => ({ name: path.split("/").at(-1)!, path, writtenAt, folders, notes });

// As the server sends it: by name, folders first; a folder's `writtenAt` is the newest write anywhere below it.
const root = folder(
  "/Notes",
  "2026-11-14T10:00:00Z",
  [
    folder("/Notes/Archive", "2026-11-01T09:00:00Z", [], [note("/Notes/Archive/old.md", "2026-11-01T09:00:00Z")]),
    folder("/Notes/Empty", null, [], []),
    folder("/Notes/Pump", "2026-11-14T10:00:00Z", [folder("/Notes/Pump/Deep", "2026-11-14T10:00:00Z", [], [note("/Notes/Pump/Deep/new.md", "2026-11-14T10:00:00Z")])], [note("/Notes/Pump/a.md", "2026-10-01T09:00:00Z")]),
    folder("/Notes/Recipes", "2026-11-01T09:00:00Z", [], [note("/Notes/Recipes/soup.md", "2026-11-01T09:00:00Z")]),
  ],
  [note("/Notes/a.md", "2026-11-02T09:00:00Z"), note("/Notes/b.md", null), note("/Notes/c.md", "2026-11-13T09:00:00Z")],
);

beforeEach(() => localStorage.clear());

describe("orderTree", () => {
  it("Name is today's order", () => {
    expect(orderTree(root, "name")).toEqual(root);
  });

  it("a folder is as new as its newest file at any depth", () => {
    expect(orderTree(root, "changed").folders.map((f) => f.name)).toEqual(["Pump", "Archive", "Recipes", "Empty"]);
  });

  it("files newest first, and ties keep the name's order", () => {
    expect(orderTree(root, "changed").notes.map((n) => n.name)).toEqual(["c.md", "a.md", "b.md"]);
    expect(orderTree(root, "changed").folders.slice(1, 3).map((f) => f.name)).toEqual(["Archive", "Recipes"]);
  });

  it("folders stay above files", () => {
    // The root's c.md is newer than Archive, Recipes and Empty; still every folder of a level comes before its files.
    const shown = rows(orderTree(root, "changed"), { "/Notes/Pump": true });
    const level = (depth: number) => shown.filter((r) => r.depth === depth).map((r) => (r.kind === "folder" ? r.folder.name : r.path.split("/").at(-1)));
    expect(level(0)).toEqual(["Pump", "Archive", "Recipes", "Empty", "c.md", "a.md", "b.md"]);
    expect(level(1)).toEqual(["Deep", "a.md"]);
  });

  it("an empty folder without a time goes last", () => {
    expect(orderTree(root, "changed").folders.at(-1)!.name).toBe("Empty");
  });
});

describe("useTreeOrder", () => {
  it("the choice is remembered on the device", () => {
    const first = renderHook(() => useTreeOrder());
    expect(first.result.current[0]).toBe("name");
    act(() => first.result.current[1]("changed"));
    expect(first.result.current[0]).toBe("changed");
    first.unmount();
    expect(renderHook(() => useTreeOrder()).result.current[0]).toBe("changed");
  });

  it("a blocked storage still orders, by name", () => {
    const get = Storage.prototype.getItem;
    Storage.prototype.getItem = () => {
      throw new Error("blocked");
    };
    try {
      expect(renderHook(() => useTreeOrder()).result.current[0]).toBe("name");
    } finally {
      Storage.prototype.getItem = get;
    }
  });
});
