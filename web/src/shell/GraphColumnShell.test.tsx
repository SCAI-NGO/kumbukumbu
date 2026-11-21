// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { GNode } from "../graph/model";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

// The canvas has its own tests; here it is a button per node.
vi.mock("../graph/GraphCanvas", () => ({
  default: (props: { nodes: GNode[] }) => (
    <div>
      {props.nodes.map((n) => (
        <button key={n.id} type="button">
          node {n.title}
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

function signedIn() {
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
  });
}

const column = () => screen.getByRole("complementary", { name: "Graph" });
const toggle = () => screen.getByRole("button", { name: "Graph" });

async function open(url = "/notes/Work/a.md") {
  const fetch = signedIn();
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

describe("the graph column in the shell", () => {
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

  it("isn't there below 1920 px, nor its toggle", async () => {
    width = 80;
    await open();
    expect(screen.queryByRole("complementary", { name: "Graph" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Graph" })).not.toBeInTheDocument();
    resize(120);
    expect(column()).toBeInTheDocument();
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

  it("does nothing below 1920 px", async () => {
    width = 80;
    await open();
    press({ ctrlKey: true, key: "G", code: "KeyG" });
    expect(localStorage.getItem(storageKey)).toBeNull();
  });
});
