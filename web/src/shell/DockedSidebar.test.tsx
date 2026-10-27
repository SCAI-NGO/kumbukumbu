// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const tree = {
  damaged: 0,
  root: { name: "Work", path: "/Work", folders: [], notes: [note("/Work/inbox.md"), note("/Work/readme.md")] },
};
const storageKey = "kumbu.sidebarCollapsed";

/** The viewport is as wide as the desktop breakpoint (Tailwind's `lg`) or narrower. */
let desktop = true;
const listeners = new Set<() => void>();
function setDesktop(value: boolean) {
  desktop = value;
  act(() => listeners.forEach((l) => l()));
}

function signedIn() {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/*": (r) =>
      json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
  });
}

const sidebar = () => screen.getByRole("complementary", { name: "File tree" });
const toggle = () => screen.getByRole("button", { name: "File tree" });

async function open(path = "/notes/Work/inbox.md") {
  signedIn();
  renderApp(path);
  await screen.findByRole("heading", { name: "inbox" });
}

beforeEach(() => {
  desktop = true;
  window.innerWidth = 1280;
  localStorage.clear();
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      return query === "(min-width: 64rem)" ? desktop : false;
    },
    media: query,
    addEventListener: (_: string, l: () => void) => listeners.add(l),
    removeEventListener: (_: string, l: () => void) => listeners.delete(l),
  }));
});

afterEach(() => {
  cleanup();
  listeners.clear();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("the sidebar on the desktop", () => {
  it("sits beside the note: no dialog, no scrim, the tree shown at once", async () => {
    await open();
    expect(await within(sidebar()).findByRole("button", { name: "readme" })).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Close file tree" })).not.toBeInTheDocument();
    expect(within(sidebar()).getByRole("separator", { name: "Resize the sidebar" })).toBeInTheDocument();
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(toggle()).toHaveAttribute("aria-controls", sidebar().id);
  });

  it("is a drawer below the breakpoint, as before", async () => {
    desktop = false;
    await open();
    expect(screen.queryByRole("complementary", { name: "File tree" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "File tree" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Open file tree" }));
    const drawer = screen.getByRole("dialog", { name: "File tree" });
    expect(drawer).toHaveAttribute("aria-modal", "true");
    expect(screen.getByRole("button", { name: "Close file tree" })).toBeInTheDocument();

    // A note opened from the drawer closes it.
    await userEvent.click(await within(drawer).findByRole("button", { name: "readme" }));
    expect(await screen.findByRole("heading", { name: "readme" })).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: "File tree" })).not.toBeInTheDocument();
  });

  it("follows the window across the breakpoint", async () => {
    await open();
    expect(sidebar()).toBeInTheDocument();
    setDesktop(false);
    expect(screen.queryByRole("complementary", { name: "File tree" })).not.toBeInTheDocument();
    setDesktop(true);
    expect(sidebar()).toBeInTheDocument();
  });

  it("keeps the tree open when a note is opened from it, and marks the open note", async () => {
    await open();
    await userEvent.click(await within(sidebar()).findByRole("button", { name: "readme" }));
    expect(await screen.findByRole("heading", { name: "readme" })).toBeInTheDocument();
    expect(sidebar()).toBeVisible();
    expect(within(sidebar()).getByRole("button", { name: "readme" })).toHaveAttribute("aria-current", "page");
  });

  it("collapses and expands with the top bar's button, and remembers it", async () => {
    await open();
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByRole("complementary", { name: "File tree" })).not.toBeInTheDocument();
    expect(toggle()).toHaveFocus();
    expect(localStorage.getItem(storageKey)).toBe("true");
    cleanup();

    await open();
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(sidebar()).toBeVisible();
    expect(localStorage.getItem(storageKey)).toBeNull();
  });

  it("collapses and expands with Ctrl+\\ and Cmd+\\, leaving the focus in place", async () => {
    await open();
    const write = screen.getByRole("button", { name: "Write" });
    act(() => write.focus());
    await userEvent.keyboard("{Control>}\\{/Control}");
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    expect(write).toHaveFocus();
    await userEvent.keyboard("{Meta>}\\{/Meta}");
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(write).toHaveFocus();
    // Ctrl+Alt+\ is not it.
    await userEvent.keyboard("{Control>}{Alt>}\\{/Alt}{/Control}");
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
  });

  it("hands the focus to the button when the shortcut hides the tree it was in", async () => {
    await open();
    const readme = await within(sidebar()).findByRole("button", { name: "readme" });
    act(() => readme.focus());
    await userEvent.keyboard("{Control>}\\{/Control}");
    expect(toggle()).toHaveFocus();
  });

  it("leaves the shortcut alone while a dialog is open", async () => {
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    await userEvent.keyboard("{Control>}\\{/Control}");
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
  });

  it("is shown again, with the focus on the open note, by the title and the dock's Files", async () => {
    localStorage.setItem(storageKey, "true");
    await open();
    expect(screen.queryByRole("complementary", { name: "File tree" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Show this note in the file tree" }));
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(await within(sidebar()).findByRole("button", { name: "inbox" })).toHaveFocus();
    expect(useEditor.getState().overlay).toBe("none");

    await userEvent.click(toggle());
    await userEvent.click(screen.getByRole("button", { name: "Files" }));
    expect(within(sidebar()).getByRole("button", { name: "inbox" })).toHaveFocus();
  });

  it("works without local storage", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "removeItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    await open();
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
  });
});
