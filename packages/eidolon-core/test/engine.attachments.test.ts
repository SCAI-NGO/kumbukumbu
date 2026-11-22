// SPDX-License-Identifier: AGPL-3.0-or-later
// PDFs and images through the engine, on a server that keeps them: binary sync both ways, conflicts, limits.
import { afterEach, describe, expect, test, vi } from "vitest";
import { MAX_ATTACHMENT_BYTES } from "../src/engine/engine";
import { OfflineError, RejectedError } from "../src/engine/ports";
import { versionOfBytes } from "../src/engine/version";
import { harness, settle, type Harness } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); });

const PNG = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const png = (...rest: number[]) => new Uint8Array([...PNG, ...rest]);
const pdf = (size: number) => {
  const bytes = new Uint8Array(size);
  bytes.set(new TextEncoder().encode("%PDF-1.7\n"));
  return bytes;
};
const bytesOf = (h: Harness, path: string) => h.files.binaries.get(path);

/** Puts an attachment on disk and in the bases, as an earlier sync would have left it. */
async function synced(h: Harness, path: string, bytes: Uint8Array): Promise<void> {
  await h.server.seedFile(path, bytes);
  h.files.binaries.set(path, bytes.slice());
  h.state.state.bases[path] = await versionOfBytes(bytes);
  await h.engine.stop();
  await h.engine.start();
  h.server.calls.length = 0;
}

