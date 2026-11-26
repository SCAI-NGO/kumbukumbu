// SPDX-License-Identifier: AGPL-3.0-or-later
// The sealed store (offline mode design §4, §5, §3a): copies and their unlocking, the sealed records, the engine's
// ports over them, the order of the first download, contact and purge, and the tabs of one browser.
import { describe, expect, test, vi } from "vitest";
import { SyncEngine } from "../src/engine/engine";
import { AuthError, OfflineError } from "../src/engine/ports";
import { versionOf } from "../src/engine/version";
import type { OfflineChoice } from "../src/store/choice";
import { MirrorCopies, IDLE_MS, type SealedMirror } from "../src/store/mirror";
import { IdleLock } from "../src/store/idle";
import { MirrorLockedError, SMALL_ATTACHMENT_BYTES, type VaultMirror } from "../src/store/vault";
import { FakeServer } from "./fakes/fakeServer";
import { delivered, FakeBrowser, realClock } from "./fakeStore";

const work = { id: "vault-1f3a", tag: "tag-a", canWrite: true, pending: false };
const team = { id: "vault-77c2", tag: "tag-b", canWrite: false, pending: false };
const signIn = { userId: "user-8d1e", username: "Ada", password: "correct horse battery", label: "Firefox on Linux", idleMs: IDLE_MS };

async function setup(vaults = [work, team]) {
  const browser = new FakeBrowser();
  browser.slots.vaults = vaults;
  const copies = new MirrorCopies(browser.tab());
  const mirror = await copies.create(signIn);
  return { browser, copies, mirror };
}

/** A sync engine for one vault over its mirror, against a fake Kumbu². */
function engineFor(vault: VaultMirror, server: FakeServer, onNotesDone = () => {}) {
  return new SyncEngine({
    vault: "Work", device: "Firefox on Linux", local: vault.files(), remote: vault.inventory(server), state: vault.stateStore(), clock: realClock,
    onStatus: () => {}, onConflict: () => {}, onAuthLost: () => {}, fetchPolicy: vault.downloadOrder(onNotesDone),
  });
}

const png = (size: number) => { const b = new Uint8Array(size); b.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]); return b; };

