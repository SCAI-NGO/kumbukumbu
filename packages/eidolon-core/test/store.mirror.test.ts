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
import type { TabChannel } from "../src/store/ports";
import { EditorDrafts } from "../src/store/writing";
import { MirrorLockedError, SMALL_ATTACHMENT_BYTES, type VaultMirror } from "../src/store/vault";
import { FakeServer } from "./fakes/fakeServer";
import { delivered, fakeKdf, FakeBrowser, nodeCrypto, realClock } from "./fakeStore";

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

  describe("in a vault held read-only", () => {
    /** Bob's read-only vault, mirrored, with text in an open editor; `changed`: a writer changed the note since. */
    async function readOnly(changed: boolean) {
      const { mirror } = await setup([team]);
      const server = new FakeServer();
      server.refuse = () => "You can only read Work.";
      await server.seed("plan.md", "one");
      const vault = await mirror.adopt(team.id);
      const first = engineFor(vault, server);
      await first.start();
      await first.idle();
      await first.stop();
      if (changed) await server.agentWrite("plan.md", "two, by Alice");
      const drafts = new EditorDrafts();
      const replaced: (string | null)[] = [];
      drafts.open("plan.md", { text: () => "one, and Bob's typing", replaced: (c) => replaced.push(c) });
      const engine = new SyncEngine({
        vault: "Work", device: "Firefox on Linux", local: vault.files(drafts), remote: vault.inventory(server), state: vault.stateStore(), clock: realClock,
        onStatus: () => {}, onConflict: () => {}, onAuthLost: () => {}, fetchPolicy: vault.downloadOrder(() => {}),
      });
      await engine.start();
      await engine.idle();
      await engine.stop();
      const held = vault.list().filter((e) => e.local).map((e) => e.path);
      return { vault, server, held, replaced };
    }

    test("the engine reads no editor's text, so it plans no upload", async () => {
      const { vault, server, held } = await readOnly(false);
      expect(server.count("put")).toBe(0);
      expect(held).toEqual(["plan.md"]);
      expect(vault.waiting()).toBe(0);
    });

    test("a writer's change comes down without a conflict copy, and the editor is offered it", async () => {
      const { vault, server, held, replaced } = await readOnly(true);
      expect(server.count("put")).toBe(0);
      expect(held).toEqual(["plan.md"]);
      expect(await vault.files().read("plan.md")).toBe("two, by Alice");
      expect(replaced).toEqual(["two, by Alice"]);
      expect(vault.waiting()).toBe(0);
    });
  });

  test("in a vault held to write, the engine still reads the editor's text", async () => {
    const { mirror } = await setup([work]);
    const vault = await mirror.adopt(work.id);
    await vault.files().writeIf("plan.md", null, "one");
    const drafts = new EditorDrafts();
    drafts.open("plan.md", { text: () => "one, and Ada's typing", replaced: () => {} });
    expect(await vault.files(drafts).read("plan.md")).toBe("one, and Ada's typing");
  });
});

