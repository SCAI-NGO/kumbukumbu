// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { ACCEPT, MAX_ATTACHMENT_BYTES, MAX_FILES, MAX_NOTE_BYTES, NOT_SUPPORTED, planUpload, toBatches } from "./planUpload";

const entry = (path: string, size = 10) => ({ path, size });

describe("planUpload", () => {
  it("keeps .md and .markdown in any case as notes and lists everything else as skipped, with the reason", () => {
    const { notes, skipped } = planUpload([entry("Vault/a.md"), entry("Vault/B.MD"), entry("Vault/notes/c.markdown"), entry("Vault/notes/.md"), entry("Vault/readme.txt")]);
    expect(notes.map((k) => k.path)).toEqual(["Vault/a.md", "Vault/B.MD", "Vault/notes/c.markdown"]);
    expect(skipped).toEqual([
      { path: "Vault/notes/.md", reason: NOT_SUPPORTED },
      { path: "Vault/readme.txt", reason: NOT_SUPPORTED },
    ]);
  });

  it("accepts the six formats and refuses docx with the formats named", () => {
    const { notes, files, skipped } = planUpload([
      entry("a.pdf"),
      entry("b.PNG"),
      entry("c.jpg"),
      entry("d.Jpeg"),
      entry("e.gif"),
      entry("f.webp"),
      entry("g.md"),
      entry("report.docx"),
      entry(".png"),
    ]);
    expect(files.map((f) => f.path)).toEqual(["a.pdf", "b.PNG", "c.jpg", "d.Jpeg", "e.gif", "f.webp"]);
    expect(notes.map((f) => f.path)).toEqual(["g.md"]);
    expect(skipped).toEqual([
      { path: "report.docx", reason: "not a format Kumbu² keeps: notes (.md, .markdown), PDFs (.pdf) and images (.png, .jpg, .jpeg, .gif, .webp)" },
      { path: ".png", reason: NOT_SUPPORTED },
    ]);
  });

  it("a heic file is refused with the formats named", () => {
    // iOS converts HEIC on pick when the input lists concrete types; one that still arrives (a folder, a drop) is refused.
    expect(planUpload([entry("IMG_0001.HEIC")]).skipped).toEqual([{ path: "IMG_0001.HEIC", reason: NOT_SUPPORTED }]);
    expect(NOT_SUPPORTED).toContain("(.pdf)");
    expect(NOT_SUPPORTED).toContain("(.png, .jpg, .jpeg, .gif, .webp)");
  });

  it("an image over 15 MB is offered for shrinking, a PDF over 15 MB is refused naming 15 MB", () => {
    const { files, tooLarge, skipped } = planUpload([
      entry("fits.png", MAX_ATTACHMENT_BYTES),
      entry("photo.jpg", MAX_ATTACHMENT_BYTES + 1),
      entry("scan.pdf", MAX_ATTACHMENT_BYTES + 1),
    ]);
    expect(MAX_ATTACHMENT_BYTES).toBe(15 * 1024 * 1024);
    expect(files.map((f) => f.path)).toEqual(["fits.png"]);
    expect(tooLarge.map((f) => f.path)).toEqual(["photo.jpg"]);
    expect(skipped).toEqual([{ path: "scan.pdf", reason: "larger than 15 MB, the limit for a file" }]);
  });

  it("the file input lists concrete types, not image/*", () => {
    const types = ACCEPT.split(",");
    expect(types).toEqual(expect.arrayContaining(".pdf,.png,.jpg,.jpeg,.gif,.webp,application/pdf,image/png,image/jpeg,image/gif,image/webp".split(",")));
    expect(types).toEqual(expect.arrayContaining([".md", ".markdown"]));
    expect(types.filter((t) => t.endsWith("/*"))).toEqual([]);
  });

  it("skips anything under .obsidian/ or .trash/ at any depth", () => {
    const { notes, files, skipped } = planUpload([
      entry(".obsidian/workspace.md"),
      entry("Vault/.obsidian/app.json"),
      entry("Vault/.Trash/old.md"),
      entry("Vault/sub/.obsidian/plugins/x/README.md"),
      entry("Vault/.trash/old.png"),
      entry("Vault/trash.md"),
    ]);
    expect(notes.map((k) => k.path)).toEqual(["Vault/trash.md"]);
    expect(files).toEqual([]);
    expect(skipped.map((s) => s.reason)).toEqual(Array(5).fill("Obsidian's settings or trash, not a note"));
  });

  it("skips a file larger than a note may be (1 MiB), without reading it", () => {
    const { notes, skipped } = planUpload([entry("big.md", MAX_NOTE_BYTES + 1), entry("fits.md", MAX_NOTE_BYTES)]);
    expect(notes.map((k) => k.path)).toEqual(["fits.md"]);
    expect(skipped).toEqual([{ path: "big.md", reason: "larger than 1048576 bytes (1 MiB), the limit for a note" }]);
  });
});

describe("toBatches", () => {
  it("puts at most 200 files into a batch", () => {
    const files = Array.from({ length: 401 }, (_, i) => ({ path: `n${i}.md`, content: "x" }));
    const { batches, skipped } = toBatches(files);
    expect(batches.map((b) => b.length)).toEqual([MAX_FILES, MAX_FILES, 1]);
    expect(batches.flat()).toEqual(files);
    expect(skipped).toEqual([]);
  });

  it("puts at most 5 MB of UTF-8 into a batch, counting multi-byte characters", () => {
    // "ü" is two bytes: each file is 1 MB, so five fit and a sixth doesn't.
    const files = ["a", "b", "c", "d", "e", "f"].map((n) => ({ path: `${n}.md`, content: "ü".repeat(500_000) }));
    expect(toBatches(files).batches.map((b) => b.map((f) => f.path))).toEqual([["a.md", "b.md", "c.md", "d.md", "e.md"], ["f.md"]]);
  });

  it("skips a file whose text, once read, is larger than a note may be", () => {
    // 524,289 two-byte characters: 1 MiB and two bytes, though a file of that text in Latin-1 is half the size.
    const { batches, skipped } = toBatches([{ path: "huge.md", content: "ü".repeat(524_289) }, { path: "a.md", content: "a" }]);
    expect(batches).toEqual([[{ path: "a.md", content: "a" }]]);
    expect(skipped).toEqual([{ path: "huge.md", reason: "larger than 1048576 bytes (1 MiB), the limit for a note" }]);
  });
});
