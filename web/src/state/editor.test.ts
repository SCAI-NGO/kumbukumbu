// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it, vi } from "vitest";
import { previewSide, useEditor } from "./editor";

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

  it("correct a focus on a right pane there isn't to the only pane", () => {
    useEditor.setState({ focused: "right", currentPath: "/Work/a.md" });
    expect(useEditor.getState()).toMatchObject({ focused: "left", panes: { left: { path: "/Work/a.md" }, right: null } });
  });

  it("close both at a session's end", () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    useEditor.setState({ currentPath: null });
    expect(useEditor.getState()).toMatchObject({ focused: "left", panes: { left: { path: null }, right: null } });
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

  describe("Write mode started by a click (writeHere)", () => {
    const at = { path: "/Work/b.md", offset: 4, y: 200 };

    it("keeps where the click was for the editor to take, also once the other pane's save made way", async () => {
      writingLeft();
      editor(true);
      await useEditor.getState().writeHere(at);
      expect(useEditor.getState()).toMatchObject({ panes: { left: { mode: "read" }, right: { mode: "write" } }, writeAt: at });
    });

    // The editor failed to load, say, and never took it: a later Write mode on the note doesn't start at that click.
    it("forgets it once that pane leaves Write mode without an editor taking it", async () => {
      writingLeft();
      editor(true);
      await useEditor.getState().writeHere(at);
      await useEditor.getState().endWrite();
      expect(useEditor.getState()).toMatchObject({ panes: { right: { mode: "read" } }, writeAt: null });

      await useEditor.getState().writeHere(at);
      await useEditor.getState().setMode("read");
      expect(useEditor.getState()).toMatchObject({ mode: "read", writeAt: null });
    });
  });

  it("forget where a click was in the focused pane as another note opens there, and keep the other pane's", () => {
    writingLeft();
    const at = { path: "/Work/b.md", offset: 4, y: 200 };
    useEditor.getState().setWriteAt({ ...at, side: "right" });
    useEditor.getState().openNote("/Work/c.md");
    expect(useEditor.getState().writeAt).toBeNull();

    useEditor.getState().setWriteAt({ ...at, side: "left" });
    useEditor.getState().openNote("/Work/d.md");
    expect(useEditor.getState().writeAt).toEqual({ ...at, side: "left" });
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

  it("end Write mode in the other pane before another note opens there, with its save, and keep it when that fails", async () => {
    writingLeft();
    const failing = editor(false);
    await useEditor.getState().openNote("/Work/c.md", "read", "left");
    expect(failing).toHaveBeenCalledTimes(1);
    expect(useEditor.getState().panes.left).toEqual({ path: "/Work/a.md", mode: "write" });

    const saving = editor(true);
    await useEditor.getState().openNote("/Work/c.md", "read", "left");
    expect(saving).toHaveBeenCalledTimes(1);
    expect(useEditor.getState()).toMatchObject({ focused: "right", currentPath: "/Work/b.md", panes: { left: { path: "/Work/c.md", mode: "read" } } });
  });

  it("leave a writing other pane alone when its own note opens there again", async () => {
    writingLeft();
    const beforeRead = editor(true);
    await useEditor.getState().openNote("/Work/a.md", "read", "left");
    expect(beforeRead).not.toHaveBeenCalled();
    expect(useEditor.getState().panes.left).toEqual({ path: "/Work/a.md", mode: "write" });
  });

  it("add a note opened in the other pane to the recent notes", () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    expect(useEditor.getState().recentPaths).toEqual(["/Work/b.md", "/Work/a.md"]);
  });

  it("don't start Write mode once the other pane has saved if the focus or its note changed meanwhile", async () => {
    writingLeft();
    const beforeRead = editor(true);
    let release!: () => void;
    beforeRead.mockImplementation(() => new Promise<boolean>((r) => (release = () => r(true))));
    const started = useEditor.getState().setMode("write");
    useEditor.getState().focus("left");
    release();
    await started;
    expect(useEditor.getState()).toMatchObject({ focused: "left", panes: { right: { mode: "read" } } });

    writingLeft();
    beforeRead.mockImplementation(() => new Promise<boolean>((r) => (release = () => r(true))));
    const again = useEditor.getState().setMode("write");
    useEditor.getState().openNote("/Work/d.md");
    release();
    await again;
    expect(useEditor.getState()).toMatchObject({ currentPath: "/Work/d.md", mode: "read" });
  });

  it("open a note in a pane, starting the split view for the right one, and focus it", () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openIn("right", "/Work/b.md");
    expect(useEditor.getState()).toMatchObject({ focused: "right", currentPath: "/Work/b.md", mode: "read", panes: { left: { path: "/Work/a.md" }, right: { path: "/Work/b.md" } } });
    useEditor.getState().openIn("left", "/Work/c.md");
    expect(useEditor.getState()).toMatchObject({ focused: "left", currentPath: "/Work/c.md", panes: { left: { path: "/Work/c.md" }, right: { path: "/Work/b.md" } } });
  });

  it("set a pane, the focused one by currentPath and mode, the other by itself", () => {
    writingLeft();
    useEditor.getState().setPane("left", { path: "/Work/e.md", mode: "read" });
    useEditor.getState().setPane("right", { path: "/Work/f.md", mode: "write" });
    expect(useEditor.getState()).toMatchObject({ focused: "right", currentPath: "/Work/f.md", mode: "write", panes: { left: { path: "/Work/e.md", mode: "read" } } });
  });

  it("save the right pane's Write mode before the left closes, as it moves to the left", async () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    useEditor.getState().focus("right");
    await useEditor.getState().setMode("write");
    const failing = editor(false);
    await useEditor.getState().closePane("left");
    expect(failing).toHaveBeenCalledTimes(1);
    expect(useEditor.getState().panes).toMatchObject({ left: { path: "/Work/a.md" }, right: { path: "/Work/b.md", mode: "write" } });

    editor(true);
    await useEditor.getState().closePane("left");
    expect(useEditor.getState()).toMatchObject({ currentPath: "/Work/b.md", mode: "read", panes: { right: null } });
  });

  it("forget a note being named, and why naming one failed, with its vault", () => {
    useEditor.setState({ renaming: ["/Tagebuch/Untitled.md", "/Work/Untitled.md"], renameProblem: { path: "/Tagebuch/Untitled.md", text: "No." } });
    useEditor.getState().forgetShown(["Tagebuch"]);
    expect(useEditor.getState()).toMatchObject({ renaming: ["/Work/Untitled.md"], renameProblem: null });
  });

  it("name the pane that is the live preview: not writing, on the writing pane's note", () => {
    useEditor.getState().openNote("/Work/a.md", "write");
    expect(previewSide(useEditor.getState())).toBeNull();
    useEditor.getState().openNote("/Work/a.md", "read", "right");
    expect(previewSide(useEditor.getState())).toBe("right");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    expect(previewSide(useEditor.getState())).toBeNull();
  });
});
