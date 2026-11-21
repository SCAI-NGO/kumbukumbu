// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GNode } from "../graph/model";
import type { Suggestion } from "../graph/suggestions";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

// The canvas has its own tests; here it is a button per node, which selects it in the dialog and opens it in the
// column, and one per suggestion.
vi.mock("../graph/GraphCanvas", () => ({
  default: (props: {
    nodes: GNode[];
    select?: boolean;
    selectedKey?: string | null;
    suggestions: Suggestion[];
    showSuggestions: boolean;
    onOpen(n: GNode, beside: boolean): void;
    onSelect?(n: GNode | null): void;
    onSuggestion(s: Suggestion): void;
  }) => (
    <div>
      {props.nodes.map((n) => (
        <button key={n.id} type="button" aria-pressed={(props.select && n.key === props.selectedKey) || undefined} onClick={() => (props.select ? props.onSelect?.(n) : props.onOpen(n, false))}>
          node {n.title}
        </button>
      ))}
      {props.showSuggestions &&
        props.suggestions.map((s) => (
          <button key={s.path} type="button" onClick={() => props.onSuggestion(s)}>
            suggestion {s.title}
          </button>
        ))}
    </div>
  ),
}));

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });
const graphNode = (path: string, depth: number) => ({ path, title: path.slice(6, -3), kind: "note", tags: [], archived: false, in: 1, out: 1, depth });
const storageKey = "kumbu.graphColumnCollapsed";

/** The window's width in rem. */
let width = 120;
const listeners = new Set<() => void>();
const resize = (to: number) =>
  act(() => {
    width = to;
    for (const l of listeners) l();
  });

function signedIn(more: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 2, embeddings: "internal" }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: [note("/Work/a.md"), note("/Work/b.md")] } }),
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      return json(200, { path, content: `# Note ${path.slice(6, -3)}\n`, version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" });
    },
    "GET /api/version/*": () => json(200, { path: "/Work/a.md", version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/graph/local": (r) => {
      const path = new URL(r.url).searchParams.get("path")!;
      return json(200, { nodes: [graphNode(path, 0)], edges: [], leftOut: {} });
    },
    "GET /api/graph/paths": () => json(200, { nodes: [], edges: [], paths: [], more: false, why: "not_connected" }),
    "GET /api/graph/node": (r) => {
      const path = new URL(r.url).searchParams.get("path")!;
      return json(200, { node: graphNode(path, 1), changed: null, created: null, createdAtLeast: false, firstParagraph: "" });
    },
    ...more,
  });
}

const column = () => screen.getByRole("complementary", { name: "Graph" });
const toggle = () => screen.getByRole("button", { name: "Graph" });

async function open(url = "/notes/Work/a.md", more?: Parameters<typeof fakeApi>[0]) {
  const fetch = signedIn(more);
  renderApp(url);
  await screen.findAllByRole("heading", { name: /^Note / });
  return fetch;
}

