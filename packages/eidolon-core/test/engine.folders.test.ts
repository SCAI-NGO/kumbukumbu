// SPDX-License-Identifier: AGPL-3.0-or-later
// Folder markers (.vaultkeep) through the engine: synced as empty files, and the folders they keep.
import { afterEach, describe, expect, test, vi } from "vitest";
import { FOLDER_SETTLE_MS } from "../src/engine/engine";
import type { Choice, MergePlan } from "../src/engine/firstSync";
import { OfflineError } from "../src/engine/ports";
import { MARKER_VERSION } from "../src/engine/version";
import { harness, settle, waitFor, type Harness } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); });

const EMPTY = new Uint8Array(0);
const putFiles = (h: Harness) => h.server.calls.filter((c) => c.method === "putFile");
const byteLengthOf = (call: { args: unknown[] }) => (call.args[1] as ArrayBuffer).byteLength;

/** Puts a marker on disk, on the server and in the bases, as an earlier sync would have left it. */
async function synced(h: Harness, path: string): Promise<void> {
  await h.server.seedFile(path, EMPTY);
  h.files.binaries.set(path, EMPTY.slice());
  h.state.state.bases[path] = MARKER_VERSION;
  await h.engine.stop();
  await h.engine.start();
  h.server.calls.length = 0;
}

/** The user makes an empty folder, and Obsidian reports it. */
function userMakesFolder(h: Harness, path: string): void {
  h.files.folders.add(path);
  h.engine.localFolderCreated(path);
}

/** The user renames a folder holding nothing but these markers (vault-relative paths below it). */
function userRenamesFolder(h: Harness, from: string, to: string, markers: string[] = []): void {
  for (const m of markers) h.files.userRename(`${from}/${m}`, `${to}/${m}`);
  for (const f of [...h.files.folders]) {
    if (f === from || f.startsWith(`${from}/`)) {
      h.files.folders.delete(f);
      h.files.folders.add(to + f.slice(from.length));
    }
  }
}