describe("a copy", () => {
  test("keeps nothing in the clear but its header, which holds no name, id or key", async () => {
    const { browser, mirror } = await setup();
    const server = new FakeServer();
    await server.seed("Diary/Secret crush.md", "I love Bob");
    const vault = await mirror.adopt(work.id);
    const engine = engineFor(vault, server);
    await engine.start();
    await engine.idle();
    expect(await vault.files().read("Diary/Secret crush.md")).toBe("I love Bob");

    const dump = browser.storage.dump();
    for (const plain of ["Secret", "crush", "Diary", "Bob", work.id, signIn.userId, signIn.username, signIn.password, "Work"]) expect(dump).not.toContain(plain);
    const slot = browser.slots.slots.get("token1")!;
    expect(dump).not.toContain(slot.key);
    const headers = [...browser.storage.records.keys()].filter((k) => k.startsWith("h/"));
    expect(headers).toHaveLength(1);
  });

  test("is found by the user's id online and by the username offline, whatever its case", async () => {
    const { copies, mirror } = await setup();
    expect((await copies.forUser("user-8d1e"))?.copy).toBe(mirror.header.copy);
    expect((await copies.forUsername("ada"))?.copy).toBe(mirror.header.copy);
    expect(await copies.forUsername("bob")).toBeNull();
    expect(await copies.forUser("user-0000")).toBeNull();
  });

  test("opens offline with the password, and not without it", async () => {
    const { copies, mirror } = await setup();
    const vault = await mirror.adopt(work.id);
    await vault.files().writeIf("Plan.md", null, "Offline plan");
    mirror.lock();

    expect(await copies.unlockOffline(mirror.header, "wrong")).toBeNull();
    const again = await copies.unlockOffline(mirror.header, signIn.password);
    expect(await again!.vault(work.id)!.files().read("Plan.md")).toBe("Offline plan");
  });

  test("opens online with the slot's key, and a sign-in wraps it again under the new password", async () => {
    const { browser, copies, mirror } = await setup();
    await mirror.adopt(work.id);
    mirror.lock();

    const online = await copies.unlockOnline(mirror.header);
    expect(online).not.toBe("wiped");
    expect((online as SealedMirror).vault(work.id)).toBeDefined();

    const changed = await copies.unlockOnline(mirror.header, { username: "Ada", password: "a new password", idleMs: 7 * 24 * 3600_000 });
    (changed as SealedMirror).lock();
    const header = (await copies.forUser(signIn.userId))!;
    expect(header.idleMs).toBe(7 * 24 * 3600_000);
    expect(await copies.unlockOffline(header, signIn.password)).toBeNull();
    expect(await copies.unlockOffline(header, "a new password")).not.toBeNull();
    expect(browser.slots.contacts.every((c) => c.token === "token1")).toBe(true);
  });

  test("reports the changes waiting right after an online unlock, which the first contact couldn't count", async () => {
    const { browser, copies, mirror } = await setup();
    const vault = await mirror.adopt(work.id);
    await vault.files().writeIf("New.md", null, "not uploaded");
    mirror.lock();
    browser.slots.contacts = [];
    await copies.unlockOnline(mirror.header);
    // The first says it doesn't know, never "none": the device list must not read it as nothing to lose.
    expect(browser.slots.contacts.map((c) => c.waiting)).toEqual([null, 1]);
  });

  test("reports nothing waiting after an online unlock too, so the device list knows", async () => {
    const { browser, copies, mirror } = await setup();
    mirror.lock();
    browser.slots.contacts = [];
    await copies.unlockOnline(mirror.header);
    expect(browser.slots.contacts.map((c) => c.waiting)).toEqual([null, 0]);
  });

  test("keeps the copy open when only the count after an online unlock fails to arrive", async () => {
    const { browser, copies, mirror } = await setup();
    await mirror.adopt(work.id);
    mirror.lock();
    const contact = browser.slots.contact.bind(browser.slots);
    let calls = 0;
    browser.slots.contact = (token, waiting) => (++calls === 2 ? Promise.reject(new OfflineError("gone away")) : contact(token, waiting));
    const opened = await copies.unlockOnline(mirror.header);
    expect(opened).not.toBe("wiped");
    expect((opened as SealedMirror).held()).toHaveLength(1);
  });

  test("a wrap copied from another copy's header doesn't open this one", async () => {
    const { browser, copies, mirror } = await setup();
    const other = await new MirrorCopies(browser.tab()).create({ ...signIn, userId: "user-2", username: "Bob" });
    const forged = { ...other.header, password: mirror.header.password };
    expect(await copies.unlockOffline(forged, signIn.password)).toBeNull();
  });

  test("locked, it reads and writes nothing", async () => {
    const { mirror } = await setup();
    const vault = await mirror.adopt(work.id);
    mirror.lock();
    expect(() => mirror.held()).toThrow(MirrorLockedError);
    await expect(vault.files().writeIf("x.md", null, "x")).rejects.toThrow(MirrorLockedError);
  });

  test("never mirrors a vault the server didn't list, as it leaves out discreet ones", async () => {
    const { mirror } = await setup([work]);
    await expect(mirror.adopt("vault-discreet")).rejects.toThrow(/lists/);
    expect(mirror.held()).toEqual([]);
  });
});

