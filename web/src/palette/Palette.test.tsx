// SPDX-License-Identifier: AGPL-3.0-or-later
import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Favourite, FavouriteInput } from "../api/favourites";
import { hideAgain } from "../discreet/hideAgain";
import { useDiscreet } from "../discreet/state";
import { startOver } from "../shell/lastNote";
import { useEditor } from "../state/editor";
import { useSession } from "../state/session";
import { fakeApi, json, navigateAway, noContent, renderApp, user } from "../test/app";

beforeEach(() => {
  window.innerWidth = 390;
  localStorage.clear();
  startOver();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  useSession.getState().reset();
  useEditor.getState().reset();
  useDiscreet.getState().reset();
});

const note = (path: string) => ({ name: path.split("/").at(-1)!, path, size: 10, kind: "note", mediaType: "text/markdown" });
const tree = (name: string, notes: string[]) => json(200, { damaged: 0, root: { name, path: `/${name}`, folders: [], notes: notes.map((n) => note(`/${name}/${n}`)) } });
const vault = (name: string, discreet = false) => ({ name, notes: 2, embeddings: "internal", discreet });

/** Work, and while `shown` the discreet Tagebuch; the favourites are kept as the server would, and written back as they are put. */
function signedInApi({
  shown = false,
  favourites = [] as FavouriteInput[],
  workTree = () => tree("Work", ["inbox.md", "plan.md"]) as Response | Promise<Response>,
  homeTree = null as (() => Response) | null,
} = {}) {
  const at = { shown };
  let held: Favourite[] = favourites.map((f) => ({ ...f, added: "2026-11-18T09:00:00Z" }));
  const puts: FavouriteInput[][] = [];
  const fetch = fakeApi({
    "GET /api/session": () => json(200, { user, previousSignInAt: null, discreet: { revealed: at.shown, until: null } }),
    "GET /api/vaults": () => json(200, [vault("Work"), ...(homeTree ? [vault("Home")] : []), ...(at.shown ? [vault("Tagebuch", true)] : [])]),
    ...(homeTree ? { "GET /api/vaults/Home/tree": homeTree } : {}),
    "GET /api/embeddings": () => json(200, []),
    "GET /api/vaults/Work/tree": () => workTree(),
    "GET /api/vaults/Tagebuch/tree": () => tree("Tagebuch", ["heute.md"]),
    "GET /api/vaults/Work/publications": () => json(200, { publications: [], unpublishable: [] }),
    "GET /api/notes/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/notes".length));
      return json(200, { path, content: `Text of ${path}.`, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-17T09:00:00Z" });
    },
    "GET /api/version/*": (r) => {
      const path = decodeURIComponent(new URL(r.url).pathname.slice("/api/version".length));
      return json(200, { path, version: "v1", lastWriter: "ada", lastWriteAt: "2026-11-17T09:00:00Z" });
    },
    "GET /api/links/*": () => json(200, []),
    "GET /api/backlinks/*": () => json(200, []),
    "GET /api/changes": () => json(200, []),
    "GET /api/member-notices": () => json(200, []),
    "GET /api/skills/marketplaces": () => json(200, { enabled: false, marketplaces: [] }),
    "POST /api/ws-ticket": () => json(503, { error: "later" }),
    "PUT /api/account/discreet/open-note": () => noContent(),
    "GET /api/account/favourites": () => json(200, { favourites: held }),
    "PUT /api/account/favourites": async (r) => {
      const body = (await r.json()) as { favourites: FavouriteInput[] };
      puts.push(body.favourites);
      held = body.favourites.map((f) => ({ ...f, added: "2026-11-18T09:00:00Z" }));
      return json(200, { favourites: held });
    },
  });
  return { fetch, puts, at };
}

const palette = () => screen.getByRole("dialog", { name: "Favourites and commands" });
const queryPalette = () => screen.queryByRole("dialog", { name: "Favourites and commands" });
const field = () => within(palette()).getByRole("combobox");
const options = () => within(palette()).queryAllByRole("option").map((o) => o.getAttribute("aria-label"));
const highlighted = () => within(palette()).getAllByRole("option").find((o) => o.getAttribute("aria-selected") === "true")?.getAttribute("aria-label");

async function shown(path = "/notes/Work/inbox.md", queryClient?: QueryClient) {
  const router = renderApp(path, queryClient);
  await screen.findByText(`Text of ${path.slice("/notes".length)}.`);
  return router;
}

const ctrlP = (target: Element = document.activeElement ?? document.body) => fireEvent.keyDown(target, { key: "p", code: "KeyP", ctrlKey: true });
const cmdP = (target: Element = document.activeElement ?? document.body) => fireEvent.keyDown(target, { key: "p", code: "KeyP", metaKey: true });

