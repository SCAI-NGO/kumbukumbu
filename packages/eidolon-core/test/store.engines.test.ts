// SPDX-License-Identifier: AGPL-3.0-or-later
// The engines of an unlocked copy (offline mode design §3, §6): the vaults chosen, attachments opened online within
// the budget, and the copy's other tabs' writes.
import { describe, expect, test, vi } from "vitest";
import { OfflineError, type ListedVault } from "../src/engine/ports";
import { MirrorEngines } from "../src/store/engines";
import { IDLE_MS, MirrorCopies, type SealedMirror } from "../src/store/mirror";
import { FakeServer } from "./fakes/fakeServer";
import { delivered, FakeBrowser, realClock } from "./fakeStore";

const work = { id: "vault-1f3a", tag: "tag-a", canWrite: true, pending: false };
const team = { id: "vault-77c2", tag: "tag-b", canWrite: true, pending: false };
const signIn = { userId: "user-8d1e", username: "Ada", password: "correct horse battery", label: "Firefox on Linux", idleMs: IDLE_MS };
const MB = 1_048_576;
const png = (size: number, fill = 0) => { const b = new Uint8Array(size).fill(fill); b.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]); return b; };

async function setup(listedByContact = [work, team]) {
  const browser = new FakeBrowser();
  browser.slots.vaults = listedByContact;
  const copies = new MirrorCopies(browser.tab());
  const mirror = await copies.create(signIn);
  const servers = new Map([["Work", new FakeServer()], ["Team", new FakeServer()]]);
  const vaults: ListedVault[] = [{ id: work.id, name: "Work", canWrite: true }, { id: team.id, name: "Team", canWrite: true }];
  const adopted: string[] = [];
  const engines = new MirrorEngines({
    mirror, device: "Firefox on Linux", clock: realClock,
    listVaults: () => Promise.resolve(vaults),
    remote: (name) => servers.get(name)!,
    adopted: (v) => { adopted.push(v.name); return Promise.resolve(); },
  });
  return { browser, copies, mirror, servers, engines, adopted, vaults };
}

const held = (mirror: SealedMirror, id: string) => mirror.vault(id)?.list().filter((e) => e.local).map((e) => e.path).sort() ?? null;

