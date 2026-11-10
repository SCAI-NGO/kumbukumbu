// SPDX-License-Identifier: AGPL-3.0-or-later
import { onlineManager, QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useConsole } from "../console/store";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { useSyncStatus } from "../state/sync";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";
import { FakeSocket } from "../test/socket";
import { forgetHeld, LATE_SAVE } from "./hideAgain";
import { useDiscreet } from "./state";

beforeEach(() => {
  FakeSocket.all = [];
  vi.stubGlobal("WebSocket", FakeSocket);
  vi.useFakeTimers({ shouldAdvanceTime: true });
});

afterEach(async () => {
  try {
    await waitFor(() => expect(useDiscreet.getState().hiding).toBe(false));
  } finally {
    // Also after a hide that never ended: the next test mustn't start offline or on fake timers.
    cleanup();
    onlineManager.setOnline(true);
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    forgetHeld();
    useSession.getState().reset();
    useEditor.getState().reset();
    useDiscreet.getState().reset();
    // Kept by a reset, as in the app; a test starts with no vault hidden before.
    useDiscreet.setState({ gone: new Set() });
    for (const stop of watching.splice(0)) stop();
    useConsole.getState().reset();
    useSyncStatus.setState({ connected: false, editors: {} });
  }
});

const PATH = "/Tagebuch/Heute.md";
const FIRST = "Liebes Tagebuch. Morgens.";
const vault = (name: string, discreet = false) => ({ name, notes: 1, embeddings: "internal", pending: false, canWrite: true, level: "owner", shared: false, discreet });
const tree = (name: string, note: string) => json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: [{ name: note, path: `/${name}/${note}`, size: 4 }] } });
const gone = () => json(404, { error: "You have no vault named Tagebuch." });
/** The note API's 404 for a note of a hidden discreet vault: the same as for a note that isn't there, path and all. */
const noNote = () => json(404, { error: `There is no note at ${PATH} (if it was removed, it is in the trash).` });

type Put = { content: string; version: string };

/**
 * Alice's server as the real one behaves around a reveal's end (CallerMiddleware, Sessions, LateSave): `Home` and the
 * discreet `Tagebuch`, shown until `until` by its clock (`ahead` ms ahead of the tab's) or until a hide. The tab tells
 * it the open discreet note (`open-note`); once the timed reveal has ended, one save of exactly that note within
 * `LATE_SAVE` is taken, a late save that fails puts the marker back once, and anything else of the vault answers 404.
 * A late save's 409 carries the status alone. A request is decided as it arrives; `gates` hold its answer back.
 * `saves` records what was written, in order.
 */
