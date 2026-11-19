// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "./editor";

afterEach(() => useEditor.getState().reset());

describe("the store after a vault's deletion", () => {
  it("closes an upload sheet open for a folder of that vault", () => {
    useEditor.setState({ overlay: "upload", upload: { folder: "/Work/projects", dropped: null } });

    useEditor.getState().forgetVault("Work");

    expect(useEditor.getState().upload).toBeNull();
    expect(useEditor.getState().overlay).toBe("none");
  });

  it("keeps an upload sheet open for another vault", () => {
    useEditor.setState({ overlay: "upload", upload: { folder: "/Workshop", dropped: null } });

    useEditor.getState().forgetVault("Work");

    expect(useEditor.getState().upload).toEqual({ folder: "/Workshop", dropped: null });
    expect(useEditor.getState().overlay).toBe("upload");
  });

  it("forgets a folder marked in it, a refusal and a scroll target there, and keeps those of another vault", () => {
    const inWork = { selectedFolder: "/Work/projects", refused: { path: "/Work/a.md", code: "read-only" as const, at: 1 }, scrollTarget: { path: "/Work/a.md", line: 3 } };
    useEditor.setState(inWork);
    useEditor.getState().forgetVault("Workshop");
    expect(useEditor.getState()).toMatchObject(inWork);

    useEditor.getState().forgetVault("Work");
    expect(useEditor.getState()).toMatchObject({ selectedFolder: null, refused: null, scrollTarget: null });
  });
});

describe("the store after a session that showed a vault", () => {
  const draft = { username: "ada", path: "/Work/a.md", text: "Not saved yet.", version: "v1" };
  const kept = {
    currentPath: "/Work/a.md",
    recentPaths: ["/Work/a.md", "/Home/b.md"],
    activeVault: "Work",
    searchQuery: "plan",
    searchTag: "research",
    find: { query: "goal", index: 1, count: 3 },
    noteTool: "find" as const,
    draft,
  };

  it("forgets its open note, recent notes and active vault, what was looked for and the note's tools, but keeps its draft", () => {
    useEditor.setState(kept);

    useEditor.getState().forgetShown(["Work"]);

    expect(useEditor.getState()).toMatchObject({
      currentPath: null,
      recentPaths: ["/Home/b.md"],
      activeVault: null,
      searchQuery: "",
      searchTag: null,
      find: { query: "", index: 0, count: 0 },
      noteTool: null,
      draft,
    });
  });

  it("forgets the live text and the reading place of a note there, and keeps those of another vault", () => {
    useEditor.setState({ live: { path: "/Work/a.md", text: "# Secret plan", caretLine: 1 }, reading: { path: "/Work/a.md", line: 3 } });
    useEditor.getState().forgetShown(["Home"]);
    expect(useEditor.getState()).toMatchObject({ live: { path: "/Work/a.md" }, reading: { path: "/Work/a.md" } });

    useEditor.getState().forgetShown(["Work"]);

    expect(useEditor.getState()).toMatchObject({ live: null, reading: null });
  });

  it("changes nothing when no vault was shown", () => {
    useEditor.setState(kept);

    useEditor.getState().forgetShown([]);

    expect(useEditor.getState()).toMatchObject(kept);
  });
});

