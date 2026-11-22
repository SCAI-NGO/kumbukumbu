// SPDX-License-Identifier: AGPL-3.0-or-later
// Shared vaults through the engine: what a refusal does, and conflict copies that name no device.
import { afterEach, describe, expect, test, vi } from "vitest";
import { KumbuApi, type HttpFn } from "../src/api/client";
import { FOLDER_SETTLE_MS, SyncEngine } from "../src/engine/engine";
import { OfflineError, AuthError } from "../src/engine/ports";
import { FakeFiles } from "./fakes/fakeFiles";
import { fakeClock, harness, MemoryState, settle } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

const READ_ONLY = "write: this key can only read vault Work";
const PNG = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const png = (...rest: number[]) => new Uint8Array([...PNG, ...rest]);

describe("a refused change", () => {
  test("a refused edit is listed, and the sync goes on", async () => {
    const h = await harness({ synced: { "plan.md": "one", "b.md": "b" } });
    h.server.refuse = () => READ_ONLY;
    h.files.userEdit("plan.md", "mine");
    h.engine.localChanged("plan.md");
    await settle(h, 2000);
    expect(h.authLost).toBe(0);
    expect(h.engine.status().mode).not.toBe("offline");
    expect(h.engine.status().notSynced).toEqual([{ path: "plan.md", reason: "rejected", detail: READ_ONLY }]);
    expect(h.files.files.get("plan.md")).toBe("mine");
    expect(h.server.notes.get("plan.md")!.content).toBe("one");
    await h.server.agentWrite("b.md", "b2");
    await h.engine.check(false);
    expect(h.files.files.get("b.md")).toBe("b2");
  });

  test("a refused edit is not sent again until the file changes", async () => {
    const h = await harness({ synced: { "plan.md": "one" } });
    h.server.refuse = () => READ_ONLY;
    h.files.userEdit("plan.md", "mine");
    h.engine.localChanged("plan.md");
    await settle(h, 2000);
    await h.engine.check(true);
    expect(h.server.count("put")).toBe(1);
    h.files.userEdit("plan.md", "mine again");
    h.engine.localChanged("plan.md");
    await settle(h, 2000);
    expect(h.server.count("put")).toBe(2);
  });

  test("a refused new note is listed", async () => {
    const h = await harness();
    h.server.refuse = () => READ_ONLY;
    h.files.files.set("new.md", "mine");
    h.engine.localChanged("new.md");
    await settle(h, 2000);
    expect(h.engine.status().notSynced).toEqual([{ path: "new.md", reason: "rejected", detail: READ_ONLY }]);
    expect(h.authLost).toBe(0);
  });

  test("a refused trash is listed and the note stays in Kumbu²", async () => {
    const h = await harness({ synced: { "plan.md": "one" } });
    h.server.refuse = () => "rm: this key can only read vault Work";
    h.files.userDelete("plan.md");
    h.engine.localDeleted("plan.md");
    await settle(h);
    expect(h.authLost).toBe(0);
    expect(h.engine.status().notSynced).toEqual([{ path: "plan.md", reason: "rejected", detail: "rm: this key can only read vault Work" }]);
    expect(h.server.notes.has("plan.md")).toBe(true);
    await h.engine.check(true);
    expect(h.server.count("remove")).toBe(1);
  });

  test("a refused move lists both names and keeps the file", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.refuse = () => READ_ONLY;
    h.files.userRename("a.md", "b.md");
    h.engine.localRenamed("a.md", "b.md");
    await settle(h);
    expect(h.server.count("move")).toBe(1);
    expect(h.authLost).toBe(0);
    expect(h.engine.status().mode).not.toBe("offline");
    expect(h.engine.status().notSynced.map((n) => n.path)).toEqual(["a.md", "b.md"]);
    expect(h.files.files.get("b.md")).toBe("one");
    expect(h.server.notes.get("a.md")!.content).toBe("one");
    expect(h.server.notes.has("b.md")).toBe(false);
  });

  test("a refused attachment upload is listed", async () => {
    const h = await harness({ remoteFiles: { "x.png": png(1) } });
    h.server.refuse = () => "upload: this key can only read vault Work";
    h.files.binaries.set("x.png", png(2));
    h.engine.localChanged("x.png");
    await settle(h, 2000);
    expect(h.authLost).toBe(0);
    expect(h.engine.status().notSynced).toEqual([{ path: "x.png", reason: "rejected", detail: "upload: this key can only read vault Work" }]);
    expect(h.files.binaries.get("x.png")).toEqual(png(2));
    expect(h.server.files.get("x.png")?.bytes).toEqual(png(1));
  });

  test("a refused attachment trash is listed and the file stays in Kumbu²", async () => {
    const h = await harness({ remoteFiles: { "x.png": png(1) } });
    h.server.refuse = () => "rm: this key can only read vault Work";
    h.files.userDelete("x.png");
    h.engine.localDeleted("x.png");
    await settle(h);
    expect(h.authLost).toBe(0);
    expect(h.engine.status().notSynced.map((n) => n.path)).toEqual(["x.png"]);
    expect(h.server.files.has("x.png")).toBe(true);
  });

  test("a device at write can't change the board's file, and the rest still syncs", async () => {
    const h = await harness({ synced: { "Board/Board.md": "board", "plan.md": "one" } });
    const sentence = "write: Only those who may maintain *Work* can change its board's file, Board/Board.md. This key is set to write; it takes a key with maintain.";
    h.server.refuse = (path) => (path === "Board/Board.md" ? sentence : null);
    h.files.userEdit("Board/Board.md", "mine");
    h.engine.localChanged("Board/Board.md");
    h.files.userEdit("plan.md", "two");
    h.engine.localChanged("plan.md");
    await settle(h, 2000);
    expect(h.engine.status().notSynced).toEqual([{ path: "Board/Board.md", reason: "rejected", detail: sentence }]);
    expect(h.server.notes.get("plan.md")!.content).toBe("two");
    expect(h.authLost).toBe(0);
  });

  test("keep local refused in a first sync keeps the text and finishes", async () => {
    const h = await harness({ start: false, localOnly: { "d.md": "mine" }, remoteOnly: { "d.md": "kumbu" } });
    h.server.refuse = () => READ_ONLY;
    const summary = await h.engine.firstSync(() => Promise.resolve("local"));
    expect(summary!.notSynced).toContainEqual({ path: "d.md", reason: "rejected", detail: READ_ONLY });
    expect(h.files.files.get("d.md")).toBe("mine");
    expect(h.authLost).toBe(0);
    // The next check finds no base and decides a conflict: the file becomes Kumbu²'s text, and the chosen text moves
    // to a copy. Nothing is lost, though it isn't quite what "keep local" chose: a refused
    // "keep local" isn't retried as such, and the maintainer decides whether it should be.
    await h.engine.check(true);
    expect([...h.files.files.values()].some((text) => text.includes("mine"))).toBe(true);
    expect(h.authLost).toBe(0);
  });
});

