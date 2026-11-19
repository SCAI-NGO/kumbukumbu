// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { besideOf, paneUrl } from "../api/paths";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { usePaneUrl } from "./usePaneUrl";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });

function signedIn() {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () =>
      json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: ["a", "b", "c", "d"].map((n) => note(`/Work/${n}.md`)) } }),
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      return json(200, { path, content: `# Note ${path.slice(6, -3)}\n`, version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" });
    },
    "GET /api/version/*": () => json(404, { error: "no" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
  });
}

const panes = () => {
  const { left, right } = useEditor.getState().panes;
  return [left.path, right?.path ?? null];
};
const at = (router: ReturnType<typeof renderApp>) => router.state.location.pathname + router.state.location.search;

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("the right pane's URL", () => {
  it("is the vault path after beside=, each segment encoded as a note's URL is", () => {
    expect(paneUrl("/Work/a b.md", "/Home/c&d+e?.md")).toBe("/notes/Work/a%20b.md?beside=Home/c%26d%2Be%3F.md");
    expect(paneUrl("/Work/a.md", null)).toBe("/notes/Work/a.md");
    expect(besideOf("?beside=Home/c%26d%2Be%3F.md")).toBe("/Home/c&d+e?.md");
    expect(besideOf("?beside=Home/f%C3%BCr.md&x=1")).toBe("/Home/für.md");
  });

  it("is nothing when it names no note", () => {
    for (const search of ["", "?beside=", "?beside=Home", "?beside=Home/", "?beside=/Home/a.md", "?beside=Home//a.md", "?beside=%E0%A4%A"]) expect(besideOf(search)).toBeNull();
  });

  it("opens the right pane on a reload, focused stays the left, and only the left note shows", async () => {
    signedIn();
    renderApp("/notes/Work/a.md?beside=Work/b.md");
    await screen.findByRole("heading", { name: "Note a" });
    await waitFor(() => expect(panes()).toEqual(["/Work/a.md", "/Work/b.md"]));
    expect(useEditor.getState()).toMatchObject({ focused: "left", currentPath: "/Work/a.md", mode: "read" });
    expect(screen.queryByRole("heading", { name: "Note b" })).not.toBeInTheDocument();
  });

  it("ignores a beside= that names no note", async () => {
    signedIn();
    renderApp("/notes/Work/a.md?beside=nonsense");
    await screen.findByRole("heading", { name: "Note a" });
    expect(panes()).toEqual(["/Work/a.md", null]);
  });

  it("restores both panes going back and forward", async () => {
    signedIn();
    const router = renderApp("/notes/Work/a.md");
    await screen.findByRole("heading", { name: "Note a" });
    await act(() => router.navigate(paneUrl("/Work/a.md", "/Work/b.md")));
    await waitFor(() => expect(panes()).toEqual(["/Work/a.md", "/Work/b.md"]));
    await act(() => router.navigate(paneUrl("/Work/c.md", "/Work/d.md")));
    await waitFor(() => expect(panes()).toEqual(["/Work/c.md", "/Work/d.md"]));

    await act(() => router.navigate(-1));
    await waitFor(() => expect(panes()).toEqual(["/Work/a.md", "/Work/b.md"]));
    await act(() => router.navigate(-1));
    await waitFor(() => expect(panes()).toEqual(["/Work/a.md", null]));
    await act(() => router.navigate(2));
    await waitFor(() => expect(panes()).toEqual(["/Work/c.md", "/Work/d.md"]));
    await screen.findByRole("heading", { name: "Note c" });
    expect(useEditor.getState()).toMatchObject({ focused: "left", currentPath: "/Work/c.md" });
  });

  it("restores both panes going back to a URL with both", async () => {
    signedIn();
    const router = renderApp(["/notes/Work/a.md?beside=Work/b.md", "/notes/Work/c.md?beside=Work/d.md"]);
    await screen.findByRole("heading", { name: "Note c" });
    await waitFor(() => expect(panes()).toEqual(["/Work/c.md", "/Work/d.md"]));
    await act(() => router.navigate(-1));
    await waitFor(() => expect(panes()).toEqual(["/Work/a.md", "/Work/b.md"]));
    await screen.findByRole("heading", { name: "Note a" });
  });
});

describe("the panes written to the URL", () => {
  let panesUrl: ReturnType<typeof usePaneUrl>;
  function Probe() {
    panesUrl = usePaneUrl();
    return null;
  }
  function on(url: string) {
    const router = createMemoryRouter([{ path: "*", element: <Probe /> }], { initialEntries: [url] });
    render(<RouterProvider router={router} />);
    return router;
  }

  it("opens a note in the right pane as a new entry", async () => {
    useEditor.getState().openNote("/Work/a.md");
    const router = on("/notes/Work/a.md");
    await act(async () => panesUrl.openBeside("/Work/b.md"));
    expect(at(router)).toBe("/notes/Work/a.md?beside=Work/b.md");
    expect(router.state.historyAction).toBe("PUSH");
  });

  it("closes a pane once it is closed in the store, the right one left as the only pane when the left closes", async () => {
    useEditor.getState().openNote("/Work/a.md");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    const router = on("/notes/Work/a.md?beside=Work/b.md");
    await act(() => panesUrl.close("right"));
    expect(at(router)).toBe("/notes/Work/a.md");

    useEditor.getState().openNote("/Work/b.md", "read", "right");
    await act(() => panesUrl.close("left"));
    expect(at(router)).toBe("/notes/Work/b.md");
  });

  it("keeps the URL while a writing pane's save fails, and the pane with it", async () => {
    useEditor.getState().openNote("/Work/a.md", "write");
    useEditor.getState().openNote("/Work/b.md", "read", "right");
    useEditor.getState().setBeforeRead(async () => false, () => ({ text: "Not saved yet.", version: "v1" }));
    const router = on("/notes/Work/a.md?beside=Work/b.md");
    await act(() => panesUrl.close("left"));
    expect(at(router)).toBe("/notes/Work/a.md?beside=Work/b.md");
    expect(panes()).toEqual(["/Work/a.md", "/Work/b.md"]);
  });
});