describe("the engine over the mirror", () => {
  test("downloads the inventory first, then every note's text, then the small attachments", async () => {
    const { mirror } = await setup();
    const server = new FakeServer();
    await server.seed("a.md", "alpha");
    await server.seed("z.md", "omega");
    await server.seedFile("img/small.png", png(100));
    await server.seedFile("img/large.png", png(SMALL_ATTACHMENT_BYTES + 1));
    const vault = await mirror.adopt(work.id);

    // The tree is complete before the first text arrives.
    let atFirstRead: string[] = [];
    const read = server.read.bind(server);
    server.read = (path) => {
      if (atFirstRead.length === 0) atFirstRead = vault.list().map((e) => `${e.path}:${e.local ? "here" : "listed"}`).sort();
      return read(path);
    };
    let notesDone = 0;
    const engine = engineFor(vault, server, () => { notesDone++; void engine.check(true); });
    await engine.start();
    await engine.idle();
    await vi.waitFor(() => expect(vault.entry("img/small.png")?.local).toBeDefined());
    await engine.idle();

    expect(atFirstRead).toEqual(["a.md:listed", "img/large.png:listed", "img/small.png:listed", "z.md:listed"]);
    const order = server.calls.filter((c) => c.method === "read" || c.method === "readFile").map((c) => `${c.method}:${c.args[0]}`);
    expect(order).toEqual(["read:a.md", "read:z.md", "readFile:img/small.png"]);
    expect(notesDone).toBe(1);
    expect(vault.entry("img/large.png")).toMatchObject({ remote: { kind: "image", size: SMALL_ATTACHMENT_BYTES + 1 } });
    expect(vault.entry("img/large.png")?.local).toBeUndefined();
    expect(vault.waiting()).toBe(0);
  });

  test("an interrupted download keeps what came, and the next start goes on from there", async () => {
    const { copies, mirror } = await setup();
    const server = new FakeServer();
    for (const n of [1, 2, 3]) await server.seed(`n${n}.md`, `note ${n}`);
    const vault = await mirror.adopt(work.id);
    server.failNext("read", new Error("connection reset"));
    const engine = engineFor(vault, server);
    await engine.start();
    await engine.idle();
    await engine.stop();
    expect(vault.list().filter((e) => e.local).map((e) => e.path)).toEqual(["n2.md", "n3.md"]);
    expect(vault.entry("n1.md")).toMatchObject({ remote: {} });
    mirror.lock();

    const again = (await copies.unlockOffline(mirror.header, signIn.password))!;
    const resumed = engineFor(again.vault(work.id)!, server);
    server.calls.length = 0;
    await resumed.start();
    await resumed.idle();
    expect(server.calls.filter((c) => c.method === "read").map((c) => c.args[0])).toEqual(["n1.md"]);
  });

  test("an edit made offline waits, and uploads once the engine runs", async () => {
    const { mirror } = await setup();
    const server = new FakeServer();
    await server.seed("Plan.md", "v1");
    const vault = await mirror.adopt(work.id);
    const engine = engineFor(vault, server);
    await engine.start();
    await engine.idle();
    await engine.stop();

    const files = vault.files();
    expect(await files.writeIf("Plan.md", await versionOf("v1"), "v2 offline")).toBe(true);
    expect(await files.writeIf("Fresh.md", null, "new")).toBe(true);
    expect(await files.trashIf("Plan.md", await versionOf("v1"))).toBe(false);
    expect(vault.waiting()).toBe(2);

    await engine.start();
    await engine.idle();
    expect(server.notes.get("Plan.md")?.content).toBe("v2 offline");
    expect(server.notes.get("Fresh.md")?.content).toBe("new");
    expect(vault.waiting()).toBe(0);
  });

  test("writeIf checks against the editor's unsaved text", async () => {
    const { mirror } = await setup();
    const vault = await mirror.adopt(work.id);
    await vault.files().writeIf("a.md", null, "saved");
    const drafts = new Map([["a.md", "typing…"]]);
    const files = vault.files({ text: (p) => drafts.get(p) ?? null });
    expect(await files.read("a.md")).toBe("typing…");
    expect(await files.writeIf("a.md", await versionOf("saved"), "from Kumbu²")).toBe(false);
    expect(await files.writeIf("a.md", await versionOf("typing…"), "from Kumbu²")).toBe(true);
  });

  test("a record moved to another path's place doesn't open there", async () => {
    const { browser, mirror } = await setup();
    const vault = await mirror.adopt(work.id);
    await vault.files().writeIf("a.md", null, "A");
    await vault.files().writeIf("b.md", null, "B");
    const bodies = [...browser.storage.records.keys()].filter((k) => k.includes("/b/"));
    const [one, two] = bodies.map((k) => browser.storage.records.get(k)!);
    browser.storage.records.set(bodies[0]!, two!);
    browser.storage.records.set(bodies[1]!, one!);
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    // Damage, not a deletion: the engine must never read it as one.
    await expect(vault.files().read("a.md")).rejects.toThrow(/damaged/);
    expect(warn).toHaveBeenCalled();
    warn.mockRestore();
  });
});