describe("over the real client", () => {
  const sync = async (reply: { status: number; body: unknown }) => {
    const http: HttpFn = (r) => {
      if (r.method === "PUT") return Promise.resolve({ status: reply.status, headers: {}, text: JSON.stringify(reply.body) });
      const body = r.url.endsWith("/api/vaults") ? [] : { full: true, cursor: "c", state: "s", damaged: 0, attachments: true, notes: [] };
      return Promise.resolve({ status: 200, headers: {}, text: JSON.stringify(body) });
    };
    const files = new FakeFiles();
    files.files.set("plan.md", "mine");
    let authLost = 0;
    const engine = new SyncEngine({
      vault: "Work", device: "Laptop", local: files, remote: new KumbuApi(http, "https://k.example", "Work", "kumbu_x"), state: new MemoryState(), clock: fakeClock,
      onStatus: () => undefined, onConflict: () => undefined, onAuthLost: () => { authLost++; },
    });
    await engine.start();
    return { engine, authLost: () => authLost };
  };

  test("a 403 with a code lists the file and keeps the key", async () => {
    const { engine, authLost } = await sync({ status: 403, body: { error: READ_ONLY, code: "read-only" } });
    expect(authLost()).toBe(0);
    expect(engine.status().notSynced).toEqual([{ path: "plan.md", reason: "rejected", detail: READ_ONLY }]);
    await engine.stop();
  });

  test("a 403 without a code stops the sync as a lost key", async () => {
    const { authLost } = await sync({ status: 403, body: { error: "No." } });
    expect(authLost()).toBe(1);
  });
});

