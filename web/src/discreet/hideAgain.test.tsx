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
import { entryView } from "../test/calendar";
import { FakeSocket } from "../test/socket";
import { forgetHeld, hideAgain, holding, SAVE_AHEAD } from "./hideAgain";
import { useDiscreet } from "./state";

beforeEach(() => {
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
});

afterEach(async () => {
  // A hide still under way would go on into the next test, which would join it rather than start its own.
  await waitFor(() => expect(useDiscreet.getState().hiding).toBe(false));
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  forgetHeld();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  // Kept by a reset, as in the app; a test starts with no vault hidden before.
  useDiscreet.setState({ gone: new Set() });
  useConsole.getState().reset();
  useSyncStatus.setState({ connected: false, editors: {} });
});

const vault = (name: string, discreet = false) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, discreet });
const tree = (name: string) => json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: [{ name: "a.md", path: `/${name}/a.md`, size: 4 }] } });
const inMinutes = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();

/**
 * Alice's server: `Work` and the discreet `Tagebuch`, shown until `until` (null: Never) by its clock, `behind` ms
 * behind the tab's, or until a hide (here, or as `server.hidden()` from elsewhere). `log` records the saves and the
 * hide in their order; `told` what the tab said of the open note.
 */
function server(until: string | null, extra: Parameters<typeof fakeApi>[0] = {}, behind = 0) {
  let hidden = false;
  const net = { offline: false };
  const log: string[] = [];
  const told: unknown[] = [];
  const isShown = () => !hidden && (until === null || Date.now() - behind < Date.parse(until));
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
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: isShown(), until: isShown() ? until : null } }),
    "GET /api/vaults": () => json(200, isShown() ? [vault("Work"), vault("Tagebuch", true)] : [vault("Work")]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => tree("Work"),
    "GET /api/vaults/Tagebuch/tree": () => (isShown() ? tree("Tagebuch") : json(404, { error: "you have no vault named Tagebuch" })),
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      if (!isShown() && path.startsWith("/Tagebuch/")) return json(404, { error: "you have no vault named Tagebuch" });
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
      hidden = true;
      return noContent();
    },
    "PUT /api/account/discreet/open-note": async (r) => {
      told.push(await r.clone().json());
      return noContent();
    },
    ...extra,
  }));
  return { fetch, log, net, told, hidden: () => (hidden = true), shownAgain: () => (hidden = false), requests: (method: string, path: string) => fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === method && new URL(r.url).pathname === path) };
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
  expect(editor.selectedFolder ?? "").not.toContain("Tagebuch");
  expect(editor.refused).toBeNull();
  expect(editor.scrollTarget).toBeNull();
  expect(editor).toMatchObject({ searchQuery: "", searchTag: null, find: { query: "", index: 0, count: 0 }, noteTool: null, mode: "read" });
  expect(queryClient.getQueryCache().getAll().map((q) => JSON.stringify([q.queryKey, q.state.data])).filter((q) => q.includes("Tagebuch"))).toEqual([]);
  expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.variables))).not.toContain("Tagebuch");
  expect(useConsole.getState().entries).toEqual([]);
  expect(useSyncStatus.getState().editors).toEqual({});
}

/** Opened on a note of `Tagebuch`, with a note of `Work` read before it, an answer in the console and an editor shown elsewhere. */
async function openDiary(until: string | null, extra: Parameters<typeof fakeApi>[0] = {}, behind = 0) {
  const api = server(until, extra, behind);
  useEditor.setState({ recentPaths: ["/Work/a.md"] });
  useConsole.getState().add({ id: 1, cwd: "/Tagebuch", typed: "ls", output: [{ kind: "text", text: "a.md" }] });
  useSyncStatus.setState({ editors: { "Tagebuch\n/a.md": ["Bob"] } });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = renderApp("/notes/Tagebuch/a.md", queryClient);
  await headingShown("a");
  await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
  expect(useDiscreet.getState().revealed).toBe(true);
  /**
   * Shown again as the tab learns it: the server shows them, and the session and the vault list read again say so. Set
   * by hand instead, the mode could meet the tab still following the session the hide asked for, which says hidden.
   */
  const showAgain = async () => {
    api.shownAgain();
    await act(() => queryClient.refetchQueries({ queryKey: ["session"] }));
    await act(() => queryClient.refetchQueries({ queryKey: ["vaults"] }));
    await waitFor(() => {
      expect(useDiscreet.getState().revealed).toBe(true);
      expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]);
    });
  };
  return { ...api, queryClient, router, showAgain };
}

