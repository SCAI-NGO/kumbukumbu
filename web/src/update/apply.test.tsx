// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { page, tryReload } from "./apply";
import { useUpdate } from "./store";
import UpdateBar from "./UpdateBar";

const v = vi.hoisted(() => ({ app: "0.1.0-pre.41+aaaaaaa" }));
vi.mock("./version", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./version")>();
  return {
    ...actual,
    get appVersion() {
      return v.app;
    },
  };
});

const NEWER = "0.2.0+a6362ec";
const initialUpdate = useUpdate.getState();
let reload: ReturnType<typeof vi.fn>;
/** What was stored for after the reload, at the moment of reloading (a stubbed reload leaves the page running). */
let atReload: unknown;

beforeEach(() => {
  atReload = undefined;
  reload = vi.fn(() => {
    atReload = JSON.parse(sessionStorage.getItem("kumbu.update.restore") ?? "null");
  });
  vi.spyOn(page, "reload").mockImplementation(reload);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  v.app = "0.1.0-pre.41+aaaaaaa";
  sessionStorage.clear();
  useUpdate.setState(initialUpdate, true);
  useSession.getState().reset();
  useEditor.getState().reset();
  // A stubbed visibilityState goes back to jsdom's own.
  delete (document as { visibilityState?: unknown }).visibilityState;
});

const note = (path: string) => ({ path, content: `# ${path}\n\nSome text.`, version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" });
const tree = { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: ["plan.md", "inbox.md"].map((n) => ({ name: n, path: `/Work/${n}`, size: 1 })) } };

/** The server, at `server`'s version: on /api/version only, as an older server would answer the rest without it. */
function serverAt(server: string) {
  return fakeApi({
    "GET /api/version": () => {
      const response = json(200, { version: server });
      response.headers.set("X-Kumbu-Version", server);
      return response;
    },
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2 }]),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/changes": () => json(200, []),
    "GET /api/notes/*": (r) => json(200, note(decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)))),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "PUT /api/notes/*": () => json(200, { version: "v2" }),
  });
}

function becomeVisible() {
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "visible" });
  act(() => {
    document.dispatchEvent(new Event("visibilitychange"));
  });
}


