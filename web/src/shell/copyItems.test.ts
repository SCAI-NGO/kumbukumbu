// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from "vitest";
import type { TreeFolder } from "../api/notes";
import { copyItems, filePathText, markdownLinkText, wikiLinkText } from "./copyItems";

/** A TreeFolder for the vault of the first path; `.png`/`.pdf` entries get kind "image"/"pdf", folders are made as needed. */
function tree(paths: string[]): TreeFolder {
  const vault = paths[0].split("/")[1];
  const root: TreeFolder = { name: vault, path: `/${vault}`, folders: [], notes: [] };
  for (const path of paths) {
    const segments = path.split("/").slice(2);
    let folder = root;
    for (const name of segments.slice(0, -1)) {
      const at = `${folder.path}/${name}`;
      let sub = folder.folders.find((f) => f.path === at);
      if (!sub) folder.folders.push((sub = { name, path: at, folders: [], notes: [] }));
      folder = sub;
    }
    const kind = path.endsWith(".png") ? "image" : path.endsWith(".pdf") ? "pdf" : "note";
    folder.notes.push({ name: segments.at(-1)!, path, size: 10, kind, mediaType: "text/markdown" });
  }
  return root;
}

describe("Copy file path", () => {
  it("copies the path with the vault", () => expect(filePathText("/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md")).toBe("/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md"));
});

describe("Copy link", () => {
  it("links by the bare name when no other note has it", () =>
    expect(wikiLinkText("/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md", tree(["/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md", "/SWE/Obsidian Parity.png"]))).toBe("[[Obsidian Parity]]"));

  it("links by the path inside the vault when the name exists twice, ignoring case and NFC", () =>
    expect(wikiLinkText("/SWE/Kumbu/Gemüse.md", tree(["/SWE/Kumbu/Gemüse.md", "/SWE/Home/GEMÜSE.md"]))).toBe("[[Kumbu/Gemüse]]"));

  it("leaves Copy link out for a name a wikilink can't hold", () => {
    for (const c of ["#", "|", "^", "[", "]"]) expect(wikiLinkText(`/SWE/a${c}b.md`, tree([`/SWE/a${c}b.md`]))).toBeNull();
  });

  it("leaves it out when the path form it needs can't be held either", () =>
    expect(wikiLinkText("/SWE/C#/plan.md", tree(["/SWE/C#/plan.md", "/SWE/Home/plan.md"]))).toBeNull());

  it("checks only the form it emits", () => expect(wikiLinkText("/SWE/C#/plan.md", tree(["/SWE/C#/plan.md"]))).toBe("[[plan]]"));

  it("falls back to the path form while the tree isn't loaded", () => expect(wikiLinkText("/SWE/Kumbu/plan.md", undefined)).toBe("[[Kumbu/plan]]"));
});

describe("Copy markdown link", () => {
  it("writes the Markdown link as the agents do", () =>
    expect(markdownLinkText("/SWE/Kumbu/Obsidian Parity/Obsidian Parity.md")).toBe("[Obsidian Parity](/Kumbu/Obsidian%20Parity/Obsidian%20Parity.md)"));

  it("encodes the destination as MarkdownLink.Encode does, non-ASCII raw", () =>
    expect(markdownLinkText("/W/Beet (2)/Gemüse #1.md")).toBe("[Gemüse #1](/Beet%20%282%29/Gemüse%20%231.md)"));

  it("encodes every character the server does, and control characters by their UTF-8 bytes", () =>
    expect(markdownLinkText("/W/%?<>^|\\`\"{}\t\u0085.md")).toBe("[%?<>^|\\\\`\"{}\t\u0085](/%25%3F%3C%3E%5E%7C%5C%60%22%7B%7D%09%C2%85.md)"));

  it("escapes brackets and parentheses in the text", () =>
    expect(markdownLinkText("/W/a [b] (c).md")).toBe("[a \\[b\\] \\(c\\)](/a%20%5Bb%5D%20%28c%29.md)"));
});

describe("the copy items", () => {
  const root = tree(["/W/garden.md", "/W/shot.png"]);

  it("offers an attachment its file path only", () => {
    const copy = vi.fn();
    const items = copyItems("/W/shot.png", root, false, copy);
    expect(items.map((i) => i.label)).toEqual(["Copy file path"]);
    items[0].onSelect();
    expect(copy).toHaveBeenCalledWith("/W/shot.png");
  });

  it("offers a note all three, in order, each copying its own text", () => {
    const copy = vi.fn();
    const items = copyItems("/W/garden.md", root, true, copy);
    expect(items.map((i) => i.label)).toEqual(["Copy file path", "Copy link", "Copy markdown link"]);
    for (const item of items) item.onSelect();
    expect(copy.mock.calls).toEqual([["/W/garden.md"], ["[[garden]]"], ["[garden](/garden.md)"]]);
  });

  it("leaves Copy link out where there is none", () =>
    expect(copyItems("/W/a#b.md", tree(["/W/a#b.md"]), true, vi.fn()).map((i) => i.label)).toEqual(["Copy file path", "Copy markdown link"]));
});
