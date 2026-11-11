// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { vaultOf } from "../api/paths";
import { useConsole } from "../console/store";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { useSyncStatus } from "../state/sync";
import { fakeApi, json, renderApp, user } from "../test/app";
import { FakeSocket } from "../test/socket";
import { forgetHeld } from "./hideAgain";
import { forgetDiscreet } from "./neutralWindow";
import { useDiscreet } from "./state";

beforeEach(() => {
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  forgetHeld();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set(), hiding: false });
  forgetDiscreet();
  useSyncStatus.setState({ connected: false, editors: {} });
  useConsole.getState().reset();
});

const vault = (name: string) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, discreet: false });

/** A hidden session of Ada's with the vaults `at.names` lists now, each with any note asked for. */
function server(names: string[], extra: Parameters<typeof fakeApi>[0] = {}) {
  const at = { names };
  const has = (name: string) => at.names.includes(name);
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: false, until: null } }),
    "GET /api/vaults": () => json(200, at.names.map(vault)),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/*": (r) => {
      const name = decodeURIComponent(new URL(r.url).pathname.split("/")[3]);
      if (new URL(r.url).pathname.endsWith("/publications")) return json(200, { publications: [], unpublishable: [] });
      if (!has(name)) return json(404, { error: `There is no vault '${name}'.` });
      return json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: [] } });
    },
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      if (!has(vaultOf(path))) return json(404, { error: `There is no note at ${path}.` });
      return json(200, { path, content: "# Note\n\nLiebeskummer\n", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-09T09:00:00Z" });
    },
    "GET /api/version/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/version".length));
      if (!has(vaultOf(path))) return json(404, { error: `There is no note at ${path}.` });
      return json(200, { path, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-09T09:00:00Z" });
    },
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/history/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/tags": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "GET /api/search": () => json(200, { results: [], notIndexed: 0, notice: null }),
    "POST /api/ws-ticket": () => json(200, { ticket: "t1", expiresAt: "2026-11-09T10:00:30Z" }),
    ...extra,
  });
  return { at, fetch };
}

const last = () => FakeSocket.all.at(-1)!;
const listedNames = (queryClient: QueryClient) => queryClient.getQueryData<{ name: string }[]>(["vaults"])?.map((v) => v.name);

/** This tab on `path`, its socket open, with the vault list in. */
async function tabOn(path: string, at: { names: string[] }) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = renderApp([path], queryClient);
  expect(await screen.findByText("Liebeskummer")).toBeInTheDocument();
  await waitFor(() => expect(listedNames(queryClient)).toEqual(at.names));
  await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
  last().open();
  return { router, queryClient };
}

/**
 * Another session marks a vault discreet, so the vault list becomes `names`: the server closes this tab's socket with
 * 4412, and it connects again, which reads the list again.
 */
async function markedElsewhere(queryClient: QueryClient, at: { names: string[] }, names: string[]) {
  at.names = names;
  const sockets = FakeSocket.all.length;
  last().drop(4412);
  await waitFor(() => expect(FakeSocket.all).toHaveLength(sockets + 1));
  last().open();
  await waitFor(() => expect(listedNames(queryClient)).toEqual(names));
}

/** Time for whatever the tab does once the list is in. */
const settle = () => act(() => new Promise((resolve) => setTimeout(resolve, 100)));

