// SPDX-License-Identifier: AGPL-3.0-or-later
// The host's fetch policy (offline mode design §6): which attachments' bytes the engine downloads.
import { afterEach, describe, expect, test, vi } from "vitest";
import { harness, settle } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); });

const png = (...rest: number[]) => new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, ...rest]);

describe("the fetch policy", () => {
  test("is asked with each attachment's path and size, and a no leaves its bytes in Kumbu²", async () => {
    const asked: [string, number | null][] = [];
    const h = await harness({
      remoteOnly: { "a.md": "one" }, remoteFiles: { "img/big.png": png(1, 2, 3), "img/small.png": png(1) },
      wantBytes: (path, size) => { asked.push([path, size]); return path !== "img/big.png"; },
    });
    expect(h.files.files.get("a.md")).toBe("one");
    expect(h.files.binaries.has("img/small.png")).toBe(true);
    expect(h.files.binaries.has("img/big.png")).toBe(false);
    expect(asked).toContainEqual(["img/big.png", 11]);
    expect(asked).toContainEqual(["img/small.png", 9]);
    expect(asked.map(([path]) => path)).not.toContain("a.md");
    // The host's own choice, so not a problem to list.
    expect(h.engine.status().notSynced).toEqual([]);
    expect(h.state.state.bases["img/big.png"]).toBeUndefined();
  });

  test("a yes after a no downloads the file at the next check", async () => {
    let want = false;
    const h = await harness({ remoteFiles: { "scan.png": png(7) }, wantBytes: () => want });
    expect(h.files.binaries.has("scan.png")).toBe(false);
    want = true;
    await h.engine.check(true);
    await settle(h);
    expect(h.files.binaries.get("scan.png")).toEqual(png(7));
  });

  test("a declined file kept here is neither replaced, sent nor trashed while Kumbu² changes it", async () => {
    let want = true;
    const h = await harness({ remoteFiles: { "photo.png": png(1) }, wantBytes: () => want });
    expect(h.files.binaries.get("photo.png")).toEqual(png(1));
    want = false;
    await h.server.seedFile("photo.png", png(2));
    await h.engine.check(true);
    await settle(h);
    expect(h.files.binaries.get("photo.png")).toEqual(png(1));
    expect(h.server.files.get("photo.png")?.bytes).toEqual(png(2));
    expect(h.server.count("putFile")).toBe(0);
  });

  test("is not asked for a file only here, which is uploaded", async () => {
    const asked: string[] = [];
    const h = await harness({ start: false, wantBytes: (path) => { asked.push(path); return false; } });
    h.files.binaries.set("mine.png", png(4));
    await h.engine.start();
    expect(h.server.files.get("mine.png")?.bytes).toEqual(png(4));
    expect(asked).toEqual([]);
  });

  test("a file deleted in Kumbu² is trashed here as usual, whatever the policy says", async () => {
    let want = true;
    const h = await harness({ remoteFiles: { "old.png": png(5) }, wantBytes: () => want });
    want = false;
    await h.server.removeFile("old.png", h.state.state.bases["old.png"]!);
    await h.engine.check(true);
    await settle(h);
    expect(h.files.binaries.has("old.png")).toBe(false);
  });
});
