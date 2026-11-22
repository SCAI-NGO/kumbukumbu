// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, test, vi } from "vitest";
import { AuthError, OfflineError, RejectedError } from "../src/engine/ports";
import { versionOf } from "../src/engine/version";
import { harness, settle, waitFor } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); });

const copies = (files: Map<string, string>) => [...files.keys()].filter((p) => p.includes("(conflict "));

describe("normal use", () => {
  test("uploads a local edit 2 s after the last change", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.files.userEdit("a.md", "two");
    h.engine.localChanged("a.md");
    await vi.advanceTimersByTimeAsync(1500);
    h.files.userEdit("a.md", "three");
    h.engine.localChanged("a.md");
    await vi.advanceTimersByTimeAsync(1999);
    expect(h.server.count("put")).toBe(0);
    await settle(h, 1);
    expect(h.server.count("put")).toBe(1);
    expect(h.server.notes.get("a.md")!.content).toBe("three");
    expect(h.state.state.bases["a.md"]).toBe(await versionOf("three"));
  });

  test("a check does not upload a path whose debounce is still running", async () => {
    const h = await harness({ synced: { "a.md": "one", "b.md": "b" } });
    h.files.userEdit("a.md", "half typed");
    h.engine.localChanged("a.md");
    await vi.advanceTimersByTimeAsync(500);
    await h.engine.check(false);
    await h.engine.check(true);
    expect(h.server.count("put")).toBe(0);
    h.files.userEdit("a.md", "typed");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    expect(h.server.count("put")).toBe(1);
    expect(h.server.notes.get("a.md")!.content).toBe("typed");
  });

  test("a check still decides a debounced path the server changed", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.files.userEdit("a.md", "mine");
    h.engine.localChanged("a.md");
    await h.server.agentWrite("a.md", "agent");
    await vi.advanceTimersByTimeAsync(500);
    await h.engine.check(false);
    expect(h.files.files.get("a.md")).toBe("agent");
    expect([...h.files.files.values()].some((t) => t.includes("mine"))).toBe(true);
  });

  test("downloads an agent edit on check", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("a.md", "agent text");
    await h.engine.check(false);
    expect(h.files.files.get("a.md")).toBe("agent text");
    expect(h.state.state.bases["a.md"]).toBe(await versionOf("agent text"));
  });

  test("downloads a new agent note on check", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("new/b.md", "fresh");
    await h.engine.check(false);
    expect(h.files.files.get("new/b.md")).toBe("fresh");
  });

  test("own upload coming back causes nothing", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.files.userEdit("a.md", "two");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    h.server.calls.length = 0;
    await h.engine.check(false);
    expect(h.server.calls.map((c) => c.method)).toEqual(["versions"]);
  });

  test("a 409 on upload with local = remote records the base", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("a.md", "same");
    h.files.userEdit("a.md", "same");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    expect(h.server.count("put")).toBe(1);
    expect(h.state.state.bases["a.md"]).toBe(await versionOf("same"));
    expect(copies(h.files.files)).toEqual([]);
  });

  test("a lost upload response is retried without a conflict", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.dropResponseOfNext("put");
    h.files.userEdit("a.md", "two");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    expect(h.engine.status().mode).toBe("offline");
    await h.engine.check(false);
    expect(h.engine.status().mode).not.toBe("offline");
    expect(h.state.state.bases["a.md"]).toBe(await versionOf("two"));
    expect(copies(h.files.files)).toEqual([]);
    expect(h.conflicts).toEqual([]);
  });

  test("typing during a download is never overwritten", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("a.md", "agent");
    const release = h.server.hold("read");
    const check = h.engine.check(false);
    await waitFor(() => h.server.count("read") > 0);
    h.files.userEdit("a.md", "typed");
    release();
    await check;
    await settle(h);
    // The server changed too, so this is a real conflict: the typed text survives as a copy.
    const [copy] = copies(h.files.files);
    expect(copy).toBeDefined();
    expect(h.files.files.get(copy!)).toContain("typed");
    expect(h.files.files.get("a.md")).toBe("agent");
    expect(h.conflicts).toHaveLength(1);
  });

  test("the same text created locally while a new note downloads only records the base", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("b.md", "agent");
    const release = h.server.hold("read");
    const check = h.engine.check(false);
    await waitFor(() => h.server.count("read") > 0);
    h.files.userEdit("b.md", "agent");
    release();
    await check;
    expect(h.files.files.get("b.md")).toBe("agent");
    expect(h.state.state.bases["b.md"]).toBe(await versionOf("agent"));
    expect(copies(h.files.files)).toEqual([]);
  });

  test("a local delete trashes the note on the server with the base version", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.files.userDelete("a.md");
    h.engine.localDeleted("a.md");
    await settle(h);
    expect(h.server.calls.find((c) => c.method === "remove")?.args).toEqual(["a.md", await versionOf("one")]);
    expect(h.server.notes.has("a.md")).toBe(false);
    expect(h.state.state.bases["a.md"]).toBeUndefined();
  });

  test("a 409 on delete downloads the remote edit", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("a.md", "agent");
    h.files.userDelete("a.md");
    h.engine.localDeleted("a.md");
    await settle(h);
    expect(h.files.files.get("a.md")).toBe("agent");
    expect(h.server.notes.get("a.md")!.content).toBe("agent");
  });

  test("an agent delete moves the unchanged local file to the trash", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("a.md", null);
    await h.engine.check(false);
    expect(h.files.files.has("a.md")).toBe(false);
    expect(h.files.systemTrash).toEqual(["a.md"]);
    expect(h.state.state.bases["a.md"]).toBeUndefined();
  });

  test("an agent delete of a locally edited note re-uploads it", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("a.md", null);
    h.files.userEdit("a.md", "mine");
    await h.engine.check(false);
    expect(h.server.notes.get("a.md")!.content).toBe("mine");
    expect(h.files.files.get("a.md")).toBe("mine");
  });

  test("nothing is trashed while the vault has damaged notes", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.damaged = 1;
    h.server.notes.delete("a.md");
    await h.engine.check(true);
    expect(h.files.files.get("a.md")).toBe("one");
    expect(h.engine.status().notSynced).toContainEqual(expect.objectContaining({ reason: "damaged-vault" }));
  });

  test("a note that becomes damaged is never trashed locally, by a since or a full check", async () => {
    const h = await harness({ synced: { "a.md": "one", "b.md": "b" } });
    h.server.damage("a.md");
    await h.engine.check(false);
    expect(h.files.files.get("a.md")).toBe("one");
    expect(h.engine.status().notSynced).toContainEqual(expect.objectContaining({ reason: "damaged-vault" }));
    await h.engine.check(true);
    expect(h.files.files.get("a.md")).toBe("one");
    expect(h.files.systemTrash).toEqual([]);
    expect(h.server.count("put")).toBe(0);
  });

  test("a rename uses move with the base", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.files.userRename("a.md", "b.md");
    h.engine.localRenamed("a.md", "b.md");
    await settle(h);
    expect(h.server.calls.map((c) => c.method)).toEqual(["move"]);
    expect(h.server.notes.get("b.md")!.content).toBe("one");
    expect(h.state.state.bases).toEqual({ "b.md": await versionOf("one") });
  });

  test("a rename of an edited file is delete plus create", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.files.userRename("a.md", "b.md");
    h.files.userEdit("b.md", "two");
    h.engine.localRenamed("a.md", "b.md");
    await settle(h, 2000);
    expect(h.server.count("move")).toBe(0);
    expect(h.server.notes.has("a.md")).toBe(false);
    expect(h.server.notes.get("b.md")!.content).toBe("two");
  });

  test("a rename the server refuses falls back to delete plus create and is not read as offline", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.failNext("move", new RejectedError("Names may not contain ':'."));
    h.files.userRename("a.md", "b.md");
    h.engine.localRenamed("a.md", "b.md");
    await settle(h);
    expect(h.engine.status().mode).not.toBe("offline");
    expect(h.server.notes.has("a.md")).toBe(false);
    expect(h.server.notes.get("b.md")!.content).toBe("one");
  });

  test("a delete the server refuses is listed, not retried as offline", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.failNext("remove", new RejectedError("Not allowed."));
    h.files.userDelete("a.md");
    h.engine.localDeleted("a.md");
    await settle(h);
    expect(h.engine.status().mode).not.toBe("offline");
    expect(h.engine.status().notSynced).toContainEqual({ path: "a.md", reason: "rejected", detail: "Not allowed." });
  });

  test("real conflict: server keeps the path, local text becomes a conflict copy", async () => {
    const h = await harness({ synced: { "projects/Plan.md": "one" } });
    await h.server.agentWrite("projects/Plan.md", "agent");
    h.files.userEdit("projects/Plan.md", "mine");
    h.engine.localChanged("projects/Plan.md");
    await settle(h, 2000);
    const copy = "projects/Plan (conflict Laptop 2026-10-27 10-15).md";
    expect(h.files.files.get("projects/Plan.md")).toBe("agent");
    expect(h.server.notes.get("projects/Plan.md")!.content).toBe("agent");
    expect(h.files.files.get(copy)).toContain("> [!warning] Kumbu² conflict");
    expect(h.files.files.get(copy)).toContain("mine");
    expect(h.server.notes.get(copy)!.content).toBe(h.files.files.get(copy));
    expect(h.conflicts).toEqual([["projects/Plan.md", copy]]);
    expect(h.engine.status().conflicts).toEqual([copy]);
  });
});

