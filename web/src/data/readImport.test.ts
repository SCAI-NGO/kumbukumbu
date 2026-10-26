// SPDX-License-Identifier: AGPL-3.0-or-later
import { strToU8, zipSync, type Zippable } from "fflate";
import { describe, expect, it } from "vitest";
import { MAX_NOTE_BYTES } from "../upload/planUpload";
import { readImport, versionOf } from "./readImport";

/** A ZIP of these entries; a name ending in "/" is a folder entry. */
function zip(entries: Record<string, string | Uint8Array>): Uint8Array {
  const files: Zippable = {};
  for (const [name, data] of Object.entries(entries)) files[name] = typeof data === "string" ? strToU8(data) : data;
  return zipSync(files);
}

const shape = (vaults: Awaited<ReturnType<typeof readImport>>) =>
  vaults.map((v) => ({ name: v.name, paths: v.notes.map((n) => n.path), skipped: v.skipped }));

describe("readImport", () => {
  it("each top-level folder is a vault", async () => {
    const vaults = await readImport(zip({ "Work/a.md": "A", "Work/p/b.md": "B", "Ideas/c.md": "C" }), "Kumbu² export 2026-10-26.zip");
    expect(shape(vaults)).toEqual([
      { name: "Work", paths: ["a.md", "p/b.md"], skipped: [] },
      { name: "Ideas", paths: ["c.md"], skipped: [] },
    ]);
    expect(vaults[0].notes[0]).toEqual({ path: "a.md", content: "A", version: await versionOf("A") });
  });

  it("an empty vault's folder entry still makes a vault", async () => {
    const vaults = await readImport(zip({ "Empty/": new Uint8Array(), "Work/a.md": "A" }), "export.zip");
    expect(shape(vaults)).toEqual([
      { name: "Empty", paths: [], skipped: [] },
      { name: "Work", paths: ["a.md"], skipped: [] },
    ]);
  });

  it("names with characters beyond ASCII come through as they are", async () => {
    const vaults = await readImport(zip({ "Bücher/Ü²/ö.md": "x" }), "export.zip");
    expect(shape(vaults)).toEqual([{ name: "Bücher", paths: ["Ü²/ö.md"], skipped: [] }]);
  });

  it("a zipped vault without a top folder is one vault named after the ZIP", async () => {
    const vaults = await readImport(zip({ "a.md": "A", "p/b.md": "B", ".obsidian/app.json": "{}" }), "My notes.zip");
    expect(shape(vaults)).toEqual([
      { name: "My notes", paths: ["a.md", "p/b.md"], skipped: [{ path: ".obsidian/app.json", reason: "Obsidian's settings or trash, not a note" }] },
    ]);
  });

  it(".markdown becomes .md as the server stores it", async () => {
    const vaults = await readImport(zip({ "Work/x.MARKDOWN": "x", "Work/Y.MD": "y" }), "export.zip");
    expect(shape(vaults)[0].paths).toEqual(["x.md", "Y.md"]);
  });

  it("two files that become the same note: the second is skipped", async () => {
    const vaults = await readImport(zip({ "Work/x.md": "1", "Work/x.markdown": "2" }), "export.zip");
    expect(shape(vaults)).toEqual([
      { name: "Work", paths: ["x.md"], skipped: [{ path: "x.markdown", reason: "another file of this upload has the same path" }] },
    ]);
  });

  it("unsafe and system entries are skipped", async () => {
    const vaults = await readImport(
      zip({
        "Work/ok.md": "ok",
        "Work/../x.md": "x",
        "/abs.md": "abs",
        "Work/a\\b.md": "ab",
        "__MACOSX/Work/._a.md": "mac",
        "Work/.DS_Store": "ds",
        "Work/.obsidian/app.json": "{}",
        "Work/.trash/t.md": "t",
        "Work/.hidden.md": "h",
        ".obsidian/workspace.json": "{}",
      }),
      "export.zip",
    );
    expect(shape(vaults)).toEqual([
      {
        name: "Work",
        paths: ["ok.md"],
        skipped: [
          { path: "../x.md", reason: "empty, '.' and '..' segments aren't allowed" },
          { path: "a\\b.md", reason: "folders are separated by '/', not '\\'" },
          { path: ".DS_Store", reason: "not a Markdown file (.md or .markdown)" },
          { path: ".obsidian/app.json", reason: "Obsidian's settings or trash, not a note" },
          { path: ".trash/t.md", reason: "Obsidian's settings or trash, not a note" },
          { path: ".hidden.md", reason: "'.hidden.md' starts with '.', which hides it in Obsidian" },
        ],
      },
    ]);
  });

  it("entries that aren't notes are never inflated", async () => {
    // A deflate stream that doesn't decode: inflating it would throw.
    const archive = zip({ "Work/a.md": "A", "Work/big.png": new Uint8Array(4000).fill(7) });
    const broken = corrupt(archive, "Work/big.png");
    const vaults = await readImport(broken, "export.zip");
    expect(shape(vaults)).toEqual([
      { name: "Work", paths: ["a.md"], skipped: [{ path: "big.png", reason: "not a Markdown file (.md or .markdown)" }] },
    ]);
    // The same damage on a note does throw, so the test above proves the entry was left alone.
    await expect(readImport(corrupt(zip({ "Work/big.md": "x".repeat(4000) }), "Work/big.md"), "export.zip")).rejects.toThrow();
  });

  it("a file that isn't UTF-8 is skipped, not mangled", async () => {
    const vaults = await readImport(zip({ "Work/latin.md": new Uint8Array([0x4b, 0xe4, 0x73, 0x65]), "Work/ok.md": "ok" }), "export.zip");
    expect(shape(vaults)).toEqual([{ name: "Work", paths: ["ok.md"], skipped: [{ path: "latin.md", reason: "not UTF-8 text" }] }]);
  });

  it("a note over 1 MiB is skipped", async () => {
    const vaults = await readImport(zip({ "Work/big.md": "x".repeat(MAX_NOTE_BYTES + 1), "Work/ok.md": "ok" }), "export.zip");
    expect(shape(vaults)).toEqual([
      { name: "Work", paths: ["ok.md"], skipped: [{ path: "big.md", reason: "larger than 1048576 bytes (1 MiB), the limit for a note" }] },
    ]);
  });

  it("something that isn't a ZIP is refused", async () => {
    await expect(readImport(strToU8("not a zip"), "x.zip")).rejects.toThrow();
  });
});

