// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { useLeaveGuard } from "../components/shownOnce";
import { routerFuture } from "../router";
import userEvent from "@testing-library/user-event";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { holdUpdates, page, registerScrollTop, tryReload } from "./apply";
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
  registerScrollTop(null);
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
    registerScrollTop(() => 0);

    expect(await tryReload("auto", () => order.push("reload"))).toBe(true);

    expect(beforeRead).toHaveBeenCalledWith({ reread: false });
    expect(order).toEqual(["save", "save", "reload"]);
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

  it("restores Read mode's scroll position after the reload", async () => {
    v.app = NEWER;
    sessionStorage.setItem("kumbu.update.reloadedFor", NEWER);
    sessionStorage.setItem("kumbu.update.restore", JSON.stringify({ mode: "read", scrollTop: 180 }));
    serverAt(NEWER);
    renderApp("/notes/Work/plan.md");

    const scroller = (await screen.findByRole("article")).parentElement!;
    expect(scroller.scrollTop).toBe(180);
    expect(useEditor.getState().mode).toBe("read");
  });

  it("keeps no restore record from a page without a note", async () => {
    useEditor.setState({ mode: "write" });
    serverAt(NEWER);
    renderApp("/signin");
    await screen.findByRole("button", { name: /sign in/i });

    becomeVisible();

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
    expect(atReload).toBeNull();
  });

  it("waits with the reload while a search hit's section is still to be shown", async () => {
    serverAt(NEWER);
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Some text.");
    act(() => useUpdate.getState().report(NEWER));
    act(() => useEditor.getState().setScrollTarget({ path: "/Work/inbox.md", line: 3 }));

    expect(await act(() => tryReload("auto"))).toBe(false);
    act(() => useEditor.getState().setScrollTarget(null));
    expect(await act(() => tryReload("auto"))).toBe(true);
  });

  it("asks the editor again if text came in while it saved", async () => {
    let calls = 0;
    // The first save finishes, but a keystroke during it left new text: the second ask saves that too.
    const beforeRead = vi.fn(async () => ++calls >= 1);
    useEditor.setState({ mode: "write", beforeRead });
    useUpdate.getState().report(NEWER);

    expect(await tryReload("button")).toBe(true);

    expect(beforeRead).toHaveBeenCalledTimes(2);
  });

  it("does not reload if the second ask finds text it can't save", async () => {
    const answers = [true, false];
    useEditor.setState({ mode: "write", beforeRead: vi.fn(async () => answers.shift()!) });
    useUpdate.getState().report(NEWER);

    expect(await tryReload("button")).toBe(false);
    expect(reload).not.toHaveBeenCalled();
  });

  it("looks for a new release when a part of the app fails to load", async () => {
    serverAt(NEWER);
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Some text.");

    act(() => {
      window.dispatchEvent(new Event("vite:preloadError"));
    });

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
  });
});

