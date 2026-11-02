// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { TreeFolder } from "../api/notes";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, noContent, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown" });
const folder = (path: string, folders: TreeFolder[] = [], notes: ReturnType<typeof note>[] = []): TreeFolder => ({
  name: path.split("/").at(-1)!,
  path,
  folders,
  notes,
});

/** `/Work/A` holds only the kept folder `/Work/A/B`; `/Work/Drafts` holds a note. */
const startTree = () => folder("/Work", [folder("/Work/A", [folder("/Work/A/B")]), folder("/Work/Drafts", [], [note("/Work/Drafts/plan.md")])], [note("/Work/inbox.md")]);

const marker = { path: "", mediaType: "application/octet-stream", size: 0, version: "e3b0c44298fc", lastWriter: "ada", lastWriteAt: "2026-10-30T09:00:00Z" };

let root: TreeFolder;

function api(extra: Parameters<typeof fakeApi>[0] = {}) {
  root = startTree();
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root }),
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/version/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/version".length)), version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    ...extra,
  });
}

/** A PUT of a marker that makes its folder in the tree served from then on (`parent.folders`). */
const makes = (parent: () => TreeFolder, path: string) => () => {
  parent().folders.push(folder(path));
  return json(201, { ...marker, path: `${path}/.vaultkeep` });
};

const drawer = () => screen.getByRole("dialog", { name: "File tree" });
const folderRow = (name: string) => within(drawer()).getByRole("button", { name: new RegExp(`^${name}\\d+\\s*items?$`) });
const requests = (fetch: ReturnType<typeof fakeApi>, method: string) => fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === method);

