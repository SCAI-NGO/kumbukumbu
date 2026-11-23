// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, renderHook } from "@testing-library/react";
import { DEFAULT_CHOICE, type OfflineChoice } from "eidolon-core/store/choice";
import type { SealedMirror } from "eidolon-core/store/mirror";
import { afterEach, describe, expect, test } from "vitest";
import { useOfflineCopy } from "./copy";
import { useKeepOffline } from "./keepOffline";

/** A copy that is open, with a choice kept in memory. */
function open(choice: Partial<OfflineChoice> = {}) {
  const mirror = {
    locked: false,
    choice: { ...DEFAULT_CHOICE, ...choice },
    async choose(change: (c: OfflineChoice) => OfflineChoice) {
      mirror.choice = change(mirror.choice);
      return mirror.choice;
    },
  };
  useOfflineCopy.getState().set({} as never, mirror as unknown as SealedMirror);
  return mirror;
}

afterEach(() => useOfflineCopy.getState().clear());

describe("Keep offline in the tree's menus", () => {
  test("is offered only with an open copy, for a vault this device keeps", () => {
    expect(renderHook(() => useKeepOffline("v")).result.current("Scans")).toEqual([]);
    open({ all: false, vaults: ["w"] });
    expect(renderHook(() => useKeepOffline("v")).result.current("Scans")).toEqual([]);
    expect(renderHook(() => useKeepOffline("w")).result.current("Scans").map((i) => i.label)).toEqual(["Keep offline"]);
  });

  test("keeps a folder, then offers to stop; a folder inside one kept is kept already", async () => {
    const mirror = open();
    const { result } = renderHook(() => useKeepOffline("v"));
    await act(async () => result.current("Scans")[0]!.onSelect());
    expect(mirror.choice.kept).toEqual({ v: ["Scans"] });
    expect(result.current("Scans").map((i) => i.label)).toEqual(["Don't keep offline"]);
    expect(result.current("Scans/2026")).toEqual([]);
    expect(result.current("Scanned").map((i) => i.label)).toEqual(["Keep offline"]);
    await act(async () => result.current("Scans")[0]!.onSelect());
    expect(mirror.choice.kept).toEqual({ v: [] });
  });

  test("the whole vault kept takes the folders kept in it into itself", async () => {
    const mirror = open({ kept: { v: ["Scans", "Photos/2026"] } });
    const { result } = renderHook(() => useKeepOffline("v"));
    await act(async () => result.current("")[0]!.onSelect());
    expect(mirror.choice.kept).toEqual({ v: [""] });
    expect(result.current("Scans")).toEqual([]);
  });

  test("a folder kept takes the folders kept below it into itself", async () => {
    const mirror = open({ kept: { v: ["Photos/2026", "Scans"] } });
    const { result } = renderHook(() => useKeepOffline("v"));
    await act(async () => result.current("Photos")[0]!.onSelect());
    expect(mirror.choice.kept).toEqual({ v: ["Scans", "Photos"] });
  });
});
