// SPDX-License-Identifier: AGPL-3.0-or-later
// Writing into the copy (offline mode design §3, work package 7): the editor's saves, new notes and trashing, the
// rights of a read-only member, the editor's unsaved text as the copy's Drafts, and the upload once online.
import { describe, expect, test } from "vitest";
import { SyncEngine } from "../src/engine/engine";
import { versionOf } from "../src/engine/version";
import { IDLE_MS } from "../src/store/mirror";
import { MirrorCopies } from "../src/store/mirror";
import type { VaultMirror } from "../src/store/vault";
import { EditorDrafts, NotHereError, NoteWriter, ReadOnlyError } from "../src/store/writing";
import { FakeServer } from "./fakes/fakeServer";
import { delivered, FakeBrowser, realClock } from "./fakeStore";

const work = { id: "vault-1f3a", tag: "tag-a", canWrite: true, pending: false };
const team = { id: "vault-77c2", tag: "tag-b", canWrite: false, pending: false };
const signIn = { userId: "user-8d1e", username: "Ada", password: "correct horse battery", label: "Firefox on Linux", idleMs: IDLE_MS, iterations: 1000 };

async function setup() {
  const browser = new FakeBrowser();
  browser.slots.vaults = [work, team];
  const mirror = await new MirrorCopies(browser.tab()).create(signIn);
  const server = new FakeServer();
  return { browser, mirror, server };
}

function engineFor(vault: VaultMirror, server: FakeServer, drafts?: EditorDrafts) {
  return new SyncEngine({
    vault: "Work", device: "Firefox on Linux", local: vault.files(drafts), remote: vault.inventory(server), state: vault.stateStore(), clock: realClock,
    onStatus: () => {}, onConflict: () => {}, onAuthLost: () => {}, fetchPolicy: vault.downloadOrder(() => {}),
  });
}

/** A vault mirrored once while online, then the engine stopped: offline from here on. */
async function synced(seed: Record<string, string>) {
  const s = await setup();
  for (const [path, text] of Object.entries(seed)) await s.server.seed(path, text);
  const vault = await s.mirror.adopt(work.id);
  const engine = engineFor(vault, s.server);
  await engine.start();
  await engine.idle();
  await engine.stop();
  const written: string[] = [];
  const writer = new NoteWriter(vault, (path) => written.push(path));
  return { ...s, vault, writer, written };
}

describe("saving offline", () => {
  test("writes on top of the version the editor had, and answers the new one", async () => {
    const { vault, writer, written } = await synced({ "plan.md": "one" });
    const result = await writer.save("plan.md", "two", await versionOf("one"));
    expect(result).toEqual({ saved: { version: await versionOf("two") } });
    expect(await vault.files().read("plan.md")).toBe("two");
    expect(vault.waiting()).toBe(1);
    expect(written).toEqual(["plan.md"]);
  });

  test("a save on an older version answers the text there now, as a 409 does, and writes nothing", async () => {
    const { vault, writer, written } = await synced({ "plan.md": "one" });
    await writer.save("plan.md", "from the other tab", await versionOf("one"));
    const result = await writer.save("plan.md", "mine", await versionOf("one"));
    expect(result).toEqual({ conflict: { content: "from the other tab", version: await versionOf("from the other tab") } });
    expect(await vault.files().read("plan.md")).toBe("from the other tab");
    expect(written).toEqual(["plan.md"]);
  });

  test("checks against what is stored, not the editor's own unsaved text", async () => {
    const s = await synced({ "plan.md": "one" });
    const drafts = new EditorDrafts();
    drafts.open("plan.md", { text: () => "typing on", replaced: () => {} });
    const writer = new NoteWriter(s.vault, () => {});
    expect(await writer.save("plan.md", "typing on", await versionOf("one"))).toEqual({ saved: { version: await versionOf("typing on") } });
  });

  test("a note listed but not on this device yet can't be saved", async () => {
    const { vault, writer } = await synced({});
    await vault.change(["big.md"], () => Promise.resolve({ entry: { path: "big.md", remote: { version: "aaaaaaaaaaaa", kind: "note" } } }));
    await expect(writer.save("big.md", "x", "aaaaaaaaaaaa")).rejects.toBeInstanceOf(NotHereError);
  });

  test("a read-only member writes nothing, not even into the copy", async () => {
    const { mirror } = await synced({});
    const vault = await mirror.adopt(team.id);
    const writer = new NoteWriter(vault, () => {});
    await expect(writer.save("plan.md", "x", "aaaaaaaaaaaa")).rejects.toBeInstanceOf(ReadOnlyError);
    await expect(writer.create("new.md", "# ")).rejects.toBeInstanceOf(ReadOnlyError);
    await expect(writer.trash("plan.md")).rejects.toBeInstanceOf(ReadOnlyError);
    expect(writer.canWrite).toBe(false);
  });
});