describe("applying an update", () => {
  it("reloads at the next navigation when nothing is unsaved", async () => {
    serverAt(NEWER);
    const router = renderApp("/notes/Work/plan.md");
    await screen.findByText("Some text.");

    act(() => useUpdate.getState().report(NEWER));
    expect(reload).not.toHaveBeenCalled();
    await act(() => router.navigate("/notes/Work/inbox.md"));

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    expect(atReload).toEqual({ mode: "read", scrollTop: 0 });
    expect(sessionStorage.getItem("kumbu.update.reloadedFor")).toBe(NEWER);
  });

  it("reloads when the page becomes visible again", async () => {
    const fetch = serverAt(NEWER);
    renderApp("/notes/Work/plan.md");
    const scroller = (await screen.findByRole("article")).parentElement!;
    scroller.scrollTop = 120;

    becomeVisible();

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    expect(fetch.mock.calls.some(([r]) => new URL((r as Request).url).pathname === "/api/version")).toBe(true);
    expect(atReload).toEqual({ mode: "read", scrollTop: 120 });
  });

  it("asks the editor to save before reloading in Write mode", async () => {
    const order: string[] = [];
    const beforeRead = vi.fn(async () => {
      order.push("save");
      return true;
    });
    useEditor.setState({ mode: "write", beforeRead });
    useUpdate.getState().report(NEWER);

    expect(await tryReload("auto", () => order.push("reload"))).toBe(true);

    expect(beforeRead).toHaveBeenCalledWith({ reread: false });
    expect(order).toEqual(["save", "reload"]);
    expect(JSON.parse(sessionStorage.getItem("kumbu.update.restore")!).mode).toBe("write");
  });

  it("never reloads while the editor can't save", async () => {
    useEditor.setState({ mode: "write", beforeRead: vi.fn(async () => false) });
    useUpdate.getState().report(NEWER);
    render(<UpdateBar />);

    expect(await tryReload("auto")).toBe(false);

    expect(reload).not.toHaveBeenCalled();
    const bar = screen.getByRole("region", { name: "Update" });
    expect(bar).toHaveTextContent("Kumbu² 0.2.0 is available");
    await userEvent.click(within(bar).getByRole("button", { name: "Update" }));
    expect(reload).not.toHaveBeenCalled();
    expect(sessionStorage.getItem("kumbu.update.reloadedFor")).toBeNull();
  });

  it("reloads into a newer version from the bar once the text is saved", async () => {
    useEditor.setState({ mode: "write", beforeRead: vi.fn(async () => true) });
    useUpdate.getState().report(NEWER);
    render(<UpdateBar />);

    await userEvent.click(within(screen.getByRole("region", { name: "Update" })).getByRole("button", { name: "Update" }));

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it("reloads once per version, then says it couldn't", async () => {
    sessionStorage.setItem("kumbu.update.reloadedFor", NEWER);
    serverAt(NEWER);
    const router = renderApp("/notes/Work/plan.md");
    await screen.findByText("Some text.");

    becomeVisible();
    await act(() => router.navigate("/notes/Work/inbox.md"));

    expect(await screen.findByText("Kumbu² couldn't load version 0.2.0. Reload the page, or ask your admin.")).toBeInTheDocument();
    expect(reload).not.toHaveBeenCalled();
  });

  it("says it updated, on the same note, in the same mode", async () => {
    v.app = NEWER;
    sessionStorage.setItem("kumbu.update.reloadedFor", NEWER);
    sessionStorage.setItem("kumbu.update.restore", JSON.stringify({ mode: "write", scrollTop: 240 }));
    serverAt(NEWER);
    renderApp("/notes/Work/plan.md");

    expect(await screen.findByRole("status", { name: "Updated" })).toHaveTextContent("Updated to 0.2.0");
    const source = await screen.findByLabelText("Markdown source");
    expect(useEditor.getState().mode).toBe("write");
    expect(source.scrollTop).toBe(240);
    expect(sessionStorage.getItem("kumbu.update.reloadedFor")).toBeNull();
    expect(sessionStorage.getItem("kumbu.update.restore")).toBeNull();
  });

  it("restores the editor's scroll position once, not on the next time in Write mode", async () => {
    v.app = NEWER;
    sessionStorage.setItem("kumbu.update.reloadedFor", NEWER);
    sessionStorage.setItem("kumbu.update.restore", JSON.stringify({ mode: "write", scrollTop: 240 }));
    serverAt(NEWER);
    renderApp("/notes/Work/plan.md");
    expect((await screen.findByLabelText("Markdown source")).scrollTop).toBe(240);

    await act(() => useEditor.getState().endWrite());
    await screen.findByRole("article");
    act(() => useEditor.getState().setMode("write"));

    expect((await screen.findByLabelText("Markdown source")).scrollTop).toBe(0);
  });

  it("lets the notice go after 4 seconds", async () => {
    vi.useFakeTimers();
    try {
      useUpdate.getState().markUpdated("0.2.0");
      render(<UpdateBar />);
      expect(screen.getByRole("status", { name: "Updated" })).toHaveTextContent("Updated to 0.2.0");

      act(() => vi.advanceTimersByTime(4_000));

      expect(screen.queryByRole("status", { name: "Updated" })).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("updates on the sign-in screen too", async () => {
    serverAt(NEWER);
    renderApp("/signin");
    await screen.findByRole("button", { name: /sign in/i });

    becomeVisible();

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
  });

  it("stores no path or content", async () => {
    serverAt(NEWER);
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Some text.");
    act(() => useUpdate.getState().report(NEWER));

    expect(await act(() => tryReload("auto"))).toBe(true);

    expect(sessionStorage.length).toBeGreaterThan(0);
    for (let i = 0; i < sessionStorage.length; i++) {
      expect(sessionStorage.getItem(sessionStorage.key(i)!)).toMatch(/^(\d[\w.\-+]*|\{"mode":"(read|write)","scrollTop":\d+\})$/);
    }
  });
});
