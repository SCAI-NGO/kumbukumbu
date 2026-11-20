// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useConsole } from "../console/store";
import { useDiscreet } from "../discreet/state";
import { startOver } from "../shell/lastNote";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { useSyncStatus } from "../state/sync";
import { fakeApi, json, navigateAway, noContent, renderApp, user } from "../test/app";
import { FakeSocket } from "../test/socket";
import { useWindows } from "./store";

beforeEach(() => {
  window.innerWidth = 390;
  localStorage.clear();
  startOver();
});

afterEach(async () => {
  await waitFor(() => expect(useDiscreet.getState().hiding).toBe(false));
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  useDiscreet.setState({ gone: new Set() });
  useConsole.getState().reset();
  useSyncStatus.setState({ connected: false, editors: {} });
});

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const tree = (name: string, notes: string[]) => json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: notes.map((n) => note(`/${name}/${n}`)) } });
const vault = (name: string, discreet = false) => ({ name, notes: 2, embeddings: "internal", discreet });

/** Work, and while `shown()` says so the discreet Tagebuch; every note reads "Text of <path>." */
function signedInApi(shown = () => false, extra: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: shown(), until: null } }),
    "GET /api/vaults": () => json(200, shown() ? [vault("Work"), vault("Tagebuch", true)] : [vault("Work")]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => tree("Work", ["inbox.md", "readme.md"]),
    "GET /api/vaults/Tagebuch/tree": () => (shown() ? tree("Tagebuch", ["a.md"]) : json(404, { error: "You have no vault named Tagebuch." })),
    "GET /api/vaults/Work/publications": () => json(200, { publications: [], unpublishable: [] }),
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      if (path.startsWith("/Tagebuch/") && !shown()) return json(404, { error: "You have no vault named Tagebuch." });
      return json(200, { path, content: `Text of ${path}.`, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-17T09:00:00Z" });
    },
    "PUT /api/notes/*": async (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      const { content } = (await r.json()) as { content: string };
      return json(200, { path, content, version: "v2", lastWriter: "ada", lastWriteAt: "2026-11-17T09:01:00Z" });
    },
    "GET /api/version/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/version".length));
      return json(200, { path, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-17T09:00:00Z" });
    },
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "POST /api/ws-ticket": () => json(503, { error: "later" }),
    "PUT /api/account/discreet/open-note": () => noContent(),
    ...extra,
  });
}

const consoleWindow = () => screen.getByRole("dialog", { name: "Console" });
const noteReads = (fetch: ReturnType<typeof fakeApi>) => fetch.mock.calls.filter(([r]) => (r as Request).method === "GET" && new URL((r as Request).url).pathname.startsWith("/api/notes/")).length;

async function openConsoleFromDrawer() {
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  const account = within(screen.getByRole("dialog", { name: "File tree" })).getByRole("navigation", { name: "Account" });
  await userEvent.click(within(account).getByRole("link", { name: "Console" }));
  return screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
}