describe("the palette's ways in", () => {
  it("Ctrl+P opens it, also from the editor", async () => {
    signedInApi();
    await shown();
    const write = await screen.findByRole("button", { name: "Write" });
    await waitFor(() => expect(write).toBeEnabled());
    await userEvent.click(write);
    // On its first load the editor replaces a stand-in; it takes the focus once it is in.
    await waitFor(() => expect(screen.getByRole("textbox", { name: "Markdown source" })).toHaveFocus());
    const source = screen.getByRole("textbox", { name: "Markdown source" });
    const event = new KeyboardEvent("keydown", { key: "p", code: "KeyP", ctrlKey: true, bubbles: true, cancelable: true });
    act(() => void source.dispatchEvent(event));
    expect(event.defaultPrevented).toBe(true);
    expect(await screen.findByRole("dialog", { name: "Favourites and commands" })).toBeInTheDocument();
    expect(field()).toHaveFocus();
  });

  it("Cmd+P opens it on a Mac and Ctrl+P does not", async () => {
    vi.spyOn(navigator, "platform", "get").mockReturnValue("MacIntel");
    signedInApi();
    await shown();
    ctrlP();
    expect(queryPalette()).not.toBeInTheDocument();
    cmdP();
    expect(await screen.findByRole("dialog", { name: "Favourites and commands" })).toBeInTheDocument();
  });

  it("not over a dialog", async () => {
    signedInApi();
    await shown();
    await userEvent.click(screen.getByRole("button", { name: "Open file tree" }));
    await screen.findByRole("dialog", { name: "File tree" });
    ctrlP();
    await act(async () => {});
    expect(queryPalette()).not.toBeInTheDocument();
  });

  it("over a window, as everywhere in the shell", async () => {
    signedInApi();
    const router = await shown();
    await navigateAway(router, "/console");
    await screen.findByRole("dialog", { name: "Console" }, { timeout: 5_000 });
    ctrlP();
    expect(await screen.findByRole("dialog", { name: "Favourites and commands" })).toBeInTheDocument();
    expect(field()).toHaveFocus();
  });

  it("Ctrl+P opens it by the key's place too, on a layout without Latin letters", async () => {
    signedInApi();
    await shown();
    fireEvent.keyDown(document.body, { key: "з", code: "KeyP", ctrlKey: true });
    expect(await screen.findByRole("dialog", { name: "Favourites and commands" })).toBeInTheDocument();
  });

  it("Ctrl+L stays the browser's where P's key types L (Dvorak)", async () => {
    signedInApi();
    await shown();
    fireEvent.keyDown(document.body, { key: "l", code: "KeyP", ctrlKey: true });
    await act(async () => {});
    expect(queryPalette()).not.toBeInTheDocument();
  });

  it("Ctrl+P again closes it", async () => {
    signedInApi();
    await shown();
    ctrlP();
    await screen.findByRole("dialog", { name: "Favourites and commands" });
    ctrlP();
    await waitFor(() => expect(queryPalette()).not.toBeInTheDocument());
  });

  it("Esc closes and returns focus", async () => {
    signedInApi();
    await shown();
    const star = screen.getByRole("button", { name: "Favourites and commands (Ctrl+P)" });
    star.focus();
    ctrlP();
    await screen.findByRole("dialog", { name: "Favourites and commands" });
    await userEvent.keyboard("{Escape}");
    expect(queryPalette()).not.toBeInTheDocument();
    expect(star).toHaveFocus();
  });

  it("the star in the top bar opens it", async () => {
    signedInApi();
    await shown();
    await userEvent.click(screen.getByRole("button", { name: "Favourites and commands (Ctrl+P)" }));
    expect(await screen.findByRole("dialog", { name: "Favourites and commands" })).toBeInTheDocument();
  });
});

async function openPalette(path?: string, queryClient?: QueryClient) {
  const router = await shown(path, queryClient);
  await userEvent.click(screen.getByRole("button", { name: "Favourites and commands (Ctrl+P)" }));
  await screen.findByRole("dialog", { name: "Favourites and commands" });
  return router;
}

