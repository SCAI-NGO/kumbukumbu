// SPDX-License-Identifier: AGPL-3.0-or-later
import { strToU8, zipSync, type Zippable } from "fflate";
import { describe, expect, it } from "vitest";
import { MAX_ATTACHMENT_BYTES, MAX_NOTE_BYTES, NOT_SUPPORTED, TOO_LARGE, TOO_LARGE_FILE } from "../upload/planUpload";
import { ImportTooLarge, readImport, versionOf, versionOfBytes } from "./readImport";

/** A ZIP of these entries; a name ending in "/" is a folder entry. */
function zip(entries: Record<string, string | Uint8Array>): Uint8Array {
  const files: Zippable = {};
  for (const [name, data] of Object.entries(entries)) files[name] = typeof data === "string" ? strToU8(data) : data;
  return zipSync(files);
}

/**
 * Rewrites every uncompressed size the ZIP declares, in the local headers and the central directory, to `size`: a ZIP
 * that lies about what its entries unpack to. fflate doesn't check the CRC, so nothing else gives the lie away.
 */
function declaring(bytes: Uint8Array, size: number): Uint8Array {
  const out = bytes.slice();
  const view = new DataView(out.buffer);
  for (let i = 0; i + 4 <= out.length; i++) {
    const signature = view.getUint32(i, true);
    if (signature === 0x04034b50) view.setUint32(i + 22, size, true);
    else if (signature === 0x02014b50) view.setUint32(i + 24, size, true);
  }
  return out;
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

  it("UTF-8 names without the ZIP's UTF-8 flag (macOS's Compress) come through as they are", async () => {
    const vaults = await readImport(withoutUtf8Flag(zip({ "Bücher/Ü².md": "x", "Bücher/plain.md": "y" })), "export.zip");
    expect(shape(vaults)).toEqual([{ name: "Bücher", paths: ["Ü².md", "plain.md"], skipped: [] }]);
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
          { path: ".DS_Store", reason: NOT_SUPPORTED },
          { path: ".obsidian/app.json", reason: "Obsidian's settings or trash, not a note" },
          { path: ".trash/t.md", reason: "Obsidian's settings or trash, not a note" },
          { path: ".hidden.md", reason: "'.hidden.md' starts with '.', which hides it in Obsidian" },
        ],
      },
    ]);
  });

  it("entries that aren't kept are never inflated", async () => {
    // A deflate stream that doesn't decode: inflating it would throw.
    const archive = zip({ "Work/a.md": "A", "Work/big.bin": new Uint8Array(4000).fill(7) });
    const broken = corrupt(archive, "Work/big.bin");
    const vaults = await readImport(broken, "export.zip");
    expect(shape(vaults)).toEqual([{ name: "Work", paths: ["a.md"], skipped: [{ path: "big.bin", reason: NOT_SUPPORTED }] }]);
    // The same damage on a note does throw, so the test above proves the entry was left alone.
    await expect(readImport(corrupt(zip({ "Work/big.md": "x".repeat(4000) }), "Work/big.md"), "export.zip")).rejects.toThrow();
  });

  it("import keeps attachments from a ZIP", async () => {
    const png = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3]);
    const pdf = strToU8("%PDF-1.7\r\nbody\r\n");
    const vaults = await readImport(
      zip({
        "Work/a.md": "A",
        "Work/img/Bespre\u0301chung.png": png,
        "Work/scan.PDF": pdf,
        "Work/report.docx": "x",
        "Work/huge.jpg": new Uint8Array(MAX_ATTACHMENT_BYTES + 1),
        "Work/.obsidian/icon.png": png,
      }),
      "export.zip",
    );
    expect(vaults).toHaveLength(1);
    const [work] = vaults;
    expect(work.notes.map((n) => n.path)).toEqual(["a.md"]);
    // Paths in NFC; the version as the server's for the bytes as they are (no CRLF → LF for a file).
    expect(work.files.map((f) => [f.path, f.size, f.version])).toEqual([
      ["img/Bespr\u00e9chung.png", png.length, await versionOfBytes(png)],
      ["scan.PDF", pdf.length, await versionOfBytes(pdf)],
    ]);
    expect(await versionOfBytes(pdf)).not.toBe(await versionOf("%PDF-1.7\r\nbody\r\n"));
    expect(work.files.map((f) => Array.from(f.read()))).toEqual([Array.from(png), Array.from(pdf)]);
    expect(work.skipped).toEqual([
      { path: "report.docx", reason: NOT_SUPPORTED },
      { path: "huge.jpg", reason: TOO_LARGE_FILE },
      { path: ".obsidian/icon.png", reason: "Obsidian's settings or trash, not a note" },
    ]);
  });

  it("two images that land at the same path (vault folders differing in case): the second is skipped", async () => {
    const vaults = await readImport(zip({ "Work/a.png": "1", "work/a.png": "2" }), "export.zip");
    expect(vaults.map((v) => [v.files.map((f) => f.path), v.skipped])).toEqual([[["a.png"], [{ path: "a.png", reason: "another file of this upload has the same path" }]]]);
  });

  it("a zipped vault with only an image at its root is one vault too", async () => {
    const vaults = await readImport(zip({ "pic.gif": strToU8("GIF89a"), "p/b.md": "B" }), "Photos.zip");
    expect(vaults.map((v) => [v.name, v.notes.map((n) => n.path), v.files.map((f) => f.path)])).toEqual([["Photos", ["p/b.md"], ["pic.gif"]]]);
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

  it("names come in NFC, as the server stores them, also from a ZIP made in NFD", async () => {
    const nfd = "Bu\u0308cher/U\u0308\u00b2.md";
    const vaults = await readImport(zip({ [nfd]: "x" }), "export.zip");
    expect(vaults[0].name).toBe("B\u00fccher");
    expect(vaults[0].notes[0].path).toBe("\u00dc\u00b2.md");
    const whole = await readImport(zip({ "a.md": "A" }), "Notizbu\u0308cher .zip");
    expect(whole[0].name).toBe("Notizb\u00fccher");
  });

  it("vault folders that differ in case or spaces only are one vault", async () => {
    const vaults = await readImport(zip({ "Work/a.md": "A", "work/b.md": "B", "Work /a.md": "again" }), "export.zip");
    expect(shape(vaults)).toEqual([{ name: "Work", paths: ["a.md", "b.md"], skipped: [{ path: "a.md", reason: "another file of this upload has the same path" }] }]);
  });

  it("an entry that is in the ZIP twice is skipped, both copies", async () => {
    // fflate's zipSync takes an object, so the second copy is spliced in by renaming another entry of the same length.
    const archive = rename(zip({ "Work/a.md": "first", "Work/b.md": "second", "Work/ok.md": "ok" }), "Work/b.md", "Work/a.md");
    const vaults = await readImport(archive, "export.zip");
    expect(shape(vaults)).toEqual([
      {
        name: "Work",
        paths: ["ok.md"],
        skipped: [
          { path: "a.md", reason: "in the ZIP more than once" },
          { path: "a.md", reason: "in the ZIP more than once" },
        ],
      },
    ]);
  });

  it("a ZIP that separates folders with backslashes only (old Windows tools) is read with them as folders", async () => {
    const vaults = await readImport(zip({ "Work\\a.md": "A", "Work\\p\\b.md": "B" }), "export.zip");
    expect(shape(vaults)).toEqual([{ name: "Work", paths: ["a.md", "p/b.md"], skipped: [] }]);
  });

  it("a byte order mark is kept, so the note's version matches the stored one", async () => {
    const vaults = await readImport(zip({ "Work/a.md": new Uint8Array([0xef, 0xbb, 0xbf, 0x41]) }), "export.zip");
    expect(vaults[0].notes[0].content).toBe("\ufeffA");
  });

  // The budget counts declared sizes (issue #50): it bounds memory only if an entry never unpacks to more than it declares.
  it("a note that unpacks to more than it declares is cut at the declared size, never grown", async () => {
    const real = "a".repeat(4 * 1024 * 1024);
    const lying = declaring(zip({ "Work/a.md": real }), 10);

    const [vault] = await readImport(lying, "export.zip", 100);

    expect(vault.notes.map((n) => n.content.length)).toEqual([10]);
  });

  it("a note that unpacks to less than it declares is not padded past what it holds", async () => {
    const lying = declaring(zip({ "Work/a.md": "short" }), 1000);

    const [vault] = await readImport(lying, "export.zip");

    expect(vault.notes.map((n) => n.content)).toEqual(["short"]);
  });

  it("a stored note that lies about its size is bounded by the ZIP itself and still refused when too large", async () => {
    const real = "a".repeat(MAX_NOTE_BYTES + 1);
    const lying = declaring(zipSync({ "Work/a.md": strToU8(real) }, { level: 0 }), 10);

    const [vault] = await readImport(lying, "export.zip", 100);

    expect(shape([vault])).toEqual([{ name: "Work", paths: [], skipped: [{ path: "a.md", reason: TOO_LARGE }] }]);
  });

  it("refuses a ZIP whose notes would unpack to more than the budget, before unpacking any", async () => {
    const big = "x".repeat(1000);
    await expect(readImport(zip({ "Work/a.md": big, "Work/b.md": big }), "export.zip", 1500)).rejects.toThrow(ImportTooLarge);
  });

  it("something that isn't a ZIP is refused", async () => {
    await expect(readImport(strToU8("not a zip"), "x.zip")).rejects.toThrow();
  });
});

