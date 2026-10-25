// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
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
    await u.click(within(screen.getByRole("dialog", { name: "File tree" })).getByRole("button", { name: /roadmap/ }));
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
