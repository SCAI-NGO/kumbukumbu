// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { editorSettled } from "./api";

afterEach(() => useEditor.getState().reset());

const PLAN = "/Work/plan.md";

describe("the editor settled before a write of the note", () => {
  // The live preview focused (workspace spec §3): the note is written in the other pane, whose next save would conflict.
  it("ends Write mode in the pane writing the note, also where another pane is focused", async () => {
    const beforeRead = vi.fn(async () => true);
    useEditor.setState({ panes: { left: { path: PLAN, mode: "write" }, right: { path: PLAN, mode: "read" } }, focused: "right", currentPath: PLAN, mode: "read", beforeRead });
    expect(await editorSettled(PLAN)).toBe(true);
    expect(useEditor.getState().panes).toMatchObject({ left: { mode: "read" }, right: { mode: "read" } });
    expect(beforeRead).toHaveBeenCalled();
  });

  it("not where its save fails", async () => {
    useEditor.setState({ panes: { left: { path: PLAN, mode: "write" }, right: { path: PLAN, mode: "read" } }, focused: "right", currentPath: PLAN, mode: "read", beforeRead: async () => false });
    expect(await editorSettled(PLAN)).toBe(false);
    expect(useEditor.getState().panes.left.mode).toBe("write");
  });
});