describe("attachments sync both ways", () => {
  test("uploads a new png and downloads one the server has", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" }, remoteFiles: { "img/theirs.png": png(2) } });
    h.files.binaries.set("img/mine.png", png(1));
    await h.engine.start();
    expect(h.server.files.get("img/mine.png")?.bytes).toEqual(png(1));
    expect(bytesOf(h, "img/theirs.png")).toEqual(png(2));
    expect(h.state.state.bases["img/mine.png"]).toBe(await versionOfBytes(png(1)));
    expect(h.state.state.bases["img/theirs.png"]).toBe(await versionOfBytes(png(2)));
    expect(h.engine.status().notSynced).toEqual([]);
    // Nothing went through the notes API.
    expect(h.server.notes.has("img/mine.png")).toBe(false);
  });

  test("a changed file is sent whole over its base, and an agent's change arrives", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    h.files.binaries.set("x.png", png(1, 2, 3));
    h.engine.localChanged("x.png");
    await settle(h, 2000);
    const put = h.server.calls.find((c) => c.method === "putFile")!;
    expect(new Uint8Array(put.args[1] as ArrayBuffer)).toEqual(png(1, 2, 3));
    expect(put.args[2]).toBe(await versionOfBytes(png(1)));

    await h.server.seedFile("x.png", png(7));
    await h.engine.check(false);
    expect(bytesOf(h, "x.png")).toEqual(png(7));
  });

  test("a change on both sides gives a conflict copy keeping .png", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "layout_editor_bad1.png", png(1));
    vi.setSystemTime(new Date(2026, 9, 27, 22, 15));
    await h.server.seedFile("layout_editor_bad1.png", png(2));
    h.files.binaries.set("layout_editor_bad1.png", png(3));
    await h.engine.check(false);
    const copy = "layout_editor_bad1 (conflict Laptop 2026-10-27 22-15).png";
    expect(h.conflicts).toEqual([["layout_editor_bad1.png", copy]]);
    expect(bytesOf(h, "layout_editor_bad1.png")).toEqual(png(2));
    expect(bytesOf(h, copy)).toEqual(png(3));
    expect(h.server.files.get(copy)?.bytes).toEqual(png(3));
    expect(h.engine.status().conflicts).toEqual([copy]);
  });

  test("a local delete trashes the attachment in Kumbu²", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.pdf", pdf(100));
    h.files.userDelete("x.pdf");
    h.engine.localDeleted("x.pdf");
    await settle(h);
    expect(h.server.files.has("x.pdf")).toBe(false);
    expect(h.state.state.bases["x.pdf"]).toBeUndefined();
  });

  test("a local rename moves the attachment in Kumbu²", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    h.files.userRename("x.png", "img/y.png");
    h.engine.localRenamed("x.png", "img/y.png");
    await settle(h);
    expect(h.server.calls.map((c) => c.method)).toEqual(["move"]);
    expect(h.server.files.has("x.png")).toBe(false);
    expect(h.server.files.get("img/y.png")?.bytes).toEqual(png(1));
    expect(h.state.state.bases).toMatchObject({ "img/y.png": await versionOfBytes(png(1)) });
  });

  // An incremental answer names deleted paths without a kind: an attachment's extension says what it was.
  test("an attachment trashed in Kumbu² arrives as a path without version or kind and goes to the trash", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    await h.server.removeFile("x.png", await versionOfBytes(png(1)));
    h.server.calls.length = 0;
    const answer = await h.server.versions(h.state.state.cursor, null);
    expect(answer.entries).toContainEqual({ path: "x.png", version: null });
    await h.engine.check(false);
    expect(h.files.binaries.has("x.png")).toBe(false);
    expect(h.files.systemTrash).toEqual(["x.png"]);
    expect(h.state.state.bases["x.png"]).toBeUndefined();
  });

  test("an attachment trashed in Kumbu² but changed here is uploaded again, not trashed", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    await h.server.removeFile("x.png", await versionOfBytes(png(1)));
    h.files.binaries.set("x.png", png(1, 9));
    await h.engine.check(false);
    expect(h.files.systemTrash).toEqual([]);
    expect(h.server.files.get("x.png")?.bytes).toEqual(png(1, 9));
  });

  test("files added while the server couldn't be reached sync once it says it keeps them", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" } });
    const at = new Date(Date.now() - 60_000).toISOString();
    h.state.state = { ...h.state.state, cursor: at, lastSyncAt: at, lastFullAt: at };
    h.server.failNext("versions", new OfflineError("down"));
    await h.engine.start();
    h.files.binaries.set("x.png", png(1));
    h.engine.localChanged("x.png");
    expect(h.engine.status().notSynced).toEqual([{ path: "x.png", reason: "attachment" }]);
    // The retry is an incremental check, which knows nothing of x.png: the local side is looked at once more.
    await settle(h, 5000);
    expect(h.server.calls.filter((c) => c.method === "versions").map((c) => c.args[0])).toEqual([null, at]);
    expect(h.server.files.get("x.png")?.bytes).toEqual(png(1));
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("an attachment moved in Kumbu² is trashed at its old path and downloaded at the new one", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    expect((await h.server.move("x.png", "img/y.png")).ok).toBe(true);
    await h.engine.check(false);
    expect(h.files.systemTrash).toEqual(["x.png"]);
    expect(bytesOf(h, "img/y.png")).toEqual(png(1));
    expect(h.state.state.bases).toEqual({ "a.md": expect.any(String) as string, "img/y.png": await versionOfBytes(png(1)) });
  });

  test("an nfd file name syncs as its nfc spelling", async () => {
    const nfd = "Bespréchung.png";
    const nfc = "Besprechung.png".replace("ec", "éc");
    expect(nfd.normalize("NFC")).toBe(nfc);
    const h = await harness({ start: false, synced: { "a.md": "one" } });
    h.files.binaries.set(nfd, png(1));
    await h.engine.start();
    expect([...h.server.files.keys()]).toEqual([nfc]);
    expect(h.state.state.bases[nfc]).toBe(await versionOfBytes(png(1)));
    // The server's change lands in the file as the file system spells it, not in a second one.
    await h.server.seedFile(nfc, png(2));
    await h.engine.check(false);
    expect([...h.files.binaries.keys()]).toEqual([nfd]);
    expect(bytesOf(h, nfd)).toEqual(png(2));
  });
});

