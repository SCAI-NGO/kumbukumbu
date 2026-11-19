// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { boundSentence, DEFAULT_MAX_FILE_BYTES, MAX_ATTACHMENT_BYTES, MAX_FILES, MAX_NOTE_BYTES, planUpload, toBatches } from "./planUpload";

const hidden = (name: string) => `'${name}' starts with '.', which hides it in Obsidian`;

const entry = (path: string, size = 10) => ({ path, size });

describe("planUpload", () => {
  it("planUpload sends markers apart", () => {
    const { markers, skipped, notes } = planUpload([entry("Vault/Drafts/.vaultkeep", 0), entry("Vault/.obsidian/x/.vaultkeep", 0), entry("Vault/.git/x/.vaultkeep", 0), entry(".vaultkeep", 0), entry("Vault/a.md")]);
    expect(markers.map((k) => k.path)).toEqual(["Vault/Drafts/.vaultkeep"]);
    expect(notes.map((k) => k.path)).toEqual(["Vault/a.md"]);
    expect(skipped.map((s) => s.path)).toEqual(["Vault/.obsidian/x/.vaultkeep", "Vault/.git/x/.vaultkeep", ".vaultkeep"]);
  });

  it("keeps .md and .markdown in any case as notes and everything else as files", () => {
    const { notes, files, skipped } = planUpload([entry("Vault/a.md"), entry("Vault/B.MD"), entry("Vault/notes/c.markdown"), entry("Vault/notes/.md"), entry("Vault/readme.txt")]);
    expect(notes.map((k) => k.path)).toEqual(["Vault/a.md", "Vault/B.MD", "Vault/notes/c.markdown"]);
    expect(files.map((k) => k.path)).toEqual(["Vault/readme.txt"]);
    expect(skipped).toEqual([{ path: "Vault/notes/.md", reason: hidden(".md") }]);
  });

  it("accepts every format, and skips a file whose name starts with a dot as the server would", () => {
    const { notes, files, skipped } = planUpload([
      entry("a.pdf"),
      entry("b.PNG"),
      entry("c.jpg"),
      entry("d.Jpeg"),
      entry("e.gif"),
      entry("f.webp"),
      entry("h.Svg"),
      entry("g.md"),
      entry("report.docx"),
      entry("IMG_0001.HEIC"),
      entry("Makefile"),
      entry(".png"),
      entry("Vault/.DS_Store"),
    ]);
    expect(files.map((f) => f.path)).toEqual(["a.pdf", "b.PNG", "c.jpg", "d.Jpeg", "e.gif", "f.webp", "h.Svg", "report.docx", "IMG_0001.HEIC", "Makefile"]);
    expect(notes.map((f) => f.path)).toEqual(["g.md"]);
    expect(skipped).toEqual([
      { path: ".png", reason: hidden(".png") },
      { path: "Vault/.DS_Store", reason: hidden(".DS_Store") },
    ]);
  });

  it("an image over 15 MB is offered for shrinking, any other file over 15 MB is sent up to the bound, an SVG is refused", () => {
    const { files, tooLarge, skipped } = planUpload([
      entry("fits.png", MAX_ATTACHMENT_BYTES),
      entry("photo.jpg", MAX_ATTACHMENT_BYTES + 1),
      entry("scan.pdf", MAX_ATTACHMENT_BYTES + 1),
      entry("map.svg", MAX_ATTACHMENT_BYTES + 1),
      entry("data.xlsx", 40_000_000),
      entry("IMG_0002.HEIC", MAX_ATTACHMENT_BYTES + 1),
      entry("film.mkv", DEFAULT_MAX_FILE_BYTES),
    ]);
    expect(MAX_ATTACHMENT_BYTES).toBe(15 * 1024 * 1024);
    expect(DEFAULT_MAX_FILE_BYTES).toBe(1024 ** 3);
    expect(files.map((f) => f.path)).toEqual(["fits.png", "scan.pdf", "data.xlsx", "IMG_0002.HEIC", "film.mkv"]);
    expect(tooLarge.map((f) => f.path)).toEqual(["photo.jpg"]);
    // An SVG is read whole by the server to be checked, so it has the 15 MB of old.
    expect(skipped).toEqual([{ path: "map.svg", reason: "larger than 15 MB; an SVG image is read whole to be checked, so it can have at most 15 MB" }]);
  });

  it("refuses a file over the bound before it is sent, naming the bound as the server does", () => {
    const { files, tooLarge, skipped } = planUpload([entry("film.mkv", DEFAULT_MAX_FILE_BYTES + 1), entry("big.bin", 21 * 1024 ** 2), entry("photo.jpg", 30 * 1024 ** 2)], 20 * 1024 ** 2);
    expect(files).toEqual([]);
    // A photo over the bound may still be shrunk under it.
    expect(tooLarge.map((f) => f.path)).toEqual(["photo.jpg"]);
    expect(skipped).toEqual([
      { path: "film.mkv", reason: "A file can have at most 20 MiB." },
      { path: "big.bin", reason: "A file can have at most 20 MiB." },
    ]);
    expect(planUpload([entry("film.mkv", DEFAULT_MAX_FILE_BYTES + 1)]).skipped).toEqual([{ path: "film.mkv", reason: "A file can have at most 1 GiB." }]);
  });

  it("words the bound as the server's FileTooLargeException does", () => {
    expect(boundSentence(1024 ** 3)).toBe("A file can have at most 1 GiB.");
    expect(boundSentence(2 * 1024 ** 3)).toBe("A file can have at most 2 GiB.");
    expect(boundSentence(20 * 1024 ** 2)).toBe("A file can have at most 20 MiB.");
    expect(boundSentence(20_000_001)).toBe("A file can have at most 20,000,001 bytes.");
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

  it("sends a script, in a skill folder or anywhere else", () =>
    expect(planUpload([entry("run.py", 9), entry("scripts/run.py", 9)]).files).toHaveLength(2));

  it("sends a text file over 15 MB as it is instead of offering to shrink it", () => {
    const plan = planUpload([entry("run.py", MAX_ATTACHMENT_BYTES + 1)]);
    expect(plan.tooLarge).toEqual([]);
    expect(plan.files.map((f) => f.path)).toEqual(["run.py"]);
  });
});
