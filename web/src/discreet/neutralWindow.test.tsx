// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createBrowserRouter, Link, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { routerFuture, routes } from "../router";
import { safeNext } from "../routes/SignIn";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, user } from "../test/app";
import { FakeSocket } from "../test/socket";
import { forgetHeld } from "./hideAgain";
import { createNeutralWindow, discreetUrl, forgetDiscreet, neutralWindow, wrapCreateHref } from "./neutralWindow";
import { useDiscreet } from "./state";

let disposeRouter: (() => void) | null = null;

beforeEach(() => {
  forgetDiscreet();
  window.history.replaceState(null, "", "/");
  document.title = "Kumbu²";
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
});

afterEach(() => {
  cleanup();
  disposeRouter?.();
  disposeRouter = null;
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  forgetHeld();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
  forgetDiscreet();
  window.history.replaceState(null, "", "/");
  localStorage.clear();
  sessionStorage.clear();
});

const shown = (...names: string[]) => useDiscreet.setState({ revealed: true, names: new Set(names) });

/** Whatever this tab's browser keeps: the address bar, the entry's state, the storages and the title. */
function browserKeeps() {
  return JSON.stringify([window.location.href, window.history.state, { ...localStorage }, { ...sessionStorage }, document.title]);
}

describe("the neutral window", () => {
  it("writes /discreet for a URL in a discreet vault, keeps only key and index in the history, and reads the real URL back", () => {
    shown("Tagebuch");
    neutralWindow.history.pushState({ usr: { paths: ["/Tagebuch/b.png"] }, key: "k1", idx: 1 }, "", "/notes/Tagebuch/a.md?at=2#Heute");

    expect(window.location.pathname + window.location.search + window.location.hash).toBe("/discreet");
    expect(window.history.state).toEqual({ key: "k1", idx: 1 });
    expect(neutralWindow.location.pathname).toBe("/notes/Tagebuch/a.md");
    expect(neutralWindow.location.search).toBe("?at=2");
    expect(neutralWindow.location.hash).toBe("#Heute");
    expect(neutralWindow.history.state).toEqual({ usr: { paths: ["/Tagebuch/b.png"] }, key: "k1", idx: 1 });
    expect(browserKeeps()).not.toContain("Tagebuch");
  });

  it("passes an ordinary vault's URL through as it is", () => {
    shown("Tagebuch");
    neutralWindow.history.pushState({ usr: null, key: "k1", idx: 1 }, "", "/notes/Work/a.md");

    expect(window.location.pathname).toBe("/notes/Work/a.md");
    expect(window.history.state).toEqual({ usr: null, key: "k1", idx: 1 });
    expect(neutralWindow.location.pathname).toBe("/notes/Work/a.md");
  });

  it("keeps a state naming a discreet note out of the history of an ordinary URL too, while they are shown", () => {
    shown("Tagebuch");
    neutralWindow.history.pushState({ usr: { back: "/Tagebuch/a.md" }, key: "k1", idx: 1 }, "", "/settings");

    expect(window.location.pathname).toBe("/settings");
    expect(window.history.state).toEqual({ key: "k1", idx: 1 });
    expect(neutralWindow.history.state).toEqual({ usr: { back: "/Tagebuch/a.md" }, key: "k1", idx: 1 });
  });

  it("knows each kind of route that names a vault", () => {
    shown("Tagebuch");
    for (const url of ["/notes/Tagebuch/a.md", "/board/Tagebuch", "/gallery/Tagebuch", "/view/Tagebuch/b.png", "/checkpoints/Tagebuch/c1/a.md", "/notes/Tagebuch"])
      expect(discreetUrl(url), url).toBe(true);
    for (const url of ["/notes/Work/a.md", "/", "/settings", "/discreet", "/notes/Tagebuchx/a.md"]) expect(discreetUrl(url), url).toBe(false);
  });

  it("forgets on hiding: the entry then reads /discreet", () => {
    shown("Tagebuch");
    neutralWindow.history.pushState({ key: "k1", idx: 1 }, "", "/notes/Tagebuch/a.md");
    forgetDiscreet();

    expect(neutralWindow.location.pathname).toBe("/discreet");
    expect(neutralWindow.history.state).toEqual({ key: "k1", idx: 1 });
  });

  it("replaces the entry shown when its vault becomes discreet", () => {
    window.history.replaceState({ key: "k0", idx: 0 }, "", "/notes/Tagebuch/a.md");
    useDiscreet.getState().setFromVaults([{ name: "Work" }, { name: "Tagebuch", discreet: true }]);

    expect(window.location.pathname).toBe("/discreet");
    expect(neutralWindow.location.pathname).toBe("/notes/Tagebuch/a.md");
    expect(window.history.state).toEqual({ key: "k0", idx: 0 });
  });

  it("never loads a discreet URL as a page", () => {
    shown("Tagebuch");
    // jsdom can't navigate, nor can its location be stubbed: a window of the test's own sees where it would go.
    const assign = vi.fn();
    const real = { history: window.history, location: { pathname: "/", origin: window.location.origin, assign }, document, addEventListener() {}, removeEventListener() {} };
    const neutral = createNeutralWindow(real as unknown as Window);
    neutral.location.assign("/notes/Tagebuch/a.md");
    neutral.location.assign("/notes/Work/a.md");
    expect(assign.mock.calls).toEqual([["/discreet"], ["/notes/Work/a.md"]]);
  });
});