describe("a refused folder marker", () => {
  const EMPTY = new Uint8Array(0);

  test("a refused marker doesn't switch markers off", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    const h = await harness({ start: false });
    h.files.binaries.set("A/.vaultkeep", EMPTY);
    h.files.binaries.set("B/.vaultkeep", EMPTY);
    h.server.refuse = () => READ_ONLY;
    await h.engine.start();
    expect(h.server.count("putFile")).toBe(2);
    expect(h.engine.status().notSynced).toEqual([]);
    expect(warn).not.toHaveBeenCalled();
    expect(h.authLost).toBe(0);
    await h.server.seedFile("C/.vaultkeep", EMPTY);
    await h.engine.check(false);
    expect(h.files.binaries.has("C/.vaultkeep")).toBe(true);
    await h.engine.check(true);
    expect(h.server.count("putFile")).toBe(2);
    warn.mockRestore();
  });

  test("a folder made in a read-only vault stays here, unlisted", async () => {
    const h = await harness();
    h.server.refuse = () => READ_ONLY;
    h.files.folders.add("Drafts");
    h.engine.localFolderCreated("Drafts");
    await settle(h, FOLDER_SETTLE_MS + 2000);
    expect(h.files.binaries.has("Drafts/.vaultkeep")).toBe(true);
    expect(h.server.files.has("Drafts/.vaultkeep")).toBe(false);
    expect(h.engine.status().notSynced).toEqual([]);
    expect(h.authLost).toBe(0);
  });
});