describe("review focus", () => {
  test("an NFD local path and the NFC remote path are one note", async () => {
    const nfd = "Besprechung März.md";
    const nfc = "Besprechung März.md";
    const h = await harness({ localOnly: { [nfd]: "x" }, remoteOnly: { [nfc]: "x" } });
    expect(h.files.files.size).toBe(1);
    expect(h.server.notes.size).toBe(1);
    expect(h.state.state.bases).toEqual({ [nfc]: await versionOf("x") });
    h.files.userEdit(nfd, "y");
    h.engine.localChanged(nfd);
    await settle(h, 2000);
    expect(h.server.notes.get(nfc)!.content).toBe("y");
    expect(h.server.notes.size).toBe(1);
    expect(copies(h.files.files)).toEqual([]);
  });

  test("paths differing only in case on a case-insensitive file system: the second is not synced and nothing is overwritten", async () => {
    const h = await harness({ caseInsensitive: true, remoteOnly: { "Plan.md": "upper", "plan.md": "lower" } });
    expect([...h.files.files]).toEqual([["Plan.md", "upper"]]);
    expect(h.server.notes.get("Plan.md")!.content).toBe("upper");
    expect(h.server.notes.get("plan.md")!.content).toBe("lower");
    expect(h.engine.status().notSynced).toContainEqual({ path: "plan.md", reason: "case-clash", detail: "Differs only in case from Plan.md." });
    await h.engine.check(true);
    expect(h.server.count("put")).toBe(0);
    expect(h.server.count("remove")).toBe(0);
    expect([...h.files.files]).toEqual([["Plan.md", "upper"]]);
  });

  test("server failures keep the work: 503 then success uploads once, status offline in between", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.failNext("put", new OfflineError("busy", 5000));
    h.files.userEdit("a.md", "two");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    expect(h.engine.status().mode).toBe("offline");
    expect(h.server.notes.get("a.md")!.content).toBe("one");
    await settle(h, 5000);
    expect(h.server.notes.get("a.md")!.content).toBe("two");
    expect(h.server.count("put")).toBe(2);
    expect(h.engine.status().mode).not.toBe("offline");
    expect(copies(h.files.files)).toEqual([]);
  });

  test("a failed versions call ends the check and the next trigger catches up", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    await h.server.agentWrite("a.md", "agent");
    h.server.failNext("versions", new OfflineError("timeout"));
    await h.engine.check(false);
    expect(h.engine.status().mode).toBe("offline");
    await h.engine.check(false);
    expect(h.files.files.get("a.md")).toBe("agent");
  });

  test("a failure in the middle of a check does not advance the cursor", async () => {
    const h = await harness({ synced: { "a.md": "one", "b.md": "one" } });
    const cursor = h.state.state.cursor;
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    await h.server.agentWrite("a.md", "agent a");
    await h.server.agentWrite("b.md", "agent b");
    // Later than the server's 5-minute overlap, so only the old cursor still finds these writes.
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    h.server.failNext("read", new OfflineError("lost signal"));
    await h.engine.check(false);
    expect(h.state.state.cursor).toBe(cursor);
    await h.engine.check(false);
    expect(h.files.files.get("a.md")).toBe("agent a");
    expect(h.files.files.get("b.md")).toBe("agent b");
  });

  test("files changed while closed are uploaded on start without conflicts", async () => {
    const h = await harness({ synced: { "a.md": "one", "b.md": "b" }, start: false });
    h.files.userEdit("a.md", "edited while closed");
    h.files.userEdit("c.md", "created while closed");
    await h.engine.start();
    expect(h.server.notes.get("a.md")!.content).toBe("edited while closed");
    expect(h.server.notes.get("c.md")!.content).toBe("created while closed");
    expect(copies(h.files.files)).toEqual([]);
    expect(h.conflicts).toEqual([]);
  });
});

