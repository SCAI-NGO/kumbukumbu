// SPDX-License-Identifier: AGPL-3.0-or-later
// What a device keeps offline (offline mode design §6): the vault choice, Keep offline, attachments opened online
// within the budget, and the choice sealed and shared by the copy's tabs.
import { describe, expect, test, vi } from "vitest";
import { SyncEngine } from "../src/engine/engine";
import { DEFAULT_CHOICE, evictions, forget, isKept, mirrors, wanted, type OfflineChoice } from "../src/store/choice";
import { IDLE_MS, MirrorCopies, type SealedMirror } from "../src/store/mirror";
import { SMALL_ATTACHMENT_BYTES, type VaultMirror } from "../src/store/vault";
import { FakeServer } from "./fakes/fakeServer";
import { delivered, FakeBrowser, realClock } from "./fakeStore";

const work = { id: "vault-1f3a", tag: "tag-a", canWrite: true, pending: false };
const team = { id: "vault-77c2", tag: "tag-b", canWrite: true, pending: false };
const signIn = { userId: "user-8d1e", username: "Ada", password: "correct horse battery", label: "Firefox on Linux", idleMs: IDLE_MS, iterations: 1000 };
const MB = 1_048_576;
const png = (size: number, fill = 0) => { const b = new Uint8Array(size).fill(fill); b.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]); return b; };

async function setup() {
  const browser = new FakeBrowser();
  browser.slots.vaults = [work, team];
  const copies = new MirrorCopies(browser.tab());
  const mirror = await copies.create(signIn);
  return { browser, copies, mirror };
}

/** An engine as the web app's host makes one: the download order with the device's choice. */
function engineFor(mirror: SealedMirror, vault: VaultMirror, server: FakeServer) {
  const engine: SyncEngine = new SyncEngine({
    vault: "Work", device: "Firefox on Linux", local: vault.files(), remote: vault.inventory(server), state: vault.stateStore(), clock: realClock,
    onStatus: () => {}, onConflict: () => {}, onAuthLost: () => {},
    fetchPolicy: vault.downloadOrder(() => void engine.check(true), (path) => wanted(mirror.choice, vault, path)),
  });
  return engine;
}

async function synced(engine: SyncEngine) {
  await engine.start();
  await engine.idle();
  await engine.check(true);
  await engine.idle();
}

describe("the choice", () => {
  test("mirrors every vault with All my vaults, else the ones chosen", () => {
    expect(mirrors(DEFAULT_CHOICE, "any")).toBe(true);
    const some: OfflineChoice = { ...DEFAULT_CHOICE, all: false, vaults: ["a"] };
    expect(mirrors(some, "a")).toBe(true);
    expect(mirrors(some, "b")).toBe(false);
  });

  test("keeps a folder's paths below it, not a sibling of a similar name; '' keeps the whole vault", () => {
    const c: OfflineChoice = { ...DEFAULT_CHOICE, kept: { v: ["Scans"], w: [""] } };
    expect(isKept(c, "v", "Scans/a.pdf")).toBe(true);
    expect(isKept(c, "v", "Scans/2026/a.pdf")).toBe(true);
    expect(isKept(c, "v", "Scans old/a.pdf")).toBe(false);
    expect(isKept(c, "v", "Scans")).toBe(false);
    expect(isKept(c, "w", "deep/x.png")).toBe(true);
    expect(isKept(c, "x", "Scans/a.pdf")).toBe(false);
  });

  test("is sealed in the copy, kept through a lock, and seen by the copy's other tabs", async () => {
    const { browser, copies, mirror } = await setup();
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    const heard = vi.fn();
    other.onChoice = heard;
    await mirror.choose((c) => ({ ...c, all: false, vaults: [work.id], kept: { [work.id]: ["Secret scans"] } }));
    await delivered();
    expect(heard).toHaveBeenCalled();
    expect(other.choice.kept).toEqual({ [work.id]: ["Secret scans"] });
    expect(browser.storage.dump()).not.toContain("Secret scans");
    expect(browser.storage.dump()).not.toContain(work.id);

    mirror.lock();
    const again = (await copies.unlockOffline((await copies.forUser(signIn.userId))!, signIn.password))!;
    expect(again.choice).toMatchObject({ all: false, vaults: [work.id] });
  });
});

