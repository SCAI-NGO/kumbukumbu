// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { TreeFolder } from "../api/notes";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown" });
const folder = (path: string, folders: TreeFolder[] = [], notes: ReturnType<typeof note>[] = []): TreeFolder => ({ name: path.split("/").at(-1)!, path, folders, notes });

/**
 * `/Work/Private` holds `secret.md`, `/Work/Routing` holds `Questions.md`; `inbox.md` sits at the root. `moved`:
 * `Private` has been moved into `Routing`.
 */
const workTree = (moved = false) => {
  const secret = (at: string) => folder(at, [], [note(`${at}/secret.md`)]);
  return folder(
    "/Work",
    [...(moved ? [] : [secret("/Work/Private")]), folder("/Work/Routing", moved ? [secret("/Work/Routing/Private")] : [], [note("/Work/Routing/Questions.md")])],
    [note("/Work/inbox.md")],
  );
};

const publication = (path: string) => ({ path, snapshotAt: "2026-11-05T14:32:00Z", older: false, changedAt: null, publishedBy: "ada", link: null });

const owned = { name: "Work", notes: 3, embeddings: "internal" };
const shared = { ...owned, owner: '"Alice"', level: "maintain", shared: true, membership: "m1" };

const publishingItems = ["Publish…", "Publish… (unpublishable)", "Mark unpublishable", "Allow publishing"];

/**
 * The server's state: the marks change with the PUTs and DELETEs on `/api/unpublishable`, and follow `Private`'s move.
 * `published`: the notes published. `marksFail`: the vault's publications can't be read.
 */
function api({
  publishEnabled = true,
  vault = owned as Record<string, unknown>,
  marks = [] as string[],
  published = [] as string[],
  marksFail = false,
} = {}) {
  const state = { marks: [...marks], moved: false };
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, passwordWindowOpen: true, publishEnabled }),
    "GET /api/vaults": () => json(200, [vault]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: workTree(state.moved) }),
    "GET /api/vaults/Work/publications": () =>
      marksFail ? json(500, { error: "Something went wrong." }) : json(200, { publications: published.map(publication), unpublishable: vault.owner ? [] : state.marks }),
    "GET /api/publications/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/publications".length));
      return published.includes(path) ? json(200, publication(path)) : json(404, { error: `${path.slice(1)} is not published.` });
    },
    "POST /api/move": async (r) => {
      const { from, to } = (await r.json()) as { from: string; to: string };
      state.moved = true;
      state.marks = state.marks.map((m) => (`${m}/` === from ? to : m));
      return json(200, { message: `moved ${from} to ${to}/ (1); no links needed rewriting`, path: null, version: null });
    },
    "PUT /api/unpublishable/*": (r) => {
      state.marks.push(decodeURIComponent(new URL(r.url).pathname.slice("/api/unpublishable".length)));
      return noContent();
    },
    "DELETE /api/unpublishable/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/unpublishable".length));
      state.marks = state.marks.filter((m) => m !== path);
      return noContent();
    },
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/version/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/version".length)), version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
  });
  return { fetch, state };
}

const drawer = () => screen.getByRole("dialog", { name: "File tree" });
const row = (name: string) => within(drawer()).getByRole("button", { name: new RegExp(`^${name}`) });
const requests = (fetch: ReturnType<typeof fakeApi>, method: string, prefix: string) =>
  fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === method && new URL(r.url).pathname.startsWith(prefix));

async function openDrawer(at = "/notes/Work/inbox.md") {
  renderApp(at);
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("Private");
}

async function openFolder(name: string, child: string) {
  await userEvent.click(row(name));
  await within(drawer()).findByRole("button", { name: new RegExp(`^${child}`) });
}

/** The entries of a row's menu, each with whether it is disabled; the menu is closed again. */
async function menuOf(name: string) {
  await userEvent.click(within(drawer()).getByRole("button", { name: `Actions for ${name}` }));
  const items = screen.getAllByRole("menuitem").map((i) => ({ label: i.textContent, disabled: (i as HTMLButtonElement).disabled }));
  await userEvent.keyboard("{Escape}");
  return items;
}
const labels = async (name: string) => (await menuOf(name)).map((i) => i.label);

