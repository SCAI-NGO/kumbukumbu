// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useConsole } from "../console/store";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { useSyncStatus } from "../state/sync";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";
import { FakeSocket } from "../test/socket";
import { forgetHeld, holding, SAVE_AHEAD } from "./hideAgain";
import { useDiscreet } from "./state";

beforeEach(() => {
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  forgetHeld();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  useConsole.getState().reset();
  useSyncStatus.setState({ connected: false, editors: {} });
});

const vault = (name: string, discreet = false) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, discreet });
const tree = (name: string) => json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: [{ name: "a.md", path: `/${name}/a.md`, size: 4 }] } });
const inMinutes = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();

/**
 * Alice's server: `Work` and the discreet `Tagebuch`, shown until `until` (null: Never) until a hide (here, or as
 * `server.hidden()` from elsewhere). `log` records the saves and the hide in their order.
 */
function server(until: string | null, extra: Parameters<typeof fakeApi>[0] = {}) {
  let shown = true;
  const net = { offline: false };
  const log: string[] = [];
  const offline = (handlers: Parameters<typeof fakeApi>[0]) =>
    Object.fromEntries(
      Object.entries(handlers).map(([key, handler]) => [
        key,
        (r: Request) => {
          if (net.offline) throw new TypeError("Failed to fetch");
          return handler(r);
        },
      ]),
    );
  const fetch = fakeApi(offline({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: shown, until: shown ? until : null } }),
    "GET /api/vaults": () => json(200, shown ? [vault("Work"), vault("Tagebuch", true)] : [vault("Work")]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => tree("Work"),
    "GET /api/vaults/Tagebuch/tree": () => (shown ? tree("Tagebuch") : json(404, { error: "you have no vault named Tagebuch" })),
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      if (!shown && path.startsWith("/Tagebuch/")) return json(404, { error: "you have no vault named Tagebuch" });
      return json(200, { path, content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-09T09:00:00Z" });
    },
    "GET /api/version/*": () => json(404, { error: "no" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/history/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "POST /api/ws-ticket": () => json(200, { ticket: "t1", expiresAt: "2026-11-09T10:00:30Z" }),
    "POST /api/account/discreet/hide": () => {
      log.push("hide");
      shown = false;
      return noContent();
    },
    ...extra,
  }));
  return { fetch, log, net, hidden: () => (shown = false), requests: (method: string, path: string) => fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === method && new URL(r.url).pathname === path) };
}

/** The open note's editor, as far as hiding sees it: it saves (or fails to) and says what is left. */
function editorOn(log: string[], saved = true, text = "Liebes Tagebuch") {
  const beforeRead = vi.fn(async () => {
    log.push("save");
    return saved;
  });
  useEditor.setState({ beforeRead, unsaved: () => (saved ? null : { text, version: "v1" }) });
  return beforeRead;
}

/** Everything a hide leaves behind in this tab, besides the URL. */
function expectForgotten(queryClient: QueryClient) {
  expect(useDiscreet.getState()).toMatchObject({ revealed: false, until: null });
  expect(useDiscreet.getState().names.size).toBe(0);
  const editor = useEditor.getState();
  expect(editor.activeVault).toBe("Work");
  expect(editor.currentPath ?? "").not.toContain("Tagebuch");
  expect(editor.recentPaths.join()).not.toContain("Tagebuch");
  expect(Object.keys(editor.expandedFolders).join()).not.toContain("Tagebuch");
  expect(editor.draft).toBeNull();
  expect(queryClient.getQueryCache().getAll().map((q) => JSON.stringify([q.queryKey, q.state.data])).filter((q) => q.includes("Tagebuch"))).toEqual([]);
  expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.variables))).not.toContain("Tagebuch");
  expect(useConsole.getState().entries).toEqual([]);
  expect(useSyncStatus.getState().editors).toEqual({});
}

/** Opened on a note of `Tagebuch`, with a note of `Work` read before it, an answer in the console and an editor shown elsewhere. */
async function openDiary(until: string | null, extra: Parameters<typeof fakeApi>[0] = {}) {
  const api = server(until, extra);
  useEditor.setState({ recentPaths: ["/Work/a.md"] });
  useConsole.getState().add({ id: 1, cwd: "/Tagebuch", typed: "ls", output: [{ kind: "text", text: "a.md" }] });
  useSyncStatus.setState({ editors: { "Tagebuch\n/a.md": ["Bob"] } });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = renderApp("/notes/Tagebuch/a.md", queryClient);
  expect(await screen.findByRole("heading", { name: "a" })).toBeInTheDocument();
  await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
  expect(useDiscreet.getState().revealed).toBe(true);
  return { ...api, queryClient, router };
}

const hideButton = () => within(screen.getByRole("group", { name: "Discreet vaults shown" })).getByRole("button", { name: "Hide discreet vaults" });

