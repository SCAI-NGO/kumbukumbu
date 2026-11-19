// SPDX-License-Identifier: AGPL-3.0-or-later
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, renderApp, user } from "../test/app";

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10 });

/** The window's width: wide (from 1280 px), desktop (from 1024 px) or a phone's. */
let width: "wide" | "desktop" | "phone" = "wide";
const listeners = new Set<() => void>();
const resize = (to: typeof width) =>
  act(() => {
    width = to;
    for (const l of listeners) l();
  });

type Put = { path: string; content: string };

/** Notes a to d in Work, each `# Note x` and a line linking to the next; `read` is a vault shared for reading. */
function signedIn({ level = "owner" as "owner" | "read", extra = {} as Parameters<typeof fakeApi>[0] } = {}) {
  const puts: Put[] = [];
  fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null }),
    "GET /api/vaults": () => json(200, [{ name: "Work", notes: 4, embeddings: "internal", ...(level === "read" ? { owner: "liz", level: "read" } : {}) }]),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () =>
      json(200, { damaged: 0, root: { name: "Work", path: "/Work", folders: [], notes: ["a", "b", "c", "d"].map((n) => note(`/Work/${n}.md`)) } }),
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      const name = path.slice(6, -3);
      return json(200, { path, content: `# Note ${name}\n\nSee [[next ${name}]].\n`, version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" });
    },
    "PUT /api/notes/*": async (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      const body = (await r.json()) as { content: string };
      puts.push({ path, content: body.content });
      return json(200, { path, version: "v2", lastWriteAt: "2026-10-25T09:01:00Z" });
    },
    "GET /api/version/*": (r) => json(200, { path: decodeURIComponent(new URL(r.url).pathname.slice("/api/version".length)), version: "v1", lastWriter: "ada", lastWriteAt: "2026-10-25T09:00:00Z" }),
    "GET /api/links/*": (r) => {
      const name = decodeURIComponent(new URL(r.url).pathname).slice(-4, -3);
      const next = { a: "b", b: "c", c: "d", d: "a" }[name]!;
      return json(200, [{ line: 3, raw: `[[next ${name}]]`, target: `next ${name}`, kind: "note", path: `/Work/${next}.md`, candidates: [] }]);
    },
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    ...extra,
  });
  return puts;
}

const pane = (side: "left" | "right") => screen.getByRole("region", { name: side === "left" ? /^Left pane/ : /^Right pane/ });
const panes = () => {
  const { left, right } = useEditor.getState().panes;
  return [left.path, right?.path ?? null];
};
const at = (router: ReturnType<typeof renderApp>) => decodeURIComponent(router.state.location.pathname + router.state.location.search);
const sidebar = () => screen.getByRole("complementary", { name: "File tree" });

