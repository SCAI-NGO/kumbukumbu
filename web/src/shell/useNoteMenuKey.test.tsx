// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const rect = (r: Partial<DOMRect>) => ({ x: 0, y: 0, width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0, toJSON() {}, ...r }) as DOMRect;
const DOCK_BUTTON = { top: 740, bottom: 788, left: 880, right: 928 };
const CARET = { left: 300, top: 200, bottom: 226 };
const MENU = { width: 192, height: 300 };

beforeEach(() => {
  vi.stubGlobal("innerHeight", 800);
  vi.stubGlobal("innerWidth", 1000);
  // jsdom has no layout: the dock's ⋯ sits at the bottom right, the caret's mark where the test says.
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    if (this.getAttribute("aria-label") === "Note actions" && this.closest("nav")) return rect(DOCK_BUTTON);
    if (this.getAttribute("role") === "menu") return rect(MENU);
    if (this.dataset.caret !== undefined) return rect(CARET);
    return rect({});
  });
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

const view = (path: string, content: string) => ({ path, content, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-18T09:00:00Z" });

function server() {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 1, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [{ name: "inbox.md", path: "/Work/inbox.md", size: 10 }] } }),
    "GET /api/account/favourites": () => json(200, { favourites: [] }),
    "GET /api/notes/Work/inbox.md": () => json(200, view("/Work/inbox.md", "# inbox\n\nSome text here.")),
    "PUT /api/notes/Work/inbox.md": async (r) => json(200, view("/Work/inbox.md", ((await r.json()) as { content: string }).content)),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "GET /api/vaults/Work/publications": () => json(200, { publications: [], unpublishable: [] }),
  });
}

const pointMenu = () => screen.queryAllByRole("menu", { name: "Note actions" })[0] ?? null;
const shiftF10 = () => fireEvent.keyDown(document.activeElement ?? document.body, { key: "F10", code: "F10", shiftKey: true });

async function readNote() {
  server();
  renderApp("/notes/Work/inbox.md");
  await screen.findByRole("heading", { name: "inbox" });
  await screen.findByRole("button", { name: "Note actions" });
}

async function writeNote() {
  await readNote();
  const write = screen.getByRole("button", { name: "Write" });
  await waitFor(() => expect(write).toBeEnabled());
  await userEvent.click(write);
  return (await screen.findByRole("textbox", { name: "Markdown source" })) as HTMLTextAreaElement;
}

describe("the note's menu from the keyboard", () => {
  it("shift_f10_in_read_mode_opens_the_note_menu_at_the_dock_button", async () => {
    await readNote();
    expect(shiftF10()).toBe(false);
    const menu = await screen.findByRole("menu", { name: "Note actions" });
    // Where the dock's own menu opens: above the button, ending at its right edge.
    expect(menu.style.top).toBe(`${DOCK_BUTTON.top - 8 - MENU.height}px`);
    expect(menu.style.left).toBe(`${DOCK_BUTTON.right - MENU.width}px`);
    expect(within(menu).getAllByRole("menuitem")[0]).toHaveFocus();
    expect(within(menu).getByRole("menuitem", { name: "Find in note" })).toBeInTheDocument();
  });

  it("context_menu_key_too", async () => {
    await readNote();
    expect(fireEvent.keyDown(document.body, { key: "ContextMenu", code: "ContextMenu" })).toBe(false);
    await screen.findByRole("menu", { name: "Note actions" });
  });

  it("shift_f10_in_write_mode_opens_at_the_caret", async () => {
    const source = await writeNote();
    source.setSelectionRange(5, 5);
    expect(shiftF10()).toBe(false);
    const menu = await screen.findByRole("menu", { name: "Note actions" });
    // Below the caret's line, from where it stands.
    expect(menu.style.left).toBe(`${CARET.left}px`);
    expect(menu.style.top).toBe(`${CARET.bottom + 4}px`);
  });

  it("esc_returns_focus_to_the_editor_at_the_same_caret", async () => {
    const source = await writeNote();
    source.setSelectionRange(5, 5);
    shiftF10();
    const menu = await screen.findByRole("menu", { name: "Note actions" });
    expect(source).not.toHaveFocus();
    await userEvent.keyboard("{Escape}");
    expect(menu).not.toBeInTheDocument();
    expect(source).toHaveFocus();
    expect(source.selectionStart).toBe(5);
    // Still Write mode: Escape closed the menu only.
    expect(useEditor.getState().mode).toBe("write");
  });

  it("not_over_a_dialog_or_the_palette", async () => {
    await readNote();
    act(() => useEditor.getState().setOverlay("palette"));
    await screen.findByRole("dialog");
    shiftF10();
    expect(pointMenu()).toBeNull();
    act(() => useEditor.getState().setOverlay("none"));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    // Nor while another menu is open: the dock's own.
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    expect(screen.getAllByRole("menu")).toHaveLength(1);
    shiftF10();
    expect(screen.getAllByRole("menu")).toHaveLength(1);
    await userEvent.keyboard("{Escape}");

    // Nor over a dialog the menu opened.
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    await screen.findByRole("dialog");
    shiftF10();
    expect(pointMenu()).toBeNull();
  });

  it("nothing_without_an_open_note", async () => {
    server();
    renderApp({ pathname: "/", state: { overview: true } });
    await screen.findByRole("navigation", { name: "Vault actions" });
    expect(shiftF10()).toBe(true);
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("items_fit_the_mode", async () => {
    await writeNote();
    shiftF10();
    const menu = await screen.findByRole("menu", { name: "Note actions" });
    const labels = within(menu).getAllByRole("menuitem").map((item) => item.textContent);
    expect(labels).toContain("Duplicate");
    expect(labels).not.toContain("Find in note");
    expect(labels).not.toContain("History");
  });

  it("the_browser_menu_after_the_key_is_kept_away_but_not_a_later_right_click", async () => {
    await readNote();
    fireEvent.keyDown(document.body, { key: "ContextMenu", code: "ContextMenu" });
    await screen.findByRole("menu", { name: "Note actions" });
    vi.useFakeTimers();
    // Windows raises the browser's menu after the key's keyup.
    fireEvent.keyUp(document.activeElement!, { key: "ContextMenu", code: "ContextMenu" });
    expect(fireEvent.contextMenu(document.activeElement!)).toBe(false);
    act(() => vi.advanceTimersByTime(1000));
    expect(fireEvent.contextMenu(document.body, { button: 2 })).toBe(true);
  });

  it("the_key_held_down_keeps_the_browser_menu_away_while_ours_is_open", async () => {
    await readNote();
    shiftF10();
    await screen.findByRole("menu", { name: "Note actions" });
    expect(fireEvent.keyDown(document.activeElement!, { key: "F10", code: "F10", shiftKey: true, repeat: true })).toBe(false);
    expect(screen.getAllByRole("menu")).toHaveLength(1);
  });
});