describe("limits and what stays on the list", () => {
  test("a file too large is never read: not by a first sync, nor when renamed", async () => {
    const h = await harness({ start: false, localOnly: { "a.md": "a" } });
    h.files.binaries.set("big.pdf", pdf(MAX_ATTACHMENT_BYTES + 1));
    const reads = vi.spyOn(h.files, "readBinary");
    const summary = await h.engine.firstSync(() => Promise.resolve("both"));
    expect(summary).toMatchObject({ uploaded: 1, notSynced: [{ path: "big.pdf", reason: "too-large" }] });
    h.files.userRename("big.pdf", "scans/big.pdf");
    h.engine.localRenamed("big.pdf", "scans/big.pdf");
    await settle(h);
    // Asked for at most where nothing is any more (the old path after the rename), never for the bytes.
    const results = await Promise.all(reads.mock.results.map((r) => r.value as Promise<ArrayBuffer | null>));
    expect(results.filter((r) => r !== null)).toEqual([]);
    expect(h.engine.status().notSynced).toEqual([{ path: "scans/big.pdf", reason: "too-large" }]);
    expect(h.server.files.size).toBe(0);
  });

  test("a different version in Kumbu² doesn't come over a file too large here, nor is it read", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.pdf", pdf(10));
    h.files.binaries.set("x.pdf", pdf(MAX_ATTACHMENT_BYTES + 1));
    h.engine.localChanged("x.pdf");
    await settle(h, 2000);
    expect(h.engine.status().notSynced).toEqual([{ path: "x.pdf", reason: "too-large" }]);
    await h.server.seedFile("x.pdf", pdf(20));
    const reads = vi.spyOn(h.files, "readBinary");
    await h.engine.check(false);
    await h.engine.check(true);
    expect(reads).not.toHaveBeenCalled();
    expect(bytesOf(h, "x.pdf")?.byteLength).toBe(MAX_ATTACHMENT_BYTES + 1);
    expect(h.conflicts).toEqual([]);
    expect(h.server.files.get("x.pdf")?.bytes).toEqual(pdf(20));
    expect(h.engine.status().notSynced).toEqual([{ path: "x.pdf", reason: "too-large" }]);

    // Once it fits again it is decided against its old base: both changed, so a conflict copy keeps it.
    h.files.binaries.set("x.pdf", pdf(30));
    h.engine.localChanged("x.pdf");
    await settle(h, 2000);
    expect(bytesOf(h, "x.pdf")).toEqual(pdf(20));
    expect(bytesOf(h, h.conflicts[0]![1])).toEqual(pdf(30));
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("a file too large here is not trashed when Kumbu² trashes it", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.pdf", pdf(10));
    h.files.binaries.set("x.pdf", pdf(MAX_ATTACHMENT_BYTES + 1));
    await h.server.removeFile("x.pdf", await versionOfBytes(pdf(10)));
    await h.engine.check(false);
    await h.engine.check(true);
    expect(bytesOf(h, "x.pdf")?.byteLength).toBe(MAX_ATTACHMENT_BYTES + 1);
    expect(h.files.systemTrash).toEqual([]);
    expect(h.engine.status().notSynced).toEqual([{ path: "x.pdf", reason: "too-large" }]);
  });

  test("the limit is 15 MB exactly", async () => {
    expect(MAX_ATTACHMENT_BYTES).toBe(15_728_640);
    const h = await harness({ start: false, synced: { "a.md": "one" } });
    h.files.binaries.set("fits.pdf", pdf(MAX_ATTACHMENT_BYTES));
    h.files.binaries.set("over.pdf", pdf(MAX_ATTACHMENT_BYTES + 1));
    await h.engine.start();
    expect([...h.server.files.keys()]).toEqual(["fits.pdf"]);
    expect(h.engine.status().notSynced).toEqual([{ path: "over.pdf", reason: "too-large" }]);
  });

  test("a file whose content the server refuses is listed with the server's sentence", async () => {
    const h = await harness({ start: false, synced: { "a.md": "one" } });
    h.files.binaries.set("fake.png", new TextEncoder().encode("MZ not an image"));
    const realPut = h.server.putFile.bind(h.server);
    h.server.putFile = (path, bytes, base) => (path === "fake.png"
      ? Promise.reject(new RejectedError("The content of fake.png is not a PNG image.", 400))
      : realPut(path, bytes, base));
    await h.engine.start();
    expect(h.engine.status().notSynced).toEqual([{ path: "fake.png", reason: "rejected", detail: "The content of fake.png is not a PNG image." }]);
  });

});

describe("the first sync", () => {
  test("merges attachments like notes, asking once for the ones that differ", async () => {
    const h = await harness({
      start: false, localOnly: { "a.md": "a" }, remoteFiles: { "same.png": png(1), "differs.png": png(2), "theirs.pdf": pdf(10) },
    });
    h.files.binaries.set("same.png", png(1));
    h.files.binaries.set("differs.png", png(3));
    h.files.binaries.set("mine.png", png(4));
    const summary = await h.engine.firstSync(() => Promise.resolve("kumbu"));
    expect(summary).toMatchObject({ uploaded: 2, downloaded: 2, unchanged: 1, conflictCopies: 0 });
    expect(bytesOf(h, "differs.png")).toEqual(png(2));
    expect(h.files.trash).toEqual(["differs.png"]);
    expect(bytesOf(h, "theirs.pdf")).toEqual(pdf(10));
    expect(h.server.files.get("mine.png")?.bytes).toEqual(png(4));
  });

  test("keep local replaces Kumbu²'s attachment", async () => {
    const h = await harness({ start: false, remoteFiles: { "differs.png": png(2) } });
    h.files.binaries.set("differs.png", png(3));
    const summary = await h.engine.firstSync(() => Promise.resolve("local"));
    expect(summary).toMatchObject({ uploaded: 1, downloaded: 0 });
    expect(h.server.files.get("differs.png")?.bytes).toEqual(png(3));
  });
});