describe("the empty palette", () => {
  it("empty: favourites, then Recent (5)", async () => {
    signedInApi({ favourites: [{ kind: "command", id: "settings.vaults" }, { kind: "note", vault: "Work", path: "plan.md" }] });
    act(() => {
      for (const p of ["/Work/a.md", "/Work/b.md", "/Work/c.md", "/Work/d.md", "/Work/e.md"]) useEditor.getState().openNote(p);
    });
    await openPalette();
    await waitFor(() => expect(options()).toEqual(["Vaults, Settings", "plan, Work", "inbox, Work", "e, Work", "d, Work", "c, Work", "b, Work"]));
    expect(within(palette()).getByRole("group", { name: "Favourites" })).toBeInTheDocument();
    expect(within(palette()).getByRole("group", { name: "Recent" })).toBeInTheDocument();
    expect(within(palette()).queryByText("Star anything here to keep it on top.")).not.toBeInTheDocument();
  });

  it("with favourites, no hint while the trees are still on their way", async () => {
    let send = (_: Response) => {};
    let first = true;
    signedInApi({
      favourites: [{ kind: "note", vault: "Work", path: "plan.md" }],
      // The shell's first read answers; the palette's waits.
      workTree: () => (first ? ((first = false), tree("Work", ["inbox.md", "plan.md"])) : new Promise<Response>((resolve) => (send = resolve))),
    });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    await openPalette(undefined, queryClient);
    await waitFor(() => expect(queryClient.getQueryData(["favourites"])).toBeDefined());
    queryClient.removeQueries({ queryKey: ["tree", "Work"] });
    await userEvent.type(field(), "x");
    await userEvent.clear(field());
    await act(async () => {});
    expect(within(palette()).queryByText("Star anything here to keep it on top.")).not.toBeInTheDocument();
    send(await tree("Work", ["inbox.md", "plan.md"]));
  });

  it("Recent leaves out the notes already shown under Favourites", async () => {
    signedInApi({ favourites: [{ kind: "note", vault: "Work", path: "inbox.md" }] });
    await openPalette();
    await waitFor(() => expect(options()).toEqual(["inbox, Work"]));
    expect(within(palette()).queryByRole("group", { name: "Recent" })).not.toBeInTheDocument();
  });

  it('without favourites: "Star anything here to keep it on top."', async () => {
    signedInApi();
    await openPalette();
    expect(await within(palette()).findByText("Star anything here to keep it on top.")).toBeInTheDocument();
    expect(options()).toEqual(["inbox, Work"]);
  });
});