/**
 * The tab is back on `path` and done hiding. The URL changes before the hide's last steps (dropping the cached answers,
 * ending `hiding`), so the URL alone says too little for what a test does next.
 */
async function doneHiding(router: ReturnType<typeof renderApp>, path = "/notes/Work/a.md") {
  await waitFor(() => {
    expect(router.state.location.pathname).toBe(path);
    expect(useDiscreet.getState().hiding).toBe(false);
  });
}

/** The note's heading is shown: looked for and checked at once, as the view may render it anew right after a hide. */
const headingShown = (name: string) => waitFor(() => expect(screen.getByRole("heading", { name })).toBeInTheDocument());

const hideButton = () => within(screen.getByRole("group", { name: "Discreet vaults shown" })).getByRole("button", { name: "Hide discreet vaults" });

describe("hiding the discreet vaults again", () => {
  it("✕ saves the open note first, tells the server, forgets everything and goes back to Work", async () => {
    const { log, queryClient, router, requests } = await openDiary(inMinutes(10));
    const beforeRead = editorOn(log);
    // The note the tab goes to is open by the time the hiding ends: the shell doesn't follow the URL meanwhile.
    const openWhenDone: (string | null)[] = [];
    const stop = useDiscreet.subscribe((s, previous) => void (previous.hiding && !s.hiding && openWhenDone.push(useEditor.getState().currentPath)));
    await userEvent.click(hideButton());
    await doneHiding(router);
    await waitFor(() => expect(openWhenDone).toEqual(["/Work/a.md"]));
    stop();
    expect(beforeRead).toHaveBeenCalledWith({ reread: false });
    expect(log).toEqual(["save", "hide"]);
    await waitFor(() => expectForgotten(queryClient));
    // The note of the URL is the open one again.
    expect(useEditor.getState().currentPath).toBe("/Work/a.md");
    await headingShown("a");
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
    await doneHiding(router, "/");
    await waitFor(() => expectForgotten(queryClient));
    await headingShown("Your vaults");
  });

  it("saves a little before the end, and hides at it, leaving the end to the server", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { log, queryClient, router, requests } = await openDiary(inMinutes(1));
    const beforeRead = editorOn(log);
    await act(() => vi.advanceTimersByTimeAsync(60_000 - SAVE_AHEAD - 2_000));
    expect(beforeRead).not.toHaveBeenCalled();
    await act(() => vi.advanceTimersByTimeAsync(3_000));
    expect(log).toEqual(["save"]);
    expect(useDiscreet.getState().revealed).toBe(true);
    await act(() => vi.advanceTimersByTimeAsync(SAVE_AHEAD));
    await doneHiding(router);
    // Saved again at the end, then hidden; the server ends the time itself, and keeps the late save meanwhile.
    expect(log).toEqual(["save", "save"]);
    await waitFor(() => expectForgotten(queryClient));
    expect(requests("POST", "/api/account/discreet/hide")).toHaveLength(0);
  });

  it("at the end, a save that fails is held and tried once more, as the server's late save", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const writes: string[] = [];
    let failing = true;
    const { log, queryClient, router, requests } = await openDiary(inMinutes(1), {
      "PUT /api/notes/Tagebuch/a.md": async (r) => {
        writes.push(((await r.clone().json()) as { content: string }).content);
        return json(200, { path: "/Tagebuch/a.md", content: "Liebes Tagebuch", version: "v2", lastWriter: "ada", lastWriteAt: "2026-11-09T10:00:00Z" });
      },
    });
    const beforeRead = vi.fn(async () => !failing);
    useEditor.setState({ beforeRead, unsaved: () => (failing ? { text: "Liebes Tagebuch", version: "v1" } : null) });
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    await doneHiding(router);
    await waitFor(() => expectForgotten(queryClient));
    expect(holding()).toBe(true);
    failing = false;
    await act(() => vi.advanceTimersByTimeAsync(30_000));
    await waitFor(() => expect(writes).toEqual(["Liebes Tagebuch"]));
    expect(holding()).toBe(false);
    await act(() => vi.advanceTimersByTimeAsync(10 * 60_000));
    expect(writes).toHaveLength(1);
    expect(log).toEqual([]);
    expect(requests("POST", "/api/account/discreet/hide")).toHaveLength(0);
  });

  it("waking from a sleep past the end, saves once and sends no hide", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { log, queryClient, router, requests } = await openDiary(inMinutes(1));
    const beforeRead = editorOn(log);
    // Asleep: the clock moves on, no timer runs; on waking, the timers due meanwhile run at once.
    vi.setSystemTime(Date.now() + 3 * 60_000);
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    await doneHiding(router);
    expect(beforeRead).toHaveBeenCalledTimes(1);
    expect(beforeRead).toHaveBeenCalledWith({ reread: false });
    await waitFor(() => expectForgotten(queryClient));
    expect(requests("POST", "/api/account/discreet/hide")).toHaveLength(0);
  });

  it("doesn't show them again when the server's clock is a little behind this tab's, and asks again", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { log, queryClient, router, requests } = await openDiary(inMinutes(1), {}, 5_000);
    editorOn(log);
    const shownAgain = vi.fn();
    const stop = useDiscreet.subscribe((s, previous) => void (s.revealed && !previous.revealed && shownAgain()));
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    await doneHiding(router);
    // The session read after the hide still says shown: the server's minute isn't over yet.
    await waitFor(() => expect(queryClient.getQueryData<{ discreet: { revealed: boolean } }>(["session"])?.discreet.revealed).toBe(true));
    expect(useDiscreet.getState().revealed).toBe(false);
    expect(screen.queryByRole("group", { name: "Discreet vaults shown" })).not.toBeInTheDocument();
    // Nor in the vault menu, though the vault list read meanwhile still has it.
    await waitFor(() => expect(JSON.stringify(queryClient.getQueryData(["vaults"]) ?? null)).toContain("Tagebuch"));
    await userEvent.click(screen.getByRole("button", { name: "Open file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Switch vault: Work" }));
    expect(await screen.findByRole("dialog", { name: /vault/i })).toBeInTheDocument();
    expect(screen.queryAllByText(/Tagebuch/)).toEqual([]);
    await userEvent.keyboard("{Escape}");
    const reads = requests("GET", "/api/session").length;
    const session = () => queryClient.getQueryData<{ discreet: { revealed: boolean } }>(["session"]);
    for (let s = 0; s < 20 && session()?.discreet.revealed; s++) await act(() => vi.advanceTimersByTimeAsync(1_000));
    expect(session()?.discreet.revealed).toBe(false);
    expect(requests("GET", "/api/session").length).toBeGreaterThan(reads);
    expect(shownAgain).not.toHaveBeenCalled();
    expect(requests("POST", "/api/account/discreet/hide")).toHaveLength(0);
    await waitFor(() => expectForgotten(queryClient));
    stop();
  });

  it("tells the server which discreet note is open, and that none is once an ordinary one is", async () => {
    const { log, told, router, requests, showAgain } = await openDiary(inMinutes(10));
    await waitFor(() => expect(told).toEqual([{ vault: "Tagebuch", path: "a.md" }]));
    await act(() => router.navigate("/notes/Work/a.md"));
    await waitFor(() => expect(told).toEqual([{ vault: "Tagebuch", path: "a.md" }, { vault: null }]));
    await act(() => router.navigate("/notes/Tagebuch/a.md"));
    await waitFor(() => expect(told).toHaveLength(3));
    expect(told[2]).toEqual({ vault: "Tagebuch", path: "a.md" });
    // ✕ clears it on the server itself: nothing more is told, not even while hiding.
    editorOn(log);
    await userEvent.click(hideButton());
    await doneHiding(router);
    await act(() => router.navigate("/notes/Work/a.md"));
    expect(told).toHaveLength(3);
    expect(requests("PUT", "/api/account/discreet/open-note")).toHaveLength(3);
    // Shown again, the same note is told again: the server forgot it with the hide.
    await showAgain();
    await act(() => router.navigate("/notes/Tagebuch/a.md"));
    await waitFor(() => expect(told).toHaveLength(4));
    expect(told[3]).toEqual({ vault: "Tagebuch", path: "a.md" });
  });

  it("tells nothing while they are hidden, nor for an ordinary note while they are shown", async () => {
    const { told, requests } = server(inMinutes(10));
    useEditor.setState({ recentPaths: [] });
    renderApp("/notes/Work/a.md");
    await headingShown("a");
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    await waitFor(() => expect(requests("GET", "/api/vaults").length).toBeGreaterThan(0));
    expect(told).toEqual([]);
  });


  it("a hidden frame from the server does the same in another tab, without telling the server again", async () => {
    const { log, queryClient, router, hidden } = await openDiary(null);
    editorOn(log);
    await waitFor(() => expect(FakeSocket.all.length).toBeGreaterThan(0));
    FakeSocket.all.at(-1)!.open();
    hidden();
    FakeSocket.all.at(-1)!.frame({ type: "hidden" });
    await doneHiding(router);
    expect(log).toEqual(["save"]);
    await waitFor(() => expectForgotten(queryClient));
  });

  it("a session read again as hidden does the same", async () => {
    const { log, queryClient, router, hidden } = await openDiary(inMinutes(30));
    editorOn(log);
    hidden();
    await act(() => queryClient.invalidateQueries({ queryKey: ["session"] }));
    await doneHiding(router);
    expect(log).toEqual(["save"]);
    await waitFor(() => expectForgotten(queryClient));
  });

  it("a 404 for a discreet vault asks for the session, which then finds it hidden", async () => {
    const { log, queryClient, router, hidden } = await openDiary(inMinutes(30));
    editorOn(log);
    hidden();
    await act(() => queryClient.refetchQueries({ queryKey: ["note", "/Tagebuch/a.md"], exact: true }));
    await doneHiding(router);
    await waitFor(() => expectForgotten(queryClient));
  });

  it("offline, keeps the unsaved text out of view, and tries it and the hide once more when back online", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
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
    await doneHiding(router);
    await waitFor(() => expectForgotten(queryClient));
    // Not in the editor's draft, nor anywhere the app shows.
    expect(JSON.stringify(useEditor.getState())).not.toContain("Liebes Tagebuch");
    expect(holding()).toBe(true);
    expect(log).toEqual(["save"]);

    net.offline = false;
    act(() => void window.dispatchEvent(new Event("online")));
    await waitFor(() => expect(writes).toEqual(["Liebes Tagebuch"]));
    expect(holding()).toBe(false);
    // Back online, the vault list is read before the session is: the server still shows them.
    await act(() => queryClient.refetchQueries({ queryKey: ["vaults"] }));
    // The session read again still shows them: the hide goes again, and the tab stays hidden.
    await act(() => queryClient.invalidateQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(log).toEqual(["save", "hide"]));
    await waitFor(() => expect(queryClient.getQueryData<{ discreet: { revealed: boolean } }>(["session"])?.discreet.revealed).toBe(false));
    expect(useDiscreet.getState().revealed).toBe(false);
    expect(useDiscreet.getState().unsent).toBe(false);
    // The vault list read while the server still showed them goes with the hide sent again.
    await waitFor(() => expect(JSON.stringify(queryClient.getQueryData(["vaults"]) ?? null)).not.toContain("Tagebuch"));
    // Once only, whatever comes later.
    act(() => void window.dispatchEvent(new Event("online")));
    await act(() => vi.advanceTimersByTimeAsync(10 * 60_000));
    expect(writes).toHaveLength(1);
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1);
  });

  it("offline and still offline, tries once after a while within the late-save window, then forgets it", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { log, net, router, requests } = await openDiary(inMinutes(10));
    editorOn(log, false);
    net.offline = true;
    await userEvent.click(hideButton());
    await doneHiding(router);
    expect(holding()).toBe(true);
    await act(() => vi.advanceTimersByTimeAsync(30_000));
    await waitFor(() => expect(holding()).toBe(false));
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1);
    await act(() => vi.advanceTimersByTimeAsync(10 * 60_000));
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1);
  });
  it("forgets what the view kept of the vault: a marked folder, a refusal, a scroll target, search, find, tools, Write mode", async () => {
    const { queryClient, router, hidden } = await openDiary(null);
    act(() =>
      useEditor.setState({
        selectedFolder: "/Tagebuch/privat",
        refused: { path: "/Tagebuch/a.md", code: "read-only", at: 1 },
        scrollTarget: { path: "/Tagebuch/a.md", line: 3 },
        searchQuery: "Liebe",
        searchTag: "privat",
        find: { query: "Liebe", index: 0, count: 1 },
        noteTool: "find",
        mode: "write",
      }),
    );
    await waitFor(() => expect(FakeSocket.all.length).toBeGreaterThan(0));
    FakeSocket.all.at(-1)!.open();
    hidden();
    FakeSocket.all.at(-1)!.frame({ type: "hidden" });
    await doneHiding(router);
    await waitFor(() => expectForgotten(queryClient));
  });

  it("a discreet vault's calendar is left when hidden again", async () => {
    const { log, queryClient, router } = await openDiary(null, {
      "GET /api/vaults/Tagebuch/calendar": () => json(200, { vault: "Tagebuch", today: "2026-11-13", zone: "Europe/Vienna", weeks: [], couldntRead: [], canWrite: true }),
    });
    editorOn(log);
    await act(() => router.navigate("/calendar/Tagebuch"));
    expect(await screen.findByRole("heading", { level: 1, name: "Tagebuch" })).toBeInTheDocument();
    await hideAgain(queryClient, router.navigate, { tell: true, pathname: "/calendar/Tagebuch" });
    await doneHiding(router);
    expect(log).toEqual(["save", "hide"]);
    await waitFor(() => expectForgotten(queryClient));
  });

  it("ends its hiding even when a step fails", async () => {
    const { queryClient, router } = await openDiary(null);
    vi.spyOn(useConsole.getState(), "reset").mockImplementation(() => {
      throw new Error("broken");
    });
    await expect(hideAgain(queryClient, router.navigate, { tell: false, pathname: "/notes/Tagebuch/a.md" })).rejects.toThrow("broken");
    expect(useDiscreet.getState().hiding).toBe(false);
  });

  it("drops a discreet vault's mutation that settles after the hide", async () => {
    const { log, queryClient, router } = await openDiary(null);
    editorOn(log);
    let settle = () => {};
    const mutation = queryClient.getMutationCache().build(queryClient, { mutationFn: () => new Promise<void>((resolve) => (settle = resolve)) });
    void mutation.execute({ path: "/Tagebuch/a.md" });
    await userEvent.click(hideButton());
    await doneHiding(router);
    act(() => settle());
    await waitFor(() => expect(JSON.stringify(queryClient.getMutationCache().getAll().map((m) => m.state.variables))).not.toContain("Tagebuch"));
  });

  it("stops dropping the vault's answers once it is shown again", async () => {
    const { log, queryClient, router, showAgain } = await openDiary(null);
    editorOn(log);
    await userEvent.click(hideButton());
    await doneHiding(router);
    await showAgain();
    act(() => void queryClient.setQueryData(["note", "/Tagebuch/a.md"], { path: "/Tagebuch/a.md", content: "Text.", version: "v1" }));
    await act(() => new Promise<void>((resolve) => queueMicrotask(resolve)));
    expect(queryClient.getQueryData(["note", "/Tagebuch/a.md"])).toBeDefined();
  });

  it("doesn't follow a session read that answers, after the hide, as it was before", async () => {
    let stale: ((r: Response) => void) | null = null;
    let state: "shown" | "next is stale" | "hidden" = "shown";
    const { log, queryClient, router } = await openDiary(inMinutes(10), {
      "GET /api/session": () => {
        if (state === "next is stale") {
          state = "hidden";
          return new Promise<Response>((resolve) => (stale = resolve));
        }
        return json(200, { user, previousSignInAt: null, discreet: { revealed: state === "shown", until: state === "shown" ? inMinutes(10) : null } });
      },
    });
    editorOn(log);
    state = "next is stale";
    void queryClient.invalidateQueries({ queryKey: ["session"] });
    await waitFor(() => expect(stale).not.toBeNull());
    // It answers just as the tab leaves the discreet note, before the hide is done.
    const answer = { user, previousSignInAt: null, discreet: { revealed: true, until: inMinutes(10) } };
    router.subscribe((s) => {
      if (s.location.pathname !== "/notes/Work/a.md" || !stale) return;
      stale(json(200, answer));
      stale = null;
      queryClient.setQueryData(["session"], answer);
    });
    const shownAgain = vi.fn();
    const stop = useDiscreet.subscribe((s, previous) => void (s.revealed && !previous.revealed && shownAgain()));
    await userEvent.click(hideButton());
    await doneHiding(router);
    await waitFor(() => expect(queryClient.getQueryData<{ discreet: { revealed: boolean } }>(["session"])?.discreet.revealed).toBe(false));
    expect(useDiscreet.getState().revealed).toBe(false);
    expect(shownAgain).not.toHaveBeenCalled();
    stop();
  });

  it("forgets a held draft when another user signs in", async () => {
    const { log, net, router } = await openDiary(inMinutes(10));
    editorOn(log, false);
    net.offline = true;
    await userEvent.click(hideButton());
    await doneHiding(router);
    expect(holding()).toBe(true);
    act(() => useSession.getState().signedIn({ user: { ...user, id: "u2", username: "bob" }, previousSignInAt: null }));
    expect(holding()).toBe(false);
  });

  it("holds the open note's text over a draft a session end left", async () => {
    const writes: string[] = [];
    const put = async (r: Request) => {
      writes.push(`${new URL(r.url).pathname} ${((await r.clone().json()) as { content: string }).content}`);
      return json(200, { path: "/Tagebuch/a.md", content: "", version: "v2", lastWriter: "ada", lastWriteAt: "2026-11-09T10:00:00Z" });
    };
    const { log, net, router } = await openDiary(inMinutes(10), { "PUT /api/notes/Tagebuch/a.md": put, "PUT /api/notes/Tagebuch/b.md": put });
    editorOn(log, false);
    useEditor.setState({ draft: { username: "ada", path: "/Tagebuch/b.md", text: "Älter", version: "v1" } });
    net.offline = true;
    await userEvent.click(hideButton());
    await doneHiding(router);
    net.offline = false;
    act(() => void window.dispatchEvent(new Event("online")));
    await waitFor(() => expect(writes).toEqual(["/api/notes/Tagebuch/a.md Liebes Tagebuch"]));
  });

  it("while the browser says offline, waits for it to be online again, not for the while", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const onLine = vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
    const { log, net, router, requests } = await openDiary(inMinutes(10), {
      "PUT /api/notes/Tagebuch/a.md": () => json(200, { path: "/Tagebuch/a.md", content: "Liebes Tagebuch", version: "v2", lastWriter: "ada", lastWriteAt: "2026-11-09T10:00:00Z" }),
    });
    editorOn(log, false);
    net.offline = true;
    await userEvent.click(hideButton());
    await doneHiding(router);
    await act(() => vi.advanceTimersByTimeAsync(2 * 60_000));
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(0);
    expect(holding()).toBe(true);
    net.offline = false;
    onLine.mockReturnValue(true);
    act(() => void window.dispatchEvent(new Event("online")));
    await waitFor(() => expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1));
    expect(holding()).toBe(false);
  });

  it("while the browser says offline all along, tries once at the end of the late-save window", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.spyOn(navigator, "onLine", "get").mockReturnValue(false);
    const { log, net, router, requests } = await openDiary(inMinutes(10));
    editorOn(log, false);
    net.offline = true;
    await userEvent.click(hideButton());
    await doneHiding(router);
    await act(() => vi.advanceTimersByTimeAsync(4 * 60_000));
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(0);
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1);
    expect(holding()).toBe(false);
    await act(() => vi.advanceTimersByTimeAsync(10 * 60_000));
    expect(requests("PUT", "/api/notes/Tagebuch/a.md")).toHaveLength(1);
  });

  it("✕ during a slow save, with the end and a hidden frame on top, saves and hides once", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { queryClient, router, requests, hidden } = await openDiary(inMinutes(1));
    let finish = (_: boolean) => {};
    // The save ahead of the end finds nothing to do; the one of the ✕ takes its time.
    const beforeRead = vi.fn(() => (beforeRead.mock.calls.length === 1 ? Promise.resolve(true) : new Promise<boolean>((resolve) => (finish = resolve))));
    useEditor.setState({ beforeRead, unsaved: () => null });
    await waitFor(() => expect(FakeSocket.all.length).toBeGreaterThan(0));
    FakeSocket.all.at(-1)!.open();
    await act(() => vi.advanceTimersByTimeAsync(60_000 - SAVE_AHEAD + 1_000));
    expect(beforeRead).toHaveBeenCalledTimes(1);
    await userEvent.click(hideButton());
    expect(beforeRead).toHaveBeenCalledTimes(2);
    // Meanwhile the time runs out, and another tab's hide arrives: both join the one under way.
    await act(() => vi.advanceTimersByTimeAsync(SAVE_AHEAD));
    hidden();
    act(() => FakeSocket.all.at(-1)!.frame({ type: "hidden" }));
    act(() => finish(true));
    await doneHiding(router);
    await waitFor(() => expectForgotten(queryClient));
    expect(beforeRead).toHaveBeenCalledTimes(2);
    expect(requests("POST", "/api/account/discreet/hide")).toHaveLength(1);
  });

  it("✕ leaves an ordinary note's unsaved text to its own guard", async () => {
    const sent: string[] = [];
    const { router } = await openDiary(null, {
      // The note is still there: the editor's version check finds no deletion.
      "GET /api/version/Work/a.md": () => json(200, { path: "/Work/a.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-09T09:00:00Z" }),
      "PUT /api/notes/Work/a.md": async (r) => {
        sent.push(((await r.clone().json()) as { content: string }).content);
        return json(500, { error: "Broken." });
      },
    });
    await act(() => router.navigate("/notes/Work/a.md"));
    await headingShown("a");
    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    await userEvent.type(await screen.findByRole("textbox", { name: "Markdown source" }), "{Control>}{End}{/Control} Mehr.");
    // After the 800 ms of the autosave.
    await screen.findByRole("alert", { name: "Not saved" }, { timeout: 3_000 });
    const failed = sent.length;
    expect(sent.at(-1)).toBe("Text. Mehr.");
    await userEvent.click(hideButton());
    await doneHiding(router);
    // The hide doesn't go away from an ordinary note; Read mode takes the editor, whose last save sends the text.
    await waitFor(() => expect(sent).toHaveLength(failed + 1));
    expect(sent.at(-1)).toBe("Text. Mehr.");
    expect(router.state.location.pathname).toBe("/notes/Work/a.md");
  });
});