function server(until: number, ahead = 0) {
  const gates: { write: Promise<unknown>; read: Promise<unknown> } = { write: Promise.resolve(), read: Promise.resolve() };
  let hidden = false;
  let marker: string | null = null;
  let retried = false;
  const net = { offline: false };
  let note = { path: PATH, content: "Liebes Tagebuch.", version: "v1", lastWriter: "alice", lastWriteAt: "2026-11-10T07:00:00Z" };
  const saves: string[] = [];
  const now = () => Date.now() + ahead;
  const shown = () => !hidden && now() < until;
  const late = () => !hidden && now() >= until && now() < until + LATE_SAVE;
  const write = (body: Put) => {
    note = { ...note, content: body.content, version: `v${Number(note.version.slice(1)) + 1}`, lastWriteAt: new Date().toISOString() };
    saves.push(body.content);
    return json(200, note);
  };
  const decide = (body: Put) => {
    if (shown()) return body.version === note.version ? write(body) : json(409, { error: "The note changed.", current: { content: note.content, version: note.version } });
    if (!late() || marker !== PATH) return gone();
    marker = null;
    if (body.version === note.version) return write(body);
    if (!retried) {
      retried = true;
      marker = PATH;
    }
    return json(409, { error: "This late save wrote nothing." });
  };
  const handlers: Parameters<typeof fakeApi>[0] = {
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: shown(), until: shown() ? new Date(until).toISOString() : null } }),
    "GET /api/vaults": () => json(200, shown() ? [vault("Home"), vault("Tagebuch", true)] : [vault("Home")]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Home/tree": () => tree("Home", "a.md"),
    "GET /api/vaults/Tagebuch/tree": () => (shown() ? tree("Tagebuch", "Heute.md") : gone()),
    "GET /api/vaults/Home/publications": () => json(200, { publications: [], unpublishable: [] }),
    "GET /api/vaults/Tagebuch/publications": () => (shown() ? json(200, { publications: [], unpublishable: [] }) : gone()),
    [`GET /api/notes${PATH}`]: async () => {
      const answer = shown() ? json(200, note) : noNote();
      await gates.read;
      return answer;
    },
    // Any other note of the vault isn't there, shown or not: the note API's 404, with its path.
    "GET /api/notes/Tagebuch/*": (r) => json(404, { error: `There is no note at ${decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length))} (if it was removed, it is in the trash).` }),
    "GET /api/notes/Home/a.md": () => json(200, { path: "/Home/a.md", content: "Einkaufen.", version: "h1", lastWriter: "alice", lastWriteAt: "2026-11-10T07:00:00Z" }),
    [`GET /api/version${PATH}`]: () => (shown() ? json(200, { path: PATH, version: note.version, lastWriter: note.lastWriter, lastWriteAt: note.lastWriteAt }) : noNote()),
    "GET /api/version/Home/a.md": () => json(200, { path: "/Home/a.md", version: "h1", lastWriter: "alice", lastWriteAt: "2026-11-10T07:00:00Z" }),
    [`PUT /api/notes${PATH}`]: async (r) => {
      const answer = decide((await r.clone().json()) as Put);
      await gates.write;
      return answer;
    },
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/history/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "GET /api/publications/*": () => json(404, { error: "Not published." }),
    "POST /api/ws-ticket": () => json(200, { ticket: "t1", expiresAt: "2026-11-10T10:00:30Z" }),
    "POST /api/account/discreet/hide": () => {
      hidden = true;
      marker = null;
      return noContent();
    },
    "PUT /api/account/discreet/open-note": async (r) => {
      const body = (await r.clone().json()) as { vault: string | null; path?: string };
      // As the server's: only while the vaults are shown.
      if (!shown()) return noContent();
      marker = body.vault ? `/${body.vault}/${body.path}` : null;
      retried = false;
      return noContent();
    },
  };
  const fetch = fakeApi(
    Object.fromEntries(
      Object.entries(handlers).map(([key, handler]) => [
        key,
        (r: Request) => {
          if (net.offline) throw new TypeError("Failed to fetch");
          return handler(r);
        },
      ]),
    ),
  );
  /** The PUTs of the note the tab sent, answered or not. */
  const puts = () => fetch.mock.calls.filter(([r]) => (r as Request).method === "PUT" && new URL((r as Request).url).pathname === `/api/notes${PATH}`).length;
  /** Shown again (a new reveal, until `next`), as after the PIN; what the tab told before is forgotten, as by a hide. */
  const shownAgain = (next: number) => {
    hidden = false;
    until = next;
    marker = null;
    retried = false;
  };
  /** Hidden from elsewhere (another tab's ✕), as the server's hide: the open note it was told is forgotten. */
  const hide = () => {
    hidden = true;
    marker = null;
  };
  return { fetch, net, gates, saves, puts, shownAgain, hide, note: () => note };
}

/** The browser goes offline (or online again): the network, `navigator.onLine` and its events, as react-query hears them. */
function browser(net: { offline: boolean }) {
  const onLine = vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  const set = (online: boolean) => {
    net.offline = !online;
    onLine.mockReturnValue(online);
    act(() => void window.dispatchEvent(new Event(online ? "online" : "offline")));
  };
  return { offline: () => set(false), online: () => set(true) };
}

/** A promise and the call that settles it: a gate of the server's. */
function gate() {
  let open = () => {};
  const promise = new Promise<void>((resolve) => (open = resolve));
  return { promise, open };
}

const wait = (ms: number) => act(() => vi.advanceTimersByTimeAsync(Math.max(0, ms)));

/**
 * Alice reveals for `minutes`, with `/Home/a.md` read before, and opens `/Tagebuch/Heute.md`, in Write mode unless
 * `write` is false. `type` types at the end of the text, as a writer going on does.
 */
async function diary({ minutes = 5, ahead = 0, write = true }: { minutes?: number; ahead?: number; write?: boolean } = {}) {
  const until = Date.now() + minutes * 60_000;
  const api = server(until, ahead);
  const net = browser(api.net);
  useEditor.setState({ recentPaths: ["/Home/a.md"] });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = renderApp(`/notes${PATH}`, queryClient);
  const u = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
  const writeButton = await screen.findByRole("button", { name: "Write" });
  await waitFor(() => expect(api.fetch.mock.calls.some(([r]) => new URL((r as Request).url).pathname === "/api/account/discreet/open-note")).toBe(true));
  if (write) {
    await u.click(writeButton);
    await screen.findByRole("textbox", { name: "Markdown source" });
    // The editor loads on first use: settled before the first keystroke.
    await wait(1_000);
  }
  const box = () => screen.getByRole<HTMLTextAreaElement>("textbox", { name: "Markdown source" });
  const type = (text: string) => u.type(box(), `{Control>}{End}{/Control}${text}`);
  return { ...api, ...net, until, queryClient, router, u, type };
}

