// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { StrictMode } from "react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { routerFuture, routes } from "../router";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { presenceKey, useSyncStatus } from "../state/sync";
import { resetRenames } from "./nameFromHeading";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
afterEach(() => {
  cleanup();
  keepalives.length = 0;
  vi.useRealTimers();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const PLAN = "/Work/projects/plan.md";
const planUrl = "/notes/Work/projects/plan.md";
const source = "# Plan\n\n- [ ] write the roadmap";
const theirs = `${source}\n- [ ] added by liz`;

const noteView = (path: string, content: string, version: string) => ({ path, content, version, lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" });
const tree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [{ name: "projects", path: "/Work/projects", folders: [], notes: ["plan.md", "roadmap.md"].map((n) => ({ name: n, path: `/Work/projects/${n}`, size: 1 })) }],
    notes: [],
  },
};

type Put = { content: string; version: string };
const keepalives: boolean[] = [];
type Answer = (body: Put) => Response;

const ok: Answer = (body) => json(200, noteView(PLAN, body.content, `v${Number(body.version.slice(1)) + 1}`));
const conflict =
  (content: string, version: string): Answer =>
  () =>
    json(409, { error: "The note changed since that version.", current: { content, version } });

/** The API with the plan note; each PUT takes the next answer (the last one repeats) and is recorded; a GET answers what was saved last. */
function editorApi(answers: Answer[] = [ok], getNote?: () => Response, extra: Parameters<typeof fakeApi>[0] = {}) {
  const puts: Put[] = [];
  let current = noteView(PLAN, source, "v1");
  getNote ??= () => json(200, current);
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2 }]),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    [`GET /api/notes${PLAN}`]: getNote,
    [`PUT /api/notes${PLAN}`]: async (r) => {
      const body = (await r.json()) as Put;
      puts.push(body);
      keepalives.push(r.keepalive);
      const answer = answers[Math.min(puts.length - 1, answers.length - 1)](body);
      if (answer.ok) current = await answer.clone().json();
      return answer;
    },
    "GET /api/notes/*": (r) => json(200, noteView(decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), "Roadmap text.", "r1")),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    ...extra,
  });
  // Not enumerable, so `toEqual` still compares the PUTs only.
  return Object.defineProperty(puts, "fetch", { value: fetch }) as Put[] & { fetch: typeof fetch };
}

const typist = () => userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
const editorBox = () => screen.getByRole<HTMLTextAreaElement>("textbox", { name: "Markdown source" });
const toast = () => screen.queryByRole("alert", { name: "Changed elsewhere" });
const wait = (ms: number) => act(() => vi.advanceTimersByTimeAsync(ms));

async function openInWrite(queryClient?: QueryClient) {
  const u = typist();
  const router = renderApp(planUrl, queryClient);
  await u.click(await screen.findByRole("button", { name: "Write" }));
  await screen.findByRole("textbox", { name: "Markdown source" });
  return { u, router };
}