describe("versionOf", () => {
  it("is the server's version token, after CRLF → LF", async () => {
    // VersionToken.Of("a\nb"): the first 12 hex characters of SHA-256.
    expect(await versionOf("a\nb")).toBe("7e18f737311b");
    expect(await versionOf("a\r\nb")).toBe("7e18f737311b");
  });
});

/** The archive with the compressed data of `name` overwritten by bytes that aren't valid deflate. */
function corrupt(archive: Uint8Array, name: string): Uint8Array {
  const copy = archive.slice();
  const view = new DataView(copy.buffer);
  const encoded = strToU8(name);
  for (let i = 0; i + 30 < copy.length; i++) {
    if (view.getUint32(i, true) !== 0x04034b50) continue;
    const nameLength = view.getUint16(i + 26, true);
    const extraLength = view.getUint16(i + 28, true);
    const at = copy.subarray(i + 30, i + 30 + nameLength);
    if (at.length !== encoded.length || at.some((b, j) => b !== encoded[j])) continue;
    if (view.getUint16(i + 8, true) !== 8) throw new Error(`${name} isn't deflated`);
    const size = view.getUint32(i + 18, true);
    // 0xFF: a block type of 3, which deflate doesn't have.
    copy.fill(0xff, i + 30 + nameLength + extraLength, i + 30 + nameLength + extraLength + size);
    return copy;
  }
  throw new Error(`no entry ${name}`);
}