/** A browser router at `path` on the neutral window, as App makes it. */
function renderBrowser(path: string, element?: JSX.Element, queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })) {
  window.history.replaceState(null, "", path);
  const router = wrapCreateHref(createBrowserRouter(element ? [{ path: "*", element }] : routes, { future: routerFuture, window: neutralWindow }));
  disposeRouter = () => router.dispose();
  render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} future={{ v7_startTransition: true }} />
    </QueryClientProvider>,
  );
  return router;
}

function Here() {
  const location = useLocation();
  return (
    <>
      <p data-testid="here">{location.pathname + location.hash}</p>
      <p data-testid="state">{JSON.stringify(location.state)}</p>
      <Link to="/notes/Tagebuch/a.md">Diary</Link>
      <Link to="/notes/Work/a.md">Work</Link>
    </>
  );
}

describe("a router on the neutral window", () => {
  it("shows /discreet in every link to a discreet note, and the real URL in the app", async () => {
    shown("Tagebuch");
    renderBrowser("/", <Here />);

    expect(screen.getByRole("link", { name: "Diary" })).toHaveAttribute("href", "/discreet");
    expect(screen.getByRole("link", { name: "Work" })).toHaveAttribute("href", "/notes/Work/a.md");
    await userEvent.click(screen.getByRole("link", { name: "Diary" }));
    expect(screen.getByTestId("here")).toHaveTextContent("/notes/Tagebuch/a.md");
    expect(window.location.pathname).toBe("/discreet");
  });

  it("goes back and forward through discreet entries, from memory", async () => {
    shown("Tagebuch");
    const router = renderBrowser("/notes/Work/a.md", <Here />);
    await act(() => router.navigate("/view/Tagebuch/b.png", { state: { paths: ["/Tagebuch/b.png"] } }));
    await act(() => router.navigate("/notes/Work/a.md#Plan"));
    expect(window.location.pathname).toBe("/notes/Work/a.md");

    act(() => window.history.back());
    await waitFor(() => expect(screen.getByTestId("here")).toHaveTextContent("/view/Tagebuch/b.png"));
    expect(screen.getByTestId("state")).toHaveTextContent('{"paths":["/Tagebuch/b.png"]}');
    expect(window.location.pathname).toBe("/discreet");
    expect(browserKeeps()).not.toContain("Tagebuch");

    act(() => window.history.forward());
    await waitFor(() => expect(screen.getByTestId("here")).toHaveTextContent("/notes/Work/a.md#Plan"));
    expect(window.location.pathname).toBe("/notes/Work/a.md");
  });
});

const vault = (name: string, discreet = false) => ({ name, notes: 2, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, discreet });
const tree = (name: string) =>
  json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: ["a.md", "b.md"].map((n) => ({ name: n, path: `/${name}/${n}`, size: 4 })) } });

