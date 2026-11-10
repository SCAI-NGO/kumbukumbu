// SPDX-License-Identifier: AGPL-3.0-or-later
import { afterEach, describe, expect, it } from "vitest";
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

  it("changes nothing when no vault was shown", () => {
    useEditor.setState(kept);

    useEditor.getState().forgetShown([]);

    expect(useEditor.getState()).toMatchObject(kept);
  });
});