describe("contact", () => {
  test("purges a vault no longer listed, and one re-keyed, and counts the changes lost", async () => {
    const { browser, mirror } = await setup();
    const w = await mirror.adopt(work.id);
    const t = await mirror.adopt(team.id);
    await w.files().writeIf("unsent.md", null, "x");
    await t.files().writeIf("t.md", null, "y");

    browser.slots.vaults = [{ ...team, tag: "tag-b2" }];
    expect(await mirror.contact()).toEqual({ vaults: 2, lost: 2 });
    expect(mirror.held()).toEqual([]);
    expect([...browser.storage.records.keys()].filter((k) => k.startsWith("c/"))).toEqual([]);
    expect(browser.slots.contacts.at(-1)?.waiting).toBe(2);
  });

  test("a vault that leaves for good drops out of kept, opened and vaults under the same lock", async () => {
    const { browser, copies, mirror } = await setup();
    await mirror.adopt(work.id);
    await mirror.adopt(team.id);
    await mirror.choose((c): OfflineChoice => ({
      ...c, all: false, vaults: [work.id, team.id],
      kept: { [work.id]: ["Projects"], [team.id]: ["Plans"] },
      opened: { [work.id]: { "Projects/a.pdf": 1 }, [team.id]: { "Plans/b.pdf": 2 } },
    }));
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;

    browser.slots.vaults = [work];
    await mirror.contact();
    const left = { all: false, vaults: [work.id], kept: { [work.id]: ["Projects"] }, opened: { [work.id]: { "Projects/a.pdf": 1 } } };
    expect(mirror.choice).toMatchObject(left);
    expect(mirror.choice.kept).not.toHaveProperty(team.id);
    expect(mirror.choice.opened).not.toHaveProperty(team.id);
    await delivered();
    expect(other.choice).toMatchObject(left);
    mirror.lock();
    other.lock();
    const again = (await copies.unlockOffline(mirror.header, signIn.password))!;
    expect(again.choice).toMatchObject(left);
    expect(again.choice.kept).not.toHaveProperty(team.id);
  });

  test("a re-keyed vault keeps its place and its kept folders in the device's choice", async () => {
    const { browser, mirror } = await setup();
    await mirror.adopt(work.id);
    await mirror.adopt(team.id);
    await mirror.choose((c) => ({ ...c, all: false, vaults: [work.id, team.id], kept: { [team.id]: ["Plans"] }, opened: { [team.id]: { "Plans/b.pdf": 2 } } }));

    browser.slots.vaults = [work, { ...team, tag: "tag-b2" }];
    expect(await mirror.contact()).toEqual({ vaults: 1, lost: 0 });
    expect(mirror.vault(team.id)).toBeUndefined();
    expect(mirror.choice).toMatchObject({ vaults: [work.id, team.id], kept: { [team.id]: ["Plans"] }, opened: { [team.id]: { "Plans/b.pdf": 2 } } });
  });

  test("an unlock drops kept and opened entries of vaults the copy doesn't hold and keeps a vault ticked but not adopted yet", async () => {
    const { copies, mirror } = await setup();
    await mirror.adopt(work.id);
    await mirror.choose((c): OfflineChoice => ({
      ...c, all: false, vaults: [work.id, team.id],
      kept: { [work.id]: ["Projects"], [team.id]: ["Plans"], "vault-gone": ["Old"] },
      opened: { [work.id]: { "Projects/a.pdf": 1 }, "vault-gone": { "Old/c.pdf": 3 } },
    }));
    mirror.lock();

    const again = (await copies.unlockOffline(mirror.header, signIn.password))!;
    expect(again.choice).toMatchObject({ vaults: [work.id, team.id], kept: { [work.id]: ["Projects"] }, opened: { [work.id]: { "Projects/a.pdf": 1 } } });
    expect(Object.keys(again.choice.kept)).toEqual([work.id]);
    expect(Object.keys(again.choice.opened)).toEqual([work.id]);
  });

  test("follows a change of rights", async () => {
    const { browser, mirror } = await setup();
    const t = await mirror.adopt(team.id);
    expect(t.canWrite).toBe(false);
    browser.slots.vaults = [work, { ...team, canWrite: true }];
    expect(await mirror.contact()).toEqual({ vaults: 0, lost: 0 });
    expect(t.canWrite).toBe(true);
  });

  test("a removed slot wipes the whole copy and locks it", async () => {
    const { browser, copies, mirror } = await setup();
    await (await mirror.adopt(work.id)).files().writeIf("a.md", null, "a");
    browser.slots.removeAll();
    expect(await mirror.contact()).toBe("wiped");
    expect(mirror.locked).toBe(true);
    expect(browser.storage.records.size).toBe(0);
    expect(await copies.forUser(signIn.userId)).toBeNull();
  });

  test("so does an online unlock that finds the slot gone", async () => {
    const { browser, copies, mirror } = await setup();
    mirror.lock();
    browser.slots.removeAll();
    expect(await copies.unlockOnline(mirror.header)).toBe("wiped");
    expect(browser.storage.records.size).toBe(0);
  });

  test("without a session or a server it changes nothing", async () => {
    const { browser, mirror } = await setup();
    await mirror.adopt(work.id);
    browser.slots.fail = new AuthError("signed out");
    await expect(mirror.contact()).rejects.toThrow(AuthError);
    expect(mirror.held()).toHaveLength(1);
  });

  test("a new copy for the same user replaces the old one", async () => {
    const { browser, copies } = await setup();
    await copies.create(signIn);
    expect((await copies.headers()).length).toBe(1);
    expect(browser.slots.slots.size).toBe(2);
  });
});

