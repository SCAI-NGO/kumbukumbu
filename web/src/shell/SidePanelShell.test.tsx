// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const tree = {
  damaged: 0,
  root: { name: "Work", path: "/Work", folders: [], notes: [note("/Work/plan.md"), note("/Work/roadmap.md")] },
};
const content = ["# Plan", "", "Why we do it.", "", "## Goals", "", "- one", "", "## Risks", "", "None yet."].join("\n");
const storageKey = "kumbu.sidePanelCollapsed";

/** The window's width: wide (from 1280 px), desktop (from 1024 px) or a phone's. */
let width: "wide" | "desktop" | "phone" = "wide";
/** What follows the window's width, told when it changes. */
const listeners = new Set<() => void>();
/** The window resized to `to`. */
const resize = (to: typeof width) =>
  act(() => {
    width = to;
    for (const l of listeners) l();
  });

function signedIn() {
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, tree),
    "GET /api/notes/*": () => json(200, { path: "/Work/plan.md", content, version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "PUT /api/notes/*": () => json(200, { path: "/Work/plan.md", version: "v2", lastWriteAt: "2026-10-25T09:01:00Z" }),
    "GET /api/version/*": () => json(200, { path: "/Work/plan.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, [{ path: "/Work/roadmap.md", line: 3, text: "See [[plan]]", ambiguous: false }]),
    "GET /api/changes": () => json(200, []),
  });
}

const panel = () => screen.getByRole("complementary", { name: "Side panel" });
const toggle = () => screen.getByRole("button", { name: "Side panel" });

async function open() {
  signedIn();
  renderApp("/notes/Work/plan.md");
  await screen.findByRole("heading", { name: "Plan" });
}

beforeEach(() => {
  width = "wide";
  listeners.clear();
  localStorage.clear();
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      if (query === "(min-width: 80rem)") return width === "wide";
      if (query === "(min-width: 64rem)") return width !== "phone";
      return false;
    },
    media: query,
    addEventListener: (_: string, l: () => void) => listeners.add(l),
    removeEventListener: (_: string, l: () => void) => listeners.delete(l),
  }));
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  useSession.getState().reset();
  useEditor.getState().reset();
});

