// SPDX-License-Identifier: AGPL-3.0-or-later
import { describe, expect, it, vi } from "vitest";
import { HttpError } from "../api/client";
import type { UploadFile } from "../upload/planUpload";
import type { ImportNote, ImportVault } from "./readImport";
import { planImport, runImport, summaryText, type Choice, type ImportApi, type VaultPlan } from "./runImport";

const note = (path: string, version = `v-${path}`, content = `text of ${path}`): ImportNote => ({ path, content, version });
const vault = (name: string, notes: ImportNote[], skipped: ImportVault["skipped"] = []): ImportVault => ({ name, notes, skipped });

interface Call {
  vault: string;
  files: UploadFile[];
  onConflict: "replace" | "skip";
}

/** A server that takes everything: each upload writes what it is sent, replacing with `replace`. */
function fakeApi(overrides: Partial<ImportApi> = {}) {
  const created: string[] = [];
  const uploads: Call[] = [];
  const api: ImportApi = {
    createVault: vi.fn(async (name: string) => {
      created.push(name);
    }),
    versions: vi.fn(async () => new Map<string, string>()),
    upload: vi.fn(async (v: string, files: UploadFile[], onConflict: "replace" | "skip") => {
      uploads.push({ vault: v, files, onConflict });
      return onConflict === "replace"
        ? { written: 0, replaced: files.length, skipped: [] }
        : { written: files.length, replaced: 0, skipped: [] };
    }),
    ...overrides,
  };
  return { api, created, uploads };
}

const run = (plans: VaultPlan[], choices: Record<string, Choice> = {}, api = fakeApi().api) =>
  runImport(plans, new Map(Object.entries(choices)), api, () => {});

describe("planImport", () => {
  it("a new vault: every note is fresh", () => {
    const [plan] = planImport([vault("Work", [note("a.md"), note("p/b.md")])], [{ name: "Ideas", versions: new Map() }]);
    expect(plan).toMatchObject({ exists: false, fresh: ["a.md", "p/b.md"], differing: [], unchanged: [] });
  });

  it("an existing vault: split by version", () => {
    const versions = new Map([
      ["a.md", "v-a.md"],
      ["b.md", "old"],
    ]);
    const [plan] = planImport([vault("Work", [note("a.md"), note("b.md"), note("c.md")])], [{ name: "Work", versions }]);
    expect(plan).toMatchObject({ exists: true, unchanged: ["a.md"], differing: ["b.md"], fresh: ["c.md"] });
  });

  it("an existing vault whose versions weren't read: nothing is assumed equal", () => {
    const [plan] = planImport([vault("Work", [note("a.md")])], [{ name: "Work", versions: null }]);
    expect(plan).toMatchObject({ exists: true, unchanged: [], differing: ["a.md"], fresh: [] });
  });
});