/** Types ` off0` … ` off{n-1}`, one every 3 s, and answers the text typed. */
async function typeInTunnel(t: Awaited<ReturnType<typeof diary>>, n: number, prefix = "off") {
  let typed = "";
  for (let i = 0; i < n; i++) {
    await t.type(` ${prefix}${i}`);
    typed += ` ${prefix}${i}`;
    await wait(3_000);
  }
  return typed;
}

describe("F9: text typed offline around the end of a reveal is saved", () => {
  it("the spec's example: offline two minutes before the end, online three minutes after it, saves the latest text once", async () => {
    const t = await diary();
    await t.type(" Morgens.");
    await wait(2_000);
    expect(t.saves.at(-1)).toBe(FIRST);
    await wait(t.until - Date.now() - 2 * 60_000);
    t.offline();
    const tunnel = await typeInTunnel(t, 30);
    await wait(t.until - Date.now() + 2_000);
    await wait(3 * 60_000);
    t.online();
    await wait(5_000);
    expect(t.saves.slice(t.saves.indexOf(FIRST))).toEqual([FIRST, FIRST + tunnel]);
    expect(t.note().content).toBe(FIRST + tunnel);
    await wait(10 * 60_000);
    expect(t.saves.slice(t.saves.indexOf(FIRST))).toHaveLength(2);
  });

  it("online again before the end, saves the latest text at once, without waiting for another keystroke", async () => {
    const t = await diary();
    await t.type(" Morgens.");
    await wait(2_000);
    await wait(t.until - Date.now() - 2 * 60_000);
    t.offline();
    const tunnel = await typeInTunnel(t, 5);
    // A minute before the end, well before the save ahead of it.
    await wait(t.until - Date.now() - 60_000);
    t.online();
    await wait(2_000);
    expect(t.saves.slice(t.saves.indexOf(FIRST))).toEqual([FIRST, FIRST + tunnel]);
    await wait(10 * 60_000);
    expect(t.saves.slice(t.saves.indexOf(FIRST))).toHaveLength(2);
  });

  it("the end while a save is in flight: what was typed meanwhile is the late save", async () => {
    const t = await diary();
    await t.type(" Morgens.");
    await wait(2_000);
    await wait(t.until - Date.now() - 3_000);
    // A slow answer: the save arrives before the end, its answer after it.
    const slow = gate();
    t.gates.write = slow.promise;
    await t.type(" spät");
    await wait(1_000);
    await t.type(" später");
    await wait(4_000);
    t.gates.write = Promise.resolve();
    slow.open();
    await wait(5_000);
    expect(t.saves.slice(t.saves.indexOf(FIRST))).toEqual([FIRST, `${FIRST} spät`, `${FIRST} spät später`]);
    await wait(10 * 60_000);
    expect(t.saves.slice(t.saves.indexOf(FIRST))).toHaveLength(3);
  });

  it("offline until six minutes past the end: the tunnel text is gone, as the late-save window is five", async () => {
    const t = await diary();
    await t.type(" Morgens.");
    await wait(2_000);
    await wait(t.until - Date.now() - 2 * 60_000);
    t.offline();
    await typeInTunnel(t, 10);
    await wait(t.until - Date.now() + 6 * 60_000);
    t.online();
    await wait(60_000);
    expect(t.saves.at(-1)).toBe(FIRST);
    expect(t.note().content).toBe(FIRST);
  });
});


/** What must not be on screen once the reveal has ended. */
const NAMES = /Tagebuch|Heute/;
/**
 * The screen's text at every change after the tab's reveal ended (its mode reset), or after now (`fromNow`): what a
 * person could have seen meanwhile. `stop` ends the watch and answers what was seen. Seen as the observer is called:
 * two renders in one task show as the last of them.
 */
function watchScreen({ fromNow = false } = {}) {
  const seen: string[] = [];
  let ended = fromNow || !useDiscreet.getState().revealed;
  // The markup, so that a label or a title is seen as well as the text.
  const look = () => void (ended && seen.push(document.body.innerHTML));
  const observer = new MutationObserver(look);
  observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true });
  const unsubscribe = useDiscreet.subscribe((s, previous) => {
    // What the screen shows from then on; not at once, before React has rendered anything of it.
    if (previous.revealed && !s.revealed) ended = true;
  });
  const stop = () => {
    observer.disconnect();
    unsubscribe();
  };
  watching.push(stop);
  return () => {
    stop();
    look();
    return seen;
  };
}

