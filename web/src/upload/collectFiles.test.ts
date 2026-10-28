// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { collectFromDrop, collectFromInput } from "./collectFiles";

type FakeEntry = FileSystemFileEntry | FileSystemDirectoryEntry;

function fileEntry(fullPath: string): FileSystemFileEntry {
  const file = new File(["x"], fullPath.split("/").at(-1)!);
  return { isFile: true, isDirectory: false, name: file.name, fullPath, file: (ok: (f: File) => void) => ok(file) } as unknown as FileSystemFileEntry;
}

/** A folder whose reader hands out its entries two at a time, as browsers do in chunks of 100. */
function dirEntry(fullPath: string, children: FakeEntry[]): FileSystemDirectoryEntry {
  return {
    isFile: false,
    isDirectory: true,
    name: fullPath.split("/").at(-1)!,
    fullPath,
    createReader: () => {
      let at = 0;
      return {
        readEntries: (ok: (entries: FakeEntry[]) => void) => {
          ok(children.slice(at, at + 2));
          at += 2;
        },
      };
    },
  } as unknown as FileSystemDirectoryEntry;
}

const items = (entries: FakeEntry[]) => entries.map((e) => ({ kind: "file", webkitGetAsEntry: () => e })) as unknown as DataTransferItemList;

describe("collectFromDrop", () => {
  it("keeps the relative paths of nested folders, the dropped folder's name first", async () => {
    const vault = dirEntry("/Vault", [
      fileEntry("/Vault/a.md"),
      dirEntry("/Vault/.obsidian", [fileEntry("/Vault/.obsidian/app.json")]),
      dirEntry("/Vault/notes", [fileEntry("/Vault/notes/b.md"), dirEntry("/Vault/notes/deep", [fileEntry("/Vault/notes/deep/c.md"), fileEntry("/Vault/notes/deep/pic.png")])]),
    ]);
    const picked = await collectFromDrop(items([vault, fileEntry("/loose.md")]));
    expect(picked.map((p) => p.path).sort()).toEqual(["Vault/.obsidian/app.json", "Vault/a.md", "Vault/notes/b.md", "Vault/notes/deep/c.md", "Vault/notes/deep/pic.png", "loose.md"]);
    expect(picked.find((p) => p.path === "loose.md")!.file.name).toBe("loose.md");
  });

  it("takes a dropped file as it is where entries aren't available", async () => {
    const file = new File(["x"], "plain.md");
    const picked = await collectFromDrop([{ kind: "file", getAsFile: () => file }] as unknown as DataTransferItemList);
    expect(picked).toEqual([{ path: "plain.md", file }]);
  });
});

describe("collectFromInput", () => {
  it("uses the path within a picked folder, else the file's name", () => {
    const inFolder = new File(["x"], "b.md");
    Object.defineProperty(inFolder, "webkitRelativePath", { value: "Vault/notes/b.md" });
    const single = new File(["x"], "a.md");
    expect(collectFromInput([inFolder, single]).map((p) => p.path)).toEqual(["Vault/notes/b.md", "a.md"]);
  });

  it("names picked in NFD (macOS, iOS) come in NFC, as the server stores paths", () => {
    const nfd = new File(["x"], "Bespre\u0301chung.png");
    expect(collectFromInput([nfd])[0].path).toBe("Bespr\u00e9chung.png");
  });
});
