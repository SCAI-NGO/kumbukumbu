// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const entry = (path: string, extra: Record<string, unknown> = {}) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown", version: `v-${path}`, ...extra });
const tree = {
  damaged: 0,
  root: {
    name: "Notes",
    path: "/Notes",
    writtenAt: "2026-11-14T09:00:00Z",
    folders: [
      {
        name: "Garden",
        path: "/Notes/Garden",
        writtenAt: "2026-11-14T09:00:00Z",
        folders: [],
        notes: [entry("/Notes/Garden/beds.md", { writtenAt: "2026-11-14T09:00:00Z", flag: "red" })],
      },
      {
        name: "Pump",
        path: "/Notes/Pump",
        writtenAt: "2026-10-01T09:00:00Z",
        folders: [],
        notes: [entry("/Notes/Pump/Certificate.md", { writtenAt: "2026-10-01T09:00:00Z" }), entry("/Notes/Pump/manual.pdf", { kind: "pdf", mediaType: "application/pdf", writtenAt: "2026-09-01T09:00:00Z" })],
      },
    ],
    notes: [entry("/Notes/inbox.md", { writtenAt: "2026-11-01T09:00:00Z" }), entry("/Notes/later.md", { writtenAt: "2026-11-13T09:00:00Z", flag: "done" })],
  },
};

const drawer = () => screen.getByRole("dialog", { name: "File tree" });
/** The tree's rows as read: a folder by its name, a note by its title. */
const shown = () =>
  within(drawer())
    .queryAllByRole("listitem")
    .map((li) => li.querySelector("[data-tree-row] span")!.textContent);

function signedIn({ level = "owner", root = tree.root, extra = {} }: { level?: string; root?: typeof tree.root; extra?: Parameters<typeof fakeApi>[0] } = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Notes", notes: 4, embeddings: "internal", canWrite: level !== "read", level }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Notes/tree": () => json(200, { ...tree, root }),
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/search": () => json(200, { results: [], notIndexed: 0, notice: null }),
    "GET /api/version/*": () => json(404, { error: "No such file" }),
    "GET /api/recent": () => json(200, { items: [], more: false, vaults: ["Notes"] }),
    ...extra,
  });
}

/** The tree of Notes with its board. */
const withBoard = { ...tree.root, folders: [{ name: "Board", path: "/Notes/Board", writtenAt: "2026-11-01T09:00:00Z", folders: [], notes: [entry("/Notes/Board/Board.md")] }, ...tree.root.folders] };

/** The toolbar's row above the folders, closed search. */
const toolbar = () => within(drawer()).getByRole("group", { name: "Tools for this vault" });
/** Its targets in their order, each by its accessible name. */
const tools = () => [...toolbar().querySelectorAll("button, a")].map((el) => el.getAttribute("aria-label") ?? el.textContent);

