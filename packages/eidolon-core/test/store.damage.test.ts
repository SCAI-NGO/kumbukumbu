// SPDX-License-Identifier: AGPL-3.0-or-later
// The copy against what can happen to the browser's storage: records removed or changed by someone without the key,
// leftovers of an earlier mirror of a vault, a header that doesn't parse. None of it may trash anything in Kumbu².
import { describe, expect, test, vi } from "vitest";
import type { ListedVault } from "../src/engine/ports";
import { MirrorEngines } from "../src/store/engines";
import { IDLE_MS, MirrorCopies, type SealedMirror } from "../src/store/mirror";
import { FakeServer } from "./fakes/fakeServer";
import { delivered, FakeBrowser, realClock } from "./fakeStore";

const work = { id: "vault-1f3a", tag: "tag-a", canWrite: true, pending: false };
const signIn = { userId: "user-8d1e", username: "Ada", password: "correct horse battery", label: "Firefox on Linux", idleMs: IDLE_MS, iterations: 1000 };
const png = (size: number, fill = 0) => { const b = new Uint8Array(size).fill(fill); b.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]); return b; };
const bytes = (text: string) => new TextEncoder().encode(text);

async function setup() {
  const browser = new FakeBrowser();
  browser.slots.vaults = [work];
  const copies = new MirrorCopies(browser.tab());
  const mirror = await copies.create(signIn);
  const server = new FakeServer();
  const vaults: ListedVault[] = [{ id: work.id, name: "Work", canWrite: true }];
  const enginesOf = (m: SealedMirror) => new MirrorEngines({ mirror: m, device: "Firefox on Linux", clock: realClock, listVaults: () => Promise.resolve(vaults), remote: () => server });
  return { browser, copies, mirror, server, enginesOf };
}

