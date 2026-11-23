// SPDX-License-Identifier: AGPL-3.0-or-later
// Reading the mirror offline (offline mode design §3, work package 5): the tree from the inventory, notes and
// attachments from the copy, links resolved in it, vaults found by the name last seen online.
import { describe, expect, test } from "vitest";
import { SyncEngine } from "../src/engine/engine";
import { versionOf } from "../src/engine/version";
import { MirrorLinks } from "../src/read/links";
import { locate, NotOnDeviceError, readFile, readNote, treeOf, vaultByName } from "../src/read/tree";
import { IDLE_MS, MirrorCopies } from "../src/store/mirror";
import type { VaultMirror } from "../src/store/vault";
import { FakeServer } from "./fakes/fakeServer";
import { FakeBrowser, realClock } from "./fakeStore";

const work = { id: "vault-1f3a", tag: "tag-a", canWrite: true, pending: false };
const signIn = { userId: "user-8d1e", username: "Ada", password: "correct horse battery", label: "Firefox on Linux", idleMs: IDLE_MS, iterations: 1000 };
const png = (size: number) => { const b = new Uint8Array(size); b.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]); return b; };

/** A copy with Work mirrored from `server`: every note's text here, no attachment's bytes yet (design §3a's order). */
async function mirrored(server: FakeServer) {
  const browser = new FakeBrowser();
  browser.slots.vaults = [work];
  const mirror = await new MirrorCopies(browser.tab()).create(signIn);
  const vault = await mirror.adopt(work.id);
  await vault.setMeta({ name: "Work" });
  const engine = engineFor(vault, server);
  await engine.start();
  await engine.idle();
  return { mirror, vault, engine };
}

function engineFor(vault: VaultMirror, server: FakeServer) {
  return new SyncEngine({
    vault: "Work", device: "Firefox on Linux", local: vault.files(), remote: vault.inventory(server), state: vault.stateStore(), clock: realClock,
    onStatus: () => {}, onConflict: () => {}, onAuthLost: () => {}, fetchPolicy: vault.downloadOrder(() => {}),
  });
}

async function seeded() {
  const server = new FakeServer();
  await server.seed("projects/plan.md", "---\naliases: [Roadmap]\n---\n# Plan\nSee [[notes]] and ![[shot.png]].\n");
  await server.seed("projects/notes.md", "Back to [[Roadmap]], [[plan#Goals]], [[missing]].\n");
  await server.seed("Zebra.md", "z");
  await server.seed("apple.md", "a");
  await server.seed("_archive/old.md", "old");
  await server.seedFile("projects/shot.png", png(64));
  return server;
}

describe("the tree", () => {
  test("is whole from the inventory, folders first, in text order, without the archive", async () => {
    const { vault } = await mirrored(await seeded());
    const { root } = treeOf(vault, "Work");
    expect(root.path).toBe("/Work");
    expect(root.folders.map((f) => f.path)).toEqual(["/Work/projects"]);
    expect(root.notes.map((n) => n.name)).toEqual(["apple.md", "Zebra.md"]);
    const projects = root.folders[0]!;
    expect(projects.notes.map((n) => [n.name, n.kind, n.held])).toEqual([
      ["notes.md", "note", true], ["plan.md", "note", true], ["shot.png", "image", false],
    ]);
    expect(projects.notes[2]).toMatchObject({ path: "/Work/projects/shot.png", size: 64, mediaType: "image/png" });
    expect(projects.notes[2]!.version).toMatch(/^[0-9a-f]{12}$/);
  });

  test("leaves out what was removed on this device and isn't uploaded yet", async () => {
    const { vault } = await mirrored(await seeded());
    const files = vault.files();
    expect(await files.trashIf("apple.md", await versionOf("a"))).toBe(true);
    expect(treeOf(vault, "Work").root.notes.map((n) => n.name)).toEqual(["Zebra.md"]);
  });
});

describe("a note", () => {
  test("is read from the copy, with the version a write compares against", async () => {
    const { mirror } = await mirrored(await seeded());
    const note = await readNote(mirror, "/Work/Zebra.md");
    expect(note).toMatchObject({ path: "/Work/Zebra.md", content: "z", version: await versionOf("z"), current: true });
  });

  test("is null where the server would answer 404, also for a vault not held", async () => {
    const { mirror, vault } = await mirrored(await seeded());
    expect(await readNote(mirror, "/Work/nothing.md")).toBeNull();
    expect(await readNote(mirror, "/Archive/plan.md")).toBeNull();
    await vault.files().trashIf("apple.md", await versionOf("a"));
    expect(await readNote(mirror, "/Work/apple.md")).toBeNull();
  });

  test("finds its vault by the name last seen online, whatever its case", async () => {
    const { mirror, vault } = await mirrored(await seeded());
    expect(vaultByName(mirror, "work")).toBe(vault);
    expect(locate(mirror, "/WORK/projects/plan.md")).toEqual({ vault, inVault: "projects/plan.md" });
    await vault.setMeta({ name: "Job" });
    expect(vaultByName(mirror, "Work")).toBeUndefined();
    expect((await readNote(mirror, "/Job/Zebra.md"))?.content).toBe("z");
  });
});

describe("an attachment", () => {
  test("not fetched yet says so; fetched, its bytes come from the copy", async () => {
    const server = await seeded();
    const { mirror, engine } = await mirrored(server);
    await expect(readFile(mirror, "/Work/projects/shot.png")).rejects.toBeInstanceOf(NotOnDeviceError);
    await engine.check(true);
    await engine.idle();
    expect((await readFile(mirror, "/Work/projects/shot.png"))?.byteLength).toBe(64);
    expect(await readFile(mirror, "/Work/projects/other.png")).toBeNull();
  });
});

describe("links", () => {
  test("resolve in the copy as the server resolves them: names, aliases, headings, attachments, missing ones", async () => {
    const { mirror } = await mirrored(await seeded());
    const links = new MirrorLinks(mirror);
    const notes = await readNote(mirror, "/Work/projects/notes.md");
    expect(await links.of("/Work/projects/notes.md", notes!.content)).toEqual([
      { line: 1, raw: "[[Roadmap]]", target: "Roadmap", kind: "note", path: "/Work/projects/plan.md", candidates: [] },
      { line: 1, raw: "[[plan#Goals]]", target: "plan", kind: "note", path: "/Work/projects/plan.md", candidates: [] },
      { line: 1, raw: "[[missing]]", target: "missing", kind: "not_found", path: null, candidates: [] },
    ]);
    const plan = await readNote(mirror, "/Work/projects/plan.md");
    expect((await links.of("/Work/projects/plan.md", plan!.content)).map((l) => [l.kind, l.path])).toEqual([
      ["note", "/Work/projects/notes.md"], ["attachment", "/Work/projects/shot.png"],
    ]);
  });

  test("follow a note written on this device", async () => {
    const { mirror, vault } = await mirrored(await seeded());
    const links = new MirrorLinks(mirror);
    expect((await links.of("/Work/apple.md", "[[fresh]]"))[0]?.kind).toBe("not_found");
    await vault.files().writeIf("fresh.md", null, "new");
    expect((await links.of("/Work/apple.md", "[[fresh]]"))[0]).toMatchObject({ kind: "note", path: "/Work/fresh.md" });
  });
});