describe("not synced", () => {
  test("an unexpected local error on one path is listed and the other paths still sync", async () => {
    const h = await harness({ synced: { "a.md": "a", "b.md": "b" } });
    await h.server.agentWrite("a.md", "agent a");
    await h.server.agentWrite("b.md", "agent b");
    const writeIf = h.files.writeIf.bind(h.files);
    let broken = true;
    h.files.writeIf = (path, expected, content) =>
      broken && path === "a.md" ? Promise.reject(new Error("A file is in the way.")) : writeIf(path, expected, content);
    await h.engine.check(false);
    expect(h.files.files.get("b.md")).toBe("agent b");
    expect(h.engine.status().notSynced).toContainEqual({ path: "a.md", reason: "rejected", detail: "Couldn't sync: A file is in the way." });
    broken = false;
    await h.engine.check(false);
    expect(h.files.files.get("a.md")).toBe("agent a");
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("a file over 1 MiB is not synced and not retried", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.files.userEdit("big.md", "x".repeat(1_048_577));
    h.engine.localChanged("big.md");
    await settle(h, 2000);
    await h.engine.check(true);
    expect(h.server.count("put")).toBe(0);
    expect(h.engine.status().notSynced).toContainEqual(expect.objectContaining({ path: "big.md", reason: "too-large" }));
  });

  test("a 400 is shown with the server's sentence and not retried until the file changes", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.failNext("put", new RejectedError("Names may not contain ':'."));
    h.files.userEdit("a.md", "two");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    expect(h.engine.status().notSynced).toContainEqual({ path: "a.md", reason: "rejected", detail: "Names may not contain ':'." });
    await h.engine.check(true);
    expect(h.server.count("put")).toBe(1);
    h.files.userEdit("a.md", "three");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    expect(h.server.count("put")).toBe(2);
    expect(h.engine.status().notSynced.filter((n) => n.reason === "rejected")).toEqual([]);
  });

  test("attachments are listed, not synced, on a server without them", async () => {
    const h = await harness({ synced: { "a.md": "one" }, localOnly: { "img/x.png": "png", ".obsidian/app.json": "{}" }, oldServer: true });
    expect(h.engine.status().notSynced).toEqual([{ path: "img/x.png", reason: "attachment" }]);
    expect(h.server.notes.has("img/x.png")).toBe(false);
  });

  test("a 401 in the middle of an upload means pair again: reported once, and nothing retries", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.failNext("put", new AuthError("revoked"));
    h.files.userEdit("a.md", "two");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    expect(h.authLost).toBe(1);
    expect(h.engine.status().mode).not.toBe("offline");
    h.server.calls.length = 0;
    await settle(h, 30 * 60_000);
    await h.engine.check(false);
    expect(h.server.calls).toEqual([]);
    expect(h.authLost).toBe(1);
  });

  test("401 calls onAuthLost and stops", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.failNext("versions", new AuthError("revoked"));
    await h.engine.check(false);
    expect(h.authLost).toBe(1);
    h.files.userEdit("a.md", "two");
    h.engine.localChanged("a.md");
    await settle(h, 2000);
    await h.engine.check(true);
    expect(h.server.calls.map((c) => c.method)).toEqual(["versions"]);
  });
});

