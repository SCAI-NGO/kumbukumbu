// SPDX-License-Identifier: AGPL-3.0-or-later
// The first anti-goal (spec §1): no stalls, no missed updates, no conflicts that aren't real.
import { afterEach, expect, test, vi } from "vitest";
import { eligibility } from "../src/engine/paths";
import type { FakeFiles } from "./fakes/fakeFiles";
import type { FakeServer } from "./fakes/fakeServer";
import { harness, settle, waitFor, type Harness } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); });

const isCopy = (p: string) => p.includes("(conflict ");

/** Both sides hold the same notes with the same text, and there are exactly `copies` conflict copies. */
function expectConverged(server: FakeServer, files: FakeFiles, copies = 0): void {
  const local = new Map([...files.files].filter(([p]) => eligibility(p) === "sync").map(([p, t]) => [p.normalize("NFC"), t]));
  const remote = new Map([...server.notes].map(([p, n]) => [p, n.content]));
  const onlyLocal = [...local.keys()].filter((p) => !remote.has(p));
  const onlyRemote = [...remote.keys()].filter((p) => !local.has(p));
  const differ = [...local.keys()].filter((p) => remote.has(p) && remote.get(p) !== local.get(p));
  expect({ onlyLocal, onlyRemote, differ }).toEqual({ onlyLocal: [], onlyRemote: [], differ: [] });
  expect([...local.keys()].filter(isCopy)).toHaveLength(copies);
}

/** The user's side as Obsidian reports it: change the file, then fire the event. */
const user = (h: Harness) => ({
  edit(path: string, text: string) { h.files.userEdit(path, text); h.engine.localChanged(path); },
  remove(path: string) { h.files.userDelete(path); h.engine.localDeleted(path); },
  rename(from: string, to: string) { h.files.userRename(from, to); h.engine.localRenamed(from, to); },
});

async function quiet(h: Harness): Promise<void> {
  await settle(h, 2500);
  await h.engine.check(false);
  await settle(h);
}

test("a keystroke between the engine's last read and trashing a note deleted in Kumbu² is uploaded, not trashed", async () => {
  const h = await harness({ synced: { "a.md": "a0" } });
  await h.server.agentWrite("a.md", null);
  let typed = false;
  h.files.beforeTrash = (p) => {
    if (typed) return;
    typed = true;
    h.files.userTypes(p, "a0 typed just now");
  };
  await h.engine.check(false);
  await settle(h);
  expect(typed).toBe(true);
  expect(h.files.systemTrash).toEqual([]);
  expect(h.files.editors.get("a.md")).toBe("a0 typed just now");
  expect(h.server.notes.get("a.md")?.content).toBe("a0 typed just now");
});

test("an upload in flight while a hint arrives", async () => {
  const h = await harness({ synced: { "a.md": "one", "b.md": "b" } });
  const release = h.server.hold("put");
  user(h).edit("a.md", "two");
  await vi.advanceTimersByTimeAsync(2000);
  await waitFor(() => h.server.count("put") > 0);
  await h.server.agentWrite("b.md", "agent b");
  const hint = h.engine.check(false);
  release();
  await hint;
  await quiet(h);
  expectConverged(h.server, h.files);
  expect(h.files.files.get("a.md")).toBe("two");
  expect(h.files.files.get("b.md")).toBe("agent b");
});

test("hints arriving twice and not at all", async () => {
  const h = await harness({ synced: { "a.md": "one" } });
  const release = h.server.hold("versions");
  const burst = Array.from({ length: 20 }, () => h.engine.check(false));
  release();
  await Promise.all(burst);
  expect(h.server.count("versions")).toBeLessThanOrEqual(2);

  // No hint at all: the hourly full check (Task 8 calls it) still converges.
  await h.server.agentWrite("a.md", "missed hint");
  await h.server.agentWrite("new.md", "also missed");
  await h.engine.check(true);
  expectConverged(h.server, h.files);
});