describe("versionOfBytes", () => {
  it("is the server's version token of a file's bytes, as they are", async () => {
    expect(await versionOfBytes(strToU8("a\nb"))).toBe("7e18f737311b");
    expect(await versionOfBytes(strToU8("a\r\nb"))).not.toBe("7e18f737311b");
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

/** The archive with the UTF-8 flag (bit 11) cleared on every entry, as tools that write UTF-8 names unflagged do. */
function withoutUtf8Flag(archive: Uint8Array): Uint8Array {
  const copy = archive.slice();
  const view = new DataView(copy.buffer);
  for (let i = 0; i + 4 <= copy.length; i++) {
    const signature = view.getUint32(i, true);
    // Local header: flags at +6; central directory header: flags at +8.
    const at = signature === 0x04034b50 ? i + 6 : signature === 0x02014b50 ? i + 8 : -1;
    if (at >= 0) view.setUint16(at, view.getUint16(at, true) & ~0x0800, true);
  }
  return copy;
}

/** The archive with entry `from` renamed to `to` (same length) in its local and central headers. */
function rename(archive: Uint8Array, from: string, to: string): Uint8Array {
  const copy = archive.slice();
  const [a, b] = [strToU8(from), strToU8(to)];
  if (a.length !== b.length) throw new Error("same length only");
  for (let i = 0; i + a.length <= copy.length; i++) {
    if (a.every((byte, j) => copy[i + j] === byte)) copy.set(b, i);
  }
  return copy;
}