describe("runImport", () => {
  it("creates a missing vault and uploads its notes to the root, in batches the server takes", async () => {
    const notes = Array.from({ length: 450 }, (_, i) => note(`n${i}.md`));
    const { api, created, uploads } = fakeApi();
    const progress: [number, number][] = [];
    const summary = await runImport(planImport([vault("Work", notes)], []), new Map(), api, (done, total) => progress.push([done, total]));
    expect(created).toEqual(["Work"]);
    expect(uploads.map((u) => [u.vault, u.files.length, u.onConflict])).toEqual([
      ["Work", 200, "skip"],
      ["Work", 200, "skip"],
      ["Work", 50, "skip"],
    ]);
    expect(uploads[0].files[0]).toEqual({ path: "n0.md", content: "text of n0.md" });
    expect(progress).toEqual([
      [0, 450],
      [200, 450],
      [400, 450],
      [450, 450],
    ]);
    expect(summary).toEqual({ vaults: 1, imported: 450, replaced: 0, kept: 0, unchanged: 0, skipped: [], failed: [] });
  });

  it("splits a batch at 5 MB of text", async () => {
    const big = "x".repeat(1_000_000);
    const { api, uploads } = fakeApi();
    await run(planImport([vault("Work", Array.from({ length: 6 }, (_, i) => note(`n${i}.md`, "v", big)))], []), {}, api);
    expect(uploads.map((u) => u.files.length)).toEqual([5, 1]);
  });

  it("an existing vault: unchanged notes aren't sent, fresh ones go with skip, differing ones with replace when chosen", async () => {
    const versions = new Map([
      ["same.md", "v-same.md"],
      ["changed.md", "old"],
    ]);
    const plans = planImport([vault("Work", [note("same.md"), note("changed.md"), note("new.md")])], [{ name: "Work", versions }]);
    const { api, created, uploads } = fakeApi();
    const summary = await run(plans, { Work: "replace" }, api);
    expect(created).toEqual([]);
    expect(uploads.map((u) => [u.files.map((f) => f.path), u.onConflict])).toEqual([
      [["new.md"], "skip"],
      [["changed.md"], "replace"],
    ]);
    expect(summary).toMatchObject({ vaults: 1, imported: 1, replaced: 1, kept: 0, unchanged: 1 });
  });

  it("with skip, the differing notes stay as they are and aren't sent", async () => {
    const plans = planImport([vault("Work", [note("changed.md"), note("new.md")])], [{ name: "Work", versions: new Map([["changed.md", "old"]]) }]);
    const { api, uploads } = fakeApi();
    const summary = await run(plans, { Work: "skip" }, api);
    expect(uploads.map((u) => [u.files.map((f) => f.path), u.onConflict])).toEqual([[["new.md"], "skip"]]);
    expect(summary).toMatchObject({ imported: 1, replaced: 0, kept: 1 });
  });

  it("a second import of the same export changes nothing", async () => {
    const notes = [note("a.md"), note("b.md")];
    const versions = new Map(notes.map((n) => [n.path, n.version]));
    const { api, uploads, created } = fakeApi();
    const summary = await run(planImport([vault("Work", notes)], [{ name: "Work", versions }]), {}, api);
    expect(uploads).toEqual([]);
    expect(created).toEqual([]);
    expect(summary).toEqual({ vaults: 1, imported: 0, replaced: 0, kept: 0, unchanged: 2, skipped: [], failed: [] });
    expect(summaryText(summary)).toBe("1 vault, 0 notes imported · ✓ no conflicts");
  });

  it("lists the files the ZIP and the server skipped, with their vault", async () => {
    const { api } = fakeApi({
      upload: vi.fn(async () => ({ written: 0, replaced: 0, skipped: [{ path: "a.md", reason: "a folder of that name exists" }] })),
    });
    const summary = await run(planImport([vault("Work", [note("a.md")], [{ path: "pic.png", reason: "not a Markdown file (.md or .markdown)" }])], []), {}, api);
    expect(summary.skipped).toEqual([
      { path: "Work/pic.png", reason: "not a Markdown file (.md or .markdown)" },
      { path: "Work/a.md", reason: "a folder of that name exists" },
    ]);
  });

  it("a vault that can't be made fails alone; the others still import", async () => {
    const { api, uploads } = fakeApi({
      createVault: vi.fn(async (name: string) => {
        if (name === "Bad:") throw new HttpError(400, "'Bad:' contains ':', which Windows can't store");
      }),
    });
    const summary = await run(planImport([vault("Bad:", [note("a.md")]), vault("Good", [note("b.md")])], []), {}, api);
    expect(summary.failed).toEqual([{ vault: "Bad:", reason: "'Bad:' contains ':', which Windows can't store" }]);
    expect(uploads.map((u) => u.vault)).toEqual(["Good"]);
    expect(summary).toMatchObject({ vaults: 1, imported: 1 });
  });

  it("a batch that met a change meanwhile is sent once more", async () => {
    let calls = 0;
    const { api } = fakeApi({
      upload: vi.fn(async (_v: string, files: UploadFile[]) => {
        if (calls++ === 0) throw new HttpError(409, "Nothing was uploaded: /Work/a.md changed meanwhile; try again.");
        return { written: files.length, replaced: 0, skipped: [] };
      }),
    });
    const summary = await run(planImport([vault("Work", [note("a.md")])], []), {}, api);
    expect(calls).toBe(2);
    expect(summary).toMatchObject({ imported: 1, failed: [] });
  });

  it("twice changed meanwhile: the vault counts as failed and the next one goes on", async () => {
    const { api, uploads } = fakeApi();
    const upload = api.upload;
    api.upload = vi.fn(async (v: string, files: UploadFile[], onConflict: "replace" | "skip") => {
      if (v === "Work") throw new HttpError(409, "Nothing was uploaded: /Work/a.md changed meanwhile; try again.");
      return upload(v, files, onConflict);
    });
    const summary = await run(planImport([vault("Work", [note("a.md")]), vault("Ideas", [note("b.md")])], []), {}, api);
    expect(api.upload).toHaveBeenCalledTimes(3);
    expect(summary.failed).toEqual([{ vault: "Work", reason: "Nothing was uploaded: /Work/a.md changed meanwhile; try again." }]);
    expect(uploads.map((u) => u.vault)).toEqual(["Ideas"]);
    expect(summary).toMatchObject({ vaults: 1, imported: 1 });
  });
});

describe("summaryText", () => {
  const clean = { vaults: 3, imported: 412, replaced: 0, kept: 0, unchanged: 0, skipped: [], failed: [] };

  it("says so when nothing conflicted", () => {
    expect(summaryText(clean)).toBe("3 vaults, 412 notes imported · ✓ no conflicts");
    // Files that aren't notes are listed apart; they aren't conflicts.
    expect(summaryText({ ...clean, skipped: [{ path: "Work/pic.png", reason: "not a Markdown file (.md or .markdown)" }] })).toBe(
      "3 vaults, 412 notes imported · ✓ no conflicts",
    );
  });

  it("gives the counts when something was replaced, kept or failed", () => {
    expect(summaryText({ ...clean, replaced: 4, kept: 1 })).toBe("3 vaults, 412 notes imported · 4 replaced · 1 kept as it was");
    expect(summaryText({ ...clean, vaults: 2, failed: [{ vault: "X", reason: "r" }] })).toBe("2 vaults, 412 notes imported · 1 vault failed");
  });
});