describe("where the update says it (I1)", () => {
  it("puts the bar under the top bar in the shell, not over it", async () => {
    serverAt(NEWER);
    renderApp("/notes/Work/plan.md");
    await screen.findByText("Some text.");
    act(() => useEditor.getState().setMode("write"));
    await screen.findByLabelText("Markdown source");

    act(() => useUpdate.getState().report(NEWER));

    const bar = await screen.findByRole("region", { name: "Update" });
    expect(screen.getAllByRole("region", { name: "Update" })).toHaveLength(1);
    const shell = screen.getByTestId("shell");
    expect(shell).toContainElement(bar);
    const header = shell.querySelector("header")!;
    const main = shell.querySelector("main")!;
    expect(header.compareDocumentPosition(bar) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(bar.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(bar.closest(".fixed")).toBeNull();
  });

  it("puts the sentence above the page outside the shell", async () => {
    sessionStorage.setItem("kumbu.update.reloadedFor", NEWER);
    serverAt(NEWER);
    renderApp("/signin");
    await screen.findByRole("button", { name: /sign in/i });

    act(() => useUpdate.getState().report(NEWER));

    const alert = await screen.findByRole("alert");
    expect(alert.compareDocumentPosition(screen.getByRole("main")) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(alert.closest(".fixed")).toBeNull();
  });

  it("lets the couldn't-load sentence be dismissed, for good in this tab", async () => {
    sessionStorage.setItem("kumbu.update.reloadedFor", NEWER);
    serverAt(NEWER);
    const router = renderApp("/notes/Work/plan.md");
    await screen.findByText("Some text.");
    act(() => useUpdate.getState().report(NEWER));
    const alert = await screen.findByRole("alert");

    await userEvent.click(within(alert).getByRole("button", { name: "Dismiss" }));
    await act(() => router.navigate("/notes/Work/inbox.md"));
    becomeVisible();

    await screen.findByText("Some text.");
    expect(screen.queryByText(/couldn't load version/)).not.toBeInTheDocument();
    expect(reload).not.toHaveBeenCalled();
  });

  it("forgets a failure once the server runs this app's own build again", () => {
    sessionStorage.setItem("kumbu.update.reloadedFor", NEWER);
    useUpdate.getState().report(NEWER);
    useUpdate.getState().fail(useUpdate.getState().available!);

    useUpdate.getState().report("0.1.0-pre.41+aaaaaaa");

    expect(useUpdate.getState().failed).toBeNull();
    expect(useUpdate.getState().available).toBeNull();
  });
});

describe("what a reload must never lose (I2, I3)", () => {
  const draft = { username: "ada", path: "/Work/plan.md", text: "unsaved words", version: "v1" };

  it("never reloads over text a session end kept, and offers no Update there", async () => {
    useEditor.setState({ mode: "write", draft });
    serverAt(NEWER);
    renderApp("/signin");
    await screen.findByRole("button", { name: /sign in/i });

    act(() => useUpdate.getState().report(NEWER));
    becomeVisible();

    expect(await tryReload("button")).toBe(false);
    expect(screen.queryByRole("region", { name: "Update" })).not.toBeInTheDocument();
    await act(() => new Promise((r) => setTimeout(r, 50)));
    expect(reload).not.toHaveBeenCalled();
    expect(useEditor.getState().draft).toEqual(draft);
  });

  it("holds the reload while a secret is shown and not yet saved", async () => {
    function Secret() {
      const [saved, setSaved] = useState(false);
      useLeaveGuard(saved);
      return (
        <button type="button" onClick={() => setSaved(true)}>
          Copy
        </button>
      );
    }
    const router = createMemoryRouter([{ path: "/", element: <Secret /> }], { initialEntries: ["/"], future: routerFuture });
    render(<RouterProvider router={router} future={{ v7_startTransition: true }} />);
    useUpdate.getState().report(NEWER);

    expect(await tryReload("auto")).toBe(false);

    await userEvent.click(screen.getByRole("button", { name: "Copy" }));
    expect(await tryReload("auto")).toBe(true);
  });

  it("holds the reload while something holds it, until it lets go", async () => {
    useUpdate.getState().report(NEWER);
    const release = holdUpdates();
    const other = holdUpdates();

    expect(await tryReload("auto")).toBe(false);
    release();
    release();
    expect(await tryReload("auto")).toBe(false);
    other();
    expect(await tryReload("auto")).toBe(true);
  });

  it("holds the reload while typed text stands in a field on the page", async () => {
    render(
      <form>
        <label>
          New name <input type="text" defaultValue="" />
        </label>
        <label>
          Remember <input type="checkbox" />
        </label>
      </form>,
    );
    useUpdate.getState().report(NEWER);
    await userEvent.click(screen.getByLabelText("Remember"));
    expect(await tryReload("auto", vi.fn())).toBe(true);
    sessionStorage.clear();

    await userEvent.type(screen.getByLabelText("New name"), "Ideas");
    expect(await tryReload("auto")).toBe(false);

    await userEvent.clear(screen.getByLabelText("New name"));
    expect(await tryReload("auto")).toBe(true);
  });

  it("lets a typed field go once it has left the page", async () => {
    const { unmount } = render(<input aria-label="Search" />);
    useUpdate.getState().report(NEWER);
    await userEvent.type(screen.getByLabelText("Search"), "plan");
    expect(await tryReload("auto")).toBe(false);

    unmount();

    expect(await tryReload("auto")).toBe(true);
  });
});
