// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
afterEach(() => {
  cleanup();
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
type Answer = (body: Put) => Response;

const ok: Answer = (body) => json(200, noteView(PLAN, body.content, `v${Number(body.version.slice(1)) + 1}`));
const conflict =
  (content: string, version: string): Answer =>
  () =>
    json(409, { error: "The note changed since that version.", current: { content, version } });

/** The API with the plan note; each PUT takes the next answer (the last one repeats) and is recorded. */
function editorApi(answers: Answer[] = [ok], getNote = () => json(200, noteView(PLAN, source, "v1"))) {
  const puts: Put[] = [];
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2 }]),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    [`GET /api/notes${PLAN}`]: getNote,
    [`PUT /api/notes${PLAN}`]: async (r) => {
      const body = (await r.json()) as Put;
      puts.push(body);
      return answers[Math.min(puts.length - 1, answers.length - 1)](body);
    },
    "GET /api/notes/*": (r) => json(200, noteView(decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), "Roadmap text.", "r1")),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
  });
  return puts;
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
