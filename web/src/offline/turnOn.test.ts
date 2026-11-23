// SPDX-License-Identifier: AGPL-3.0-or-later
import "fake-indexeddb/auto";
import type { CopyHeader, SealedMirror } from "eidolon-core/store/mirror";
import { afterEach, expect, test, vi } from "vitest";
import { useOfflineCopy } from "./copy";
import { openCopy } from "./mode";

afterEach(() => useOfflineCopy.getState().clear());

test("a copy of another user held by the tab is locked and let go when someone without one signs in", async () => {
  const lock = vi.fn();
  const header = { copy: "someone-elses" } as CopyHeader;
  useOfflineCopy.getState().set(header, { header, locked: false, lock } as unknown as SealedMirror);
  await openCopy("u-other");
  expect(lock).toHaveBeenCalled();
  expect(useOfflineCopy.getState().header).toBeNull();
});