describe("the side panel in the shell", () => {
  it("sits beside the note on a wide window, with Linked from in it and not at the note's bottom", async () => {
    await open();
    const linked = await within(panel()).findByRole("region", { name: "Linked from" });
    expect(linked).toHaveTextContent("roadmap");
    expect(screen.getAllByRole("region", { name: "Linked from" })).toHaveLength(1);
    expect(within(screen.getByRole("main")).queryByRole("region", { name: "Unlinked mentions" })).not.toBeInTheDocument();
    expect(within(screen.getByRole("main")).queryByRole("complementary", { name: "Side panel" })).not.toBeInTheDocument();
  });

  it("has its toggle at the top bar's right end, and the top bar no Outline button", async () => {
    await open();
    const buttons = within(screen.getByRole("banner")).getAllByRole("button");
    expect(buttons.at(-1)).toBe(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(toggle()).toHaveAttribute("aria-controls", panel().id);
    expect(toggle()).toHaveAttribute("aria-keyshortcuts", "Control+Shift+\\ Meta+Shift+\\");
    expect(toggle()).toHaveAttribute("title", "Hide the side panel (Ctrl+Shift+\\ or ⌘⇧\\)");
    expect(screen.queryByRole("button", { name: "Outline" })).not.toBeInTheDocument();
  });

  it("collapses and expands with its toggle, remembers it, and keeps Linked from off the note's bottom while collapsed", async () => {
    await open();
    await within(panel()).findByRole("region", { name: "Linked from" });
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    expect(toggle()).toHaveAttribute("title", "Show the side panel (Ctrl+Shift+\\ or ⌘⇧\\)");
    expect(screen.queryByRole("complementary", { name: "Side panel" })).not.toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Linked from" })).not.toBeInTheDocument();
    expect(localStorage.getItem(storageKey)).toBe("true");
    cleanup();

    await open();
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(panel()).toBeVisible();
  });

  it("isn't there without a note", async () => {
    signedIn();
    renderApp("/");
    await screen.findByRole("heading", { name: "Work" });
    expect(screen.queryByRole("complementary", { name: "Side panel" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Side panel" })).not.toBeInTheDocument();
  });

  it.each(["desktop", "phone"] as const)("is not there on a %s, where Linked from stays at the note's bottom and the outline a sheet", async (w) => {
    width = w;
    await open();
    expect(await within(screen.getByRole("main")).findByRole("region", { name: "Linked from" })).toHaveTextContent("roadmap");
    expect(screen.queryByRole("complementary", { name: "Side panel" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Side panel" })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Outline" }));
    expect(screen.getByRole("dialog", { name: "Outline" })).toBeInTheDocument();
  });

  it("closes the outline sheet when the window widens to show the panel, so the outline isn't there twice", async () => {
    width = "desktop";
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Outline" }));
    expect(screen.getByRole("dialog", { name: "Outline" })).toBeInTheDocument();
    resize("wide");
    expect(panel()).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: "Outline" })).not.toBeInTheDocument();
    expect(useEditor.getState().noteTool).toBeNull();
    resize("desktop");
    expect(screen.queryByRole("dialog", { name: "Outline" })).not.toBeInTheDocument();
  });

  it("jumps in Read mode to the heading clicked in its outline", async () => {
    await open();
    await userEvent.click(within(panel()).getByRole("tab", { name: "Outline" }));
    const risks = screen.getByRole("heading", { name: "Risks" });
    const scrolled = vi.fn();
    risks.scrollIntoView = scrolled;
    await userEvent.click(within(panel()).getByRole("button", { name: "Risks" }));
    await waitFor(() => expect(scrolled).toHaveBeenCalledWith({ block: "start" }));
  });
});

/** The panel's outline entries, and the one marked as where the reader is. */
const entries = () => within(screen.getByRole("tabpanel", { name: "Outline" })).getAllByRole("button");
const marked = () => within(screen.getByRole("tabpanel", { name: "Outline" })).queryAllByRole("button").find((b) => b.getAttribute("aria-current") === "location")?.textContent ?? null;

async function writing() {
  await open();
  await userEvent.click(within(panel()).getByRole("tab", { name: "Outline" }));
  const write = screen.getByRole("button", { name: "Write" });
  await waitFor(() => expect(write).toBeEnabled());
  await userEvent.click(write);
  return (await screen.findByRole("textbox", { name: "Markdown source" })) as HTMLTextAreaElement;
}

/** Puts the caret at `offset`, as a click or an arrow key would. */
function caretTo(area: HTMLTextAreaElement, offset: number) {
  act(() => {
    area.setSelectionRange(offset, offset);
    fireEvent.select(area);
  });
}

describe("the side panel's outline", () => {
  it("follows the text being written, unsaved included", async () => {
    const area = await writing();
    await userEvent.type(area, "\n\n## New", { initialSelectionStart: content.length, initialSelectionEnd: content.length });
    await waitFor(() => expect(entries().map((b) => b.textContent)).toEqual(["Plan", "Goals", "Risks", "New"]));
  });

  it("tells what follows the writing only when the text or the caret's line changed", async () => {
    const area = await writing();
    caretTo(area, content.indexOf("- one"));
    const live = useEditor.getState().live;
    expect(live).toMatchObject({ path: "/Work/plan.md", text: content, caretLine: 7 });
    // Further along the same line, and a selection event without a move: nothing new to tell.
    caretTo(area, content.indexOf("- one") + 2);
    caretTo(area, content.indexOf("- one") + 2);
    expect(useEditor.getState().live).toBe(live);
    caretTo(area, content.indexOf("None yet."));
    expect(useEditor.getState().live).toMatchObject({ caretLine: 11 });
  });

  it("marks the heading at or above the caret's line while writing", async () => {
    const area = await writing();
    caretTo(area, content.indexOf("- one"));
    await waitFor(() => expect(marked()).toBe("Goals"));
    caretTo(area, content.indexOf("None yet."));
    await waitFor(() => expect(marked()).toBe("Risks"));
  });

  it("puts the caret at the start of the heading's line in Write mode, the focus in the textarea and the line at its top", async () => {
    const area = await writing();
    const rect = HTMLElement.prototype.getBoundingClientRect;
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      // The caret's mark in the hidden copy of the text stands 500 px below the textarea's top.
      if (this.dataset.caret !== undefined) return { top: 600, bottom: 620, left: 30 } as DOMRect;
      if (this === area) return { top: 100, bottom: 700, left: 0 } as DOMRect;
      return rect.call(this);
    });
    await userEvent.click(within(panel()).getByRole("button", { name: "Risks" }));
    const start = content.indexOf("## Risks");
    expect(area).toHaveFocus();
    expect([area.selectionStart, area.selectionEnd]).toEqual([start, start]);
    expect(area.scrollTop).toBe(500);
    await waitFor(() => expect(marked()).toBe("Risks"));
  });

  it("marks the last heading above the top of the visible text in Read mode, following the scroll", async () => {
    await open();
    await userEvent.click(within(panel()).getByRole("tab", { name: "Outline" }));
    const article = screen.getByRole("article");
    const scroller = article.parentElement!;
    const tops: Record<string, number> = { Plan: 40, Goals: 300, Risks: 600 };
    const rect = HTMLElement.prototype.getBoundingClientRect;
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      if (this === scroller) return { top: 64, bottom: 800 } as DOMRect;
      if (/^H[1-6]$/.test(this.tagName) && this.textContent! in tops) return { top: tops[this.textContent!], bottom: tops[this.textContent!] + 30 } as DOMRect;
      return rect.call(this);
    });
    fireEvent.scroll(scroller);
    await waitFor(() => expect(marked()).toBe("Plan"));
    Object.assign(tops, { Plan: -500, Goals: 64, Risks: 400 });
    fireEvent.scroll(scroller);
    await waitFor(() => expect(marked()).toBe("Goals"));
    Object.assign(tops, { Goals: -300, Risks: 100 });
    fireEvent.scroll(scroller);
    await waitFor(() => expect(marked()).toBe("Goals"));
  });

  it("follows the note's text growing or shrinking in Read mode (late images, a resize), and stops observing it once gone", async () => {
    const observers: { callback: () => void; targets: Element[]; disconnected: boolean }[] = [];
    vi.stubGlobal(
      "ResizeObserver",
      class {
        o: (typeof observers)[number];
        constructor(callback: () => void) {
          this.o = { callback, targets: [], disconnected: false };
          observers.push(this.o);
        }
        observe(target: Element) {
          this.o.targets.push(target);
        }
        unobserve() {}
        disconnect() {
          this.o.disconnected = true;
        }
      },
    );
    await open();
    await userEvent.click(within(panel()).getByRole("tab", { name: "Outline" }));
    const article = screen.getByRole("article");
    const scroller = article.parentElement!;
    const tops: Record<string, number> = { Plan: 40, Goals: 300, Risks: 600 };
    const rect = HTMLElement.prototype.getBoundingClientRect;
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      if (this === scroller) return { top: 64, bottom: 800 } as DOMRect;
      if (/^H[1-6]$/.test(this.tagName) && this.textContent! in tops) return { top: tops[this.textContent!], bottom: tops[this.textContent!] + 30 } as DOMRect;
      return rect.call(this);
    });
    const observer = observers.find((o) => o.targets.includes(article) && !o.disconnected)!;
    expect(observer).toBeDefined();
    act(() => observer.callback());
    await waitFor(() => expect(marked()).toBe("Plan"));
    // An image above Goals came in late and pushed the text up past the top.
    Object.assign(tops, { Plan: -500, Goals: 20, Risks: 400 });
    act(() => observer.callback());
    await waitFor(() => expect(marked()).toBe("Goals"));
    cleanup();
    expect(observer.disconnected).toBe(true);
  });
});