describe("the attachments the engine downloads", () => {
  test("are the small ones after the notes, a large one only when kept offline or opened online", async () => {
    const { mirror } = await setup();
    const server = new FakeServer();
    await server.seed("a.md", "alpha");
    await server.seedFile("small.png", png(100));
    await server.seedFile("Scans/big.pdf", new Uint8Array(2 * MB));
    await server.seedFile("photos/big.png", png(2 * MB));
    await server.seedFile("photos/other.png", png(2 * MB));
    await mirror.choose((c) => ({ ...c, kept: { [work.id]: ["Scans"] } }));
    const vault = await mirror.adopt(work.id);
    const engine = engineFor(mirror, vault, server);
    await synced(engine);
    const held = () => vault.list().filter((e) => e.local).map((e) => e.path).sort();
    expect(held()).toEqual(["Scans/big.pdf", "a.md", "small.png"]);

    await mirror.choose((c) => ({ ...c, opened: { [work.id]: { "photos/big.png": 1 } } }));
    await engine.check(true);
    await engine.idle();
    expect(held()).toEqual(["Scans/big.pdf", "a.md", "photos/big.png", "small.png"]);
    await engine.stop();
  });
});

describe("the budget", () => {
  /** A vault with these large attachments held, opened at the times given. */
  async function holding(files: Record<string, number>, choice: Partial<OfflineChoice> = {}) {
    const { browser, mirror } = await setup();
    const server = new FakeServer();
    for (const path of Object.keys(files)) await server.seedFile(path, png(2 * MB, path.length));
    await server.seedFile("small.png", png(100));
    await mirror.choose((c) => ({ ...c, ...choice, opened: { [work.id]: files } }));
    const vault = await mirror.adopt(work.id);
    const engine = engineFor(mirror, vault, server);
    await synced(engine);
    return { browser, mirror, server, vault, engine };
  }

  test("lets the least recently opened go first, until the rest fits; small and kept ones never", async () => {
    const { mirror, vault, engine } = await holding({ "old.png": 1, "mid.png": 2, "new.png": 3, "Keep/k.png": 0 }, { budget: 5 * MB, kept: { [work.id]: ["Keep"] } });
    await engine.stop();
    expect(evictions(mirror.choice, [vault])).toEqual(new Map([[work.id, ["old.png"]]]));
    expect(evictions({ ...mirror.choice, budget: 2 * MB }, [vault])).toEqual(new Map([[work.id, ["old.png", "mid.png"]]]));
    expect(evictions({ ...mirror.choice, budget: 6 * MB }, [vault])).toEqual(new Map());
  });

  test("never lets a file go that changed here and hasn't uploaded", async () => {
    const { mirror, vault, engine, server } = await holding({ "old.png": 1, "new.png": 2 }, { budget: 2 * MB });
    await engine.stop();
    await vault.files().writeBinaryIf("old.png", vault.entry("old.png")!.local!.version, png(2 * MB, 9).buffer);
    expect(evictions(mirror.choice, [vault])).toEqual(new Map([[work.id, ["new.png"]]]));
    // Even when asked to: the change stays.
    await vault.evict(["old.png"]);
    expect(vault.entry("old.png")?.local).toBeDefined();
    expect(server.count("removeFile")).toBe(0);
  });

  test("an evicted file is as one never downloaded: nothing waits, nothing is deleted, and opening it fetches it again", async () => {
    const { mirror, vault, engine, server } = await holding({ "old.png": 1, "new.png": 2 }, { budget: 2 * MB });
    await engine.stop();
    const gone = evictions(mirror.choice, [vault]);
    await vault.evict(gone.get(work.id)!);
    await mirror.choose((c) => forget(c, gone));
    expect(vault.entry("old.png")).toMatchObject({ remote: { kind: "image" } });
    expect(vault.entry("old.png")?.local).toBeUndefined();
    expect(await vault.bytes("old.png")).toBeNull();
    expect(vault.waiting()).toBe(0);

    await engine.start();
    await engine.idle();
    expect(server.count("removeFile")).toBe(0);
    expect(server.files.has("old.png")).toBe(true);
    expect(vault.entry("old.png")?.local).toBeUndefined();

    await mirror.choose((c) => ({ ...c, opened: { [work.id]: { ...c.opened[work.id], "old.png": 9 } } }));
    await engine.check(true);
    await engine.idle();
    expect((await vault.bytes("old.png"))?.byteLength).toBe(2 * MB);
    expect(vault.waiting()).toBe(0);
    await engine.stop();
  });

  test("counts the small attachments as used but never lets them go", async () => {
    const { mirror, vault, engine } = await holding({}, { budget: 0 });
    await engine.stop();
    expect(vault.entry("small.png")?.local?.size).toBeLessThanOrEqual(SMALL_ATTACHMENT_BYTES);
    expect(evictions(mirror.choice, [vault])).toEqual(new Map());
  });
});
