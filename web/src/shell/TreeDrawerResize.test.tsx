// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const longName = "A very long note name that does not fit";
const tree = {
  damaged: 0,
  root: { name: "Work", path: "/Work", folders: [], notes: [note("/Work/short.md"), note(`/Work/${longName}.md`)] },
};

const storageKey = "kumbu.sidebarWidth";
const handle = () => screen.getByRole("separator", { name: "Resize the sidebar" });
const drawer = () => screen.getByRole("dialog", { name: "File tree" });
const widthOf = () => drawer().style.getPropertyValue("--sidebar-width");

async function openDrawer() {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length)), content: "Text.", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
  });
  renderApp("/notes/Work/short.md");
  await userEvent.click(await screen.findByRole("button", { name: "Open file tree" }));
  await within(drawer()).findByText(longName);
}

beforeEach(() => {
  window.innerWidth = 1280;
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("the sidebar's width", () => {
  it("starts at the default and is resized by the keyboard within its limits", async () => {
    await openDrawer();
    expect(handle()).toHaveAttribute("aria-orientation", "vertical");
    expect(handle()).toHaveAttribute("aria-valuemin", "200");
    expect(handle()).toHaveAttribute("aria-valuemax", "560");
    expect(handle()).toHaveAttribute("aria-valuenow", "318");
    expect(widthOf()).toBe("318px");

    act(() => handle().focus());
    await userEvent.keyboard("{ArrowRight}");
    expect(handle()).toHaveAttribute("aria-valuenow", "334");
    expect(widthOf()).toBe("334px");
    await userEvent.keyboard("{ArrowLeft}{ArrowLeft}");
    expect(handle()).toHaveAttribute("aria-valuenow", "302");

    await userEvent.keyboard("{Home}");
    expect(handle()).toHaveAttribute("aria-valuenow", "200");
    await userEvent.keyboard("{ArrowLeft}");
    expect(handle()).toHaveAttribute("aria-valuenow", "200");
    await userEvent.keyboard("{End}{ArrowRight}");
    expect(handle()).toHaveAttribute("aria-valuenow", "560");
    expect(localStorage.getItem(storageKey)).toBe("560");
  });

  it("allows half the viewport at most", async () => {
    window.innerWidth = 800;
    await openDrawer();
    expect(handle()).toHaveAttribute("aria-valuemax", "400");
    act(() => handle().focus());
    await userEvent.keyboard("{End}");
    expect(handle()).toHaveAttribute("aria-valuenow", "400");
  });

  it("restores the stored width, clamped", async () => {
    localStorage.setItem(storageKey, "420");
    await openDrawer();
    expect(handle()).toHaveAttribute("aria-valuenow", "420");
    cleanup();

    localStorage.setItem(storageKey, "9000");
    await openDrawer();
    expect(handle()).toHaveAttribute("aria-valuenow", "560");
    cleanup();

    localStorage.setItem(storageKey, "nonsense");
    await openDrawer();
    expect(handle()).toHaveAttribute("aria-valuenow", "318");
  });

  it("works without local storage", async () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    await openDrawer();
    expect(handle()).toHaveAttribute("aria-valuenow", "318");
    act(() => handle().focus());
    await userEvent.keyboard("{ArrowRight}");
    expect(handle()).toHaveAttribute("aria-valuenow", "334");
  });

  it("goes back to the default on a double click", async () => {
    localStorage.setItem(storageKey, "450");
    await openDrawer();
    await userEvent.dblClick(handle());
    expect(handle()).toHaveAttribute("aria-valuenow", "318");
    expect(localStorage.getItem(storageKey)).toBeNull();
  });

  it("follows a drag and stores the width when it ends", async () => {
    await openDrawer();
    fireEvent.pointerDown(handle(), { button: 0, clientX: 318, pointerId: 1 });
    fireEvent.pointerMove(handle(), { clientX: 400, pointerId: 1 });
    expect(handle()).toHaveAttribute("aria-valuenow", "400");
    expect(localStorage.getItem(storageKey)).toBeNull();
    fireEvent.pointerMove(handle(), { clientX: 5000, pointerId: 1 });
    expect(handle()).toHaveAttribute("aria-valuenow", "560");
    fireEvent.pointerUp(handle(), { pointerId: 1 });
    expect(localStorage.getItem(storageKey)).toBe("560");
    // After the drag, moving the pointer changes nothing.
    fireEvent.pointerMove(handle(), { clientX: 100, pointerId: 1 });
    expect(handle()).toHaveAttribute("aria-valuenow", "560");
  });
});

describe("a cut-off name", () => {
  /** Every label is as wide as its text is long, in a box of 200 px: the long name is cut off, the short one fits. */
  function measure() {
    vi.spyOn(HTMLElement.prototype, "scrollWidth", "get").mockImplementation(function (this: HTMLElement) {
      return (this.textContent?.length ?? 0) * 8;
    });
    vi.spyOn(HTMLElement.prototype, "clientWidth", "get").mockReturnValue(200);
  }

  it("shows the full path as the title when hovered or focused, and only then", async () => {
    measure();
    await openDrawer();
    const long = within(drawer()).getByRole("button", { name: longName });
    const short = within(drawer()).getByRole("button", { name: "short" });
    expect(long).not.toHaveAttribute("title");

    await userEvent.hover(short);
    expect(short).not.toHaveAttribute("title");
    await userEvent.hover(long);
    expect(long).toHaveAttribute("title", `/Work/${longName}.md`);

    act(() => short.focus());
    expect(short).not.toHaveAttribute("title");
  });

  it("shows it on keyboard focus too", async () => {
    measure();
    await openDrawer();
    const long = within(drawer()).getByRole("button", { name: longName });
    act(() => long.focus());
    expect(long).toHaveAttribute("title", `/Work/${longName}.md`);
  });

  it("gives no title when the name fits", async () => {
    vi.spyOn(HTMLElement.prototype, "scrollWidth", "get").mockReturnValue(100);
    vi.spyOn(HTMLElement.prototype, "clientWidth", "get").mockReturnValue(200);
    await openDrawer();
    const long = within(drawer()).getByRole("button", { name: longName });
    await userEvent.hover(long);
    expect(long).not.toHaveAttribute("title");
  });
});
