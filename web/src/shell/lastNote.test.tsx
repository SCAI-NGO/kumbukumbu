// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  localStorage.clear();
});

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });

const workTree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [{ name: "projects", path: "/Work/projects", folders: [], notes: [note("/Work/projects/roadmap.md")] }],
    notes: [note("/Work/inbox.md"), note("/Work/readme.md")],
  },
};

const homeTree = { damaged: 0, root: { name: "Home", path: "/Home", folders: [], notes: [note("/Home/diary.md")] } };

type Vault = { name: string; notes: number; embeddings: string; discreet?: boolean };
const home: Vault = { name: "Home", notes: 1, embeddings: "internal" };
const work: Vault = { name: "Work", notes: 3, embeddings: "internal" };

function signedInApi(extra: Parameters<typeof fakeApi>[0] = {}, { vaults = [home, work], revealed = false, who = user }: { vaults?: Vault[]; revealed?: boolean; who?: typeof user } = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user: who, previousSignInAt: null, discreet: { revealed, until: null } }),
    "GET /api/vaults": () => json(200, vaults),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, workTree),
    "GET /api/vaults/Home/tree": () => json(200, homeTree),
    "GET /api/notes/*": (r) =>
      json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    ...extra,
  });
}

const kept = () => localStorage.getItem("kumbu.lastNote.u1");
const newClient = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });

describe("the last note", () => {
  it("keeps the note opened, under the user's id", async () => {
    signedInApi();
    renderApp("/notes/Work/inbox.md");
    expect(await screen.findByRole("heading", { name: "inbox" })).toBeInTheDocument();
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
  });

  it("opens it on start with no note in the address, the tree at it", async () => {
    localStorage.setItem("kumbu.lastNote.u1", "/Work/projects/roadmap.md");
    signedInApi();
    const router = renderApp("/");
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
    expect(await screen.findByRole("heading", { name: "roadmap" })).toBeInTheDocument();
    // The vault list is never shown on the way.
    expect(screen.queryByRole("heading", { name: "Your vaults" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Open file tree" }));
    const tree = screen.getByRole("dialog", { name: "File tree" });
    expect(await within(tree).findByRole("button", { name: "roadmap" })).toHaveAttribute("aria-current", "page");
  });

  it("shows no vault list while the kept note's tree is on its way", async () => {
    localStorage.setItem("kumbu.lastNote.u1", "/Work/inbox.md");
    let answer: (r: Response) => void = () => {};
    signedInApi({ "GET /api/vaults/Work/tree": () => new Promise<Response>((resolve) => (answer = resolve)) });
    const router = renderApp("/");
    await waitFor(() => expect(useEditor.getState().activeVault).toBe("Work"));
    expect(screen.queryByRole("heading", { name: "Your vaults" })).not.toBeInTheDocument();
    await act(async () => answer(json(200, workTree)));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/inbox.md"));
  });

  it("keeps a note only once it has loaded: one that isn't there leaves the last one kept", async () => {
    signedInApi({ "GET /api/notes/Work/nope.md": () => json(404, { error: "There is no note at /Work/nope.md." }) });
    const router = renderApp("/notes/Work/inbox.md");
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    await act(() => router.navigate("/notes/Work/nope.md"));
    expect(await screen.findByText("There is no note at /Work/nope.md.")).toBeInTheDocument();
    expect(kept()).toBe("/Work/inbox.md");
  });

  it("a vault chosen before the shell shows (Share's Open) wins over it", async () => {
    localStorage.setItem("kumbu.lastNote.u1", "/Work/inbox.md");
    signedInApi();
    useEditor.getState().setActiveVault("Home");
    const router = renderApp("/");
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
    expect(useEditor.getState().activeVault).toBe("Home");
    expect(kept()).toBe("/Work/inbox.md");
  });

  it("opens only on the app's start: a later / in the same page load stays the vaults", async () => {
    signedInApi();
    renderApp("/", newClient());
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    // Another tab keeps a note meanwhile; the shell shows again, from nothing chosen.
    localStorage.setItem("kumbu.lastNote.u1", "/Work/inbox.md");
    cleanup();
    useEditor.getState().reset();
    const router = renderApp("/", newClient());
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
  });

  it("a note that is gone opens its vault's root, and is forgotten", async () => {
    localStorage.setItem("kumbu.lastNote.u1", "/Work/gone.md");
    signedInApi();
    const router = renderApp("/");
    await waitFor(() => expect(kept()).toBeNull());
    expect(router.state.location.pathname).toBe("/");
    expect(useEditor.getState().activeVault).toBe("Work");
  });

  it("a vault that is gone starts as today, and is forgotten", async () => {
    localStorage.setItem("kumbu.lastNote.u1", "/Gone/x.md");
    signedInApi();
    const router = renderApp("/");
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    await waitFor(() => expect(kept()).toBeNull());
    expect(router.state.location.pathname).toBe("/");
    expect(useEditor.getState().activeVault).toBe("Home");
  });

  it("a note in a discreet vault is never kept", async () => {
    signedInApi({}, { vaults: [{ ...home, discreet: true }, work], revealed: true });
    const router = renderApp("/notes/Work/inbox.md");
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    await waitFor(() => expect(useDiscreet.getState().names).toEqual(new Set(["Home"])));
    await act(() => router.navigate("/notes/Home/diary.md"));
    expect(await screen.findByRole("heading", { name: "diary" })).toBeInTheDocument();
    expect(kept()).toBe("/Work/inbox.md");
    for (let i = 0; i < localStorage.length; i++) expect(localStorage.getItem(localStorage.key(i)!)).not.toContain("Home");
  });

  it.each([
    ["marked discreet", [home, { ...work, discreet: true }], true],
    ["left out of the list", [home], false],
  ])("the kept note goes once its vault is %s, here or elsewhere", async (_, vaults, revealed) => {
    localStorage.setItem("kumbu.lastNote.u1", "/Work/inbox.md");
    signedInApi({}, { vaults, revealed });
    const router = renderApp("/");
    await waitFor(() => expect(kept()).toBeNull());
    expect(router.state.location.pathname).toBe("/");
  });

  it("signing out forgets it", async () => {
    signedInApi({ "DELETE /api/session": () => noContent() });
    const router = renderApp("/notes/Work/inbox.md", newClient());
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(screen.getByRole("button", { name: "Sign out" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(kept()).toBeNull();
  });

  it("a session that ends keeps it", async () => {
    signedInApi();
    const router = renderApp("/notes/Work/inbox.md", newClient());
    await waitFor(() => expect(kept()).toBe("/Work/inbox.md"));
    act(() => useSession.getState().end());
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(kept()).toBe("/Work/inbox.md");
  });

  it("another user never sees it", async () => {
    signedInApi({}, { who: { ...user, id: "u2" } });
    renderApp("/notes/Work/inbox.md", newClient());
    await waitFor(() => expect(localStorage.getItem("kumbu.lastNote.u2")).toBe("/Work/inbox.md"));
    cleanup();
    vi.unstubAllGlobals();
    useSession.getState().reset();
    useEditor.getState().reset();

    signedInApi();
    const router = renderApp("/", newClient());
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/");
  });
});
