// SPDX-License-Identifier: AGPL-3.0-or-later
import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

// The sheet's chunk can't be fetched (a new release meanwhile, a lost connection).
vi.mock("../search/SearchSheet", () => {
  throw new Error("Failed to fetch dynamically imported module");
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("a search sheet that doesn't load", () => {
  it("says so, and the dock's stand-in field lets go of the focus (and the keyboard)", async () => {
    fakeApi({
      "GET /api/session": () => json(200, { user, previousSignInAt: null }),
      "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1 }]),
      "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [] } }),
      "GET /api/notes/*": () => json(200, { path: "/Work/inbox.md", content: "x", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
      "GET /api/links/*": () => json(200, []),
      "GET /api/backlinks/*": () => json(200, []),
      "GET /api/changes": () => json(200, []),
    });
    vi.spyOn(console, "error").mockImplementation(() => {});
    renderApp("/notes/Work/inbox.md");
    fireEvent.click(await screen.findByRole("button", { name: /^Search/ }));
    expect(await screen.findByRole("alertdialog", { name: "This didn't load" })).toBeInTheDocument();
    expect(document.activeElement?.hasAttribute("data-focus-return")).toBe(false);
  });
});