describe("What's on and the badge, as the discreet vaults are hidden again", () => {
  const setAppBadge = vi.fn(async (_count?: number) => {});
  const clearAppBadge = vi.fn(async () => {});
  afterEach(() => {
    delete (navigator as { setAppBadge?: unknown }).setAppBadge;
    delete (navigator as { clearAppBadge?: unknown }).clearAppBadge;
    setAppBadge.mockClear();
    clearAppBadge.mockClear();
  });

  it("drops the hidden vault's share at once, on screen and on the app's icon, and asks again", async () => {
    Object.defineProperty(navigator, "setAppBadge", { value: setAppBadge, configurable: true });
    Object.defineProperty(navigator, "clearAppBadge", { value: clearAppBadge, configurable: true });
    let diary: Awaited<ReturnType<typeof openDiary>> | undefined;
    const shown = () => !diary?.log.includes("hide");
    diary = await openDiary(null, {
      "GET /api/calendar/whats-on": () =>
        json(200, {
          items: [],
          ...(shown()
            ? { count: 3, overdue: true, vaults: [{ vault: "Work", count: 1, overdue: false }, { vault: "Tagebuch", count: 2, overdue: true }] }
            : { count: 1, overdue: false, vaults: [{ vault: "Work", count: 1, overdue: false }] }),
          couldntRead: 0,
          couldntReadVaults: [],
        }),
    });
    // The phone's tree button carries the count.
    const badge = () => screen.getByRole("button", { name: "Open file tree" });
    await waitFor(() => expect(badge()).toHaveAccessibleDescription("3 on, some overdue"));
    // The app's icon never counts a discreet vault, shown or not: Work's share only.
    await waitFor(() => expect(setAppBadge).toHaveBeenLastCalledWith(1));
    expect(setAppBadge).not.toHaveBeenCalledWith(3);
    setAppBadge.mockClear();
    editorOn(diary.log);
    await userEvent.click(hideButton());
    await doneHiding(diary.router);
    // Nothing of the count as it was stays, not even for a moment: the icon is cleared first, the count asked again.
    expect(clearAppBadge).toHaveBeenCalled();
    expect(badge()).not.toHaveAccessibleDescription(/3 on/);
    await waitFor(() => expect(badge()).toHaveAccessibleDescription("1 on"));
    expect(setAppBadge).toHaveBeenLastCalledWith(1);
    await waitFor(() => expectForgotten(diary!.queryClient));
  });

  it("takes the hidden vault's entries off a What's on page left open", async () => {
    let hidden = false;
    const entry = (vault: string, title: string) => ({ ...entryView(title, { path: `/${vault}/${title}.md`, vault }) });
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: !hidden, until: null } }),
      "GET /api/vaults": () => json(200, hidden ? [vault("Work")] : [vault("Work"), vault("Tagebuch", true)]),
      "GET /api/calendar/whats-on": () =>
        json(200, { items: hidden ? [entry("Work", "Report")] : [entry("Tagebuch", "Brief"), entry("Work", "Report")], count: hidden ? 1 : 2, overdue: false, vaults: [], couldntRead: 0, couldntReadVaults: [] }),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/whats-on", queryClient);
    expect(await screen.findByRole("link", { name: "Brief" })).toBeInTheDocument();
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    hidden = true;
    await act(() => hideAgain(queryClient, router.navigate, { tell: false, pathname: "/whats-on" }));
    await waitFor(() => expect(screen.queryByRole("link", { name: "Brief" })).not.toBeInTheDocument());
    expect(await screen.findByRole("link", { name: "Report" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Brief" })).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/whats-on");
  });

  it("hiding again empties Recent changes on screen", async () => {
    let hidden = false;
    const row = (vault: string, title: string) => ({ path: `/${vault}/${title}.md`, vault, title, folder: vault, kind: "note", flag: null, writer: "alice", writerKind: "self", writtenAt: new Date().toISOString() });
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: !hidden, until: null } }),
      "GET /api/vaults": () => json(200, hidden ? [vault("Work")] : [vault("Work"), vault("Tagebuch", true)]),
      "GET /api/calendar/whats-on": () => json(200, { items: [], count: 0, overdue: false, vaults: [], couldntRead: 0, couldntReadVaults: [] }),
      "GET /api/recent": () =>
        json(200, hidden ? { items: [row("Work", "Report")], more: false, vaults: ["Work"] } : { items: [row("Tagebuch", "Brief"), row("Work", "Report")], more: false, vaults: ["Tagebuch", "Work"] }),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/recent", queryClient);
    expect(await screen.findByRole("link", { name: /Brief/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tagebuch" })).toBeInTheDocument();
    await waitFor(() => expect(useDiscreet.getState().revealed).toBe(true));
    hidden = true;
    await act(() => hideAgain(queryClient, router.navigate, { tell: false, pathname: "/recent" }));
    await waitFor(() => expect(screen.queryByRole("link", { name: /Brief/ })).not.toBeInTheDocument());
    expect(await screen.findByRole("link", { name: /Report/ })).toBeInTheDocument();
    expect(document.body).not.toHaveTextContent("Tagebuch");
    expect(router.state.location.pathname).toBe("/recent");
  });

});