describe("a vault marked discreet in another session", () => {
  it("leaves this tab's view for the overview", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Tagebuch/geheim.md", at);

    await markedElsewhere(queryClient, at, ["Work"]);

    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(document.body.textContent).not.toContain("Liebeskummer");
    expect(document.body.textContent).not.toContain("geheim");
  });

  it("forgets its recent notes, active vault, folders and search fields", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Tagebuch/geheim.md", at);
    await act(() => router.navigate("/notes/Work/inbox.md"));
    await waitFor(() => expect(useEditor.getState().recentPaths).toEqual(["/Work/inbox.md", "/Tagebuch/geheim.md"]));
    act(() =>
      useEditor.setState({
        activeVault: "Tagebuch",
        selectedFolder: "/Tagebuch/Briefe",
        expandedFolders: { "/Tagebuch/Briefe": true, "/Work/Projekte": true },
        searchQuery: "Liebeskummer",
        searchTag: "herz",
      }),
    );

    await markedElsewhere(queryClient, at, ["Work"]);

    await waitFor(() => expect(useEditor.getState().recentPaths).toEqual(["/Work/inbox.md"]));
    expect(useEditor.getState()).toMatchObject({
      currentPath: "/Work/inbox.md",
      selectedFolder: null,
      expandedFolders: { "/Work/Projekte": true },
      searchQuery: "",
      searchTag: null,
    });
    expect(useEditor.getState().activeVault).not.toBe("Tagebuch");
    // An ordinary note on screen stays.
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
  });

  it("drops its cached queries", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Tagebuch/geheim.md", at);
    expect(queryClient.getQueryData(["note", "/Tagebuch/geheim.md"])).toBeDefined();

    await markedElsewhere(queryClient, at, ["Work"]);
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    await settle();

    const keys = queryClient.getQueryCache().getAll().map((q) => JSON.stringify(q.queryKey));
    expect(keys.filter((k) => k.includes("Tagebuch"))).toEqual([]);
    // Positive control: what the tab still shows stays cached.
    expect(keys).toContain(JSON.stringify(["vaults"]));
  });

  it("keeps an unsaved draft of that vault, for once it is shown again", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Work/inbox.md", at);
    const draft = { username: user.username, path: "/Tagebuch/geheim.md", text: "Not saved yet.", version: "v1" };
    act(() => useEditor.setState({ draft, recentPaths: ["/Work/inbox.md", "/Tagebuch/geheim.md"] }));

    await markedElsewhere(queryClient, at, ["Work"]);

    await waitFor(() => expect(useEditor.getState().recentPaths).toEqual(["/Work/inbox.md"]));
    expect(useEditor.getState().draft).toEqual(draft);
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
  });

  /** This tab writing in Tagebuch's note, its text not saved (the server fails the save). */
  async function writing() {
    const { at } = server(["Work", "Tagebuch"], { "PUT /api/notes/*": () => json(500, { error: "Broken." }) });
    const tab = await tabOn("/notes/Tagebuch/geheim.md", at);
    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    await userEvent.type(await screen.findByRole("textbox", { name: "Markdown source" }), "{Control>}{End}{/Control} Mehr.");
    // After the 800 ms of the autosave.
    await screen.findByRole("alert", { name: "Not saved" }, { timeout: 3_000 });
    return { at, ...tab };
  }

  it("keeps the text unsaved in its open note as a draft, and leaves the note", async () => {
    const { at, router, queryClient } = await writing();

    await markedElsewhere(queryClient, at, ["Work"]);

    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(screen.queryByRole("textbox", { name: "Markdown source" })).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain("Liebeskummer");
    expect(useEditor.getState().draft).toEqual({ username: user.username, path: "/Tagebuch/geheim.md", text: "# Note\n\nLiebeskummer\n Mehr.", version: "v1" });
  });

  it("never replaces a draft there is already", async () => {
    const { at, router, queryClient } = await writing();
    const draft = { username: user.username, path: "/Tagebuch/brief.md", text: "Left by a session's end.", version: "v3" };
    act(() => useEditor.setState({ draft }));

    await markedElsewhere(queryClient, at, ["Work"]);

    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(useEditor.getState().draft).toEqual(draft);
  });

  it("forgets the console's scrollback, directory and history", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { queryClient } = await tabOn("/notes/Work/inbox.md", at);
    act(() => {
      useConsole.setState({ cwd: "/Tagebuch" });
      useConsole.getState().remember("ls");
      useConsole.getState().add({ id: 1, cwd: "/Tagebuch", typed: "ls", sent: "ls", output: [{ kind: "text", text: "geheim.md" }] });
    });

    await markedElsewhere(queryClient, at, ["Work"]);

    await waitFor(() => expect(useConsole.getState()).toMatchObject({ cwd: "/", entries: [], history: [] }));
  });

  it("drops what was found in it", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { queryClient } = await tabOn("/notes/Work/inbox.md", at);
    const found = (path: string) => ({ results: [{ path, title: "Note", snippet: "Liebeskummer", score: 1 }], notIndexed: 0, notice: null });
    queryClient.setQueryData(["search", "Liebeskummer", "hybrid", null, false], found("/Tagebuch/geheim.md"));
    queryClient.setQueryData(["search", "inbox", "hybrid", null, false], found("/Work/inbox.md"));

    await markedElsewhere(queryClient, at, ["Work"]);
    await waitFor(() => expect(queryClient.getQueryData(["search", "Liebeskummer", "hybrid", null, false])).toBeUndefined());
    await settle();

    expect(queryClient.getQueryData(["search", "Liebeskummer", "hybrid", null, false])).toBeUndefined();
    // Positive control: what was found elsewhere stays.
    expect(queryClient.getQueryData(["search", "inbox", "hybrid", null, false])).toEqual(found("/Work/inbox.md"));
  });

  it("leaves this tab's view when it was marked while the connection was down", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router } = await tabOn("/notes/Tagebuch/geheim.md", at);

    // The phone's socket dropped; meanwhile the laptop marked Tagebuch, and the phone's next ticket leaves it out.
    last().drop();
    at.names = ["Work"];
    await waitFor(() => expect(FakeSocket.all).toHaveLength(2), { timeout: 3_000 });
    last().open();

    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(useEditor.getState().recentPaths).toEqual([]);
    expect(document.body.textContent).not.toContain("Liebeskummer");
  });
});