describe("Write mode", () => {
  it("shows the source in a focused 16 px mono textarea, and the toolbar instead of the dock", async () => {
    editorApi();
    await openInWrite();
    const area = editorBox();
    expect(area).toHaveValue(source);
    expect(area).toHaveFocus();
    expect(area.className).toMatch(/\btext-\[16px\]/);
    expect(area.className).toMatch(/\bfont-mono\b/);
    expect(screen.getByRole("toolbar", { name: "Markdown" })).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Vault actions" })).not.toBeInTheDocument();
  });

  it("focuses a stand-in inside the tap on Write and, the editor being loaded already, mounts it in that tap", async () => {
    editorApi();
    renderApp(planUrl);
    const write = await screen.findByRole("button", { name: "Write" });
    // The note view fetched the editor ahead; give that a moment to land.
    await wait(50);
    const focused: Element[] = [];
    const record = (e: FocusEvent) => focused.push(e.target as Element);
    document.addEventListener("focusin", record);
    fireEvent.click(write);
    document.removeEventListener("focusin", record);
    // Within the click itself: first the stand-in, then the editor's textarea, rendered directly (no suspending).
    expect(focused[0]).toHaveAttribute("data-stand-in");
    expect(focused.at(-1)).toBe(editorBox());
    expect(editorBox()).toHaveFocus();
  });

  it("saves 800 ms after the last keystroke with the known version, then on with the version that came back", async () => {
    const puts = editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), " now");
    expect(puts).toHaveLength(0);
    await wait(800);
    await waitFor(() => expect(puts).toEqual([{ content: `${source} now`, version: "v1" }]));
    await u.type(editorBox(), "!");
    await wait(800);
    await waitFor(() => expect(puts[1]).toEqual({ content: `${source} now!`, version: "v2" }));
  });

  it("saves on blur and when the page is hidden", async () => {
    const puts = editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), "a");
    fireEvent.blur(editorBox());
    await waitFor(() => expect(puts).toHaveLength(1));

    await u.type(editorBox(), "b");
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    act(() => void document.dispatchEvent(new Event("visibilitychange")));
    await waitFor(() => expect(puts[1]).toEqual({ content: `${source}ab`, version: "v2" }));
  });

  it("sends the save of a hidden or closing page with keepalive, and a large note as a normal request", async () => {
    const puts = editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), "a");
    vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    act(() => void document.dispatchEvent(new Event("visibilitychange")));
    await waitFor(() => expect(puts).toHaveLength(1));

    await u.type(editorBox(), "b");
    act(() => void window.dispatchEvent(new Event("pagehide")));
    await waitFor(() => expect(puts).toHaveLength(2));

    // keepalive bodies may not exceed 64 KB; the browser would refuse the request.
    fireEvent.change(editorBox(), { target: { value: "x".repeat(70_000) } });
    act(() => void window.dispatchEvent(new Event("pagehide")));
    await waitFor(() => expect(puts).toHaveLength(3));
    expect(keepalives).toEqual([true, true, false]);
  });

  it("asks before the page closes while text is unsaved", async () => {
    editorApi();
    const { u } = await openInWrite();
    // jsdom's Event has the legacy boolean returnValue; what the handler assigns is caught instead.
    const closing = () => {
      const event = new Event("beforeunload", { cancelable: true });
      let returnValue: unknown;
      Object.defineProperty(event, "returnValue", { set: (v) => (returnValue = v), get: () => returnValue });
      window.dispatchEvent(event);
      return { prevented: event.defaultPrevented, returnValue };
    };
    expect(closing()).toEqual({ prevented: false, returnValue: undefined });
    await u.type(editorBox(), "a");
    expect(closing()).toEqual({ prevented: true, returnValue: "" });
  });

  it("never sends a newer version it only read: an agent's write in the meantime ends in the conflict toast", async () => {
    let version = "v1";
    const puts = editorApi([conflict(theirs, "v9")], () => json(200, noteView(PLAN, version === "v1" ? source : theirs, version)));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { u } = await openInWrite(queryClient);
    version = "v9";
    await act(() => queryClient.invalidateQueries({ queryKey: ["note", PLAN] }));
    await u.type(editorBox(), " mine");
    await wait(800);
    await waitFor(() => expect(toast()).toBeInTheDocument());
    expect(puts).toEqual([{ content: `${source} mine`, version: "v1" }]);
    expect(editorBox()).toHaveValue(`${source} mine`);
  });

  it("on a 409 keeps the text, pauses autosave, and saves on top of the newer version only on keep mine", async () => {
    const puts = editorApi([conflict(theirs, "v7"), ok]);
    const { u } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    expect(await screen.findByRole("alert", { name: "Changed elsewhere" })).toHaveTextContent("Changed elsewhere — reload / keep mine / view diff");
    expect(editorBox()).toHaveValue(`${source} mine`);

    await u.type(editorBox(), " more");
    fireEvent.blur(editorBox());
    await wait(5000);
    expect(puts).toHaveLength(1);

    await u.click(within(toast()!).getByRole("button", { name: "keep mine" }));
    await waitFor(() => expect(puts[1]).toEqual({ content: `${source} mine more`, version: "v7" }));
    await waitFor(() => expect(toast()).not.toBeInTheDocument());

    await u.type(editorBox(), ".");
    await wait(800);
    await waitFor(() => expect(puts[2]).toEqual({ content: `${source} mine more.`, version: "v8" }));
  });

  it("shows the toast again when keep mine meets another write, and never saves on its own", async () => {
    const puts = editorApi([conflict(theirs, "v7"), conflict(`${theirs}\nand more`, "v8")]);
    const { u } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    await u.click(within(await screen.findByRole("alert", { name: "Changed elsewhere" })).getByRole("button", { name: "keep mine" }));
    await waitFor(() => expect(puts).toHaveLength(2));
    expect(await screen.findByRole("alert", { name: "Changed elsewhere" })).toBeInTheDocument();
    await wait(5000);
    expect(puts).toHaveLength(2);
  });

  it("on reload replaces the text with the newer version and saves nothing", async () => {
    const puts = editorApi([conflict(theirs, "v7")]);
    const { u } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    await u.click(within(await screen.findByRole("alert", { name: "Changed elsewhere" })).getByRole("button", { name: "reload" }));
    expect(editorBox()).toHaveValue(theirs);
    expect(toast()).not.toBeInTheDocument();
    await wait(5000);
    expect(puts).toHaveLength(1);
  });

  it("view diff shows the newer version against mine, with the same two choices", async () => {
    const puts = editorApi([conflict(theirs, "v7"), ok]);
    const { u } = await openInWrite();
    await u.clear(editorBox());
    await u.type(editorBox(), "# Plan{Enter}{Enter}- [[x] write the roadmap");
    await wait(800);
    await u.click(within(await screen.findByRole("alert", { name: "Changed elsewhere" })).getByRole("button", { name: "view diff" }));

    const dialog = screen.getByRole("dialog", { name: "Changed elsewhere" });
    expect(within(dialog).getByText("- [ ] write the roadmap").closest("[data-kind]")).toHaveAttribute("data-kind", "removed");
    expect(within(dialog).getByText("- [ ] added by liz").closest("[data-kind]")).toHaveAttribute("data-kind", "removed");
    expect(within(dialog).getByText("- [x] write the roadmap").closest("[data-kind]")).toHaveAttribute("data-kind", "added");
    expect(within(dialog).getByText("# Plan").closest("[data-kind]")).toHaveAttribute("data-kind", "same");

    await u.click(within(dialog).getByRole("button", { name: "Keep mine" }));
    await waitFor(() => expect(puts[1]).toEqual({ content: "# Plan\n\n- [x] write the roadmap", version: "v7" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("Done saves what is pending first, then shows Read mode with it", async () => {
    const puts = editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), "{Enter}- [[ ] new task");
    await u.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(useEditor.getState().mode).toBe("read"));
    expect(puts).toEqual([{ content: `${source}\n- [ ] new task`, version: "v1" }]);
    expect(await screen.findByRole("checkbox", { name: "new task" })).toBeInTheDocument();
  });

  it("with an open conflict, Done and the Read toggle keep Write mode and the toast", async () => {
    editorApi([conflict(theirs, "v7")]);
    const { u } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    await screen.findByRole("alert", { name: "Changed elsewhere" });

    await u.click(screen.getByRole("button", { name: "Done" }));
    await wait(0);
    expect(useEditor.getState().mode).toBe("write");
    await u.click(screen.getByRole("button", { name: "Read" }));
    await wait(0);
    expect(useEditor.getState().mode).toBe("write");
    expect(toast()).toBeInTheDocument();
    expect(editorBox()).toHaveValue(`${source} mine`);
  });

  it("the Read toggle ends Write mode the way Done does", async () => {
    const puts = editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), "!");
    await u.click(screen.getByRole("button", { name: "Read" }));
    await waitFor(() => expect(useEditor.getState().mode).toBe("read"));
    expect(puts).toHaveLength(1);
  });

  it("keeps another note from opening while a conflict is open", async () => {
    editorApi([conflict(theirs, "v7"), ok]);
    const { u, router } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    await screen.findByRole("alert", { name: "Changed elsewhere" });

    await u.click(screen.getByRole("button", { name: "Open file tree" }));
    await u.click(within(screen.getByRole("dialog", { name: "File tree" })).getByRole("button", { name: "roadmap" }));
    await wait(0);
    expect(router.state.location.pathname).toBe(planUrl);
    expect(useEditor.getState()).toMatchObject({ mode: "write", currentPath: PLAN });
    expect(editorBox()).toHaveValue(`${source} mine`);
    expect(toast()).toBeInTheDocument();
  });

  it("saves before another note opens", async () => {
    const puts = editorApi();
    const { u, router } = await openInWrite();
    await u.type(editorBox(), " mine");
    await act(async () => {
      useEditor.getState().openNote("/Work/projects/roadmap.md");
      await router.navigate("/notes/Work/projects/roadmap.md");
    });
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/projects/roadmap.md"));
    expect(puts).toEqual([{ content: `${source} mine`, version: "v1" }]);
    expect(await screen.findByText("Roadmap text.")).toBeInTheDocument();
  });

  it("says “Not saved” when a save fails, keeps the text, and retries on request", async () => {
    const puts = editorApi([() => json(500, {}), ok]);
    const { u } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    const notSaved = await screen.findByRole("alert", { name: "Not saved" });
    expect(notSaved).toHaveTextContent("Not saved — retry");
    expect(editorBox()).toHaveValue(`${source} mine`);
    await u.click(within(notSaved).getByRole("button", { name: "retry" }));
    await waitFor(() => expect(puts[1]).toEqual({ content: `${source} mine`, version: "v1" }));
    await waitFor(() => expect(screen.queryByRole("alert", { name: "Not saved" })).not.toBeInTheDocument());
  });
});