beforeEach(() => {
  width = 120;
  listeners.clear();
  localStorage.clear();
  vi.stubGlobal("matchMedia", (query: string) => ({
    get matches() {
      const min = /^\(min-width: (\d+)rem\)$/.exec(query);
      return !!min && width >= Number(min[1]);
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

describe("the graph in the shell, as a column or a dialog", () => {
  it("sits beside the note from 1920 px on, with its toggle after the side panel's", async () => {
    await open();
    expect(await within(column()).findByRole("button", { name: "node a" })).toBeInTheDocument();
    const buttons = within(screen.getByRole("banner")).getAllByRole("button");
    expect(buttons.at(-1)).toBe(toggle());
    expect(buttons.at(-2)).toBe(screen.getByRole("button", { name: "Side panel" }));
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    expect(toggle()).toHaveAttribute("aria-controls", column().id);
    expect(toggle()).toHaveAttribute("aria-keyshortcuts", "Control+Shift+G Meta+Shift+G");
    expect(toggle()).toHaveAttribute("title", "Hide the graph (Ctrl+Shift+G or ⌘⇧G)");
  });

  it("below 1920 px the button opens the dialog", async () => {
    width = 90;
    await open();
    expect(screen.queryByRole("complementary", { name: "Graph" })).not.toBeInTheDocument();
    expect(toggle()).toHaveAttribute("aria-haspopup", "dialog");
    expect(toggle()).not.toHaveAttribute("aria-expanded");
    expect(toggle()).not.toHaveAttribute("aria-controls");
    expect(toggle()).toHaveAttribute("title", "Show the graph (Ctrl+Shift+G or ⌘⇧G)");
    await userEvent.click(toggle());
    const dialog = await screen.findByRole("dialog", { name: "a" });
    expect(dialog).toHaveTextContent("Graph");
    expect(await within(dialog).findByRole("button", { name: "node a" })).toBeInTheDocument();
  });

  it("in the split view between 1920 and 2880 px it is a dialog too", async () => {
    await open("/notes/Work/a.md?beside=Work/b.md");
    expect(screen.queryByRole("complementary", { name: "Graph" })).not.toBeInTheDocument();
    expect(toggle()).toHaveAttribute("aria-haspopup", "dialog");
    await userEvent.click(toggle());
    expect(await screen.findByRole("dialog", { name: "a" })).toBeInTheDocument();
  });

  it("on a phone the button shows in read mode only", async () => {
    width = 24;
    await open();
    expect(toggle()).toHaveAttribute("aria-haspopup", "dialog");
    // Beside Outline, as it is only in read mode too.
    const buttons = within(screen.getByRole("banner")).getAllByRole("button");
    expect(buttons[buttons.indexOf(screen.getByRole("button", { name: "Outline" })) + 1]).toBe(toggle());
    await userEvent.click(screen.getByRole("button", { name: "Write" }));
    expect(screen.queryByRole("button", { name: "Graph" })).not.toBeInTheDocument();
  });

  it("Escape closes the card first, then the dialog, and the focus returns to the button", async () => {
    width = 90;
    await open();
    await userEvent.click(toggle());
    const dialog = await screen.findByRole("dialog", { name: "a" });
    await userEvent.click(await within(dialog).findByRole("button", { name: "node a" }));
    expect(within(dialog).getByRole("button", { name: "Open" })).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    expect(within(dialog).queryByRole("button", { name: "Open" })).not.toBeInTheDocument();
    expect(dialog).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(toggle()).toHaveFocus();
  });

  it("Escape closes a suggestion's card in the dialog, and the dialog stays", async () => {
    localStorage.setItem("kumbu.graph.mockSimilarity", "1");
    localStorage.setItem("kumbu.graphPrefs", JSON.stringify({ suggestions: true }));
    width = 90;
    await open();
    await userEvent.click(toggle());
    const dialog = await screen.findByRole("dialog", { name: "a" });
    await userEvent.click(await within(dialog).findByRole("button", { name: "suggestion b" }));
    expect(screen.getByRole("dialog", { name: "b" })).toBeInTheDocument();
    await userEvent.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "b" })).not.toBeInTheDocument();
    expect(dialog).toBeInTheDocument();
  });

  it("widening the window into the column closes the dialog", async () => {
    width = 90;
    await open();
    await userEvent.click(toggle());
    expect(await screen.findByRole("dialog", { name: "a" })).toBeInTheDocument();
    resize(120);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(column()).toBeInTheDocument();
  });

  it("Open in the dialog opens the note and closes the dialog", async () => {
    width = 90;
    await open("/notes/Work/a.md", {
      "GET /api/graph/local": () => json(200, { nodes: [graphNode("/Work/a.md", 0), graphNode("/Work/b.md", 1)], edges: [[0, 1, 0]], leftOut: {} }),
    });
    await userEvent.click(toggle());
    const dialog = await screen.findByRole("dialog", { name: "a" });
    await userEvent.click(await within(dialog).findByRole("button", { name: "node b" }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Open" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Note b" })).toBeInTheDocument();
  });

  it("isn't there without a note", async () => {
    signedIn();
    renderApp("/");
    await screen.findByRole("heading", { name: "Work" });
    expect(screen.queryByRole("complementary", { name: "Graph" })).not.toBeInTheDocument();
  });

  it("needs 2880 px in the split view, and its foot names the other pane there", async () => {
    const fetch = await open("/notes/Work/a.md?beside=Work/b.md");
    expect(screen.queryByRole("complementary", { name: "Graph" })).not.toBeInTheDocument();
    resize(180);
    expect(await within(column()).findByText("Other pane: not connected")).toBeInTheDocument();
    const asked = fetch.mock.calls.map(([r]) => new URL(r.url)).find((u) => u.pathname === "/api/graph/paths")!;
    expect([asked.searchParams.get("from"), asked.searchParams.get("to")]).toEqual(["/Work/a.md", "/Work/b.md"]);
  });

  it("collapses with its toggle and stays collapsed when opened again", async () => {
    await open();
    await userEvent.click(toggle());
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    expect(toggle()).toHaveAttribute("title", "Show the graph (Ctrl+Shift+G or ⌘⇧G)");
    expect(screen.queryByRole("complementary", { name: "Graph" })).not.toBeInTheDocument();
    expect(localStorage.getItem(storageKey)).toBe("true");
    cleanup();

    await open();
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(toggle());
    expect(column()).toBeVisible();
  });
});

describe("Ctrl+Shift+G", () => {
  const press = (init: KeyboardEventInit) => act(() => void document.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, cancelable: true, shiftKey: true, ...init })));

  it("hides and shows the graph, Cmd too, and by the key's code", async () => {
    await open();
    press({ ctrlKey: true, key: "G", code: "KeyG" });
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    press({ metaKey: true, key: "g", code: "KeyG" });
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
    // A layout where G is elsewhere.
    press({ ctrlKey: true, key: "Ü", code: "KeyG" });
    expect(toggle()).toHaveAttribute("aria-expanded", "false");
    // The side panel's stays the side panel's.
    expect(screen.getByRole("button", { name: "Side panel" })).toHaveAttribute("aria-expanded", "true");
  });

  it("isn't Ctrl+G or Ctrl+Alt+Shift+G", async () => {
    await open();
    press({ ctrlKey: true, shiftKey: false, key: "g", code: "KeyG" });
    press({ ctrlKey: true, altKey: true, key: "G", code: "KeyG" });
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
  });

  it("hands the focus to the toggle when it hides the column holding it", async () => {
    await open();
    const depth = within(column()).getByRole("button", { name: "Depth 2" });
    act(() => depth.focus());
    press({ ctrlKey: true, key: "G", code: "KeyG" });
    expect(toggle()).toHaveFocus();
  });

  it("does nothing while a dialog is open", async () => {
    await open();
    await userEvent.click(screen.getByRole("button", { name: "Note actions" }));
    await userEvent.click(screen.getByRole("menuitem", { name: "Delete note" }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    press({ ctrlKey: true, key: "G", code: "KeyG" });
    expect(toggle()).toHaveAttribute("aria-expanded", "true");
  });

  it("Ctrl+Shift+G opens the dialog where no column fits", async () => {
    width = 90;
    await open();
    press({ ctrlKey: true, key: "G", code: "KeyG" });
    expect(await screen.findByRole("dialog", { name: "a" })).toBeInTheDocument();
    expect(localStorage.getItem(storageKey)).toBeNull();
    // The open graph dialog is a dialog too: the key doesn't open a second one.
    press({ ctrlKey: true, key: "G", code: "KeyG" });
    expect(screen.getAllByRole("dialog")).toHaveLength(1);
  });
});