describe("the engines of a copy", () => {
  test("keep every vault with All my vaults, and only the chosen ones else; a vault no longer chosen leaves the copy", async () => {
    const { mirror, servers, engines, adopted } = await setup();
    await servers.get("Work")!.seed("w.md", "work");
    await servers.get("Team")!.seed("t.md", "team");
    await engines.start();
    await vi.waitFor(() => expect(held(mirror, team.id)).toEqual(["t.md"]));
    expect(held(mirror, work.id)).toEqual(["w.md"]);
    expect(adopted.sort()).toEqual(["Team", "Work"]);

    await mirror.choose((c) => ({ ...c, all: false, vaults: [work.id] }));
    await engines.refresh(true);
    expect(mirror.vault(team.id)).toBeUndefined();
    expect(held(mirror, work.id)).toEqual(["w.md"]);
    await engines.stop();
  });

  test("a vault unticked and ticked again starts without its old folders", async () => {
    const { mirror, engines } = await setup();
    await engines.start();
    await vi.waitFor(() => expect(mirror.vault(team.id)).toBeDefined());
    await mirror.choose((c) => ({ ...c, kept: { [team.id]: ["Plans"] }, opened: { [team.id]: { "Plans/b.pdf": 2 } } }));

    await mirror.choose((c) => ({ ...c, all: false, vaults: [work.id] }));
    await engines.refresh(true);
    expect(mirror.vault(team.id)).toBeUndefined();
    await mirror.choose((c) => ({ ...c, vaults: [work.id, team.id] }));
    await engines.refresh(true);
    await vi.waitFor(() => expect(mirror.vault(team.id)).toBeDefined());
    expect(mirror.choice.kept).not.toHaveProperty(team.id);
    expect(mirror.choice.opened).not.toHaveProperty(team.id);
    await engines.stop();
  });

  test("never take a vault the contact didn't list", async () => {
    const { mirror, servers, engines } = await setup([work]);
    await servers.get("Team")!.seed("t.md", "team");
    await engines.start();
    expect(mirror.held().map((v) => v.id)).toEqual([work.id]);
    await engines.stop();
  });

  test("keep an attachment opened online, letting the least recently opened go when the budget is full", async () => {
    const { mirror, servers, engines } = await setup([work]);
    const server = servers.get("Work")!;
    await server.seedFile("a.png", png(2 * MB, 1));
    await server.seedFile("b.png", png(2 * MB, 2));
    await server.seedFile("huge.pdf", new Uint8Array(6 * MB));
    await mirror.choose((c) => ({ ...c, budget: 5 * MB }));
    await engines.start();
    await vi.waitFor(() => expect(mirror.vault(work.id)?.entry("a.png")?.remote).toBeDefined());

    await engines.opened(work.id, "a.png");
    expect(held(mirror, work.id)).toEqual(["a.png"]);
    await engines.opened(work.id, "b.png");
    expect(held(mirror, work.id)).toEqual(["a.png", "b.png"]);
    await server.seedFile("c.png", png(2 * MB, 3));
    await engines.refresh(true);
    await engines.opened(work.id, "c.png");
    expect(held(mirror, work.id)).toEqual(["b.png", "c.png"]);
    expect(Object.keys(mirror.choice.opened[work.id]!).sort()).toEqual(["b.png", "c.png"]);
    // Larger than the whole budget: not kept.
    await engines.opened(work.id, "huge.pdf");
    expect(held(mirror, work.id)).toEqual(["b.png", "c.png"]);
    expect(server.count("removeFile")).toBe(0);
    expect(mirror.waiting()).toBe(0);
    await engines.stop();
  });

  test("hear the copy's other tabs: their writes are uploaded, their choice applied", async () => {
    const { browser, copies, mirror, servers, engines } = await setup([work]);
    await engines.start();
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await delivered();
    await other.vault(work.id)!.files().writeIf("new.md", null, "from the other tab");
    await vi.waitFor(() => expect(servers.get("Work")!.notes.get("new.md")?.content).toBe("from the other tab"), { timeout: 5000 });

    await servers.get("Work")!.seedFile("Scans/big.pdf", new Uint8Array(2 * MB));
    await other.choose((c) => ({ ...c, kept: { [work.id]: ["Scans"] } }));
    await vi.waitFor(() => expect(held(mirror, work.id)).toContain("Scans/big.pdf"));
    await engines.stop();
  });

  test("on a reconnect, send what waits at once rather than at the engine's next retry: a new note cut off by the network goes up once", async () => {
    const { mirror, servers, engines } = await setup([work]);
    const server = servers.get("Work")!;
    await engines.start();
    server.failNext("put", new OfflineError("The server can't be reached."));
    await mirror.vault(work.id)!.files().writeIf("new.md", null, "# New\n");
    engines.localChanged(work.id, "new.md");
    await vi.waitFor(() => expect(server.count("put")).toBe(1), { timeout: 5000 });
    expect(mirror.waiting()).toBe(1);

    await engines.refresh();
    await vi.waitFor(() => expect(server.notes.get("new.md")?.content).toBe("# New\n"), { timeout: 1000 });
    expect(server.count("put")).toBe(2);
    await vi.waitFor(() => expect(mirror.waiting()).toBe(0), { timeout: 1000 });
    await engines.stop();
  });
});

describe("a vault leaving the copy while its engine runs", () => {
  /** Notes and an attachment in Work, all of them here, the engine running. */
  async function synced() {
    const s = await setup([work]);
    const server = s.servers.get("Work")!;
    for (const n of [1, 2, 3]) await server.seed(`n${n}.md`, `note ${n}`);
    await server.seedFile("a.png", png(100));
    await s.engines.start();
    await vi.waitFor(() => expect(held(s.mirror, work.id)).toEqual(["a.png", "n1.md", "n2.md", "n3.md"]));
    return { ...s, server };
  }

  test("re-keyed: nothing in Kumbu² goes to the trash, and the vault comes again under its new tag", async () => {
    const { browser, mirror, engines, server } = await synced();
    browser.slots.vaults = [{ ...work, tag: "tag-new" }];
    expect(await mirror.contact()).toEqual({ vaults: 1, lost: 0 });
    await engines.refresh(true);
    await vi.waitFor(() => expect(held(mirror, work.id)).toEqual(["a.png", "n1.md", "n2.md", "n3.md"]));
    expect(mirror.vault(work.id)!.tag).toBe("tag-new");
    expect(server.count("remove")).toBe(0);
    expect(server.count("removeFile")).toBe(0);
    expect([...server.notes.keys()].sort()).toEqual(["n1.md", "n2.md", "n3.md"]);
    await engines.stop();
  });

  test("purged in another tab: this tab's engine stops before it can read the emptied copy", async () => {
    const { browser, copies, mirror, engines, server } = await synced();
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await other.purge(work.id);
    await delivered();
    await server.seed("n4.md", "note 4");
    engines.hinted("Work");
    await new Promise((r) => setTimeout(r, 50));
    expect(server.count("remove")).toBe(0);
    expect(server.count("removeFile")).toBe(0);
    expect(mirror.vault(work.id)).toBeUndefined();
    await engines.stop();
  });

  test("a check already under way when the vault is purged fails, rather than deciding on what isn't there", async () => {
    const { mirror, engines, server } = await synced();
    const vault = mirror.vault(work.id)!;
    const files = vault.files();
    await mirror.purge(work.id);
    await expect(files.list()).rejects.toThrow();
    await expect(files.read("n1.md")).rejects.toThrow();
    await expect(vault.stateStore().load()).rejects.toThrow();
    expect(server.count("remove")).toBe(0);
    await engines.stop();
  });
});