describe("a file the server lists but can't hand over", () => {
  const CANT_OPEN = "Couldn't sync: Kumbu² can't open this file just now; it is tried again with every sync.";
  /** readFile answers as for a missing file (the server's 404) for `path` until the returned function is called. */
  function unserved(h: Harness, path: string): () => void {
    const real = h.server.readFile.bind(h.server);
    let broken = true;
    h.server.readFile = (p) => (broken && p === path ? Promise.resolve(null) : real(p));
    return () => { broken = false; };
  }

  test("is kept here and listed, not trashed, and arrives once it opens", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    const cursor = h.state.state.cursor;
    await h.server.seedFile("x.png", png(2));
    const repair = unserved(h, "x.png");
    await h.engine.check(false);
    // Past the server's overlap: the answer names x.png no more, and the cursor moved on, yet it is tried again.
    vi.setSystemTime(Date.now() + 10 * 60_000);
    await h.engine.check(false);
    expect(h.server.calls.filter((c) => c.method === "versions").map((c) => c.args[0])).not.toContain(null);
    expect(bytesOf(h, "x.png")).toEqual(png(1));
    expect(h.files.systemTrash).toEqual([]);
    expect(h.state.state.bases["x.png"]).toBe(await versionOfBytes(png(1)));
    expect(h.state.state.cursor).not.toBe(cursor);
    expect(h.engine.status().notSynced).toEqual([{ path: "x.png", reason: "rejected", detail: CANT_OPEN }]);

    repair();
    vi.setSystemTime(Date.now() + 10 * 60_000);
    await h.engine.check(false);
    expect(bytesOf(h, "x.png")).toEqual(png(2));
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("keeps a changed local file whole in a conflict", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    await h.server.seedFile("x.png", png(2));
    h.files.binaries.set("x.png", png(3));
    unserved(h, "x.png");
    await h.engine.check(false);
    expect([...h.files.binaries.keys()]).toEqual(["x.png"]);
    expect(bytesOf(h, "x.png")).toEqual(png(3));
    expect(h.conflicts).toEqual([]);
    expect(h.engine.status().notSynced).toEqual([{ path: "x.png", reason: "rejected", detail: CANT_OPEN }]);
  });

  test("is trashed here when the next answer says it was deleted", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    await h.server.seedFile("x.png", png(2));
    unserved(h, "x.png");
    await h.engine.check(false);
    await h.server.removeFile("x.png", await versionOfBytes(png(2)));
    await h.engine.check(false);
    expect(h.files.systemTrash).toEqual(["x.png"]);
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("doesn't hold up a first sync, which finishes and leaves it to the next check", async () => {
    const h = await harness({ start: false, localOnly: { "a.md": "a" }, remoteFiles: { "x.png": png(2) } });
    const repair = unserved(h, "x.png");
    const summary = await h.engine.firstSync(() => Promise.resolve("both"));
    expect(summary).toMatchObject({ uploaded: 1, downloaded: 0, notSynced: [{ path: "x.png", reason: "rejected", detail: CANT_OPEN }] });
    // Finished: the plugin starts with start(), not a first sync, whenever a cursor is saved.
    expect(h.state.state.cursor).not.toBeNull();
    repair();
    vi.setSystemTime(Date.now() + 10 * 60_000);
    await h.engine.check(false);
    expect(bytesOf(h, "x.png")).toEqual(png(2));
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("a restart after a first sync with a file Kumbu² can't hand over doesn't run the first sync again", async () => {
    const h = await harness({ start: false, remoteFiles: { "x.png": png(2), "d.png": png(4) } });
    h.files.binaries.set("d.png", png(5));
    const repair = unserved(h, "x.png");
    let asked = 0;
    await h.engine.firstSync(() => { asked++; return Promise.resolve("both"); });
    expect(asked).toBe(1);
    expect(h.state.state.cursor).not.toBeNull();
    expect(h.state.state.pendingChoice).toBeUndefined();

    // As the plugin's next start: a saved cursor means start(), whose full check finds x.png again.
    await h.engine.stop();
    await h.engine.start();
    expect(h.engine.status().notSynced).toEqual([{ path: "x.png", reason: "rejected", detail: CANT_OPEN }]);
    repair();
    await h.engine.stop();
    await h.engine.start();
    expect(bytesOf(h, "x.png")).toEqual(png(2));
    expect(asked).toBe(1);
    expect(h.files.trash).toEqual([]);
    expect(h.engine.status().notSynced).toEqual([]);
  });
});

describe("an attachment is read only when it may have changed", () => {
  /** Counts the reads of `path` from now on. */
  function countReads(h: Harness, path: string): () => number {
    const spy = vi.spyOn(h.files, "readBinary");
    return () => spy.mock.calls.filter(([p]) => p === path).length;
  }

  test("a full check doesn't read an unchanged file, and reads it once its stat changes", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    const reads = countReads(h, "x.png");
    await h.engine.check(true);
    expect(reads()).toBe(0);
    // Kept with the bases, so a restart doesn't read it either.
    await h.engine.stop();
    await h.engine.start();
    expect(reads()).toBe(0);

    h.files.touch("x.png");
    await h.engine.check(true);
    expect(reads()).toBe(1);
    await h.engine.check(true);
    expect(reads()).toBe(1);

    // Changed without an event (by another program, while Obsidian was closed): the new size gives it away.
    h.files.binaries.set("x.png", png(1, 2));
    await h.engine.check(true);
    expect(reads()).toBe(2);
    expect(h.server.files.get("x.png")?.bytes).toEqual(png(1, 2));
    expect(h.server.count("putFile")).toBe(1);
  });

  test("news from the server is decided with the local bytes read", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    const reads = countReads(h, "x.png");
    await h.server.seedFile("x.png", png(2));
    await h.engine.check(false);
    expect(reads()).toBe(1);
    expect(bytesOf(h, "x.png")).toEqual(png(2));
  });

  test("a file deleted here is forgotten", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    expect(h.state.state.hashed?.["x.png"]).toBeDefined();
    h.files.userDelete("x.png");
    h.engine.localDeleted("x.png");
    await settle(h);
    expect(h.state.state.hashed?.["x.png"]).toBeUndefined();
  });
});