describe("tabs", () => {
  test("see each other's changes, and only one runs the engines at a time", async () => {
    const { browser, mirror: a } = await setup();
    const vaultA = await a.adopt(work.id);
    const b = (await new MirrorCopies(browser.tab()).unlockOffline(a.header, signIn.password))!;
    const heard: string[] = [];
    a.onChanged = (_v, paths) => heard.push(...paths);

    const vaultB = b.vault(work.id)!;
    await vaultB.files().writeIf("from-b.md", null, "hello");
    // Hearing it reads the entry again, which takes WebCrypto's own time.
    await vi.waitFor(() => expect(heard).toContain("from-b.md"));
    expect(await vaultA.files().read("from-b.md")).toBe("hello");

    const leads: string[] = [];
    void a.lead().then(() => leads.push("a"));
    await delivered();
    void b.lead().then(() => leads.push("b"));
    await delivered();
    expect(leads).toEqual(["a"]);
    a.lock();
    await delivered();
    expect(leads).toEqual(["a", "b"]);
  });

  test("a sign-out in one tab locks the others, through their host when it listens", async () => {
    const { browser, mirror: a } = await setup();
    const b = (await new MirrorCopies(browser.tab()).unlockOffline(a.header, signIn.password))!;
    const c = (await new MirrorCopies(browser.tab()).unlockOffline(a.header, signIn.password))!;
    let asked = 0;
    c.onLockAsked = () => { asked++; c.lock(); };
    a.lockEverywhere();
    await delivered();
    expect([a.locked, b.locked, c.locked]).toEqual([true, true, true]);
    expect(asked).toBe(1);
  });

  test("a wipe in one tab locks the others", async () => {
    const { browser, mirror: a } = await setup();
    const b = (await new MirrorCopies(browser.tab()).unlockOffline(a.header, signIn.password))!;
    let purged: string | null | undefined;
    b.onPurged = (v) => { purged = v; };
    await a.remove();
    await delivered();
    expect(b.locked).toBe(true);
    expect(purged).toBeNull();
  });
});

describe("the idle lock", () => {
  test("runs after the limit without use, and each use starts it again", () => {
    vi.useFakeTimers();
    try {
      let idle = 0;
      const lock = new IdleLock(realClock, 30 * 60_000, () => { idle++; });
      vi.advanceTimersByTime(20 * 60_000);
      lock.touch();
      vi.advanceTimersByTime(20 * 60_000);
      expect(idle).toBe(0);
      vi.advanceTimersByTime(10 * 60_000);
      expect(idle).toBe(1);
      lock.stop();
    } finally {
      vi.useRealTimers();
    }
  });

  test("a use after the limit has passed, unseen by the timer (a sleeping laptop), locks instead of starting again", () => {
    let now = 0;
    const frozen = { now: () => now, setTimeout: () => 1, clearTimeout: () => {} };
    let idle = 0;
    const lock = new IdleLock(frozen, 30 * 60_000, () => { idle++; });
    now = 8 * 3600_000;
    lock.touch();
    expect(idle).toBe(1);
    lock.touch();
    expect(idle).toBe(1);
  });

  test("a check on waking locks once the limit has passed", () => {
    let now = 0;
    const frozen = { now: () => now, setTimeout: () => 1, clearTimeout: () => {} };
    let idle = 0;
    const lock = new IdleLock(frozen, 30 * 60_000, () => { idle++; });
    now = 29 * 60_000;
    lock.check();
    expect(idle).toBe(0);
    now = 31 * 60_000;
    lock.check();
    expect(idle).toBe(1);
  });
});