describe("a vault's mirror removed for the server's reasons is told, whichever path removed it", () => {
  async function running() {
    const s = await setup();
    await s.engines.start();
    await vi.waitFor(() => expect(s.mirror.held().map((v) => v.id).sort()).toEqual([work.id, team.id].sort()));
    const told = vi.fn();
    s.mirror.listenRemoved(told);
    return { ...s, told };
  }

  test("the vault list leaves it out before a contact does: told once, though the contact finds nothing to purge", async () => {
    const { browser, mirror, engines, vaults, told } = await running();
    vaults.splice(vaults.findIndex((v) => v.id === team.id), 1);
    browser.slots.vaults = [work];
    await engines.refresh();
    expect(await mirror.contact()).toEqual({ vaults: 0, lost: 0 });
    expect(mirror.vault(team.id)).toBeUndefined();
    expect(told.mock.calls).toEqual([[{ vaults: 1, lost: 0 }]]);
    await engines.stop();
  });

  test("a contact leaves it out: told once", async () => {
    const { browser, mirror, engines, told } = await running();
    browser.slots.vaults = [work];
    await mirror.contact();
    expect(told.mock.calls).toEqual([[{ vaults: 1, lost: 0 }]]);
    await engines.stop();
  });

  test("left out when the copy is unlocked: told once the host listens", async () => {
    const { browser, copies, mirror, engines } = await running();
    await engines.stop();
    mirror.lock();
    browser.slots.vaults = [work];
    const opened = (await copies.unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    const told = vi.fn();
    opened.listenRemoved(told);
    expect(told.mock.calls).toEqual([[{ vaults: 1, lost: 0 }]]);
  });

  test("removed in another tab: told in this one too", async () => {
    const { browser, copies, engines, told } = await running();
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    browser.slots.vaults = [work];
    await other.contact();
    await delivered();
    expect(told.mock.calls).toEqual([[{ vaults: 1, lost: 0 }]]);
    await engines.stop();
  });

  test("no longer chosen on this device: not told, the device's own choice", async () => {
    const { mirror, engines, told } = await running();
    await mirror.choose((c) => ({ ...c, all: false, vaults: [work.id] }));
    await engines.refresh(true);
    expect(mirror.vault(team.id)).toBeUndefined();
    expect(told).not.toHaveBeenCalled();
    await engines.stop();
  });
});

describe("a vault the vault list leaves out, in a copy unlocked offline", () => {
  /** Both vaults in the copy, then locked and opened again with the password, as the offline sign-in form does. */
  async function unlockedOffline() {
    const s = await setup();
    await s.engines.start();
    await vi.waitFor(() => expect(s.mirror.held().map((v) => v.id).sort()).toEqual([work.id, team.id].sort()));
    await s.engines.stop();
    s.mirror.lock();
    const mirror = (await s.copies.unlockOffline((await s.copies.forUsername(signIn.username))!, signIn.password))!;
    const listed: ListedVault[] = [{ id: work.id, name: "Work", canWrite: true }];
    const engines = new MirrorEngines({
      mirror, device: "Firefox on Linux", clock: realClock, listVaults: () => Promise.resolve(listed), remote: (name) => s.servers.get(name)!,
    });
    return { ...s, mirror, engines, listed };
  }

  test("stays until a contact of the copy went through", async () => {
    const { browser, mirror, engines } = await unlockedOffline();
    await engines.start();
    expect(mirror.vault(team.id)).toBeDefined();

    browser.slots.vaults = [work];
    expect(await mirror.contact()).toEqual({ vaults: 1, lost: 0 });
    expect(mirror.vault(team.id)).toBeUndefined();
    await engines.stop();
  });

  test("goes once a contact went through, though the contact still listed it", async () => {
    const { mirror, engines } = await unlockedOffline();
    await engines.start();
    await mirror.contact();
    await engines.refresh();
    expect(mirror.vault(team.id)).toBeUndefined();
    await engines.stop();
  });
});