const sessionGone: Answer = () => json(401, { error: "Sign in first." });

/** Every value in both web storages, joined: what the browser would keep of this tab. */
const stored = () =>
  [localStorage, sessionStorage].flatMap((storage) => Array.from({ length: storage.length }, (_, i) => storage.getItem(storage.key(i)!))).join("\n");

async function signInAgain(u: ReturnType<typeof typist>) {
  await u.type(await screen.findByLabelText("Username"), "ada");
  await u.type(screen.getByLabelText("Password"), "correct horse battery");
  // A plain click: what follows (the sign-in, the note, its save) is awaited by the test, inside waitFor.
  fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
}

describe("A session that ends mid-edit", () => {
  const signInAs = (who = user) => ({ "POST /api/session": () => json(200, { user: who, previousSignInAt: null }) });

  it("keeps the unsaved text in memory only, and saves it on its version after signing in again", async () => {
    const puts = editorApi([sessionGone, ok], undefined, signInAs());
    const { u, router } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    await waitFor(() => expect(useEditor.getState().draft).not.toBeNull());

    expect(await screen.findByText("Your session has ended. Please sign in again.")).toBeInTheDocument();
    expect(router.state.location.search).toBe(`?next=${encodeURIComponent(planUrl)}`);
    expect(useEditor.getState().draft).toEqual({ username: "ada", path: PLAN, text: `${source} mine`, version: "v1" });
    expect(stored()).not.toContain("mine");
    // Without a session a save can only fail: nothing more is sent.
    expect(puts).toHaveLength(1);

    await signInAgain(u);

    await waitFor(() => expect(puts[1]).toEqual({ content: `${source} mine`, version: "v1" }));
    await waitFor(() => expect(editorBox()).toHaveValue(`${source} mine`));
    expect(router.state.location.pathname).toBe(planUrl);
    expect(useEditor.getState().draft).toBeNull();
  });

  it("leaves Write mode behind on the sign-in screen, with the text kept", async () => {
    editorApi([sessionGone, ok], undefined, signInAs());
    await openInWrite().then(({ u }) => u.type(editorBox(), " mine"));
    await wait(800);
    await waitFor(() => expect(useEditor.getState().draft).not.toBeNull());

    expect(await screen.findByText("Your session has ended. Please sign in again.")).toBeInTheDocument();
    expect(useEditor.getState().mode).toBe("read");
    expect(useEditor.getState().draft?.text).toBe(`${source} mine`);
  });

  it("shows the conflict toast when the note changed meanwhile, with the text kept", async () => {
    const puts = editorApi([sessionGone, conflict(theirs, "v7")], undefined, signInAs());
    const { u, router } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    await waitFor(() => expect(useEditor.getState().draft).not.toBeNull());
    await screen.findByText("Your session has ended. Please sign in again.");

    await signInAgain(u);

    expect(await screen.findByRole("alert", { name: "Changed elsewhere" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe(planUrl);
    expect(editorBox()).toHaveValue(`${source} mine`);
    expect(puts).toHaveLength(2);
  });

  it("drops the text, the note and the recent notes when someone else signs in", async () => {
    const puts = editorApi([sessionGone, ok], undefined, signInAs({ ...user, id: "u2", username: "liz", display: "Liz" }));
    const { u, router } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    await waitFor(() => expect(useEditor.getState().draft).not.toBeNull());
    await screen.findByText("Your session has ended. Please sign in again.");

    await signInAgain(u);

    // The vault overview, and the first vault taken up afresh (it was reset on the way).
    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Your vaults" })).toBeInTheDocument();
      expect(useEditor.getState().activeVault).toBe("Work");
    });
    expect(router.state.location.pathname).toBe("/");
    expect(useEditor.getState()).toMatchObject({ draft: null, recentPaths: [], currentPath: null, mode: "read" });
    expect(puts).toHaveLength(1);
  });
});

describe("Signing out mid-edit", () => {
  const signOut = async (u: ReturnType<typeof typist>) => {
    await u.click(screen.getByRole("button", { name: "Open file tree" }));
    // The save before signing out settles within act(); a sign-out it lets through is awaited by the test.
    await act(async () => void fireEvent.click(within(screen.getByRole("dialog", { name: "File tree" })).getByRole("button", { name: "Sign out" })));
  };
  const sent = (fetch: ReturnType<typeof fakeApi>) => fetch.mock.calls.map(([r]) => `${r.method} ${new URL(r.url).pathname}`).filter((k) => !k.startsWith("GET"));

  it("saves the text first, and signs out once it is saved", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const bodies: Put[] = [];
    const { fetch } = editorApi([ok], undefined, {
      "DELETE /api/session": () => noContent(),
      [`PUT /api/notes${PLAN}`]: async (r) => {
        const body = (await r.json()) as Put;
        bodies.push(body);
        await gate;
        return ok(body);
      },
    });
    const { u, router } = await openInWrite();
    await u.type(editorBox(), " mine");

    await signOut(u);
    await wait(50);
    expect(sent(fetch)).toEqual([`PUT /api/notes${PLAN}`]);
    release();

    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(bodies).toEqual([{ content: `${source} mine`, version: "v1" }]);
    expect(sent(fetch)).toEqual([`PUT /api/notes${PLAN}`, "DELETE /api/session"]);
    // The note isn't read again between its save and the DELETE: that read would race it.
    const calls = fetch.mock.calls.map(([r]) => `${r.method} ${new URL(r.url).pathname}`);
    expect(calls.slice(calls.indexOf(`PUT /api/notes${PLAN}`), calls.indexOf("DELETE /api/session"))).not.toContain(`GET /api/notes${PLAN}`);
  });

  it("asks while the text can't be saved, and signs out only when told to", async () => {
    const puts = editorApi([conflict(theirs, "v7")], undefined, { "DELETE /api/session": () => noContent() });
    const { u, router } = await openInWrite();
    await u.type(editorBox(), " mine");
    await wait(800);
    await screen.findByRole("alert", { name: "Changed elsewhere" });

    await signOut(u);
    const ask = await screen.findByRole("alertdialog", { name: "Sign out with unsaved changes?" });
    expect(sent(puts.fetch)).toEqual([`PUT /api/notes${PLAN}`]);
    await u.click(within(ask).getByRole("button", { name: "Stay" }));
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    expect(router.state.location.pathname).toBe(planUrl);
    expect(useSession.getState().user).not.toBeNull();

    fireEvent.click(within(screen.getByRole("dialog", { name: "File tree" })).getByRole("button", { name: "Sign out" }));
    fireEvent.click(within(await screen.findByRole("alertdialog", { name: "Sign out with unsaved changes?" })).getByRole("button", { name: "Sign out anyway" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    expect(sent(puts.fetch)).toEqual([`PUT /api/notes${PLAN}`, "DELETE /api/session"]);
    expect(useEditor.getState().draft).toBeNull();
  });
});

describe("Done", () => {
  it("asks for the note again, so a read that started before the last save can't leave an older version on screen", async () => {
    let gets = 0;
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    let server = noteView(PLAN, source, "v1");
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    editorApi(
      [
        (body) => {
          server = noteView(PLAN, body.content, "v2");
          return json(200, server);
        },
      ],
      () => json(200, server),
      {
        [`GET /api/notes${PLAN}`]: async () => {
          gets += 1;
          const answer = noteView(PLAN, server.content, server.version);
          // The second read is a focus refetch that the server answers late, with what it had then.
          if (gets === 2) await gate;
          return json(200, answer);
        },
      },
    );
    const { u } = await openInWrite(queryClient);
    void queryClient.refetchQueries({ queryKey: ["note", PLAN], exact: true });
    await u.type(editorBox(), "{Enter}- [[ ] new task");
    await u.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(useEditor.getState().mode).toBe("read"));
    await act(async () => release());

    await waitFor(() => expect(queryClient.getQueryData<{ version: string }>(["note", PLAN])?.version).toBe("v2"));
    expect(await screen.findByRole("checkbox", { name: "new task" })).toBeInTheDocument();
    await wait(100);
    expect(queryClient.getQueryData<{ version: string }>(["note", PLAN])?.version).toBe("v2");
  });
});

describe("[[ completion", () => {
  const listbox = () => screen.queryByRole("listbox", { name: "Notes to link" });

  it("lists the vault's notes as the name is typed, and Enter inserts the link", async () => {
    editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), "{Control>}{End}{/Control}{Enter}See [[[[");
    // The note itself isn't offered.
    expect(within(listbox()!).getAllByRole("option").map((o) => o.textContent)).toEqual(["roadmapWork / projects"]);
    await u.type(editorBox(), "ro");
    expect(within(listbox()!).getByRole("option", { selected: true })).toHaveTextContent("roadmap");
    await u.keyboard("{Enter}");
    expect(editorBox().value).toBe(`${source}\nSee [[roadmap]]`);
    expect(editorBox().selectionStart).toBe(editorBox().value.length);
    expect(listbox()).toBeNull();
  });

  it("inserts on a tap and keeps the textarea focused; Escape closes the list", async () => {
    editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), "{Control>}{End}{/Control} [[[[road");
    await u.click(within(listbox()!).getByRole("option"));
    expect(editorBox().value).toBe(`${source} [[roadmap]]`);
    expect(editorBox()).toHaveFocus();

    await u.type(editorBox(), " [[[[r");
    expect(listbox()).not.toBeNull();
    await u.keyboard("{Escape}");
    expect(listbox()).toBeNull();
    await u.type(editorBox(), "o");
    expect(listbox()).toBeNull();
  });

  it("leaves Enter to an IME that is composing", async () => {
    editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), "{Control>}{End}{/Control} [[[[ro");
    expect(listbox()).not.toBeNull();
    const before = editorBox().value;
    expect(fireEvent.keyDown(editorBox(), { key: "Enter", isComposing: true })).toBe(true);
    expect(fireEvent.keyDown(editorBox(), { key: "Enter", keyCode: 229 })).toBe(true);
    expect(editorBox().value).toBe(before);
    expect(listbox()).not.toBeNull();
  });

  it("completes the [[ ]] the toolbar inserted", async () => {
    editorApi();
    const { u } = await openInWrite();
    await u.type(editorBox(), "{Control>}{End}{/Control} ");
    await u.click(screen.getByRole("button", { name: "Wiki link" }));
    await u.type(editorBox(), "road", { skipClick: true });
    expect(screen.getByRole("listbox", { name: "Notes to link" })).toBeInTheDocument();
    await u.keyboard("{Tab}");
    expect(editorBox().value).toBe(`${source} [[roadmap]]`);
  });
});