describe("a sign-in while another tab removes the copy", () => {
  /** Ada's copy, locked; a sign-in in a second tab whose password wrap waits for `release`. */
  async function signingIn() {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    const copies = new MirrorCopies(browser.tab());
    (await copies.create(signIn)).lock();
    const header = (await copies.forUser(signIn.userId))!;
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const slow = { ...browser.tab(), crypto: { ...nodeCrypto, kdf: { derive: async (...a: Parameters<typeof fakeKdf.derive>) => { await gate; return fakeKdf.derive(...a); } } } };
    const opening = new MirrorCopies(slow).unlockOnline(header, { username: signIn.username, password: signIn.password, idleMs: IDLE_MS });
    await delivered();
    return { browser, copies, header, release, opening };
  }

  test("writes no header over a copy another tab removed, and answers wiped", async () => {
    const { browser, copies, header, release, opening } = await signingIn();
    await copies.wipe(header.copy);
    browser.slots.fail = new OfflineError("Kumbu² can't be reached.");
    release();
    expect(await opening).toBe("wiped");
    expect(await copies.headers()).toEqual([]);
    expect([...browser.storage.records.keys()]).toEqual([]);
  });

  test("the sign-in's header write waits for the copy's lock, and a removal after it still removes the copy", async () => {
    const { browser, copies, header, release, opening } = await signingIn();
    const holder = browser.locks.hold(`kumbu-mirror:${header.copy}`);
    await holder.acquired;
    release();
    await delivered();
    expect(await copies.headers()).toEqual([header]);
    const removing = copies.wipe(header.copy);
    await delivered();
    expect(await copies.headers()).toHaveLength(1);
    holder.release();
    await removing;
    const opened = await opening;
    if (opened !== "wiped") opened.lock();
    expect(await copies.headers()).toEqual([]);
  });

  test("with the copy still there, the sign-in wraps it under the password given", async () => {
    const { copies, release, opening } = await signingIn();
    release();
    const opened = (await opening) as SealedMirror;
    opened.lock();
    expect(await copies.unlockOffline((await copies.forUsername(signIn.username))!, signIn.password)).not.toBeNull();
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
    await vi.waitFor(() => expect(other.choice).toMatchObject(left), { timeout: 5000 });
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

  test("an unlock drops kept and opened entries of vaults neither held nor chosen, and keeps those of a vault ticked but not adopted yet", async () => {
    const { copies, mirror } = await setup();
    await mirror.adopt(work.id);
    await mirror.choose((c): OfflineChoice => ({
      ...c, all: false, vaults: [work.id, team.id],
      kept: { [work.id]: ["Projects"], [team.id]: ["Plans"], "vault-gone": ["Old"] },
      opened: { [work.id]: { "Projects/a.pdf": 1 }, "vault-gone": { "Old/c.pdf": 3 } },
    }));
    mirror.lock();

    const again = (await copies.unlockOffline(mirror.header, signIn.password))!;
    expect(again.choice).toMatchObject({
      vaults: [work.id, team.id], kept: { [work.id]: ["Projects"], [team.id]: ["Plans"] }, opened: { [work.id]: { "Projects/a.pdf": 1 } },
    });
    expect(Object.keys(again.choice.kept).sort()).toEqual([work.id, team.id].sort());
    expect(Object.keys(again.choice.opened)).toEqual([work.id]);
  });

  test.each([
    ["chosen one by one", { all: false, vaults: [work.id, team.id] }],
    ["with All my vaults", { all: true, vaults: [] }],
  ])("a re-key seen at an online unlock keeps the vault's kept folders, %s", async (_, chosen) => {
    const { browser, copies, mirror } = await setup();
    await mirror.adopt(work.id);
    await mirror.adopt(team.id);
    await mirror.choose((c) => ({ ...c, ...chosen, kept: { [team.id]: ["Plans"] }, opened: { [team.id]: { "Plans/b.pdf": 2 } } }));
    mirror.lock();

    browser.slots.vaults = [work, { ...team, tag: "tag-b2" }];
    const again = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    expect(again.vault(team.id)).toBeUndefined();
    expect(again.choice).toMatchObject({ kept: { [team.id]: ["Plans"] }, opened: { [team.id]: { "Plans/b.pdf": 2 } } });
  });

  test("an online unlock drops the kept folders of a chosen vault the listing no longer has", async () => {
    const { browser, copies, mirror } = await setup();
    await mirror.adopt(work.id);
    await mirror.choose((c): OfflineChoice => ({ ...c, all: false, vaults: [work.id, team.id], kept: { [work.id]: ["Projects"], [team.id]: ["Plans"] } }));
    mirror.lock();

    browser.slots.vaults = [work];
    const again = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    expect(again.choice.kept).toEqual({ [work.id]: ["Projects"] });
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

describe("sign in again while another tab removes the copy", () => {
  test("writes no header back over the removed copy, and answers wiped", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    // The password wrap of this tab waits for `release`, once the copy is made.
    let slowNow = false;
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const kdf = { derive: async (...a: Parameters<typeof fakeKdf.derive>) => { if (slowNow) await gate; return fakeKdf.derive(...a); } };
    const mirror = await new MirrorCopies({ ...browser.tab(), crypto: { ...nodeCrypto, kdf } }).create(signIn);
    slowNow = true;
    const rewrapping = mirror.rewrap(signIn.username, signIn.password);
    await delivered();

    const other = new MirrorCopies(browser.tab());
    await other.wipe(mirror.header.copy);
    release();

    expect(await rewrapping).toBe("wiped");
    expect(await other.headers()).toEqual([]);
    expect([...browser.storage.records.keys()]).toEqual([]);
  });

  test("leaves the copy locked once it answers wiped, even in a tab that heard nothing of the removal", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    let slowNow = false;
    let release!: () => void;
    const gate = new Promise<void>((r) => { release = r; });
    const kdf = { derive: async (...a: Parameters<typeof fakeKdf.derive>) => { if (slowNow) await gate; return fakeKdf.derive(...a); } };
    const deaf = { ...browser.tab(), crypto: { ...nodeCrypto, kdf }, channel: (copy: string) => ({ post: browser.bus.channel(copy).post, listen: () => () => {} }) };
    const mirror = await new MirrorCopies(deaf).create(signIn);
    slowNow = true;
    const rewrapping = mirror.rewrap(signIn.username, signIn.password);
    await delivered();

    await new MirrorCopies(browser.tab()).wipe(mirror.header.copy);
    release();

    expect(await rewrapping).toBe("wiped");
    expect(mirror.locked).toBe(true);
  });

  test("a removal waits for the copy's lock, which the header write holds", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    const copies = new MirrorCopies(browser.tab());
    const copy = (await copies.create(signIn)).header.copy;
    const holder = browser.locks.hold(`kumbu-mirror:${copy}`);
    await holder.acquired;
    const removing = copies.wipe(copy);
    await delivered();
    expect((await copies.headers()).length).toBe(1);
    holder.release();
    await removing;
    expect(await copies.headers()).toEqual([]);
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

  const metaRecords = (browser: FakeBrowser) => [...browser.storage.records.keys()].filter((k) => /^c\/[^/]+\/m\//.test(k));

  test("setMeta writes nothing for a vault purged meanwhile in the same tab", async () => {
    const { browser, mirror } = await setup([work]);
    const vault = await mirror.adopt(work.id);
    await Promise.allSettled([vault.setMeta({ name: "Home" }), mirror.purge(work.id)]);
    expect(metaRecords(browser)).toEqual([]);
  });

  test("setMeta writes nothing for a vault another tab purged before this one heard", async () => {
    const { browser, copies, mirror } = await setup([work]);
    const vault = await mirror.adopt(work.id);
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await delivered();
    browser.slots.vaults = [];
    await other.contact();
    await vault.setMeta({ name: "Home" });
    await delivered();
    expect(metaRecords(browser)).toEqual([]);
    mirror.lock();
    other.lock();
    expect((await copies.unlockOffline(mirror.header, signIn.password))!.held()).toEqual([]);
  });

  test("setMeta writes nothing over a vault another tab re-keyed and adopted again before this one heard", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    // This tab hears nothing from the others.
    const deaf = { ...browser.tab(), channel: (copy: string) => ({ post: browser.bus.channel(copy).post, listen: () => () => {} }) };
    const copies = new MirrorCopies(deaf);
    const mirror = await copies.create(signIn);
    const vault = await mirror.adopt(work.id);
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await delivered();
    browser.slots.vaults = [{ ...work, tag: "tag-b" }];
    await other.contact();
    await (await other.adopt(work.id)).files().writeIf("Plan.md", null, "Written after the re-key");
    await vault.setMeta({ name: "Home" });
    await delivered();
    mirror.lock();
    other.lock();
    const again = (await copies.unlockOffline(mirror.header, signIn.password))!.vault(work.id)!;
    expect(again.tag).toBe("tag-b");
    expect(again.name).toBeUndefined();
    expect(await again.files().read("Plan.md")).toBe("Written after the re-key");
  });

  test("a note written by a tab that hasn't heard the vault was purged is refused, and leaves nothing behind", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    const deaf = { ...browser.tab(), channel: (copy: string) => ({ post: browser.bus.channel(copy).post, listen: () => () => {} }) };
    const copies = new MirrorCopies(deaf);
    const mirror = await copies.create(signIn);
    const vault = await mirror.adopt(work.id);
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await delivered();
    browser.slots.vaults = [];
    await other.contact();
    await expect(vault.files().writeIf("Stale.md", null, "Written before hearing")).rejects.toThrow(MirrorLockedError);
    expect([...browser.storage.records.keys()].filter((k) => k.startsWith(vault.dataPrefix))).toEqual([]);
  });

  test("a note written by a tab that hasn't heard of the vault's new adoption is refused, and the new one's notes stay", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    // This tab hears nothing from the others.
    const deaf = { ...browser.tab(), channel: (copy: string) => ({ post: browser.bus.channel(copy).post, listen: () => () => {} }) };
    const copies = new MirrorCopies(deaf);
    const mirror = await copies.create(signIn);
    const vault = await mirror.adopt(work.id);
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await delivered();
    browser.slots.vaults = [{ ...work, tag: "tag-b" }];
    await other.contact();
    await (await other.adopt(work.id)).files().writeIf("Plan.md", null, "Written after the re-key");
    await expect(vault.files().writeIf("Stale.md", null, "Written before hearing")).rejects.toThrow(MirrorLockedError);
    expect(() => vault.assertHeld()).toThrow(MirrorLockedError);
    await delivered();
    mirror.lock();
    other.lock();
    const again = (await copies.unlockOffline(mirror.header, signIn.password))!.vault(work.id)!;
    expect(again.list().map((e) => e.path)).toEqual(["Plan.md"]);
    expect(await again.files().read("Plan.md")).toBe("Written after the re-key");
  });

  test("a tab that missed a vault's purge takes up its new adoption when told of a change in it", async () => {
    const { browser, copies, mirror } = await setup([work]);
    await mirror.adopt(work.id);
    // This tab misses the purge, and hears the rest.
    const missing = {
      ...browser.tab(),
      channel: (copy: string): TabChannel => {
        const c = browser.bus.channel(copy);
        return { post: c.post, listen: (fn) => c.listen((m) => { if (m.type !== "purged") fn(m); }) };
      },
    };
    const other = (await new MirrorCopies(missing).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    const old = other.vault(work.id)!;
    const dropped: string[] = [];
    other.onDrop = (id) => { dropped.push(id); return Promise.resolve(); };
    await mirror.purge(work.id, false, false);
    await (await mirror.adopt(work.id)).files().writeIf("Plan.md", null, "Written after the new adoption");
    await vi.waitFor(() => expect(other.vault(work.id)?.entry("Plan.md")).toBeDefined());
    expect(other.vault(work.id)).not.toBe(old);
    expect(() => old.assertHeld()).toThrow(MirrorLockedError);
    expect(dropped).toEqual([work.id]);
  });

  test("a tab that missed a re-adoption doesn't remove the new one when it purges", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    // This tab hears nothing from the others.
    const deaf = { ...browser.tab(), channel: (copy: string) => ({ post: browser.bus.channel(copy).post, listen: () => () => {} }) };
    const copies = new MirrorCopies(deaf);
    const mirror = await copies.create(signIn);
    await mirror.adopt(work.id);
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await delivered();
    browser.slots.vaults = [{ ...work, tag: "tag-b" }];
    await other.contact();
    await (await other.adopt(work.id)).files().writeIf("Plan.md", null, "Written after the re-key");
    await mirror.contact();
    expect(mirror.vault(work.id)?.entry("Plan.md")).toBeDefined();
    await delivered();
    mirror.lock();
    other.lock();
    const again = (await copies.unlockOffline(mirror.header, signIn.password))!.vault(work.id);
    expect(again?.list().map((e) => e.path)).toEqual(["Plan.md"]);
  });

  test("a tab that missed a re-key's purge purges the vault too, and holds it no more", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    // This tab hears nothing from the others.
    const deaf = { ...browser.tab(), channel: (copy: string) => ({ post: browser.bus.channel(copy).post, listen: () => () => {} }) };
    const copies = new MirrorCopies(deaf);
    const mirror = await copies.create(signIn);
    await mirror.adopt(work.id);
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await delivered();
    browser.slots.vaults = [{ ...work, tag: "tag-b" }];
    await other.contact();
    await mirror.contact();
    expect(mirror.vault(work.id)).toBeUndefined();
    expect(metaRecords(browser)).toEqual([]);
  });

  test("a tab that missed a re-adoption still removes the vault when it leaves for good", async () => {
    const browser = new FakeBrowser();
    browser.slots.vaults = [work];
    // This tab hears nothing from the others.
    const deaf = { ...browser.tab(), channel: (copy: string) => ({ post: browser.bus.channel(copy).post, listen: () => () => {} }) };
    const copies = new MirrorCopies(deaf);
    const mirror = await copies.create(signIn);
    await mirror.adopt(work.id);
    const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    await delivered();
    browser.slots.vaults = [{ ...work, tag: "tag-b" }];
    await other.contact();
    await (await other.adopt(work.id)).files().writeIf("Plan.md", null, "Written after the re-key");
    browser.slots.vaults = [];
    await mirror.contact();
    expect(mirror.vault(work.id)).toBeUndefined();
    await delivered();
    mirror.lock();
    other.lock();
    expect((await copies.unlockOffline(mirror.header, signIn.password))!.vault(work.id)).toBeUndefined();
  });

  test("a tab whose own write found the vault adopted again takes up the new adoption at the next change it hears", async () => {
    const { browser, copies, mirror } = await setup([work]);
    await mirror.adopt(work.id);
    // This tab misses the purge, and hears the rest.
    const missing = {
      ...browser.tab(),
      channel: (copy: string): TabChannel => {
        const c = browser.bus.channel(copy);
        return { post: c.post, listen: (fn) => c.listen((m) => { if (m.type !== "purged") fn(m); }) };
      },
    };
    const other = (await new MirrorCopies(missing).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
    const old = other.vault(work.id)!;
    await mirror.purge(work.id, false, false);
    const fresh = await mirror.adopt(work.id);
    await expect(old.files().writeIf("Stale.md", null, "Written before hearing")).rejects.toThrow(MirrorLockedError);
    await fresh.files().writeIf("Plan.md", null, "Written after the new adoption");
    await vi.waitFor(() => expect(other.vault(work.id)?.entry("Plan.md")).toBeDefined());
    expect(other.vault(work.id)).not.toBe(old);
  });

  test("a copy locked while it hears of a purge leaves no rejection unhandled", async () => {
    const rejections: unknown[] = [];
    const onRejection = (e: unknown) => { rejections.push(e); };
    process.on("unhandledRejection", onRejection);
    try {
      const { browser, copies, mirror } = await setup([work]);
      await mirror.adopt(work.id);
      const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
      other.onDrop = () => { other.lock(); return Promise.resolve(); };
      await mirror.purge(work.id, false, false);
      await delivered();
      await new Promise((resolve) => setTimeout(resolve, 0));
      expect(other.locked).toBe(true);
      expect(rejections).toEqual([]);
    } finally {
      process.off("unhandledRejection", onRejection);
    }
  });

  test("a copy locked during a refresh leaves no rejection unhandled", async () => {
    const rejections: unknown[] = [];
    const onRejection = (e: unknown) => { rejections.push(e); };
    process.on("unhandledRejection", onRejection);
    try {
      const { browser, copies, mirror } = await setup([work]);
      const vault = await mirror.adopt(work.id);
      const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
      await delivered();
      const get = browser.storage.get.bind(browser.storage);
      let armed = false;
      browser.storage.get = async (k: string) => {
        const v = await get(k);
        if (armed && k === other.vault(work.id)?.metaKey) { armed = false; other.lock(); }
        return v;
      };
      // After this write only the other tab's refresh reads the meta.
      await vault.files().writeIf("Plan.md", null, "Written in this tab");
      armed = true;
      await delivered();
      await new Promise((resolve) => setTimeout(resolve, 0));
      expect(other.locked).toBe(true);
      expect(rejections).toEqual([]);
    } finally {
      process.off("unhandledRejection", onRejection);
    }
  });

  test("a copy still open leaves a failure while it handles a message unhidden", async () => {
    const rejections: unknown[] = [];
    const onRejection = (e: unknown) => { rejections.push(e); };
    process.on("unhandledRejection", onRejection);
    try {
      const { browser, copies, mirror } = await setup([work]);
      const vault = await mirror.adopt(work.id);
      const other = (await new MirrorCopies(browser.tab()).unlockOnline((await copies.forUser(signIn.userId))!)) as SealedMirror;
      await delivered();
      const get = browser.storage.get.bind(browser.storage);
      let armed = false;
      browser.storage.get = async (k: string) => {
        if (armed && k === other.vault(work.id)?.metaKey) { armed = false; throw new Error("The disk is full."); }
        return get(k);
      };
      await vault.files().writeIf("Plan.md", null, "Written in this tab");
      armed = true;
      await delivered();
      await new Promise((resolve) => setTimeout(resolve, 0));
      expect(other.locked).toBe(false);
      expect(rejections.map(String)).toEqual(["Error: The disk is full."]);
    } finally {
      process.off("unhandledRejection", onRejection);
    }
  });

  test("setMeta waits for the copy's lock, and writes nothing if the vault went while it waited", async () => {
    const { browser, mirror } = await setup([work]);
    const vault = await mirror.adopt(work.id);
    const other = browser.locks.hold(`kumbu-mirror:${mirror.header.copy}`);
    await other.acquired;
    const naming = vault.setMeta({ name: "Home" });
    await delivered();
    await browser.storage.write({ remove: [vault.metaKey], removePrefix: [vault.dataPrefix] });
    other.release();
    await naming;
    expect(metaRecords(browser)).toEqual([]);
  });

  test("setMeta still writes a held vault's name", async () => {
    const { copies, mirror } = await setup([work]);
    await (await mirror.adopt(work.id)).setMeta({ name: "Home" });
    mirror.lock();
    expect((await copies.unlockOffline(mirror.header, signIn.password))!.vault(work.id)?.name).toBe("Home");
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
