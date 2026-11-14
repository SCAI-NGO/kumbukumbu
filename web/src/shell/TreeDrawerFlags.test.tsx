// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";
import { bodiesOf } from "../test/calendar";

const entry = (path: string, extra: Record<string, unknown> = {}) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown", version: `v-${path}`, ...extra });
const tree = {
  damaged: 0,
  root: {
    name: "Work",
    path: "/Work",
    folders: [],
    notes: [
      entry("/Work/inbox.md"),
      entry("/Work/plan.md", { flag: "red", writtenAt: "2026-11-14T09:00:00Z" }),
      entry("/Work/report.pdf", { kind: "pdf", mediaType: "application/pdf" }),
      entry("/Work/todo.md", { flag: "done" }),
    ],
  },
};

const drawer = () => screen.getByRole("dialog", { name: "File tree" });

async function openDrawer(level = "owner", extra: Parameters<typeof fakeApi>[0] = {}) {
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, embeddings: "internal", canWrite: level !== "read", level }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/version/*": () => json(404, { error: "No such file" }),
    "GET /api/vaults/Work/calendar/entry": () => json(200, { entry: null, couldntRead: null, canWrite: level !== "read" }),
    "GET /api/vaults/Work/flag": (r) =>
      json(200, { flag: new URL(r.url).searchParams.get("path") === "/Work/plan.md" ? "red" : null, inText: null, openTask: false, canWrite: level !== "read" }),
    ...extra,
  });
  renderApp("/notes/Work/inbox.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText("report.pdf");
  return fetch;
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

describe("flags in the tree", () => {
  it("the tree shows the flag after the name", async () => {
    await openDrawer();
    const plan = within(drawer()).getByRole("button", { name: "plan, Red flag" });
    const icon = within(plan).getByRole("img", { name: "Red flag" });
    expect(within(plan).getByText("plan").compareDocumentPosition(icon) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(within(drawer()).getByRole("button", { name: "todo, Done" })).getByRole("img", { name: "Done" })).toBeInTheDocument();
    expect(within(within(drawer()).getByRole("button", { name: "inbox" })).queryByRole("img")).not.toBeInTheDocument();
  });

  it("a note's ⋯ → Flag opens the flag menu, and a choice is written", async () => {
    const fetch = await openDrawer("owner", { "POST /api/vaults/Work/flag": () => json(200, { path: "/Work/plan.md", version: "v2", note: { flag: "yellow", inText: null, openTask: false, canWrite: true } }) });
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for plan" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Flag" }));
    const sheet = await screen.findByRole("dialog", { name: "Flag" });
    await waitFor(() => expect(within(sheet).getByRole("button", { name: "Red" })).toHaveAttribute("aria-pressed", "true"));
    await userEvent.click(within(sheet).getByRole("button", { name: "Yellow" }));
    expect(await screen.findByRole("status", { name: "Flag changed" })).toHaveTextContent("Flagged Yellow · Undo");
    expect(await bodiesOf(fetch, "POST", "/api/vaults/Work/flag")).toEqual([{ path: "/Work/plan.md", flag: "yellow" }]);
  });

  it("no Flag for an attachment, nor for a member who may only read", async () => {
    await openDrawer();
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for report.pdf" }));
    expect(screen.queryByRole("menuitem", { name: "Flag" })).not.toBeInTheDocument();
    cleanup();

    await openDrawer("read");
    expect(within(drawer()).getByRole("button", { name: "plan, Red flag" })).toBeInTheDocument();
    await userEvent.click(within(drawer()).getByRole("button", { name: "Actions for plan" }));
    expect(screen.queryByRole("menuitem", { name: "Flag" })).not.toBeInTheDocument();
  });
});