async function openDrawer() {
  renderApp("/notes/Work/inbox.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("Drafts");
}

async function menuOf(name: string) {
  await userEvent.click(within(drawer()).getByRole("button", { name: `Actions for ${name}` }));
  return screen.getAllByRole("menuitem").map((i) => i.textContent);
}

async function createFolder(name: string) {
  const dialog = await screen.findByRole("dialog", { name: "New folder" });
  await userEvent.type(within(dialog).getByLabelText("Name"), name);
  await userEvent.click(within(dialog).getByRole("button", { name: "Create folder" }));
  return dialog;
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

describe("new folders", () => {
  it("New folder in a folder's menu writes the marker and selects the folder", async () => {
    const fetch = api({ "PUT /api/files/Work/Drafts/Inbox/.vaultkeep": makes(() => root.folders[1], "/Work/Drafts/Inbox") });
    await openDrawer();
    const items = await menuOf("Drafts");
    expect(items.indexOf("New folder…")).toBe(items.indexOf("New note here") + 1);
    await userEvent.click(screen.getByRole("menuitem", { name: "New folder…" }));
    const dialog = await createFolder("  Inbox ");

    await waitFor(() => expect(dialog).not.toBeInTheDocument());
    const [put] = requests(fetch, "PUT");
    expect(new URL(put.url).pathname).toBe("/api/files/Work/Drafts/Inbox/.vaultkeep");
    expect((await put.arrayBuffer()).byteLength).toBe(0);
    // Drafts opens to show it, and the new folder is the one marked and focused.
    const inbox = await waitFor(() => folderRow("Inbox"));
    expect(inbox).toHaveAttribute("aria-current", "true");
    expect(inbox).toHaveFocus();
    expect(folderRow("Drafts")).not.toHaveAttribute("aria-current");
  });

  it("the mark ends with a note opened, and the focus goes to the folder once only", async () => {
    api({ "PUT /api/files/Work/Drafts/Inbox/.vaultkeep": makes(() => root.folders[1], "/Work/Drafts/Inbox") });
    await openDrawer();
    await menuOf("Drafts");
    await userEvent.click(screen.getByRole("menuitem", { name: "New folder…" }));
    await createFolder("Inbox");
    await waitFor(() => expect(folderRow("Inbox")).toHaveFocus());

    // Drafts closed and opened again: Inbox is shown anew, yet the focus stays where it is.
    await userEvent.click(folderRow("Drafts"));
    await userEvent.click(folderRow("Drafts"));
    expect(folderRow("Inbox")).toHaveAttribute("aria-current", "true");
    expect(folderRow("Drafts")).toHaveFocus();

    await userEvent.click(within(drawer()).getByRole("button", { name: "plan" }));
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("Drafts");
    expect(folderRow("Inbox")).not.toHaveAttribute("aria-current");
    expect(useEditor.getState().selectedFolder).toBeNull();
  });

  it("New folder in the vault's menu and in the dock", async () => {
    const fetch = api({
      "PUT /api/files/Work/Ideas/.vaultkeep": makes(() => root, "/Work/Ideas"),
      // Made meanwhile, elsewhere: there is the folder, as asked.
      "PUT /api/files/Work/Drafts/Later/.vaultkeep": () => {
        root.folders[1].folders.push(folder("/Work/Drafts/Later"));
        return json(409, { error: "Drafts/Later/.vaultkeep exists already.", current: { ...marker, path: "/Work/Drafts/Later/.vaultkeep" } });
      },
    });
    await openDrawer();
    await menuOf("Work");
    await userEvent.click(screen.getByRole("menuitem", { name: "New folder…" }));
    await createFolder("Ideas");
    await waitFor(() => expect(folderRow("Ideas")).toHaveAttribute("aria-current", "true"));

    await userEvent.click(screen.getByRole("button", { name: "Close file tree" }));
    await userEvent.click(screen.getByRole("button", { name: "New" }));
    expect(screen.getAllByRole("menuitem").map((i) => i.textContent)).toEqual(["New note", "New folder…", "Upload"]);
    // The dock's folder: the open note's, which is the vault's root here.
    await userEvent.click(screen.getByRole("menuitem", { name: "New folder…" }));
    await createFolder("Drafts/Later");
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "New folder" })).not.toBeInTheDocument());
    expect(requests(fetch, "PUT").map((r) => new URL(r.url).pathname)).toEqual(["/api/files/Work/Ideas/.vaultkeep", "/api/files/Work/Drafts/Later/.vaultkeep"]);
    expect(useEditor.getState().selectedFolder).toBe("/Work/Drafts/Later");
    expect(useEditor.getState().expandedFolders["/Work/Drafts"]).toBe(true);
  });

  it("a name that exists as a folder says so, selects it and writes nothing", async () => {
    const fetch = api();
    await openDrawer();
    await menuOf("Work");
    await userEvent.click(screen.getByRole("menuitem", { name: "New folder…" }));
    const dialog = await createFolder("Drafts");

    expect(await within(dialog).findByRole("alert")).toHaveTextContent('A folder named "Drafts" exists already.');
    expect(folderRow("Drafts")).toHaveAttribute("aria-current", "true");
    // The dialog keeps the focus while it is open.
    expect(dialog).toContainElement(document.activeElement as HTMLElement);
    expect(requests(fetch, "PUT")).toEqual([]);
  });

  it("refuses an empty, \".\" or \"..\" segment, which would write elsewhere, and finds a folder by a name ending in a slash", async () => {
    const fetch = api();
    await openDrawer();
    await menuOf("Drafts");
    await userEvent.click(screen.getByRole("menuitem", { name: "New folder…" }));
    const dialog = await createFolder(".");
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("A folder name can't be empty, “.” or “..”.");

    const field = within(dialog).getByLabelText("Name");
    await userEvent.clear(field);
    // A new name, and what was said against the last one goes.
    expect(within(dialog).queryByRole("alert")).not.toBeInTheDocument();
    await userEvent.type(field, "../Ideas");
    await userEvent.click(within(dialog).getByRole("button", { name: "Create folder" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("A folder name can't be empty, “.” or “..”.");
    await userEvent.clear(field);
    await userEvent.type(field, "x//y");
    await userEvent.click(within(dialog).getByRole("button", { name: "Create folder" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("A folder name can't be empty, “.” or “..”.");
    await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    await menuOf("Work");
    await userEvent.click(screen.getByRole("menuitem", { name: "New folder…" }));
    const again = await createFolder("Drafts/");
    expect(await within(again).findByRole("alert")).toHaveTextContent('A folder named "Drafts" exists already.');
    expect(folderRow("Drafts")).toHaveAttribute("aria-current", "true");
    expect(requests(fetch, "PUT")).toEqual([]);
  });

  it("a refused name shows the server's sentence", async () => {
    api({ "PUT /api/files/Work/Drafts/con/.vaultkeep": () => json(400, { error: "A name can't be con." }) });
    await openDrawer();
    await menuOf("Drafts");
    await userEvent.click(screen.getByRole("menuitem", { name: "New folder…" }));
    const dialog = await createFolder("con");

    expect(await within(dialog).findByRole("alert")).toHaveTextContent("A name can't be con.");
    expect(useEditor.getState().selectedFolder).toBeNull();
  });
});

describe("a menu inside the phone drawer", () => {
  it("is drawn inside the drawer but outside its scrolling list, Escape closes it before the drawer and Tab stays within the dialog", async () => {
    api();
    await openDrawer();
    const trigger = within(drawer()).getByRole("button", { name: "Actions for Work" });
    await userEvent.click(trigger);
    const menu = screen.getByRole("menu");
    expect(drawer().contains(menu)).toBe(true);
    expect(menu.closest("ul")).toBeNull();

    await userEvent.tab();
    expect(within(menu).getAllByRole("menuitem")[0]).toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("menu")).toBeNull();
    expect(trigger).toHaveFocus();
    expect(drawer()).toBeInTheDocument();

    await userEvent.click(trigger);
    await userEvent.tab();
    const items = within(screen.getByRole("menu")).getAllByRole("menuitem");
    for (let i = 1; i <= items.length; i++) await userEvent.tab();
    expect(screen.queryByRole("menu")).toBeNull();
    expect(drawer().contains(document.activeElement)).toBe(true);

    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "File tree" })).toBeNull();
  });
});