async function noteMenu() {
  await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
  const items = screen.getAllByRole("menuitem").map((i) => ({ label: i.textContent, disabled: (i as HTMLButtonElement).disabled }));
  await userEvent.keyboard("{Escape}");
  return items;
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

describe("publishing in the tree", () => {
  it("shows marks in the tree for the owner", async () => {
    api({ marks: ["/Work/Private", "/Work/inbox.md"] });
    await openDrawer();
    await waitFor(() => expect(within(row("Private")).getByRole("img", { name: "Unpublishable" })).toBeInTheDocument());
    expect(within(row("inbox")).getByRole("img", { name: "Unpublishable" })).toBeInTheDocument();
    expect(within(row("Routing")).queryByRole("img", { name: "Unpublishable" })).toBeNull();
    // Covered by its folder's mark, yet not marked itself: the mark is the folder's.
    await openFolder("Private", "secret");
    expect(within(row("secret")).queryByRole("img", { name: "Unpublishable" })).toBeNull();
  });

  it("offers no publishing and no marks to members", async () => {
    const { fetch } = api({ vault: shared, marks: ["/Work/Private"] });
    await openDrawer();
    await openFolder("Routing", "Questions");
    for (const name of ["Work", "Private", "Routing", "inbox", "Questions"]) {
      const items = await labels(name);
      expect(items.length).toBeGreaterThan(0);
      for (const item of publishingItems) expect(items).not.toContain(item);
    }
    expect(within(drawer()).queryByRole("img", { name: "Unpublishable" })).toBeNull();
    expect(requests(fetch, "GET", "/api/vaults/Work/publications")).toHaveLength(0);
    await userEvent.click(screen.getByRole("button", { name: "Close file tree" }));
    for (const item of publishingItems) expect((await noteMenu()).map((i) => i.label)).not.toContain(item);
  });

  it("hides Publish but keeps the marks when publishing is off", async () => {
    api({ publishEnabled: false, marks: ["/Work/Private"] });
    await openDrawer();
    await waitFor(() => expect(within(row("Private")).getByRole("img", { name: "Unpublishable" })).toBeInTheDocument());
    const inbox = await labels("inbox");
    expect(inbox).toContain("Mark unpublishable");
    expect(inbox).not.toContain("Publish…");
    expect(inbox).not.toContain("Publish… (unpublishable)");
    expect(await labels("Private")).toContain("Allow publishing");
    expect(await labels("Routing")).toContain("Mark unpublishable");

    await userEvent.click(screen.getByRole("button", { name: "Close file tree" }));
    const actions = (await noteMenu()).map((i) => i.label);
    expect(actions).toContain("Mark unpublishable");
    expect(actions).not.toContain("Publish…");
    cleanup();

    // With no note open the tree asks for the marks itself: the open note's menu filled the cache above.
    api({ publishEnabled: false, marks: ["/Work/Private"] });
    useEditor.getState().reset();
    useEditor.getState().setActiveVault("Work");
    const router = renderApp("/");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("Private");
    await waitFor(() => expect(within(row("Private")).getByRole("img", { name: "Unpublishable" })).toBeInTheDocument());
    expect(await labels("Private")).toContain("Allow publishing");
    expect(router.state.location.pathname).toBe("/");
  });

  it("offers Publish only where no mark covers the note", async () => {
    api({ marks: ["/Work/Private"] });
    await openDrawer();
    await waitFor(() => expect(within(row("Private")).getByRole("img", { name: "Unpublishable" })).toBeInTheDocument());
    await openFolder("Private", "secret");
    await openFolder("Routing", "Questions");
    expect(await labels("inbox")).toContain("Publish…");

    const secret = await menuOf("secret");
    expect(secret).toContainEqual({ label: "Publish… (unpublishable)", disabled: true });
    expect(secret.map((i) => i.label)).not.toContain("Publish…");
    expect(await labels("Questions")).toContain("Publish…");
    // The folder holds no note of its own to publish.
    expect(await labels("Routing")).not.toContain("Publish…");

    // The dock's menu of the note, covered by its folder's mark, says the same.
    await userEvent.click(row("secret"));
    expect(await screen.findByRole("heading", { name: "secret" })).toBeInTheDocument();
    const actions = await noteMenu();
    expect(actions).toContainEqual({ label: "Publish… (unpublishable)", disabled: true });
    expect(actions.map((i) => i.label)).not.toContain("Publish…");

    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Questions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Publish…" }));
    expect(await screen.findByRole("dialog", { name: "Publish this note" })).toBeInTheDocument();
  });

  it("marks a folder and lifts the mark", async () => {
    const { fetch, state } = api();
    await openDrawer();
    expect(within(row("Private")).queryByRole("img", { name: "Unpublishable" })).toBeNull();

    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Private" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Mark unpublishable" }));
    await waitFor(() => expect(within(row("Private")).getByRole("img", { name: "Unpublishable" })).toBeInTheDocument());
    expect(requests(fetch, "PUT", "/api/unpublishable").map((r) => new URL(r.url).pathname)).toEqual(["/api/unpublishable/Work/Private"]);
    expect(state.marks).toEqual(["/Work/Private"]);

    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Private" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Allow publishing" }));
    await waitFor(() => expect(within(row("Private")).queryByRole("img", { name: "Unpublishable" })).toBeNull());
    expect(requests(fetch, "DELETE", "/api/unpublishable").map((r) => new URL(r.url).pathname)).toEqual(["/api/unpublishable/Work/Private"]);
  });

  it("offers no Publish for a published note, nor while the marks are unknown", async () => {
    api({ published: ["/Work/inbox.md"] });
    await openDrawer();
    await openFolder("Routing", "Questions");
    await waitFor(async () => expect(await labels("Questions")).toContain("Publish…"));
    const inbox = await labels("inbox");
    expect(inbox).not.toContain("Publish…");
    expect(inbox).toContain("Mark unpublishable");
    cleanup();

    // Unknown marks could hold one that covers the note: the dock offers Publish only once they are in, as the tree does.
    const { fetch } = api({ marksFail: true });
    renderApp("/notes/Work/inbox.md");
    await screen.findByRole("heading", { name: "inbox" });
    await waitFor(() => expect(requests(fetch, "GET", "/api/publications/Work/inbox.md")).toHaveLength(1));
    await waitFor(() => expect(requests(fetch, "GET", "/api/vaults/Work/publications").length).toBeGreaterThan(0));
    await new Promise((r) => setTimeout(r, 50));
    const actions = (await noteMenu()).map((i) => i.label);
    expect(actions).not.toContain("Publish…");
    expect(actions).not.toContain("Publish… (unpublishable)");
  });

  it("asks before a mark withdraws a published link, and only then", async () => {
    const { fetch, state } = api({ published: ["/Work/Private/secret.md", "/Work/inbox.md"] });
    const question = "Marking it unpublishable withdraws its published link: whoever has it can't read the note any more.";
    await openDrawer();

    // The folder covers a published note: asked, and Keep is where the focus starts.
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Private" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Mark unpublishable" }));
    let ask = await screen.findByRole("alertdialog", { name: question });
    expect(within(ask).getByRole("button", { name: "Keep" })).toHaveFocus();
    await userEvent.click(within(ask).getByRole("button", { name: "Keep" }));
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(requests(fetch, "PUT", "/api/unpublishable")).toHaveLength(0);

    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Private" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Mark unpublishable" }));
    ask = await screen.findByRole("alertdialog", { name: question });
    await userEvent.click(within(ask).getByRole("button", { name: "Mark unpublishable" }));
    await waitFor(() => expect(state.marks).toEqual(["/Work/Private"]));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).toBeNull());

    // Nothing published below Routing: no question.
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for Routing" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Mark unpublishable" }));
    await waitFor(() => expect(state.marks).toEqual(["/Work/Private", "/Work/Routing"]));
    expect(screen.queryByRole("alertdialog")).toBeNull();

    // The dock's entry for the open, published note asks the same.
    await userEvent.click(screen.getByRole("button", { name: "Close file tree" }));
    await userEvent.click(await screen.findByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Mark unpublishable" }));
    ask = await screen.findByRole("alertdialog", { name: question });
    expect(within(ask).getByRole("button", { name: "Keep" })).toHaveFocus();
    await userEvent.click(within(ask).getByRole("button", { name: "Mark unpublishable" }));
    await waitFor(() => expect(state.marks).toEqual(["/Work/Private", "/Work/Routing", "/Work/inbox.md"]));
  });

  it("shows the mark at a folder's new place after a move in the docked tree", async () => {
    vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(min-width: 64rem)", media: query, addEventListener: () => {}, removeEventListener: () => {} }));
    window.innerWidth = 1280;
    api({ marks: ["/Work/Private"] });
    renderApp("/notes/Work/inbox.md");
    const sidebar = await screen.findByRole("complementary", { name: "File tree" });
    const inSidebar = (name: string) => within(sidebar).getByRole("button", { name: new RegExp(`^${name}`) });
    await waitFor(() => expect(within(inSidebar("Private")).getByRole("img", { name: "Unpublishable" })).toBeInTheDocument());

    await userEvent.click(within(sidebar).getByRole("button", { name: "Actions for Private" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename or move" }));
    const dialog = await screen.findByRole("dialog", { name: "Rename or move Private" });
    await userEvent.selectOptions(within(dialog).getByLabelText("Folder"), "/Work/Routing");
    await userEvent.click(within(dialog).getByRole("button", { name: "Move" }));
    await within(dialog).findByRole("status");

    // The folder it went into is open now, to show it.
    await waitFor(() => expect(within(sidebar).getByRole("button", { name: "Actions for Questions" })).toBeInTheDocument());
    await waitFor(() => expect(within(inSidebar("Private")).getByRole("img", { name: "Unpublishable" })).toBeInTheDocument());
  });
});