describe("Ctrl+Shift+\\", () => {
  const press = (init: KeyboardEventInit) => act(() => void document.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, cancelable: true, shiftKey: true, ...init })));

  it("hides and shows the side panel, Cmd too, the focus left in place", async () => {
    await open();
    const write = screen.getByRole("button", { name: "Write" });
    act(() => write.focus());
    // Shift+\ types `|` on many layouts.
    press({ ctrlKey: true, key: "|", code: "Backslash" });
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    expect(write).toHaveFocus();
    press({ metaKey: true, key: "\\", code: "Backslash" });
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    // The tree's shortcut stays the tree's.
    expect(screen.getByRole("button", { name: "File tree" })).toHaveAttribute("aria-expanded", "true");
  });

  it("goes by the key's code too, for layouts where \\ is elsewhere, and by the key where the code differs", async () => {
    await open();
    press({ ctrlKey: true, key: "'", code: "Backslash" });
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    press({ ctrlKey: true, key: "\\", code: "IntlBackslash" });
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
  });

  it("isn't Ctrl+\\ or Ctrl+Alt+Shift+\\", async () => {
    await open();
    press({ ctrlKey: true, shiftKey: false, key: "\\", code: "Backslash" });
    press({ ctrlKey: true, altKey: true, key: "|", code: "Backslash" });
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("button", { name: "File tree" })).toHaveAttribute("aria-expanded", "false");
  });

  it("hands the focus to the toggle when it hides the panel holding it", async () => {
    await open();
    const tab = within(panel()).getByRole("tab", { name: "Links" });
    act(() => tab.focus());
    press({ ctrlKey: true, key: "|", code: "Backslash" });
    expect(toggle()).toHaveFocus();
  });

  it("does nothing while a dialog is open", async () => {
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    press({ ctrlKey: true, key: "|", code: "Backslash" });
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
  });

  it("does nothing below 1280 px", async () => {
    width = "desktop";
    await open();
    press({ ctrlKey: true, key: "|", code: "Backslash" });
    expect(localStorage.getItem(storageKey)).toBeNull();
  });
});
