// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { TreeFolder } from "../api/notes";
import { fakeApi, json, noContent, user } from "../test/app";
import ArchiveSheet from "./ArchiveSheet";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown" });
const folder = (path: string, folders: TreeFolder[] = [], notes: ReturnType<typeof note>[] = []): TreeFolder => ({ name: path.split("/").at(-1)!, path, folders, notes });

const manual = {
  path: "/Work/_archive/Manual 2026-11-08 213005",
  name: "Manual 2026-11-08 213005",
  originalPath: "/Work/Manual",
  archivedAt: "2026-11-08T21:30:05Z",
  folder: true,
  files: 2,
  bytes: 3 * 1024 * 1024,
  kind: "folder",
};
const plan = {
  path: "/Work/_archive/plan 2026-11-07 101500.md",
  name: "plan 2026-11-07 101500.md",
  originalPath: "/Work/plan.md",
  archivedAt: "2026-11-07T10:15:00Z",
  folder: false,
  files: 1,
  bytes: 120,
  kind: "note",
};
const report = {
  path: "/Work/_archive/Projects/report 2026-11-01 090000.pdf",
  name: "report 2026-11-01 090000.pdf",
  originalPath: "/Work/Projects/report.pdf",
  archivedAt: "2026-11-01T09:00:00Z",
  folder: false,
  files: 1,
  bytes: 2048,
  kind: "pdf",
};

/** Work's tree without anything at the items' original paths; `taken` adds one there. */
const freeTree = () => folder("/Work", [folder("/Work/Projects")], [note("/Work/inbox.md")]);

function api({ canWrite = true, tree = freeTree(), move = () => json(200, { message: "moved", path: null, version: null }) }: { canWrite?: boolean; tree?: TreeFolder; move?: () => Response } = {}) {
  const moves: unknown[] = [];
  const trashed: unknown[] = [];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal", canWrite, level: canWrite ? "owner" : "read" }]),
    "GET /api/vaults/Work/archive": () => json(200, { items: [manual, plan, report], canWrite }),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: tree }),
    "GET /api/vaults/Work/versions": () =>
      json(200, {
        full: true,
        cursor: "c",
        state: "s",
        damaged: 0,
        deleted: [],
        notes: [
          { path: "/Work/inbox.md", version: "v1" },
          { path: `${manual.path}/Overview.md`, version: "v2" },
          { path: `${manual.path}/images/wiring diagram.png`, version: "v3", kind: "image" },
          { path: `${manual.path}/images/.vaultkeep`, version: "v4" },
        ],
      }),
    "POST /api/move": async (r) => {
      moves.push(await r.json());
      return move();
    },
    "POST /api/folders/trash": async (r) => {
      trashed.push(await r.json());
      return noContent();
    },
    "DELETE /api/notes/*": () => noContent(),
    "DELETE /api/files/*": () => noContent(),
  });
  return { fetch, moves, trashed };
}

function Location() {
  const location = useLocation();
  return <p data-testid="location">{location.pathname}</p>;
}

function renderSheet() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const invalidated = vi.spyOn(queryClient, "invalidateQueries");
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <Routes>
          <Route path="*" element={<Location />} />
        </Routes>
        <ArchiveSheet vault="Work" onClose={() => {}} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { queryClient, invalidated };
}

const requests = (fetch: ReturnType<typeof fakeApi>, method: string, path?: string) =>
  fetch.mock.calls.map(([r]) => r as Request).filter((r) => r.method === method && (!path || decodeURIComponent(new URL(r.url).pathname) === path));
const list = () => screen.findByRole("list", { name: "Archived items" });

