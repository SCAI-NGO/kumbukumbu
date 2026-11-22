// SPDX-License-Identifier: AGPL-3.0-or-later
// A refused change goes again when the rights change, on a timer while one is listed, and at the next start.
import { afterEach, expect, test, vi } from "vitest";
import { REFUSED_RETRY_MS, SyncEngine } from "../src/engine/engine";
import { fakeClock, harness, settle } from "./fakes/harness";

afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); });

const READ_ONLY = "write: this key can only read vault Work";

const refusedEdit = async () => {
  const h = await harness({ synced: { "plan.md": "one" } });
  h.server.level = "read";
  await h.engine.check(false);
  h.server.refuse = () => READ_ONLY;
  h.files.userEdit("plan.md", "mine");
  h.engine.localChanged("plan.md");
  await settle(h, 2000);
  expect(h.engine.status().notSynced).toEqual([{ path: "plan.md", reason: "rejected", detail: READ_ONLY }]);
  expect(h.server.count("put")).toBe(1);
  return h;
};

test("a check that finds the level changed sends the refused change again", async () => {
  const h = await refusedEdit();
  h.server.refuse = null;
  h.server.level = "write";
  await h.engine.check(false);
  expect(h.server.notes.get("plan.md")!.content).toBe("mine");
  expect(h.engine.status().notSynced).toEqual([]);
  await h.engine.stop();
});

test("a check at the same level sends nothing again", async () => {
  const h = await refusedEdit();
  await h.engine.check(false);
  await h.engine.check(true);
  expect(h.server.count("put")).toBe(1);
  await h.engine.stop();
});

test("the timer sends it again, and goes on while the server still refuses", async () => {
  const h = await refusedEdit();
  await settle(h, REFUSED_RETRY_MS);
  expect(h.server.count("put")).toBe(2);
  expect(h.engine.status().notSynced).toEqual([{ path: "plan.md", reason: "rejected", detail: READ_ONLY }]);
  h.server.refuse = null;
  await settle(h, REFUSED_RETRY_MS);
  expect(h.server.notes.get("plan.md")!.content).toBe("mine");
  expect(h.engine.status().notSynced).toEqual([]);
  await h.engine.stop();
});

test("a remote change meanwhile keeps both texts", async () => {
  const h = await refusedEdit();
  await h.server.agentWrite("plan.md", "theirs");
  h.server.refuse = null;
  await settle(h, REFUSED_RETRY_MS);
  expect(h.files.files.get("plan.md")).toBe("theirs");
  expect([...h.files.files.values()].some((text) => text.includes("mine"))).toBe(true);
  await h.engine.stop();
});

test("without anything refused there is no timer", async () => {
  const h = await harness({ synced: { "plan.md": "one" } });
  const asked = h.server.count("versions");
  await settle(h, REFUSED_RETRY_MS * 2);
  expect(h.server.count("versions")).toBe(asked);
  await h.engine.stop();
});

test("stopping ends the timer", async () => {
  const h = await refusedEdit();
  await h.engine.stop();
  await vi.advanceTimersByTimeAsync(REFUSED_RETRY_MS * 2);
  expect(h.server.count("put")).toBe(1);
});

test("a start sends it again", async () => {
  const h = await refusedEdit();
  await h.engine.stop();
  h.server.refuse = null;
  const next = new SyncEngine({
    vault: "Work", device: "Laptop", local: h.files, remote: h.server, state: h.state, clock: fakeClock,
    onStatus: () => undefined, onConflict: () => undefined, onAuthLost: () => undefined,
  });
  await next.start();
  expect(h.server.notes.get("plan.md")!.content).toBe("mine");
  expect(next.status().notSynced).toEqual([]);
  await next.stop();
});