test("an agent edit and a user edit of different notes at the same time", async () => {
  const h = await harness({ synced: { "a.md": "a", "b.md": "b" } });
  user(h).edit("a.md", "user a");
  await h.server.agentWrite("b.md", "agent b");
  const hint = h.engine.check(false);
  await quiet(h);
  await hint;
  expectConverged(h.server, h.files);
  expect(h.server.notes.get("a.md")!.content).toBe("user a");
  expect(h.files.files.get("b.md")).toBe("agent b");
});

test("an agent edit while the user's typing is only in the editor: a conflict copy, never an overwrite", async () => {
  // The demo's step 3: Chris types in Car.md, Claude moves the appointment, the hint comes before Obsidian saves.
  const h = await harness({ synced: { "Car.md": "Car at 14:00" } });
  h.files.userTypes("Car.md", "Car at 14:00, bring the papers");
  await h.server.agentWrite("Car.md", "Car at 16:00");
  await h.engine.check(false);
  expect(h.files.files.get("Car.md")).toBe("Car at 16:00");
  const copy = [...h.files.files.keys()].find(isCopy)!;
  expect(h.files.files.get(copy)).toContain("bring the papers");
  expect(h.server.notes.get(copy)!.content).toContain("bring the papers");
});

test("an agent edit of the note the user is typing in", async () => {
  const h = await harness({ synced: { "a.md": "one" } });
  user(h).edit("a.md", "typing");
  await vi.advanceTimersByTimeAsync(1000);
  await h.server.agentWrite("a.md", "agent");
  void h.engine.check(false);
  user(h).edit("a.md", "typing more");
  await quiet(h);
  expectConverged(h.server, h.files, 1);
  expect(h.files.files.get("a.md")).toBe("agent");
  const copy = [...h.files.files.keys()].find(isCopy)!;
  expect(h.files.files.get(copy)).toContain("typing more");
  expect(h.conflicts).toHaveLength(1);
});

test("a rename on one side while the other edits", async () => {
  const h = await harness({ synced: { "a.md": "one" } });
  await h.server.agentWrite("a.md", "agent edit");
  user(h).rename("a.md", "b.md");
  await quiet(h);
  expectConverged(h.server, h.files);
  // The agent's edit moved with the note and reached the device: nothing lost.
  expect(h.files.files.get("b.md")).toBe("agent edit");
});

test("a rename and an edit on the device while the agent edits the old path", async () => {
  const h = await harness({ synced: { "a.md": "one" } });
  await h.server.agentWrite("a.md", "agent edit");
  user(h).rename("a.md", "b.md");
  user(h).edit("b.md", "user edit");
  await quiet(h);
  // Both changed the same note: a real conflict, and both texts survive.
  expectConverged(h.server, h.files, 1);
  const texts = [...h.files.files.values()];
  expect(texts).toContain("agent edit");
  expect(texts.some((t) => t.includes("user edit"))).toBe(true);
});

test("a delete racing an edit", async () => {
  const h = await harness({ synced: { "a.md": "one" } });
  await h.server.agentWrite("a.md", "agent edit");
  user(h).remove("a.md");
  await quiet(h);
  expectConverged(h.server, h.files);
  expect(h.files.files.get("a.md")).toBe("agent edit");
});

test("the since answer arrives before a write it overlaps", async () => {
  const h = await harness({ synced: { "a.md": "one" } });
  await vi.advanceTimersByTimeAsync(60_000);
  await h.engine.check(false);
  // Stamped a second before the cursor this check got, committed only now.
  await vi.advanceTimersByTimeAsync(10_000);
  await h.server.agentWrite("a.md", "late commit", Date.now() - 11_000);
  await h.engine.check(false);
  expectConverged(h.server, h.files);
  expect(h.files.files.get("a.md")).toBe("late commit");
});