describe("ArchiveSheet", () => {
  it("lists the items in the server's order, newest first, with where they were, when and how big", async () => {
    api();
    renderSheet();
    const items = within(await list()).getAllByRole("listitem");
    expect(items.map((i) => i.querySelector(".font-medium")?.textContent)).toEqual(["Manual/", "plan", "report.pdf"]);
    expect(items[0]).toHaveTextContent("2 files · 3.0 MB");
    expect(items[0]).toHaveTextContent(/Archived \d+ Nov 2026/);
    expect(items[2]).toHaveTextContent("Work / Projects");
    expect(items[2]).toHaveTextContent("2.0 KB");
  });

  it("restores an item to its original path and asks the tree, the archive and the storage again", async () => {
    const { fetch, moves } = api();
    const { invalidated } = renderSheet();
    await userEvent.click(within(await list()).getByRole("button", { name: "Restore report.pdf" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Restored report.pdf to Work / Projects.");
    expect(moves).toEqual([{ from: report.path, to: "/Work/Projects/report.pdf", keepLinks: false }]);
    // The tree was asked before the move, to check the name.
    expect(requests(fetch, "GET", "/api/vaults/Work/tree").length).toBeGreaterThan(0);
    const keys = invalidated.mock.calls.map(([f]) => JSON.stringify(f?.queryKey));
    expect(keys).toEqual(expect.arrayContaining([JSON.stringify(["tree", "Work"]), JSON.stringify(["archive", "Work"]), JSON.stringify(["account", "storage"])]));
  });

  it("restores a folder as a folder", async () => {
    const { moves } = api();
    renderSheet();
    await userEvent.click(within(await list()).getByRole("button", { name: "Restore Manual/" }));
    await screen.findByRole("status");
    expect(moves).toEqual([{ from: `${manual.path}/`, to: "/Work/Manual/", keepLinks: false }]);
  });

  it.each([
    ["a note", "plan", folder("/Work", [folder("/Work/Projects")], [note("/Work/Plan.md")]), "plan"],
    ["a file", "report.pdf", folder("/Work", [folder("/Work/Projects", [], [note("/Work/Projects/report.pdf")])]), "report"],
    ["a folder", "Manual/", folder("/Work", [folder("/Work/Manual"), folder("/Work/Projects")]), "Manual"],
  ])("asks for another name when %s is at the original path", async (_, name, tree, prefilled) => {
    const { moves } = api({ tree });
    renderSheet();
    await userEvent.click(within(await list()).getByRole("button", { name: `Restore ${name}` }));
    const dialog = await screen.findByRole("dialog", { name: "Restore as…" });
    expect(within(dialog).getByLabelText("Name")).toHaveValue(prefilled);
    expect(moves).toEqual([]);
  });

  it("restores under the name given in the dialog", async () => {
    const { moves } = api({ tree: folder("/Work", [], [note("/Work/plan.md")]) });
    renderSheet();
    await userEvent.click(within(await list()).getByRole("button", { name: "Restore plan" }));
    const dialog = await screen.findByRole("dialog", { name: "Restore as…" });
    const field = within(dialog).getByLabelText("Name");
    await userEvent.clear(field);
    await userEvent.type(field, "plan old");
    await userEvent.click(within(dialog).getByRole("button", { name: "Restore" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Restored plan old.md to Work.");
    expect(moves).toEqual([{ from: plan.path, to: "/Work/plan old.md", keepLinks: false }]);
    expect(screen.queryByRole("dialog", { name: "Restore as…" })).not.toBeInTheDocument();
  });

  it("opens the dialog when the server finds the name taken meanwhile", async () => {
    const { moves } = api({ move: () => json(409, { error: "mv: /Work/Projects/report.pdf exists already." }) });
    renderSheet();
    await userEvent.click(within(await list()).getByRole("button", { name: "Restore report.pdf" }));
    const dialog = await screen.findByRole("dialog", { name: "Restore as…" });
    expect(dialog).toHaveTextContent("mv: /Work/Projects/report.pdf exists already.");
    expect(moves).toHaveLength(1);
  });

  it("deletes a folder with the folder trash, a note and a file with their own", async () => {
    const { fetch, trashed } = api();
    const { invalidated } = renderSheet();
    await userEvent.click(within(await list()).getByRole("button", { name: "Delete Manual/" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Moved Manual/ to the trash.");
    expect(trashed).toEqual([{ path: `${manual.path}/` }]);
    await userEvent.click(within(await list()).getByRole("button", { name: "Delete plan" }));
    await waitFor(() => expect(requests(fetch, "DELETE", `/api/notes/Work/_archive/plan 2026-11-07 101500.md`)).toHaveLength(1));
    await userEvent.click(within(await list()).getByRole("button", { name: "Delete report.pdf" }));
    await waitFor(() => expect(requests(fetch, "DELETE", `/api/files/Work/_archive/Projects/report 2026-11-01 090000.pdf`)).toHaveLength(1));
    const keys = invalidated.mock.calls.map(([f]) => JSON.stringify(f?.queryKey));
    expect(keys).toEqual(expect.arrayContaining([JSON.stringify(["tree", "Work"]), JSON.stringify(["archive", "Work"]), JSON.stringify(["account", "storage"])]));
  });

  it("shows a reader the items without restoring or deleting", async () => {
    api({ canWrite: false });
    renderSheet();
    const items = await list();
    expect(within(items).getByRole("button", { name: "Open plan" })).toBeInTheDocument();
    expect(within(items).queryByRole("button", { name: /^Restore/ })).not.toBeInTheDocument();
    expect(within(items).queryByRole("button", { name: /^Delete/ })).not.toBeInTheDocument();
  });

  it("opens a note as the note and a folder in place, listing its files", async () => {
    api();
    renderSheet();
    const items = await list();
    const open = within(items).getByRole("button", { name: "Open Manual/" });
    expect(open).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(open);
    expect(open).toHaveAttribute("aria-expanded", "true");
    const files = await screen.findByRole("list", { name: "Files in Manual/" });
    expect(within(files).getAllByRole("listitem").map((i) => i.querySelector(".text-sm")?.textContent)).toEqual(["wiring diagram.png", "Overview"]);
    await userEvent.click(within(files).getByRole("button", { name: "Open wiring diagram.png" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/view/Work/_archive/Manual%202026-11-08%20213005/images/wiring%20diagram.png");

    await userEvent.click(within(items).getByRole("button", { name: "Open plan" }));
    expect(screen.getByTestId("location")).toHaveTextContent("/notes/Work/_archive/plan%202026-11-07%20101500.md");
  });
});