describe("folders a remote change left empty", () => {
  test("a remote folder move removes the old local folder", async () => {
    const h = await harness({ synced: { "Test/a/one.md": "1", "Test/a/two.md": "2", "Test/a/three.md": "3" } });
    for (const name of ["one", "two", "three"]) await h.server.move(`Test/a/${name}.md`, `Test/b/${name}.md`);
    await h.engine.check(false);
    expect([...h.files.files.keys()].sort()).toEqual(["Test/b/one.md", "Test/b/three.md", "Test/b/two.md"]);
    expect(h.files.removedFolders).toEqual(["Test/a"]);
    expect(h.files.folders.has("Test/a")).toBe(false);
  });

  test("a remote delete of the last note removes each folder that is empty afterwards, upwards", async () => {
    const h = await harness({ synced: { "a/b/c/note.md": "n" } });
    await h.server.agentWrite("a/b/c/note.md", null);
    await h.engine.check(false);
    expect(h.files.removedFolders).toEqual(["a/b/c", "a/b", "a"]);
    expect(h.files.folders.size).toBe(0);
  });

  test("a parent that still holds a note stays", async () => {
    const h = await harness({ synced: { "a/b/c/note.md": "n", "a/keep.md": "k" } });
    await h.server.agentWrite("a/b/c/note.md", null);
    await h.engine.check(false);
    expect(h.files.removedFolders).toEqual(["a/b/c", "a/b"]);
    expect(h.files.files.has("a/keep.md")).toBe(true);
  });

  test("a remote delete of one of two notes keeps the folder", async () => {
    const h = await harness({ synced: { "a/one.md": "1", "a/two.md": "2" } });
    await h.server.agentWrite("a/one.md", null);
    await h.engine.check(false);
    expect(h.files.files.has("a/two.md")).toBe(true);
    expect(h.files.removedFolders).toEqual([]);
  });

  test("an empty folder the user made stays, elsewhere and next to the removed one", async () => {
    const h = await harness({ synced: { "a/note.md": "n" } });
    h.files.folders.add("drafts");
    h.files.folders.add("a/empty by hand");
    await h.server.agentWrite("a/note.md", null);
    await h.engine.check(false);
    // The user's folder inside keeps "a" too: it isn't empty.
    expect(h.files.removedFolders).toEqual([]);
    expect(h.files.folders).toEqual(new Set(["a", "drafts", "a/empty by hand"]));
  });

  test("a remote rename within the folder leaves no stray folder and removes none", async () => {
    const h = await harness({ synced: { "a/old.md": "n" } });
    await h.server.move("a/old.md", "a/new.md");
    await h.engine.check(false);
    expect([...h.files.files.keys()]).toEqual(["a/new.md"]);
    expect(h.files.removedFolders).toEqual([]);
  });

  test("a folder that still holds a file that isn't a note stays", async () => {
    const h = await harness({ synced: { "a/note.md": "n" }, localOnly: { "a/picture.png": "png" } });
    await h.server.agentWrite("a/note.md", null);
    await h.engine.check(false);
    expect(h.files.files.has("a/picture.png")).toBe(true);
    expect(h.files.removedFolders).toEqual([]);
  });

  test("a local delete by the user leaves the folder to Obsidian", async () => {
    const h = await harness({ synced: { "a/note.md": "n" } });
    h.files.userDelete("a/note.md");
    h.engine.localDeleted("a/note.md");
    await settle(h);
    expect(h.server.notes.has("a/note.md")).toBe(false);
    expect(h.files.removedFolders).toEqual([]);
  });
});