async function split(url = "/notes/Work/a.md?beside=Work/b.md", extra: Parameters<typeof fakeApi>[0] = {}) {
  const puts = signedIn({ extra });
  const router = renderApp(url);
  if (url.includes("beside=")) {
    await within(await screen.findByRole("region", { name: /^Right pane/ })).findByRole("heading", { name: /^Note / });
    await within(pane("left")).findByRole("heading", { name: /^Note / });
  } else await screen.findByRole("heading", { name: /^Note / });
  return { router, puts, u: userEvent.setup() };
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

describe("two panes", () => {
  it("show side by side at 1280 px, each with its note bar, its note and its own mode", async () => {
    await split();
    expect(within(pane("left")).getByRole("heading", { name: "Note a" })).toBeInTheDocument();
    expect(within(pane("right")).getByRole("heading", { name: "Note b" })).toBeInTheDocument();
    for (const side of ["left", "right"] as const) expect(within(pane(side)).getByRole("group", { name: "This note" })).toBeInTheDocument();
    act(() => useEditor.getState().setPane("right", { path: "/Work/b.md", mode: "write" }));
    expect(await within(pane("right")).findByRole("textbox", { name: "Markdown source" })).toBeInTheDocument();
    expect(within(pane("left")).getByRole("heading", { name: "Note a" })).toBeInTheDocument();
  });

  it("show only the focused pane below 1280 px, and both again once wide", async () => {
    await split();
    await resize("desktop");
    expect(screen.queryByRole("region", { name: /^(Left|Right) pane/ })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Note a" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Note b" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Split view" })).not.toBeInTheDocument();

    act(() => useEditor.getState().focus("right"));
    expect(await screen.findByRole("heading", { name: "Note b" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Note a" })).not.toBeInTheDocument();

    await resize("wide");
    expect(within(pane("left")).getByRole("heading", { name: "Note a" })).toBeInTheDocument();
    expect(pane("right")).toHaveAttribute("aria-current", "true");
  });

  it("put the dock under the focused pane only, and the top bar's Read and Write act on it", async () => {
    const { u } = await split();
    expect(within(pane("left")).getByRole("navigation", { name: "Vault actions" })).toBeInTheDocument();
    expect(within(pane("right")).queryByRole("navigation", { name: "Vault actions" })).not.toBeInTheDocument();

    act(() => useEditor.getState().focus("right"));
    expect(within(pane("right")).getByRole("navigation", { name: "Vault actions" })).toBeInTheDocument();
    expect(screen.getByRole("banner")).toHaveTextContent("b");
    await u.click(screen.getByRole("button", { name: "Write" }));
    expect(await within(pane("right")).findByRole("textbox", { name: "Markdown source" })).toBeInTheDocument();
    expect(useEditor.getState().panes).toMatchObject({ left: { mode: "read" }, right: { mode: "write" } });
  });

  it("leave the other pane's Write mode alone when Read is pressed in a reading pane", async () => {
    const { u } = await split();
    act(() => useEditor.getState().setPane("left", { path: "/Work/a.md", mode: "write" }));
    await within(pane("left")).findByRole("textbox", { name: "Markdown source" });
    act(() => useEditor.getState().focus("right"));
    await u.click(screen.getByRole("button", { name: "Read" }));
    expect(useEditor.getState().panes.left.mode).toBe("write");
  });
});

describe("the focused pane", () => {
  it("is marked by its top line, aria-current and its label; the other's note bar and title are dimmed", async () => {
    await split();
    expect(pane("left")).toHaveAccessibleName("Left pane, focused");
    expect(pane("left")).toHaveAttribute("aria-current", "true");
    expect(pane("left")).toHaveClass("border-t-[3px]", "border-t-accent");
    expect(pane("right")).toHaveAccessibleName("Right pane");
    expect(pane("right")).not.toHaveAttribute("aria-current");
    expect(within(within(pane("right")).getByRole("group", { name: "This note" })).getByText("b")).toHaveClass("text-dim");
    expect(within(within(pane("left")).getByRole("group", { name: "This note" })).getByText("a")).toHaveClass("text-text");
  });

  it("moves with a click on a pane's note bar, without starting Write mode", async () => {
    const { u } = await split();
    await u.click(within(within(pane("right")).getByRole("group", { name: "This note" })).getByText("b"));
    expect(useEditor.getState()).toMatchObject({ focused: "right", currentPath: "/Work/b.md", mode: "read" });
    expect(pane("right")).toHaveAccessibleName("Right pane, focused");
  });

  it("moves with Ctrl+Alt+← and →, and Cmd+Alt on a Mac, but not over a dialog", async () => {
    const { u } = await split();
    fireEvent.keyDown(document, { key: "ArrowRight", ctrlKey: true, altKey: true });
    expect(useEditor.getState().focused).toBe("right");
    expect(pane("right")).toHaveFocus();
    fireEvent.keyDown(document, { key: "ArrowLeft", metaKey: true, altKey: true });
    expect(useEditor.getState().focused).toBe("left");

    await u.keyboard("{Control>}p{/Control}");
    await screen.findByRole("dialog", { name: "Favourites and commands" });
    fireEvent.keyDown(document.activeElement!, { key: "ArrowRight", ctrlKey: true, altKey: true });
    expect(useEditor.getState().focused).toBe("left");
  });

  it("is the tree's current note, and the other pane's note has a quieter mark", async () => {
    await split();
    const row = async (name: string) => within(sidebar()).findByRole("button", { name: new RegExp(`^${name}`) });
    expect(await row("a")).toHaveAttribute("aria-current", "page");
    expect(await row("b")).not.toHaveAttribute("aria-current");
    expect(await row("b")).toHaveAccessibleDescription("Open in the other pane");
    act(() => useEditor.getState().focus("right"));
    expect(await row("b")).toHaveAttribute("aria-current", "page");
    expect(await row("a")).toHaveAccessibleDescription("Open in the other pane");
  });
});

describe("where a note opens", () => {
  it("is the focused pane, for the tree: the other pane unchanged, a new history entry", async () => {
    const { router, u } = await split();
    act(() => useEditor.getState().focus("right"));
    await u.click(await within(sidebar()).findByRole("button", { name: /^c/ }));
    await within(pane("right")).findByRole("heading", { name: "Note c" });
    expect(at(router)).toBe("/notes/Work/a.md?beside=Work/c.md");
    expect(router.state.historyAction).toBe("PUSH");
    expect(within(pane("left")).getByRole("heading", { name: "Note a" })).toBeInTheDocument();
  });

  it("is the focused pane for the palette, and the right one with Ctrl+Enter, which focuses it", async () => {
    const { router, u } = await split("/notes/Work/a.md");
    await u.keyboard("{Control>}p{/Control}");
    await u.type(await screen.findByRole("combobox"), "Work c");
    await u.hover(await screen.findByRole("option", { name: "c, Work" }));
    await u.keyboard("{Enter}");
    await waitFor(() => expect(at(router)).toBe("/notes/Work/c.md"));

    await u.keyboard("{Control>}p{/Control}");
    await u.type(await screen.findByRole("combobox"), "Work d");
    await u.hover(await screen.findByRole("option", { name: "d, Work" }));
    await u.keyboard("{Control>}{Enter}{/Control}");
    await waitFor(() => expect(at(router)).toBe("/notes/Work/c.md?beside=Work/d.md"));
    expect(useEditor.getState()).toMatchObject({ focused: "right", currentPath: "/Work/d.md" });
    await within(pane("right")).findByRole("heading", { name: "Note d" });
  });

  it("is the link's own pane for a link, which takes the focus", async () => {
    const { router, u } = await split();
    await u.click(await within(pane("right")).findByRole("link", { name: "next b" }));
    await within(pane("right")).findByRole("heading", { name: "Note c" });
    expect(at(router)).toBe("/notes/Work/a.md?beside=Work/c.md");
    expect(useEditor.getState()).toMatchObject({ focused: "right", currentPath: "/Work/c.md" });

    await u.click(await within(pane("left")).findByRole("link", { name: "next a" }));
    await within(pane("left")).findByRole("heading", { name: "Note b" });
    expect(at(router)).toBe("/notes/Work/b.md?beside=Work/c.md");
  });

  it("is the right pane for Ctrl+Alt+click on a link, the split started and the right pane focused", async () => {
    const { router } = await split("/notes/Work/a.md");
    const link = await screen.findByRole("link", { name: "next a" });
    fireEvent.click(link, { ctrlKey: true, altKey: true });
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md?beside=Work/b.md"));
    expect(useEditor.getState()).toMatchObject({ focused: "right", currentPath: "/Work/b.md" });
    await within(pane("right")).findByRole("heading", { name: "Note b" });
  });

  it("is the right pane for Open beside in a tree row's menu", async () => {
    const { router, u } = await split("/notes/Work/a.md");
    await u.click(await within(sidebar()).findByRole("button", { name: "Actions for c" }));
    await u.click(await screen.findByRole("menuitem", { name: "Open beside" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md?beside=Work/c.md"));
    expect(useEditor.getState()).toMatchObject({ focused: "right", currentPath: "/Work/c.md" });
  });
});

describe("Split view", () => {
  it("starts with the left pane in Write mode and the same note on the right, the focus in the left editor", async () => {
    const { router, u } = await split("/notes/Work/a.md");
    await u.click(screen.getByRole("button", { name: "Split view" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md?beside=Work/a.md"));
    const source = await within(pane("left")).findByRole("textbox", { name: "Markdown source" });
    await waitFor(() => expect(source).toHaveFocus());
    expect(useEditor.getState()).toMatchObject({ focused: "left", panes: { left: { path: "/Work/a.md", mode: "write" }, right: { path: "/Work/a.md", mode: "read" } } });
    expect(await within(pane("right")).findByRole("heading", { name: "Note a" })).toBeInTheDocument();
    expect(within(pane("left")).getByRole("button", { name: "Split view" })).toHaveAttribute("aria-pressed", "true");
  });

  it("keeps a note that can't be written in Read mode on the left", async () => {
    signedIn({ level: "read" });
    const router = renderApp("/notes/Work/a.md");
    const u = userEvent.setup();
    await screen.findByRole("heading", { name: "Note a" });
    await u.click(await screen.findByRole("button", { name: "Split view" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md?beside=Work/a.md"));
    expect(useEditor.getState().panes).toMatchObject({ left: { mode: "read" }, right: { path: "/Work/a.md" } });
  });

  it("is in the palette's commands", async () => {
    const { router, u } = await split("/notes/Work/a.md");
    await u.keyboard("{Control>}p{/Control}");
    await u.type(await screen.findByRole("combobox"), "Split");
    await u.click(await screen.findByRole("option", { name: /^Split view/ }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md?beside=Work/a.md"));
  });

  it("ends with the button again or the right pane's ✕, saving a writing right pane first", async () => {
    const { router, u, puts } = await split();
    await u.click(within(pane("left")).getByRole("button", { name: "Split view" }));
    await waitFor(() => {
      expect(at(router)).toBe("/notes/Work/a.md");
      expect(screen.queryByRole("region", { name: /^(Left|Right) pane/ })).not.toBeInTheDocument();
    });

    await act(() => router.navigate("/notes/Work/a.md?beside=Work/b.md"));
    await u.click(within(within(pane("right")).getByRole("group", { name: "This note" })).getByText("b"));
    await u.click(screen.getByRole("button", { name: "Write" }));
    await u.type(await within(pane("right")).findByRole("textbox", { name: "Markdown source" }), " more");
    await u.click(within(pane("right")).getByRole("button", { name: "Close this pane" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md"));
    expect(puts.at(-1)).toEqual({ path: "/Work/b.md", content: "# Note b\n\nSee [[next b]].\n more" });
  });

  it("leaves the right pane as the only one when the left pane's ✕ closes it", async () => {
    const { router, u } = await split();
    await u.click(within(pane("left")).getByRole("button", { name: "Close this pane" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/b.md"));
    expect(panes()).toEqual(["/Work/b.md", null]);
    // The note is read afresh in its new place: on a busy machine that may take longer than a second.
    expect(await screen.findByRole("heading", { name: "Note b" }, { timeout: 5000 })).toBeInTheDocument();
  });

  it("then opens a note picked in the tree in the right pane, in Read mode, while the left keeps Write mode", async () => {
    const { router, u } = await split("/notes/Work/a.md");
    await u.click(screen.getByRole("button", { name: "Split view" }));
    const source = await within(pane("left")).findByRole("textbox", { name: "Markdown source" });
    await u.type(source, " typed");
    await u.click(within(within(pane("right")).getByRole("group", { name: "This note" })).getByText("a"));
    await u.click(await within(sidebar()).findByRole("button", { name: /^c/ }));
    await within(pane("right")).findByRole("heading", { name: "Note c" });
    expect(at(router)).toBe("/notes/Work/a.md?beside=Work/c.md");
    expect(within(pane("left")).getByRole("textbox", { name: "Markdown source" })).toHaveValue("# Note a\n\nSee [[next a]].\n typed");
    expect(useEditor.getState().panes).toMatchObject({ left: { mode: "write" }, right: { path: "/Work/c.md", mode: "read" } });
  });
});

describe("the writing pane's text", () => {
  it("is saved before another note opens in the right pane while it writes", async () => {
    const { router, u, puts } = await split();
    await u.click(within(within(pane("right")).getByRole("group", { name: "This note" })).getByText("b"));
    await u.click(screen.getByRole("button", { name: "Write" }));
    await u.type(await within(pane("right")).findByRole("textbox", { name: "Markdown source" }), " kept");
    await u.click(await within(sidebar()).findByRole("button", { name: /^d/ }));
    await within(pane("right")).findByRole("heading", { name: "Note d" });
    expect(at(router)).toBe("/notes/Work/a.md?beside=Work/d.md");
    expect(puts).toEqual([{ path: "/Work/b.md", content: "# Note b\n\nSee [[next b]].\n kept" }]);
  });

  it("is held in the writing pane, not the focused one, while a navigation waits for its save", async () => {
    const { router, u, puts } = await split();
    act(() => useEditor.getState().setPane("right", { path: "/Work/b.md", mode: "write" }));
    await u.type(await within(pane("right")).findByRole("textbox", { name: "Markdown source" }), " held");
    // The left pane focused, the right one's note changes by the URL (back, a link elsewhere).
    act(() => useEditor.getState().focus("left"));
    await act(() => router.navigate("/notes/Work/a.md?beside=Work/c.md"));
    await within(pane("right")).findByRole("heading", { name: "Note c" });
    expect(puts).toEqual([{ path: "/Work/b.md", content: "# Note b\n\nSee [[next b]].\n held" }]);
    expect(useEditor.getState()).toMatchObject({ focused: "left", currentPath: "/Work/a.md", mode: "read", panes: { right: { path: "/Work/c.md", mode: "read" } } });
  });
});

describe("the URL after a change to a pane's note", () => {
  it("keeps the split when the right pane's note is moved", async () => {
    const { router, u } = await split("/notes/Work/a.md?beside=Work/b.md", {
      "POST /api/move": () => json(200, { message: "moved /Work/b.md to /Work/e.md; no links needed rewriting", path: "/Work/e.md", version: "v2" }),
    });
    await u.click(within(pane("right")).getByRole("button", { name: "Rename or move" }));
    const dialog = await screen.findByRole("dialog", { name: "Rename or move b" });
    await u.clear(within(dialog).getByLabelText("Name"));
    await u.type(within(dialog).getByLabelText("Name"), "e");
    await u.click(within(dialog).getByRole("button", { name: "Move" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md?beside=Work/e.md"));
    expect(router.state.historyAction).toBe("REPLACE");
    expect(panes()).toEqual(["/Work/a.md", "/Work/e.md"]);
  });

  it("keeps the split when a note in either pane is named from its heading", async () => {
    const { router } = await split();
    act(() => useEditor.getState().follow?.("/Work/b.md", "/Work/Plan.md"));
    expect(at(router)).toBe("/notes/Work/a.md?beside=Work/Plan.md");
    act(() => useEditor.getState().follow?.("/Work/a.md", "/Work/Goals.md"));
    expect(at(router)).toBe("/notes/Work/Goals.md?beside=Work/Plan.md");
    act(() => useEditor.getState().follow?.("/Work/c.md", "/Work/Other.md"));
    expect(at(router)).toBe("/notes/Work/Goals.md?beside=Work/Plan.md");
  });

  it("leaves the other pane as the only one when a pane's note is deleted", async () => {
    const { router, u } = await split("/notes/Work/a.md?beside=Work/b.md", { "DELETE /api/notes/Work/b.md": () => new Response(null, { status: 204 }) });
    await u.click(within(pane("right")).getByRole("button", { name: "Delete note" }));
    await u.click(within(await screen.findByRole("dialog", { name: "Delete b?" })).getByRole("button", { name: "Delete" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md"));
    expect(panes()).toEqual(["/Work/a.md", null]);
  });
});

describe("the writing editor, unsaved text and all", () => {
  const source = () => screen.getByRole("textbox", { name: "Markdown source" }) as HTMLTextAreaElement;
  const typed = (name: string, text: string) => `# Note ${name}\n\nSee [[next ${name}]].\n${text}`;

  it("stays the same element when Split view starts while the left pane writes", async () => {
    const { router, u } = await split("/notes/Work/a.md");
    await u.click(screen.getByRole("button", { name: "Write" }));
    const before = await screen.findByRole("textbox", { name: "Markdown source" });
    await u.type(before, " y");
    await u.click(screen.getByRole("button", { name: "Split view" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md?beside=Work/a.md"));
    await within(pane("right")).findByRole("heading", { name: "Note a" });
    expect(within(pane("left")).getByRole("textbox", { name: "Markdown source" })).toBe(before);
    expect(before).toHaveValue(typed("a", " y"));
  });

  it("stays the same element when the right pane's ✕ or the button ends the split while the left pane writes", async () => {
    const { router, u } = await split();
    await u.click(within(within(pane("left")).getByRole("group", { name: "This note" })).getByText("a"));
    await u.click(screen.getByRole("button", { name: "Write" }));
    const before = await within(pane("left")).findByRole("textbox", { name: "Markdown source" });
    await u.type(before, " x");
    await u.click(within(pane("right")).getByRole("button", { name: "Close this pane" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md"));
    expect(source()).toBe(before);
    expect(before).toHaveValue(typed("a", " x"));

    await u.click(screen.getByRole("button", { name: "Split view" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md?beside=Work/a.md"));
    await u.type(before, "z");
    await u.click(within(pane("left")).getByRole("button", { name: "Split view" }));
    await waitFor(() => expect(at(router)).toBe("/notes/Work/a.md"));
    expect(source()).toBe(before);
    expect(before).toHaveValue(typed("a", " xz"));
  });

  it("stays the same element as the window narrows below 1280 px and widens again, in either pane", async () => {
    const { u } = await split();
    await u.click(within(within(pane("left")).getByRole("group", { name: "This note" })).getByText("a"));
    await u.click(screen.getByRole("button", { name: "Write" }));
    const left = await within(pane("left")).findByRole("textbox", { name: "Markdown source" });
    await u.type(left, " l");
    await resize("desktop");
    expect(source()).toBe(left);
    await resize("wide");
    expect(source()).toBe(left);
    expect(left).toHaveValue(typed("a", " l"));
  });

  it("stays the same element as the window narrows while the right pane writes, focused or not", async () => {
    const { u } = await split(undefined, { "PUT /api/notes/*": () => json(500, { title: "boom" }) });
    await u.click(within(within(pane("right")).getByRole("group", { name: "This note" })).getByText("b"));
    await u.click(screen.getByRole("button", { name: "Write" }));
    const right = await within(pane("right")).findByRole("textbox", { name: "Markdown source" });
    await u.type(right, " r");
    await resize("desktop");
    expect(source()).toBe(right);
    await resize("wide");
    expect(within(pane("right")).getByRole("textbox", { name: "Markdown source" })).toBe(right);

    // The left pane focused, the right one still writing: narrowed, it is out of sight but keeps its text.
    await u.click(within(within(pane("left")).getByRole("group", { name: "This note" })).getByText("a"));
    await resize("desktop");
    expect(screen.getByRole("heading", { name: "Note a" })).toBeInTheDocument();
    expect(right).toBeInTheDocument();
    await resize("wide");
    expect(within(pane("right")).getByRole("textbox", { name: "Markdown source" })).toBe(right);
    expect(right).toHaveValue(typed("b", " r"));
  });
});

describe("a note opened from a window into the writing pane", () => {
  it("waits for the writing pane's save, and stays on the window with the text kept when it fails", async () => {
    const { router, u } = await split(undefined, { "PUT /api/notes/*": () => json(500, { title: "boom" }), "GET /api/recent": () => json(200, { items: [], vaults: [], more: false }) });
    await u.click(within(within(pane("right")).getByRole("group", { name: "This note" })).getByText("b"));
    await u.click(screen.getByRole("button", { name: "Write" }));
    const right = await within(pane("right")).findByRole("textbox", { name: "Markdown source" });
    await u.type(right, " w");
    await act(() => router.navigate("/recent"));

    // A link in the window (Recent), then an opener that moves the store first (the palette, What's on).
    await act(() => router.navigate("/notes/Work/a.md?beside=Work/c.md"));
    await act(async () => {
      useEditor.getState().openIn("right", "/Work/d.md");
      await router.navigate("/notes/Work/a.md?beside=Work/d.md");
    });
    await new Promise((r) => setTimeout(r, 300));
    expect(at(router)).toBe("/recent");
    expect(screen.getByRole("textbox", { name: "Markdown source", hidden: true })).toBe(right);
    expect(right).toHaveValue("# Note b\n\nSee [[next b]].\n w");
    expect(useEditor.getState().panes).toMatchObject({ left: { path: "/Work/a.md", mode: "read" }, right: { path: "/Work/b.md", mode: "write" } });
  });
});