describe("rows", () => {
  it("Enter on a row navigates", async () => {
    signedInApi();
    const router = await openPalette();
    await userEvent.type(field(), "plan");
    await waitFor(() => expect(highlighted()).toBe("plan, Work"));
    await userEvent.keyboard("{Enter}");
    await waitFor(() => expect(router.state.location.pathname).toBe("/notes/Work/plan.md"));
    expect(queryPalette()).not.toBeInTheDocument();
    expect(await screen.findByText("Text of /Work/plan.md.")).toBeInTheDocument();
  });

  it("Enter on ✕ closes it, without opening a row", async () => {
    signedInApi();
    const router = await openPalette();
    await userEvent.type(field(), "plan");
    await waitFor(() => expect(highlighted()).toBe("plan, Work"));
    within(palette()).getByRole("button", { name: "Close" }).focus();
    await userEvent.keyboard("{Enter}");
    await waitFor(() => expect(queryPalette()).not.toBeInTheDocument());
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
  });

  it("opened again, it reads no tree again while they are fresh", async () => {
    const { fetch } = signedInApi();
    await openPalette();
    await waitFor(() => expect(options()).toContain("inbox, Work"));
    const reads = () => fetch.mock.calls.filter(([r]) => new URL((r as Request).url).pathname === "/api/vaults/Work/tree").length;
    const before = reads();
    await userEvent.keyboard("{Escape}");
    await userEvent.click(screen.getByRole("button", { name: "Favourites and commands (Ctrl+P)" }));
    await screen.findByRole("dialog", { name: "Favourites and commands" });
    await act(async () => {});
    expect(reads()).toBe(before);
  });

  it("arrow keys move the highlight", async () => {
    signedInApi();
    await openPalette();
    await userEvent.type(field(), "settings");
    await waitFor(() => expect(highlighted()).toBe("About, Settings"));
    const all = options();
    await userEvent.keyboard("{ArrowDown}");
    expect(highlighted()).toBe(all[1]);
    expect(field()).toHaveAttribute("aria-activedescendant", within(palette()).getAllByRole("option")[1].id);
    await userEvent.keyboard("{ArrowUp}{ArrowUp}");
    expect(highlighted()).toBe(all.at(-1));
  });

  it("Ctrl+D toggles the highlighted row's favourite", async () => {
    const { puts } = signedInApi();
    await openPalette();
    await within(palette()).findByText("Star anything here to keep it on top.");
    await userEvent.type(field(), "appearance");
    await waitFor(() => expect(highlighted()).toBe("Appearance, Settings"));
    const event = new KeyboardEvent("keydown", { key: "d", code: "KeyD", ctrlKey: true, bubbles: true, cancelable: true });
    act(() => void field().dispatchEvent(event));
    expect(event.defaultPrevented).toBe(true);
    await waitFor(() => expect(puts).toEqual([[{ kind: "command", id: "settings.appearance" }]]));
    expect(within(palette()).getByRole("button", { name: "Remove Appearance from favourites" })).toHaveAttribute("aria-pressed", "true");
  });

  it("the row's star toggles it", async () => {
    const { puts } = signedInApi({ favourites: [{ kind: "note", vault: "Work", path: "plan.md" }] });
    const router = await openPalette();
    const star = await within(palette()).findByRole("button", { name: "Remove plan from favourites" });
    await userEvent.click(star);
    await waitFor(() => expect(puts).toEqual([[]]));
    // The star only toggles: the row isn't opened.
    expect(router.state.location.pathname).toBe("/notes/Work/inbox.md");
    expect(palette()).toBeInTheDocument();
  });

  it("the last row is \"Search notes for ‘plan’\" and hands over the text", async () => {
    signedInApi();
    await openPalette();
    await userEvent.type(field(), "plan");
    await waitFor(() => expect(options().at(-1)).toBe("Search notes for ‘plan’"));
    await userEvent.click(within(palette()).getByRole("option", { name: "Search notes for ‘plan’" }));
    expect(await screen.findByRole("dialog", { name: "Search" })).toBeInTheDocument();
    expect(useEditor.getState().overlay).toBe("search");
    expect(useEditor.getState().searchQuery).toBe("plan");
  });

  it("a hidden discreet vault leaves no row", async () => {
    signedInApi({ shown: true });
    await openPalette();
    await userEvent.type(field(), "tagebuch");
    await waitFor(() => expect(options()).toEqual(expect.arrayContaining(["Tagebuch, Vault", "heute, Tagebuch"])));
    act(() => useDiscreet.setState({ revealed: false }));
    await waitFor(() => expect(options()).toEqual(["Search notes for ‘tagebuch’"]));
  });

  it("a note favourite whose note isn't in its vault's tree is not shown", async () => {
    signedInApi({ favourites: [{ kind: "note", vault: "Work", path: "gone.md" }, { kind: "note", vault: "Work", path: "plan.md" }] });
    await openPalette();
    await waitFor(() => expect(options()).toEqual(["plan, Work", "inbox, Work"]));
  });

  const toggleAppearance = async () => {
    await userEvent.type(field(), "appearance");
    await waitFor(() => expect(highlighted()).toBe("Appearance, Settings"));
    await userEvent.keyboard("{Control>}d{/Control}");
  };

  it("the next write drops it", async () => {
    const { puts } = signedInApi({ favourites: [{ kind: "note", vault: "Work", path: "gone.md" }, { kind: "note", vault: "Work", path: "plan.md" }] });
    await openPalette();
    await waitFor(() => expect(options()).toContain("plan, Work"));
    await toggleAppearance();
    await waitFor(() => expect(puts).toEqual([[{ kind: "note", vault: "Work", path: "plan.md" }, { kind: "command", id: "settings.appearance" }]]));
  });

  it("a favourite of a vault whose tree hasn't loaded is kept", async () => {
    const { puts } = signedInApi({
      favourites: [{ kind: "note", vault: "Home", path: "garden.md" }, { kind: "note", vault: "Work", path: "gone.md" }],
      homeTree: () => json(500, { error: "Broken." }),
    });
    await openPalette();
    await waitFor(() => expect(options()).toContain("inbox, Work"));
    await toggleAppearance();
    await waitFor(() => expect(puts).toEqual([[{ kind: "note", vault: "Home", path: "garden.md" }, { kind: "command", id: "settings.appearance" }]]));
  });

  it("a tree with notes that don't open drops nothing: the note may be one of them", async () => {
    const { puts } = signedInApi({
      favourites: [{ kind: "note", vault: "Work", path: "broken.md" }],
      workTree: () => json(200, { damaged: 1, root: { name: "Work", path: "/Work", folders: [], notes: [note("/Work/inbox.md"), note("/Work/plan.md")] } }),
    });
    await openPalette();
    await waitFor(() => expect(options()).toContain("inbox, Work"));
    await toggleAppearance();
    await waitFor(() => expect(puts).toEqual([[{ kind: "note", vault: "Work", path: "broken.md" }, { kind: "command", id: "settings.appearance" }]]));
  });

  it("closes when the discreet vaults are hidden again, and opens again with an empty field", async () => {
    const { at } = signedInApi({ shown: true });
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const router = await openPalette(undefined, queryClient);
    await userEvent.type(field(), "tagebuch");
    await waitFor(() => expect(options()).toContain("heute, Tagebuch"));
    at.shown = false;
    await act(() => hideAgain(queryClient, router.navigate, { tell: false, pathname: router.state.location.pathname }));
    expect(queryPalette()).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Favourites and commands (Ctrl+P)" }));
    await screen.findByRole("dialog", { name: "Favourites and commands" });
    expect(field()).toHaveValue("");
  });
});