/** The watches still on: a test that failed before ending its own leaves them to `afterEach`. */
const watching: (() => void)[] = [];

/** Nothing seen names the vault or the note. */
const namesNothing = (seen: string[]) => expect(seen.filter((text) => NAMES.test(text))).toEqual([]);

describe("F10: after the end, nothing on screen names the discreet note", () => {
  it("the spec's example: the note closes offline at the end, Home is shown, and nothing names it, also once online", async () => {
    const t = await diary();
    const watch = watchScreen();
    await t.type(" Morgens.");
    await wait(2_000);
    await wait(t.until - Date.now() - 2 * 60_000);
    t.offline();
    await typeInTunnel(t, 30);
    await wait(t.until - Date.now() + 2_000);
    await waitFor(() => expect(t.router.state.location.pathname).toBe("/notes/Home/a.md"));
    const sentBefore = t.puts();
    await wait(3 * 60_000);
    // Offline, nothing of the note leaves the tab: the held draft waits for the browser to be online again.
    expect(t.puts()).toBe(sentBefore);
    t.online();
    await wait(5_000);
    // Online again, the ordinary note the tab went to is read.
    await screen.findByText("Einkaufen.");
    await wait(10 * 60_000);
    namesNothing(watch());
    expect(useEditor.getState().currentPath).toBe("/Home/a.md");
  });

  it("the end while a save is in flight, with the note's read answering 404 meanwhile", async () => {
    const t = await diary();
    await t.type(" Morgens.");
    await wait(2_000);
    await wait(t.until - Date.now() - 3_000);
    const slow = gate();
    t.gates.write = slow.promise;
    await t.type(" spät");
    await wait(4_000);
    const watch = watchScreen();
    // The note is read again while the hide waits for the save: the server has hidden it.
    await act(() => t.queryClient.invalidateQueries({ queryKey: ["note", PATH] }));
    await wait(1_000);
    t.gates.write = Promise.resolve();
    slow.open();
    await waitFor(() => expect(t.router.state.location.pathname).toBe("/notes/Home/a.md"));
    await wait(5_000);
    namesNothing(watch());
  });

  it("a 404 before the tab's end (the server's clock is ahead) shows no path, and the hide that follows names nothing", async () => {
    const t = await diary({ write: false, ahead: 20_000 });
    await screen.findByText("Liebes Tagebuch.");
    await wait(t.until - Date.now() - 10_000);
    const all = watchScreen({ fromNow: true });
    const watch = watchScreen();
    // The server's reveal has ended; the tab's hasn't. Read again (the window regains focus, say), the note answers 404.
    await act(() => t.queryClient.refetchQueries({ queryKey: ["note", PATH], exact: true }));
    await waitFor(() => expect(t.router.state.location.pathname).toBe("/notes/Home/a.md"));
    await wait(15_000);
    namesNothing(watch());
    expect(all().filter((text) => text.includes("There is no note at"))).toEqual([]);
  });

  it("a 404 that arrives after the hide names nothing", async () => {
    // The server's reveal ends 2 s before the tab's: the read under way at the end is answered 404.
    const t = await diary({ write: false, ahead: 2_000 });
    await screen.findByText("Liebes Tagebuch.");
    await wait(t.until - Date.now() - 1_000);
    // A read of the note under way at the end, answered after the tab has hidden the vault.
    const late = gate();
    t.gates.read = late.promise;
    void t.queryClient.refetchQueries({ queryKey: ["note", PATH], exact: true });
    const watch = watchScreen();
    await wait(2_000);
    await waitFor(() => expect(t.router.state.location.pathname).toBe("/notes/Home/a.md"));
    t.gates.read = Promise.resolve();
    late.open();
    await wait(5_000);
    namesNothing(watch());
  });

  it("✕ while offline with text unsaved goes to Home and names nothing", async () => {
    const t = await diary();
    await t.type(" Morgens.");
    await wait(2_000);
    t.offline();
    await typeInTunnel(t, 3);
    const watch = watchScreen();
    const sent = t.puts();
    await t.u.click(screen.getByRole("button", { name: "Hide discreet vaults" }));
    await waitFor(() => expect(t.router.state.location.pathname).toBe("/notes/Home/a.md"));
    await wait(2_000);
    namesNothing(watch());
    // The hide's own save tried once; the text is held from then on, and the editor going sends it no second time.
    expect(t.puts()).toBe(sent + 1);
  });

  it("online all along (step 2.9): typing until the end, the note closes, Home is shown, everything is saved once", async () => {
    const t = await diary();
    const watch = watchScreen();
    let typed = "";
    while (Date.now() < t.until - 1_000) {
      await t.type(" w");
      typed += " w";
      await wait(5_000);
    }
    await wait(t.until - Date.now() + 2_000);
    await waitFor(() => expect(t.router.state.location.pathname).toBe("/notes/Home/a.md"));
    await wait(10 * 60_000);
    expect(t.note().content).toBe("Liebes Tagebuch." + typed);
    expect(new Set(t.saves).size).toBe(t.saves.length);
    namesNothing(watch());
  });

  it("a note that isn't there in a shown discreet vault: the 404 names no path", async () => {
    const t = await diary({ write: false });
    await screen.findByText("Liebes Tagebuch.");
    const all = watchScreen({ fromNow: true });
    await act(() => t.router.navigate("/notes/Tagebuch/Fehlt.md"));
    await screen.findByText("There is no such note (if it was removed, it is in the trash).");
    expect(all().filter((text) => text.includes("There is no note at"))).toEqual([]);
  });

  it("shown again after a hide, the vault's notes render again", async () => {
    const t = await diary({ write: false });
    await screen.findByText("Liebes Tagebuch.");
    await t.u.click(screen.getByRole("button", { name: "Hide discreet vaults" }));
    await waitFor(() => {
      expect(t.router.state.location.pathname).toBe("/notes/Home/a.md");
      expect(useDiscreet.getState().hiding).toBe(false);
    });
    // Shown again, as after the PIN: the session and the vault list read again say so.
    t.shownAgain(Date.now() + 5 * 60_000);
    await act(() => t.queryClient.refetchQueries({ queryKey: ["session"] }));
    await act(() => t.queryClient.refetchQueries({ queryKey: ["vaults"] }));
    await waitFor(() => {
      expect(useDiscreet.getState().revealed).toBe(true);
      expect(useDiscreet.getState().names.has("Tagebuch")).toBe(true);
    });
    await act(() => t.router.navigate(`/notes${PATH}`));
    await screen.findByText("Liebes Tagebuch.");
  });

  it("hidden, shown again and hidden once more, the editor still keeps its text until the hide has it", async () => {
    const t = await diary();
    await t.u.click(screen.getByRole("button", { name: "Hide discreet vaults" }));
    await waitFor(() => expect(useDiscreet.getState().hiding).toBe(false));
    t.shownAgain(Date.now() + 5 * 60_000);
    await act(() => t.queryClient.refetchQueries({ queryKey: ["session"] }));
    await act(() => t.queryClient.refetchQueries({ queryKey: ["vaults"] }));
    await waitFor(() => expect(useDiscreet.getState().names.has("Tagebuch")).toBe(true));
    await act(() => t.router.navigate(`/notes${PATH}`));
    await t.u.click(await screen.findByRole("button", { name: "Write" }));
    await screen.findByRole("textbox", { name: "Markdown source" });
    await wait(1_000);
    // A slow save: the second hide waits for it.
    const slow = gate();
    t.gates.write = slow.promise;
    await t.type(" zwei");
    await t.u.click(screen.getByRole("button", { name: "Hide discreet vaults" }));
    expect(useDiscreet.getState().hiding).toBe(true);
    // Meanwhile the user goes to another note: the editor's guard holds it back, as the hide hasn't the text yet.
    await act(() => t.router.navigate("/notes/Home/a.md"));
    expect(t.router.state.location.pathname).toBe(`/notes${PATH}`);
    t.gates.write = Promise.resolve();
    slow.open();
    await waitFor(() => expect(t.router.state.location.pathname).toBe("/notes/Home/a.md"));
    expect(t.note().content).toBe("Liebes Tagebuch. zwei");
  });

  it("a hidden frame from another tab ends it the same way", async () => {
    const t = await diary();
    await waitFor(() => expect(FakeSocket.all.length).toBeGreaterThan(0));
    FakeSocket.all.at(-1)!.open();
    await t.type(" Morgens.");
    await wait(2_000);
    t.offline();
    await typeInTunnel(t, 3);
    const watch = watchScreen();
    // Another tab hid them; its frame arrives here (the socket outlives a short loss of the network, say).
    t.hide();
    FakeSocket.all.at(-1)!.frame({ type: "hidden" });
    await waitFor(() => expect(t.router.state.location.pathname).toBe("/notes/Home/a.md"));
    await wait(2_000);
    namesNothing(watch());
  });
});