async function openDrawer(options: Parameters<typeof signedIn>[0] = {}) {
  signedIn(options);
  const router = renderApp("/notes/Notes/inbox.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("later");
  return router;
}

async function find(words: string) {
  await userEvent.click(within(drawer()).getByRole("button", { name: "Find in this vault" }));
  const field = within(drawer()).getByRole("searchbox", { name: "Find by name" });
  expect(field).toHaveFocus();
  if (words) await userEvent.type(field, words);
  return field;
}

beforeEach(() => {
  window.innerWidth = 390;
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("finding in the tree", () => {
  it("shows only what matches, and the folders on the way, opened", async () => {
    await openDrawer();
    expect(shown()).toEqual(["Garden", "Pump", "inbox", "later"]);
    await find("pump cert");
    expect(shown()).toEqual(["Pump", "Certificate"]);
    expect(within(drawer()).getByRole("button", { name: /^Pump/ })).toHaveAttribute("aria-expanded", "true");
  });

  it("Escape gives the full tree back", async () => {
    await openDrawer();
    await find("pump");
    expect(shown()).toEqual(["Pump", "Certificate", "manual.pdf"]);
    await userEvent.keyboard("{Escape}");
    // The search closes, the drawer stays.
    expect(within(drawer()).queryByRole("searchbox", { name: "Find by name" })).not.toBeInTheDocument();
    expect(shown()).toEqual(["Garden", "Pump", "inbox", "later"]);
    expect(within(drawer()).getByRole("button", { name: "Find in this vault" })).toHaveFocus();
    // Opened again, it starts empty.
    expect(await find("")).toHaveValue("");
  });

  it("✕ gives the full tree back too", async () => {
    await openDrawer();
    await find("garden");
    await userEvent.click(within(drawer()).getByRole("button", { name: "Close the search" }));
    expect(shown()).toEqual(["Garden", "Pump", "inbox", "later"]);
  });

  it("Nothing called that here.", async () => {
    await openDrawer();
    await find("pump x");
    expect(within(drawer()).queryAllByRole("listitem")).toEqual([]);
    const nothing = within(drawer()).getByText("Nothing called that here.");
    const next = within(drawer()).getByRole("button", { name: "Search the contents…" });
    expect(nothing.compareDocumentPosition(next) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(drawer()).queryByText(/No notes yet/)).not.toBeInTheDocument();
  });

  it("Search the contents… opens the search with the words", async () => {
    await openDrawer();
    await find("pump cert");
    await userEvent.click(within(drawer()).getByRole("button", { name: "Search the contents…" }));
    const sheet = await screen.findByRole("dialog", { name: "Search" });
    expect(within(sheet).getByRole("searchbox", { name: "Search" })).toHaveValue("pump cert");
    // On a phone the drawer gives way to the sheet.
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
  });

  it("the chips wrap onto further rows instead of scrolling, named apart from the note's flag", async () => {
    await openDrawer();
    await find("");
    const flags = within(drawer()).getByRole("group", { name: "Find by flag" });
    expect(flags).toHaveClass("flex-wrap");
    expect(flags).not.toHaveClass("flex-nowrap");
    expect(flags).not.toHaveClass("overflow-x-auto");
    expect(within(drawer()).queryByRole("group", { name: "Flag" })).not.toBeInTheDocument();
  });

  it("a pressed chip stays shown while it is pressed, even when no note carries its flag any more", async () => {
    let root = tree.root;
    signedIn({ extra: { "GET /api/vaults/Notes/tree": () => json(200, { ...tree, root }) } });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    renderApp("/notes/Notes/inbox.md", queryClient);
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("later");
    await find("");
    const flags = () => within(drawer()).getByRole("group", { name: "Find by flag" });
    await userEvent.click(within(flags()).getByRole("button", { name: "Red" }));
    // The red note loses its flag elsewhere.
    root = { ...tree.root, folders: tree.root.folders.map((f) => ({ ...f, notes: f.notes.map((n) => ({ ...n, flag: undefined })) })) };
    await act(() => queryClient.invalidateQueries({ queryKey: ["tree", "Notes"] }));
    await waitFor(() => expect(shown()).toEqual([]));
    expect(within(flags()).getAllByRole("button").map((b) => b.textContent)).toEqual(["Any flag", "Red", "Done"]);
    expect(within(flags()).getByRole("button", { name: "Red" })).toHaveAttribute("aria-pressed", "true");
    // Let go, it goes.
    await userEvent.click(within(flags()).getByRole("button", { name: "Red" }));
    expect(within(flags()).getAllByRole("button").map((b) => b.textContent)).toEqual(["Any flag", "Done"]);
  });

  it("shows no chips where no note carries a flag", async () => {
    const plain = { ...tree.root, folders: tree.root.folders.map((f) => ({ ...f, notes: f.notes.map((n) => ({ ...n, flag: undefined })) })), notes: tree.root.notes.map((n) => ({ ...n, flag: undefined })) };
    await openDrawer({ root: plain });
    await find("");
    expect(within(drawer()).queryByRole("group", { name: "Find by flag" })).not.toBeInTheDocument();
    expect(within(drawer()).queryByRole("button", { name: "Any flag" })).not.toBeInTheDocument();
    expect(within(drawer()).getByRole("button", { name: "Search the contents…" })).toBeInTheDocument();
  });

  it("the field is a 48 px target", async () => {
    await openDrawer();
    expect(await find("")).toHaveClass("h-12");
  });

  it("a flag chip, one at a time, with the words or alone", async () => {
    await openDrawer();
    await find("");
    const flags = within(drawer()).getByRole("group", { name: "Find by flag" });
    // Only the flags some note of this vault carries, in the flags' order.
    expect(within(flags).getAllByRole("button").map((b) => b.textContent)).toEqual(["Any flag", "Red", "Done"]);
    await userEvent.click(within(flags).getByRole("button", { name: "Any flag" }));
    expect(shown()).toEqual(["Garden", "beds", "later"]);
    await userEvent.click(within(flags).getByRole("button", { name: "Done" }));
    expect(within(flags).getByRole("button", { name: "Any flag" })).toHaveAttribute("aria-pressed", "false");
    expect(within(flags).getByRole("button", { name: "Done" })).toHaveAttribute("aria-pressed", "true");
    expect(shown()).toEqual(["later"]);
    await userEvent.type(within(drawer()).getByRole("searchbox", { name: "Find by name" }), "garden");
    expect(shown()).toEqual([]);
    // Pressed again, the chip lets go.
    await userEvent.click(within(flags).getByRole("button", { name: "Done" }));
    expect(shown()).toEqual(["Garden", "beds"]);
  });
});

describe("the toolbar", () => {
  it("is one row directly above the folders: New note, the icons, then the vault's menu", async () => {
    await openDrawer({ root: withBoard });
    expect(tools()).toEqual(["New note", "Upload", "Calendar", "Board", "Recent changes", "Sorted by name. Sort by last changed", "Find in this vault", "Actions for Notes"]);
    expect(toolbar().compareDocumentPosition(within(drawer()).getAllByRole("list")[0]) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // One menu, in one place: the vault's name has none beside it any more.
    const menus = within(drawer()).getAllByRole("button", { name: "Actions for Notes" });
    expect(menus).toHaveLength(1);
    expect(toolbar()).toContainElement(menus[0]);
  });

  it("New note is the one with words, in the accent; every icon is a 48 px target named by its label and its tooltip", async () => {
    await openDrawer({ root: withBoard });
    const newNote = within(toolbar()).getByRole("button", { name: "New note" });
    expect(newNote).toHaveClass("min-h-12");
    expect(newNote).toHaveTextContent("New note");
    expect(newNote.querySelector(".bg-accent")).not.toBeNull();
    const icons = [...toolbar().querySelectorAll("button, a")].filter((el) => el !== newNote);
    expect(icons).toHaveLength(7);
    for (const icon of icons) {
      expect(icon).toHaveClass("size-12");
      expect(icon).toHaveAttribute("title", icon.getAttribute("aria-label"));
      expect(icon.textContent).toBe("");
    }
  });

  it("offers no New note and no Upload to someone who only reads", async () => {
    await openDrawer({ level: "read" });
    expect(tools()).toEqual(["Calendar", "Recent changes", "Sorted by name. Sort by last changed", "Find in this vault", "Actions for Notes"]);
  });

  it("New note makes a note in the vault's root", async () => {
    let body: unknown;
    const router = await openDrawer({
      extra: {
        "PUT /api/notes/Notes/Untitled.md": async (r) => {
          body = await r.json();
          return json(201, { path: "/Notes/Untitled.md", content: "# ", version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-15T09:00:00Z" });
        },
      },
    });
    await userEvent.click(within(toolbar()).getByRole("button", { name: "New note" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Notes/Untitled.md"));
    expect(body).toEqual({ content: "# " });
  });

  it("Upload opens the upload into the vault's root", async () => {
    await openDrawer();
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Upload" }));
    expect(await screen.findByRole("dialog", { name: "Upload" })).toBeInTheDocument();
    expect(useEditor.getState().upload?.folder).toBe("/Notes");
  });

  it("Calendar and Board open the vault's views, closing the drawer", async () => {
    const router = await openDrawer({ root: withBoard });
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Calendar" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/calendar/Notes"));
    expect(useEditor.getState().overlay).toBe("none");
    await router.navigate("/notes/Notes/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Board" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/board/Notes"));
    expect(useEditor.getState().overlay).toBe("none");
  });

  it("Recent changes is the clock, and opens its page across the vaults", async () => {
    const router = await openDrawer();
    const item = within(toolbar()).getByRole("link", { name: "Recent changes" });
    expect(item).toHaveAttribute("href", "/recent");
    expect(item.querySelector("svg")).not.toBeNull();
    await userEvent.click(item);
    await waitFor(() => expect(router.state.location.pathname).toBe("/recent"));
    expect(await screen.findByRole("heading", { level: 1, name: "Recent changes" })).toBeInTheDocument();
    expect(useEditor.getState().overlay).toBe("none");
  });

  it("the vault's menu keeps Calendar and Board, and offers Recent changes after the gallery", async () => {
    const router = await openDrawer({ root: withBoard });
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Actions for Notes" }));
    const items = screen.getAllByRole("menuitem").map((i) => i.textContent);
    expect(items.slice(0, 4)).toEqual(["New note here", "New folder…", "Upload here", "Trash"]);
    expect(items).toEqual(expect.arrayContaining(["Calendar", "Board"]));
    expect(items.indexOf("Recent changes")).toBe(items.indexOf("Gallery") + 1);
    await userEvent.click(screen.getByRole("menuitem", { name: "Recent changes" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/recent"));
    expect(useEditor.getState().overlay).toBe("none");
  });

  it("while Find by name is open, the row is the search's: Sort and Recent changes drop out, Search the contents… stays", async () => {
    await openDrawer();
    await find("pump");
    expect(within(drawer()).queryByRole("group", { name: "Tools for this vault" })).not.toBeInTheDocument();
    expect(within(drawer()).queryByRole("link", { name: "Recent changes" })).not.toBeInTheDocument();
    expect(within(drawer()).queryByRole("button", { name: /^Sorted by/ })).not.toBeInTheDocument();
    expect(within(drawer()).getByRole("button", { name: "Search the contents…" })).toBeInTheDocument();
  });
});

describe("the toolbar where the row is narrow", () => {
  let width = 0;
  const observers = new Set<{ target: Element; callback: ResizeObserverCallback }>();
  /** The row's width changes, as the sidebar is resized: every observer of the toolbar hears it. */
  const resize = (next: number) =>
    act(() => {
      width = next;
      for (const o of observers) o.callback([{ target: o.target, contentRect: { width } } as unknown as ResizeObserverEntry], {} as ResizeObserver);
    });

  beforeEach(() => {
    width = 302;
    observers.clear();
    // jsdom lays nothing out: the row reports its width through this stand-in, + New note its own.
    vi.stubGlobal(
      "ResizeObserver",
      class {
        constructor(private callback: ResizeObserverCallback) {}
        observe(target: Element) {
          if (target.getAttribute("aria-label") !== "Tools for this vault") return;
          observers.add({ target, callback: this.callback });
          this.callback([{ target, contentRect: { width } } as unknown as ResizeObserverEntry], this as unknown as ResizeObserver);
        }
        unobserve() {}
        disconnect() {
          for (const o of observers) if (o.callback === this.callback) observers.delete(o);
        }
      },
    );
    vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockImplementation(function (this: HTMLElement) {
      return this.textContent === "New note" ? 108 : 0;
    });
  });

  it("moves Upload, Sort and Recent changes to the top of ⋯, and follows the width live", async () => {
    await openDrawer({ root: withBoard });
    expect(tools()).toEqual(["New note", "Calendar", "Board", "Find in this vault", "Actions for Notes"]);
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Actions for Notes" }));
    const items = screen.getAllByRole("menuitem").map((i) => i.textContent);
    expect(items.slice(0, 4)).toEqual(["Upload", "Sort by last changed", "Recent changes", "New note here"]);
    // Once each: Upload is the root's Upload here, Recent changes no longer further down.
    expect(items).not.toContain("Upload here");
    expect(items.filter((i) => i === "Recent changes")).toHaveLength(1);
    await userEvent.click(screen.getByRole("menuitem", { name: "Sort by last changed" }));
    expect(shown()).toEqual(["Garden", "Board", "Pump", "later", "inbox"]);

    // Wider: everything is back in the row, and the menu is as before.
    resize(600);
    expect(tools()).toEqual(["New note", "Upload", "Calendar", "Board", "Recent changes", "Sorted by last changed. Sort by name", "Find in this vault", "Actions for Notes"]);
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Actions for Notes" }));
    expect(screen.getAllByRole("menuitem")[0]).toHaveTextContent("New note here");
    await userEvent.keyboard("{Escape}");

    // Narrower than the drawer: Find goes too, into ⋯ after the three; New note and ⋯ stay.
    resize(252);
    expect(tools()).toEqual(["New note", "Calendar", "Board", "Actions for Notes"]);
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Actions for Notes" }));
    expect(screen.getAllByRole("menuitem").map((i) => i.textContent).slice(0, 4)).toEqual(["Upload", "Sort by name", "Recent changes", "Find in this vault"]);
    await userEvent.click(screen.getByRole("menuitem", { name: "Find in this vault" }));
    expect(within(drawer()).getByRole("searchbox", { name: "Find by name" })).toHaveFocus();
  });

  it("Upload and Recent changes from ⋯ do what their buttons do", async () => {
    const router = await openDrawer();
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Actions for Notes" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Upload" }));
    expect(await screen.findByRole("dialog", { name: "Upload" })).toBeInTheDocument();
    expect(useEditor.getState().upload?.folder).toBe("/Notes");
    act(() => useEditor.getState().setOverlay("tree"));
    await userEvent.click(within(toolbar()).getByRole("button", { name: "Actions for Notes" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Recent changes" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/recent"));
    expect(useEditor.getState().overlay).toBe("none");
  });
});

describe("the empty tree", () => {
  const empty = { ...tree.root, folders: [], notes: [] };

  it("points to New note", async () => {
    signedIn({ root: empty });
    renderApp("/notes/Notes");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    expect(await within(drawer()).findByText("No notes yet. Make the first with + New note above.")).toBeInTheDocument();
  });

  it("says only that to someone who reads", async () => {
    signedIn({ root: empty, level: "read" });
    renderApp("/notes/Notes");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    expect(await within(drawer()).findByText("No notes yet.")).toBeInTheDocument();
    expect(within(drawer()).queryByText(/New note/)).not.toBeInTheDocument();
  });
});

describe("the tree's order", () => {
  const sort = () => within(toolbar()).getByRole("button", { name: /^Sorted by/ });

  it("one button switches to Last changed, which puts what moved last at the top, folders above files", async () => {
    await openDrawer();
    expect(sort()).toHaveAccessibleName("Sorted by name. Sort by last changed");
    expect(sort()).toHaveAttribute("title", "Sorted by name. Sort by last changed");
    await userEvent.click(sort());
    expect(sort()).toHaveAccessibleName("Sorted by last changed. Sort by name");
    expect(sort()).toHaveAttribute("title", "Sorted by last changed. Sort by name");
    expect(shown()).toEqual(["Garden", "Pump", "later", "inbox"]);
    await userEvent.click(within(drawer()).getByRole("button", { name: /^Pump/ }));
    expect(shown()).toEqual(["Garden", "Pump", "Certificate", "manual.pdf", "later", "inbox"]);
    await userEvent.click(sort());
    expect(sort()).toHaveAccessibleName("Sorted by name. Sort by last changed");
    expect(shown()).toEqual(["Garden", "Pump", "Certificate", "manual.pdf", "inbox", "later"]);
  });

  it("the choice is remembered on the device", async () => {
    await openDrawer();
    await userEvent.click(sort());
    expect(localStorage.getItem("kumbu.treeOrder")).toBe("changed");
    await userEvent.click(screen.getByRole("button", { name: "Close file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await waitFor(() => expect(sort()).toHaveAccessibleName("Sorted by last changed. Sort by name"));
    expect(shown()).toEqual(["Garden", "Pump", "later", "inbox"]);
  });

  it("the order holds for what a search finds", async () => {
    localStorage.setItem("kumbu.treeOrder", "changed");
    await openDrawer();
    await find("e");
    expect(shown()).toEqual(["Garden", "beds", "Pump", "Certificate", "later"]);
  });
});

describe("the toolbar on the desktop", () => {
  it("the docked sidebar finds as the drawer does, and Escape leaves it open", async () => {
    window.innerWidth = 1280;
    vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", media: query, addEventListener: () => {}, removeEventListener: () => {} }));
    signedIn();
    renderApp("/notes/Notes/inbox.md");
    const sidebar = await screen.findByRole("complementary", { name: "File tree" });
    await within(sidebar).findByText("later");
    // The same row, docked: above the folders, with the vault's one menu.
    const row = within(sidebar).getByRole("group", { name: "Tools for this vault" });
    expect(within(sidebar).getAllByRole("button", { name: "Actions for Notes" })).toEqual([within(row).getByRole("button", { name: "Actions for Notes" })]);
    await userEvent.click(within(sidebar).getByRole("button", { name: "Find in this vault" }));
    await userEvent.type(within(sidebar).getByRole("searchbox", { name: "Find by name" }), "cert");
    expect(within(sidebar).getAllByRole("listitem").map((li) => li.querySelector("[data-tree-row] span")!.textContent)).toEqual(["Pump", "Certificate"]);
    await userEvent.keyboard("{Escape}");
    expect(within(sidebar).getAllByRole("listitem")).toHaveLength(4);
    expect(screen.getByRole("complementary", { name: "File tree" })).toBeVisible();
  });
});
