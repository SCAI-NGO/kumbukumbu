// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { movedPath, useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const tree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [
      { name: "projects", path: "/Work/projects", folders: [{ name: "deep", path: "/Work/projects/deep", folders: [], notes: [note("/Work/projects/deep/plan.md")] }], notes: [] },
      { name: "archive", path: "/Work/archive", folders: [], notes: [note("/Work/archive/old.md")] },
    ],
    notes: [note("/Work/inbox.md")],
  },
};

function api(move: (body: unknown) => Response) {
  const moves: unknown[] = [];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 3 }]),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/*": (r) =>
      json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "POST /api/move": async (r) => {
      const body = await r.json();
      moves.push(body);
      return move(body);
    },
  });
  return { moves, fetch };
}

describe("movedPath", () => {
  it("maps a note, and everything below a folder", () => {
    expect(movedPath("/W/a.md", "/W/a.md", "/W/b/a.md")).toBe("/W/b/a.md");
    expect(movedPath("/W/c.md", "/W/a.md", "/W/b/a.md")).toBe("/W/c.md");
    expect(movedPath("/W/p/deep/x.md", "/W/p/", "/W/q")).toBe("/W/q/deep/x.md");
    expect(movedPath("/W/pp/x.md", "/W/p/", "/W/q")).toBe("/W/pp/x.md");
  });
});

describe("the store after a move", () => {
  it("carries the draft and the upload folder along, and opens the target's folder chain", () => {
    useEditor.setState({
      draft: { username: "ada", path: "/W/p/a.md", text: "t", version: "v1" },
      upload: { folder: "/W/p/deep", dropped: null },
      expandedFolders: { "/W/p": true },
    });
    useEditor.getState().moved("/W/p/", "/W/x/y/p");
    const s = useEditor.getState();
    expect(s.draft?.path).toBe("/W/x/y/p/a.md");
    expect(s.upload?.folder).toBe("/W/x/y/p/deep");
    expect(s.expandedFolders).toEqual({ "/W/x/y/p": true, "/W/x": true, "/W/x/y": true });

    useEditor.getState().moved("/W/x/y/p/a.md", "/W/z/b.md");
    expect(useEditor.getState().draft?.path).toBe("/W/z/b.md");
    expect(useEditor.getState().upload?.folder).toBe("/W/x/y/p/deep");
    expect(useEditor.getState().expandedFolders["/W/z"]).toBe(true);
  });
});

describe("Rename or move", () => {
  it("mounts the dialog inside the tap, its name field focused, for iOS to raise the keyboard", async () => {
    api(() => json(500, { error: "" }));
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    // The shell fetched the dialog ahead; give that a moment to land.
    await new Promise((r) => setTimeout(r, 50));

    fireEvent.click(screen.getByRole("menuitem", { name: "Rename or move" }));

    // Within the click itself: rendered directly, not suspending for a chunk already in.
    expect(screen.getByRole("dialog", { name: "Rename or move inbox" })).toBeInTheDocument();
    expect(screen.getByLabelText("Name")).toHaveFocus();
  });

  it("renames the open note from its menu, says the links were rewritten, and opens it at its new place", async () => {
    const { moves } = api(() => json(200, { message: "moved /Work/inbox.md to /Work/archive/todo.md; rewrote 2 links in /Work/archive/old.md", path: "/Work/archive/todo.md", version: "v2" }));
    const router = renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename or move" }));

    const dialog = await screen.findByRole("dialog", { name: "Rename or move inbox" });
    expect(dialog).toHaveTextContent("Links to this note in other notes are updated to the new place");
    const name = within(dialog).getByLabelText("Name");
    expect(name).toHaveFocus();
    await userEvent.clear(name);
    await userEvent.type(name, "todo");
    await waitFor(() => expect(within(dialog).getByRole("option", { name: "Work / archive" })).toBeInTheDocument());
    await userEvent.selectOptions(within(dialog).getByLabelText("Folder"), "/Work/archive");
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));

    expect(await within(dialog).findByRole("status")).toHaveTextContent("Rewrote 2 links in /Work/archive/old.md.");
    expect(moves).toEqual([{ from: "/Work/inbox.md", to: "/Work/archive/todo.md", keepLinks: false }]);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/archive/todo.md"));
    expect(useEditor.getState().recentPaths[0]).toBe("/Work/archive/todo.md");
    await userEvent.click(within(dialog).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog", { name: /Rename or move/ })).not.toBeInTheDocument();
  });

  it("moves a folder from the tree, never into itself, and the open note below it follows", async () => {
    const { moves } = api(() => json(200, { message: "moved /Work/projects/ to /Work/archive/projects/ (1); no links needed rewriting", path: null, version: null }));
    const router = renderApp("/notes/Work/projects/deep/plan.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    const drawer = screen.getByRole("dialog", { name: "File tree" });
    await userEvent.click(await within(drawer).findByRole("button", { name: "Actions for projects" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename or move" }));

    const dialog = await screen.findByRole("dialog", { name: "Rename or move projects" });
    const folder = within(dialog).getByLabelText("Folder");
    const options = within(folder).getAllByRole("option").map((o) => o.textContent);
    expect(options).toEqual(["Work (top level)", "Work / archive"]);
    await userEvent.selectOptions(folder, "/Work/archive");
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));

    expect(await within(dialog).findByRole("status")).toHaveTextContent("No links needed rewriting.");
    expect(moves).toEqual([{ from: "/Work/projects/", to: "/Work/archive/projects", keepLinks: false }]);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/archive/projects/deep/plan.md"));
    expect(useEditor.getState().expandedFolders["/Work/archive/projects/deep"]).toBe(true);
  });

  it("renames a folder when only its case changes", async () => {
    const { moves } = api(() => json(200, { message: "moved /Work/archive/ to /Work/Archive/ (1); no links needed rewriting", path: null, version: null }));
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    const drawer = screen.getByRole("dialog", { name: "File tree" });
    await userEvent.click(await within(drawer).findByRole("button", { name: "Actions for archive" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename or move" }));
    const dialog = await screen.findByRole("dialog", { name: "Rename or move archive" });
    const name = within(dialog).getByLabelText("Name");
    await userEvent.clear(name);
    await userEvent.type(name, "Archive");
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));
    expect(await within(dialog).findByRole("status")).toHaveTextContent("No links needed rewriting.");
    expect(moves).toEqual([{ from: "/Work/archive/", to: "/Work/Archive", keepLinks: false }]);
  });

  it("offers no move for the vault's root, and one for every note in the tree", async () => {
    api(() => json(500, {}));
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    const drawer = screen.getByRole("dialog", { name: "File tree" });
    await userEvent.click(within(drawer).getByRole("button", { name: "Actions for Work" }));
    expect(screen.queryByRole("menuitem", { name: "Rename or move" })).not.toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    await userEvent.click(await within(drawer).findByRole("button", { name: "Actions for inbox" }));
    expect(screen.getByRole("menuitem", { name: "Rename or move" })).toBeInTheDocument();
  });

  it("refuses a name that is taken by a folder, and shows the server's refusal", async () => {
    const { moves } = api(() => json(409, { error: "There is a note at /Work/archive/old.md already." }));
    renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename or move" }));
    const dialog = await screen.findByRole("dialog", { name: "Rename or move inbox" });
    const name = within(dialog).getByLabelText("Name");

    await userEvent.clear(name);
    await userEvent.type(name, "archive.md");
    await waitFor(() => expect(within(dialog).getByRole("option", { name: "Work / archive" })).toBeInTheDocument());
    // A folder is "archive", the note would be "archive.md": not the same, so this one goes to the server.
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("There is a note at /Work/archive/old.md already.");
    expect(moves).toHaveLength(1);

    await userEvent.clear(name);
    await userEvent.type(name, "a/b");
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));
    expect(within(dialog).getAllByRole("alert")[0]).toHaveTextContent("A name can't be empty or contain “/”.");
    expect(moves).toHaveLength(1);
  });
});