describe("removing a folder", () => {
  it("Remove folder is offered only on an empty folder", async () => {
    api();
    await openDrawer();
    // A holds only the kept folder B: not empty.
    expect(await menuOf("A")).not.toContain("Remove folder");
    await userEvent.keyboard("{Escape}");
    expect(await menuOf("Drafts")).not.toContain("Remove folder");
    await userEvent.keyboard("{Escape}");
    expect(await menuOf("Work")).not.toContain("Remove folder");
    await userEvent.keyboard("{Escape}");

    await userEvent.click(folderRow("A"));
    expect(await menuOf("B")).toContain("Remove folder");
  });

  it("Remove folder deletes the marker and the folder goes", async () => {
    const fetch = api({
      "DELETE /api/files/Work/A/B/.vaultkeep": () => {
        root.folders[0].folders = [];
        return noContent();
      },
    });
    await openDrawer();
    await userEvent.click(folderRow("A"));
    await menuOf("B");
    await userEvent.click(screen.getByRole("menuitem", { name: "Remove folder" }));

    await waitFor(() => expect(within(drawer()).queryByRole("button", { name: /^B\s*\d/ })).not.toBeInTheDocument());
    expect(requests(fetch, "DELETE").map((r) => new URL(r.url).pathname + new URL(r.url).search)).toEqual(["/api/files/Work/A/B/.vaultkeep"]);
  });

  it("Remove folder can't be chosen again while its request is on its way", async () => {
    let answer: (r: Response) => void = () => {};
    const fetch = api({ "DELETE /api/files/Work/A/B/.vaultkeep": () => new Promise<Response>((resolve) => (answer = resolve)) });
    await openDrawer();
    await userEvent.click(folderRow("A"));
    await menuOf("B");
    await userEvent.click(screen.getByRole("menuitem", { name: "Remove folder" }));
    await waitFor(() => expect(requests(fetch, "DELETE")).toHaveLength(1));

    await menuOf("B");
    expect(screen.getByRole("menuitem", { name: "Remove folder" })).toBeDisabled();
    root.folders[0].folders = [];
    answer(noContent());
    await waitFor(() => expect(within(drawer()).queryByRole("button", { name: /^B\s*\d/ })).not.toBeInTheDocument());
  });

  it("a folder removed meanwhile goes all the same", async () => {
    api({
      "DELETE /api/files/Work/A/B/.vaultkeep": () => {
        root.folders[0].folders = [];
        return json(404, { error: "No such file or directory" });
      },
    });
    await openDrawer();
    await userEvent.click(folderRow("A"));
    await menuOf("B");
    await userEvent.click(screen.getByRole("menuitem", { name: "Remove folder" }));

    await waitFor(() => expect(within(drawer()).queryByRole("button", { name: /^B\s*\d/ })).not.toBeInTheDocument());
    expect(within(drawer()).queryByRole("alert")).not.toBeInTheDocument();
  });
});

