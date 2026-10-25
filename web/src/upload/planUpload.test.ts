// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it } from "vitest";
import { MAX_FILES, MAX_NOTE_BYTES, planUpload, toBatches } from "./planUpload";

const entry = (path: string, size = 10) => ({ path, size });

describe("planUpload", () => {
  it("keeps .md and .markdown in any case and lists everything else as skipped, with the reason", () => {
    const { keep, skipped } = planUpload([
      entry("Vault/a.md"),
      entry("Vault/B.MD"),
      entry("Vault/notes/c.markdown"),
      entry("Vault/notes/pic.png"),
      entry("Vault/notes/.md"),
      entry("Vault/readme.txt"),
    ]);
    expect(keep.map((k) => k.path)).toEqual(["Vault/a.md", "Vault/B.MD", "Vault/notes/c.markdown"]);
    expect(skipped).toEqual([
      { path: "Vault/notes/pic.png", reason: "not a Markdown file (.md or .markdown)" },
      { path: "Vault/notes/.md", reason: "not a Markdown file (.md or .markdown)" },
      { path: "Vault/readme.txt", reason: "not a Markdown file (.md or .markdown)" },
    ]);
  });

  it("skips anything under .obsidian/ or .trash/ at any depth", () => {
    const { keep, skipped } = planUpload([
      entry(".obsidian/workspace.md"),
      entry("Vault/.obsidian/app.json"),
      entry("Vault/.Trash/old.md"),
      entry("Vault/sub/.obsidian/plugins/x/README.md"),
      entry("Vault/trash.md"),
    ]);
    expect(keep.map((k) => k.path)).toEqual(["Vault/trash.md"]);
    expect(skipped.map((s) => s.reason)).toEqual(Array(4).fill("Obsidian's settings or trash, not a note"));
  });

  it("skips a file larger than a note may be (1 MiB), without reading it", () => {
    const { keep, skipped } = planUpload([entry("big.md", MAX_NOTE_BYTES + 1), entry("fits.md", MAX_NOTE_BYTES)]);
    expect(keep.map((k) => k.path)).toEqual(["fits.md"]);
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