describe("hiding the discreet vaults again", () => {
  it("✕ saves the open note first, tells the server, forgets everything and goes back to Work", async () => {
    const { log, queryClient, router, requests } = await openDiary(inMinutes(10));
    const beforeRead = editorOn(log);
    await userEvent.click(hideButton());
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/a.md"));
    expect(beforeRead).toHaveBeenCalledWith({ reread: false });
    expect(log).toEqual(["save", "hide"]);
    await waitFor(() => expectForgotten(queryClient));
    // The session is still there, and asked again.
    await waitFor(() => expect(queryClient.getQueryData<{ discreet: { revealed: boolean } }>(["session"])?.discreet.revealed).toBe(false));
    expect(requests("GET", "/api/session").length).toBeGreaterThan(1);
    expect(screen.queryByRole("group", { name: "Discreet vaults shown" })).not.toBeInTheDocument();
    expect(holding()).toBe(false);
  });

  it("without an ordinary note read before, shows the vault Work", async () => {
    const { log, queryClient, router } = await openDiary(null);
    useEditor.setState({ recentPaths: ["/Tagebuch/a.md"] });
    editorOn(log);
    await userEvent.click(hideButton());
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    await waitFor(() => expectForgotten(queryClient));
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
  });

  it("saves a little before the end, and hides at it", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { log, queryClient, router } = await openDiary(inMinutes(1));
    const beforeRead = editorOn(log);
    await act(() => vi.advanceTimersByTimeAsync(60_000 - SAVE_AHEAD - 2_000));
    expect(beforeRead).not.toHaveBeenCalled();
    await act(() => vi.advanceTimersByTimeAsync(3_000));
    expect(log).toEqual(["save"]);
    expect(useDiscreet.getState().revealed).toBe(true);
    await act(() => vi.advanceTimersByTimeAsync(SAVE_AHEAD));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/a.md"));
    // Saved again at the end, then hidden.
    expect(log).toEqual(["save", "save", "hide"]);
    await waitFor(() => expectForgotten(queryClient));
  });

  it("a hidden frame from the server does the same in another tab, without telling the server again", async () => {
    const { log, queryClient, router, hidden } = await openDiary(null);
    editorOn(log);
    await waitFor(() => expect(FakeSocket.all.length).toBeGreaterThan(0));
    FakeSocket.all.at(-1)!.open();
    hidden();
    FakeSocket.all.at(-1)!.frame({ type: "hidden" });
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/a.md"));
    expect(log).toEqual(["save"]);
    await waitFor(() => expectForgotten(queryClient));
  });

  it("a session read again as hidden does the same", async () => {
    const { log, queryClient, router, hidden } = await openDiary(inMinutes(30));
    editorOn(log);
    hidden();
    await act(() => queryClient.invalidateQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/a.md"));
    expect(log).toEqual(["save"]);
    await waitFor(() => expectForgotten(queryClient));
  });

  it("a 404 for a discreet vault asks for the session, which then finds it hidden", async () => {
    const { log, queryClient, router, hidden } = await openDiary(inMinutes(30));
    editorOn(log);
    hidden();
    await act(() => queryClient.refetchQueries({ queryKey: ["note", "/Tagebuch/a.md"], exact: true }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/a.md"));
    await waitFor(() => expectForgotten(queryClient));
  });

  it("offline, keeps the unsaved text out of view, and tries it and the hide once more when back online", async () => {
    const writes: string[] = [];
    const { log, net, queryClient, router, requests } = await openDiary(inMinutes(10), {
      "PUT /api/notes/Tagebuch/a.md": async (r) => {
        writes.push(((await r.clone().json()) as { content: string }).content);
        return json(200, { path: "/Tagebuch/a.md", content: "Liebes Tagebuch", version: "v2", lastWriter: "ada", lastWriteAt: "2026-11-09T10:00:00Z" });
      },
    });
    editorOn(log, false);
    net.offline = true;
    await userEvent.click(hideButton());
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/a.md"));
    await waitFor(() => expectForgotten(queryClient));
    // Not in the editor's draft, nor anywhere the app shows.
    expect(JSON.stringify(useEditor.getState())).not.toContain("Liebes Tagebuch");
    expect(holding()).toBe(true);
    expect(log).toEqual(["save"]);

    net.offline = false;
    act(() => void window.dispatchEvent(new Event("online")));
    await waitFor(() => expect(writes).toEqual(["Liebes Tagebuch"]));
    expect(holding()).toBe(false);
    // The session read again still shows them: the hide goes again, and the tab stays hidden.
    await act(() => queryClient.invalidateQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(log).toEqual(["save", "hide"]));
    await waitFor(() => expect(queryClient.getQueryData<{ discreet: { revealed: boolean } }>(["session"])?.discreet.revealed).toBe(false));
    expect(useDiscreet.getState().revealed).toBe(false);
    expect(useDiscreet.getState().unsent).toBe(false);
    // Once only.
    act(() => void window.dispatchEvent(new Event("online")));
    await new Promise((r) => setTimeout(r, 20));
    expect(writes).toHaveLength(1);
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1);
  });

  it("offline and still offline, tries once after a while within the late-save window, then forgets it", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { log, net, router, requests } = await openDiary(inMinutes(10));
    editorOn(log, false);
    net.offline = true;
    await userEvent.click(hideButton());
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/a.md"));
    expect(holding()).toBe(true);
    await act(() => vi.advanceTimersByTimeAsync(30_000));
    await waitFor(() => expect(holding()).toBe(false));
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1);
    await act(() => vi.advanceTimersByTimeAsync(10 * 60_000));
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1);
  });
});