describe("markers sync both ways", () => {
  test("a marker in Kumbu² makes its folder here", async () => {
    const h = await harness({ remoteFiles: { "Drafts/.vaultkeep": EMPTY } });
    expect(h.files.binaries.get("Drafts/.vaultkeep")).toEqual(EMPTY);
    expect(h.state.state.bases["Drafts/.vaultkeep"]).toBe(MARKER_VERSION);
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("a marker here is uploaded empty by the full check", async () => {
    const h = await harness({ start: false });
    h.files.binaries.set("Drafts/.vaultkeep", EMPTY);
    await h.engine.start();
    const puts = putFiles(h);
    expect(puts).toHaveLength(1);
    expect(puts[0]!.args[0]).toBe("Drafts/.vaultkeep");
    expect(puts[0]!.args[2]).toBeNull();
    expect(byteLengthOf(puts[0]!)).toBe(0);
    expect(h.state.state.bases["Drafts/.vaultkeep"]).toBe(MARKER_VERSION);
  });

  test("a marker with content here is sent empty and makes no conflict copy", async () => {
    const kept = await harness({ start: false, remoteFiles: { "Drafts/.vaultkeep": EMPTY } });
    kept.files.binaries.set("Drafts/.vaultkeep", new Uint8Array([1, 2]));
    await kept.engine.start();
    expect(putFiles(kept)).toHaveLength(0);
    expect(kept.conflicts).toEqual([]);
    expect(kept.state.state.bases["Drafts/.vaultkeep"]).toBe(MARKER_VERSION);

    const fresh = await harness({ start: false });
    fresh.files.binaries.set("Drafts/.vaultkeep", new Uint8Array([1, 2]));
    await fresh.engine.start();
    const puts = putFiles(fresh);
    expect(puts).toHaveLength(1);
    expect(byteLengthOf(puts[0]!)).toBe(0);
    expect(fresh.conflicts).toEqual([]);
  });

  test("an nfd folder's marker is the server's nfc one", async () => {
    const nfd = "Bespréchung/.vaultkeep".normalize("NFD");
    const nfc = "Bespréchung/.vaultkeep".normalize("NFC");
    const h = await harness({ start: false, remoteFiles: { [nfc]: EMPTY } });
    h.files.binaries.set(nfd, EMPTY);
    await h.engine.start();
    expect(putFiles(h)).toHaveLength(0);
    expect(h.files.writes).toBe(0);
    expect(Object.keys(h.state.state.bases)).toEqual([nfc]);
  });

  test("other dot files stay ignored beside a marker", async () => {
    const h = await harness({ start: false });
    h.files.files.set(".obsidian/app.json", "{}");
    h.files.binaries.set("Drafts/.DS_Store", new Uint8Array([0]));
    h.files.binaries.set("Drafts/.vaultkeep", EMPTY);
    await h.engine.start();
    expect(putFiles(h).map((c) => c.args[0])).toEqual(["Drafts/.vaultkeep"]);
    expect(h.server.count("put")).toBe(0);
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("a folder holding a marker survives the left-empty sweep", async () => {
    const h = await harness({ synced: { "Drafts/a.md": "a" } });
    await synced(h, "Drafts/.vaultkeep");
    await h.server.agentWrite("Drafts/a.md", null);
    await h.engine.check(false);
    expect(h.files.files.has("Drafts/a.md")).toBe(false);
    expect(h.files.removedFolders).not.toContain("Drafts");
    expect(h.files.binaries.has("Drafts/.vaultkeep")).toBe(true);
  });

  test("a marker removed in Kumbu² takes its empty folder with it", async () => {
    const h = await harness();
    await synced(h, "Drafts/.vaultkeep");
    h.server.agentRemoveFile("Drafts/.vaultkeep");
    await h.engine.check(false);
    expect(h.files.binaries.has("Drafts/.vaultkeep")).toBe(false);
    expect(h.files.removedFolders).toContain("Drafts");
    expect(h.state.state.bases["Drafts/.vaultkeep"]).toBeUndefined();
  });

  test("a marker removed in Kumbu² leaves a folder that still holds notes", async () => {
    const h = await harness({ synced: { "Drafts/a.md": "a" } });
    await synced(h, "Drafts/.vaultkeep");
    h.server.agentRemoveFile("Drafts/.vaultkeep");
    await h.engine.check(false);
    expect(h.files.binaries.has("Drafts/.vaultkeep")).toBe(false);
    expect(h.files.removedFolders).not.toContain("Drafts");
    expect(h.files.files.get("Drafts/a.md")).toBe("a");
  });

  test("a folder moved in Kumbu² moves its marker here", async () => {
    const h = await harness();
    await synced(h, "Old/.vaultkeep");
    await h.server.move("Old/.vaultkeep", "New/.vaultkeep");
    h.server.calls.length = 0;
    await h.engine.check(false);
    expect(h.files.binaries.get("New/.vaultkeep")).toEqual(EMPTY);
    expect(h.files.binaries.has("Old/.vaultkeep")).toBe(false);
    expect(h.files.removedFolders).toContain("Old");
    expect(h.state.state.bases).toEqual({ "New/.vaultkeep": MARKER_VERSION });
  });

  test("markers are ignored on a server without attachments", async () => {
    const h = await harness({ start: false, oldServer: true });
    h.files.binaries.set("Drafts/.vaultkeep", EMPTY);
    await h.engine.start();
    await h.engine.check(true);
    expect(h.server.calls.map((c) => c.method)).toEqual(["versions", "versions"]);
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("the first sync syncs markers but never counts or asks about them", async () => {
    const h = await harness({
      start: false, localOnly: { "a.md": "a", "d.md": "local" }, remoteOnly: { "d.md": "kumbu" },
      remoteFiles: { "B/.vaultkeep": EMPTY, "C/.vaultkeep": EMPTY },
    });
    h.files.binaries.set("A/.vaultkeep", EMPTY);
    h.files.binaries.set("C/.vaultkeep", new Uint8Array([1]));
    const plans: MergePlan[] = [];
    const choose = (plan: MergePlan) => { plans.push(plan); return Promise.resolve<Choice | null>("both"); };
    const summary = await h.engine.firstSync(choose);
    expect(plans).toHaveLength(1);
    expect(plans[0]).toMatchObject({ toUpload: ["a.md"], toDownload: [], same: [], differing: ["d.md"], notSynced: [] });
    expect(summary).toEqual({ uploaded: 1, downloaded: 0, unchanged: 0, conflictCopies: 1, notSynced: [] });
    expect(h.state.state.bases).toMatchObject({ "A/.vaultkeep": MARKER_VERSION, "B/.vaultkeep": MARKER_VERSION, "C/.vaultkeep": MARKER_VERSION });
    expect(h.server.files.has("A/.vaultkeep")).toBe(true);
    expect(h.files.binaries.get("B/.vaultkeep")).toEqual(EMPTY);
  });
});

describe("a server without folder markers", () => {
  async function refused() {
    const h = await harness({ start: false, localOnly: { "a.md": "a" } });
    h.server.markersSupported = false;
    h.files.binaries.set("A/.vaultkeep", EMPTY);
    h.files.binaries.set("B/.vaultkeep", EMPTY);
    return h;
  }

  test("a server that refuses markers is asked once per start and lists nothing", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const h = await refused();
    await h.engine.start();
    expect(h.server.count("putFile")).toBe(1);
    expect(h.server.notes.has("a.md")).toBe(true);
    expect(h.engine.status().notSynced).toEqual([]);
    expect(h.state.state.cursor).not.toBeNull();
    expect(warn).toHaveBeenCalledTimes(1);

    await h.engine.check(true);
    expect(h.server.count("putFile")).toBe(1);
    expect(warn).toHaveBeenCalledTimes(1);

    await h.engine.stop();
    await h.engine.start();
    expect(h.server.count("putFile")).toBe(2);
    warn.mockRestore();
  });

  test("a refused marker is never retried by the debounce", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const h = await refused();
    await h.engine.start();
    h.engine.localChanged("A/.vaultkeep");
    await settle(h, 2000);
    expect(h.server.count("putFile")).toBe(1);
    warn.mockRestore();
  });

  test("a refused removal of a marker is final too", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const h = await harness();
    await synced(h, "A/.vaultkeep");
    h.server.markersSupported = false;
    h.files.binaries.delete("A/.vaultkeep");
    h.engine.localChanged("A/.vaultkeep");
    await settle(h, 2000);
    expect(h.server.count("removeFile")).toBe(1);
    expect(h.engine.status().notSynced).toEqual([]);
    expect(warn).toHaveBeenCalledTimes(1);
    h.engine.localChanged("A/.vaultkeep");
    await settle(h, 2000);
    expect(h.server.count("removeFile")).toBe(1);
    warn.mockRestore();
  });
});

describe("folders made in Obsidian", () => {
  test("an empty folder made here gets a marker after 5 s", async () => {
    expect(FOLDER_SETTLE_MS).toBe(5000);
    const h = await harness();
    userMakesFolder(h, "Drafts");
    await settle(h, 4999);
    expect(h.files.binaries.has("Drafts/.vaultkeep")).toBe(false);
    await settle(h, 1 + 2000);
    expect(h.files.binaries.get("Drafts/.vaultkeep")).toEqual(EMPTY);
    expect(h.server.files.has("Drafts/.vaultkeep")).toBe(true);
    expect(putFiles(h).map(byteLengthOf)).toEqual([0]);
  });

  test("a folder that holds something by then gets none", async () => {
    const h = await harness();
    userMakesFolder(h, "Drafts");
    await settle(h, 3000);
    h.files.userEdit("Drafts/a.md", "a");
    h.engine.localChanged("Drafts/a.md");
    await settle(h, 2000 + 2000);
    expect(h.files.binaries.has("Drafts/.vaultkeep")).toBe(false);
    expect(h.server.files.has("Drafts/.vaultkeep")).toBe(false);
    expect(h.server.notes.has("Drafts/a.md")).toBe(true);
  });

  test("a folder the plugin made itself gets none", async () => {
    const h = await harness();
    h.files.madeHere.add("Drafts");
    userMakesFolder(h, "Drafts");
    await settle(h, 5000 + 2000);
    expect(h.files.binaries.has("Drafts/.vaultkeep")).toBe(false);
    expect(putFiles(h)).toHaveLength(0);
    expect(h.files.madeHere.has("Drafts")).toBe(false);
  });

  test("a folder the user made before a download into it is not one the plugin made", async () => {
    const h = await harness();
    userMakesFolder(h, "Drafts");
    await h.server.agentWrite("Drafts/a.md", "a");
    await h.server.agentWrite("New/b.md", "b");
    await h.engine.check(false);
    expect(h.files.files.get("Drafts/a.md")).toBe("a");
    // The download made New, but Drafts was there before it.
    expect(h.files.madeHere.has("Drafts")).toBe(false);
    expect(h.files.madeHere.has("New")).toBe(true);
    // The user moves the note out again within the wait: Drafts is empty, and theirs.
    h.files.userRename("Drafts/a.md", "a.md");
    h.engine.localRenamed("Drafts/a.md", "a.md");
    await settle(h, 5000);
    await settle(h, 2000);
    expect(h.files.binaries.get("Drafts/.vaultkeep")).toEqual(EMPTY);
    expect(h.server.files.has("Drafts/.vaultkeep")).toBe(true);
  });

  test("no marker while offline, on a server without attachments, in a dot folder, or during the first sync", async () => {
    const offline = await harness();
    userMakesFolder(offline, "Drafts");
    offline.server.failNext("versions", new OfflineError("down"));
    await offline.engine.check(false);
    await settle(offline, 5000 + 2000);
    expect(offline.files.writes).toBe(0);
    expect(offline.server.count("putFile")).toBe(0);

    const old = await harness({ oldServer: true });
    userMakesFolder(old, "Drafts");
    await settle(old, 5000 + 2000);
    expect(old.files.writes).toBe(0);
    expect(old.server.count("putFile")).toBe(0);

    const dot = await harness();
    userMakesFolder(dot, "a/.hidden");
    await settle(dot, 5000 + 2000);
    expect(dot.files.writes).toBe(0);
    expect(dot.server.count("putFile")).toBe(0);

    const first = await harness({ start: false });
    const release = first.server.hold("versions");
    const done = first.engine.firstSync(() => Promise.resolve<Choice | null>("both"));
    userMakesFolder(first, "Drafts");
    // Not settle: the queue waits for the held versions call.
    await vi.advanceTimersByTimeAsync(5000);
    release();
    await done;
    await settle(first, 2000);
    expect(first.files.writes).toBe(0);
    expect(first.server.count("putFile")).toBe(0);
  });

  test("a folder renamed within the wait moves the wait to its new name", async () => {
    const h = await harness();
    userMakesFolder(h, "Untitled");
    await settle(h, 3000);
    userRenamesFolder(h, "Untitled", "Drafts");
    h.engine.localFolderRenamed("Untitled", "Drafts");
    await settle(h, 3000);
    expect(h.files.binaries.size).toBe(0);
    expect(h.server.files.size).toBe(0);
    await settle(h, 2000 + 2000);
    expect([...h.files.binaries.keys()]).toEqual(["Drafts/.vaultkeep"]);
    expect([...h.server.files.keys()]).toEqual(["Drafts/.vaultkeep"]);
  });

  test("an Untitled folder named after its marker leaves one folder", async () => {
    const h = await harness();
    userMakesFolder(h, "Untitled");
    await settle(h, 5000 + 2000);
    expect([...h.server.files.keys()]).toEqual(["Untitled/.vaultkeep"]);
    h.server.calls.length = 0;
    userRenamesFolder(h, "Untitled", "Drafts", [".vaultkeep"]);
    h.engine.localFolderRenamed("Untitled", "Drafts");
    await settle(h, 5000 + 2000);
    expect(h.server.count("move")).toBe(1);
    expect([...h.server.files.keys()]).toEqual(["Drafts/.vaultkeep"]);
    expect([...h.files.binaries.keys()]).toEqual(["Drafts/.vaultkeep"]);
  });

  test("an Untitled folder renamed before its new marker was uploaded leaves one folder", async () => {
    const h = await harness();
    userMakesFolder(h, "Untitled");
    await settle(h, 5000);
    expect([...h.files.binaries.keys()]).toEqual(["Untitled/.vaultkeep"]);
    expect(h.server.files.size).toBe(0);
    userRenamesFolder(h, "Untitled", "Drafts", [".vaultkeep"]);
    h.engine.localFolderRenamed("Untitled", "Drafts");
    await settle(h, 2000 + 2000);
    expect([...h.server.files.keys()]).toEqual(["Drafts/.vaultkeep"]);
    expect([...h.files.binaries.keys()]).toEqual(["Drafts/.vaultkeep"]);
    expect(h.state.state.bases).toEqual({ "Drafts/.vaultkeep": MARKER_VERSION });
  });

  test("an Untitled folder renamed while its new marker is uploading leaves one folder", async () => {
    const h = await harness();
    userMakesFolder(h, "Untitled");
    await settle(h, 5000);
    const release = h.server.hold("putFile");
    await vi.advanceTimersByTimeAsync(2000);
    await waitFor(() => h.server.count("putFile") > 0);
    userRenamesFolder(h, "Untitled", "Drafts", [".vaultkeep"]);
    h.engine.localFolderRenamed("Untitled", "Drafts");
    release();
    await settle(h, 2000 + 2000);
    expect([...h.server.files.keys()]).toEqual(["Drafts/.vaultkeep"]);
    expect([...h.files.binaries.keys()]).toEqual(["Drafts/.vaultkeep"]);
    expect(h.state.state.bases).toEqual({ "Drafts/.vaultkeep": MARKER_VERSION });
  });

  test("a folder deleted within the wait gets no marker", async () => {
    const h = await harness();
    userMakesFolder(h, "Drafts");
    await settle(h, 2000);
    h.files.folders.delete("Drafts");
    h.engine.localFolderDeleted("Drafts");
    await settle(h, 7000);
    expect(h.files.writes).toBe(0);
    expect(putFiles(h)).toHaveLength(0);
  });

  test("stop cancels a folder's wait", async () => {
    const h = await harness();
    userMakesFolder(h, "Drafts");
    await h.engine.stop();
    await settle(h, 7000);
    expect(h.files.writes).toBe(0);
  });
});

describe("kept folders renamed and deleted here", () => {
  test("renaming a kept folder moves its markers in Kumbu²", async () => {
    const h = await harness();
    await synced(h, "Old/.vaultkeep");
    await synced(h, "Old/Sub/.vaultkeep");
    userRenamesFolder(h, "Old", "New", [".vaultkeep", "Sub/.vaultkeep"]);
    h.engine.localFolderRenamed("Old", "New");
    await settle(h);
    expect(h.server.calls.filter((c) => c.method === "move").map((c) => c.args)).toEqual([
      ["Old/.vaultkeep", "New/.vaultkeep"], ["Old/Sub/.vaultkeep", "New/Sub/.vaultkeep"],
    ]);
    expect(putFiles(h)).toHaveLength(0);
    expect(h.state.state.bases).toEqual({ "New/.vaultkeep": MARKER_VERSION, "New/Sub/.vaultkeep": MARKER_VERSION });
  });

  test("a rename reported for the folder and its subfolder moves each marker once", async () => {
    const h = await harness();
    await synced(h, "Old/.vaultkeep");
    await synced(h, "Old/Sub/.vaultkeep");
    userRenamesFolder(h, "Old", "New", [".vaultkeep", "Sub/.vaultkeep"]);
    h.engine.localFolderRenamed("Old", "New");
    h.engine.localFolderRenamed("Old/Sub", "New/Sub");
    await settle(h, 2000);
    expect(h.server.count("move")).toBe(2);
    expect(h.server.count("removeFile")).toBe(0);
    expect(putFiles(h)).toHaveLength(0);
    expect([...h.server.files.keys()].sort()).toEqual(["New/.vaultkeep", "New/Sub/.vaultkeep"]);
  });

  test("a kept folder moved into a dot folder loses its marker in Kumbu²", async () => {
    const h = await harness();
    await synced(h, "Drafts/.vaultkeep");
    userRenamesFolder(h, "Drafts", ".archive/Drafts", [".vaultkeep"]);
    h.engine.localFolderRenamed("Drafts", ".archive/Drafts");
    await settle(h, 2000);
    const removes = h.server.calls.filter((c) => c.method === "removeFile");
    expect(removes.map((c) => c.args)).toEqual([["Drafts/.vaultkeep", MARKER_VERSION]]);
    expect(putFiles(h)).toHaveLength(0);
    expect(h.server.count("move")).toBe(0);
    expect(h.server.files.size).toBe(0);
  });

  test("deleting a kept folder removes its marker in Kumbu²", async () => {
    const h = await harness();
    await synced(h, "Drafts/.vaultkeep");
    h.files.userDelete("Drafts/.vaultkeep");
    h.files.folders.delete("Drafts");
    h.engine.localFolderDeleted("Drafts");
    await settle(h);
    const removes = h.server.calls.filter((c) => c.method === "removeFile");
    expect(removes.map((c) => c.args)).toEqual([["Drafts/.vaultkeep", MARKER_VERSION]]);
    expect(h.state.state.bases["Drafts/.vaultkeep"]).toBeUndefined();
  });
});
