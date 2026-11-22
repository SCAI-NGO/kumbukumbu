// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import DuplicateNotice from "../note/DuplicateNotice";
import { copyText } from "../note/duplicate";
import { useDiscreet } from "../discreet/state";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { useNoteActions } from "./useNoteActions";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.setState({ gone: new Set() });
});

const ORIGINAL = "---\nwhen: 2026-11-20\ntags: [plan]\n---\n# inbox\n\nText.";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const tree = (vault: string) => ({ damaged: 0, root: { name: vault, path: `/${vault}`, folders: [], notes: [note(`/${vault}/inbox.md`)] } });
const notePath = (r: Request) => decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
const view = (path: string, content: string, version: string) => ({ path, content, version, lastWriter: "ada", lastWriteAt: "2026-11-18T09:00:00Z" });

/**
 * The server: the open note `/Work/inbox.md` with `content`, saved over by its own PUT; a PUT of any other note is a
 * copy, created unless `taken` says the name is taken. `events` records the inbox's reads and writes and the copies, in order.
 */
function server({
  taken = (_: string): boolean => false,
  failSave = false,
  vaults = [{ name: "Work", notes: 1, embeddings: "internal" }] as unknown[],
  content = ORIGINAL,
  publish = false,
} = {}) {
  const state = { content, version: "v1", events: [] as string[], copies: [] as { path: string; body: Record<string, unknown> }[] };
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, publishEnabled: publish }),
    "GET /api/vaults": () => json(200, vaults),
    ...(publish ? { "GET /api/vaults/Work/publications": () => json(200, { publications: [], unpublishable: [] }) } : {}),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree("Work")),
    "GET /api/vaults/Shared/tree": () => json(200, tree("Shared")),
    "GET /api/account/favourites": () => json(200, { favourites: [] }),
    "GET /api/notes/Work/inbox.md": () => {
      state.events.push("read");
      return json(200, view("/Work/inbox.md", state.content, state.version));
    },
    "PUT /api/notes/Work/inbox.md": async (r) => {
      const body = (await r.json()) as { content: string };
      state.events.push("save");
      if (failSave) return json(500, { error: "" });
      state.content = body.content;
      state.version = `v${Number(state.version.slice(1)) + 1}`;
      return json(200, view("/Work/inbox.md", state.content, state.version));
    },
    "PUT /api/notes/*": async (r) => {
      const path = notePath(r);
      const body = (await r.json()) as Record<string, unknown>;
      state.events.push(`copy ${path}`);
      state.copies.push({ path, body });
      if (taken(path)) return json(409, { error: "There is a note at that path already." });
      return json(201, view(path, body.content as string, "c1"));
    },
    "GET /api/notes/*": (r) => json(200, view(notePath(r), "Text.", "v1")),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "GET /api/blame/*": () => json(200, { path: "/Work/inbox.md", version: state.version, content: state.content, earlier: null, dropped: false, writers: [], sections: [] }),
  });
  return state;
}

const labels = () => screen.getAllByRole("menuitem").map((item) => item.textContent);

async function openMenu() {
  await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
  await screen.findByRole("menuitem", { name: "History" });
}

async function duplicate() {
  await screen.findByRole("heading", { name: "inbox" });
  // Once the favourites are in, the menu is complete.
  await waitFor(() => expect(useEditor.getState().currentPath).toBe("/Work/inbox.md"));
  await openMenu();
  await userEvent.click(screen.getByRole("menuitem", { name: "Duplicate" }));
}

/** The hook for the open note, in a tree of its own beside the app (the dock is hidden in Write mode). */
function actionsBeside(queryClient: QueryClient) {
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
  const shell = { onMove: vi.fn(), onDelete: vi.fn(), onDate: vi.fn(), onFlag: vi.fn() };
  return renderHook(() => useNoteActions("/Work/inbox.md", shell), { wrapper });
}

async function writeMore(queryClient: QueryClient) {
  renderApp("/notes/Work/inbox.md", queryClient);
  const write = await screen.findByRole("button", { name: "Write" });
  await waitFor(() => expect(write).toBeEnabled());
  await userEvent.click(write);
  await userEvent.type(await screen.findByRole("textbox", { name: "Markdown source" }), " more");
}