describe("a vault list read again once the connection is back", () => {
  it("keeps the view of a vault renamed elsewhere while the connection was down", async () => {
    const { at } = server(["Work", "Garten"]);
    const { router, queryClient } = await tabOn("/notes/Garten/geheim.md", at);
    act(() => useEditor.setState({ searchQuery: "Beet" }));

    // Renamed while this tab's socket was closed: it never heard the `renamed` frame.
    await markedElsewhere(queryClient, at, ["Work", "Gartenarbeit"]);
    await settle();

    expect(router.state.location.pathname).toBe("/notes/Garten/geheim.md");
    expect(useEditor.getState()).toMatchObject({ currentPath: "/Garten/geheim.md", searchQuery: "Beet" });
    expect(useEditor.getState().recentPaths).toContain("/Garten/geheim.md");
  });

  it("leaves the views of all vaults it lost when more were lost than gained", async () => {
    const { at } = server(["Work", "Garten", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Garten/geheim.md", at);
    act(() => useEditor.setState({ recentPaths: ["/Garten/geheim.md", "/Tagebuch/brief.md", "/Work/inbox.md"] }));

    // One renamed and one marked meanwhile: which is which can't be told.
    await markedElsewhere(queryClient, at, ["Work", "Gartenarbeit"]);

    // The overview opens the ordinary note read last, as `/` does.
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/inbox.md"));
    expect(useEditor.getState().recentPaths).toEqual(["/Work/inbox.md"]);
  });

  it("changes nothing after an ordinary reconnect with the same vaults", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Tagebuch/geheim.md", at);
    act(() => useEditor.setState({ searchQuery: "Liebeskummer", activeVault: "Tagebuch" }));
    act(() => useConsole.getState().remember("ls"));

    const read = queryClient.getQueryState(["vaults"])!.dataUpdatedAt;
    last().drop();
    await waitFor(() => expect(FakeSocket.all).toHaveLength(2), { timeout: 3_000 });
    last().open();
    await waitFor(() => expect(queryClient.getQueryState(["vaults"])!.dataUpdatedAt).toBeGreaterThan(read));
    await settle();

    expect(router.state.location.pathname).toBe("/notes/Tagebuch/geheim.md");
    expect(useEditor.getState()).toMatchObject({ recentPaths: ["/Tagebuch/geheim.md"], searchQuery: "Liebeskummer", activeVault: "Tagebuch" });
    expect(useConsole.getState().history).toEqual(["ls"]);
    expect(queryClient.getQueryData(["note", "/Tagebuch/geheim.md"])).toBeDefined();
  });

  it("changes nothing when it is empty after a removal, and compares the next one", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Tagebuch/geheim.md", at);

    await markedElsewhere(queryClient, at, []);
    await settle();
    expect(router.state.location.pathname).toBe("/notes/Tagebuch/geheim.md");
    expect(useEditor.getState().recentPaths).toEqual(["/Tagebuch/geheim.md"]);

    at.names = ["Work"];
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(useEditor.getState().recentPaths).toEqual([]);
  });

  it("changes nothing when it still has every vault", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Tagebuch/geheim.md", at);
    act(() => useEditor.setState({ searchQuery: "Liebeskummer", activeVault: "Tagebuch" }));

    await markedElsewhere(queryClient, at, ["Work", "Tagebuch"]);
    await settle();

    expect(router.state.location.pathname).toBe("/notes/Tagebuch/geheim.md");
    expect(useEditor.getState()).toMatchObject({ recentPaths: ["/Tagebuch/geheim.md"], searchQuery: "Liebeskummer", activeVault: "Tagebuch" });
    expect(queryClient.getQueryData(["note", "/Tagebuch/geheim.md"])).toBeDefined();
  });
});

describe("a vault list read again while the connection stays open", () => {
  it("changes nothing", async () => {
    const { at } = server(["Work", "Tagebuch"]);
    const { router, queryClient } = await tabOn("/notes/Tagebuch/geheim.md", at);
    act(() => useEditor.setState({ searchQuery: "Liebeskummer" }));

    at.names = ["Work"];
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));
    await waitFor(() => expect(listedNames(queryClient)).toEqual(["Work"]));
    await settle();

    expect(router.state.location.pathname).toBe("/notes/Tagebuch/geheim.md");
    expect(useEditor.getState()).toMatchObject({ recentPaths: ["/Tagebuch/geheim.md"], searchQuery: "Liebeskummer" });
  });
});