/** Alice's server: `Work` and the discreet `Tagebuch`, shown until a hide; `/api/session` answers 401 once `ended`. */
function server() {
  const at = { hidden: false, ended: false };
  fakeApi({
    "GET /api/session": () =>
      at.ended ? json(401, { error: "not signed in" }) : json(200, { user, previousSignInAt: null, discreet: { revealed: !at.hidden, until: null } }),
    "GET /api/vaults": () => json(200, at.hidden ? [vault("Work")] : [vault("Work"), vault("Tagebuch", true)]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => tree("Work"),
    "GET /api/vaults/Tagebuch/tree": () => tree("Tagebuch"),
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
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
      at.hidden = true;
      return noContent();
    },
    "PUT /api/account/discreet/open-note": () => noContent(),
  });
  return at;
}

/** The app at Work's note, then two notes of `Tagebuch` opened after it. */
async function openDiary() {
  const at = server();
  const router = renderBrowser("/notes/Work/a.md");
  expect(await screen.findByRole("heading", { name: "a" })).toBeInTheDocument();
  await waitFor(() => expect([...useDiscreet.getState().names]).toEqual(["Tagebuch"]));
  await act(() => router.navigate("/notes/Tagebuch/a.md"));
  await act(() => router.navigate("/notes/Tagebuch/b.md"));
  expect(await screen.findByRole("heading", { name: "b" })).toBeInTheDocument();
  expect(window.location.pathname).toBe("/discreet");
  return { at, router };
}

describe("the app on the neutral window", () => {
  it("shows a discreet note at /discreet, and keeps its name out of the browser", async () => {
    const { router } = await openDiary();
    expect(router.state.location.pathname).toBe("/notes/Tagebuch/b.md");
    expect(document.title).toBe("Kumbu²");
    expect(browserKeeps()).not.toContain("Tagebuch");
    for (const link of document.querySelectorAll("a")) expect(link.getAttribute("href") ?? "").not.toContain("Tagebuch");
  });

  it("reloaded at /discreet, shows the vault list", async () => {
    server();
    const router = renderBrowser("/discreet");
    await waitFor(() => expect(router.state.location.pathname).toBe("/"));
    expect(window.location.pathname).toBe("/");
    expect(await screen.findByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
  });

  it("hiding leaves the discreet view and forgets it: going back to it finds /discreet, which goes to the vault list", async () => {
    const { router } = await openDiary();
    const { key } = window.history.state as { key: string };
    await userEvent.click(within(screen.getByRole("group", { name: "Discreet vaults shown" })).getByRole("button", { name: "Hide discreet vaults" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/a.md"));
    expect(window.location.pathname).toBe("/notes/Work/a.md");

    // As the browser goes back to that entry (where the shell's own entries put it varies): its key, /discreet.
    const visited: string[] = [];
    const states: string[] = [];
    const stop = router.subscribe(({ location }) => void (visited.push(location.pathname), states.push(JSON.stringify(location.state))));
    act(() => {
      window.history.pushState({ key, idx: 9 }, "", "/discreet");
      window.dispatchEvent(new PopStateEvent("popstate"));
    });
    // The vault list, which then opens Work's note again as the shell does for an open note.
    await waitFor(() => expect(visited).toContain("/"));
    stop();
    expect(visited.join() + states.join()).not.toContain("Tagebuch");
  });

  it("a session that ends in a discreet note sends to sign-in with the way back to /", async () => {
    const { at, router } = await openDiary();
    at.ended = true;
    act(() => useSession.getState().end());
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(router.state.location.search).toBe("?next=%2F");
    expect(browserKeeps()).not.toContain("Tagebuch");
  });
});

describe("signing in", () => {
  it("never follows a next of /discreet", () => {
    expect(safeNext("/discreet")).toBe("/");
    expect(safeNext("/discreet?x=1")).toBe("/");
    expect(safeNext("/notes/Work/a.md")).toBe("/notes/Work/a.md");
  });
});