/** mulberry32: small, seeded, the same on every machine. */
function prng(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

test("randomised: 200 seeded rounds of mixed user and agent operations converge with no unexpected conflict copies", async () => {
  const seed = Number(process.env.RACE_SEED ?? 20261027);
  const random = prng(seed);
  const pick = <T,>(xs: T[]): T => xs[Math.floor(random() * xs.length)]!;
  const names = ["a", "b", "c", "d", "e", "f", "g", "h"].map((n) => `${n}.md`);
  const h = await harness({ synced: { "a.md": "a0", "b.md": "b0", "c.md": "c0" } });
  const u = user(h);
  let round = 0;
  let log: string[] = [];
  try {
    for (round = 0; round < 200; round++) {
      log = [];
      const userTouched = new Set<string>();
      const agentTouched = new Set<string>();
      const conflictsBefore = h.conflicts.length;
      const ops = 1 + Math.floor(random() * 4);
      for (let i = 0; i < ops; i++) {
        const r = random();
        const localNotes = [...h.files.files.keys()].filter((p) => !isCopy(p));
        const remoteNotes = [...h.server.notes.keys()].filter((p) => !isCopy(p));
        if (r < 0.3) {
          const p = pick(names);
          log.push(`user edits ${p}`);
          u.edit(p, `user ${round}.${i}`);
          userTouched.add(p);
        } else if (r < 0.4 && localNotes.length > 0) {
          const p = pick(localNotes);
          log.push(`user deletes ${p}`);
          u.remove(p);
          userTouched.add(p);
        } else if (r < 0.5 && localNotes.length > 0) {
          const from = pick(localNotes);
          const to = pick(names.filter((n) => !h.files.files.has(n)));
          if (to !== undefined) {
            log.push(`user renames ${from} to ${to}`);
            u.rename(from, to);
            userTouched.add(from).add(to);
          }
        } else if (r < 0.8) {
          const p = pick(names);
          log.push(`agent writes ${p}`);
          await h.server.agentWrite(p, `agent ${round}.${i}`);
          agentTouched.add(p);
        } else if (r < 0.9 && remoteNotes.length > 0) {
          const p = pick(remoteNotes);
          log.push(`agent deletes ${p}`);
          await h.server.agentWrite(p, null);
          agentTouched.add(p);
        } else {
          log.push("hint");
          void h.engine.check(false);
        }
        const wait = Math.floor(random() * 3000);
        log.push(`wait ${wait} ms`);
        await vi.advanceTimersByTimeAsync(wait);
      }
      if (random() < 0.9) void h.engine.check(false);
      await quiet(h);
      if (round % 10 === 9) await h.engine.check(true);
      for (const [original] of h.conflicts.slice(conflictsBefore)) {
        expect(userTouched.has(original) && agentTouched.has(original), `unexpected conflict copy for ${original}`).toBe(true);
      }
      // Copies are never edited or deleted in this run, so every one made is still there, and no other.
      expectConverged(h.server, h.files, h.conflicts.length);
      expect(h.engine.status().mode).not.toBe("offline");
    }
  } catch (e) {
    throw new Error(`randomised race run failed in round ${round} with RACE_SEED=${seed} after ${log.join(", ")}: ${e instanceof Error ? e.message : String(e)} ${JSON.stringify((e as { actual?: unknown }).actual ?? null)}`);
  }
}, 60_000);

test("the original deleted while its conflict copy is written: the copy still reaches the server", async () => {
  const h = await harness({ synced: { "a.md": "one" } });
  await h.server.agentWrite("a.md", "agent");
  h.files.beforeWrite = (path) => {
    if (isCopy(path)) {
      h.files.beforeWrite = null;
      h.files.userDelete("a.md");
    }
  };
  user(h).edit("a.md", "mine");
  await settle(h, 2000);
  const copy = [...h.files.files.keys()].find(isCopy)!;
  expect(h.server.notes.get(copy)?.content).toContain("mine");
  await quiet(h);
  expectConverged(h.server, h.files, 1);
});

test("typing between writing the conflict copy and replacing the original updates that copy, not a second one", async () => {
  const h = await harness({ synced: { "a.md": "one" } });
  await h.server.agentWrite("a.md", "agent");
  h.files.beforeWrite = (path) => {
    if (isCopy(path)) {
      h.files.beforeWrite = null;
      h.files.userEdit("a.md", "mine plus more");
    }
  };
  user(h).edit("a.md", "mine");
  await settle(h, 2000);
  await quiet(h);
  const copies = [...h.files.files.keys()].filter(isCopy);
  expect(copies).toHaveLength(1);
  expect(h.files.files.get(copies[0]!)).toContain("mine plus more");
  expect(h.files.files.get("a.md")).toBe("agent");
  expectConverged(h.server, h.files, 1);
});

test("randomised: concurrent rounds of user and agent edits never lose an edit", async () => {
  const seed = Number(process.env.RACE_SEED ?? 20261028);
  const random = prng(seed);
  const pick = <T,>(xs: T[]): T => xs[Math.floor(random() * xs.length)]!;
  const names = ["a", "b", "c", "d", "e"].map((n) => `${n}.md`);
  const h = await harness({ synced: { "a.md": "a0", "b.md": "b0", "c.md": "c0" } });
  const u = user(h);
  // Every text either side wrote must survive somewhere, unless a side replaced or deleted it while it could see
  // it: the user what is in the local file, the agent what is on the server.
  const mustSurvive = new Set<string>();
  const seen = (content: string | undefined) => {
    for (const t of [...mustSurvive]) if (content?.includes(t)) mustSurvive.delete(t);
  };
  let round = 0;
  let log: string[] = [];
  try {
    for (round = 0; round < 150; round++) {
      log = [];
      const ops = 1 + Math.floor(random() * 4);
      for (let i = 0; i < ops; i++) {
        const r = random();
        const localNotes = [...h.files.files.keys()].filter((p) => !isCopy(p));
        const remoteNotes = [...h.server.notes.keys()].filter((p) => !isCopy(p));
        if (r < 0.35) {
          const p = pick(names);
          const text = `«user ${round}.${i}»`;
          log.push(`user edits ${p}`);
          seen(h.files.files.get(p));
          u.edit(p, text);
          mustSurvive.add(text);
        } else if (r < 0.45 && localNotes.length > 0) {
          const p = pick(localNotes);
          log.push(`user deletes ${p}`);
          seen(h.files.files.get(p));
          u.remove(p);
        } else if (r < 0.55 && localNotes.length > 0) {
          const from = pick(localNotes);
          const to = names.find((n) => !h.files.files.has(n));
          if (to !== undefined) {
            log.push(`user renames ${from} to ${to}`);
            u.rename(from, to);
          }
        } else if (r < 0.9) {
          const p = pick(names);
          const text = `«agent ${round}.${i}»`;
          log.push(`agent writes ${p}`);
          // A blind write: what it replaces is what was there when it landed, which the agent overwrote.
          seen(await h.server.agentWrite(p, text));
          mustSurvive.add(text);
        } else if (remoteNotes.length > 0) {
          const p = pick(remoteNotes);
          log.push(`agent deletes ${p}`);
          seen(await h.server.agentWrite(p, null));
        }
        // Short waits only: edits in a round are concurrent (only a delete syncs at once, without a debounce).
        await vi.advanceTimersByTimeAsync(Math.floor(random() * 300));
      }
      await quiet(h);
      const contents = [...h.files.files.values()];
      for (const text of mustSurvive) expect(contents.some((c) => c.includes(text)), `lost ${text}`).toBe(true);
      expectConverged(h.server, h.files, h.conflicts.length);
    }
  } catch (e) {
    const dump = (m: Iterable<[string, string]>) => [...m].map(([p, t]) => `${p}=${t.replace(/[\s\S]*\n\n/, "copy:")}`).join(" | ");
    throw new Error(`no-loss run failed in round ${round} with RACE_SEED=${seed} after ${log.join(", ")}: ${e instanceof Error ? e.message : String(e)}`
      + `\nlocal: ${dump(h.files.files)}\nserver: ${dump([...h.server.notes].map(([p, n]) => [p, n.content]))}`);
  }
}, 60_000);
