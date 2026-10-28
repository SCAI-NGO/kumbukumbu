// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const entry = (path: string, kind = "note", mediaType = "text/markdown") => ({ name: path.split("/").at(-1)!, path, size: 10, kind, mediaType });
const tree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [],
    notes: [
      entry("/Work/a shot.png", "image", "image/png"),
      entry("/Work/plan.md"),
      entry("/Work/report.pdf", "pdf", "application/pdf"),
      entry("/Work/z photo.jpg", "image", "image/jpeg"),
    ],
  },
};

const drawer = () => screen.getByRole("dialog", { name: "File tree" });

async function openDrawer() {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    // The lightbox, once open: the file is gone meanwhile.
    "GET /api/files/*": () => json(404, { error: "No such file" }),
    "GET /api/version/*": () => json(404, { error: "No such file" }),
  });
  const router = renderApp("/notes/Work/plan.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("report.pdf");
  return router;
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

describe("attachments in the tree", () => {
  it("shows each entry with its kind's icon and an attachment's name with its extension", async () => {
    await openDrawer();
    const kinds = [...drawer().querySelectorAll("[data-file-kind]")].map((e) => `${e.getAttribute("data-file-kind")} ${e.textContent}`);
    expect(kinds).toEqual(["image a shot.png", "note plan", "pdf report.pdf", "image z photo.jpg"]);
    // Moving an attachment is the lightbox's: the note's dialog would make it a note.
    expect(within(drawer()).queryByRole("button", { name: "Actions for report.pdf" })).toBeNull();
    expect(within(drawer()).getByRole("button", { name: "Actions for plan" })).toBeInTheDocument();
  });

  it("opens an attachment in the lightbox with the folder's attachments in order", async () => {
    const router = await openDrawer();
    await userEvent.click(within(drawer()).getByRole("button", { name: "report.pdf" }));
    await waitFor(() => expect(router.state.location.pathname).toBe("/view/Work/report.pdf"));
    expect(router.state.location.state).toEqual({ paths: ["/Work/a shot.png", "/Work/report.pdf", "/Work/z photo.jpg"] });
  });
});