describe("new notes offline", () => {
  test("are made in the copy and wait to upload", async () => {
    const { vault, writer, written } = await synced({});
    expect(await writer.create("projects/Idea.md", "# ")).toEqual({ version: await versionOf("# ") });
    expect(vault.entry("projects/Idea.md")?.local?.text).toBe(true);
    expect(vault.waiting()).toBe(1);
    expect(written).toEqual(["projects/Idea.md"]);
  });

  test("never over a note there, on this device or only listed", async () => {
    const { vault, writer } = await synced({ "plan.md": "one" });
    await vault.change(["big.md"], () => Promise.resolve({ entry: { path: "big.md", remote: { version: "aaaaaaaaaaaa", kind: "note" } } }));
    expect(await writer.create("plan.md", "# ")).toBeNull();
    expect(await writer.create("big.md", "# ")).toBeNull();
    expect(await vault.files().read("plan.md")).toBe("one");
  });

  test("only as notes at paths Kumbu² takes", async () => {
    const { writer } = await synced({});
    for (const path of ["", "/abs.md", "a//b.md", "../up.md", ".obsidian/x.md", "notes/.hidden.md", "photo.png", "plain.txt", "dir/"]) {
      await expect(writer.create(path, "x"), path).rejects.toThrow();
    }
  });
});

describe("trashing offline", () => {
  test("removes the note from the copy, and the upload trashes it in Kumbu²", async () => {
    const { vault, writer, written, server } = await synced({ "old.md": "bye" });
    expect(await writer.trash("old.md")).toBe(true);
    expect(vault.entry("old.md")?.local).toBeUndefined();
    expect(vault.waiting()).toBe(1);
    expect(written).toEqual(["old.md"]);

    const engine = engineFor(vault, server);
    await engine.start();
    await engine.idle();
    expect(server.notes.has("old.md")).toBe(false);
    expect(vault.waiting()).toBe(0);
    await engine.stop();
  });

  test("a note made offline and trashed again leaves nothing to upload", async () => {
    const { vault, writer } = await synced({});
    await writer.create("tmp.md", "# ");
    expect(await writer.trash("tmp.md")).toBe(true);
    expect(vault.waiting()).toBe(0);
    expect(vault.entry("tmp.md")).toBeUndefined();
  });

  test("a note not on this device can't be trashed here", async () => {
    const { vault, writer } = await synced({});
    await vault.change(["big.md"], () => Promise.resolve({ entry: { path: "big.md", remote: { version: "aaaaaaaaaaaa", kind: "note" } } }));
    await expect(writer.trash("big.md")).rejects.toBeInstanceOf(NotHereError);
    expect(await writer.trash("none.md")).toBe(false);
  });
});

describe("back online", () => {
  test("what was written offline uploads; a note changed in Kumbu² meanwhile keeps both texts", async () => {
    const { vault, writer, server } = await synced({ "plan.md": "one", "list.md": "a" });
    await writer.save("plan.md", "mine", await versionOf("one"));
    await writer.save("list.md", "a b", await versionOf("a"));
    await writer.create("new.md", "# New");
    await server.agentWrite("plan.md", "theirs");

    const conflicts: string[] = [];
    const engine = new SyncEngine({
      vault: "Work", device: "Firefox on Linux", local: vault.files(), remote: vault.inventory(server), state: vault.stateStore(), clock: realClock,
      onStatus: () => {}, onConflict: (path) => conflicts.push(path), onAuthLost: () => {}, fetchPolicy: vault.downloadOrder(() => {}),
    });
    await engine.start();
    await engine.idle();
    expect(server.notes.get("list.md")?.content).toBe("a b");
    expect(server.notes.get("new.md")?.content).toBe("# New");
    expect(server.notes.get("plan.md")?.content).toBe("theirs");
    expect(await vault.files().read("plan.md")).toBe("theirs");
    const copy = engine.status().conflicts[0]!;
    expect(copy).toMatch(/^plan \(conflict .*\)\.md$/);
    expect(await vault.files().read(copy)).toContain("mine");
    expect(conflicts).toHaveLength(1);
    await engine.stop();
  });
});

describe("the editor's unsaved text", () => {
  test("is what the engine reads, so a download never takes it away", async () => {
    const { vault, server } = await synced({ "plan.md": "one" });
    const drafts = new EditorDrafts();
    const replaced: (string | null)[] = [];
    drafts.open("plan.md", { text: () => "one, and typing", replaced: (c) => replaced.push(c) });
    const files = vault.files(drafts);
    expect(await files.read("plan.md")).toBe("one, and typing");

    await server.agentWrite("plan.md", "changed in Kumbu²");
    const engine = engineFor(vault, server, drafts);
    await engine.start();
    await engine.idle();
    // The engine saw text other than its base: a conflict copy, and Kumbu²'s text kept its path.
    expect(engine.status().conflicts).toHaveLength(1);
    await engine.stop();
  });

  test("an editor that closed has none, and a second one for the same path replaces the first", () => {
    const drafts = new EditorDrafts();
    const close = drafts.open("a.md", { text: () => "first", replaced: () => {} });
    drafts.open("a.md", { text: () => "second", replaced: () => {} });
    expect(drafts.text("a.md")).toBe("second");
    close();
    expect(drafts.text("a.md")).toBe("second");
    expect(drafts.text("b.md")).toBeNull();
  });

  test("hears when a download replaced the file under it", async () => {
    const drafts = new EditorDrafts();
    const seen: (string | null)[] = [];
    const close = drafts.open("a.md", { text: () => null, replaced: (c) => seen.push(c) });
    drafts.replaced("a.md", "new");
    drafts.replaced("b.md", "other");
    close();
    drafts.replaced("a.md", null);
    await delivered();
    expect(seen).toEqual(["new"]);
  });
});