describe("folders of a vault shared with the user", () => {
  const shared = (level: string) =>
    api({ "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal", canWrite: level !== "read", level, owner: '"Alice"', shared: true }]) });

  it("a reader gets no folder actions, not even Remove folder on an empty one, and the dock makes nothing", async () => {
    shared("read");
    await openDrawer();
    await userEvent.click(folderRow("A"));
    await within(drawer()).findByText("B");
    for (const name of ["A", "B", "Drafts"]) expect(within(drawer()).queryByRole("button", { name: `Actions for ${name}` })).not.toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    await userEvent.click(screen.getByRole("button", { name: "New" }));
    expect(screen.getByRole("menuitem", { name: "New folder…" })).toBeDisabled();
  });

  it.each([
    ["write", ["New note here", "New folder…", "Upload here"]],
    ["maintain", ["New note here", "New folder…", "Upload here", "Rename or move", "Remove folder"]],
  ])("the board's folder moves or goes only from maintain (%s)", async (level, items) => {
    shared(level);
    root.folders.push(folder("/Work/Board"));
    await openDrawer();
    expect(await menuOf("Board")).toEqual(items);
    await userEvent.keyboard("{Escape}");
    // Any other folder is a writer's to move.
    expect(await menuOf("Drafts")).toContain("Rename or move");
  });
});

