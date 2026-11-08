// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown" });
const root = { name: "Work", path: "/Work", folders: [], notes: [note("/Work/inbox.md")] };

function api(archive?: { files: number; bytes: number }) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root, ...(archive ? { archive } : {}) }),
    "GET /api/vaults/Work/archive": () => json(200, { items: [], canWrite: true }),
    "GET /api/notes/*": () => json(200, { path: "/Work/inbox.md", content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
  });
}

const drawer = () => screen.getByRole("dialog", { name: "File tree" });

async function openDrawer() {
  renderApp("/notes/Work/inbox.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("inbox");
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

describe("the archive's entry in the tree", () => {
  it("shows what the archive holds, dimmed below the tree, and opens the archive", async () => {
    api({ files: 23, bytes: 412 * 1024 * 1024 });
    await openDrawer();
    const entry = within(drawer()).getByRole("button", { name: "Archive · 23 files · 412.0 MB" });
    expect(entry).toHaveClass("text-muted");
    // Below the tree's own rows.
    const rows = within(drawer()).getAllByRole("button").filter((b) => b.hasAttribute("data-tree-row"));
    expect(rows.at(-1)).toBe(entry);
    await userEvent.click(entry);
    const sheet = await screen.findByRole("dialog", { name: "Archive" });
    expect(await within(sheet).findByText("The archive is empty.")).toBeInTheDocument();
  });

  it("says 1 file for one", async () => {
    api({ files: 1, bytes: 500 });
    await openDrawer();
    expect(within(drawer()).getByRole("button", { name: "Archive · 1 file · 500 B" })).toBeInTheDocument();
  });

  it("isn't there without an archive", async () => {
    api();
    await openDrawer();
    expect(within(drawer()).queryByRole("button", { name: /^Archive/ })).not.toBeInTheDocument();
  });
});
