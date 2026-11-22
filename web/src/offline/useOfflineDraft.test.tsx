// SPDX-License-Identifier: AGPL-3.0-or-later
// The open editor as the copy's draft (offline mode design §4.7, work package 7): only while offline writes are on.
import "fake-indexeddb/auto";
import { act, renderHook } from "@testing-library/react";
import { afterEach, expect, it } from "vitest";
import { offlineCopy } from "./testCopy";
import { draftsFor, setOfflineWrites, useOfflineDraft } from "./writing";

afterEach(() => setOfflineWrites(null));

it("registers while offline writes are on, and leaves when they go off or the editor closes", async () => {
  const { on } = await offlineCopy();
  setOfflineWrites(null);
  const editor = { text: () => "typing", replaced: () => {} };
  const { unmount } = renderHook(() => useOfflineDraft("/Work/plan.md", editor));
  const drafts = draftsFor("v-work");
  expect(drafts.text("plan.md")).toBeNull();
  act(on);
  expect(drafts.text("plan.md")).toBe("typing");
  act(() => setOfflineWrites(null));
  expect(drafts.text("plan.md")).toBeNull();
  act(on);
  unmount();
  expect(drafts.text("plan.md")).toBeNull();
});