describe("the note's actions", () => {
  it("offers_the_same_items_as_before", async () => {
    server();
    renderApp("/notes/Work/inbox.md");
    await screen.findByRole("heading", { name: "inbox" });
    await openMenu();
    await screen.findByRole("menuitem", { name: "Add to favourites" });
    expect(labels()).toEqual([
      "Find in note",
      "History",
      "Who wrote what",
      "Open graph…",
      "Copy file path",
      "Copy link",
      "Copy markdown link",
      "Add to favourites",
      "Duplicate",
      "Rename or move",
      "Delete note",
      "Flag",
      "Add to calendar…",
    ]);
  });

  it("write_mode_offers_nothing_that_acts_on_the_saved_text", async () => {
    server({ content: "# inbox\n\nText.", publish: true });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/notes/Work/inbox.md", queryClient);
    await screen.findByRole("heading", { name: "inbox" });
    const actions = actionsBeside(queryClient);
    const offered = () => actions.result.current.items.map((i) => i.label);
    await waitFor(() => expect(offered()).toEqual(expect.arrayContaining(["History", "Publish…", "Add property…"])));
    await userEvent.click(await screen.findByRole("button", { name: "Write" }));
    await waitFor(() => expect(useEditor.getState().mode).toBe("write"));
    expect(offered()).toContain("Duplicate");
    for (const label of ["Find in note", "History", "Who wrote what", "Publish…", "Add property…"]) expect(offered()).not.toContain(label);
  });

  it("who_wrote_what_opens_its_window_over_the_note", async () => {
    server();
    const router = renderApp("/notes/Work/inbox.md");
    await screen.findByRole("heading", { name: "inbox" });
    await openMenu();
    await userEvent.click(screen.getByRole("menuitem", { name: "Who wrote what" }));
    expect(await screen.findByRole("dialog", { name: "Who wrote what" })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe("/blame/Work/inbox.md");
  });

  it("no_duplicate_under_archive_or_without_write", async () => {
    server();
    renderApp("/notes/Work/_archive/old.md");
    await screen.findByRole("heading", { name: "old" });
    await openMenu();
    expect(screen.queryByRole("menuitem", { name: "Duplicate" })).not.toBeInTheDocument();
    cleanup();

    server({ vaults: [{ name: "Shared", notes: 1, embeddings: "internal", owner: '"Carol"', level: "read", canWrite: false, shared: true, membership: "m3" }] });
    renderApp("/notes/Shared/inbox.md");
    await screen.findByRole("heading", { name: "inbox" });
    await openMenu();
    expect(screen.queryByRole("menuitem", { name: "Duplicate" })).not.toBeInTheDocument();
  });

  it("duplicate_writes_the_copy_beside", async () => {
    const state = server();
    renderApp("/notes/Work/inbox.md");
    await duplicate();
    await waitFor(() => expect(state.copies).toEqual([{ path: "/Work/Copy of inbox.md", body: { content: copyText(ORIGINAL) } }]));
  });

  it("two_duplicates_in_a_row", async () => {
    const state = server({ taken: (path) => path === "/Work/Copy of inbox.md" });
    renderApp("/notes/Work/inbox.md");
    await duplicate();
    expect(await screen.findByRole("status", { name: "Copy created" })).toHaveTextContent("Copy of inbox 2.md created");
    expect(state.copies.map((c) => c.path)).toEqual(["/Work/Copy of inbox.md", "/Work/Copy of inbox 2.md"]);
  });

  it("gives_up_after_99", async () => {
    const state = server({ taken: () => true });
    renderApp("/notes/Work/inbox.md");
    await duplicate();
    expect(await screen.findByRole("alert")).toHaveTextContent("There are already 99 copies of inbox.md; rename one and try again.");
    expect(state.copies).toHaveLength(99);
    expect(state.copies.at(-1)!.path).toBe("/Work/Copy of inbox 99.md");
    expect(screen.queryByRole("status", { name: "Copy created" })).not.toBeInTheDocument();
  });

  it("saves_unsaved_text_first", async () => {
    const state = server();
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    await writeMore(queryClient);
    state.events = [];
    const actions = actionsBeside(queryClient);
    act(() => actions.result.current.items.find((i) => i.label === "Duplicate")!.onSelect());
    await waitFor(() => expect(state.events).toEqual(["save", "read", "copy /Work/Copy of inbox.md"]));
    expect(state.content).toContain("more");
    expect(state.copies[0].body).toEqual({ content: copyText(state.content) });
    // Write mode goes on.
    expect(useEditor.getState().mode).toBe("write");
  });

  it("a_failed_save_copies_nothing", async () => {
    const state = server({ failSave: true });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    await writeMore(queryClient);
    state.events = [];
    const actions = actionsBeside(queryClient);
    act(() => actions.result.current.items.find((i) => i.label === "Duplicate")!.onSelect());
    await waitFor(() => expect(state.events).toEqual(["save"]));
    await new Promise((r) => setTimeout(r, 50));
    // Neither read nor copied.
    expect(state.events).toEqual(["save"]);
    expect(useEditor.getState().mode).toBe("write");
  });

  it("the_notice_names_the_copy_and_open_opens_it", async () => {
    server();
    const router = renderApp("/notes/Work/inbox.md");
    await duplicate();
    const notice = await screen.findByRole("status", { name: "Copy created" });
    expect(notice).toHaveTextContent("Copy of inbox.md created");
    await userEvent.click(within(notice).getByRole("button", { name: "Open" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/Copy%20of%20inbox.md"));
    expect(screen.queryByRole("status", { name: "Copy created" })).not.toBeInTheDocument();
  });

  it("hiding_the_vault_again_forgets_the_notice_and_the_error", async () => {
    server({ taken: (path) => path !== "/Work/Copy of inbox 3.md" && !path.endsWith(" 4.md") });
    renderApp("/notes/Work/inbox.md");
    await duplicate();
    await screen.findByRole("status", { name: "Copy created" });
    act(() => useDiscreet.setState({ gone: new Set(["Work"]) }));
    expect(screen.queryByRole("status", { name: "Copy created" })).not.toBeInTheDocument();
    cleanup();
    useDiscreet.setState({ gone: new Set() });

    server({ taken: () => true });
    renderApp("/notes/Work/inbox.md");
    await duplicate();
    expect(await screen.findByRole("alert")).toHaveTextContent("99 copies of inbox.md");
    act(() => useDiscreet.setState({ gone: new Set(["Work"]) }));
    expect(screen.queryByText(/99 copies of inbox\.md/)).not.toBeInTheDocument();
  });

  it("a_copy_made_after_hiding_names_nothing", async () => {
    // Hidden while the unsaved text is being saved: the copy is made after, and its notice would name it.
    const listed: unknown[] = [{ name: "Work", notes: 1, embeddings: "internal" }];
    const state = server({ vaults: listed });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    let release = () => {};
    const saved = new Promise<void>((resolve) => (release = resolve));
    const fetch = globalThis.fetch;
    vi.stubGlobal("fetch", async (r: Request) => {
      if (r.method === "PUT" && notePath(r) === "/Work/inbox.md") await saved;
      return fetch(r);
    });
    await writeMore(queryClient);
    fireEvent.keyDown(screen.getByRole("textbox", { name: "Markdown source" }), { key: "F10", code: "F10", shiftKey: true });
    await userEvent.click(await screen.findByRole("menuitem", { name: "Duplicate" }));
    // Hidden, the vault is no longer listed.
    listed.length = 0;
    act(() => useDiscreet.setState({ gone: new Set(["Work"]) }));
    await act(async () => release());
    await waitFor(() => expect(state.copies).toHaveLength(1));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByText(/Copy of inbox/)).not.toBeInTheDocument();
    // Nor once the vault is shown again.
    act(() => useDiscreet.setState({ gone: new Set() }));
    expect(screen.queryByText(/Copy of inbox/)).not.toBeInTheDocument();
  });

  it("a_late_failure_after_hiding_names_nothing", async () => {
    let answer = (_: Response) => {};
    const held = new Promise<Response>((resolve) => (answer = resolve));
    server();
    const fetch = globalThis.fetch;
    vi.stubGlobal("fetch", (r: Request) => (r.method === "PUT" && notePath(r) === "/Work/Copy of inbox.md" ? held : fetch(r)));
    renderApp("/notes/Work/inbox.md");
    await duplicate();
    act(() => useDiscreet.setState({ gone: new Set(["Work"]) }));
    await act(async () => answer(json(500, { error: "Couldn't write inbox.md." })));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByText(/inbox\.md/)).not.toBeInTheDocument();
  });

  it("the_original_stays_open", async () => {
    server();
    const router = renderApp("/notes/Work/inbox.md");
    await duplicate();
    await screen.findByRole("status", { name: "Copy created" });
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
    expect(useEditor.getState().currentPath).toBe("/Work/inbox.md");
    expect(screen.getByRole("heading", { name: "inbox" })).toBeInTheDocument();
  });
});

describe("the notice of a copy", () => {
  const show = () => {
    const onOpen = vi.fn();
    const onClose = vi.fn();
    render(<DuplicateNotice name="Copy of plan.md" onOpen={onOpen} onClose={onClose} />);
    return { onOpen, onClose, notice: screen.getByRole("status", { name: "Copy created" }) };
  };

  it("goes after 8 seconds", () => {
    vi.useFakeTimers();
    const { onClose } = show();
    act(() => vi.advanceTimersByTime(7999));
    expect(onClose).not.toHaveBeenCalled();
    act(() => vi.advanceTimersByTime(1));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("stays while the focus is in it, and goes once the focus leaves", () => {
    vi.useFakeTimers();
    const { onClose, notice } = show();
    within(notice).getByRole("button", { name: "Open" }).focus();
    act(() => vi.advanceTimersByTime(9000));
    expect(onClose).not.toHaveBeenCalled();
    act(() => within(notice).getByRole("button", { name: "Open" }).blur());
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("goes on Esc while focused, and on ✕", () => {
    const { onClose, notice } = show();
    fireEvent.keyDown(within(notice).getByRole("button", { name: "Open" }), { key: "Escape" });
    expect(onClose).toHaveBeenCalledOnce();
    fireEvent.click(within(notice).getByRole("button", { name: "Dismiss" }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});