describe("the panes", () => {
  /** The one editor's guard, as the editor sets it: saves (or fails to), and says what is left unsaved. */
  function editor(saved: boolean) {
    const beforeRead = vi.fn(async () => saved);
    useEditor.getState().setBeforeRead(beforeRead, () => (saved ? null : { text: "Not saved yet.", version: "v1" }));
    return beforeRead;
  }
  /** `/Work/a.md` on the left in Write mode, `/Work/b.md` on the right, focused. */
  function writingLeft() {
    useEditor.getState().openNote("/Work/a.md", "write");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    useEditor.getState().focus("right");
  }

  it("start as one pane, the left, focused", () => {
    expect(useEditor.getState()).toMatchObject({ panes: { left: { path: null, mode: "read" }, right: null }, focused: "left" });
  });

  it("open a note in the focused pane, and currentPath and mode are the focused pane's", () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    expect(useEditor.getState()).toMatchObject({ currentPath: "/Work/a.md", focused: "left", panes: { left: { path: "/Work/a.md" }, right: { path: "/Work/b.md", mode: "read" } } });

    useEditor.getState().focus("right");
    expect(useEditor.getState()).toMatchObject({ currentPath: "/Work/b.md", mode: "read", activeVault: "Work" });
    useEditor.getState().openNote("/Work/c.md");
    expect(useEditor.getState()).toMatchObject({ currentPath: "/Work/c.md", panes: { left: { path: "/Work/a.md" }, right: { path: "/Work/c.md" } } });

    useEditor.getState().focus("left");
    expect(useEditor.getState().currentPath).toBe("/Work/a.md");
  });

  it("follow currentPath and mode however they are set, the editor's guard holding its note too", () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.setState({ currentPath: "/Work/b.md", mode: "write" });
    expect(useEditor.getState().panes.left).toEqual({ path: "/Work/b.md", mode: "write" });
  });

  it("have one in Write mode at most: starting it in one first ends it in the other, with its save", async () => {
    writingLeft();
    const beforeRead = editor(true);
    const order: string[] = [];
    beforeRead.mockImplementation(async () => {
      order.push(`save, right ${useEditor.getState().panes.right?.mode}`);
      return true;
    });

    await useEditor.getState().setMode("write");

    expect(order).toEqual(["save, right read"]);
    expect(useEditor.getState()).toMatchObject({ mode: "write", panes: { left: { mode: "read" }, right: { mode: "write" } } });
  });

  it("keep Write mode where it was when that save fails, the text untouched", async () => {
    writingLeft();
    const beforeRead = editor(false);

    await useEditor.getState().setMode("write");

    expect(beforeRead).toHaveBeenCalledTimes(1);
    expect(useEditor.getState()).toMatchObject({ mode: "read", panes: { left: { mode: "write" }, right: { mode: "read" } } });
    expect(useEditor.getState().unsaved?.()).toEqual({ text: "Not saved yet.", version: "v1" });
  });

  it("don't end Write mode in the other pane when one is focused", () => {
    writingLeft();
    const beforeRead = editor(true);
    useEditor.getState().focus("left");
    useEditor.getState().focus("right");
    expect(beforeRead).not.toHaveBeenCalled();
    expect(useEditor.getState()).toMatchObject({ mode: "read", panes: { left: { mode: "write" } } });
  });

  it("end Write mode in the pane that writes, focused or not", async () => {
    writingLeft();
    editor(true);
    await useEditor.getState().endWrite();
    expect(useEditor.getState().panes.left.mode).toBe("read");
  });

  it("open a note in Read mode while the other pane writes", () => {
    writingLeft();
    useEditor.getState().openNote("/Work/c.md", "write");
    expect(useEditor.getState()).toMatchObject({ mode: "read", panes: { left: { mode: "write" }, right: { path: "/Work/c.md", mode: "read" } } });
  });

  it("save a writing pane before closing it, and keep it when that save fails", async () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    useEditor.getState().focus("right");
    useEditor.getState().setMode("write");
    const failing = editor(false);

    await useEditor.getState().closePane("right");
    expect(failing).toHaveBeenCalledTimes(1);
    expect(useEditor.getState()).toMatchObject({ focused: "right", panes: { right: { path: "/Work/b.md", mode: "write" } } });

    const saving = editor(true);
    await useEditor.getState().closePane("right");
    expect(saving).toHaveBeenCalledTimes(1);
    expect(useEditor.getState()).toMatchObject({ focused: "left", currentPath: "/Work/a.md", mode: "read", panes: { left: { path: "/Work/a.md" }, right: null } });
  });

  it("leave the right as the only pane when the left closes", async () => {
    writingLeft();
    editor(true);
    await useEditor.getState().closePane("left");
    expect(useEditor.getState()).toMatchObject({ focused: "left", currentPath: "/Work/b.md", mode: "read", panes: { left: { path: "/Work/b.md", mode: "read" }, right: null } });
  });

  it("close a pane whose vault is forgotten, the right becoming the only pane if the left goes", () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openNote("/Tagebuch/b.md", "read", "right");
    useEditor.getState().focus("right");
    useEditor.getState().forgetVault("Tagebuch");
    expect(useEditor.getState()).toMatchObject({ focused: "left", currentPath: "/Work/a.md", panes: { left: { path: "/Work/a.md" }, right: null } });

    useEditor.getState().openNote("/Tagebuch/b.md", "read", "right");
    useEditor.getState().forgetShown(["Work"]);
    expect(useEditor.getState()).toMatchObject({ focused: "left", currentPath: "/Tagebuch/b.md", panes: { left: { path: "/Tagebuch/b.md" }, right: null } });
  });

  it("close the other pane when its note is deleted, and follow a move in both", () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    useEditor.getState().moved("/Work/", "/Home/");
    expect(useEditor.getState()).toMatchObject({ currentPath: "/Home/a.md", panes: { left: { path: "/Home/a.md" }, right: { path: "/Home/b.md" } } });

    useEditor.getState().forgetNote("/Home/b.md");
    expect(useEditor.getState()).toMatchObject({ currentPath: "/Home/a.md", panes: { right: null } });
  });
});
