// SPDX-License-Identifier: AGPL-3.0-or-later
// The multi-vault host (offline mode design §2, gap 1): one engine per mirrored vault, keyed by its id.
import { describe, expect, test } from "vitest";
import type { ListedVault } from "../src/engine/ports";
import { VaultHost, type MirrorEngine, type VaultRef } from "../src/host/vaults";

class FakeEngine implements MirrorEngine {
  running = false;
  checks: boolean[] = [];
  constructor(readonly vault: VaultRef) {}
  start(): Promise<void> { this.running = true; return Promise.resolve(); }
  stop(): Promise<void> { this.running = false; return Promise.resolve(); }
  check(full: boolean): Promise<void> { this.checks.push(full); return Promise.resolve(); }
}

function setup(o: { listed: ListedVault[]; held?: string[]; mirrors?: (v: VaultRef) => boolean }) {
  const made: FakeEngine[] = [];
  const left: [string, string][] = [];
  const world = { listed: o.listed, held: o.held ?? [], mirrors: o.mirrors ?? (() => true), offline: false };
  const host = new VaultHost({
    listVaults: () => (world.offline ? Promise.reject(new Error("offline")) : Promise.resolve(world.listed)),
    held: () => world.held,
    mirrors: (v) => world.mirrors(v),
    createEngine: (v) => { const e = new FakeEngine(v); made.push(e); return e; },
    // The host purges the mirror, so it is held no more.
    onLeft: (id, why) => { left.push([id, why]); world.held = world.held.filter((h) => h !== id); },
  });
  const running = () => made.filter((e) => e.running).map((e) => `${e.vault.id}:${e.vault.name}`);
  return { host, made, left, world, running };
}

const work: ListedVault = { id: "v1", name: "Work", canWrite: true };
const team: ListedVault = { id: "v2", name: "Team", canWrite: false, shared: true, level: "read" };

describe("the vault host", () => {
  test("starts one engine per mirrored vault, with its id and its name", async () => {
    const s = setup({ listed: [work, team] });
    await s.host.refresh();
    expect(s.running()).toEqual(["v1:Work", "v2:Team"]);
    expect(s.made.map((e) => e.vault)).toEqual([{ id: "v1", name: "Work", canWrite: true }, { id: "v2", name: "Team", canWrite: false }]);
    expect(s.host.engine("v2")).toBe(s.made[1]);
  });

  test("a second refresh with nothing new changes nothing", async () => {
    const s = setup({ listed: [work] });
    await s.host.refresh();
    await s.host.refresh();
    expect(s.made).toHaveLength(1);
    expect(s.running()).toEqual(["v1:Work"]);
  });

  test("leaves out the vaults this device doesn't mirror", async () => {
    const s = setup({ listed: [work, team], mirrors: (v) => v.id === "v1" });
    await s.host.refresh();
    expect(s.running()).toEqual(["v1:Work"]);
    s.world.mirrors = () => false;
    await s.host.refresh();
    expect(s.running()).toEqual([]);
    expect(s.left).toEqual([]);
  });

  test("a vault renamed while away gets an engine under its new name, keyed by the same id", async () => {
    const s = setup({ listed: [work] });
    await s.host.refresh();
    s.world.listed = [{ ...work, name: "Job" }];
    await s.host.refresh();
    expect(s.running()).toEqual(["v1:Job"]);
    expect((s.host.engine("v1") as FakeEngine).vault).toEqual({ id: "v1", name: "Job", canWrite: true });
  });

  test("a vault no longer listed is stopped and reported gone, also one only held from an earlier visit", async () => {
    const s = setup({ listed: [work, team], held: ["v1", "v2", "v9"] });
    await s.host.refresh();
    expect(s.left).toEqual([["v9", "gone"]]);
    s.world.listed = [work];
    await s.host.refresh();
    expect(s.running()).toEqual(["v1:Work"]);
    expect(s.left).toEqual([["v9", "gone"], ["v2", "gone"]]);
    expect(s.host.engine("v2")).toBeUndefined();
  });

  test("a discreet vault is never mirrored, and one that becomes discreet is stopped and reported", async () => {
    const s = setup({ listed: [work, { ...team, discreet: true }] });
    await s.host.refresh();
    expect(s.running()).toEqual(["v1:Work"]);
    expect(s.left).toEqual([]);
    s.world.listed = [{ ...work, discreet: true }];
    await s.host.refresh();
    expect(s.running()).toEqual([]);
    expect(s.left).toEqual([["v1", "discreet"]]);
  });

  test("a discreet vault held from an earlier visit is reported, though no engine ran for it", async () => {
    const s = setup({ listed: [{ ...work, discreet: true }], held: ["v1"] });
    await s.host.refresh();
    expect(s.left).toEqual([["v1", "discreet"]]);
  });

  test("a vault listed without an id (a server before 0.6.0) is not mirrored", async () => {
    const s = setup({ listed: [{ name: "Old", canWrite: true }] });
    await s.host.refresh();
    expect(s.made).toEqual([]);
  });

  test("a hint by name reaches the engine of the vault that has that name now", async () => {
    const s = setup({ listed: [work, team] });
    await s.host.refresh();
    s.host.hinted("Team");
    s.host.hinted("Nobody");
    expect(s.made[1]!.checks).toEqual([false]);
    expect(s.made[0]!.checks).toEqual([]);
  });

  test("a failed list keeps every engine running", async () => {
    const s = setup({ listed: [work] });
    await s.host.refresh();
    s.world.offline = true;
    await expect(s.host.refresh()).rejects.toThrow("offline");
    expect(s.running()).toEqual(["v1:Work"]);
    expect(s.left).toEqual([]);
  });

  test("refreshes run one at a time, so two at once start one engine", async () => {
    const s = setup({ listed: [work] });
    await Promise.all([s.host.refresh(), s.host.refresh()]);
    expect(s.made).toHaveLength(1);
  });

  test("stop stops every engine", async () => {
    const s = setup({ listed: [work, team] });
    await s.host.refresh();
    await s.host.stop();
    expect(s.running()).toEqual([]);
  });
});