describe("a window over the notes", () => {
  it("opens over the note shown", async () => {
    signedInApi();
    const router = renderApp("/notes/Work/inbox.md");
    await screen.findByText("Text of /Work/inbox.md.");
    await openConsoleFromDrawer();
    expect(router.state.location.pathname).toBe("/console");
    expect(screen.getByText("Text of /Work/inbox.md.")).toBeInTheDocument();
    expect(screen.getByTestId("shell").parentElement).toHaveAttribute("inert");
  });

  it.each([
    ["✕", () => userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }))],
    ["Esc", () => userEvent.keyboard("{Escape}")],
    ["Back", (router: ReturnType<typeof renderApp>) => act(() => router.navigate(-1))],
  ])("closes back to the note with %s, leaving no entry that Back would reopen", async (_, close) => {
    signedInApi();
    const router = renderApp(["/notes/Work/readme.md", "/notes/Work/inbox.md"]);
    await screen.findByText("Text of /Work/inbox.md.");
    await openConsoleFromDrawer();
    await close(router);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/inbox.md"));
    expect(screen.queryByRole("dialog", { name: "Console" })).not.toBeInTheDocument();
    expect(screen.getByTestId("shell").parentElement).not.toHaveAttribute("inert");
    expect(screen.getByText("Text of /Work/inbox.md.")).toBeInTheDocument();
    await act(() => router.navigate(-1));
    expect(router.state.location.pathname).toBe("/notes/Work/readme.md");
  });

  it.each([
    ["while its save is under way", () => new Promise<Response>(() => {})],
    ["when its save fails", () => json(500, { error: "Broken." })],
  ])("keeps Write mode and its draft unsaved, opening without waiting for a save, %s", async (_, put) => {
    const fetch = signedInApi(undefined, { "PUT /api/notes/*": put });
    const router = renderApp("/notes/Work/inbox.md");
    const write = await screen.findByRole("button", { name: "Write" });
    await waitFor(() => expect(write).toBeEnabled());
    await userEvent.click(write);
    // On its first load the editor replaces a stand-in; it takes the focus once it is in.
    await waitFor(() => expect(screen.getByRole("textbox", { name: "Markdown source" })).toHaveFocus());
    const source = screen.getByRole("textbox", { name: "Markdown source" });
    await userEvent.type(source, "Draft");
    const draft = (source as HTMLTextAreaElement).value;
    expect(draft).toContain("Draft");
    const reads = noteReads(fetch);
    const puts = () => fetch.mock.calls.filter(([r]) => (r as Request).method === "PUT" && new URL((r as Request).url).pathname.startsWith("/api/notes/")).length;
    const putsBefore = puts();
    // Opened in-app, as the drawer's link does.
    await navigateAway(router, "/console");
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Console" })).not.toBeInTheDocument());
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
    expect(useEditor.getState().mode).toBe("write");
    expect(screen.getByRole("textbox", { name: "Markdown source" })).toHaveValue(draft);
    expect(useEditor.getState().unsaved?.()?.text).toBe(draft);
    // The blur as the focus moves into the window saves, as the autosave would anyway; nothing waits for it.
    expect(puts()).toBeLessThanOrEqual(putsBefore + 1);
    expect(noteReads(fetch)).toBe(reads);
  });

  it("gives the focus to the top bar on closing, the drawer it was opened from being gone", async () => {
    signedInApi();
    renderApp(["/notes/Work/readme.md", "/notes/Work/inbox.md"]);
    await screen.findByText("Text of /Work/inbox.md.");
    await openConsoleFromDrawer();
    expect(screen.getByRole("textbox", { name: "Command" })).toHaveFocus();
    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Console" })).not.toBeInTheDocument());
    expect(screen.getByRole("button", { name: "Open file tree" })).toHaveFocus();
  });

  it("gives the focus to the top bar on closing a window reached by a deep link, which no control opened", async () => {
    signedInApi();
    renderApp("/console");
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    await screen.findByRole("heading", { name: "Your vaults" });
    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Console" })).not.toBeInTheDocument());
    expect(screen.getByRole("button", { name: "Open file tree" })).toHaveFocus();
  });

  it("doesn't jump to where the note was opened at again, as a window opens and closes", async () => {
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    signedInApi();
    // Followed in-app, so that its entry has a key of its own (a first entry's is "default", as a keyless one's).
    const router = renderApp("/notes/Work/readme.md");
    await screen.findByText("Text of /Work/readme.md.");
    await act(() => router.navigate("/notes/Work/inbox.md", { state: { line: 1 } }));
    const text = await screen.findByText("Text of /Work/inbox.md.", { selector: "p" });
    await waitFor(() => expect(scrollIntoView.mock.contexts).toContain(text));
    scrollIntoView.mockClear();
    await navigateAway(router, "/console");
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/inbox.md"));
    await act(async () => {});
    expect(scrollIntoView.mock.contexts).not.toContain(text);
  });

  it("on a deep link shows the window over the kept note, and closes to it", async () => {
    signedInApi();
    localStorage.setItem("kumbu.lastNote.u1", "/Work/inbox.md");
    const router = renderApp("/console");
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    expect(await screen.findByText("Text of /Work/inbox.md.")).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/console");
    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/inbox.md"));
    expect(router.state.historyAction).toBe("REPLACE");
    expect(screen.getByText("Text of /Work/inbox.md.")).toBeInTheDocument();
  });

  it("on a deep link on a fresh device closes to the vault list", async () => {
    signedInApi();
    const router = renderApp("/console");
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(router.state.historyAction).toBe("REPLACE");
    expect(screen.getByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
  });

  it("over a discreet note, closes back to it, its own address naming no vault", async () => {
    signedInApi(() => true);
    const router = renderApp("/notes/Tagebuch/a.md");
    await screen.findByText("Text of /Tagebuch/a.md.");
    await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
    await act(() => router.navigate("/console"));
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    expect(router.state.location.pathname).toBe("/console");
    expect(screen.getByText("Text of /Tagebuch/a.md.")).toBeInTheDocument();
    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Tagebuch/a.md"));
    expect(screen.getByText("Text of /Tagebuch/a.md.")).toBeInTheDocument();
  });

  it("stays open as the discreet vault behind it hides, and then closes to the vault list", async () => {
    let shown = true;
    signedInApi(() => shown);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/notes/Tagebuch/a.md", queryClient);
    await screen.findByText("Text of /Tagebuch/a.md.");
    await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
    await act(() => router.navigate("/console"));
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    await userEvent.type(screen.getByRole("textbox", { name: "Command" }), "pw");

    // Hidden elsewhere: the session read again says so.
    shown = false;
    await act(() => queryClient.refetchQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: false, hiding: false }));
    await screen.findByRole("heading", { name: "Your vaults" });
    expect(router.state.location.pathname).toBe("/console");
    expect(screen.getByRole("textbox", { name: "Command" })).toHaveValue("pw");

    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(screen.getByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    await waitFor(() => expect(document.body.textContent).not.toContain("Tagebuch"));
  });

  it("closes to the vault list after the discreet vault behind it hides, also with an ordinary note read before", async () => {
    let shown = true;
    signedInApi(() => shown);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/notes/Work/readme.md", queryClient);
    await screen.findByText("Text of /Work/readme.md.");
    await act(() => router.navigate("/notes/Tagebuch/a.md"));
    await screen.findByText("Text of /Tagebuch/a.md.");
    await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
    await act(() => router.navigate("/console"));
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });

    shown = false;
    await act(() => queryClient.refetchQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: false, hiding: false }));

    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(router.state.location.state).toMatchObject({ overview: true });
    expect(screen.getByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    await act(async () => {});
    expect(router.state.location.pathname).toBe("/");
    expect(document.body.textContent).not.toContain("Tagebuch");
  });

  it("stays open without the discreet note behind it when the vault list was read again before the hide", async () => {
    let shown = true;
    signedInApi(() => shown);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/notes/Tagebuch/a.md", queryClient);
    await screen.findByText("Text of /Tagebuch/a.md.");
    await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
    await act(() => router.navigate("/console"));
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    await userEvent.type(screen.getByRole("textbox", { name: "Command" }), "pw");

    // Hidden elsewhere: the vault list is read again before the session that says so.
    shown = false;
    await act(() => queryClient.refetchQueries({ queryKey: ["vaults"] }));
    await waitFor(() => expect(useDiscreet.getState().names.size).toBe(0));
    await act(() => queryClient.refetchQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: false, hiding: false }));
    expect(router.state.location.pathname).toBe("/console");
    expect(screen.getByRole("textbox", { name: "Command" })).toHaveValue("pw");
    await waitFor(() => expect(useWindows.getState().behind?.pathname ?? "").not.toContain("Tagebuch"));
    await waitFor(() => expect(document.body.textContent).not.toContain("Tagebuch"));
  });

  it("stays open as the discreet vault beside the note behind it hides, and then closes to that note alone", async () => {
    let shown = true;
    signedInApi(() => shown);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/notes/Work/inbox.md?beside=Tagebuch/a.md", queryClient);
    await screen.findByText("Text of /Work/inbox.md.");
    await waitFor(() => expect(useEditor.getState().panes.right?.path).toBe("/Tagebuch/a.md"));
    await act(() => router.navigate("/console"));
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });

    shown = false;
    await act(() => queryClient.refetchQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: false, hiding: false }));
    expect(router.state.location.pathname).toBe("/console");
    expect(useEditor.getState().panes.right).toBeNull();

    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname + router.state.location.search).toBe("/notes/Work/inbox.md"));
    expect(screen.getByText("Text of /Work/inbox.md.")).toBeInTheDocument();
    expect(useEditor.getState().panes.right).toBeNull();
  });

  it("stays open as the discreet vault of the note behind it hides, and then closes to the note beside it alone", async () => {
    let shown = true;
    signedInApi(() => shown);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/notes/Tagebuch/a.md?beside=Work/inbox.md", queryClient);
    await waitFor(() => expect(useEditor.getState().panes.right?.path).toBe("/Work/inbox.md"));
    await act(() => router.navigate("/console"));
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });

    shown = false;
    await act(() => queryClient.refetchQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: false, hiding: false }));
    expect(router.state.location.pathname).toBe("/console");

    await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(router.state.location.pathname + router.state.location.search).toBe("/notes/Work/inbox.md"));
    expect(await screen.findByText("Text of /Work/inbox.md.")).toBeInTheDocument();
    expect(useEditor.getState().panes).toEqual({ left: { path: "/Work/inbox.md", mode: "read" }, right: null });
  });

  it("leaves nothing of a peeked discreet note in the console as its vault hides, and reads it no more", async () => {
    let shown = true;
    const fetch = signedInApi(() => shown);
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/notes/Work/inbox.md", queryClient);
    await screen.findByText("Text of /Work/inbox.md.");
    await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
    await act(() => router.navigate("/console"));
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    await userEvent.type(screen.getByRole("textbox", { name: "Command" }), "p /Tagebuch/a.md{Enter}");
    await within(consoleWindow()).findByText("Text of /Tagebuch/a.md.");

    shown = false;
    await act(() => queryClient.refetchQueries({ queryKey: ["session"] }));
    await waitFor(() => expect(useDiscreet.getState()).toMatchObject({ revealed: false, hiding: false }));
    const diaryReads = () => fetch.mock.calls.filter(([r]) => new URL((r as Request).url).pathname === "/api/notes/Tagebuch/a.md").length;
    const reads = diaryReads();
    await waitFor(() => expect(consoleWindow().textContent).not.toContain("Tagebuch"));
    await act(() => queryClient.invalidateQueries());
    expect(diaryReads()).toBe(reads);
  });

  describe("as the sync socket moves the note behind it", () => {
    beforeEach(() => {
      FakeSocket.all = [];
      vi.stubGlobal("WebSocket", FakeSocket);
    });

    /** Work and Garten, while `at.names` lists them; the socket gets a ticket. */
    function socketApi(at: { names: string[] }) {
      return signedInApi(undefined, {
        "GET /api/vaults": () => json(200, at.names.map((n) => vault(n))),
        "GET /api/vaults/Garten/tree": () => tree("Garten", ["inbox.md"]),
        "POST /api/ws-ticket": () => json(200, { ticket: "t1", expiresAt: "2026-11-17T10:00:30Z" }),
      });
    }

    /** The console open over /notes/Work/inbox.md, reached in-app, "pw" typed into it, the socket open. */
    async function consoleOverInbox(at: { names: string[] }) {
      const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
      const router = renderApp(["/notes/Work/readme.md", "/notes/Work/inbox.md"], queryClient);
      await screen.findByText("Text of /Work/inbox.md.");
      await waitFor(() => expect(FakeSocket.all).toHaveLength(1));
      FakeSocket.all.at(-1)!.open();
      await waitFor(() => expect(queryClient.getQueryData<{ name: string }[]>(["vaults"])?.map((v) => v.name)).toEqual(at.names));
      await navigateAway(router, "/console");
      await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
      await userEvent.type(screen.getByRole("textbox", { name: "Command" }), "pw");
      return { router, queryClient };
    }

    it("stays open as the vault behind it is taken away, and then closes to the vault list", async () => {
      const at = { names: ["Work", "Garten"] };
      socketApi(at);
      const { router, queryClient } = await consoleOverInbox(at);

      // Marked discreet in another session: the socket is closed for it, opens again and the list no longer has Work.
      at.names = ["Garten"];
      FakeSocket.all.at(-1)!.drop(4412);
      await waitFor(() => expect(FakeSocket.all).toHaveLength(2));
      FakeSocket.all.at(-1)!.open();
      await waitFor(() => expect(queryClient.getQueryData<{ name: string }[]>(["vaults"])?.map((v) => v.name)).toEqual(["Garten"]));

      await screen.findByRole("heading", { name: "Your vaults" });
      expect(router.state.location.pathname).toBe("/console");
      expect(screen.getByRole("dialog", { name: "Console" })).toBeInTheDocument();
      expect(screen.getByRole("textbox", { name: "Command" })).toHaveValue("pw");

      await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
      await waitFor(() => expect(router.state.location.pathname).toBe("/"));
      expect(router.state.historyAction).toBe("REPLACE");
      expect(screen.getByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
    });

    it("stays open as the vault behind it is renamed in another tab, and then closes to the renamed note", async () => {
      const at = { names: ["Work", "Garten"] };
      socketApi(at);
      const { router } = await consoleOverInbox(at);

      FakeSocket.all.at(-1)!.frame({ type: "renamed", vault: "Garten", from: "Work" });

      expect(await screen.findByText("Text of /Garten/inbox.md.")).toBeInTheDocument();
      expect(router.state.location.pathname).toBe("/console");
      expect(screen.getByRole("textbox", { name: "Command" })).toHaveValue("pw");

      await userEvent.click(within(consoleWindow()).getByRole("button", { name: "Close" }));
      await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Garten/inbox.md"));
      expect(router.state.historyAction).toBe("REPLACE");
      expect(screen.getByText("Text of /Garten/inbox.md.")).toBeInTheDocument();
    });
  });
});