describe("a new note in the board's folder", () => {
  const column = (name: string, needsReason = false) => ({ name, archivable: false, needsReason, cards: [] as unknown[] });
  const boardView = (columns = [column("Ideas"), column("Doing")]) => ({ title: "Work", instructions: null, columns, problems: [] });
  const withCard = (title: string) => {
    const b = boardView();
    b.columns[0].cards = [{ path: `/Work/Board/${title}.md`, title, due: null, overdue: false, tags: [], reason: null }];
    return b;
  };
  /** The vault at `level` (the owner's own without one), with a board whose folder holds `notes`. */
  function boardApi(level: string | null, extra: Parameters<typeof fakeApi>[0] = {}, notes = [note("/Work/Board/Board.md"), note("/Work/Board/Plan.md")]) {
    const vault = level ? { name: "Work", notes: 2, embeddings: "internal", canWrite: level !== "read", level, owner: '"Alice"', shared: true } : { name: "Work", notes: 2, embeddings: "internal" };
    const fetch = api({ "GET /api/vaults": () => json(200, [vault]), "GET /api/vaults/Work/board": () => json(200, boardView()), ...extra });
    root.folders.push(folder("/Work/Board", [folder("/Work/Board/Archive")], notes));
    return fetch;
  }
  const posts = (fetch: ReturnType<typeof fakeApi>) =>
    Promise.all(requests(fetch, "POST").filter((r) => new URL(r.url).pathname === "/api/vaults/Work/board/cards").map((r) => r.clone().json()));

  async function newNoteIn(name: string) {
    await menuOf(name);
    await userEvent.click(screen.getByRole("menuitem", { name: "New note here" }));
  }

  it.each([["owner", null], ["write", "write"]])("becomes a card in the first column (%s)", async (_, level) => {
    const fetch = boardApi(level, { "POST /api/vaults/Work/board/cards": () => json(201, withCard("Untitled")) });
    const router = renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("Board");
    await newNoteIn("Board");

    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/Board/Untitled.md"));
    expect(await posts(fetch)).toEqual([{ title: "Untitled", column: "Ideas" }]);
    expect(requests(fetch, "PUT")).toEqual([]);
    expect(useEditor.getState().mode).toBe("write");
  });

  it("goes into the first column a card can start in", async () => {
    const fetch = boardApi(null, {
      "GET /api/vaults/Work/board": () => json(200, boardView([column("Blocked", true), column("Ideas")])),
      "POST /api/vaults/Work/board/cards": () => json(201, withCard("Untitled")),
    });
    await openDrawer();
    await newNoteIn("Board");
    await waitFor(async () => expect(await posts(fetch)).toEqual([{ title: "Untitled", column: "Ideas" }]));
  });

  it("a new note with a name that clashes says so, and changes nothing", async () => {
    const fetch = boardApi(null, {
      "POST /api/vaults/Work/board/cards": () => json(409, { error: "'Untitled' exists already (/Work/Board/Untitled.md); move or update it instead" }),
    });
    const router = renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("Board");
    await newNoteIn("Board");

    expect(await screen.findByRole("alert")).toHaveTextContent("'Untitled' exists already (/Work/Board/Untitled.md); move or update it instead");
    // The note there, maybe a card already, isn't written over by a plain note.
    expect(requests(fetch, "PUT")).toEqual([]);
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
  });

  it("a refusal for the level shows the server's sentence and writes no plain note instead", async () => {
    // The role was lowered to read since the vault list was read.
    const fetch = boardApi("write", {
      "POST /api/vaults/Work/board/cards": () => json(403, { error: "board add: you can only read vault Work", code: "read-only" }),
    });
    await openDrawer();
    await newNoteIn("Board");

    expect(await screen.findByRole("alert")).toHaveTextContent("you can only read vault Work");
    expect(requests(fetch, "PUT")).toEqual([]);
  });

  it("a reader is offered no new note in the board's folder", async () => {
    boardApi("read");
    await openDrawer();
    expect(within(drawer()).queryByRole("button", { name: "Actions for Board" })).not.toBeInTheDocument();
  });

  it.each([
    ["a sub-folder of the board's", "Archive", "/api/notes/Work/Board/Archive/Untitled.md", [note("/Work/Board/Board.md")]],
    ["a Board folder without a board", "Board", "/api/notes/Work/Board/Untitled.md", [note("/Work/Board/Plan.md")]],
  ])("in %s it stays a plain note", async (_, name, put, notes) => {
    const fetch = boardApi(null, { [`PUT ${put}`]: () => json(201, { path: decodeURIComponent(put.slice("/api/notes".length)), content: "", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T10:00:00Z" }) }, notes);
    await openDrawer();
    if (name === "Archive") await userEvent.click(folderRow("Board"));
    await newNoteIn(name);

    await waitFor(() => expect(requests(fetch, "PUT").map((r) => new URL(r.url).pathname)).toEqual([put]));
    expect(await posts(fetch)).toEqual([]);
  });
});

describe("deleting the vault from its menu", () => {
  it("the owner's vault menu offers Delete vault…, a member's doesn't", async () => {
    const bodies: unknown[] = [];
    let vaults = [{ name: "Work", notes: 2, embeddings: "internal" }, { name: "Gartenarbeit", notes: 0, embeddings: "internal" }];
    api({
      "GET /api/vaults": () => json(200, vaults),
      "GET /api/keys": () => json(200, []),
      "GET /api/vaults/Gartenarbeit/tree": () => json(200, { damaged: 0, root: folder("/Gartenarbeit") }),
      "POST /api/vaults/Work/delete": async (r) => {
        bodies.push(await r.json());
        vaults = vaults.filter((v) => v.name !== "Work");
        return noContent();
      },
    });
    const router = renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("Drafts");
    const items = await menuOf("Work");
    expect(items.at(-1)).toBe("Delete vault…");
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete vault…" }));

    // The dialog Settings has: the name typed and the password.
    const dialog = screen.getByRole("alertdialog", { name: "Delete Work?" });
    const confirm = within(dialog).getByRole("button", { name: "Delete vault" });
    await userEvent.type(within(dialog).getByLabelText("Type Work to confirm"), "Work");
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Your password"), "my password, long");
    await userEvent.click(confirm);

    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    expect(bodies).toEqual([{ password: "my password, long", confirm: "Work" }]);
    // Away from its note, which is gone with it.
    await waitFor(() => expect(router.state.location.pathname).not.toMatch(/^\/notes\/Work\//));
    expect(useEditor.getState().recentPaths.some((p) => p.startsWith("/Work/"))).toBe(false);
  });

  it.each(["write", "maintain"])("a member (%s) is offered no Delete vault…", async (level) => {
    api({ "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal", canWrite: true, level, owner: '"Alice"', shared: true, membership: "m1" }]) });
    await openDrawer();
    const items = await menuOf("Work");
    expect(items).toContain("Shared with you…");
    expect(items).not.toContain("Delete vault…");
  });
});

describe("renaming the vault from its menu", () => {
  it("the owner's vault menu offers Rename vault…, before Delete vault…", async () => {
    api();
    await openDrawer();
    const items = await menuOf("Work");
    expect(items).toContain("Rename vault…");
    expect(items.indexOf("Rename vault…")).toBeLessThan(items.indexOf("Delete vault…"));
    expect(items.at(-1)).toBe("Delete vault…");
  });

  it.each(["read", "write", "maintain"])("a member (%s) is offered no Rename vault…", async (level) => {
    api({ "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal", canWrite: level !== "read", level, owner: '"Alice"', shared: true, membership: "m1" }]) });
    await openDrawer();
    const items = await menuOf("Work");
    expect(items).toContain("Shared with you…");
    expect(items).not.toContain("Rename vault…");
  });

  it("Rename vault… renames the vault and goes on showing the note under the new name", async () => {
    let vaults = [{ name: "Work", notes: 2, embeddings: "internal" }];
    const fetch = api({
      "GET /api/vaults": () => json(200, vaults),
      "GET /api/keys": () => json(200, []),
      "GET /api/connections": () => json(200, []),
      "GET /api/vaults/Work/access": () => json(200, { apiKeys: [], deviceKeys: [], connections: [], members: [], left: [] }),
      "GET /api/vaults/Garten/tree": () => json(200, { damaged: 0, root: folder("/Garten", [], [note("/Garten/inbox.md")]) }),
      "PUT /api/vaults/Work/name": () => {
        vaults = [{ name: "Garten", notes: 2, embeddings: "internal" }];
        return noContent();
      },
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = renderApp("/notes/Work/inbox.md", queryClient);
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("Drafts");
    await menuOf("Work");
    await userEvent.click(screen.getByRole("menuitem", { name: "Rename vault…" }));

    const dialog = screen.getByRole("dialog", { name: "Rename Work" });
    const field = within(dialog).getByLabelText("New name");
    await userEvent.clear(field);
    await userEvent.type(field, "Garten");
    await waitFor(() => expect(within(dialog).getByRole("button", { name: "Rename vault" })).toBeEnabled());
    await userEvent.click(within(dialog).getByRole("button", { name: "Rename vault" }));

    // The dialog was held as chosen: the active vault changing under it doesn't drop its success handler.
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Garten/inbox.md"));
    await waitFor(() => expect(screen.queryByRole("dialog", { name: /^Rename / })).not.toBeInTheDocument());
    expect(requests(fetch, "PUT").map((r) => new URL(r.url).pathname)).toEqual(["/api/vaults/Work/name"]);
    expect(useEditor.getState().activeVault).toBe("Garten");
    // Nor did it turn into a dialog for the new name while it closed.
    expect(requests(fetch, "GET").map((r) => new URL(r.url).pathname)).not.toContain("/api/vaults/Garten/access");
    // Nothing asked again under the old name: not the note still shown as it went, nor the dialog's access list.
    const after = () => fetch.mock.calls.map(([r]) => new URL((r as Request).url).pathname).slice(fetch.mock.calls.findIndex(([r]) => (r as Request).method === "PUT") + 1);
    await waitFor(() => expect(after()).toContain("/api/notes/Garten/inbox.md"));
    expect(after().filter((p) => /^\/api\/[a-z]+\/Work\//.test(p))).toEqual([]);
    // And nothing of the old name stays in the caches, once nothing shows it.
    const old = (k: unknown) => typeof k === "string" && (k === "Work" || k.startsWith("/Work/"));
    await waitFor(() => expect(queryClient.getQueryCache().findAll({ predicate: (q) => q.queryKey.some(old) }).map((q) => q.queryKey)).toEqual([]));
  });
});

describe("leaving the vault from its menu", () => {
  const shared = (level: string) => ({ name: "Work", notes: 2, embeddings: "internal", canWrite: level !== "read", level, owner: '"Alice"', shared: true, membership: "m1" });

  it.each(["read", "write", "maintain"])("a member (%s) has Leave this vault… last in the vault menu", async (level) => {
    api({ "GET /api/vaults": () => json(200, [shared(level)]) });
    await openDrawer();
    const items = await menuOf("Work");
    expect(items.at(-1)).toBe("Leave this vault…");
    expect(items).toContain("Shared with you…");
  });

  it("Leave this vault… opens the dialog and leaves", async () => {
    let vaults = [shared("write")];
    const fetch = api({
      "GET /api/vaults": () => json(200, vaults),
      "POST /api/memberships/m1/leave": () => {
        vaults = [];
        return noContent();
      },
    });
    const router = renderApp("/notes/Work/inbox.md");
    await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
    await within(drawer()).findByText("Drafts");
    await menuOf("Work");
    await userEvent.click(screen.getByRole("menuitem", { name: "Leave this vault…" }));

    const dialog = screen.getByRole("alertdialog", { name: "Leave Work?" });
    expect(requests(fetch, "POST")).toEqual([]);
    await userEvent.click(within(dialog).getByRole("button", { name: "Leave vault" }));

    await waitFor(() => expect(requests(fetch, "POST").map((r) => new URL(r.url).pathname)).toEqual(["/api/memberships/m1/leave"]));
    await waitFor(() => expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument());
    // Away from its note, which she can no longer read.
    expect(router.state.location.pathname).toBe("/");
    expect(useEditor.getState().recentPaths.some((p) => p.startsWith("/Work/"))).toBe(false);
  });

  it("the owner's vault menu has no Leave this vault…", async () => {
    api();
    await openDrawer();
    expect(await menuOf("Work")).not.toContain("Leave this vault…");
  });
});