describe("Named from its heading", () => {
  const UNTITLED = "/Home/Garden/Untitled.md";
  const untitledUrl = "/notes/Home/Garden/Untitled.md";
  const BEET = "/Home/Garden/Beet.md";
  type Move = { from: string; to: string; keepLinks: boolean; exact: boolean };

  // Unmounted first, as above: the store's change would otherwise reach a mounted shell outside act().
  afterEach(() => {
    cleanup();
    resetRenames();
    useSyncStatus.setState({ editors: {} });
  });

  /** The vault Home with its notes; PUTs and moves change them, and every PUT and move is logged in order. */
  function gardenApi({
    notes = { [UNTITLED]: "# ", [BEET]: "Beet text." },
    move,
    tree: treeHook,
    extra = {},
  }: {
    notes?: Record<string, string>;
    move?: (body: Move) => Response | Promise<Response>;
    /** Runs before each tree answer; what it awaits holds that answer. */
    tree?: () => void | Promise<void>;
    extra?: Parameters<typeof fakeApi>[0];
  } = {}) {
    const access = { level: "owner" };
    const store = new Map(Object.entries(notes).map(([p, content]) => [p, { content, version: "v1" }]));
    const log: string[] = [];
    const puts: { path: string; content: string }[] = [];
    const moves: Move[] = [];
    const pathOf = (r: Request) => decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
    const tree = () => ({
      damaged: 0,
      root: {
        name: "Home",
        path: "/Home",
        notes: [],
        folders: [{ name: "Garden", path: "/Home/Garden", folders: [], notes: [...store.keys()].map((p) => ({ name: p.slice(p.lastIndexOf("/") + 1), path: p, size: 1 })) }],
      },
    });
    const fetch = fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, [{ name: "Home", notes: store.size, level: access.level }]),
      "GET /api/vaults/Home/tree": async () => {
        log.push("GET tree");
        await treeHook?.();
        return json(200, tree());
      },
      "GET /api/notes/*": (r) => {
        const note = store.get(pathOf(r));
        return note ? json(200, noteView(pathOf(r), note.content, note.version)) : json(404, { error: "Not found." });
      },
      "PUT /api/notes/*": async (r) => {
        const path = pathOf(r);
        const { content } = (await r.json()) as { content: string };
        log.push(`PUT ${path}`);
        puts.push({ path, content });
        const old = store.get(path);
        const version = old ? `v${Number(old.version.slice(1)) + 1}` : "v1";
        store.set(path, { content, version });
        return json(old ? 200 : 201, noteView(path, content, version));
      },
      "POST /api/move": async (r) => {
        const body = (await r.json()) as Move;
        moves.push(body);
        log.push(`MOVE ${body.from} → ${body.to}`);
        const answer = move && (await move(body));
        if (answer && !answer.ok) return answer;
        const note = store.get(body.from);
        if (!note) return json(404, { error: `There is no note at ${body.from}.` });
        store.delete(body.from);
        store.set(body.to, note);
        return json(200, { message: `Moved ${body.from} to ${body.to}`, path: body.to, version: note.version });
      },
      "GET /api/links/*": () => json(200, []),
      "GET /api/backlinks/*": () => json(200, []),
      "GET /api/changes": () => json(200, []),
      "GET /api/member-notices": () => json(200, []),
      ...extra,
    });
    return { fetch, log, puts, moves, store, access };
  }

  async function writeIn(url: string, queryClient?: QueryClient) {
    const u = typist();
    const router = renderApp(url, queryClient);
    await u.click(await screen.findByRole("button", { name: "Write" }));
    await screen.findByRole("textbox", { name: "Markdown source" });
    return { u, router };
  }
  const read = (u: ReturnType<typeof typist>) => u.click(screen.getByRole("button", { name: "Read" }));
  // Exact: a folder of that name appearing meanwhile can't take the note in.
  const named = (to: string, from = UNTITLED): Move => ({ from, to, keepLinks: false, exact: true });
  const openInTree = async (u: ReturnType<typeof typist>, name: string) => {
    await u.click(screen.getByRole("button", { name: "Open file tree" }));
    await u.click(within(screen.getByRole("dialog", { name: "File tree" })).getByRole("button", { name }));
  };
  const menuItems = async (u: ReturnType<typeof typist>, label: string) => {
    await u.click(screen.getByRole("button", { name: label }));
    const items = screen.getAllByRole("menuitem").map((i) => i.textContent);
    await u.keyboard("{Escape}");
    return items;
  };

  it("starts a new note with `# ` and the caret after it", async () => {
    const { puts } = gardenApi({ notes: { [BEET]: "Beet text." } });
    const u = typist();
    const router = renderApp("/notes/Home/Garden/Beet.md");
    await screen.findByText("Beet text.");
    await u.click(screen.getByRole("button", { name: "New" }));
    await u.click(screen.getByRole("menuitem", { name: "New note" }));
    await waitFor(() => expect(router.state.location.pathname).toBe(untitledUrl));
    expect(puts).toEqual([{ path: UNTITLED, content: "# " }]);
    await waitFor(() => expect(editorBox()).toHaveValue("# "));
    expect(editorBox()).toHaveFocus();
    expect([editorBox().selectionStart, editorBox().selectionEnd]).toEqual([2, 2]);
  });

  it("names the note on Read, after its last save, and the URL follows", async () => {
    const { log, moves, store } = gardenApi();
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await wait(800);
    await read(u);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Einkaufsliste.md"));
    expect(moves).toEqual([named("/Home/Garden/Einkaufsliste.md")]);
    expect(log.filter((l) => !l.startsWith("GET")).at(-1)).toBe(`MOVE ${UNTITLED} → /Home/Garden/Einkaufsliste.md`);
    expect(store.get("/Home/Garden/Einkaufsliste.md")?.content).toBe("# Einkaufsliste");
    expect(useEditor.getState().currentPath).toBe("/Home/Garden/Einkaufsliste.md");
    // Found, not held on to: the note is read again after the move, which may render the heading anew.
    await screen.findByRole("heading", { level: 1, name: "Einkaufsliste" });
  });

  it("asks nothing more for the old name once the note is named", async () => {
    const { fetch } = gardenApi({ extra: { "GET /api/publications/*": () => json(404, { error: "Not found." }) } });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { u, router } = await writeIn(untitledUrl, queryClient);
    await u.type(editorBox(), "Einkaufsliste");
    await wait(800);
    await read(u);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Einkaufsliste.md"));
    await screen.findByRole("heading", { level: 1, name: "Einkaufsliste" });
    await wait(100);
    const sent = fetch.mock.calls.map(([r]) => `${r.method} ${decodeURIComponent(new URL(r.url).pathname)}`);
    const after = sent.slice(sent.indexOf("POST /api/move") + 1);
    expect(after.filter((r) => r.includes("Untitled.md"))).toEqual([]);
    expect(after).toContain("GET /api/notes/Home/Garden/Einkaufsliste.md");
    // Gone once nothing shows it: a new note at the old name later doesn't show this one's text.
    cleanup();
    expect(queryClient.getQueryData(["note", UNTITLED])).toBeUndefined();
  });

  it("leaves highlight and maths marks out of the name", async () => {
    const { moves } = gardenApi();
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "==Tomaten== $x$");
    await read(u);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Tomaten%20x.md"));
    expect(moves).toEqual([named("/Home/Garden/Tomaten x.md")]);
  });

  it("keeps text typed just before Read", async () => {
    const { log, puts, store } = gardenApi();
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste{Enter}{Enter}- Tomaten");
    await read(u);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Einkaufsliste.md"));
    const writes = log.filter((l) => !l.startsWith("GET"));
    expect(writes.at(-1)).toBe(`MOVE ${UNTITLED} → /Home/Garden/Einkaufsliste.md`);
    expect(writes.slice(0, -1).every((l) => l === `PUT ${UNTITLED}`)).toBe(true);
    expect(puts.at(-1)?.content).toBe("# Einkaufsliste\n\n- Tomaten");
    expect(store.get("/Home/Garden/Einkaufsliste.md")?.content).toBe("# Einkaufsliste\n\n- Tomaten");
  });

  it("names it when another note opens, and stays on that note", async () => {
    const { moves } = gardenApi();
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await u.click(screen.getByRole("button", { name: "Open file tree" }));
    await u.click(within(screen.getByRole("dialog", { name: "File tree" })).getByRole("button", { name: "Beet" }));
    await waitFor(() => expect(moves).toEqual([named("/Home/Garden/Einkaufsliste.md")]));
    await wait(50);
    expect(router.state.location.pathname).toBe("/notes/Home/Garden/Beet.md");
    expect(useEditor.getState().currentPath).toBe(BEET);
    expect(useEditor.getState().recentPaths.slice(0, 2)).toEqual([BEET, "/Home/Garden/Einkaufsliste.md"]);
    expect(useEditor.getState().recentPaths).not.toContain(UNTITLED);
    await screen.findByText("Beet text.");
  });

  it("names it when leaving the notes for Settings", async () => {
    const { moves } = gardenApi();
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await wait(800);
    await act(() => router.navigate("/settings"));
    await waitFor(() => expect(moves).toEqual([named("/Home/Garden/Einkaufsliste.md")]));
    await wait(50);
    expect(router.state.location.pathname).toBe("/settings");
    expect(useEditor.getState().recentPaths[0]).toBe("/Home/Garden/Einkaufsliste.md");
    expect(useEditor.getState().recentPaths).not.toContain(UNTITLED);
  });

  it("leaves a named note alone", async () => {
    const { moves, puts } = gardenApi({ notes: { "/Home/Garden/Markt.md": "# Einkaufsliste Samstag" } });
    const { u } = await writeIn("/notes/Home/Garden/Markt.md");
    await u.type(editorBox(), "!");
    await read(u);
    await waitFor(() => expect(useEditor.getState().mode).toBe("read"));
    await wait(50);
    expect(puts).toEqual([{ path: "/Home/Garden/Markt.md", content: "# Einkaufsliste Samstag!" }]);
    expect(moves).toEqual([]);
  });

  it("renames an Obsidian `Untitled 1.md`", async () => {
    const { moves } = gardenApi({ notes: { "/Home/Garden/Untitled 1.md": "" } });
    const { u, router } = await writeIn("/notes/Home/Garden/Untitled%201.md");
    await u.type(editorBox(), "# Beet");
    await read(u);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Beet.md"));
    expect(moves).toEqual([named(BEET, "/Home/Garden/Untitled 1.md")]);
  });

  it("skips while someone else edits the note", async () => {
    const { moves } = gardenApi();
    const { u } = await writeIn(untitledUrl);
    act(() => useSyncStatus.setState({ editors: { [presenceKey("Home", "Garden/Untitled.md")]: ["Bob"] } }));
    await u.type(editorBox(), "Einkaufsliste");
    await read(u);
    await waitFor(() => expect(useEditor.getState().mode).toBe("read"));
    await wait(50);
    expect(moves).toEqual([]);
  });

  it("sends no move while text is unsaved", async () => {
    const { moves, puts } = gardenApi({ notes: { [UNTITLED]: "# Einkaufsliste" } });
    const { u } = await writeIn(untitledUrl);
    await u.type(editorBox(), "{Enter}- Tomaten");
    // Write mode ended under the editor, without its guard (a role lowered meanwhile): what was typed is sent as it
    // goes, and a move now could overtake that save.
    act(() => useEditor.getState().setMode("read"));
    await wait(50);
    await waitFor(() => expect(puts.at(-1)).toEqual({ path: UNTITLED, content: "# Einkaufsliste\n- Tomaten" }));
    expect(moves).toEqual([]);
  });

  it("says so when the move fails, once", async () => {
    const { moves, store } = gardenApi({ move: () => json(409, { error: "There is a note named Einkaufsliste.md there already." }) });
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await read(u);
    expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't name the note from its heading: There is a note named Einkaufsliste.md there already.");
    expect(router.state.location.pathname).toBe(untitledUrl);
    expect(store.get(UNTITLED)?.content).toBe("# Einkaufsliste");

    // Said for that note only; coming back to it without a new Write session sends nothing again.
    await openInTree(u, "Beet");
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Beet.md"));
    expect(screen.queryByText(/Couldn't name the note/)).not.toBeInTheDocument();
    await openInTree(u, "Untitled");
    await waitFor(() => expect(router.state.location.pathname).toBe(untitledUrl));
    await screen.findByText(/Couldn't name the note/);
    await wait(50);
    expect(moves).toHaveLength(1);
  });

  it("gives up after 15 s without an answer, says so, and lets Write mode start again", async () => {
    const { moves } = gardenApi({ move: () => new Promise<Response>(() => {}) });
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await read(u);
    await waitFor(() => expect(moves).toHaveLength(1));
    expect(screen.getByRole("button", { name: "Write" })).toBeDisabled();
    await wait(15_000);
    expect(await screen.findByRole("alert")).toHaveTextContent("Couldn't name the note from its heading: The server didn't answer in time.");
    expect(screen.getByRole("button", { name: "Write" })).toBeEnabled();
    expect(router.state.location.pathname).toBe(untitledUrl);
  });

  it("leaves a note alone that was moved by hand from Write mode", async () => {
    const { moves } = gardenApi();
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await wait(800);
    await u.click(screen.getByRole("button", { name: "Open file tree" }));
    await u.click(within(screen.getByRole("dialog", { name: "File tree" })).getByRole("button", { name: "Actions for Untitled" }));
    await u.click(screen.getByRole("menuitem", { name: "Rename or move" }));
    const dialog = await screen.findByRole("dialog", { name: /Rename or move/ });
    await u.clear(within(dialog).getByLabelText("Name"));
    await u.type(within(dialog).getByLabelText("Name"), "Markt");
    await u.click(within(dialog).getByRole("button", { name: "Move" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Markt.md"));
    await wait(100);
    expect(moves).toEqual([{ from: UNTITLED, to: "/Home/Garden/Markt.md", keepLinks: false, exact: false }]);
    expect(screen.queryByText(/Couldn't name the note/)).not.toBeInTheDocument();
  });

  it("names two untitled notes left one after the other, each by a fresh look at the folder", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const { moves } = gardenApi({
      notes: { [UNTITLED]: "# ", "/Home/Garden/Untitled 2.md": "# ", [BEET]: "Beet text." },
      move: (body) => (body.from === UNTITLED ? gate.then(() => json(200, {})) : json(200, {})),
    });
    const { u } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await read(u);
    await waitFor(() => expect(moves).toHaveLength(1));

    await openInTree(u, "Untitled 2");
    await u.click(await screen.findByRole("button", { name: "Write" }));
    await u.type(await screen.findByRole("textbox", { name: "Markdown source" }), "Einkaufsliste");
    await openInTree(u, "Beet");
    await screen.findByText("Beet text.");
    await wait(50);
    expect(moves).toHaveLength(1);

    await act(async () => release());
    await waitFor(() => expect(moves).toEqual([named("/Home/Garden/Einkaufsliste.md"), named("/Home/Garden/Einkaufsliste 2.md", "/Home/Garden/Untitled 2.md")]));
  });

  it("offers no Rename or move for the note while it is being named", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const { moves } = gardenApi({ move: () => gate.then(() => json(200, {})) });
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await read(u);
    await waitFor(() => expect(moves).toHaveLength(1));
    expect(await menuItems(u, "Note actions")).not.toContain("Rename or move");
    await u.click(screen.getByRole("button", { name: "Open file tree" }));
    expect(await menuItems(u, "Actions for Untitled")).not.toContain("Rename or move");
    expect(await menuItems(u, "Actions for Beet")).toContain("Rename or move");

    await act(async () => release());
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Einkaufsliste.md"));
    await screen.findByRole("button", { name: "Actions for Einkaufsliste" });
    expect(await menuItems(u, "Actions for Einkaufsliste")).toContain("Rename or move");
  });

  it("leaves it alone once the vault is only read", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { moves, puts, access } = gardenApi();
    const { u } = await writeIn(untitledUrl, queryClient);
    await u.type(editorBox(), "Einkaufsliste");
    await wait(800);
    await waitFor(() => expect(puts.at(-1)?.content).toBe("# Einkaufsliste"));
    // The role was lowered: the vault list read again says so, and Write mode ends.
    access.level = "read";
    await act(() => queryClient.invalidateQueries({ queryKey: ["vaults"] }));
    await waitFor(() => expect(useEditor.getState().mode).toBe("read"));
    await wait(100);
    expect(moves).toEqual([]);
  });

  it("stops when the session ends while it looks at the folder", async () => {
    let holding = false;
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const { moves } = gardenApi({ tree: () => (holding ? gate : undefined) });
    const { u } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await wait(800);
    holding = true;
    await read(u);
    await waitFor(() => expect(useEditor.getState().mode).toBe("read"));
    await wait(50);
    act(() => useSession.getState().reset());
    await act(async () => release());
    await wait(100);
    expect(moves).toEqual([]);
    expect(screen.queryByText(/Couldn't name the note/)).not.toBeInTheDocument();
  });

  it("sends no move when the user signs out from Write mode", async () => {
    const { moves, puts } = gardenApi({ extra: { "DELETE /api/session": () => noContent() } });
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await u.click(screen.getByRole("button", { name: "Open file tree" }));
    await act(async () => void fireEvent.click(within(screen.getByRole("dialog", { name: "File tree" })).getByRole("button", { name: "Sign out" })));
    await waitFor(() => expect(router.state.location.pathname).toBe("/signin"));
    await wait(100);
    expect(puts.at(-1)?.content).toBe("# Einkaufsliste");
    expect(moves).toEqual([]);
  });

  it("doesn't enter Write while the move is in flight, and saves nothing to the old path after it", async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const { log, moves } = gardenApi({ move: () => gate.then(() => json(200, {})) });
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await read(u);
    await waitFor(() => expect(moves).toHaveLength(1));
    expect(screen.getByRole("button", { name: "Write" })).toBeDisabled();
    act(() => useEditor.getState().setMode("write"));
    expect(useEditor.getState().mode).toBe("read");
    act(() => useEditor.getState().openNote(UNTITLED, "write"));
    expect(useEditor.getState().mode).toBe("read");

    await act(async () => release());
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Einkaufsliste.md"));
    await waitFor(() => expect(screen.getByRole("button", { name: "Write" })).toBeEnabled());
    await wait(1000);
    expect(log.slice(log.indexOf(`MOVE ${UNTITLED} → /Home/Garden/Einkaufsliste.md`))).not.toContain(`PUT ${UNTITLED}`);
  });

  it("opens a second new note and stays on it", async () => {
    const { puts } = gardenApi();
    const { u, router } = await writeIn(untitledUrl);
    await u.type(editorBox(), "Einkaufsliste");
    await read(u);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Einkaufsliste.md"));

    await u.click(screen.getByRole("button", { name: "New" }));
    await u.click(screen.getByRole("menuitem", { name: "New note" }));
    await waitFor(() => expect(router.state.location.pathname).toBe(untitledUrl));
    expect(puts.at(-1)).toEqual({ path: UNTITLED, content: "# " });
    await waitFor(() => expect(editorBox()).toHaveValue("# "));
    await wait(100);
    expect(router.state.location.pathname).toBe(untitledUrl);
    expect(useEditor.getState()).toMatchObject({ currentPath: UNTITLED, mode: "write" });
  });

  it("sends nothing for an untitled note without a heading", async () => {
    const { log, moves } = gardenApi();
    const { u } = await writeIn(untitledUrl);
    await wait(50);
    const before = log.length;
    await read(u);
    await waitFor(() => expect(useEditor.getState().mode).toBe("read"));
    await wait(50);
    expect(log.slice(before)).not.toContain("GET tree");
    expect(moves).toEqual([]);
  });

  it("moves once under StrictMode, when Write mode really ends", async () => {
    const { moves } = gardenApi({ notes: { [UNTITLED]: "# Beet" } });
    const u = typist();
    const router = createMemoryRouter(routes, { initialEntries: [untitledUrl], future: routerFuture });
    render(
      <StrictMode>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <RouterProvider router={router} future={{ v7_startTransition: true }} />
        </QueryClientProvider>
      </StrictMode>,
    );
    await u.click(await screen.findByRole("button", { name: "Write" }));
    await screen.findByRole("textbox", { name: "Markdown source" });
    await wait(100);
    expect(moves).toEqual([]);
    await read(u);
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Home/Garden/Beet.md"));
    await wait(100);
    expect(moves).toEqual([named(BEET)]);
  });
});
