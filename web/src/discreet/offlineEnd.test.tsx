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
  await waitFor(() => expect(useDiscreet.getState().hiding).toBe(false));
  cleanup();
  onlineManager.setOnline(true);
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
  return { fetch, net, gates, saves, puts, shownAgain, note: () => note };
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

