// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import type { TreeFolder, TreeNote } from "../api/notes";
import { filterTree } from "./treeFilter";

const note = (path: string, extra: Partial<TreeNote> = {}): TreeNote => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown", ...extra });
const folder = (path: string, folders: TreeFolder[], notes: TreeNote[]): TreeFolder => ({ name: path.split("/").at(-1)!, path, folders, notes });

const root = folder(
  "/Notes",
  [
    folder("/Notes/Pump", [folder("/Notes/Pump/Old", [], [note("/Notes/Pump/Old/Manual.md", { flag: "red" })])], [note("/Notes/Pump/Certificate.md"), note("/Notes/Pump/photo.jpg", { kind: "image", mediaType: "image/jpeg" })]),
    folder("/Notes/Garden", [], [note("/Notes/Garden/Plan.md", { flag: "done" }), note("/Notes/Garden/pump list.md")]),
    folder("/Notes/Empty", [], []),
  ],
  [note("/Notes/inbox.md", { flag: "red" })],
);

/** Every note and attachment the filtered tree still holds. */
const paths = (f: TreeFolder): string[] => [...f.folders.flatMap(paths), ...f.notes.map((n) => n.path)];

describe("filterTree", () => {
  it("every word must match", () => {
    expect(paths(filterTree(root, "pump cert", null).root)).toEqual(["/Notes/Pump/Certificate.md"]);
    expect(paths(filterTree(root, "pump x", null).root)).toEqual([]);
    expect(paths(filterTree(root, "  pump   cert  ", null).root)).toEqual(["/Notes/Pump/Certificate.md"]);
  });

  it("case is ignored", () => {
    expect(paths(filterTree(root, "PUMP Cert", null).root)).toEqual(["/Notes/Pump/Certificate.md"]);
  });

  it("matches the path within the vault, without a note's .md", () => {
    expect(paths(filterTree(root, "notes", null).root)).toEqual([]);
    expect(paths(filterTree(root, "md", null).root)).toEqual([]);
    expect(paths(filterTree(root, "pump", null).root)).toEqual(["/Notes/Pump/Old/Manual.md", "/Notes/Pump/Certificate.md", "/Notes/Pump/photo.jpg", "/Notes/Garden/pump list.md"]);
  });

  it("folders on the way are kept and opened", () => {
    const found = filterTree(root, "manual", null);
    expect(found.root.folders.map((f) => f.path)).toEqual(["/Notes/Pump"]);
    expect(found.root.folders[0].notes).toEqual([]);
    expect([...found.open].sort()).toEqual(["/Notes/Pump", "/Notes/Pump/Old"]);
    expect(found.root.folders.some((f) => f.path === "/Notes/Empty")).toBe(false);
  });

  it("attachments match by name", () => {
    expect(paths(filterTree(root, "photo.jpg", null).root)).toEqual(["/Notes/Pump/photo.jpg"]);
  });

  it("the flag chip keeps flagged notes; any flag every flagged one", () => {
    expect(paths(filterTree(root, "", "red").root)).toEqual(["/Notes/Pump/Old/Manual.md", "/Notes/inbox.md"]);
    expect(paths(filterTree(root, "", "any").root)).toEqual(["/Notes/Pump/Old/Manual.md", "/Notes/Garden/Plan.md", "/Notes/inbox.md"]);
    expect(paths(filterTree(root, "", "white").root)).toEqual([]);
  });

  it("words and flag together", () => {
    expect(paths(filterTree(root, "pump", "red").root)).toEqual(["/Notes/Pump/Old/Manual.md"]);
    expect(paths(filterTree(root, "garden", "red").root)).toEqual([]);
  });
});