describe("rejected meets a remote change", () => {
  const copiesOf = (paths: Iterable<string>) => [...paths].filter((p) => p.includes("(conflict "));

  /** plan.md changed here and refused, as a read-only member's edit is. */
  const refused = async () => {
    const h = await harness({ synced: { "plan.md": "one" } });
    h.server.refuse = () => READ_ONLY;
    h.files.userEdit("plan.md", "mine");
    h.engine.localChanged("plan.md");
    await settle(h, 2000);
    expect(h.engine.status().notSynced.map((n) => n.path)).toEqual(["plan.md"]);
    return h;
  };

  test("a refused edit meets a remote change: the text survives in a copy", async () => {
    const h = await refused();
    await h.server.agentWrite("plan.md", "theirs");
    await h.engine.check(false);
    const [copy, ...more] = copiesOf(h.files.files.keys());
    expect(more).toEqual([]);
    expect(h.files.files.get("plan.md")).toBe("theirs");
    expect(h.files.files.get(copy!)).toContain("mine");
    expect(h.conflicts).toEqual([["plan.md", copy]]);
    expect(h.engine.status().conflicts).toEqual([copy]);
    // The original is settled; the copy is refused in its turn, so it stays here, listed, and reaches nobody.
    expect(h.engine.status().notSynced).toEqual([{ path: copy, reason: "rejected", detail: READ_ONLY }]);
    expect(h.server.notes.has(copy!)).toBe(false);
    expect(h.authLost).toBe(0);
  });

  test("no second copy and no resend after that", async () => {
    const h = await refused();
    await h.server.agentWrite("plan.md", "theirs");
    await h.engine.check(false);
    const puts = h.server.count("put");
    await h.engine.check(true);
    await h.engine.check(false);
    expect(copiesOf(h.files.files.keys())).toHaveLength(1);
    expect(h.server.count("put")).toBe(puts);
    expect(h.files.files.get("plan.md")).toBe("theirs");
  });

  test("an edit typed after the refusal is what the copy keeps", async () => {
    const h = await refused();
    h.files.userEdit("plan.md", "mine, later");
    h.engine.localChanged("plan.md");
    await h.server.agentWrite("plan.md", "theirs");
    await h.engine.check(false);
    await settle(h, 2000);
    const [copy, ...more] = copiesOf(h.files.files.keys());
    expect(more).toEqual([]);
    expect(h.files.files.get(copy!)).toContain("mine, later");
    expect(h.files.files.get("plan.md")).toBe("theirs");
  });

  test("the same text arriving from Kumbu² settles it without a copy", async () => {
    const h = await refused();
    await h.server.agentWrite("plan.md", "mine");
    await h.engine.check(false);
    expect(copiesOf(h.files.files.keys())).toEqual([]);
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("the note deleted in Kumbu² keeps the refused text here", async () => {
    const h = await refused();
    await h.server.agentWrite("plan.md", null);
    await h.engine.check(false);
    expect(h.files.files.get("plan.md")).toBe("mine");
    expect(h.files.trash).toEqual([]);
    expect(h.server.count("put")).toBe(1);
    expect(h.engine.status().notSynced.map((n) => n.path)).toEqual(["plan.md"]);
  });

  test("a refused new note meets a note of that name in Kumbu²: the text survives in a copy", async () => {
    const h = await harness();
    h.server.refuse = () => READ_ONLY;
    h.files.files.set("new.md", "mine");
    h.engine.localChanged("new.md");
    await settle(h, 2000);
    await h.server.agentWrite("new.md", "theirs");
    await h.engine.check(false);
    const [copy, ...more] = copiesOf(h.files.files.keys());
    expect(more).toEqual([]);
    expect(h.files.files.get("new.md")).toBe("theirs");
    expect(h.files.files.get(copy!)).toContain("mine");
  });

  test("a refused trash meets a remote change: the server's version comes back", async () => {
    const h = await harness({ synced: { "plan.md": "one" } });
    h.server.refuse = () => READ_ONLY;
    h.files.userDelete("plan.md");
    h.engine.localDeleted("plan.md");
    await settle(h);
    await h.server.agentWrite("plan.md", "theirs");
    await h.engine.check(false);
    expect(h.files.files.get("plan.md")).toBe("theirs");
    expect(h.engine.status().notSynced).toEqual([]);
    expect(copiesOf(h.files.files.keys())).toEqual([]);
  });

  test("a refused attachment meets a remote change: its bytes survive in a copy", async () => {
    const h = await harness({ remoteFiles: { "x.png": png(1) } });
    h.server.refuse = () => READ_ONLY;
    h.files.binaries.set("x.png", png(2));
    h.engine.localChanged("x.png");
    await settle(h, 2000);
    await h.server.seedFile("x.png", png(3));
    await h.engine.check(false);
    const [copy, ...more] = copiesOf(h.files.binaries.keys());
    expect(more).toEqual([]);
    expect(h.files.binaries.get("x.png")).toEqual(png(3));
    expect(h.files.binaries.get(copy!)).toEqual(png(2));
    expect(h.server.files.has(copy!)).toBe(false);
    expect(h.engine.status().notSynced.map((n) => n.path)).toEqual([copy]);
  });

  test("a refused edit taken back leaves the list", async () => {
    const h = await refused();
    h.files.userEdit("plan.md", "one");
    h.engine.localChanged("plan.md");
    await settle(h, 2000);
    expect(h.engine.status().notSynced).toEqual([]);
    expect(h.server.count("put")).toBe(1);
  });

  test("a refused new note deleted leaves the list", async () => {
    const h = await harness();
    h.server.refuse = () => READ_ONLY;
    h.files.files.set("new.md", "mine");
    h.engine.localChanged("new.md");
    await settle(h, 2000);
    h.files.userDelete("new.md");
    h.engine.localDeleted("new.md");
    await settle(h);
    expect(h.engine.status().notSynced).toEqual([]);
  });

  test("a refused trash taken back leaves the list", async () => {
    const h = await harness({ synced: { "plan.md": "one" } });
    h.server.refuse = () => READ_ONLY;
    h.files.userDelete("plan.md");
    h.engine.localDeleted("plan.md");
    await settle(h);
    h.files.files.set("plan.md", "one");
    h.engine.localChanged("plan.md");
    await settle(h, 2000);
    expect(h.engine.status().notSynced).toEqual([]);
  });
});

describe("open conflict copies", () => {
  test("copies with and without a device are recognised after a restart", async () => {
    const h = await harness({ start: false, synced: {
      "Plan (conflict 2026-10-27 10-15).md": "a",
      "Plan (conflict 2026-10-27 10-15) 2.md": "b",
      "Old (conflict Laptop 2026-10-27 10-15).md": "c",
      "Not a copy (conflict) 2026-10-27 10-15.md": "d",
    } });
    h.files.binaries.set("shot (conflict 2026-10-27 10-15).png", png(1));
    await h.engine.start();
    expect(h.engine.status().conflicts).toEqual([
      "Old (conflict Laptop 2026-10-27 10-15).md",
      "Plan (conflict 2026-10-27 10-15) 2.md",
      "Plan (conflict 2026-10-27 10-15).md",
      "shot (conflict 2026-10-27 10-15).png",
    ]);
  });
});

describe("conflict copies in a shared vault", () => {
  const COPY = "projects/Plan (conflict 2026-10-27 10-15).md";

  /** projects/Plan.md changed on both sides; the local edit's upload finds out. */
  const bothChanged = async (h: Awaited<ReturnType<typeof harness>>) => {
    await h.server.agentWrite("projects/Plan.md", "agent");
    h.files.userEdit("projects/Plan.md", "mine");
    h.engine.localChanged("projects/Plan.md");
    await settle(h, 2000);
  };

  test("in a shared vault the copy names no device, in its name or its text", async () => {
    const h = await harness({ synced: { "projects/Plan.md": "one" } });
    h.server.listing = true;
    await bothChanged(h);
    expect(h.files.files.get(COPY)).toContain("> Change from **this device** at");
    expect(h.files.files.get(COPY)).toContain("mine");
    expect(h.files.files.get(COPY)).not.toContain("Laptop");
    expect(h.server.notes.get(COPY)!.content).toBe(h.files.files.get(COPY));
    expect([...h.files.files.keys(), ...h.server.notes.keys()].join()).not.toContain("Laptop");
    expect(h.conflicts).toEqual([["projects/Plan.md", COPY]]);
    expect(h.engine.status().conflicts).toEqual([COPY]);
  });

  test("an attachment's copy in a shared vault names no device", async () => {
    const h = await harness({ remoteFiles: { "x.png": png(1) } });
    h.server.listing = true;
    await h.server.seedFile("x.png", png(2));
    h.files.binaries.set("x.png", png(3));
    h.engine.localChanged("x.png");
    await settle(h, 2000);
    expect(h.files.binaries.get("x (conflict 2026-10-27 10-15).png")).toEqual(png(3));
  });

  test("the device's own vault that isn't shared: nothing changes", async () => {
    const h = await harness({ synced: { "projects/Plan.md": "one" } });
    await bothChanged(h);
    const copy = "projects/Plan (conflict Laptop 2026-10-27 10-15).md";
    expect(h.files.files.get(copy)).toContain("> Change from **Laptop** at");
  });

  test.each<[string, boolean | "unlisted" | "absent" | Error]>([
    ["the list fails offline", new OfflineError("down")],
    ["the list says the key is lost", new AuthError("revoked")],
    ["the list answers something odd", new TypeError("v.map is not a function")],
    ["the vault isn't listed", "unlisted"],
    ["the server says nothing of sharing", "absent"],
  ])("%s: it fails closed", async (_, listing) => {
    const h = await harness({ synced: { "projects/Plan.md": "one" } });
    expect(h.engine.copyDevice).toBe("Laptop");
    h.server.listing = listing;
    await bothChanged(h);
    expect(h.files.files.has(COPY)).toBe(true);
    expect(h.engine.copyDevice).toBeNull();
    expect(h.authLost).toBe(0);
    expect(h.engine.status().mode).not.toBe("offline");
  });

  test("before the server has said anything, the vault counts as shared", async () => {
    const h = await harness({ start: false });
    expect(h.engine.copyDevice).toBeNull();
  });

  test("a check asks, whether or not it finds a conflict", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.vaultsAsked = 0;
    await h.engine.check(false);
    expect(h.server.vaultsAsked).toBe(1);
    await h.engine.check(true);
    expect(h.server.vaultsAsked).toBe(2);
  });

  test("a vault shared later, or no longer shared, is seen by the next check", async () => {
    const h = await harness({ synced: { "a.md": "one" } });
    h.server.listing = true;
    await h.engine.check(false);
    expect(h.engine.copyDevice).toBeNull();
    h.server.listing = false;
    await h.engine.check(false);
    expect(h.engine.copyDevice).toBe("Laptop");
  });

  test("a vault shared since the last check is seen when the copy is made", async () => {
    const h = await harness({ synced: { "projects/Plan.md": "one" } });
    expect(h.engine.copyDevice).toBe("Laptop");
    h.server.listing = true;
    await bothChanged(h);
    expect(h.files.files.has(COPY)).toBe(true);
  });

  test("an attachment in a vault shared since the last check", async () => {
    const h = await harness({ remoteFiles: { "x.png": png(1) } });
    expect(h.engine.copyDevice).toBe("Laptop");
    h.server.listing = true;
    await h.server.seedFile("x.png", png(2));
    h.files.binaries.set("x.png", png(3));
    h.engine.localChanged("x.png");
    await settle(h, 2000);
    expect(h.files.binaries.has("x (conflict 2026-10-27 10-15).png")).toBe(true);
  });

  test("a first sync asks first, so its question and its copies name no device", async () => {
    const h = await harness({ start: false, localOnly: { "d.md": "mine" }, remoteOnly: { "d.md": "kumbu" } });
    h.server.listing = true;
    const asked: (string | null)[] = [];
    await h.engine.firstSync(() => { asked.push(h.engine.copyDevice); return Promise.resolve("both"); });
    expect(asked).toEqual([null]);
    expect(h.files.files.get("d (conflict 2026-10-27 10-15).md")).toContain("mine");
  });

  test("a first sync in an own vault still names the device", async () => {
    const h = await harness({ start: false, localOnly: { "d.md": "mine" }, remoteOnly: { "d.md": "kumbu" } });
    const asked: (string | null)[] = [];
    await h.engine.firstSync(() => { asked.push(h.engine.copyDevice); return Promise.resolve("both"); });
    expect(asked).toEqual(["Laptop"]);
    expect(h.files.files.has("d (conflict Laptop 2026-10-27 10-15).md")).toBe(true);
  });

  test("two copies in one minute both name no device and both count", async () => {
    const h = await harness({ synced: { "projects/Plan.md": "one" } });
    h.server.listing = true;
    await bothChanged(h);
    await h.server.agentWrite("projects/Plan.md", "agent again");
    h.files.userEdit("projects/Plan.md", "mine again");
    h.engine.localChanged("projects/Plan.md");
    await settle(h, 2000);
    expect(h.engine.status().conflicts).toEqual(["projects/Plan (conflict 2026-10-27 10-15) 2.md", COPY]);
  });
});

describe("a change from my other device", () => {
  const conflictWithPhone = async (listing: boolean) => {
    const h = await harness({ synced: { "plan.md": "one" } });
    h.server.listing = listing;
    await h.server.seed("plan.md", "from the phone", "Work – Obsidian Phone");
    h.files.userEdit("plan.md", "mine");
    h.engine.localChanged("plan.md");
    await settle(h, 2000);
    return h;
  };

  test("a change from my other device names neither device in a shared vault", async () => {
    const h = await conflictWithPhone(true);
    const text = h.files.files.get("plan (conflict 2026-10-27 10-15).md")!;
    expect(text).toContain("mine");
    expect(text).not.toContain("Phone");
    expect(text).not.toContain("Laptop");
    expect(text).toContain("> The other change, at 10:15, is in [[plan|plan]].");
  });

  test("in an own vault that isn't shared it names them as ever", async () => {
    const h = await conflictWithPhone(false);
    expect(h.files.files.get("plan (conflict Laptop 2026-10-27 10-15).md")).toContain("> The change from **Work – Obsidian Phone** at 10:15");
  });
});

test("the spec's worked example", async () => {
  const sentence = "write: your role in vault Work is read, and a key never acts above your role; only the owner can change it";
  const h = await harness({ synced: { "plan.md": "one", "other.md": "x" } });
  h.server.listing = true;

  // Bob reads only: his edit is refused, listed, and everything else goes on.
  h.server.refuse = () => sentence;
  h.files.userEdit("plan.md", "bob");
  h.engine.localChanged("plan.md");
  await settle(h, 2000);
  expect(h.engine.status().notSynced).toEqual([{ path: "plan.md", reason: "rejected", detail: sentence }]);
  expect(h.authLost).toBe(0);
  await h.server.agentWrite("other.md", "y");
  await h.engine.check(false);
  expect(h.files.files.get("other.md")).toBe("y");

  // Alice raises him to write, and they edit plan.md at once.
  h.server.refuse = null;
  await h.server.agentWrite("plan.md", "alice");
  h.files.userEdit("plan.md", "bob again");
  h.engine.localChanged("plan.md");
  await settle(h, 2000);
  const copy = "plan (conflict 2026-10-27 10-15).md";
  expect(h.files.files.get("plan.md")).toBe("alice");
  expect(h.files.files.get(copy)).toContain("Change from **this device**");
  expect(h.files.files.get(copy)).toContain("bob again");
  expect(h.files.files.get(copy)).not.toContain("Laptop");
  expect(h.server.notes.get(copy)!.content).toBe(h.files.files.get(copy));
  expect(h.engine.status().notSynced).toEqual([]);
});