describe("a file too large here and the first sync", () => {
  const big = () => pdf(MAX_ATTACHMENT_BYTES + 1);

  for (const choice of ["both", "local", "kumbu"] as const) {
    test(`stays as it is and out of the question with “${choice}”`, async () => {
      const h = await harness({ start: false, localOnly: { "a.md": "a" }, remoteFiles: { "big.pdf": pdf(10), "d.png": png(2) } });
      h.files.binaries.set("big.pdf", big());
      h.files.binaries.set("d.png", png(3));
      const reads = vi.spyOn(h.files, "readBinary");
      let differing: string[] = [];
      await h.engine.firstSync((plan) => { differing = plan.differing; return Promise.resolve(choice); });
      expect(differing).toEqual(["d.png"]);
      expect(reads.mock.calls.filter(([p]) => p === "big.pdf")).toEqual([]);
      expect(bytesOf(h, "big.pdf")?.byteLength).toBe(MAX_ATTACHMENT_BYTES + 1);
      expect(h.files.trash).not.toContain("big.pdf");
      expect(h.conflicts.map(([original]) => original)).not.toContain("big.pdf");
      expect(h.server.files.get("big.pdf")?.bytes).toEqual(pdf(10));
      expect(h.engine.status().notSynced).toEqual([{ path: "big.pdf", reason: "too-large" }]);
    });
  }

  for (const choice of ["local", "kumbu"] as const) {
    test(`stays as it is when it grows while “${choice}” is being chosen`, async () => {
      const h = await harness({ start: false, remoteFiles: { "x.pdf": pdf(10) } });
      h.files.binaries.set("x.pdf", pdf(20));
      await h.engine.firstSync(() => {
        h.files.binaries.set("x.pdf", big());
        return Promise.resolve(choice);
      });
      expect(bytesOf(h, "x.pdf")?.byteLength).toBe(MAX_ATTACHMENT_BYTES + 1);
      expect(h.files.trash).toEqual([]);
      expect(h.server.files.get("x.pdf")?.bytes).toEqual(pdf(10));
      expect(h.engine.status().notSynced).toEqual([{ path: "x.pdf", reason: "too-large" }]);
    });
  }
});

