// SPDX-License-Identifier: AGPL-3.0-or-later
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
  return within(drawer()).getAllByRole("menuitem").map((i) => i.textContent);
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
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "New folder…" }));
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
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "New folder…" }));
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
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "New folder…" }));
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
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "New folder…" }));
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
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "New folder…" }));
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
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "New folder…" }));
    const again = await createFolder("Drafts/");
    expect(await within(again).findByRole("alert")).toHaveTextContent('A folder named "Drafts" exists already.');
    expect(folderRow("Drafts")).toHaveAttribute("aria-current", "true");
    expect(requests(fetch, "PUT")).toEqual([]);
  });

  it("a refused name shows the server's sentence", async () => {
    api({ "PUT /api/files/Work/Drafts/con/.vaultkeep": () => json(400, { error: "A name can't be con." }) });
    await openDrawer();
    await menuOf("Drafts");
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "New folder…" }));
    const dialog = await createFolder("con");

    expect(await within(dialog).findByRole("alert")).toHaveTextContent("A name can't be con.");
    expect(useEditor.getState().selectedFolder).toBeNull();
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
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "Remove folder" }));

    await waitFor(() => expect(within(drawer()).queryByRole("button", { name: /^B\s*\d/ })).not.toBeInTheDocument());
    expect(requests(fetch, "DELETE").map((r) => new URL(r.url).pathname + new URL(r.url).search)).toEqual(["/api/files/Work/A/B/.vaultkeep"]);
  });

  it("Remove folder can't be chosen again while its request is on its way", async () => {
    let answer: (r: Response) => void = () => {};
    const fetch = api({ "DELETE /api/files/Work/A/B/.vaultkeep": () => new Promise<Response>((resolve) => (answer = resolve)) });
    await openDrawer();
    await userEvent.click(folderRow("A"));
    await menuOf("B");
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "Remove folder" }));
    await waitFor(() => expect(requests(fetch, "DELETE")).toHaveLength(1));

    await menuOf("B");
    expect(within(drawer()).getByRole("menuitem", { name: "Remove folder" })).toBeDisabled();
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
    await userEvent.click(within(drawer()).getByRole("menuitem", { name: "Remove folder" }));

    await waitFor(() => expect(within(drawer()).queryByRole("button", { name: /^B\s*\d/ })).not.toBeInTheDocument());
    expect(within(drawer()).queryByRole("alert")).not.toBeInTheDocument();
  });
});
