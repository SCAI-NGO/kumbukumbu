// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
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

function signedIn() {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Notes", notes: 4, embeddings: "internal", canWrite: true, level: "owner" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Notes/tree": () => json(200, tree),
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/search": () => json(200, { results: [], notIndexed: 0, notice: null }),
    "GET /api/version/*": () => json(404, { error: "No such file" }),
  });
}

async function openDrawer() {
  signedIn();
  renderApp("/notes/Notes/inbox.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("later");
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

  it("a flag chip, one at a time, with the words or alone", async () => {
    await openDrawer();
    await find("");
    const flags = within(drawer()).getByRole("group", { name: "Flag" });
    expect(within(flags).getAllByRole("button").map((b) => b.textContent)).toEqual(["Any flag", "Red", "Green", "Blue", "Purple", "Yellow", "White", "Done", "Important"]);
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

describe("the tree's order", () => {
  it("Last changed puts what moved last at the top, folders above files", async () => {
    await openDrawer();
    const order = within(drawer()).getByRole("radiogroup", { name: "Order" });
    expect(within(order).getByRole("radio", { name: "Name" })).toBeChecked();
    await userEvent.click(within(order).getByRole("radio", { name: "Last changed" }));
    expect(shown()).toEqual(["Garden", "Pump", "later", "inbox"]);
    await userEvent.click(within(drawer()).getByRole("button", { name: /^Pump/ }));
    expect(shown()).toEqual(["Garden", "Pump", "Certificate", "manual.pdf", "later", "inbox"]);
  });

  it("the choice is remembered on the device", async () => {
    await openDrawer();
    await userEvent.click(within(drawer()).getByRole("radio", { name: "Last changed" }));
    expect(localStorage.getItem("kumbu.treeOrder")).toBe("changed");
    await userEvent.click(screen.getByRole("button", { name: "Close file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await waitFor(() => expect(within(drawer()).getByRole("radio", { name: "Last changed" })).toBeChecked());
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
    await userEvent.click(within(sidebar).getByRole("button", { name: "Find in this vault" }));
    await userEvent.type(within(sidebar).getByRole("searchbox", { name: "Find by name" }), "cert");
    expect(within(sidebar).getAllByRole("listitem").map((li) => li.querySelector("[data-tree-row] span")!.textContent)).toEqual(["Pump", "Certificate"]);
    await userEvent.keyboard("{Escape}");
    expect(within(sidebar).getAllByRole("listitem")).toHaveLength(4);
    expect(screen.getByRole("complementary", { name: "File tree" })).toBeVisible();
  });
});