const heldPaths = (m: SealedMirror) => m.vault(work.id)?.list().filter((e) => e.local).map((e) => e.path).sort() ?? null;
const removals = (server: FakeServer) => server.calls.filter((c) => c.method === "remove" || c.method === "removeFile").map((c) => String(c.args[0]));
const keysWith = (browser: FakeBrowser, part: string) => [...browser.storage.records.keys()].filter((k) => k.includes(part)).sort();
const reload = async (browser: FakeBrowser, copies: MirrorCopies) =>
  (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;

/** Runs a copy's engines until they have nothing left to do. */
async function runOnce(engines: MirrorEngines) {
  await engines.start();
  await engines.refresh(true);
  await new Promise((r) => setTimeout(r, 100));
  await engines.stop();
}

/** Six notes and a picture, all here, the copy locked: the device left alone. */
async function synced() {
  const s = await setup();
  for (const path of ["a.md", "b.md", "c.md", "d.md", "e.md", "f.md"]) await s.server.seed(path, `text of ${path}`);
  await s.server.seedFile("pic.png", png(3000, 5));
  const engines = s.enginesOf(s.mirror);
  await engines.start();
  await vi.waitFor(() => expect(heldPaths(s.mirror)?.length).toBe(7), { timeout: 5000 });
  await engines.stop();
  s.mirror.lock();
  return s;
}

describe("records removed or changed on the device, without the key", () => {
  test("a removed body, a removed entry, a changed body and a changed entry are damage: nothing is removed in Kumbu²", async () => {
    const { browser, copies, server, enginesOf } = await synced();
    const records = browser.storage.records;
    // The names are opaque, but an entry and the body of one path share theirs.
    const names = keysWith(browser, "/b/").map((k) => k.slice(k.lastIndexOf("/") + 1));
    const prefix = keysWith(browser, "/b/")[0]!.replace(/b\/[^/]+$/, "");
    const flip = (key: string) => { const v = records.get(key)!.slice(); v[v.length - 1]! ^= 1; records.set(key, v); };
    records.delete(`${prefix}b/${names[0]}`);
    records.delete(`${prefix}e/${names[1]}`);
    flip(`${prefix}b/${names[2]}`);
    flip(`${prefix}e/${names[3]}`);

    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    await runOnce(enginesOf(await reload(browser, copies)));
    warn.mockRestore();
    expect(removals(server)).toEqual([]);
    expect(server.notes.size + server.files.size).toBe(7);
  });

  test("every body removed: every note and file stays in Kumbu²", async () => {
    const { browser, copies, server, enginesOf } = await synced();
    for (const key of keysWith(browser, "/b/")) browser.storage.records.delete(key);
    await runOnce(enginesOf(await reload(browser, copies)));
    expect(removals(server)).toEqual([]);
    expect(server.notes.size + server.files.size).toBe(7);
  });

  test("a note deleted on the device is still deleted in Kumbu²", async () => {
    const { browser, copies, server, enginesOf } = await synced();
    const mirror = await reload(browser, copies);
    await mirror.vault(work.id)!.files().trashToLocal("a.md");
    mirror.lock();
    await runOnce(enginesOf(await reload(browser, copies)));
    expect(removals(server)).toEqual(["a.md"]);
  });
});

describe("a vault's mirror purged under a write", () => {
  test("a write waiting for the copy's lock is refused once its vault is purged, and leaves nothing behind", async () => {
    const { browser, mirror, server, enginesOf } = await setup();
    await server.seed("a.md", "one");
    const engines = enginesOf(mirror);
    await engines.start();
    await vi.waitFor(() => expect(heldPaths(mirror)).toEqual(["a.md"]));
    await engines.stop();
    const vault = mirror.vault(work.id)!;
    const files = vault.files();

    let release!: () => void;
    const holder = browser.locks.run(`kumbu-mirror:${mirror.header.copy}`, () => new Promise<void>((r) => { release = r; }));
    const purge = mirror.purge(work.id);
    const late = files.writeIf("late.md", null, "typed while the purge waited").then(() => "written", () => "refused");
    await delivered();
    release();
    await holder;
    await purge;
    expect(await late).toBe("refused");
    await expect(vault.stateStore().save({ bases: {}, cursor: null, lastFullAt: null, lastSyncAt: null })).rejects.toThrow();
    await expect(vault.setMeta({ canWrite: false })).rejects.toThrow();
    expect(keysWith(browser, "/v/")).toEqual([]);
    expect(keysWith(browser, "/m/")).toEqual([]);
  });

  test("a vault taken in again starts clean, whatever an earlier mirror of it left in storage", async () => {
    const { browser, copies, mirror, server, enginesOf } = await setup();
    await server.seed("a.md", "one");
    await server.seed("b.md", "two");
    const engines = enginesOf(mirror);
    await engines.start();
    await vi.waitFor(() => expect(heldPaths(mirror)).toEqual(["a.md", "b.md"]));
    await engines.stop();
    // Leftovers as a copy from before this rule could hold them: the vault's records, without its meta.
    const leftovers = [...browser.storage.records].filter(([k]) => k.includes("/v/"));
    await mirror.purge(work.id);
    for (const [k, v] of leftovers) browser.storage.records.set(k, v);
    mirror.lock();

    browser.slots.vaults = [{ ...work, tag: "tag-new" }];
    const again = await reload(browser, copies);
    await runOnce(enginesOf(again));
    expect(removals(server)).toEqual([]);
    expect([...server.notes.keys()].sort()).toEqual(["a.md", "b.md"]);
  });
});

describe("a header that doesn't parse", () => {
  test.each([["not JSON", "{"], ["JSON null", "null"], ["empty", ""]])("%s is passed over, and offline can be turned on again with one slot", async (_name, text) => {
    const { browser, copies } = await setup();
    browser.storage.records.set("h/zzzz", bytes(text));
    expect(await copies.forUser(signIn.userId)).not.toBeNull();
    expect(await copies.forUsername("someone else")).toBeNull();
    await copies.create(signIn);
    expect(browser.slots.slots.size).toBe(2);
  });
});

describe("older records put back in their place", () => {
  /** a.md synced at "the old text", the records of that moment kept aside, then a newer text synced over it. */
  async function replayable() {
    const s = await setup();
    await s.server.seed("a.md", "the old text");
    await s.server.seed("b.md", "to be deleted here");
    let engines = s.enginesOf(s.mirror);
    await engines.start();
    await vi.waitFor(() => expect(heldPaths(s.mirror)).toEqual(["a.md", "b.md"]));
    await engines.stop();
    const at = (part: string) => new Map([...s.browser.storage.records].filter(([k]) => k.includes(part)).map(([k, v]) => [k, v.slice()]));
    const old = { entries: at("/e/"), bodies: at("/b/"), state: at("/v/") };

    await s.server.agentWrite("a.md", "the new text, written elsewhere");
    engines = s.enginesOf(s.mirror);
    await engines.start();
    await vi.waitFor(async () => expect(new TextDecoder().decode((await s.mirror.vault(work.id)!.bytes("a.md"))!)).toBe("the new text, written elsewhere"));
    await engines.stop();
    s.mirror.lock();
    const puts = () => s.server.count("put");
    return { ...s, old, puts, before: puts() };
  }

  const putBack = (browser: FakeBrowser, records: Map<string, Uint8Array<ArrayBuffer>>) => { for (const [k, v] of records) browser.storage.records.set(k, v); };

  test("an older entry and body: the old text is never uploaded over the new", async () => {
    const { browser, copies, server, enginesOf, old, puts, before } = await replayable();
    putBack(browser, old.entries);
    putBack(browser, old.bodies);
    await runOnce(enginesOf(await reload(browser, copies)));
    expect(server.notes.get("a.md")?.content).toBe("the new text, written elsewhere");
    expect(puts()).toBe(before);
  });

  test("an older body under the current entry: not uploaded either", async () => {
    const { browser, copies, server, enginesOf, old, puts, before } = await replayable();
    putBack(browser, old.bodies);
    await runOnce(enginesOf(await reload(browser, copies)));
    expect(server.notes.get("a.md")?.content).toBe("the new text, written elsewhere");
    expect(puts()).toBe(before);
  });

  test("an older deletion made here, put back over a note that is here again: nothing is removed", async () => {
    const { browser, copies, server, enginesOf } = await synced();
    let mirror = await reload(browser, copies);
    await mirror.vault(work.id)!.files().trashToLocal("a.md");
    const deleted = new Map([...browser.storage.records].filter(([k]) => k.includes("/e/")).map(([k, v]) => [k, v.slice()]));
    // Not uploaded: the copy is opened again, the deletion is undone by writing the note back as it was.
    await mirror.vault(work.id)!.files().writeIf("a.md", null, "text of a.md");
    mirror.lock();
    putBack(browser, deleted);
    mirror = await reload(browser, copies);
    await runOnce(enginesOf(mirror));
    expect(removals(server)).toEqual([]);
  });

  test("the whole vault put back as it was: the newer text comes down, nothing old goes up", async () => {
    const { browser, copies, server, enginesOf, old, puts, before } = await replayable();
    putBack(browser, old.state);
    const mirror = await reload(browser, copies);
    await runOnce(enginesOf(mirror));
    expect(server.notes.get("a.md")?.content).toBe("the new text, written elsewhere");
    expect(puts()).toBe(before);
    expect(new TextDecoder().decode((await mirror.vault(work.id)!.bytes("a.md"))!)).toBe("the new text, written elsewhere");
  });

  test("a change made here and not uploaded yet still goes up after a reload", async () => {
    const { browser, copies, server, enginesOf } = await synced();
    let mirror = await reload(browser, copies);
    const files = mirror.vault(work.id)!.files();
    await files.writeIf("a.md", mirror.vault(work.id)!.entry("a.md")!.local!.version, "edited offline");
    mirror.lock();
    mirror = await reload(browser, copies);
    await runOnce(enginesOf(mirror));
    expect(server.notes.get("a.md")?.content).toBe("edited offline");
  });
});