describe("across support and renames", () => {
  test("a server that stops keeping attachments for a while loses nothing, and learns of a deletion meanwhile", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    await synced(h, "y.png", png(2));
    // Past the server's overlap, so the answer after support returns doesn't name x.png.
    vi.setSystemTime(Date.now() + 10 * 60_000);
    h.server.attachmentsSupported = false;
    await h.engine.check(true);
    expect(h.engine.status().notSynced).toEqual([{ path: "x.png", reason: "attachment" }, { path: "y.png", reason: "attachment" }]);
    h.files.userDelete("x.png");
    h.engine.localDeleted("x.png");
    await settle(h);
    await h.engine.check(true);
    expect(h.files.systemTrash).toEqual([]);
    expect(bytesOf(h, "y.png")).toEqual(png(2));
    expect(h.server.files.has("x.png")).toBe(true);

    h.server.attachmentsSupported = true;
    await h.engine.check(false);
    expect(h.server.files.has("x.png")).toBe(false);
    expect(h.server.files.get("y.png")?.bytes).toEqual(png(2));
    expect(bytesOf(h, "y.png")).toEqual(png(2));
    await h.engine.check(true);
    expect(h.server.count("removeFile")).toBe(1);
    expect(h.server.count("putFile")).toBe(0);
    expect(h.engine.status().notSynced).toEqual([]);
    expect(Object.keys(h.state.state.bases).sort()).toEqual(["a.md", "y.png"]);
  });

  test("a rename the server can't move (a.png → a.jpg) is a delete and a create, and the refusal is listed", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "a.png", png(1));
    const realPut = h.server.putFile.bind(h.server);
    let refused = 0;
    h.server.putFile = (path, bytes, base) => {
      if (path !== "a.jpg") return realPut(path, bytes, base);
      refused++;
      return Promise.reject(new RejectedError("The content of a.jpg is not a JPEG image.", 400));
    };
    h.files.userRename("a.png", "a.jpg");
    h.engine.localRenamed("a.png", "a.jpg");
    await settle(h);
    expect(h.server.calls.map((c) => c.method)).toEqual(["move", "removeFile"]);
    expect(refused).toBe(1);
    // In Kumbu²'s trash, and whole here under its new name.
    expect(h.server.files.has("a.png")).toBe(false);
    expect(bytesOf(h, "a.jpg")).toEqual(png(1));
    expect(h.engine.status().notSynced).toEqual([{ path: "a.jpg", reason: "rejected", detail: "The content of a.jpg is not a JPEG image." }]);
    await h.engine.check(true);
    expect(refused).toBe(1);
  });

  test("a rename within one format (a.jpg → a.jpeg) is a move", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    const jpeg = new Uint8Array([0xff, 0xd8, 0xff, 1]);
    await synced(h, "a.jpg", jpeg);
    h.files.userRename("a.jpg", "a.jpeg");
    h.engine.localRenamed("a.jpg", "a.jpeg");
    await settle(h);
    expect(h.server.calls.map((c) => c.method)).toEqual(["move"]);
    expect(h.server.files.get("a.jpeg")?.bytes).toEqual(jpeg);
  });

  test("a download that loses the race with a local change keeps that change in a conflict copy", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await synced(h, "x.png", png(1));
    await h.server.seedFile("x.png", png(2));
    const realWrite = h.files.writeBinaryIf.bind(h.files);
    let raced = false;
    h.files.writeBinaryIf = (path, expected, bytes) => {
      if (path === "x.png" && !raced) {
        raced = true;
        h.files.binaries.set("x.png", png(9));
      }
      return realWrite(path, expected, bytes);
    };
    await h.engine.check(false);
    expect(raced).toBe(true);
    expect(bytesOf(h, "x.png")).toEqual(png(2));
    const copy = h.conflicts[0]![1];
    expect(bytesOf(h, copy)).toEqual(png(9));
    expect(h.server.files.get(copy)?.bytes).toEqual(png(9));
  });

  test("the damaged count names files too on a server with attachments", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.damaged = 2;
    await h.engine.check(true);
    expect(h.engine.status().notSynced).toContainEqual({
      path: "", reason: "damaged-vault",
      detail: "2 notes or files in Kumbu² can't be opened, so no local file is trashed because a note or file is missing.",
    });
  });
});
